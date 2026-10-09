using System;
using System.Windows.Media;

namespace PSText.Models
{
    /// <summary>标注类型。</summary>
    public enum AnnotationKind
    {
        Arrow = 0,
        Rectangle = 1,
        Ellipse = 2,
        NumberBadge = 3,
        Highlight = 4,
        Text = 5,

        /// <summary>遮盖（马赛克 / 模糊）。与其它类型不同：它的内容是**底图的像素级派生**，不是几何。</summary>
        Mosaic = 6
    }

    /// <summary>遮盖类标注的处理方式。</summary>
    public enum MosaicStyle
    {
        /// <summary>像素化（块平均）：打码文字、号码这类离散信息。</summary>
        Pixelate = 0,

        /// <summary>高斯模糊：遮人脸、背景这类连续色调更自然。</summary>
        Blur = 1
    }

    /// <summary>
    /// 标注对象（可变）。
    ///
    /// 为什么是"对象"而不是"画上去的像素"：标注要能**选中再改参数**
    /// （把箭头换个颜色、把序号往上挪一点），像素一旦画上去就改不动了。
    /// 因此标注以对象形式存在，画面 = 底图 + 全部对象实时渲染；
    /// 历史里保存的是**整列表的深拷贝快照**（对象只有几个，快照极便宜，完全不需要存像素）。
    ///
    /// 坐标全部是**图像像素坐标**（与画布叠加层 1:1），因此放大缩小不会让标注跑偏。
    /// </summary>
    public sealed class AnnotationObject
    {
        public AnnotationKind Kind { get; set; }

        /// <summary>起点（箭头尾 / 矩形一角 / 文字锚点）。</summary>
        public double X1 { get; set; }

        public double Y1 { get; set; }

        /// <summary>终点（箭头尖 / 矩形对角）。</summary>
        public double X2 { get; set; }

        public double Y2 { get; set; }

        /// <summary>主色。</summary>
        public Color Color { get; set; }

        /// <summary>线宽 / 箭头粗细（像素）。</summary>
        public double StrokeWidth { get; set; }

        /// <summary>文字内容（序号标注时是序号数字）。</summary>
        public string Text { get; set; }

        /// <summary>字号（像素）。</summary>
        public double FontSize { get; set; }

        /// <summary>遮盖类标注的处理方式。</summary>
        public MosaicStyle MosaicStyle { get; set; }

        /// <summary>
        /// 遮盖强度：马赛克时是块边长（像素），模糊时是模糊半径（像素）。
        ///
        /// 两种语义共用一个字段，是因为界面上只有一个"强度"滑块；
        /// 切换方式时沿用同一个值也更顺手（块 12 ↔ 半径 12 都算中等强度）。
        /// 真正的收敛逻辑在 <see cref="Services.Filters.MosaicFilter"/> 里，
        /// 由各自的 Clamp 决定合法区间。
        /// </summary>
        public double CoverSize { get; set; }

        public AnnotationObject()
        {
            Color = Color.FromRgb(0xE2, 0x4B, 0x4A);
            StrokeWidth = 4.0;
            FontSize = 28.0;
            Text = string.Empty;
            MosaicStyle = MosaicStyle.Pixelate;
            CoverSize = 12.0;
        }

        public double Left
        {
            get { return Math.Min(X1, X2); }
        }

        public double Top
        {
            get { return Math.Min(Y1, Y2); }
        }

        public double Width
        {
            get { return Math.Abs(X2 - X1); }
        }

        public double Height
        {
            get { return Math.Abs(Y2 - Y1); }
        }

        /// <summary>中点（序号标签等居中显示用）。</summary>
        public double CenterX
        {
            get { return (X1 + X2) / 2.0; }
        }

        public double CenterY
        {
            get { return (Y1 + Y2) / 2.0; }
        }

        /// <summary>深拷贝（历史快照用）。</summary>
        public AnnotationObject Clone()
        {
            return new AnnotationObject
            {
                Kind = Kind,
                X1 = X1,
                Y1 = Y1,
                X2 = X2,
                Y2 = Y2,
                Color = Color,
                StrokeWidth = StrokeWidth,
                Text = Text,
                FontSize = FontSize,
                MosaicStyle = MosaicStyle,
                CoverSize = CoverSize
            };
        }

        /// <summary>
        /// 该对象的**视觉包围盒**（比几何包围盒略大，因为箭头尖、线宽、字号都往外溢）。
        /// 命中测试与"是否需要重绘"都按它来判断。
        /// </summary>
        public System.Windows.Rect VisualBounds
        {
            get
            {
                // 外扩量必须**按类型**取，不能统一用 max(线宽, 字号)：
                // FontSize 对所有标注都有默认值 28，统一算的话一个 4px 粗的矩形也会得到
                // 20 多像素的抓取边距 —— 于是在它旁边点一下会被判成"选中并拖动"，
                // 而不是"在空白处新建一个标注"（这个坑是被自检 [35] 抓出来的）。
                double padding;

                switch (Kind)
                {
                    case AnnotationKind.Mosaic:
                        // 实心矩形，像素正好铺满几何，外扩会让选中框比遮盖区域大一圈。
                        padding = 0.0;
                        break;

                    case AnnotationKind.Text:
                    case AnnotationKind.NumberBadge:
                        padding = FontSize * 0.6 + 4.0;
                        break;

                    case AnnotationKind.Arrow:
                        // 箭头尖最大约 26px 长、半宽 12px 左右，会伸出几何盒之外。
                        padding = Math.Max(StrokeWidth, 12.0) + 4.0;
                        break;

                    default:
                        padding = StrokeWidth / 2.0 + 4.0;
                        break;
                }

                return new System.Windows.Rect(
                    Left - padding,
                    Top - padding,
                    Width + padding * 2.0,
                    Height + padding * 2.0);
            }
        }

        /// <summary>点是否落在对象上（命中测试，tolerance 为额外容差）。</summary>
        public bool HitTest(double x, double y, double tolerance)
        {
            System.Windows.Rect bounds = VisualBounds;

            return x >= bounds.Left - tolerance
                   && x <= bounds.Right + tolerance
                   && y >= bounds.Top - tolerance
                   && y <= bounds.Bottom + tolerance;
        }
    }
}
