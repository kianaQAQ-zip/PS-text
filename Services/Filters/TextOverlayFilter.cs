using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>文字在画面中的锚点位置。</summary>
    public enum TextAnchor
    {
        TopLeft = 0,
        TopCenter,
        TopRight,
        MiddleLeft,
        Center,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight
    }

    /// <summary>水印的排布方式。</summary>
    public enum TextLayout
    {
        /// <summary>按锚点放一处（九宫格位置之一）。</summary>
        Single = 0,

        /// <summary>按网格铺满整幅图（可倾斜），"防止截图外传"这类场景用。</summary>
        Tile = 1
    }

    /// <summary>文字水印参数。</summary>
    public sealed class TextOverlayOptions
    {
        public TextOverlayOptions()
        {
            FontFamilyName = "Microsoft YaHei UI";
            FontSize = 48.0;
            Color = Colors.White;
            Anchor = TextAnchor.BottomRight;
            Margin = 24.0;
            Bold = true;
            Italic = false;
            Shadow = true;
            Layout = TextLayout.Single;
            TileSpacingXPercent = 25.0;
            TileSpacingYPercent = 18.0;
            TileAngleDegrees = -30.0;
        }

        /// <summary>文字内容。</summary>
        public string Text { get; set; }

        /// <summary>字体名称。</summary>
        public string FontFamilyName { get; set; }

        /// <summary>字号（像素）。</summary>
        public double FontSize { get; set; }

        /// <summary>文字颜色。</summary>
        public Color Color { get; set; }

        /// <summary>锚点位置（仅 <see cref="TextLayout.Single"/> 有效）。</summary>
        public TextAnchor Anchor { get; set; }

        /// <summary>距边缘的边距（像素，仅 Single 有效）。</summary>
        public double Margin { get; set; }

        /// <summary>是否加粗。</summary>
        public bool Bold { get; set; }

        /// <summary>是否斜体。</summary>
        public bool Italic { get; set; }

        /// <summary>是否绘制投影（提升可读性）。</summary>
        public bool Shadow { get; set; }

        /// <summary>排布方式。</summary>
        public TextLayout Layout { get; set; }

        /// <summary>平铺时的水平间距，占图像宽度的百分比。</summary>
        public double TileSpacingXPercent { get; set; }

        /// <summary>平铺时的垂直间距，占图像高度的百分比。</summary>
        public double TileSpacingYPercent { get; set; }

        /// <summary>平铺时的倾斜角度（度，负值为常见的左下→右上方向）。</summary>
        public double TileAngleDegrees { get; set; }
    }

    /// <summary>
    /// 文字叠加滤镜（需求 P1-8）。
    ///
    /// 与其它滤镜的区别：文字排版依赖 WPF 的字体渲染（FormattedText / DrawingVisual），
    /// 因此这里用 RenderTargetBitmap 合成，而不是逐像素写数组。
    /// 为了与既有流程一致，输入输出仍然使用 PixelBuffer：
    /// 先把源像素包成 BitmapSource 作为背景绘制，再在 DPI 感知的画布上绘制文字，
    /// 最后把渲染结果读回像素缓冲，交给统一的提交 / 撤销流程处理。
    ///
    /// 线程要求（原先写成"必须在 UI 线程"，实测是**多余的约束**，已修正）：
    ///   WPF 对 DrawingVisual / FormattedText / RenderTargetBitmap 的真实要求是
    ///   "创建与使用必须在同一个线程"，并没有规定必须是 UI 线程。
    ///   证据有两条：自检一直在普通线程上直接调用 <see cref="Apply"/> 且从未失败；
    ///   批量流水线整条跑在线程池线程上也能正常出图（见自检 [32]）。
    ///   这条修正不是纸面清理 —— 若不解除，批量每张图都要切回 UI 线程排队，界面会一顿一顿。
    /// </summary>
    public sealed class TextOverlayFilter
    {
        /// <summary>渲染文字并返回新的像素缓冲（必须在 UI 线程调用）。</summary>
        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            TextOverlayOptions options,
            double dpiX,
            double dpiY)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            int width = source.Width;
            int height = source.Height;

            if (string.IsNullOrEmpty(options.Text))
            {
                return Copy(source);
            }

            if (dpiX <= 0.5 || double.IsNaN(dpiX))
            {
                dpiX = 96.0;
            }

            if (dpiY <= 0.5 || double.IsNaN(dpiY))
            {
                dpiY = 96.0;
            }

            // 背景：直接用源像素构造位图（已 Freeze，可安全用于绘制）
            BitmapSource background = PixelBuffer.ToBitmap(
                new PixelBuffer(source.GetPixels(), width, height),
                dpiX,
                dpiY);

            DrawingVisual visual = new DrawingVisual();

            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawImage(background, new Rect(0, 0, width, height));

                double fontSize = options.FontSize <= 0 ? 48.0 : options.FontSize;
                double margin = options.Margin < 0 ? 0 : options.Margin;

                // PixelsPerDip 决定文字在非 96 DPI 下的清晰度
                double pixelsPerDip = dpiY / 96.0;

                FormattedText formatted = new FormattedText(
                    options.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    BuildTypeface(options),
                    fontSize,
                    new SolidColorBrush(options.Color),
                    pixelsPerDip);

                Brush shadowBrush = null;

                if (options.Shadow)
                {
                    // 投影：偏移 2 像素的半透明黑色，保证浅色背景上也能看清。
                    // 只建一次 —— 平铺时可能有上千个瓦片，每片 new 一个画刷是纯浪费。
                    SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
                    brush.Freeze();
                    shadowBrush = brush;
                }

                if (options.Layout == TextLayout.Tile)
                {
                    DrawTiled(context, formatted, shadowBrush, width, height, options);
                }
                else
                {
                    Point origin = CalculateOrigin(formatted, options.Anchor, width, height, margin);
                    DrawOne(context, formatted, shadowBrush, origin);
                }
            }

            RenderTargetBitmap rendered = new RenderTargetBitmap(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32);

            rendered.Render(visual);
            rendered.Freeze();

            return PixelBuffer.FromBitmap(rendered);
        }

        /// <summary>
        /// 按网格把水印铺满整幅图。
        ///
        /// 三个必须处理的点：
        ///   1. **倾斜时要按对角线铺**，不能按宽高铺 —— 网格转了 30° 之后，
        ///      原本"刚好盖住"的矩形会在四个角留出空白。
        ///   2. **瓦片数要设上限**：间距是按百分比给的，在 20000px 的大图上按 5% 铺
        ///      就是 400×400 = 16 万个瓦片，会直接把界面卡死。超出上限时等比放大间距。
        ///   3. 间距小于文字本身时瓦片会糊成一团，所以要给一个不小于文字尺寸的下限。
        /// </summary>
        private static void DrawTiled(
            DrawingContext context,
            FormattedText formatted,
            Brush shadowBrush,
            int width,
            int height,
            TextOverlayOptions options)
        {
            double stepX = width * ClampPercent(options.TileSpacingXPercent) / 100.0;
            double stepY = height * ClampPercent(options.TileSpacingYPercent) / 100.0;

            // 下限：至少能放得下一个字（再密就糊成一团，不叫水印叫噪声）
            stepX = Math.Max(stepX, Math.Max(formatted.Width * 1.25, 12.0));
            stepY = Math.Max(stepY, Math.Max(formatted.Height * 1.5, 12.0));

            double angle = double.IsNaN(options.TileAngleDegrees) ? 0.0 : options.TileAngleDegrees;
            angle = Math.Max(-80.0, Math.Min(80.0, angle));

            // 旋转后要覆盖整幅图 ⇒ 按对角线铺
            double diagonal = Math.Sqrt((double)width * width + (double)height * height);
            double centerX = width / 2.0;
            double centerY = height / 2.0;

            int columns = (int)Math.Ceiling(diagonal / stepX) + 2;
            int rows = (int)Math.Ceiling(diagonal / stepY) + 2;

            if ((long)columns * rows > MaxTileCount)
            {
                double scale = Math.Sqrt((double)columns * rows / MaxTileCount);
                stepX *= scale;
                stepY *= scale;
                columns = (int)Math.Ceiling(diagonal / stepX) + 2;
                rows = (int)Math.Ceiling(diagonal / stepY) + 2;
            }

            double startX = centerX - columns * stepX / 2.0;
            double startY = centerY - rows * stepY / 2.0;

            // 文字在格子里居中，看起来才整齐
            double offsetX = Math.Max(0.0, (stepX - formatted.Width) / 2.0);
            double offsetY = Math.Max(0.0, (stepY - formatted.Height) / 2.0);

            bool rotated = Math.Abs(angle) > 0.01;

            if (rotated)
            {
                context.PushTransform(new RotateTransform(angle, centerX, centerY));
            }

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    DrawOne(
                        context,
                        formatted,
                        shadowBrush,
                        new Point(startX + column * stepX + offsetX, startY + row * stepY + offsetY));
                }
            }

            if (rotated)
            {
                context.Pop();
            }
        }

        private static void DrawOne(
            DrawingContext context,
            FormattedText formatted,
            Brush shadowBrush,
            Point origin)
        {
            if (shadowBrush != null)
            {
                Geometry shadowGeometry = formatted.BuildGeometry(new Point(origin.X + 2, origin.Y + 2));
                context.DrawGeometry(shadowBrush, null, shadowGeometry);
            }

            context.DrawText(formatted, origin);
        }

        private static double ClampPercent(double percent)
        {
            if (double.IsNaN(percent) || percent < 3.0)
            {
                return 3.0;
            }

            return percent > 200.0 ? 200.0 : percent;
        }

        /// <summary>
        /// 单幅图上的瓦片数上限。
        ///
        /// 取 4000 是因为每个瓦片都要建一次字形几何（开了投影时还要两次），
        /// 再多就会明显卡顿；而 4000 个水印覆盖任何尺寸的图都绰绰有余。
        /// </summary>
        private const int MaxTileCount = 4000;

        /// <summary>
        /// 异步包装：把渲染放到线程池执行，避免大图在 UI 线程上同步渲染造成卡顿。
        ///
        /// 早先这里是直接同步调用后返回已完成任务（理由是"RenderTargetBitmap 依赖 UI 线程"），
        /// 那个理由不成立（详见类型注释），代价却是单张加字时 UI 会僵住一下。
        /// </summary>
        public Task<PixelBuffer> ApplyAsync(
            IReadOnlyPixelBuffer source,
            TextOverlayOptions options,
            double dpiX,
            double dpiY,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Apply(source, options, dpiX, dpiY), cancellationToken);
        }

        private static Typeface BuildTypeface(TextOverlayOptions options)
        {
            FontFamily family;

            try
            {
                family = new FontFamily(string.IsNullOrWhiteSpace(options.FontFamilyName)
                    ? "Microsoft YaHei UI"
                    : options.FontFamilyName);
            }
            catch (ArgumentException)
            {
                // 字体名非法（Win7 上用户可能输入不存在的字体）时退回系统默认。
                family = new FontFamily("Segoe UI");
            }

            FontStyle style = options.Italic ? FontStyles.Italic : FontStyles.Normal;
            FontWeight weight = options.Bold ? FontWeights.Bold : FontWeights.Normal;

            return new Typeface(family, style, weight, FontStretches.Normal);
        }

        private static Point CalculateOrigin(
            FormattedText text,
            TextAnchor anchor,
            int width,
            int height,
            double margin)
        {
            double textWidth = text.Width;
            double textHeight = text.Height;

            double availableWidth = Math.Max(0.0, width - margin * 2);
            double availableHeight = Math.Max(0.0, height - margin * 2);

            switch (anchor)
            {
                case TextAnchor.TopLeft:
                    return new Point(margin, margin);
                case TextAnchor.TopCenter:
                    return new Point(margin + (availableWidth - textWidth) / 2.0, margin);
                case TextAnchor.TopRight:
                    return new Point(margin + availableWidth - textWidth, margin);
                case TextAnchor.MiddleLeft:
                    return new Point(margin, margin + (availableHeight - textHeight) / 2.0);
                case TextAnchor.Center:
                    return new Point(
                        margin + (availableWidth - textWidth) / 2.0,
                        margin + (availableHeight - textHeight) / 2.0);
                case TextAnchor.MiddleRight:
                    return new Point(
                        margin + availableWidth - textWidth,
                        margin + (availableHeight - textHeight) / 2.0);
                case TextAnchor.BottomLeft:
                    return new Point(margin, margin + availableHeight - textHeight);
                case TextAnchor.BottomCenter:
                    return new Point(
                        margin + (availableWidth - textWidth) / 2.0,
                        margin + availableHeight - textHeight);
                default:
                    return new Point(margin + availableWidth - textWidth, margin + availableHeight - textHeight);
            }
        }

        private static PixelBuffer Copy(IReadOnlyPixelBuffer source)
        {
            return new PixelBuffer(source.GetPixels(), source.Width, source.Height);
        }
    }
}
