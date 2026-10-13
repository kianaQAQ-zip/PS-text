using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Models;
using PSText.Services;

namespace PSText.Services.Imaging
{
    /// <summary>
    /// 图片元数据（EXIF / IPTC / XMP）与 ICC 色彩配置的保留。
    ///
    /// ## 为什么是"保存时重读文件"而不是"加载时存一份"
    ///
    /// <see cref="BitmapMetadata"/> 与 <see cref="ColorContext"/> **都有线程亲和性**：
    /// 解码发生在线程池线程上，把这两个对象存进文档快照、之后在界面线程上访问，会抛
    /// <c>InvalidOperationException: 调用线程无法访问此对象，因为另一个线程拥有该对象</c>。
    /// 而这类异常最阴的地方是：只要调用方顺手 catch 一下，表现就变成"元数据莫名其妙没了"
    /// （本次实现时真踩过：读出来全是 null，查了很久才发现是线程亲和性）。
    ///
    /// 所以策略是：**加载时只记两个布尔**（供界面提示），
    /// 真正要写回元数据时，由保存方在**自己那个线程**上从这个源文件重新读一次。
    /// 对象从创建到使用始终在同一个线程上，问题从根上不存在；
    /// 顺带还保证写进去的一定是文件里的真实内容，不会"拿着几天前的缓存去写"。
    ///
    /// ## 三个必须处理的点（都是 WPF 编码器的既有行为，不是猜的）
    ///
    ///   1. **元数据格式必须与目标编码器一致**。把从 JPEG 解出来的元数据传给 PNG 编码器会抛异常，
    ///      所以"另存为其他格式"时只能丢弃，并把这件事明确告诉用户（而不是静默丢掉）。
    ///   2. **方向标记必须清零**。像素在加载时已经按 EXIF 方向校正过了，
    ///      原样写回方向标记会让看图软件**再转一次** —— 用户看到的是"存完之后图躺倒了"。
    ///   3. **EXIF 缩略图必须丢掉**。那是**编辑之前**的小图，
    ///      留着会出现"缩略图与主图不一致"这种看起来很诡异的问题。
    ///
    /// 另外，像素尺寸字段（PixelXDimension / PixelYDimension）会按实际输出尺寸修正，
    /// 否则缩放过的图会写着原始尺寸，元数据自相矛盾。
    /// </summary>
    public static class ImageMetadataHelper
    {
        /// <summary>EXIF 方向标记（IFD0 / 274）。</summary>
        public const string OrientationQuery = "/app1/ifd/{uint=274}";

        /// <summary>相机厂商。</summary>
        public const string MakeQuery = "/app1/ifd/{uint=271}";

        /// <summary>相机型号。</summary>
        public const string ModelQuery = "/app1/ifd/{uint=272}";

        /// <summary>拍摄时间。</summary>
        public const string DateTimeOriginalQuery = "/app1/ifd/exif/subifd:{uint=36867}";

        /// <summary>EXIF 缩略图数据（存在于 EXIF 子 IFD）。</summary>
        private static readonly string[] ThumbnailQueries =
        {
            "/app1/ifd/exif/subifd:{uint=513}",
            "/app1/ifd/exif/subifd:{uint=514}",
            "/app1/ifd/exif/{uint=513}",
            "/app1/ifd/exif/{uint=514}"
        };

        private static readonly string[] PixelWidthQueries =
        {
            "/app1/ifd/exif/subifd:{uint=40962}",
            "/app1/ifd/exif/{uint=40962}"
        };

        private static readonly string[] PixelHeightQueries =
        {
            "/app1/ifd/exif/subifd:{uint=40963}",
            "/app1/ifd/exif/{uint=40963}"
        };

        /// <summary>编码器认识的格式名（<c>BitmapMetadata.Format</c> 用的就是这套名字）。</summary>
        public static string GetMetadataFormatName(ImageFileFormat format)
        {
            switch (format)
            {
                case ImageFileFormat.Jpeg:
                    return "jpg";
                case ImageFileFormat.Png:
                    return "png";
                case ImageFileFormat.Bmp:
                    return "bmp";
                case ImageFileFormat.Tiff:
                    return "tiff";
                case ImageFileFormat.Gif:
                    return "gif";
                default:
                    return null;
            }
        }

        #region 加载侧：只留两个布尔

        /// <summary>源文件是否带有可读的元数据（在解码线程上调用，只回传布尔）。</summary>
        public static bool HasReadableMetadata(BitmapFrame frame)
        {
            if (frame == null)
            {
                return false;
            }

            try
            {
                BitmapMetadata metadata = frame.Metadata as BitmapMetadata;

                if (metadata == null)
                {
                    return false;
                }

                return metadata.ContainsQuery("/app1")
                       || metadata.ContainsQuery("/ifd")
                       || metadata.ContainsQuery("/xmp");
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>源文件是否带 ICC 色彩配置。</summary>
        public static bool HasReadableColorProfile(BitmapFrame frame)
        {
            if (frame == null)
            {
                return false;
            }

            try
            {
                ReadOnlyCollection<ColorContext> contexts = frame.ColorContexts;
                return contexts != null && contexts.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region 保存侧：在调用线程上重读

        /// <summary>
        /// 为保存准备元数据。**格式不匹配 / 读不到时返回 null**，调用方应据此告知用户。
        ///
        /// 必须在**执行保存的那个线程**上调用（本方法把对象读出来就直接交回去，不做线程切换）。
        /// </summary>
        /// <param name="sourcePath">元数据来源文件（通常是文档的源文件路径）。</param>
        /// <param name="targetFormat">即将写出的格式。</param>
        /// <param name="pixelWidth">实际输出的像素宽。</param>
        /// <param name="pixelHeight">实际输出的像素高。</param>
        /// <param name="colorContexts">取到的 ICC 色彩配置（没有则为 null）。</param>
        public static BitmapMetadata LoadForSave(
            string sourcePath,
            ImageFileFormat targetFormat,
            int pixelWidth,
            int pixelHeight,
            out ReadOnlyCollection<ColorContext> colorContexts)
        {
            colorContexts = null;

            string expected = GetMetadataFormatName(targetFormat);

            if (expected == null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return null;
            }

            try
            {
                using (FileStream stream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames == null || decoder.Frames.Count == 0)
                    {
                        return null;
                    }

                    BitmapFrame frame = decoder.Frames[0];
                    BitmapMetadata source = frame.Metadata as BitmapMetadata;

                    if (source == null)
                    {
                        return null;
                    }

                    // 关键：格式不一致一律丢弃。硬塞给编码器会直接抛异常，
                    // 而"静默丢掉"又会让用户以为元数据还在 —— 所以返回 null，由调用方说明。
                    string actual;

                    try
                    {
                        actual = source.Format;
                    }
                    catch (Exception)
                    {
                        return null;
                    }

                    if (string.IsNullOrEmpty(actual)
                        || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    BitmapMetadata prepared = (BitmapMetadata)source.Clone();

                    RemoveThumbnail(prepared);
                    ResetOrientation(prepared);
                    UpdatePixelDimensions(prepared, pixelWidth, pixelHeight);

                    colorContexts = CopyColorContexts(frame);

                    return prepared;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>说明"这次为什么没保留元数据"，用于状态栏提示（绝不静默丢弃）。</summary>
        public static string DescribeSkipReason(string sourcePath, ImageFileFormat targetFormat)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return Loc.T("Meta_ReasonSourceGone");
            }

            string expected = GetMetadataFormatName(targetFormat);
            string targetName = ImageFileFormatHelper.GetDisplayName(targetFormat);

            if (expected == null)
            {
                return string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc.T("Meta_ReasonFormatUnsupported"),
                    targetName);
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.T("Meta_ReasonFormatMismatch"),
                targetName);
        }

        #endregion

        #region 读写工具

        /// <summary>安全读取一个字符串查询（读不到返回 null，绝不抛）。</summary>
        public static string TryGetString(BitmapMetadata metadata, string query)
        {
            if (metadata == null || string.IsNullOrEmpty(query))
            {
                return null;
            }

            try
            {
                if (!metadata.ContainsQuery(query))
                {
                    return null;
                }

                object value = metadata.GetQuery(query);
                return value == null ? null : value.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>安全读取一个无符号短整型查询（读不到返回 0）。</summary>
        public static ushort TryGetUShort(BitmapMetadata metadata, string query)
        {
            if (metadata == null || string.IsNullOrEmpty(query))
            {
                return 0;
            }

            try
            {
                if (!metadata.ContainsQuery(query))
                {
                    return 0;
                }

                object value = metadata.GetQuery(query);
                return value == null ? (ushort)0 : Convert.ToUInt16(value);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static ReadOnlyCollection<ColorContext> CopyColorContexts(BitmapFrame frame)
        {
            try
            {
                ReadOnlyCollection<ColorContext> contexts = frame.ColorContexts;

                if (contexts == null || contexts.Count == 0)
                {
                    return null;
                }

                ColorContext[] copy = new ColorContext[contexts.Count];

                for (int i = 0; i < contexts.Count; i++)
                {
                    copy[i] = contexts[i];
                }

                return new ReadOnlyCollection<ColorContext>(copy);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RemoveThumbnail(BitmapMetadata metadata)
        {
            for (int i = 0; i < ThumbnailQueries.Length; i++)
            {
                TryRemoveQuery(metadata, ThumbnailQueries[i]);
            }
        }

        /// <summary>
        /// 把方向标记清零。
        ///
        /// 只在**原本存在**这个查询时才写 —— 凭空给一张没有 EXIF 的图加一个方向标记，
        /// 反而会造出一段语义不完整的元数据。
        /// </summary>
        private static void ResetOrientation(BitmapMetadata metadata)
        {
            try
            {
                if (!metadata.ContainsQuery(OrientationQuery))
                {
                    return;
                }

                metadata.SetQuery(OrientationQuery, (ushort)1);
            }
            catch (Exception)
            {
            }
        }

        private static void UpdatePixelDimensions(BitmapMetadata metadata, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return;
            }

            SetIfPresent(metadata, PixelWidthQueries, (uint)pixelWidth);
            SetIfPresent(metadata, PixelHeightQueries, (uint)pixelHeight);
        }

        private static void SetIfPresent(BitmapMetadata metadata, string[] queries, uint value)
        {
            for (int i = 0; i < queries.Length; i++)
            {
                try
                {
                    if (!metadata.ContainsQuery(queries[i]))
                    {
                        continue;
                    }

                    metadata.SetQuery(queries[i], value);
                    return;
                }
                catch (Exception)
                {
                }
            }
        }

        private static void TryRemoveQuery(BitmapMetadata metadata, string query)
        {
            try
            {
                if (metadata.ContainsQuery(query))
                {
                    metadata.RemoveQuery(query);
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion
    }
}
