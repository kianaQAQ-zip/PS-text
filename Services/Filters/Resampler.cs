using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>重采样核（对照 Photoshop「图像大小」里的三种插值方式）。</summary>
    public enum ResampleKernel
    {
        /// <summary>三次立方（Catmull-Rom）：默认，兼顾锐度与平滑，缩放照片首选。</summary>
        Bicubic = 0,

        /// <summary>双线性（三角核）：更快，边缘略软。</summary>
        Bilinear = 1,

        /// <summary>邻近取样：不做混合，保留硬边（放大像素画 / 图标时用）。</summary>
        NearestNeighbor = 2
    }

    /// <summary>
    /// 可分离重采样引擎（纯函数，行级并行）。
    ///
    /// 为什么不用 WPF 的 <c>TransformedBitmap</c>：它只暴露 <c>BitmapScalingMode</c> 的四档档位，
    /// 既不能选核，也不会在缩小时把核按比例展宽 —— 而"缩小时展宽"正是避免摩尔纹的关键
    /// （否则每 5 个像素里只读 1 个，细密纹理会被采样成错误的低频条纹）。
    /// 这里按「核 + 缩小时展宽」实现，与 Photoshop / PIL 的做法一致。
    ///
    /// 两个容易踩的坑，本实现都处理了：
    ///   1. **先预乘 alpha 再重采样，最后还原**。透明像素的 RGB 通常是 0，
    ///      若直接对直通 alpha 做加权平均，半透明边缘会被那些"看不见的黑"拉暗，出现黑边。
    ///   2. **缩小时的核展宽要作用在源坐标上**，即权重按 <c>(j - center) * scale</c> 计算，
    ///      而不是只扩大取样窗口。
    /// </summary>
    public static class Resampler
    {
        /// <summary>单边最大像素数。</summary>
        public const int MaxDimension = 20000;

        /// <summary>总像素上限（4000 万像素 ≈ 160 MB 单缓冲，超出直接拒绝而不是崩溃）。</summary>
        public const long MaxPixels = 40000000L;

        /// <summary>缩放尺寸是否在可处理范围内。</summary>
        public static bool IsValidSize(int width, int height)
        {
            return width >= 1
                   && height >= 1
                   && width <= MaxDimension
                   && height <= MaxDimension
                   && (long)width * height <= MaxPixels;
        }

        /// <summary>把图像缩放到指定像素尺寸（同步纯函数）。</summary>
        public static PixelBuffer Resize(
            IReadOnlyPixelBuffer source,
            int targetWidth,
            int targetHeight,
            ResampleKernel kernel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (!IsValidSize(targetWidth, targetHeight))
            {
                throw new ArgumentOutOfRangeException(
                    "targetWidth",
                    string.Format("目标尺寸不可用：{0} × {1}（单边上限 {2}，总像素上限 {3}）。",
                        targetWidth, targetHeight, MaxDimension, MaxPixels));
            }

            if (targetWidth == source.Width && targetHeight == source.Height)
            {
                // 尺寸未变：直接复制。保证"同尺寸缩放"是逐像素恒等的，不引入任何插值误差。
                return Copy(source, cancellationToken);
            }

            if (kernel == ResampleKernel.NearestNeighbor)
            {
                // 邻近取样走直通路径：既不预乘也不插值，保证整数倍放大是严格的像素复制。
                return ResizeNearest(source, targetWidth, targetHeight, cancellationToken);
            }

            // 可分离两遍：先水平（源宽 → 目标宽），再垂直（源高 → 目标高）。
            // 结果质量与一次性二维卷积等价，代价从 O(n²) 降到 O(2n)。
            PixelBuffer horizontal = ResizeHorizontal(source, targetWidth, kernel, cancellationToken);
            return ResizeVertical(horizontal, targetHeight, kernel, cancellationToken);
        }

        /// <summary>异步缩放（在后台线程调用同步版本）。</summary>
        public static Task<PixelBuffer> ResizeAsync(
            IReadOnlyPixelBuffer source,
            int targetWidth,
            int targetHeight,
            ResampleKernel kernel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(
                () => Resize(source, targetWidth, targetHeight, kernel, cancellationToken),
                cancellationToken);
        }

        #region 水平 / 垂直两遍

        /// <summary>
        /// 水平方向重采样：源（sw × h）→ 中间（dw × h）。
        /// 输出的 RGB 是**已预乘 alpha** 的值，供垂直方向继续处理。
        /// </summary>
        private static PixelBuffer ResizeHorizontal(
            IReadOnlyPixelBuffer source,
            int targetWidth,
            ResampleKernel kernel,
            CancellationToken cancellationToken)
        {
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            int sourceStride = source.Stride;
            int targetStride = targetWidth * 4;

            byte[] output = new byte[targetStride * sourceHeight];
            WeightTable table = BuildWeights(sourceWidth, targetWidth, kernel, cancellationToken);

            Parallel.For(
                0,
                sourceHeight,
                new ParallelOptions { CancellationToken = cancellationToken },
                () => new byte[sourceStride],
                (y, loopState, rowBuffer) =>
                {
                    source.CopyRow(y, rowBuffer, 0, sourceStride);
                    int rowOffset = y * targetStride;

                    for (int x = 0; x < targetWidth; x++)
                    {
                        int start = table.Starts[x];
                        int count = table.Counts[x];

                        double sumB = 0.0;
                        double sumG = 0.0;
                        double sumR = 0.0;
                        double sumA = 0.0;

                        for (int t = 0; t < count; t++)
                        {
                            double weight = table.Weights[start + t];
                            int index = table.Indices[start + t] * 4;
                            int alpha = rowBuffer[index + 3];

                            // 预乘：颜色值先乘 alpha 再参与加权，避免透明区的"黑"渗进边缘
                            sumB += weight * (rowBuffer[index] * alpha / 255.0);
                            sumG += weight * (rowBuffer[index + 1] * alpha / 255.0);
                            sumR += weight * (rowBuffer[index + 2] * alpha / 255.0);
                            sumA += weight * alpha;
                        }

                        int targetIndex = rowOffset + x * 4;
                        output[targetIndex] = ToByte(sumB);
                        output[targetIndex + 1] = ToByte(sumG);
                        output[targetIndex + 2] = ToByte(sumR);
                        output[targetIndex + 3] = ToByte(sumA);
                    }

                    return rowBuffer;
                },
                rowBuffer => { });

            return new PixelBuffer(output, targetWidth, sourceHeight);
        }

        /// <summary>
        /// 垂直方向重采样：中间（w × sh）→ 目标（w × dh）。
        /// 输入是预乘值，输出会**还原为直通 alpha**。
        /// </summary>
        private static PixelBuffer ResizeVertical(
            PixelBuffer source,
            int targetHeight,
            ResampleKernel kernel,
            CancellationToken cancellationToken)
        {
            int width = source.Width;
            int sourceHeight = source.Height;
            int stride = source.Stride;

            byte[] sourcePixels = source.GetPixels();
            byte[] output = new byte[stride * targetHeight];
            WeightTable table = BuildWeights(sourceHeight, targetHeight, kernel, cancellationToken);

            Parallel.For(
                0,
                targetHeight,
                new ParallelOptions { CancellationToken = cancellationToken },
                y =>
                {
                    int targetOffset = y * stride;
                    int start = table.Starts[y];
                    int count = table.Counts[y];

                    for (int x = 0; x < width; x++)
                    {
                        int column = x * 4;

                        double sumB = 0.0;
                        double sumG = 0.0;
                        double sumR = 0.0;
                        double sumA = 0.0;

                        for (int t = 0; t < count; t++)
                        {
                            double weight = table.Weights[start + t];
                            int index = table.Indices[start + t] * stride + column;

                            sumB += weight * sourcePixels[index];
                            sumG += weight * sourcePixels[index + 1];
                            sumR += weight * sourcePixels[index + 2];
                            sumA += weight * sourcePixels[index + 3];
                        }

                        int targetIndex = targetOffset + column;
                        int alpha = ToByte(sumA);

                        if (alpha <= 0)
                        {
                            output[targetIndex] = 0;
                            output[targetIndex + 1] = 0;
                            output[targetIndex + 2] = 0;
                            output[targetIndex + 3] = 0;
                            continue;
                        }

                        // 还原：预乘值 ÷ alpha
                        output[targetIndex] = Unpremultiply(sumB, alpha);
                        output[targetIndex + 1] = Unpremultiply(sumG, alpha);
                        output[targetIndex + 2] = Unpremultiply(sumR, alpha);
                        output[targetIndex + 3] = (byte)alpha;
                    }
                });

            return new PixelBuffer(output, width, targetHeight);
        }

        /// <summary>邻近取样（两遍合并为一次直接映射）。</summary>
        private static PixelBuffer ResizeNearest(
            IReadOnlyPixelBuffer source,
            int targetWidth,
            int targetHeight,
            CancellationToken cancellationToken)
        {
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            int sourceStride = source.Stride;
            int targetStride = targetWidth * 4;

            byte[] output = new byte[targetStride * targetHeight];

            // 源 / 目标比值决定每个目标像素取哪一源像素（取目标像素中心对应的源像素）。
            double ratioX = (double)sourceWidth / targetWidth;
            double ratioY = (double)sourceHeight / targetHeight;

            int[] sourceColumns = new int[targetWidth];

            for (int x = 0; x < targetWidth; x++)
            {
                int column = (int)((x + 0.5) * ratioX);
                sourceColumns[x] = column < 0 ? 0 : (column >= sourceWidth ? sourceWidth - 1 : column);
            }

            Parallel.For(
                0,
                targetHeight,
                new ParallelOptions { CancellationToken = cancellationToken },
                () => new byte[sourceStride],
                (y, loopState, rowBuffer) =>
                {
                    int sourceRow = (int)((y + 0.5) * ratioY);

                    if (sourceRow < 0)
                    {
                        sourceRow = 0;
                    }
                    else if (sourceRow >= sourceHeight)
                    {
                        sourceRow = sourceHeight - 1;
                    }

                    source.CopyRow(sourceRow, rowBuffer, 0, sourceStride);
                    int rowOffset = y * targetStride;

                    for (int x = 0; x < targetWidth; x++)
                    {
                        int index = sourceColumns[x] * 4;
                        int targetIndex = rowOffset + x * 4;

                        output[targetIndex] = rowBuffer[index];
                        output[targetIndex + 1] = rowBuffer[index + 1];
                        output[targetIndex + 2] = rowBuffer[index + 2];
                        output[targetIndex + 3] = rowBuffer[index + 3];
                    }

                    return rowBuffer;
                },
                rowBuffer => { });

            return new PixelBuffer(output, targetWidth, targetHeight);
        }

        #endregion

        #region 权重表

        /// <summary>扁平化的采样权重表：weights 由各输出位置的 count 个权重顺序拼接。</summary>
        private sealed class WeightTable
        {
            public double[] Weights;
            public int[] Indices;
            public int[] Starts;
            public int[] Counts;
        }

        /// <summary>
        /// 为「源长度 → 目标长度」构建采样权重。
        ///
        /// 权重公式（PIL / ImageMagick 同款）：
        ///   center_i  = (i + 0.5) / scale - 0.5          —— 目标像素中心对应的源坐标
        ///   filterScale = min(1, scale)                  —— 缩小时把核按比例展宽
        ///   w(j) = kernel((j - center_i) * filterScale)
        /// 展宽之后，缩小 5 倍时每个目标像素会对约 2×5 = 10 个源像素加权求和，
        /// 等效于一个抗混叠的低通，这就是"缩小时不糊不闪"的原因。
        /// </summary>
        private static WeightTable BuildWeights(
            int sourceLength,
            int targetLength,
            ResampleKernel kernel,
            CancellationToken cancellationToken)
        {
            double scale = (double)targetLength / sourceLength;
            double inverseScale = 1.0 / scale;
            double filterScale = scale < 1.0 ? scale : 1.0;
            double support = (kernel == ResampleKernel.Bicubic ? 2.0 : 1.0) / filterScale;

            int[] starts = new int[targetLength];
            int[] counts = new int[targetLength];
            int[] indices = new int[targetLength * 8];
            double[] weights = new double[targetLength * 8];

            int cursor = 0;

            for (int i = 0; i < targetLength; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double center = (i + 0.5) * inverseScale - 0.5;
                int left = (int)Math.Ceiling(center - support);
                int right = (int)Math.Floor(center + support);

                // 窗口会随缩小倍数变大，按"当前游标 + 本窗口"扩容（显式判断，不依赖隐式假设）。
                int needed = cursor + (right - left + 1) * 2;

                if (needed > indices.Length)
                {
                    Array.Resize(ref indices, needed);
                    Array.Resize(ref weights, needed);
                }

                starts[i] = cursor;
                double sum = 0.0;
                int count = 0;

                for (int j = left; j <= right; j++)
                {
                    double weight = Evaluate(kernel, (j - center) * filterScale);

                    if (weight == 0.0)
                    {
                        continue;
                    }

                    // 越界的取样点按"边缘延伸"处理：把权重并到最近的边界像素上。
                    // 若反过来丢弃这些权重，边缘会因为权重和不足 1 而变暗。
                    int index = j < 0 ? 0 : (j >= sourceLength ? sourceLength - 1 : j);

                    indices[cursor] = index;
                    weights[cursor] = weight;
                    cursor++;
                    count++;
                    sum += weight;
                }

                if (count == 0 || sum == 0.0)
                {
                    // 理论上不会发生；兜底成最近邻，绝不产生空权重。
                    int index = (int)Math.Round(center);
                    index = index < 0 ? 0 : (index >= sourceLength ? sourceLength - 1 : index);
                    indices[cursor] = index;
                    weights[cursor] = 1.0;
                    cursor++;
                    count = 1;
                    sum = 1.0;
                }

                // 归一化：保证权重和为 1，纯色区域缩放后颜色不变。
                for (int t = 0; t < count; t++)
                {
                    weights[starts[i] + t] /= sum;
                }

                counts[i] = count;
            }

            WeightTable table = new WeightTable();
            table.Weights = weights;
            table.Indices = indices;
            table.Starts = starts;
            table.Counts = counts;
            return table;
        }

        /// <summary>核函数求值。</summary>
        private static double Evaluate(ResampleKernel kernel, double t)
        {
            double x = Math.Abs(t);

            if (kernel == ResampleKernel.Bicubic)
            {
                // Catmull-Rom（a = -0.5）
                if (x < 1.0)
                {
                    return 1.5 * x * x * x - 2.5 * x * x + 1.0;
                }

                if (x < 2.0)
                {
                    return -0.5 * x * x * x + 2.5 * x * x - 4.0 * x + 2.0;
                }

                return 0.0;
            }

            // 三角核（双线性）
            return x < 1.0 ? 1.0 - x : 0.0;
        }

        #endregion

        #region 字节工具

        private static byte ToByte(double value)
        {
            int rounded = (int)Math.Round(value);

            if (rounded <= 0)
            {
                return 0;
            }

            return rounded >= 255 ? (byte)255 : (byte)rounded;
        }

        private static byte Unpremultiply(double premultiplied, int alpha)
        {
            double value = premultiplied * 255.0 / alpha;
            return ToByte(value);
        }

        private static PixelBuffer Copy(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int stride = source.Stride;
            byte[] output = new byte[stride * source.Height];

            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(y, output, y * stride, stride);
            }

            return new PixelBuffer(output, source.Width, source.Height);
        }

        #endregion
    }
}
