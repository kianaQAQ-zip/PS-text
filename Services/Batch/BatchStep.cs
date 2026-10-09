using System;
using System.Threading;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Batch
{
    /// <summary>批量流水线的步骤类型。</summary>
    public enum BatchStepKind
    {
        /// <summary>尺寸缩放。</summary>
        Resize = 0,

        /// <summary>基础调整（亮度 / 对比度 / 饱和度 / 色温）。</summary>
        Adjustments,

        /// <summary>翻转与 90° 倍数旋转。</summary>
        FlipRotate,

        /// <summary>边框。</summary>
        Border,

        /// <summary>文字水印。</summary>
        Watermark
    }

    /// <summary>
    /// 流水线执行上下文。
    ///
    /// 存在的唯一理由是 <see cref="Scale"/>：面板里的缩略图预览是在**降采样图**上跑同一套步骤的，
    /// 而步骤里有"绝对像素"参数（目标宽高、边框粗细、固定字号）。
    /// 若不按比例换算，预览与实际输出就会不一致（预览里的水印明显偏大）。
    ///
    /// 约定（务必遵守，否则预览会骗人）：
    ///   * **绝对**像素参数一律乘 <see cref="Scale"/>；
    ///   * **相对**参数（百分比、比例）原样使用 —— 因为降采样不改变比例关系，
    ///     乘了反而会错。这条性质让"按宽度百分比"的字号天然对预览友好。
    /// 实际输出时 Scale 恒为 1.0。
    /// </summary>
    public sealed class BatchContext
    {
        /// <summary>实际输出用上下文（Scale = 1）。</summary>
        public static readonly BatchContext FullResolution = new BatchContext(1.0, 96.0, 96.0);

        public BatchContext(double scale, double dpiX, double dpiY)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0)
            {
                scale = 1.0;
            }

            Scale = scale;
            DpiX = dpiX > 0.5 && !double.IsNaN(dpiX) ? dpiX : 96.0;
            DpiY = dpiY > 0.5 && !double.IsNaN(dpiY) ? dpiY : 96.0;
        }

        /// <summary>预览缩放因子；实际输出为 1.0。</summary>
        public double Scale { get; private set; }

        /// <summary>源图水平 DPI。</summary>
        public double DpiX { get; private set; }

        /// <summary>源图垂直 DPI。</summary>
        public double DpiY { get; private set; }

        /// <summary>把绝对像素长度换算到当前上下文（至少 1 像素）。</summary>
        public int ScaleLength(int value)
        {
            if (value <= 0)
            {
                return 0;
            }

            int scaled = (int)Math.Round(value * Scale, MidpointRounding.AwayFromZero);
            return scaled < 1 ? 1 : scaled;
        }

        /// <summary>把绝对像素长度换算到当前上下文（保留小数，调用方自行取整）。</summary>
        public double ScaleLength(double value)
        {
            return value <= 0.0 ? 0.0 : value * Scale;
        }
    }

    /// <summary>
    /// 批量流水线中的一个步骤。
    ///
    /// 与 Add-ins 无关的简单约定：
    ///   * 纯函数 —— 不修改入参，返回新的缓冲；
    ///   * 不在内部做线程调度（调用方已经在后台线程上按序执行整条流水线）；
    ///   * <see cref="Clone"/> 用于把面板上的草稿步骤固化进队列，避免面板继续改动影响到已加入的步骤。
    /// </summary>
    public interface IBatchStep
    {
        /// <summary>步骤类型。</summary>
        BatchStepKind Kind { get; }

        /// <summary>列表里显示的步骤名。</summary>
        string DisplayName { get; }

        /// <summary>参数摘要（供列表第二行显示）。</summary>
        string Summary { get; }

        /// <summary>应用本步骤。</summary>
        PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BatchContext context,
            CancellationToken cancellationToken);

        /// <summary>复制一份（深拷贝），用于队列与面板之间的隔离。</summary>
        IBatchStep Clone();
    }
}
