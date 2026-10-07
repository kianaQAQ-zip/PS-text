using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 模糊与锐化滤镜（需求 P1-6）：高斯模糊（可调半径）、USM 锐化。
    ///
    /// 算法说明（English summary）：
    ///   * A true 2D Gaussian is expensive; instead the blur is separable and
    ///     implemented as a sequence of sliding-window box-blur passes.
    ///     A sliding-window box blur costs O(1) per pixel **independent of radius**,
    ///     so a 50px blur on a 24MP image is still fast.
    ///   * Three box passes approximate a Gaussian (central limit theorem) with
    ///     sigma ≈ sqrt(n·(w²−1)/12); the widths are derived from the requested sigma
    ///     so the visual result matches an exact Gaussian closely.
    ///   * Edge handling uses mirroring (reflect without repeating the edge pixel),
    ///     which avoids the darkening border that zero-padding produces.
    ///   * Large images are processed in row-parallel fashion with per-thread buffers,
    ///     so the UI thread never blocks.
    /// </summary>
    public sealed class BlurFilters
    {
        /// <summary>默认的盒式模糊迭代次数（3 次已足够接近高斯）。</summary>
        private const int DefaultBoxPasses = 3;

        /// <summary>
        /// 高斯模糊（异步）。半径单位为像素，内部按 sigma = radius / 3 计算
        /// （3σ 覆盖约 99.7% 能量，因此 radius 就是“视觉上模糊的范围”）。
        /// </summary>
        /// <param name="source">源缓冲。</param>
        /// <param name="radius">模糊半径（像素），0 表示不处理。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> GaussianBlurAsync(
            IReadOnlyPixelBuffer source,
            double radius,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => GaussianBlur(source, radius, cancellationToken), cancellationToken);
        }

        /// <summary>高斯模糊（同步纯函数）。</summary>
        public PixelBuffer GaussianBlur(
            IReadOnlyPixelBuffer source,
            double radius,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            double effectiveRadius = radius;

            if (double.IsNaN(effectiveRadius) || effectiveRadius <= 0.0)
            {
                // 半径为 0：返回原图副本，保证调用方拿到独立缓冲。
                return Copy(source, cancellationToken);
            }

            // 半径上限按图像尺寸收敛，避免出现远大于图像本身的核。
            double maxRadius = Math.Max(source.Width, source.Height) / 2.0;
            if (effectiveRadius > maxRadius)
            {
                effectiveRadius = maxRadius;
            }

            double sigma = effectiveRadius / 3.0;
            int[] radii = PlanBoxRadii(sigma, DefaultBoxPasses);

            PixelBuffer current = Copy(source, cancellationToken);

            try
            {
                foreach (int boxRadius in radii)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (boxRadius <= 0)
                    {
                        continue;
                    }

                    PixelBuffer horizontal = BoxBlurHorizontal(current, boxRadius, cancellationToken);
                    PixelBuffer vertical = BoxBlurVertical(horizontal, boxRadius, cancellationToken);

                    current = vertical;
                }

                return current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        /// <summary>
        /// USM 锐化（Unsharp Mask）：原图 + 强度 ×（原图 − 模糊图）。
        /// </summary>
        /// <param name="source">源缓冲。</param>
        /// <param name="radius">模糊半径（决定锐化“颗粒”大小）。</param>
        /// <param name="amount">强度百分比，100 为 1 倍。</param>
        /// <param name="threshold">阈值 0~255：差值小于该值不锐化，避免放大噪点。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> UnsharpMaskAsync(
            IReadOnlyPixelBuffer source,
            double radius,
            double amount,
            int threshold = 0,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(
                () => UnsharpMask(source, radius, amount, threshold, cancellationToken),
                cancellationToken);
        }

        /// <summary>USM 锐化（同步纯函数）。</summary>
        public PixelBuffer UnsharpMask(
            IReadOnlyPixelBuffer source,
            double radius,
            double amount,
            int threshold,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (amount <= 0.0 || double.IsNaN(amount))
            {
                return Copy(source, cancellationToken);
            }

            // 用同一套高斯实现得到“模糊层”。
            PixelBuffer blurred = GaussianBlur(source, radius, cancellationToken);

            byte[] original = PixelParallel.AllocateOutput(source);
            byte[] output = PixelParallel.AllocateOutput(source);
            int width = source.Width;
            int clampedThreshold = threshold < 0 ? 0 : (threshold > 255 ? 255 : threshold);
            int amountFixed = (int)Math.Round(amount * 256.0 / 100.0);

            try
            {
                // 逐行处理：需要同时访问原图行与模糊层行。
                byte[] sourceRow = new byte[source.Stride];
                byte[] blurredRow = new byte[blurred.Stride];
                byte[] outputBuffer = output;
                byte[] originalBuffer = original;

                for (int y = 0; y < source.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    source.CopyRow(y, sourceRow, 0, source.Stride);
                    blurred.CopyRow(y, blurredRow, 0, blurred.Stride);

                    int rowOffset = y * source.Stride;

                    for (int x = 0; x < width; x++)
                    {
                        int index = x * 4;

                        // 保存原图（供返回缓冲复用，避免再次 CopyRow）
                        originalBuffer[rowOffset + index] = sourceRow[index];
                        originalBuffer[rowOffset + index + 1] = sourceRow[index + 1];
                        originalBuffer[rowOffset + index + 2] = sourceRow[index + 2];
                        originalBuffer[rowOffset + index + 3] = sourceRow[index + 3];

                        for (int channel = 0; channel < 3; channel++)
                        {
                            int src = sourceRow[index + channel];
                            int blur = blurredRow[index + channel];
                            int difference = src - blur;

                            if (clampedThreshold > 0 && Math.Abs(difference) < clampedThreshold)
                            {
                                outputBuffer[rowOffset + index + channel] = (byte)src;
                                continue;
                            }

                            // 定点运算：src + (difference * amount)
                            int sharpened = src + (((difference * amountFixed) + 128) >> 8);
                            outputBuffer[rowOffset + index + channel] = (byte)PixelParallel.ClampToByte(sharpened);
                        }

                        outputBuffer[rowOffset + index + 3] = sourceRow[index + 3];
                    }
                }
            }
            finally
            {
                // 中间缓冲及时释放引用，降低大图峰值内存。
                blurred = null;
            }

            return new PixelBuffer(output, source.Width, source.Height);
        }

        #region 盒式模糊（滑动窗口，O(1)/像素）

        /// <summary>
        /// 由目标 sigma 推导 3 次盒式模糊各自的半径。
        /// 依据：n 次宽度 w（半径 r）的盒式模糊，其方差为 n·w·(w+1)/6，令其等于 σ²。
        /// </summary>
        private static int[] PlanBoxRadii(double sigma, int passes)
        {
            if (passes < 1)
            {
                passes = 1;
            }

            // 先求理想盒宽，再尽量平均分配到各次迭代（半径必须为整数）。
            double idealWidth = Math.Sqrt(12.0 * sigma * sigma / passes + 1.0);
            int baseRadius = (int)Math.Floor((idealWidth - 1.0) / 2.0);

            if (baseRadius < 1)
            {
                baseRadius = 1;
            }

            int[] radii = new int[passes];
            for (int i = 0; i < passes; i++)
            {
                radii[i] = baseRadius;
            }

            return radii;
        }

        /// <summary>
        /// 水平方向盒式模糊：对每一行使用滑动窗口累加，
        /// 每个像素只需一次加法和一次减法，**与半径无关**（O(1)/像素）。
        /// </summary>
        private static PixelBuffer BoxBlurHorizontal(
            IReadOnlyPixelBuffer source,
            int radius,
            CancellationToken cancellationToken)
        {
            byte[] output = PixelParallel.AllocateOutput(source);
            int width = source.Width;
            int window = radius * 2 + 1;

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) =>
                {
                    int sumB = 0;
                    int sumG = 0;
                    int sumR = 0;
                    int sumA = 0;

                    // 初始窗口：x ∈ [0, 2r]，左半部分镜像取样
                    for (int k = -radius; k <= radius; k++)
                    {
                        int index = Mirror(k, width) * 4;
                        sumB += rowBuffer[index];
                        sumG += rowBuffer[index + 1];
                        sumR += rowBuffer[index + 2];
                        sumA += rowBuffer[index + 3];
                    }

                    for (int x = 0; x < width; x++)
                    {
                        int index = outputOffset + x * 4;
                        output[index] = (byte)(sumB / window);
                        output[index + 1] = (byte)(sumG / window);
                        output[index + 2] = (byte)(sumR / window);
                        output[index + 3] = (byte)(sumA / window);

                        if (x == width - 1)
                        {
                            break;
                        }

                        // 窗口右移一格：减去离开的列，加入进入的列
                        int leaving = Mirror(x - radius, width) * 4;
                        int entering = Mirror(x + radius + 1, width) * 4;

                        sumB += rowBuffer[entering] - rowBuffer[leaving];
                        sumG += rowBuffer[entering + 1] - rowBuffer[leaving + 1];
                        sumR += rowBuffer[entering + 2] - rowBuffer[leaving + 2];
                        sumA += rowBuffer[entering + 3] - rowBuffer[leaving + 3];
                    }
                },
                true,
                cancellationToken);

            return new PixelBuffer(output, source.Width, source.Height);
        }

        /// <summary>
        /// 垂直方向盒式模糊。
        ///
        /// 采用「按列维护环形缓冲区 + 维护每列像素和各通道和」的方式：
        /// 每个输出行只需减去离开窗口的那一行、加上进入窗口的那一行，
        /// 因此整体复杂度为 O(宽 × 高)，与半径无关。
        /// English: Vertical box blur with per-column running sums; each output row
        /// only subtracts the departing row and adds the entering one → O(w·h) total.
        /// </summary>
        private static PixelBuffer BoxBlurVertical(
            IReadOnlyPixelBuffer source,
            int radius,
            CancellationToken cancellationToken)
        {
            int width = source.Width;
            int height = source.Height;
            int stride = source.Stride;
            int window = radius * 2 + 1;
            int rowInts = width * 4;

            byte[] output = PixelParallel.AllocateOutput(source);
            int[] columnSums = new int[rowInts];

            // 行缓冲缓存：按**镜像后的源行号**缓存，避免反复 CopyRow。
            // 注意窗口位置与镜像行号不是一回事（位置可能为负，例如 y=0、r=2 时位置为 -2..2），
            // 因此窗口里必须同时记录位置和它对应的缓冲，不能只用位置去查缓存。
            Dictionary<int, byte[]> rowCache = new Dictionary<int, byte[]>();
            List<int> windowPositions = new List<int>(window);
            List<byte[]> windowBuffers = new List<byte[]>(window);

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ---- 让窗口恰好覆盖 [y-r, y+r]（镜像取样） ----
                int firstNeeded = y - radius;
                int lastNeeded = y + radius;

                // 移除窗口左侧不再需要的行
                while (windowPositions.Count > 0 && windowPositions[0] < firstNeeded)
                {
                    SubtractRow(windowBuffers[0], columnSums, rowInts);
                    windowPositions.RemoveAt(0);
                    windowBuffers.RemoveAt(0);
                }

                // 加入窗口右侧新进入的行
                int nextRow = windowPositions.Count == 0
                    ? firstNeeded
                    : windowPositions[windowPositions.Count - 1] + 1;

                for (int position = nextRow; position <= lastNeeded; position++)
                {
                    int sourceY = Mirror(position, height);
                    byte[] buffer = GetCachedRow(source, rowCache, sourceY, stride);

                    windowPositions.Add(position);
                    windowBuffers.Add(buffer);
                    AddRow(buffer, columnSums, rowInts);
                }

                // ---- 写出当前行 ----
                int outputOffset = y * stride;

                for (int x = 0; x < rowInts; x++)
                {
                    output[outputOffset + x] = (byte)(columnSums[x] / window);
                }

                // 控制内存峰值：只清理严格低于当前窗口最左位置的缓冲
                if (windowPositions.Count > 0)
                {
                    PruneCache(rowCache, windowPositions[0]);
                }
            }

            return new PixelBuffer(output, width, height);
        }

        private static byte[] GetCachedRow(
            IReadOnlyPixelBuffer source,
            Dictionary<int, byte[]> cache,
            int sourceY,
            int stride)
        {
            byte[] buffer;

            if (!cache.TryGetValue(sourceY, out buffer))
            {
                buffer = new byte[stride];
                source.CopyRow(sourceY, buffer, 0, stride);
                cache[sourceY] = buffer;
            }

            return buffer;
        }

        /// <summary>
        /// 丢弃窗口不再需要的行缓冲。
        /// 注意：只能清理“严格小于当前窗口最左行”的项——
        /// 镜像取样会让较小的源行在后续行（越界一侧）继续被用到，清理过早会取不到行缓冲。
        /// </summary>
        private static void PruneCache(Dictionary<int, byte[]> cache, int minimumRow)
        {
            if (cache.Count <= 8)
            {
                return;
            }

            List<int> stale = null;

            foreach (int key in cache.Keys)
            {
                if (key < minimumRow)
                {
                    if (stale == null)
                    {
                        stale = new List<int>();
                    }

                    stale.Add(key);
                }
            }

            if (stale == null)
            {
                return;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                cache.Remove(stale[i]);
            }
        }

        private static void AddRow(byte[] row, int[] sums, int count)
        {
            for (int i = 0; i < count; i++)
            {
                sums[i] += row[i];
            }
        }

        private static void SubtractRow(byte[] row, int[] sums, int count)
        {
            for (int i = 0; i < count; i++)
            {
                sums[i] -= row[i];
            }
        }

        /// <summary>镜像坐标（不重复边缘像素）。</summary>
        private static int Mirror(int coordinate, int length)
        {
            if (length <= 1)
            {
                return 0;
            }

            int period = 2 * length - 2;
            int value = coordinate % period;

            if (value < 0)
            {
                value += period;
            }

            return value < length ? value : period - value;
        }

        /// <summary>拷贝为独立缓冲。</summary>
        private static PixelBuffer Copy(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            byte[] output = PixelParallel.AllocateOutput(source);

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) => Buffer.BlockCopy(rowBuffer, 0, output, outputOffset, source.Stride),
                true,
                cancellationToken);

            return new PixelBuffer(output, source.Width, source.Height);
        }

        #endregion
    }
}
