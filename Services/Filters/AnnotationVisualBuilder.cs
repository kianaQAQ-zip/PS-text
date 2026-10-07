using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PSText.Models;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 一个标注对象"长什么样"：要画的几何 + 填充 / 描边 + 文字几何。
    ///
    /// 之所以把描述抽出来共用：**叠加层预览与最终合并必须用同一份几何**。
    /// 如果两边各画各的，用户看到的和合并进画面的就会对不上 —— 这类"所见非所得"最难查。
    /// </summary>
    public sealed class AnnotationVisual
    {
        /// <summary>主体几何（可为 null，例如纯文字标注把文字放在 LabelGeometry 之外）。</summary>
        public Geometry Geometry { get; set; }

        public Brush Fill { get; set; }

        public Brush Stroke { get; set; }

        public double StrokeThickness { get; set; }

        /// <summary>附加文字几何（序号数字等；白色描边效果用它单独填色）。</summary>
        public Geometry LabelGeometry { get; set; }

        public Brush LabelBrush { get; set; }
    }

    /// <summary>把标注对象翻译成可绘制的几何（叠加层与合并渲染共用）。</summary>
    public static class AnnotationVisualBuilder
    {
        /// <summary>标注文字用的字体（与界面保持一致）。</summary>
        private const string FontFamilyName = "Microsoft YaHei UI, Segoe UI, Arial";

        /// <summary>箭头头的最大长度（像素）。</summary>
        private const double MaxArrowHeadLength = 26.0;

        public static AnnotationVisual Build(AnnotationObject item)
        {
            AnnotationVisual visual = new AnnotationVisual
            {
                StrokeThickness = item.StrokeWidth
            };

            if (item == null)
            {
                return visual;
            }

            SolidColorBrush strokeBrush = new SolidColorBrush(item.Color);
            strokeBrush.Freeze();

            switch (item.Kind)
            {
                case AnnotationKind.Arrow:
                    visual.Geometry = BuildArrow(item);
                    visual.Fill = strokeBrush;
                    break;

                case AnnotationKind.Rectangle:
                    visual.Geometry = Freeze(new RectangleGeometry(
                        new Rect(item.Left, item.Top, item.Width, item.Height)));
                    visual.Stroke = strokeBrush;
                    break;

                case AnnotationKind.Ellipse:
                    visual.Geometry = Freeze(new EllipseGeometry(
                        new Point(item.CenterX, item.CenterY),
                        Math.Max(0.5, item.Width / 2.0),
                        Math.Max(0.5, item.Height / 2.0)));
                    visual.Stroke = strokeBrush;
                    break;

                case AnnotationKind.Highlight:
                    // 高亮是半透明的"记号笔"，因此填充而不是描边
                    SolidColorBrush highlight = new SolidColorBrush(item.Color);
                    highlight.Opacity = 0.35;
                    highlight.Freeze();

                    visual.Geometry = Freeze(new RectangleGeometry(
                        new Rect(item.Left, item.Top, item.Width, item.Height)));
                    visual.Fill = highlight;
                    break;

                case AnnotationKind.NumberBadge:
                    double radius = Math.Max(6.0, Math.Max(item.Width, item.Height) / 2.0);
                    visual.Geometry = Freeze(new EllipseGeometry(
                        new Point(item.CenterX, item.CenterY), radius, radius));
                    visual.Fill = strokeBrush;

                    SolidColorBrush labelBrush = new SolidColorBrush(Colors.White);
                    labelBrush.Freeze();
                    visual.LabelBrush = labelBrush;
                    visual.LabelGeometry = BuildCenteredLabel(item, item.Text, item.FontSize, visible: true);
                    break;

                case AnnotationKind.Text:
                    visual.Geometry = BuildLabelAt(item, item.Text, item.FontSize, item.X1, item.Y1);
                    visual.Fill = strokeBrush;
                    break;
            }

            return visual;
        }

        /// <summary>
        /// 箭头：用**一个填充多边形**画出来（箭杆是细长的四边形、箭头是三角形）。
        ///
        /// 为什么不用"一条 Line + 一个三角"两个元素：合成成一个多边形后，
        /// 叠加层与合并渲染都只需一次 DrawGeometry，两者天然一致；
        /// 而且线宽变化时箭杆与箭头不会出现接缝。
        /// </summary>
        private static Geometry BuildArrow(AnnotationObject item)
        {
            double dx = item.X2 - item.X1;
            double dy = item.Y2 - item.Y1;
            double length = Math.Sqrt(dx * dx + dy * dy);

            if (length < 1e-6)
            {
                return Geometry.Empty;
            }

            double ux = dx / length;
            double uy = dy / length;
            double px = -uy;
            double py = ux;

            double headLength = Math.Min(MaxArrowHeadLength, Math.Max(6.0, length * 0.35));
            double headHalf = Math.Max(item.StrokeWidth * 0.9, headLength * 0.45);
            double shaftHalf = Math.Max(0.5, item.StrokeWidth / 2.0);

            // 箭头根部（箭杆结束、箭头开始的位置）
            double baseX = item.X2 - ux * headLength;
            double baseY = item.Y2 - uy * headLength;

            PathFigure figure = new PathFigure
            {
                StartPoint = new Point(item.X1 + px * shaftHalf, item.Y1 + py * shaftHalf),
                IsClosed = true,
                IsFilled = true
            };

            figure.Segments.Add(new LineSegment(
                new Point(baseX + px * shaftHalf, baseY + py * shaftHalf), true));
            figure.Segments.Add(new LineSegment(
                new Point(baseX + px * headHalf, baseY + py * headHalf), true));
            figure.Segments.Add(new LineSegment(new Point(item.X2, item.Y2), true));
            figure.Segments.Add(new LineSegment(
                new Point(baseX - px * headHalf, baseY - py * headHalf), true));
            figure.Segments.Add(new LineSegment(
                new Point(baseX - px * shaftHalf, baseY - py * shaftHalf), true));
            figure.Segments.Add(new LineSegment(
                new Point(item.X1 - px * shaftHalf, item.Y1 - py * shaftHalf), true));

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            return Freeze(geometry);
        }

        /// <summary>居中于对象中心的文字（序号标注）。</summary>
        private static Geometry BuildCenteredLabel(AnnotationObject item, string text, double fontSize, bool visible)
        {
            if (!visible || string.IsNullOrEmpty(text))
            {
                return null;
            }

            FormattedText formatted = CreateFormattedText(text, fontSize, Brushes.White);

            return BuildLabelAt(item, text, fontSize, item.CenterX - formatted.Width / 2.0, item.CenterY - formatted.Height / 2.0);
        }

        /// <summary>在指定位置生成文字几何（左上角对齐）。</summary>
        private static Geometry BuildLabelAt(AnnotationObject item, string text, double fontSize, double left, double top)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            FormattedText formatted = CreateFormattedText(text, fontSize, Brushes.Black);
            Geometry geometry = formatted.BuildGeometry(new Point(left, top));

            return geometry == null ? null : Freeze(geometry);
        }

        private static FormattedText CreateFormattedText(string text, double fontSize, Brush brush)
        {
            Typeface typeface = new Typeface(
                new FontFamily(FontFamilyName),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);

            double size = fontSize < 6.0 ? 6.0 : fontSize;

            // 96 DPI：这样 1 DIP = 1 图像像素，叠加层坐标与像素坐标严格一致。
            return new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                brush,
                96.0);
        }

        private static Geometry Freeze(Geometry geometry)
        {
            if (geometry != null && geometry.CanFreeze)
            {
                geometry.Freeze();
            }

            return geometry;
        }
    }
}
