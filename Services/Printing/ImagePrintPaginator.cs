using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PSText.Services.Printing
{
    /// <summary>
    /// 把一张图片分页打印的 <see cref="DocumentPaginator"/> 实现（需求 P2-10 / P2-11）。
    ///
    /// 关键设计：
    ///   * 页面尺寸用 DIP（1/96 英寸）。WPF 打印管线会把 DIP 映射到打印机的实际分辨率，
    ///     因此这里**不需要**也不应该手动换算成打印机像素，否则会二次缩放导致模糊。
    ///   * 图像的物理尺寸由**图像自身 DPI** 决定（<see cref="PrintUnits.PixelsToDips"/>），
    ///     而不是屏幕 DPI，因此"打印输出使用图片原始 DPI"这一要求自然成立。
    ///   * 图像超出可打印区域时按像素行分页，且相邻页不重叠；
    ///     必要时把 <see cref="BitmapSource"/> 按源区域裁剪成小位图，避免每页都整图解码。
    /// </summary>
    public sealed class ImagePrintPaginator : DocumentPaginator
    {
        private readonly BitmapSource _image;
        private readonly PrintLayout _layout;
        private readonly int _totalPages;
        private readonly Dictionary<int, BitmapSource> _pageImageCache = new Dictionary<int, BitmapSource>();
        private Size _pageSize;

        /// <summary>
        /// 构造分页器。
        /// </summary>
        /// <param name="image">要打印的位图（应为已 Freeze 的位图）。</param>
        /// <param name="layout">版面计算结果。</param>
        /// <param name="pageSizeDips">单页尺寸（DIP，通常等于纸张尺寸）。</param>
        public ImagePrintPaginator(BitmapSource image, PrintLayout layout, Size pageSizeDips)
        {
            if (image == null)
            {
                throw new ArgumentNullException("image");
            }

            if (layout == null)
            {
                throw new ArgumentNullException("layout");
            }

            _image = image;
            _layout = layout;
            _pageSize = pageSizeDips;
            _totalPages = layout.PageCount;
        }

        /// <summary>版面信息（供预览与打印共用）。</summary>
        public PrintLayout Layout
        {
            get { return _layout; }
        }

        public override bool IsPageCountValid
        {
            get { return true; }
        }

        public override int PageCount
        {
            get { return _totalPages; }
        }

        public override Size PageSize
        {
            get { return _pageSize; }
            set { _pageSize = value; }
        }

        public override IDocumentPaginatorSource Source
        {
            get { return null; }
        }

        /// <summary>
        /// 生成第 pageNumber 页（0 起）。
        /// </summary>
        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= _totalPages)
            {
                return DocumentPage.Missing;
            }

            DrawingVisual visual = new DrawingVisual();

            using (DrawingContext context = visual.RenderOpen())
            {
                // 纸张底色：白色，避免预览/打印出现透明背景
                context.DrawRectangle(Brushes.White, null, new Rect(new Point(0, 0), _pageSize));

                Int32Rect sourceRect;
                Rect destinationRect;
                _layout.GetPageImageRects(
                    pageNumber,
                    _totalPages,
                    _image.PixelWidth,
                    _image.PixelHeight,
                    out sourceRect,
                    out destinationRect);

                if (destinationRect.IsEmpty || sourceRect.Width <= 0 || sourceRect.Height <= 0)
                {
                    return new DocumentPage(visual, _pageSize, new Rect(_pageSize), Rect.Empty);
                }

                BitmapSource pageImage = GetPageImage(pageNumber, sourceRect);

                // 让 WPF 在缩放时使用高质量插值（打印小图放大时更平滑）
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                context.DrawImage(pageImage, destinationRect);
            }

            // 注意：bleed / content box 都按整页给出；真实内容区域由调用方通过
            // PrintLayout.PrintableArea 表达，这里不裁剪，避免打印机驱动二次缩放。
            return new DocumentPage(visual, _pageSize, new Rect(_pageSize), Rect.Empty);
        }

        /// <summary>
        /// 取该页使用的位图。
        /// 若整图就是一页，直接复用原图；否则按源区域裁出小位图（并缓存），
        /// 这样多页打印时每页只处理自己需要的那部分像素。
        /// </summary>
        private BitmapSource GetPageImage(int pageNumber, Int32Rect sourceRect)
        {
            if (_totalPages == 1
                && sourceRect.X == 0
                && sourceRect.Y == 0
                && sourceRect.Width == _image.PixelWidth
                && sourceRect.Height == _image.PixelHeight)
            {
                return _image;
            }

            BitmapSource cached;

            if (_pageImageCache.TryGetValue(pageNumber, out cached))
            {
                return cached;
            }

            CroppedBitmap cropped = new CroppedBitmap(_image, sourceRect);

            if (cropped.CanFreeze)
            {
                cropped.Freeze();
            }

            _pageImageCache[pageNumber] = cropped;
            return cropped;
        }
    }
}
