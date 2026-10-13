using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PSText.Models
{
    /// <summary>
    /// 图片加载结果（不可变）。携带解码后的位图以及用于打印 / 保存的元数据。
    /// </summary>
    public sealed class ImageLoadResult
    {
        public ImageLoadResult(
            BitmapSource bitmap,
            ImageFileFormat format,
            double dpiX,
            double dpiY,
            bool orientationNormalized,
            long fileSizeBytes,
            string filePath)
            : this(bitmap, format, dpiX, dpiY, orientationNormalized, fileSizeBytes, filePath, false, false)
        {
        }

        public ImageLoadResult(
            BitmapSource bitmap,
            ImageFileFormat format,
            double dpiX,
            double dpiY,
            bool orientationNormalized,
            long fileSizeBytes,
            string filePath,
            bool hasSourceMetadata,
            bool hasColorProfile)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            Bitmap = bitmap;
            Format = format;
            FileSizeBytes = fileSizeBytes;
            FilePath = filePath;
            OrientationNormalized = orientationNormalized;

            // DPI 兜底：部分扫描/手机图片缺失 DPI 信息，按屏幕标准 96 DPI 处理，
            // 保证打印与保存时不会出现除零或尺寸异常。
            double safeDpiX = dpiX > 0.5 && !double.IsNaN(dpiX) ? dpiX : 96.0;
            double safeDpiY = dpiY > 0.5 && !double.IsNaN(dpiY) ? dpiY : 96.0;

            DpiX = safeDpiX;
            DpiY = safeDpiY;
            HasSourceMetadata = hasSourceMetadata;
            HasColorProfile = hasColorProfile;
        }

        /// <summary>解码后的位图（已冻结，可跨线程访问）。</summary>
        public BitmapSource Bitmap { get; private set; }

        /// <summary>像素宽度。</summary>
        public int PixelWidth
        {
            get { return Bitmap.PixelWidth; }
        }

        /// <summary>像素高度。</summary>
        public int PixelHeight
        {
            get { return Bitmap.PixelHeight; }
        }

        /// <summary>水平 DPI（原始 DPI，缺失时 96）。</summary>
        public double DpiX { get; private set; }

        /// <summary>垂直 DPI（原始 DPI，缺失时 96）。</summary>
        public double DpiY { get; private set; }

        /// <summary>检测到的文件格式。</summary>
        public ImageFileFormat Format { get; private set; }

        /// <summary>是否根据 EXIF 方向标记做了旋转/镜像校正。</summary>
        public bool OrientationNormalized { get; private set; }

        /// <summary>源文件字节数，未知时为 0。</summary>
        public long FileSizeBytes { get; private set; }

        /// <summary>源文件完整路径。</summary>
        public string FilePath { get; private set; }

        /// <summary>
        /// 源文件是否带有 EXIF / IPTC / XMP 元数据（仅供界面提示）。
        ///
        /// 这里**只留一个 bool，不留 BitmapMetadata 对象** —— 那个类型有线程亲和性：
        /// 解码发生在线程池线程上，界面线程碰它就会抛"调用线程无法访问此对象"。
        /// 真正要保留元数据时，是保存时在**同一个线程**上从源文件重读的
        /// （见 ImageMetadataHelper 与 WpfImageService.EncodeToFile 的说明）。
        /// </summary>
        public bool HasSourceMetadata { get; private set; }

        /// <summary>源文件是否带 ICC 色彩配置（同样只留 bool，理由见上）。</summary>
        public bool HasColorProfile { get; private set; }
    }
}
