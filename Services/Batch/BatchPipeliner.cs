using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Batch
{
    /// <summary>
    /// 批量流水线执行器：把一串步骤按顺序套到像素缓冲上。
    ///
    /// 为什么顺序是有意义的（而不是"实现细节"）：
    ///   * 「先缩放 → 后加水印」：水印按百分比字号算，缩放前后都占同样比例，看着一样；
    ///     但若字号是**绝对像素**，先缩放会让水印在结果里显得更大；
    ///   * 「先加水印 → 后缩放」：水印被当作画面的一部分一起重采样，字会被缩小并变糊。
    /// 两种顺序得到的是不同的东西，所以顺序交给用户而不是藏起来。
    /// </summary>
    public static class BatchPipeliner
    {
        /// <summary>
        /// 按顺序应用全部步骤。返回的缓冲与入参完全独立（所有滤镜都是纯函数）。
        /// </summary>
        /// <param name="source">源缓冲（不被修改）。</param>
        /// <param name="steps">有序步骤列表；null 或空表示原样返回。</param>
        /// <param name="context">执行上下文（携带预览缩放因子与 DPI）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public static PixelBuffer Run(
            IReadOnlyPixelBuffer source,
            IList<IBatchStep> steps,
            BatchContext context,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (context == null)
            {
                context = BatchContext.FullResolution;
            }

            IReadOnlyPixelBuffer current = source;

            if (steps == null || steps.Count == 0)
            {
                return CopyOf(current, cancellationToken);
            }

            for (int i = 0; i < steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IBatchStep step = steps[i];

                if (step == null)
                {
                    continue;
                }

                // 任何一步返回 null 都视为"这一步没有输出"，属于实现错误，
                // 必须显式失败而不是把 null 传下去 —— 否则会以 NullReferenceException
                // 的形式出现在离现场很远的地方，排查成本很高。
                PixelBuffer next = step.Apply(current, context, cancellationToken);

                if (next == null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "流水线第 {0} 步（{1}）没有返回结果。",
                            i + 1,
                            step.DisplayName));
                }

                current = next;
            }

            return current is PixelBuffer buffer ? buffer : CopyOf(current, cancellationToken);
        }

        /// <summary>把步骤列表描述成一行文字（用于确认对话框 / 状态栏）。</summary>
        public static string Describe(IList<IBatchStep> steps)
        {
            if (steps == null || steps.Count == 0)
            {
                return "（没有步骤，只做格式转换）";
            }

            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" → ");
                }

                builder.Append(steps[i].DisplayName);
            }

            return builder.Length == 0 ? "（没有步骤，只做格式转换）" : builder.ToString();
        }

        private static PixelBuffer CopyOf(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int stride = source.Width * 4;
            byte[] pixels = new byte[stride * source.Height];

            for (int y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(y, pixels, y * stride, stride);
            }

            return new PixelBuffer(pixels, source.Width, source.Height);
        }
    }
}
