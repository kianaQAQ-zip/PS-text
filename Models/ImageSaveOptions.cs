using System;

namespace PSText.Models
{
    /// <summary>保存（编码）参数。</summary>
    public sealed class ImageSaveOptions
    {
        public ImageSaveOptions()
        {
            Format = ImageFileFormat.Png;
            QualityLevel = 90;
            TiffCompression = "LZW";
            PreserveDpi = true;
        }

        /// <summary>目标格式。</summary>
        public ImageFileFormat Format { get; set; }

        /// <summary>JPEG 质量（1~100）。</summary>
        public int QualityLevel { get; set; }

        /// <summary>TIFF 压缩方式：None / LZW / Zip / Rle。</summary>
        public string TiffCompression { get; set; }

        /// <summary>是否沿用原图 DPI（打印清晰度的关键）。</summary>
        public bool PreserveDpi { get; set; }

        /// <summary>PreserveDpi 为 false 时使用的 DPI。</summary>
        public double DpiX { get; set; }

        /// <summary>PreserveDpi 为 false 时使用的 DPI。</summary>
        public double DpiY { get; set; }

        /// <summary>
        /// 要从哪个文件读取元数据写回（通常是当前文档的源文件路径）。
        ///
        /// 这里传的是**路径而不是 BitmapMetadata 对象**，原因是那个类型有线程亲和性：
        /// 解码发生在线程池线程上，对象一旦跨线程使用就抛"调用线程无法访问此对象"。
        /// 传路径让编码器在**自己那个线程**上重新读一次，从根上绕开这个问题
        /// （顺带还保证了写进去的一定是文件里的真实内容）。
        /// </summary>
        public string MetadataSourcePath { get; set; }

        /// <summary>是否尝试保留源文件的元数据（默认关，由调用方显式打开）。</summary>
        public bool PreserveMetadata { get; set; }

        /// <summary>
        /// 元数据因格式不匹配被丢弃时的说明（由编码方填写，供界面提示）。
        /// </summary>
        public string MetadataNote { get; set; }

        /// <summary>按文件扩展名创建默认保存参数。</summary>
        public static ImageSaveOptions FromPath(string path)
        {
            ImageFileFormat format = ImageFileFormatHelper.FromPath(path);

            if (format == ImageFileFormat.Unknown)
            {
                format = ImageFileFormat.Png;
            }

            return new ImageSaveOptions { Format = format };
        }

        /// <summary>质量值修正到合法区间。</summary>
        public int GetSafeQuality()
        {
            if (QualityLevel < 1)
            {
                return 1;
            }

            return QualityLevel > 100 ? 100 : QualityLevel;
        }
    }
}
