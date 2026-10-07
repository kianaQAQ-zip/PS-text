using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 滤镜的公共并行执行辅助。
    ///
    /// 所有滤镜都是「输入 PixelBuffer → 输出新 PixelBuffer」的纯函数，
    /// 逐行处理时行与行相互独立，因此可以安全地按行并行，
    /// 并且复用每线程一份的行缓冲，避免大图产生大量短命对象。
    /// English: Row-wise parallel helper shared by all pixel filters. Each row is independent,
    /// so results are identical to a serial loop.
    /// </summary>
    internal static class PixelParallel
    {
        /// <summary>启用行级并行的最小像素数（约 200 万像素）。</summary>
        private const long ParallelThreshold = 2000000L;

        private static readonly bool IsSingleCpu = Environment.ProcessorCount <= 1;

        /// <summary>
        /// 按行执行处理函数：行缓冲由本方法提供（每线程一份），
        /// 处理函数只负责“读 rowBuffer → 写 output”。
        /// </summary>
        /// <param name="source">源缓冲（只读）。</param>
        /// <param name="output">输出缓冲（已分配，长度为 source.Stride * source.Height）。</param>
        /// <param name="rowAction">(y, rowBuffer, outputOffset) 的处理委托。</param>
        /// <param name="useParallel">是否允许并行（调用方有跨行状态时传 false）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <param name="progress">进度回调（0~1），可为 null。</param>
        public static void ForEachRow(
            IReadOnlyPixelBuffer source,
            byte[] output,
            Action<int, byte[], int> rowAction,
            bool useParallel = true,
            CancellationToken cancellationToken = default(CancellationToken),
            IProgress<double> progress = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            if (rowAction == null)
            {
                throw new ArgumentNullException("rowAction");
            }

            int width = source.Width;
            int height = source.Height;
            int stride = width * 4;

            long required = (long)stride * height;
            if (output.LongLength < required)
            {
                throw new ArgumentException("输出缓冲长度不足。", "output");
            }

            bool parallel = useParallel
                            && !IsSingleCpu
                            && progress == null
                            && required >= ParallelThreshold;

            if (parallel)
            {
                Parallel.For(
                    0,
                    height,
                    new ParallelOptions { CancellationToken = cancellationToken },
                    () => new byte[stride],
                    (y, state, threadBuffer) =>
                    {
                        source.CopyRow(y, threadBuffer, 0, stride);
                        rowAction(y, threadBuffer, y * stride);
                        return threadBuffer;
                    },
                    threadBuffer => { });

                return;
            }

            byte[] rowBuffer = new byte[stride];
            int progressInterval = Math.Max(1, height / 20);

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                source.CopyRow(y, rowBuffer, 0, stride);
                rowAction(y, rowBuffer, y * stride);

                if (progress != null && (y % progressInterval == 0 || y == height - 1))
                {
                    progress.Report((y + 1) / (double)height);
                }
            }
        }

        /// <summary>分配与源缓冲同尺寸的输出数组。</summary>
        public static byte[] AllocateOutput(IReadOnlyPixelBuffer source)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            long required = (long)source.Stride * source.Height;

            if (required > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    "source",
                    string.Format("图像过大（需要 {0} 字节），超出单次处理上限。", required));
            }

            return new byte[required];
        }

        /// <summary>把值限制到 0~255。</summary>
        public static int ClampToByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > 255 ? 255 : value;
        }
    }
}
