using System;
using System.Globalization;
using System.Threading;
using System.Windows.Media;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services.Filters;

namespace PSText.Services.Batch
{
    /// <summary>缩放模式。</summary>
    public enum BatchResizeMode
    {
        /// <summary>按百分比（相对参数，不受预览缩放影响）。</summary>
        Percent = 0,

        /// <summary>约束长边（绝对像素）。</summary>
        LongEdge,

        /// <summary>约束宽度（绝对像素）。</summary>
        FitWidth,

        /// <summary>约束高度（绝对像素）。</summary>
        FitHeight,

        /// <summary>精确宽高（绝对像素，不保持比例）。</summary>
        Exact
    }

    /// <summary>
    /// 尺寸缩放步骤。
    ///
    /// 与单张编辑的"尺寸"面板共用 <see cref="Resampler"/>，因此抗混叠与预乘 alpha 的处理完全一致，
    /// 批量不会因为"走的是另一条路径"而出现质量差异。
    /// </summary>
    public sealed class BatchResizeStep : IBatchStep
    {
        public BatchResizeStep()
        {
            Mode = BatchResizeMode.LongEdge;
            Value = 1600;
            Width = 800;
            Height = 600;
            Kernel = ResampleKernel.Bicubic;
            OnlyShrink = true;
        }

        public BatchStepKind Kind
        {
            get { return BatchStepKind.Resize; }
        }

        public string DisplayName
        {
            get { return "尺寸缩放"; }
        }

        public BatchResizeMode Mode { get; set; }

        /// <summary>Percent / LongEdge / FitWidth / FitHeight 模式下的数值。</summary>
        public int Value { get; set; }

        /// <summary>Exact 模式下的目标宽度。</summary>
        public int Width { get; set; }

        /// <summary>Exact 模式下的目标高度。</summary>
        public int Height { get; set; }

        /// <summary>重采样核。</summary>
        public ResampleKernel Kernel { get; set; }

        /// <summary>只缩不放（小图保持原样，避免被放大成模糊图）。</summary>
        public bool OnlyShrink { get; set; }

        public string Summary
        {
            get
            {
                string text;

                switch (Mode)
                {
                    case BatchResizeMode.Percent:
                        text = string.Format(CultureInfo.InvariantCulture, "缩放到 {0}%", Value);
                        break;
                    case BatchResizeMode.FitWidth:
                        text = string.Format(CultureInfo.InvariantCulture, "宽度 {0} px", Value);
                        break;
                    case BatchResizeMode.FitHeight:
                        text = string.Format(CultureInfo.InvariantCulture, "高度 {0} px", Value);
                        break;
                    case BatchResizeMode.Exact:
                        text = string.Format(CultureInfo.InvariantCulture, "{0} × {1} px（不保持比例）", Width, Height);
                        break;
                    default:
                        text = string.Format(CultureInfo.InvariantCulture, "长边 {0} px", Value);
                        break;
                }

                if (OnlyShrink)
                {
                    text += " · 只缩不放";
                }

                return text + " · " + DescribeKernel(Kernel);
            }
        }

        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (context == null)
            {
                context = BatchContext.FullResolution;
            }

            int sourceWidth = source.Width;
            int sourceHeight = source.Height;

            ReadTargetSize(sourceWidth, sourceHeight, context, out int targetWidth, out int targetHeight);

            if (OnlyShrink && targetWidth >= sourceWidth && targetHeight >= sourceHeight)
            {
                // 已经比目标还小：保持原样（不放大）。
                return CopyOf(source, cancellationToken);
            }

            if (targetWidth == sourceWidth && targetHeight == sourceHeight)
            {
                return CopyOf(source, cancellationToken);
            }

            return Resampler.Resize(source, targetWidth, targetHeight, Kernel, cancellationToken);
        }

        public IBatchStep Clone()
        {
            return new BatchResizeStep
            {
                Mode = Mode,
                Value = Value,
                Width = Width,
                Height = Height,
                Kernel = Kernel,
                OnlyShrink = OnlyShrink
            };
        }

        /// <summary>
        /// 计算目标尺寸。
        ///
        /// 预览与输出的唯一差别就在这里：绝对像素模式要把目标值乘上预览缩放因子，
        /// 百分比模式不能乘（百分比是相对量，降采样不改变比例关系）。写错这一处，
        /// 面板缩略图就会与实际输出不一致 —— 所以单独抽出来便于自检直接钉住。
        /// </summary>
        internal void ReadTargetSize(
            int sourceWidth,
            int sourceHeight,
            BatchContext context,
            out int targetWidth,
            out int targetHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                targetWidth = Math.Max(1, sourceWidth);
                targetHeight = Math.Max(1, sourceHeight);
                return;
            }

            switch (Mode)
            {
                case BatchResizeMode.Percent:
                {
                    // 相对参数：不乘 Scale。
                    double percent = Math.Max(1, Value) / 100.0;
                    targetWidth = ClampAtLeastOne((int)Math.Round(sourceWidth * percent, MidpointRounding.AwayFromZero));
                    targetHeight = ClampAtLeastOne((int)Math.Round(sourceHeight * percent, MidpointRounding.AwayFromZero));
                    break;
                }

                case BatchResizeMode.FitWidth:
                {
                    int wanted = ClampAtLeastOne(context.ScaleLength(Value));
                    double factor = wanted / (double)sourceWidth;
                    targetWidth = wanted;
                    targetHeight = ClampAtLeastOne((int)Math.Round(sourceHeight * factor, MidpointRounding.AwayFromZero));
                    break;
                }

                case BatchResizeMode.FitHeight:
                {
                    int wanted = ClampAtLeastOne(context.ScaleLength(Value));
                    double factor = wanted / (double)sourceHeight;
                    targetWidth = ClampAtLeastOne((int)Math.Round(sourceWidth * factor, MidpointRounding.AwayFromZero));
                    targetHeight = wanted;
                    break;
                }

                case BatchResizeMode.Exact:
                {
                    targetWidth = ClampAtLeastOne(context.ScaleLength(Width));
                    targetHeight = ClampAtLeastOne(context.ScaleLength(Height));
                    break;
                }

                default:
                {
                    int wanted = ClampAtLeastOne(context.ScaleLength(Value));
                    bool landscape = sourceWidth >= sourceHeight;

                    if (landscape)
                    {
                        double factor = wanted / (double)sourceWidth;
                        targetWidth = wanted;
                        targetHeight = ClampAtLeastOne((int)Math.Round(sourceHeight * factor, MidpointRounding.AwayFromZero));
                    }
                    else
                    {
                        double factor = wanted / (double)sourceHeight;
                        targetWidth = ClampAtLeastOne((int)Math.Round(sourceWidth * factor, MidpointRounding.AwayFromZero));
                        targetHeight = wanted;
                    }

                    break;
                }
            }
        }

        private static int ClampAtLeastOne(int value)
        {
            return value < 1 ? 1 : value;
        }

        private static PixelBuffer CopyOf(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int stride = source.Width * 4;
            byte[] pixels = new byte[stride * source.Height];

            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(y, pixels, y * stride, stride);
            }

            return new PixelBuffer(pixels, source.Width, source.Height);
        }

        internal static string DescribeKernel(ResampleKernel kernel)
        {
            switch (kernel)
            {
                case ResampleKernel.Bilinear:
                    return "双线性";
                case ResampleKernel.NearestNeighbor:
                    return "邻近";
                default:
                    return "三次立方";
            }
        }
    }

    /// <summary>基础调整步骤（纯查表，天然与分辨率无关）。</summary>
    public sealed class BatchAdjustmentStep : IBatchStep
    {
        public BatchAdjustmentStep()
        {
            Adjustments = PixelAdjustments.Neutral;
        }

        public BatchStepKind Kind
        {
            get { return BatchStepKind.Adjustments; }
        }

        public string DisplayName
        {
            get { return "基础调整"; }
        }

        public PixelAdjustments Adjustments { get; set; }

        public string Summary
        {
            get
            {
                PixelAdjustments value = Adjustments ?? PixelAdjustments.Neutral;
                return value.IsNeutral ? "中性（无变化）" : value.ToDisplayString();
            }
        }

        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return new AdjustmentsFilter().Apply(source, Adjustments ?? PixelAdjustments.Neutral, null, cancellationToken);
        }

        public IBatchStep Clone()
        {
            return new BatchAdjustmentStep { Adjustments = Adjustments ?? PixelAdjustments.Neutral };
        }
    }

    /// <summary>翻转与 90° 倍数旋转步骤。</summary>
    public sealed class BatchFlipRotateStep : IBatchStep
    {
        public BatchStepKind Kind
        {
            get { return BatchStepKind.FlipRotate; }
        }

        public string DisplayName
        {
            get { return "翻转旋转"; }
        }

        public bool FlipHorizontal { get; set; }

        public bool FlipVertical { get; set; }

        /// <summary>顺时针 90° 倍数旋转。</summary>
        public RotationAngle Angle { get; set; }

        public string Summary
        {
            get
            {
                System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();

                if (Angle != RotationAngle.None)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "顺时针 {0}°", (int)Angle));
                }

                if (FlipHorizontal)
                {
                    parts.Add("左右翻转");
                }

                if (FlipVertical)
                {
                    parts.Add("上下翻转");
                }

                return parts.Count == 0 ? "无变化" : string.Join(" · ", parts.ToArray());
            }
        }

        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            GeometryFilters filters = new GeometryFilters();

            // 先转正再镜像：用户的心智模型是"先把照片转正，再决定要不要镜像"。
            PixelBuffer current = filters.Rotate(source, Angle, cancellationToken);

            if (FlipHorizontal)
            {
                current = filters.FlipHorizontal(current, cancellationToken);
            }

            if (FlipVertical)
            {
                current = filters.FlipVertical(current, cancellationToken);
            }

            return current;
        }

        public IBatchStep Clone()
        {
            return new BatchFlipRotateStep
            {
                FlipHorizontal = FlipHorizontal,
                FlipVertical = FlipVertical,
                Angle = Angle
            };
        }
    }

    /// <summary>边框步骤。</summary>
    public sealed class BatchBorderStep : IBatchStep
    {
        public BatchBorderStep()
        {
            Style = BorderStyle.Solid;
            Width = 12;
            Color = Colors.White;
            SecondaryColor = Color.FromRgb(0x33, 0x33, 0x33);
        }

        public BatchStepKind Kind
        {
            get { return BatchStepKind.Border; }
        }

        public string DisplayName
        {
            get { return "边框"; }
        }

        public BorderStyle Style { get; set; }

        /// <summary>边框宽度（绝对像素，会按预览缩放因子换算）。</summary>
        public int Width { get; set; }

        public Color Color { get; set; }

        public Color SecondaryColor { get; set; }

        public string Summary
        {
            get
            {
                string style;

                switch (Style)
                {
                    case BorderStyle.InnerLine:
                        style = "内白外黑";
                        break;
                    case BorderStyle.Shadow:
                        style = "柔和阴影";
                        break;
                    case BorderStyle.None:
                        style = "无";
                        break;
                    default:
                        style = "实线";
                        break;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} px · {2}",
                    style,
                    Width,
                    BatchColorNames.Describe(Style == BorderStyle.Solid ? Color : SecondaryColor));
            }
        }

        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (context == null)
            {
                context = BatchContext.FullResolution;
            }

            return new BorderFilters().Apply(
                source,
                Style,
                context.ScaleLength(Width),
                Color,
                SecondaryColor,
                cancellationToken);
        }

        public IBatchStep Clone()
        {
            return new BatchBorderStep
            {
                Style = Style,
                Width = Width,
                Color = Color,
                SecondaryColor = SecondaryColor
            };
        }
    }

    /// <summary>水印字号的计量方式。</summary>
    public enum WatermarkSizeMode
    {
        /// <summary>固定像素：适合"所有图尺寸一样"的场景。</summary>
        Absolute = 0,

        /// <summary>按图像宽度的百分比：批量场景的默认值。</summary>
        RelativeToWidth
    }

    /// <summary>
    /// 文字水印步骤。
    ///
    /// 为什么默认"按图像宽度百分比"：批量处理的图长宽常常不一致，
    /// 固定像素字号会让 4000 px 的大图上水印小得看不见、600 px 的小图上占满半屏。
    /// 相对字号同时还有一个副作用是好的 —— 预览降采样后它天然还是对的，不需要额外换算。
    /// </summary>
    public sealed class BatchWatermarkStep : IBatchStep
    {
        public BatchWatermarkStep()
        {
            Text = "仅供 XX 使用";
            FontFamilyName = "Microsoft YaHei UI";
            SizeMode = WatermarkSizeMode.RelativeToWidth;
            FontSize = 5.0;
            Margin = 2.0;
            Color = Colors.White;
            Opacity = 170;
            Anchor = TextAnchor.BottomRight;
            Bold = true;
            Italic = false;
            Shadow = false;
        }

        public BatchStepKind Kind
        {
            get { return BatchStepKind.Watermark; }
        }

        public string DisplayName
        {
            get { return "文字水印"; }
        }

        public string Text { get; set; }

        public string FontFamilyName { get; set; }

        public WatermarkSizeMode SizeMode { get; set; }

        /// <summary>Absolute 模式为像素；RelativeToWidth 模式为占图像宽度的百分比。</summary>
        public double FontSize { get; set; }

        /// <summary>边距。计量方式与 <see cref="FontSize"/> 相同。</summary>
        public double Margin { get; set; }

        public Color Color { get; set; }

        /// <summary>不透明度（0~255）。半透明水印的核心参数。</summary>
        public byte Opacity { get; set; }

        public TextAnchor Anchor { get; set; }

        public bool Bold { get; set; }

        public bool Italic { get; set; }

        public bool Shadow { get; set; }

        public string Summary
        {
            get
            {
                string text = string.IsNullOrEmpty(Text) ? "（未填写文字）" : "「" + Text + "」";

                string size = SizeMode == WatermarkSizeMode.RelativeToWidth
                    ? string.Format(CultureInfo.InvariantCulture, "宽度 {0:0.#}%", FontSize)
                    : string.Format(CultureInfo.InvariantCulture, "{0:0.#} px", FontSize);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · {1} · {2} · 不透明度 {3}%",
                    text,
                    size,
                    DescribeAnchor(Anchor),
                    (int)Math.Round(Opacity / 255.0 * 100.0));
            }
        }

        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (context == null)
            {
                context = BatchContext.FullResolution;
            }

            cancellationToken.ThrowIfCancellationRequested();

            double width = source.Width;
            double fontSize;
            double margin;

            if (SizeMode == WatermarkSizeMode.RelativeToWidth)
            {
                // 相对参数：直接用当前（可能是降采样的）宽度换算，因此预览天然一致。
                fontSize = width * Math.Max(0.0, FontSize) / 100.0;
                margin = width * Math.Max(0.0, Margin) / 100.0;
            }
            else
            {
                fontSize = context.ScaleLength(Math.Max(0.0, FontSize));
                margin = context.ScaleLength(Math.Max(0.0, Margin));
            }

            if (fontSize < 1.0)
            {
                // 缩到看不清的字号没有意义，退化成"不加水印"，避免输出一片噪点。
                return CopyOf(source, cancellationToken);
            }

            TextOverlayOptions options = new TextOverlayOptions
            {
                Text = Text,
                FontFamilyName = FontFamilyName,
                FontSize = fontSize,
                Color = Color.FromArgb(Opacity, Color.R, Color.G, Color.B),
                Anchor = Anchor,
                Margin = margin,
                Bold = Bold,
                Italic = Italic,
                Shadow = Shadow
            };

            return new TextOverlayFilter().Apply(source, options, context.DpiX, context.DpiY);
        }

        public IBatchStep Clone()
        {
            return new BatchWatermarkStep
            {
                Text = Text,
                FontFamilyName = FontFamilyName,
                SizeMode = SizeMode,
                FontSize = FontSize,
                Margin = Margin,
                Color = Color,
                Opacity = Opacity,
                Anchor = Anchor,
                Bold = Bold,
                Italic = Italic,
                Shadow = Shadow
            };
        }

        /// <summary>九宫格锚点的中文名。</summary>
        public static string DescribeAnchor(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.TopLeft:
                    return "左上";
                case TextAnchor.TopCenter:
                    return "上中";
                case TextAnchor.TopRight:
                    return "右上";
                case TextAnchor.MiddleLeft:
                    return "左中";
                case TextAnchor.Center:
                    return "居中";
                case TextAnchor.MiddleRight:
                    return "右中";
                case TextAnchor.BottomLeft:
                    return "左下";
                case TextAnchor.BottomCenter:
                    return "下中";
                default:
                    return "右下";
            }
        }

        private static PixelBuffer CopyOf(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int stride = source.Width * 4;
            byte[] pixels = new byte[stride * source.Height];

            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(y, pixels, y * stride, stride);
            }

            return new PixelBuffer(pixels, source.Width, source.Height);
        }
    }

    /// <summary>常见颜色的中文描述（面板摘要里用，避免显示一串 #AARRGGBB 让人看不懂）。</summary>
    internal static class BatchColorNames
    {
        public static string Describe(Color color)
        {
            if (color.A < 250)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}（透明度 {3}%）",
                    color.R,
                    color.G,
                    color.B,
                    (int)Math.Round(color.A / 255.0 * 100.0));
            }

            if (color.R == 255 && color.G == 255 && color.B == 255)
            {
                return "白色";
            }

            if (color.R == 0 && color.G == 0 && color.B == 0)
            {
                return "黑色";
            }

            if (color.R == color.G && color.G == color.B)
            {
                return string.Format(CultureInfo.InvariantCulture, "灰阶 {0}", color.R);
            }

            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }
    }
}
