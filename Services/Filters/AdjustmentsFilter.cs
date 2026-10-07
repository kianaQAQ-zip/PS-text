using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;
using PSText.Models;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 基础调整滤镜：亮度 / 对比度 / 饱和度 / 色温。
    ///
    /// 纯函数实现：输入 IReadOnlyPixelBuffer + 参数，输出新的 PixelBuffer，
    /// 不修改输入、不接触 UI、可在任意线程调用（需求 P1-4）。
    ///
    /// 算法说明（English summary）：
    ///   * Brightness / Contrast / Temperature are per-channel point operations
    ///     (out = clamp(contrast*(in - 128) + 128 + brightness + tempShift[c])),
    ///     so they are precomputed into three 256-entry lookup tables — O(1) per channel,
    ///     which keeps large images fast.
    ///   * Saturation is a per-pixel blend towards the Rec.601 luma:
    ///     out = luma + (in - luma) * saturationFactor.
    /// </summary>
    public sealed class AdjustmentsFilter
    {
        // Rec.601 亮度权重（整数定点，避免逐像素浮点运算）
        private const int LumaRed = 77;    // 0.299 * 256
        private const int LumaGreen = 150; // 0.587 * 256
        private const int LumaBlue = 29;   // 0.114 * 256

        /// <summary>启用行级并行的最小像素数（约 200 万像素，即 1920×1080 以下走串行）。</summary>
        private const long ParallelThreshold = 2000000L;

        private static readonly bool IsSingleCpu = Environment.ProcessorCount <= 1;

        /// <summary>
        /// 处理一行像素（无状态，可安全并行调用；行缓冲由调用方提供以便复用）。
        /// </summary>
        private static void ApplyRow(
            IReadOnlyPixelBuffer source,
            byte[] output,
            int y,
            int stride,
            byte[] rowBuffer,
            byte[] lutBlue,
            byte[] lutGreen,
            byte[] lutRed,
            int saturationFactor)
        {
            source.CopyRow(y, rowBuffer, 0, stride);

            int rowOffset = y * stride;
            int width = source.Width;

            for (int x = 0; x < width; x++)
            {
                int index = x * 4;

                int blue = lutBlue[rowBuffer[index]];
                int green = lutGreen[rowBuffer[index + 1]];
                int red = lutRed[rowBuffer[index + 2]];

                if (saturationFactor != 256)
                {
                    // 饱和度：向亮度灰阶插值。
                    int luma = (LumaRed * red + LumaGreen * green + LumaBlue * blue + 128) >> 8;
                    red = ClampToByte(luma + (((red - luma) * saturationFactor) >> 8));
                    green = ClampToByte(luma + (((green - luma) * saturationFactor) >> 8));
                    blue = ClampToByte(luma + (((blue - luma) * saturationFactor) >> 8));
                }

                output[rowOffset + index] = (byte)blue;
                output[rowOffset + index + 1] = (byte)green;
                output[rowOffset + index + 2] = (byte)red;
                output[rowOffset + index + 3] = rowBuffer[index + 3];
            }
        }

        /// <summary>
        /// 对全分辨率缓冲应用调整（异步，内部在线程池执行）。
        /// 大图时分块处理，并按需回报进度，保证不阻塞 UI 线程（需求 P3-15）。
        /// </summary>
        /// <param name="source">源像素缓冲（不被修改）。</param>
        /// <param name="adjustments">调整参数。</param>
        /// <param name="progress">进度回调（0~1），可为 null。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> ApplyAsync(
            IReadOnlyPixelBuffer source,
            PixelAdjustments adjustments,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            PixelAdjustments effective = adjustments ?? PixelAdjustments.Neutral;

            // 中性参数直接复制，避免无意义的运算。
            return Task.Run(
                () => Apply(source, effective, progress, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// 同步应用调整（纯函数）。
        /// 预览时传入降采样缓冲即可获得同样结果，只是分辨率更低。
        /// </summary>
        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            PixelAdjustments adjustments,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            PixelAdjustments effective = adjustments ?? PixelAdjustments.Neutral;

            int width = source.Width;
            int height = source.Height;
            int stride = width * 4;
            byte[] output = new byte[stride * height];

            // 逐行取数：既避免依赖底层数组布局，也避免在像素循环里反复取数组引用。
            byte[] rowBuffer = new byte[stride];

            if (effective.IsNeutral)
            {
                // 原样复制。
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    source.CopyRow(y, rowBuffer, 0, stride);
                    Buffer.BlockCopy(rowBuffer, 0, output, y * stride, stride);
                }

                ReportProgress(progress, 1.0);
                return new PixelBuffer(output, width, height);
            }

            byte[] lutBlue;
            byte[] lutGreen;
            byte[] lutRed;
            int saturationFactor;

            BuildLookupTables(effective, out lutBlue, out lutGreen, out lutRed, out saturationFactor);

            // 大图分块并行处理（每个线程负责若干行，行之间完全独立，结果与串行一致）。
            // 小图走串行路径，避免并行调度开销反而变慢。
            bool useParallel = height * width >= ParallelThreshold && !IsSingleCpu;

            if (useParallel && progress == null)
            {
                // 每个线程复用自己的行缓冲（若每行都 new 一个，大图会产生大量短命对象）。
                Parallel.For(
                    0,
                    height,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    () => new byte[stride],
                    (y, state, threadBuffer) =>
                    {
                        ApplyRow(source, output, y, stride, threadBuffer, lutBlue, lutGreen, lutRed, saturationFactor);
                        return threadBuffer;
                    },
                    threadBuffer => { });
            }
            else
            {
                // 每处理这么多行回报一次进度，避免过于频繁的跨线程回调。
                int progressInterval = Math.Max(1, height / 20);

                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ApplyRow(source, output, y, stride, rowBuffer, lutBlue, lutGreen, lutRed, saturationFactor);

                    if (progress != null && (y % progressInterval == 0 || y == height - 1))
                    {
                        ReportProgress(progress, (y + 1) / (double)height);
                    }
                }
            }

            ReportProgress(progress, 1.0);
            return new PixelBuffer(output, width, height);
        }

        /// <summary>
        /// 构建逐通道查找表：把亮度、对比度、色温三个逐点运算合并成一次查表。
        /// English: Folds brightness, contrast and temperature into three per-channel LUTs.
        /// </summary>
        private static void BuildLookupTables(
            PixelAdjustments adjustments,
            out byte[] lutBlue,
            out byte[] lutGreen,
            out byte[] lutRed,
            out int saturationFactor)
        {
            lutBlue = new byte[256];
            lutGreen = new byte[256];
            lutRed = new byte[256];

            // 对比度：以 128 为支点做线性拉伸。
            //   0    -> 斜率 1（不变）
            //   +100 -> 斜率 2.5
            //   -100 -> 斜率 0.25（接近纯灰）
            double contrastSlope = 1.0 + (adjustments.Contrast / 100.0) * 1.5;
            double brightnessOffset = adjustments.Brightness * 1.28;

            // 色温：正值加强红、减弱蓝（偏暖），负值相反。系数参考常见白平衡近似。
            double temperature = adjustments.Temperature / 100.0;
            double redShift = temperature * 32.0;
            double greenShift = temperature * 4.0;
            double blueShift = -temperature * 32.0;

            for (int value = 0; value < 256; value++)
            {
                double baseValue = value;

                lutRed[value] = ToByte(ClampChan(ApplyTone(baseValue, contrastSlope, brightnessOffset + redShift)));
                lutGreen[value] = ToByte(ClampChan(ApplyTone(baseValue, contrastSlope, brightnessOffset + greenShift)));
                lutBlue[value] = ToByte(ClampChan(ApplyTone(baseValue, contrastSlope, brightnessOffset + blueShift)));
            }

            // 饱和度以 256 为 1.0；-100 -> 0（完全灰度），+100 -> 512（约 2 倍）。
            saturationFactor = (int)Math.Round(256.0 * (1.0 + adjustments.Saturation / 100.0));
        }

        private static double ApplyTone(double value, double contrastSlope, double offset)
        {
            return 128.0 + (value - 128.0) * contrastSlope + offset;
        }

        private static double ClampChan(double value)
        {
            if (value < 0.0)
            {
                return 0.0;
            }

            return value > 255.0 ? 255.0 : value;
        }

        private static byte ToByte(double value)
        {
            return (byte)(value + 0.5);
        }

        private static int ClampToByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > 255 ? 255 : value;
        }

        private static void ReportProgress(IProgress<double> progress, double value)
        {
            if (progress != null)
            {
                progress.Report(value);
            }
        }
    }
}
