using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 智能填充（消除）：把掩膜标记的区域用周围像素重建成"看起来本来就没有这块东西"。
    ///
    /// 算法是**调和扩散**（harmonic inpainting）：求解 Laplace 方程 Δu = 0，
    /// 边界条件取掩膜外一圈的真实像素。它的两个性质正好对上任一办公场景：
    ///
    ///   1. **常值解**：若边界是同一个颜色，填充结果就是那个颜色 —— 所以白纸上的红章、
    ///      纯色背景上的半透明文字，填完就是"干净的原背景"；
    ///   2. **调和函数精确重现线性函数**：若背景是线性渐变（常见于扫描件的照明不均），
    ///      填充结果仍然严格落在同一条渐变线上，不会留下可见接缝。
    ///
    /// 反过来也要说清限制：调和插值是**平滑**的，因此**纹理背景**（草地、织物、人脸）
    /// 上的水印填完会留下一块平滑斑块。这不是实现问题，是这类算法的数学边界 ——
    /// 那种情况应改用仿制图章手动取纹理。
    ///
    /// 数值做法：只在掩膜的包围盒外扩一圈（ROI）内求解，用对称 SOR（ω=1.9）加速收敛，
    /// 收敛后只把掩膜内的像素写回，因此**掩膜外的像素逐字节不变**。
    /// </summary>
    public static class InpaintFilter
    {
        /// <summary>单次求解的 ROI 像素上限（float 缓冲约 32MB）；超出请分批处理。</summary>
        public const long MaxSolvePixels = 2000000L;

        /// <summary>SOR 松弛因子（1 为标准 Gauss-Seidel，接近 2 收敛最快但不稳定）。</summary>
        private const double Omega = 1.9;

        /// <summary>收敛判据：单次扫掠的最大变化小于该值即停。</summary>
        private const double ConvergenceThreshold = 0.05;

        /// <summary>表面（掩膜包围盒）像素数量上限检查。</summary>
        public static bool IsSolvable(int maskBoundingWidth, int maskBoundingHeight)
        {
            if (maskBoundingWidth <= 0 || maskBoundingHeight <= 0)
            {
                return true;
            }

            long margin = Math.Max(8, Math.Max(maskBoundingWidth, maskBoundingHeight) / 4);
            long roiWidth = maskBoundingWidth + margin * 2;
            long roiHeight = maskBoundingHeight + margin * 2;

            return roiWidth * roiHeight <= MaxSolvePixels;
        }

        /// <summary>按掩膜填充（同步纯函数；mask 非 0 的像素会被重建）。</summary>
        public static PixelBuffer Inpaint(
            IReadOnlyPixelBuffer source,
            byte[] mask,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Inpaint(source, mask, 0, cancellationToken);
        }

        /// <summary>按掩膜填充（同步纯函数）。maxSweeps 为 0 时按 ROI 尺寸自动决定。</summary>
        public static PixelBuffer Inpaint(
            IReadOnlyPixelBuffer source,
            byte[] mask,
            int maxSweeps,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            int width = source.Width;
            int height = source.Height;

            if (mask == null || mask.Length < width * height)
            {
                throw new ArgumentException("掩膜长度必须与图像像素数一致。", "mask");
            }

            byte[] sourcePixels = source.GetPixels();
            byte[] output = CopyPixels(sourcePixels);

            // ---- 1) 掩膜包围盒 ----
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = -1;
            int maxY = -1;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    if (mask[rowOffset + x] == 0)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            if (maxX < 0)
            {
                // 没有任何标记：原样返回（保证"空掩膜 = 逐像素恒等"）
                return new PixelBuffer(output, width, height);
            }

            // ---- 2) ROI：包围盒外扩一圈，保证未知像素的邻域全部落在 ROI 内 ----
            int boundingWidth = maxX - minX + 1;
            int boundingHeight = maxY - minY + 1;
            int margin = Math.Max(8, Math.Max(boundingWidth, boundingHeight) / 4);

            int roiX = Math.Max(0, minX - margin);
            int roiY = Math.Max(0, minY - margin);
            int roiRight = Math.Min(width - 1, maxX + margin);
            int roiBottom = Math.Min(height - 1, maxY + margin);
            int roiWidth = roiRight - roiX + 1;
            int roiHeight = roiBottom - roiY + 1;

            if ((long)roiWidth * roiHeight > MaxSolvePixels)
            {
                throw new ArgumentOutOfRangeException(
                    "mask",
                    string.Format(
                        "标记区域过大（求解范围 {0} × {1} px），请分批标记后再填充。",
                        roiWidth, roiHeight));
            }

            // ---- 3) 把 ROI 取到 float 缓冲，未知像素打标 ----
            int roiPixels = roiWidth * roiHeight;
            float[] values = new float[roiPixels * 4];
            bool[] unknown = new bool[roiPixels];

            double[] knownSum = new double[4];
            long knownCount = 0;

            for (int ry = 0; ry < roiHeight; ry++)
            {
                int sourceRow = (roiY + ry) * width;

                for (int rx = 0; rx < roiWidth; rx++)
                {
                    int sourceIndex = (sourceRow + roiX + rx) * 4;
                    int roiIndex = (ry * roiWidth + rx) * 4;
                    bool isUnknown = mask[sourceRow + roiX + rx] != 0;

                    unknown[ry * roiWidth + rx] = isUnknown;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        float value = sourcePixels[sourceIndex + channel];
                        values[roiIndex + channel] = value;

                        if (!isUnknown)
                        {
                            knownSum[channel] += value;
                        }
                    }

                    if (!isUnknown)
                    {
                        knownCount++;
                    }
                }
            }

            // ---- 4) 初值：已知像素的均值（迭代的起点，不影响收敛结果，只影响速度） ----
            if (knownCount > 0)
            {
                for (int i = 0; i < roiPixels; i++)
                {
                    if (!unknown[i])
                    {
                        continue;
                    }

                    int index = i * 4;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        values[index + channel] = (float)(knownSum[channel] / knownCount);
                    }
                }
            }

            // ---- 5) 对称 SOR 松弛求解 ----
            int sweeps = maxSweeps > 0
                ? maxSweeps
                : Math.Max(128, Math.Max(roiWidth, roiHeight) * 3);

            for (int sweep = 0; sweep < sweeps; sweep++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool forward = (sweep % 2) == 0;
                double maxDelta = 0.0;

                for (int step = 0; step < roiPixels; step++)
                {
                    int i = forward ? step : (roiPixels - 1 - step);

                    if (!unknown[i])
                    {
                        continue;
                    }

                    int x = i % roiWidth;
                    int y = i / roiWidth;
                    int index = i * 4;

                    int left = x > 0 ? i - 1 : -1;
                    int right = x < roiWidth - 1 ? i + 1 : -1;
                    int up = y > 0 ? i - roiWidth : -1;
                    int down = y < roiHeight - 1 ? i + roiWidth : -1;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        double sum = 0.0;
                        int count = 0;

                        if (left >= 0)
                        {
                            sum += values[left * 4 + channel];
                            count++;
                        }

                        if (right >= 0)
                        {
                            sum += values[right * 4 + channel];
                            count++;
                        }

                        if (up >= 0)
                        {
                            sum += values[up * 4 + channel];
                            count++;
                        }

                        if (down >= 0)
                        {
                            sum += values[down * 4 + channel];
                            count++;
                        }

                        if (count == 0)
                        {
                            continue;
                        }

                        double target = sum / count;
                        double current = values[index + channel];
                        double next = current + Omega * (target - current);

                        values[index + channel] = (float)next;

                        double delta = Math.Abs(next - current);

                        if (delta > maxDelta)
                        {
                            maxDelta = delta;
                        }
                    }
                }

                if (maxDelta < ConvergenceThreshold)
                {
                    break;
                }
            }

            // ---- 6) 只把掩膜内的像素写回 ----
            for (int ry = 0; ry < roiHeight; ry++)
            {
                int sourceRow = (roiY + ry) * width;

                for (int rx = 0; rx < roiWidth; rx++)
                {
                    int flatIndex = ry * roiWidth + rx;

                    if (!unknown[flatIndex])
                    {
                        continue;
                    }

                    int sourceIndex = (sourceRow + roiX + rx) * 4;
                    int roiIndex = flatIndex * 4;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        output[sourceIndex + channel] = ToByte(values[roiIndex + channel]);
                    }
                }
            }

            return new PixelBuffer(output, width, height);
        }

        /// <summary>异步填充（在后台线程调用同步版本）。</summary>
        public static Task<PixelBuffer> InpaintAsync(
            IReadOnlyPixelBuffer source,
            byte[] mask,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(() => Inpaint(source, mask, cancellationToken), cancellationToken);
        }

        private static byte[] CopyPixels(byte[] pixels)
        {
            byte[] copy = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
            return copy;
        }

        private static byte ToByte(double value)
        {
            int rounded = (int)Math.Round(value);

            if (rounded <= 0)
            {
                return 0;
            }

            return rounded >= 255 ? (byte)255 : (byte)rounded;
        }
    }
}
