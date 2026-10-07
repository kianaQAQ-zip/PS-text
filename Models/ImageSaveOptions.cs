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
