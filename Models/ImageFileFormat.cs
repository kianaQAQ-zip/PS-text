using System;
using System.Collections.Generic;
using System.IO;

namespace PSText.Models
{
    /// <summary>支持的图片文件格式。</summary>
    public enum ImageFileFormat
    {
        /// <summary>未知格式，保存时按 PNG 处理。</summary>
        Unknown = 0,
        Jpeg,
        Png,
        Bmp,
        Tiff,
        Gif
    }

    /// <summary>图片格式枚举与扩展名 / 编码器 MIME 类型的映射工具。</summary>
    public static class ImageFileFormatHelper
    {
        private static readonly Dictionary<ImageFileFormat, string[]> Extensions =
            new Dictionary<ImageFileFormat, string[]>
            {
                { ImageFileFormat.Jpeg, new[] { ".jpg", ".jpeg", ".jpe", ".jfif" } },
                { ImageFileFormat.Png, new[] { ".png" } },
                { ImageFileFormat.Bmp, new[] { ".bmp", ".dib" } },
                { ImageFileFormat.Tiff, new[] { ".tif", ".tiff" } },
                { ImageFileFormat.Gif, new[] { ".gif" } }
            };

        private static readonly Dictionary<ImageFileFormat, string> MimeTypes =
            new Dictionary<ImageFileFormat, string>
            {
                { ImageFileFormat.Jpeg, "image/jpeg" },
                { ImageFileFormat.Png, "image/png" },
                { ImageFileFormat.Bmp, "image/bmp" },
                { ImageFileFormat.Tiff, "image/tiff" },
                { ImageFileFormat.Gif, "image/gif" }
            };

        /// <summary>按文件路径（扩展名）推断格式，无法判断时返回 Unknown。</summary>
        public static ImageFileFormat FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ImageFileFormat.Unknown;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return ImageFileFormat.Unknown;
            }

            return FromExtension(extension);
        }

        /// <summary>按扩展名推断格式（忽略大小写）。</summary>
        public static ImageFileFormat FromExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ImageFileFormat.Unknown;
            }

            string normalized = extension.Trim();
            if (!normalized.StartsWith(".", StringComparison.Ordinal))
            {
                normalized = "." + normalized;
            }

            foreach (KeyValuePair<ImageFileFormat, string[]> pair in Extensions)
            {
                foreach (string candidate in pair.Value)
                {
                    if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Key;
                    }
                }
            }

            return ImageFileFormat.Unknown;
        }

        /// <summary>获取该格式的首选扩展名（含点）。</summary>
        public static string GetDefaultExtension(ImageFileFormat format)
        {
            string[] candidates;
            if (Extensions.TryGetValue(format, out candidates) && candidates.Length > 0)
            {
                return candidates[0];
            }

            return ".png";
        }

        /// <summary>获取 WPF 位图编码器使用的 MIME 类型。</summary>
        public static string GetMimeType(ImageFileFormat format)
        {
            string mime;
            if (MimeTypes.TryGetValue(format, out mime))
            {
                return mime;
            }

            return "image/png";
        }

        /// <summary>格式的中文显示名。</summary>
        public static string GetDisplayName(ImageFileFormat format)
        {
            switch (format)
            {
                case ImageFileFormat.Jpeg:
                    return "JPEG";
                case ImageFileFormat.Png:
                    return "PNG";
                case ImageFileFormat.Bmp:
                    return "BMP";
                case ImageFileFormat.Tiff:
                    return "TIFF";
                case ImageFileFormat.Gif:
                    return "GIF";
                default:
                    return "未知";
            }
        }

        /// <summary>构造 OpenFileDialog 用的过滤器字符串。</summary>
        public static string BuildOpenFilter()
        {
            return "所有支持的图片|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.tif;*.tiff;*.gif"
                   + "|JPEG 图片|*.jpg;*.jpeg;*.jpe;*.jfif"
                   + "|PNG 图片|*.png"
                   + "|BMP 图片|*.bmp;*.dib"
                   + "|TIFF 图片|*.tif;*.tiff"
                   + "|GIF 图片|*.gif"
                   + "|所有文件|*.*";
        }
    }
}
