using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Models;

namespace PSText.Services.Interfaces
{
    /// <summary>
    /// 图片加载 / 保存服务。
    /// 约定：
    ///   1. 加载时立即解码并释放文件锁（BitmapCacheOption.OnLoad），不占用文件句柄；
    ///   2. 返回的位图已 Freeze，可安全跨线程使用；
    ///   3. 保存保持原始分辨率，按需沿用原始 DPI。
    /// </summary>
    public interface IImageService
    {
        /// <summary>
        /// 加载图片（异步）。全过程不持有文件锁。
        /// </summary>
        /// <param name="filePath">本机绝对路径。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task<ImageLoadResult> LoadAsync(string filePath, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// 加载降采样预览（用于拖动滑块时的实时预览），
        /// 仅解码到指定像素宽度，显著降低大图预览延迟。
        /// </summary>
        /// <param name="filePath">本机绝对路径。</param>
        /// <param name="decodePixelWidth">目标解码宽度；&lt;=0 表示按原分辨率解码。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task<ImageLoadResult> LoadPreviewAsync(
            string filePath,
            int decodePixelWidth,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// 保存位图到文件（异步）。按扩展名选择编码器，保持原始像素尺寸。
        /// </summary>
        /// <param name="bitmap">待保存位图（不会修改原对象）。</param>
        /// <param name="filePath">目标绝对路径。</param>
        /// <param name="options">保存参数；null 时按扩展名使用默认参数。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>写入后的文件字节数。</returns>
        Task<long> SaveAsync(
            System.Windows.Media.Imaging.BitmapSource bitmap,
            string filePath,
            ImageSaveOptions options,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>按文件头 + 扩展名检测格式。</summary>
        ImageFileFormat DetectFormat(string filePath);
    }
}
