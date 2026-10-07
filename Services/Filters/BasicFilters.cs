using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 基础点运算滤镜：反色、灰度、二值化（需求 P1-5）。
    ///
    /// 全部为纯函数：输入 IReadOnlyPixelBuffer，输出新的 PixelBuffer，
    /// 不修改输入、不接触 UI、可安全在后台线程或并行执行。
    /// </summary>
    public sealed class BasicFilters
    {
        // Rec.601 亮度权重（与 AdjustmentsFilter 保持一致，便于肉眼预期一致）
        private const int LumaRed = 77;    // 0.299 * 256
        private const int LumaGreen = 150; // 0.587 * 256
        private const int LumaBlue = 29;   // 0.114 * 256

        /// <summary>反色（负片）。Alpha 保持不变。</summary>
        public Task<PixelBuffer> InvertAsync(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Invert(source, cancellationToken), cancellationToken);
        }

        /// <summary>反色（同步纯函数）。</summary>
        public PixelBuffer Invert(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ApplyPerPixel(
                source,
                (r, g, b) => new Rgb(255 - r, 255 - g, 255 - b),
                cancellationToken,
                null);
        }

        /// <summary>灰度（按 Rec.601 加权亮度）。</summary>
        public Task<PixelBuffer> GrayscaleAsync(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Grayscale(source, cancellationToken), cancellationToken);
        }

        /// <summary>灰度（同步纯函数）。</summary>
        public PixelBuffer Grayscale(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ApplyPerPixel(
                source,
                (r, g, b) =>
                {
                    int luma = (LumaRed * r + LumaGreen * g + LumaBlue * b + 128) >> 8;
                    return new Rgb(luma, luma, luma);
                },
                cancellationToken,
                null);
        }

        /// <summary>
        /// 二值化：亮度高于阈值的像素变白，否则变黑。
        /// </summary>
        /// <param name="source">源缓冲。</param>
        /// <param name="threshold">阈值 0~255。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> ThresholdAsync(
            IReadOnlyPixelBuffer source,
            int threshold,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Threshold(source, threshold, cancellationToken), cancellationToken);
        }

        /// <summary>二值化（同步纯函数）。</summary>
        public PixelBuffer Threshold(
            IReadOnlyPixelBuffer source,
            int threshold,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int level = threshold < 0 ? 0 : (threshold > 255 ? 255 : threshold);

            return ApplyPerPixel(
                source,
                (r, g, b) =>
                {
                    int luma = (LumaRed * r + LumaGreen * g + LumaBlue * b + 128) >> 8;
                    int value = luma >= level ? 255 : 0;
                    return new Rgb(value, value, value);
                },
                cancellationToken,
                null);
        }

        /// <summary>
        /// 逐像素处理的公共实现（并行 + 保持 Alpha）。
        /// </summary>
        /// <param name="transform">
        /// 逐像素变换：参数顺序为 (红, 绿, 蓝)，返回新的 (红, 绿, 蓝)。
        /// </param>
        private static PixelBuffer ApplyPerPixel(
            IReadOnlyPixelBuffer source,
            Func<int, int, int, Rgb> transform,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            if (transform == null)
            {
                throw new ArgumentNullException("transform");
            }

            byte[] output = PixelParallel.AllocateOutput(source);
            int width = source.Width;

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = x * 4;

                        // 明确按 (R, G, B) 顺序传入：缓冲区是 BGRA，
                        // 若这里传成 (B, G, R)，自定义运算会把红蓝通道搞反（曾踩此坑）。
                        Rgb result = transform(
                            rowBuffer[index + 2], // R
                            rowBuffer[index + 1], // G
                            rowBuffer[index]);    // B

                        output[outputOffset + index] = (byte)PixelParallel.ClampToByte(result.Blue);
                        output[outputOffset + index + 1] = (byte)PixelParallel.ClampToByte(result.Green);
                        output[outputOffset + index + 2] = (byte)PixelParallel.ClampToByte(result.Red);
                        output[outputOffset + index + 3] = rowBuffer[index + 3];
                    }
                },
                true,
                cancellationToken,
                progress);

            return new PixelBuffer(output, source.Width, source.Height);
        }

        /// <summary>颜色三元组（避免每像素分配对象而改用值类型）。</summary>
        private struct Rgb
        {
            public Rgb(int red, int green, int blue)
            {
                Red = red;
                Green = green;
                Blue = blue;
            }

            public int Red;

            public int Green;

            public int Blue;
        }
    }
}
