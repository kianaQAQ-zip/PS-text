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
        }

        /// <summary>文字内容。</summary>
        public string Text { get; set; }

        /// <summary>字体名称。</summary>
        public string FontFamilyName { get; set; }

        /// <summary>字号（像素）。</summary>
        public double FontSize { get; set; }

        /// <summary>文字颜色。</summary>
        public Color Color { get; set; }

        /// <summary>锚点位置。</summary>
        public TextAnchor Anchor { get; set; }

        /// <summary>距边缘的边距（像素）。</summary>
        public double Margin { get; set; }

        /// <summary>是否加粗。</summary>
        public bool Bold { get; set; }

        /// <summary>是否斜体。</summary>
        public bool Italic { get; set; }

        /// <summary>是否绘制投影（提升可读性）。</summary>
        public bool Shadow { get; set; }
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
    /// 注意：RenderTargetBitmap 必须在 UI 线程创建（WPF 的 DispatcherObject 约定），
    /// 因此本方法内部通过调用方提供的调度器切回 UI 线程，保证在 Win7 上行为稳定。
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

                Point origin = CalculateOrigin(formatted, options.Anchor, width, height, margin);

                if (options.Shadow)
                {
                    // 投影：偏移 2 像素的半透明黑色，保证浅色背景上也能看清
                    Geometry shadowGeometry = formatted.BuildGeometry(new Point(origin.X + 2, origin.Y + 2));
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), null, shadowGeometry);
                }

                context.DrawText(formatted, origin);
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

        /// <summary>异步包装（内部依然要求 UI 线程，由调用方的调度器保证）。</summary>
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

            // RenderTargetBitmap 依赖 UI 线程，这里不做 Task.Run，直接调用并返回已完成任务。
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Apply(source, options, dpiX, dpiY));
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
