using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PSText.Infrastructure.Imaging
{
    /// <summary>
    /// 只读像素缓冲接口。
    /// 滤镜算法只依赖这个接口，因此可以在“降采样预览缓冲”和“全分辨率缓冲”上
    /// 复用完全相同的算法（需求 P1-4：拖动时降采样预览，松开后全分辨率应用）。
    /// </summary>
    public interface IReadOnlyPixelBuffer
    {
        /// <summary>像素宽度。</summary>
        int Width { get; }

        /// <summary>像素高度。</summary>
        int Height { get; }

        /// <summary>每行的字节数（Bgra32 下 = Width * 4）。</summary>
        int Stride { get; }

        /// <summary>像素格式，固定为 Bgra32（算法内部按 4 字节/像素处理）。</summary>
        PixelFormat Format { get; }

        /// <summary>
        /// 读取一行像素到目标数组。
        /// </summary>
        /// <param name="sourceY">源行号。</param>
        /// <param name="destination">目标数组。</param>
        /// <param name="destinationOffset">目标起始下标。</param>
        /// <param name="count">拷贝字节数。</param>
        void CopyRow(int sourceY, byte[] destination, int destinationOffset, int count);

        /// <summary>获取底层像素数组（只读用途，调用方不得修改）。</summary>
        byte[] GetPixels();
    }

    /// <summary>
    /// Bgra32 像素缓冲。
    ///
    /// 这是步骤 2 的核心数据结构：把“解码后的像素”与“位图对象”分离，
    /// 使得滤镜可以在纯字节数组上运行（纯函数、无副作用、可后台线程），
    /// 并且同一份数据可以按需生成降采样预览（用于滑块实时预览）。
    /// </summary>
    public sealed class PixelBuffer : IReadOnlyPixelBuffer
    {
        private const int BytesPerPixel = 4;

        private readonly byte[] _pixels;
        private readonly object _previewLock = new object();
        private PixelBuffer _preview;

        public PixelBuffer(byte[] pixels, int width, int height)
        {
            if (pixels == null)
            {
                throw new ArgumentNullException("pixels");
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "像素尺寸必须为正数。");
            }

            int stride = width * BytesPerPixel;
            long required = (long)stride * height;

            if (pixels.LongLength < required)
            {
                throw new ArgumentException(
                    string.Format("像素数组长度不足：需要 {0} 字节，实际 {1} 字节。", required, pixels.LongLength),
                    "pixels");
            }

            _pixels = pixels;
            Width = width;
            Height = height;
            Stride = stride;
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int Stride { get; private set; }

        public PixelFormat Format
        {
            get { return PixelFormats.Bgra32; }
        }

        /// <summary>像素数据占用的字节数。</summary>
        public long ByteCount
        {
            get { return (long)Stride * Height; }
        }

        public void CopyRow(int sourceY, byte[] destination, int destinationOffset, int count)
        {
            if (destination == null)
            {
                throw new ArgumentNullException("destination");
            }

            if (sourceY < 0 || sourceY >= Height)
            {
                throw new ArgumentOutOfRangeException("sourceY");
            }

            if (count < 0 || count > Stride)
            {
                throw new ArgumentOutOfRangeException("count");
            }

            Buffer.BlockCopy(_pixels, sourceY * Stride, destination, destinationOffset, count);
        }

        public byte[] GetPixels()
        {
            return _pixels;
        }

        /// <summary>返回像素数据的独立副本（用于保存历史等不可变场景）。</summary>
        public byte[] GetPixelsCopy()
        {
            byte[] copy = new byte[_pixels.Length];
            Buffer.BlockCopy(_pixels, 0, copy, 0, _pixels.Length);
            return copy;
        }

        /// <summary>
        /// 获取降采样预览缓冲（惰性创建并缓存）。
        /// 算法：盒式平均（box filter），按整数因子抽样，避免引入 aliasing 导致的色彩跳变。
        /// English: Lazily builds and caches a downsampled copy used for live slider previews.
        /// </summary>
        /// <param name="maxWidth">预览的最大宽度。</param>
        /// <param name="maxHeight">预览的最大高度。</param>
        public PixelBuffer GetPreview(int maxWidth, int maxHeight)
        {
            if (maxWidth <= 0)
            {
                maxWidth = 1280;
            }

            if (maxHeight <= 0)
            {
                maxHeight = 1280;
            }

            // 原图本来就小：直接用原图，避免无意义的缩放。
            if (Width <= maxWidth && Height <= maxHeight)
            {
                return this;
            }

            lock (_previewLock)
            {
                if (_preview == null)
                {
                    _preview = CreatePreview(maxWidth, maxHeight);
                }

                return _preview;
            }
        }

        private PixelBuffer CreatePreview(int maxWidth, int maxHeight)
        {
            // 整数倍抽样：factor 取 1、2、3……保证每个输出像素由固定的 factor×factor 块平均而来。
            int factor = Math.Max(
                1,
                Math.Min(
                    (int)Math.Ceiling((double)Width / maxWidth),
                    (int)Math.Ceiling((double)Height / maxHeight)));

            if (factor <= 1)
            {
                return this;
            }

            int previewWidth = Math.Max(1, Width / factor);
            int previewHeight = Math.Max(1, Height / factor);
            byte[] output = new byte[previewWidth * previewHeight * BytesPerPixel];

            byte[] source = _pixels;
            int sourceStride = Stride;
            int blockArea = factor * factor;

            for (int y = 0; y < previewHeight; y++)
            {
                int sourceYStart = y * factor;
                int destinationRow = y * previewWidth * BytesPerPixel;

                for (int x = 0; x < previewWidth; x++)
                {
                    int sourceXStart = x * factor;

                    int sumB = 0;
                    int sumG = 0;
                    int sumR = 0;
                    int sumA = 0;

                    for (int blockY = 0; blockY < factor; blockY++)
                    {
                        int rowOffset = (sourceYStart + blockY) * sourceStride;
                        int columnOffset = rowOffset + sourceXStart * BytesPerPixel;

                        for (int blockX = 0; blockX < factor; blockX++)
                        {
                            int index = columnOffset + blockX * BytesPerPixel;
                            sumB += source[index];
                            sumG += source[index + 1];
                            sumR += source[index + 2];
                            sumA += source[index + 3];
                        }
                    }

                    int destinationIndex = destinationRow + x * BytesPerPixel;
                    output[destinationIndex] = (byte)(sumB / blockArea);
                    output[destinationIndex + 1] = (byte)(sumG / blockArea);
                    output[destinationIndex + 2] = (byte)(sumR / blockArea);
                    output[destinationIndex + 3] = (byte)(sumA / blockArea);
                }
            }

            return new PixelBuffer(output, previewWidth, previewHeight);
        }

        /// <summary>
        /// 从 BitmapSource 读取像素（Bgra32）。
        /// 对于 Bgra32 / Pbgra32 源位图用一次性整块拷贝；其余格式先统一转换再拷贝，
        /// 避免出现 Windows 7 上常见的跨行 stride 错位问题。
        /// </summary>
        public static PixelBuffer FromBitmap(BitmapSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = width * BytesPerPixel;
            byte[] pixels = new byte[stride * height];

            BitmapSource readable = source;
            bool converted = false;

            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
            {
                FormatConvertedBitmap convertedBitmap = new FormatConvertedBitmap(
                    source,
                    PixelFormats.Bgra32,
                    null,
                    0.0);
                convertedBitmap.Freeze();
                readable = convertedBitmap;
                converted = true;
            }

            try
            {
                // 注意：不能用“整块 CopyPixels”，因为 WPF 的复制会按源位图自己的 stride 排布，
                // 当 stride != Width*4 时后面的行会整体错位。这里逐行拷贝，绝对安全。
                for (int y = 0; y < height; y++)
                {
                    readable.CopyPixels(new System.Windows.Int32Rect(0, y, width, 1), pixels, stride, y * stride);
                }
            }
            catch (Exception) when (converted)
            {
                // 转换位图读取失败时回退到原始位图。
                for (int y = 0; y < height; y++)
                {
                    source.CopyPixels(new System.Windows.Int32Rect(0, y, width, 1), pixels, stride, y * stride);
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>
        /// 用像素数据创建 BitmapSource（可跨线程，已 Freeze）。
        /// </summary>
        public static BitmapSource ToBitmap(PixelBuffer buffer, double dpiX, double dpiY)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (dpiX <= 0.5 || double.IsNaN(dpiX))
            {
                dpiX = 96.0;
            }

            if (dpiY <= 0.5 || double.IsNaN(dpiY))
            {
                dpiY = 96.0;
            }

            BitmapSource bitmap = BitmapSource.Create(
                buffer.Width,
                buffer.Height,
                dpiX,
                dpiY,
                PixelFormats.Bgra32,
                null,
                buffer.GetPixels(),
                buffer.Stride);

            bitmap.Freeze();
            return bitmap;
        }
    }
}
