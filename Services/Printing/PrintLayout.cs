using System;
using System.Windows;

namespace PSText.Services.Printing
{
    /// <summary>打印布局模式（需求 P2-10）。</summary>
    public enum PrintLayoutMode
    {
        /// <summary>居中：图像按“适应”缩放后居中，四周留白（可自由拖动）。</summary>
        Center = 0,

        /// <summary>填充：缩放到铺满整张纸（会裁剪超出部分，不出现白边）。</summary>
        Fill,

        /// <summary>适应：完整放进纸张可打印区域，不裁剪、不超出。</summary>
        Fit,

        /// <summary>原始尺寸：按图像自身 DPI 还原物理尺寸（可能超出纸张并分页）。</summary>
        OriginalSize
    }

    /// <summary>
    /// 打印视图状态（用户可调部分）：布局模式、缩放比例、相对基准位置的偏移。
    /// 由它 + 纸张信息推导出最终 <see cref="PrintLayout"/>。
    /// </summary>
    public sealed class PrintViewState
    {
        public PrintViewState()
        {
            Mode = PrintLayoutMode.Fit;
            Scale = 1.0;
            OffsetX = 0.0;
            OffsetY = 0.0;
        }

        /// <summary>布局模式。</summary>
        public PrintLayoutMode Mode { get; set; }

        /// <summary>用户缩放系数（1.0 = 该模式下的基准大小）。</summary>
        public double Scale { get; set; }

        /// <summary>相对基准位置的横向偏移（DIP）。</summary>
        public double OffsetX { get; set; }

        /// <summary>相对基准位置的纵向偏移（DIP）。</summary>
        public double OffsetY { get; set; }

        /// <summary>重置为用户可预期的默认状态。</summary>
        public void Reset(PrintLayoutMode mode)
        {
            Mode = mode;
            Scale = 1.0;
            OffsetX = 0.0;
            OffsetY = 0.0;
        }
    }

    /// <summary>
    /// 一次打印的完整版面计算结果（不可变）。
    ///
    /// 所有尺寸单位均为 DIP，坐标系原点在**纸张左上角**（不是可打印区域左上角），
    /// 这样预览窗口可以直接把纸张画在 (0,0)，图像位置直接沿用即可。
    /// </summary>
    public sealed class PrintLayout
    {
        private PrintLayout(
            Rect paper,
            Rect printableArea,
            Rect imageBounds,
            bool isClipped,
            double effectiveDpiX,
            double effectiveDpiY,
            int pageColumns,
            int pageRows,
            PrintLayoutMode mode)
        {
            Paper = paper;
            PrintableArea = printableArea;
            ImageBounds = imageBounds;
            IsClipped = isClipped;
            EffectiveDpiX = effectiveDpiX;
            EffectiveDpiY = effectiveDpiY;
            PageColumns = pageColumns;
            PageRows = pageRows;
            Mode = mode;
        }

        /// <summary>纸张矩形（原点 0,0）。</summary>
        public Rect Paper { get; private set; }

        /// <summary>可打印区域（已扣除打印机硬边距）。</summary>
        public Rect PrintableArea { get; private set; }

        /// <summary>图像在纸张坐标系中的位置与大小（DIP）。</summary>
        public Rect ImageBounds { get; private set; }

        /// <summary>图像是否被纸张/可打印区域裁剪。</summary>
        public bool IsClipped { get; private set; }

        /// <summary>实际打印分辨率（每英寸像素），用于提示模糊风险。</summary>
        public double EffectiveDpiX { get; private set; }

        /// <summary>实际打印分辨率（纵向）。</summary>
        public double EffectiveDpiY { get; private set; }

        /// <summary>横向页数。</summary>
        public int PageColumns { get; private set; }

        /// <summary>纵向页数。</summary>
        public int PageRows { get; private set; }

        /// <summary>总页数。</summary>
        public int PageCount
        {
            get { return Math.Max(1, PageColumns * PageRows); }
        }

        /// <summary>使用的布局模式。</summary>
        public PrintLayoutMode Mode { get; private set; }

        /// <summary>
        /// 计算版面。
        /// </summary>
        /// <param name="viewState">用户可调状态。</param>
        /// <param name="paperSizeDips">纸张尺寸（DIP，含不可打印边距）。</param>
        /// <param name="hardMarginDips">打印机硬边距（DIP）。</param>
        /// <param name="imagePixelWidth">图像像素宽度。</param>
        /// <param name="imagePixelHeight">图像像素高度。</param>
        /// <param name="imageDpiX">图像水平 DPI（决定物理尺寸）。</param>
        /// <param name="imageDpiY">图像垂直 DPI。</param>
        public static PrintLayout Create(
            PrintViewState viewState,
            Size paperSizeDips,
            Thickness hardMarginDips,
            int imagePixelWidth,
            int imagePixelHeight,
            double imageDpiX,
            double imageDpiY)
        {
            if (viewState == null)
            {
                viewState = new PrintViewState();
            }

            double paperWidth = Math.Max(1.0, paperSizeDips.Width);
            double paperHeight = Math.Max(1.0, paperSizeDips.Height);

            double marginLeft = Math.Max(0.0, hardMarginDips.Left);
            double marginTop = Math.Max(0.0, hardMarginDips.Top);
            double marginRight = Math.Max(0.0, hardMarginDips.Right);
            double marginBottom = Math.Max(0.0, hardMarginDips.Bottom);

            Rect paper = new Rect(0, 0, paperWidth, paperHeight);
            Rect printable = new Rect(
                marginLeft,
                marginTop,
                Math.Max(1.0, paperWidth - marginLeft - marginRight),
                Math.Max(1.0, paperHeight - marginTop - marginBottom));

            if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            {
                return new PrintLayout(paper, printable, Rect.Empty, false, 0, 0, 1, 1, viewState.Mode);
            }

            // 图像在“像素 : DIP = 1 : 1”下的物理尺寸
            double naturalWidth = PrintUnits.PixelsToDips(imagePixelWidth, imageDpiX);
            double naturalHeight = PrintUnits.PixelsToDips(imagePixelHeight, imageDpiY);

            if (naturalWidth <= 0.001 || naturalHeight <= 0.001)
            {
                naturalWidth = imagePixelWidth;
                naturalHeight = imagePixelHeight;
            }

            double baseScaleX = printable.Width / naturalWidth;
            double baseScaleY = printable.Height / naturalHeight;

            double scaleX;
            double scaleY;
            double anchorX;
            double anchorY;

            switch (viewState.Mode)
            {
                case PrintLayoutMode.Fill:
                    // 填充：取较大比例，保证铺满（会有裁剪）
                    scaleX = Math.Max(baseScaleX, baseScaleY);
                    scaleY = scaleX;
                    anchorX = printable.Left + (printable.Width - naturalWidth * scaleX) / 2.0;
                    anchorY = printable.Top + (printable.Height - naturalHeight * scaleY) / 2.0;
                    break;

                case PrintLayoutMode.OriginalSize:
                    // 原始尺寸：严格按图像 DPI 还原，不缩放
                    scaleX = 1.0;
                    scaleY = 1.0;
                    anchorX = printable.Left + (printable.Width - naturalWidth) / 2.0;
                    anchorY = printable.Top + (printable.Height - naturalHeight) / 2.0;
                    break;

                case PrintLayoutMode.Center:
                case PrintLayoutMode.Fit:
                default:
                    // 居中 / 适应：取较小比例，保证完整可见
                    scaleX = Math.Min(baseScaleX, baseScaleY);
                    scaleY = scaleX;
                    anchorX = printable.Left + (printable.Width - naturalWidth * scaleX) / 2.0;
                    anchorY = printable.Top + (printable.Height - naturalHeight * scaleY) / 2.0;
                    break;
            }

            double userScale = viewState.Scale;
            if (double.IsNaN(userScale) || double.IsInfinity(userScale) || userScale <= 0.0)
            {
                userScale = 1.0;
            }

            double imageWidth = naturalWidth * scaleX * userScale;
            double imageHeight = naturalHeight * scaleY * userScale;

            if (imageWidth < 1.0)
            {
                imageWidth = 1.0;
            }

            if (imageHeight < 1.0)
            {
                imageHeight = 1.0;
            }

            // 超出基准尺寸时，锚点保持在可打印区域中心
            double overflowX = Math.Max(0.0, imageWidth - naturalWidth * scaleX);
            double overflowY = Math.Max(0.0, imageHeight - naturalHeight * scaleY);

            double baseX = anchorX - overflowX / 2.0;
            double baseY = anchorY - overflowY / 2.0;

            double offsetX = ClampOffset(viewState.OffsetX, baseX, imageWidth, printable.Left, printable.Width, viewState.Mode);
            double offsetY = ClampOffset(viewState.OffsetY, baseY, imageHeight, printable.Top, printable.Height, viewState.Mode);

            Rect imageBounds = new Rect(baseX + offsetX, baseY + offsetY, imageWidth, imageHeight);

            // 是否被裁剪：图像超出纸张即视为裁剪（预览里会提示）
            bool clipped = imageBounds.Left < paper.Left - 0.01
                           || imageBounds.Top < paper.Top - 0.01
                           || imageBounds.Right > paper.Right + 0.01
                           || imageBounds.Bottom > paper.Bottom + 0.01;

            double effectiveDpiX = PrintUnits.EffectiveDpi(imagePixelWidth, imageWidth);
            double effectiveDpiY = PrintUnits.EffectiveDpi(imagePixelHeight, imageHeight);

            int pageColumns = Math.Max(1, (int)Math.Ceiling((imageBounds.Width - 0.5) / printable.Width));
            int pageRows = Math.Max(1, (int)Math.Ceiling((imageBounds.Height - 0.5) / printable.Height));

            return new PrintLayout(
                paper,
                printable,
                imageBounds,
                clipped,
                effectiveDpiX,
                effectiveDpiY,
                pageColumns,
                pageRows,
                viewState.Mode);
        }

        /// <summary>
        /// 计算第 index 页（0 起）对应的图像源区域与目标区域（均以纸张坐标系为准）。
        /// 采用“按图像像素行分页、不重叠”的策略，避免相邻页重复内容。
        /// </summary>
        public void GetPageImageRects(
            int pageIndex,
            int totalPages,
            int imagePixelWidth,
            int imagePixelHeight,
            out Int32Rect sourceRect,
            out Rect destinationRect)
        {
            int columns = Math.Max(1, PageColumns);
            int rows = Math.Max(1, PageRows);

            int index = pageIndex < 0 ? 0 : (pageIndex >= totalPages ? totalPages - 1 : pageIndex);
            int column = index % columns;
            int row = index / columns;

            // 该页在图像坐标系中的矩形（DIP 相对图像左上角）
            double pageLeft = column * PrintableArea.Width;
            double pageTop = row * PrintableArea.Height;

            double visibleWidth = Math.Min(PrintableArea.Width, Math.Max(0.0, ImageBounds.Width - pageLeft));
            double visibleHeight = Math.Min(PrintableArea.Height, Math.Max(0.0, ImageBounds.Height - pageTop));

            if (visibleWidth <= 0.0 || visibleHeight <= 0.0)
            {
                sourceRect = new Int32Rect(0, 0, 0, 0);
                destinationRect = Rect.Empty;
                return;
            }

            // DIP → 图像像素
            double pixelsPerDipX = imagePixelWidth / Math.Max(0.001, ImageBounds.Width);
            double pixelsPerDipY = imagePixelHeight / Math.Max(0.001, ImageBounds.Height);

            // 分页边界必须落在整数像素上：第 column 页的起点取整，终点用**下一页起点**而非
            // ceil(宽度)，否则相邻页会重叠 1 个像素（整页放大时表现为一条重复的细缝）。
            int sourceX = (int)Math.Round(pageLeft * pixelsPerDipX);
            int sourceY = (int)Math.Round(pageTop * pixelsPerDipY);
            int sourceXEnd = (int)Math.Round((pageLeft + visibleWidth) * pixelsPerDipX);
            int sourceYEnd = (int)Math.Round((pageTop + visibleHeight) * pixelsPerDipY);

            int sourceWidth = sourceXEnd - sourceX;
            int sourceHeight = sourceYEnd - sourceY;

            // 收敛到图像范围内
            if (sourceX < 0)
            {
                sourceX = 0;
            }

            if (sourceY < 0)
            {
                sourceY = 0;
            }

            if (sourceX >= imagePixelWidth)
            {
                sourceX = imagePixelWidth - 1;
            }

            if (sourceY >= imagePixelHeight)
            {
                sourceY = imagePixelHeight - 1;
            }

            if (sourceX + sourceWidth > imagePixelWidth)
            {
                sourceWidth = imagePixelWidth - sourceX;
            }

            if (sourceY + sourceHeight > imagePixelHeight)
            {
                sourceHeight = imagePixelHeight - sourceY;
            }

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                sourceRect = new Int32Rect(0, 0, 0, 0);
                destinationRect = Rect.Empty;
                return;
            }

            sourceRect = new Int32Rect(sourceX, sourceY, sourceWidth, sourceHeight);

            // 目标位置（纸张坐标系）：
            //   图像上被打印的那一块 = ImageBounds.Left + pageLeft
            //   页网格原点在可打印区域左上角，第 column 页覆盖
            //   [PrintableArea.Left + column*PrintableArea.Width, +Width]
            // 两者相减即得该块在本页中的落点。注意不要重复叠加 PrintableArea.Left。
            double destinationX = ImageBounds.Left + pageLeft - column * PrintableArea.Width;
            double destinationY = ImageBounds.Top + pageTop - row * PrintableArea.Height;

            destinationRect = new Rect(
                destinationX,
                destinationY,
                visibleWidth,
                visibleHeight);
        }

        /// <summary>
        /// 限制用户拖动范围：
        ///   * 填充：必须始终盖住可打印区域（否则会露出白边）；
        ///   * 适应 / 原始尺寸：不能拖出可打印区域；
        ///   * 居中：允许自由拖动（留白是可接受的）。
        /// </summary>
        private static double ClampOffset(
            double offset,
            double basePosition,
            double imageSize,
            double areaStart,
            double areaSize,
            PrintLayoutMode mode)
        {
            if (double.IsNaN(offset) || double.IsInfinity(offset))
            {
                return 0.0;
            }

            if (mode == PrintLayoutMode.Center)
            {
                return offset;
            }

            double minimum;
            double maximum;

            if (mode == PrintLayoutMode.Fill)
            {
                // 图像比区域大：允许在 [areaStart + areaSize - imageSize, areaStart] 之间移动
                minimum = areaStart + areaSize - imageSize;
                maximum = areaStart;
            }
            else
            {
                // 图像 <= 区域：必须完全落在区域内
                minimum = areaStart;
                maximum = areaStart + areaSize - imageSize;
            }

            if (minimum > maximum)
            {
                // 图像比区域还大（原始尺寸模式下可能发生）：居中固定
                double fixedOffset = (areaStart + areaSize / 2.0) - (basePosition + imageSize / 2.0);
                return fixedOffset;
            }

            double position = basePosition + offset;

            if (position < minimum)
            {
                return minimum - basePosition;
            }

            if (position > maximum)
            {
                return maximum - basePosition;
            }

            return offset;
        }
    }
}
