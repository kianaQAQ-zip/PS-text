using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Models;
using PSText.Services.Interfaces;

namespace PSText.Services
{
    /// <summary>
    /// WPF 实现的图片加载 / 保存服务。
    ///
    /// 关键设计（对应需求 P0-1）：
    ///   * 加载：FileStream + BitmapCacheOption.OnLoad，解码完成即关闭流，
    ///     立即释放文件锁（文件可被其他程序占用 / 删除 / 重命名）。
    ///   * 线程：解码在后台线程完成，返回已 Freeze 的位图，UI 线程仅做赋值。
    ///   * 保存：保持原始像素尺寸，写入图片原始 DPI，JPEG 质量可调。
    ///   * EXIF：根据方向标记（274）自动校正拍摄方向。
    /// </summary>
    public sealed class WpfImageService : IImageService
    {
        /// <summary>EXIF 方向标记的元数据查询路径。</summary>
        private const string OrientationQuery = "System.Photo.Orientation";

        public Task<ImageLoadResult> LoadAsync(string filePath, CancellationToken cancellationToken = default(CancellationToken))
        {
            return LoadCoreAsync(filePath, 0, cancellationToken);
        }

        public Task<ImageLoadResult> LoadPreviewAsync(
            string filePath,
            int decodePixelWidth,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return LoadCoreAsync(filePath, decodePixelWidth, cancellationToken);
        }

        public async Task<long> SaveAsync(
            BitmapSource bitmap,
            string filePath,
            ImageSaveOptions options,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("保存路径不能为空。", "filePath");
            }

            ImageSaveOptions effectiveOptions = options ?? ImageSaveOptions.FromPath(filePath);

            if (effectiveOptions.Format == ImageFileFormat.Unknown)
            {
                effectiveOptions.Format = ImageFileFormatHelper.FromPath(filePath);
            }

            // 编码属于 CPU 密集操作，放到线程池，避免阻塞 UI 线程（需求 P3-15）。
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EncodeToFile(bitmap, filePath, effectiveOptions);
                },
                cancellationToken).ConfigureAwait(true);

            try
            {
                FileInfo info = new FileInfo(filePath);
                return info.Exists ? info.Length : 0L;
            }
            catch (IOException)
            {
                return 0L;
            }
            catch (UnauthorizedAccessException)
            {
                return 0L;
            }
        }

        public ImageFileFormat DetectFormat(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ImageFileFormat.Unknown;
            }

            // 优先按文件头判断，扩展名仅作兜底（避免“.jpg 实为 png”这类情况）。
            ImageFileFormat fromHeader = DetectFormatByHeader(filePath);
            if (fromHeader != ImageFileFormat.Unknown)
            {
                return fromHeader;
            }

            return ImageFileFormatHelper.FromPath(filePath);
        }

        #region 加载

        private async Task<ImageLoadResult> LoadCoreAsync(
            string filePath,
            int decodePixelWidth,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("图片路径不能为空。", "filePath");
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("找不到图片文件：" + filePath, filePath);
            }

            ImageFileFormat headerFormat = DetectFormatByHeader(filePath);
            long fileSize = 0L;

            try
            {
                fileSize = new FileInfo(filePath).Length;
            }
            catch (IOException)
            {
                // 大小读取失败不影响加载。
                fileSize = 0L;
            }

            // 解码在线程池执行；BitmapFrame 在此处创建并立刻 OverrideCacheOption，
            // 因此 Task 结束后不存在任何文件句柄。
            ImageLoadResult result = await Task.Run(
                () => Decode(filePath, decodePixelWidth, headerFormat, fileSize),
                cancellationToken).ConfigureAwait(true);

            return result;
        }

        /// <summary>
        /// 同步解码（后台线程）。返回前关闭文件流、校正方向并冻结位图。
        /// </summary>
        private static ImageLoadResult Decode(
            string filePath,
            int decodePixelWidth,
            ImageFileFormat headerFormat,
            long fileSize)
        {
            BitmapDecoder decoder;
            using (FileStream stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                BitmapCacheOption cacheOption = BitmapCacheOption.OnLoad;

                if (decodePixelWidth > 0)
                {
                    BitmapImage preview = new BitmapImage();
                    preview.BeginInit();
                    preview.CacheOption = cacheOption;
                    preview.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    preview.DecodePixelWidth = decodePixelWidth;
                    preview.StreamSource = stream;
                    preview.EndInit();
                    preview.Freeze();

                    // 预览解码：不再读取元数据，按文件头推断格式。
                    return BuildResult(preview, headerFormat, preview.DpiX, preview.DpiY, false, fileSize, filePath);
                }

                decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    cacheOption);
            }

            if (decoder.Frames == null || decoder.Frames.Count == 0)
            {
                throw new InvalidOperationException("图片文件不包含可解码的图像帧：" + filePath);
            }

            BitmapFrame frame = decoder.Frames[0];
            ImageFileFormat format = ResolveFormat(headerFormat, decoder);
            double dpiX = frame.DpiX;
            double dpiY = frame.DpiY;

            // EXIF 方向标记必须在位图仍携带元数据时读取。
            ushort orientation = ReadOrientation(frame);
            BitmapSource oriented = ApplyOrientation(frame, orientation);
            bool normalized = orientation > 1;

            // 关键：把像素真正复制到一份新的位图里，与解码器彻底脱钩。
            //
            // BitmapFrame 即使 Freeze() 之后，访问某些属性（如 DpiX）仍会惰性回调解码器，
            // 而解码器保留了创建线程的线程亲和性 —— 表现为“调用线程无法访问此对象”，
            // 且只在跨线程使用位图时偶发（例如在别的线程保存 / 压缩快照）。
            // 复制像素后得到的是纯内存位图，可安全跨线程。
            BitmapSource materialized = Materialize(oriented);

            return new ImageLoadResult(materialized, format, dpiX, dpiY, normalized, fileSize, filePath);
        }

        /// <summary>
        /// 把位图物化为独立的纯内存位图（像素逐行复制，不做重采样）。
        ///
        /// English: Copies pixels into a standalone BitmapSource so the result has no
        /// reference to the decoder (whose objects keep the creating thread's affinity).
        /// </summary>
        private static BitmapSource Materialize(BitmapSource source)
        {
            if (source == null)
            {
                throw new InvalidOperationException("解码结果为空。");
            }

            int width = source.PixelWidth;
            int height = source.PixelHeight;

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("解码结果尺寸非法。");
            }

            // 统一用 Bgra32：后续滤镜 / 快照编码都按 4 字节/像素处理。
            // PixelFormat 是结构体（非编译期常量），因此用 static readonly。
            PixelFormat targetFormat = PixelFormats.Bgra32;

            BitmapSource readable = source;

            if (source.Format != targetFormat)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(source, targetFormat, null, 0.0);
                converted.Freeze();
                readable = converted;
            }

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];

            // 逐行复制：不能整块 CopyPixels，因为 WPF 按源位图自己的 stride 排布，
            // 当 stride != Width*4 时后续行会整体错位。
            for (int y = 0; y < height; y++)
            {
                readable.CopyPixels(new Int32Rect(0, y, width, 1), pixels, stride, y * stride);
            }

            BitmapSource result = BitmapSource.Create(
                width,
                height,
                source.DpiX,
                source.DpiY,
                targetFormat,
                null,
                pixels,
                stride);

            result.Freeze();
            return result;
        }

        private static ImageLoadResult BuildResult(
            BitmapSource bitmap,
            ImageFileFormat format,
            double dpiX,
            double dpiY,
            bool normalized,
            long fileSize,
            string filePath)
        {
            if (bitmap == null)
            {
                throw new InvalidOperationException("解码失败：" + filePath);
            }

            if (bitmap.CanFreeze && !bitmap.IsFrozen)
            {
                bitmap.Freeze();
            }

            return new ImageLoadResult(bitmap, format, dpiX, dpiY, normalized, fileSize, filePath);
        }

        private static ImageFileFormat ResolveFormat(ImageFileFormat headerFormat, BitmapDecoder decoder)
        {
            if (headerFormat != ImageFileFormat.Unknown)
            {
                return headerFormat;
            }

            try
            {
                if (decoder is JpegBitmapDecoder)
                {
                    return ImageFileFormat.Jpeg;
                }

                if (decoder is PngBitmapDecoder)
                {
                    return ImageFileFormat.Png;
                }

                if (decoder is BmpBitmapDecoder)
                {
                    return ImageFileFormat.Bmp;
                }

                if (decoder is TiffBitmapDecoder)
                {
                    return ImageFileFormat.Tiff;
                }

                if (decoder is GifBitmapDecoder)
                {
                    return ImageFileFormat.Gif;
                }

                string mime = decoder.CodecInfo != null ? decoder.CodecInfo.MimeTypes : null;
                if (!string.IsNullOrEmpty(mime))
                {
                    if (mime.IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0
                        || mime.IndexOf("jpg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ImageFileFormat.Jpeg;
                    }

                    if (mime.IndexOf("png", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ImageFileFormat.Png;
                    }

                    if (mime.IndexOf("bmp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ImageFileFormat.Bmp;
                    }

                    if (mime.IndexOf("tiff", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ImageFileFormat.Tiff;
                    }

                    if (mime.IndexOf("gif", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return ImageFileFormat.Gif;
                    }
                }
            }
            catch (NotSupportedException)
            {
                // CodecInfo 在个别解码器上可能不支持，忽略。
            }

            return ImageFileFormat.Unknown;
        }

        #endregion

        #region 文件头检测

        /// <summary>
        /// 通过魔数（magic number）判断格式：JPEG/PNG/BMP/TIFF/GIF。
        /// 读取失败时返回 Unknown，由扩展名兜底。
        /// </summary>
        private static ImageFileFormat DetectFormatByHeader(string filePath)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] header = new byte[8];
                    int read = stream.Read(header, 0, header.Length);

                    if (read < 4)
                    {
                        return ImageFileFormat.Unknown;
                    }

                    // JPEG: FF D8 FF
                    if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                    {
                        return ImageFileFormat.Jpeg;
                    }

                    // PNG: 89 50 4E 47 0D 0A 1A 0A
                    if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        return ImageFileFormat.Png;
                    }

                    // BMP: 'BM'
                    if (header[0] == 0x42 && header[1] == 0x4D)
                    {
                        return ImageFileFormat.Bmp;
                    }

                    // GIF: 'GIF8'
                    if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38)
                    {
                        return ImageFileFormat.Gif;
                    }

                    // TIFF: little endian 'II' 2A 00 或 big endian 'MM' 00 2A
                    bool littleEndian = header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00;
                    bool bigEndian = header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A;

                    if (littleEndian || bigEndian)
                    {
                        return ImageFileFormat.Tiff;
                    }

                    return ImageFileFormat.Unknown;
                }
            }
            catch (IOException)
            {
                return ImageFileFormat.Unknown;
            }
            catch (UnauthorizedAccessException)
            {
                return ImageFileFormat.Unknown;
            }
            catch (ArgumentException)
            {
                return ImageFileFormat.Unknown;
            }
        }

        #endregion

        #region EXIF 方向

        /// <summary>读取 EXIF 方向标记；读取失败或无标记时返回 1（正常）。</summary>
        private static ushort ReadOrientation(BitmapFrame frame)
        {
            try
            {
                if (frame == null)
                {
                    return 1;
                }

                BitmapMetadata bitmapMetadata = frame.Metadata as BitmapMetadata;
                if (bitmapMetadata == null)
                {
                    return 1;
                }

                object value = bitmapMetadata.GetQuery(OrientationQuery);
                if (value == null)
                {
                    return 1;
                }

                return Convert.ToUInt16(value, CultureInfo.InvariantCulture);
            }
            catch (NotSupportedException)
            {
                // 某些格式（如 PNG 无 EXIF）不支持查询。
                return 1;
            }
            catch (Exception)
            {
                // 元数据损坏时按正常方向处理，绝不因方向标记导致加载失败。
                return 1;
            }
        }

        /// <summary>
        /// 按 EXIF 方向标记（1~8）校正位图。
        /// English: Applies the EXIF orientation transform (flip/rotate) so the decoded
        /// bitmap matches what the camera intended. Orientation 1 means no transform.
        /// </summary>
        private static BitmapSource ApplyOrientation(BitmapSource source, ushort orientation)
        {
            if (source == null || orientation <= 1 || orientation > 8)
            {
                return source;
            }

            try
            {
                Transform transform = null;

                switch (orientation)
                {
                    case 2: // 水平镜像
                        transform = new ScaleTransform(-1, 1);
                        break;
                    case 3: // 旋转 180°
                        transform = new RotateTransform(180);
                        break;
                    case 4: // 垂直镜像
                        transform = new ScaleTransform(1, -1);
                        break;
                    case 5: // 顺时针 90° + 水平镜像
                        transform = new TransformGroup
                        {
                            Children = new TransformCollection
                            {
                                new ScaleTransform(-1, 1),
                                new RotateTransform(90)
                            }
                        };
                        break;
                    case 6: // 顺时针 90°
                        transform = new RotateTransform(90);
                        break;
                    case 7: // 顺时针 270° + 水平镜像
                        transform = new TransformGroup
                        {
                            Children = new TransformCollection
                            {
                                new ScaleTransform(-1, 1),
                                new RotateTransform(270)
                            }
                        };
                        break;
                    case 8: // 顺时针 270°
                        transform = new RotateTransform(270);
                        break;
                    default:
                        return source;
                }

                if (transform == null)
                {
                    return source;
                }

                if (transform.CanFreeze)
                {
                    transform.Freeze();
                }

                TransformedBitmap transformed = new TransformedBitmap(source, transform);
                if (transformed.CanFreeze)
                {
                    transformed.Freeze();
                }

                return transformed;
            }
            catch (Exception)
            {
                // 校正失败时退回原始位图，保证图片可显示。
                return source;
            }
        }

        #endregion

        #region 保存

        /// <summary>
        /// 编码并写入文件（后台线程）。
        /// 说明：始终使用 dpix/dpiy 写入原始 DPI，屏幕缩放不会影响打印清晰度（需求 P2-11）。
        /// </summary>
        private static void EncodeToFile(BitmapSource bitmap, string filePath, ImageSaveOptions options)
        {
            BitmapEncoder encoder = CreateEncoder(options.Format);

            double dpiX = options.PreserveDpi ? bitmap.DpiX : options.DpiX;
            double dpiY = options.PreserveDpi ? bitmap.DpiY : options.DpiY;

            if (dpiX <= 0.5 || double.IsNaN(dpiX))
            {
                dpiX = 96.0;
            }

            if (dpiY <= 0.5 || double.IsNaN(dpiY))
            {
                dpiY = 96.0;
            }

            // 格式整形 + DPI 覆盖在同一个转换步骤内完成：
            // FormatConvertedBitmap(source, format, palette, alphaThreshold, destDpiX, destDpiY)
            // 这是 BitmapSource.DpiX/DpiY 只读情况下唯一可靠的写 DPI 方式（需求 P2-11）。
            BitmapSource source = PrepareForEncoding(bitmap, options.Format, dpiX, dpiY);

            encoder.Frames.Add(BitmapFrame.Create(source, null, null, null));

            ApplyEncoderOptions(encoder, options);

            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
                stream.Flush();
            }
        }

        private static BitmapEncoder CreateEncoder(ImageFileFormat format)
        {
            switch (format)
            {
                case ImageFileFormat.Jpeg:
                    return new JpegBitmapEncoder();
                case ImageFileFormat.Bmp:
                    return new BmpBitmapEncoder();
                case ImageFileFormat.Tiff:
                    return new TiffBitmapEncoder();
                case ImageFileFormat.Gif:
                    return new GifBitmapEncoder();
                case ImageFileFormat.Png:
                default:
                    return new PngBitmapEncoder();
            }
        }

        /// <summary>按格式设置编码器专属参数。</summary>
        private static void ApplyEncoderOptions(BitmapEncoder encoder, ImageSaveOptions options)
        {
            JpegBitmapEncoder jpeg = encoder as JpegBitmapEncoder;
            if (jpeg != null)
            {
                jpeg.QualityLevel = options.GetSafeQuality();
            }

            TiffBitmapEncoder tiff = encoder as TiffBitmapEncoder;
            if (tiff != null)
            {
                tiff.Compression = ParseTiffCompression(options.TiffCompression);
            }

            // PNG / BMP / GIF 无质量参数；帧格式已由 PrepareForEncoding 整形。
        }

        private static TiffCompressOption ParseTiffCompression(string compression)
        {
            if (string.IsNullOrWhiteSpace(compression))
            {
                return TiffCompressOption.Lzw;
            }

            try
            {
                return (TiffCompressOption)Enum.Parse(typeof(TiffCompressOption), compression.Trim(), true);
            }
            catch (ArgumentException)
            {
                return TiffCompressOption.Lzw;
            }
        }

        /// <summary>
        /// 保存前的格式整形 + DPI 写入。
        ///   1. GIF 编码器只接受索引像素格式，需转换；
        ///   2. JPEG / BMP 不支持 Alpha 通道，Bgra32 等需转 Bgr24，否则可能黑底或保存失败；
        ///   3. 通过 FormatConvertedBitmap 的构造函数写入目标 DPI —— BitmapSource.DpiX/DpiY 是只读的，
        ///      这是在不复制像素数据的前提下设定输出 DPI 的标准做法。
        /// English: Normalizes the pixel format for the target encoder and writes the destination DPI
        /// in the same conversion step, since BitmapSource.DpiX/DpiY are read-only.
        /// </summary>
        private static BitmapSource PrepareForEncoding(
            BitmapSource bitmap,
            ImageFileFormat format,
            double dpiX,
            double dpiY)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            PixelFormat targetFormat = bitmap.Format;
            BitmapPalette targetPalette = null;
            bool needsConversion = false;

            if (format == ImageFileFormat.Jpeg || format == ImageFileFormat.Bmp)
            {
                // 这两种编码器没有 Alpha 通道概念，带 Alpha 或索引色的位图必须先转 Bgr24。
                if (bitmap.Format == PixelFormats.Bgra32
                    || bitmap.Format == PixelFormats.Pbgra32
                    || bitmap.Format == PixelFormats.Bgr101010
                    || bitmap.Format == PixelFormats.Rgba64
                    || bitmap.Format == PixelFormats.Prgba64
                    || bitmap.Format == PixelFormats.Indexed8
                    || bitmap.Format == PixelFormats.Indexed4
                    || bitmap.Format == PixelFormats.Indexed2
                    || bitmap.Format == PixelFormats.Indexed1
                    || bitmap.Format == PixelFormats.Gray8
                    || bitmap.Format == PixelFormats.Gray16)
                {
                    targetFormat = PixelFormats.Bgr24;
                    needsConversion = true;
                }
            }
            else if (format == ImageFileFormat.Gif)
            {
                if (bitmap.Format != PixelFormats.Indexed8)
                {
                    targetFormat = PixelFormats.Indexed8;
                    targetPalette = BitmapPalettes.Halftone256Transparent;
                    needsConversion = true;
                }
            }

            // 即使像素格式无需转换，只要 DPI 不同也必须重建一次才能写入目标 DPI。
            bool dpiChanged = Math.Abs(bitmap.DpiX - dpiX) > 0.01 || Math.Abs(bitmap.DpiY - dpiY) > 0.01;

            if (!needsConversion)
            {
                if (!dpiChanged)
                {
                    if (bitmap.CanFreeze && !bitmap.IsFrozen)
                    {
                        bitmap.Freeze();
                    }

                    return bitmap;
                }

                return ApplyDpi(bitmap, dpiX, dpiY);
            }

            BitmapSource converted;

            try
            {
                converted = new FormatConvertedBitmap(bitmap, targetFormat, targetPalette, 0.0);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                // 个别像素格式组合不被支持时，退回原始格式，保证保存不失败。
                System.Diagnostics.Debug.WriteLine("[WpfImageService] 像素格式整形失败，退回原格式: " + ex.Message);
                return bitmap;
            }

            return dpiChanged ? ApplyDpi(converted, dpiX, dpiY) : Freeze(converted);
        }

        /// <summary>
        /// 以目标 DPI 重建位图（像素数据逐字节复制，不做重采样，因此不会损失画质）。
        ///
        /// 为什么需要这一步：BitmapSource.DpiX / DpiY 是只读属性，.NET Framework 的
        /// FormatConvertedBitmap 也没有 dpi 参数，只有 BitmapSource.Create 能在创建时指定 DPI。
        /// 打印与“另存为”必须写入正确 DPI，否则 300 DPI 的扫描件存成 96 DPI，打印尺寸会放大 3 倍。
        /// English: Rebuilds the bitmap with the target DPI via BitmapSource.Create (pixels are copied
        /// verbatim, no resampling), because DpiX/DpiY are read-only on BitmapSource.
        /// </summary>
        private static BitmapSource ApplyDpi(BitmapSource source, double dpiX, double dpiY)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            try
            {
                int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
                byte[] pixels = new byte[stride * source.PixelHeight];
                source.CopyPixels(pixels, stride, 0);

                BitmapPalette palette = source.Palette;
                if (palette != null && (palette.Colors == null || palette.Colors.Count == 0))
                {
                    // BitmapSource.Create 拒绝空调色板，索引格式在读取像素后已自带正确调色板。
                    palette = null;
                }

                BitmapSource rebuilt = BitmapSource.Create(
                    source.PixelWidth,
                    source.PixelHeight,
                    dpiX,
                    dpiY,
                    source.Format,
                    palette,
                    pixels,
                    stride);

                return Freeze(rebuilt);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException || ex is OutOfMemoryException)
            {
                // 内存不足或格式不受支持时保留原始 DPI，优先保证能保存成功。
                System.Diagnostics.Debug.WriteLine("[WpfImageService] 写入 DPI 失败，保留原 DPI: " + ex.Message);
                return Freeze(source);
            }
        }

        private static BitmapSource Freeze(BitmapSource source)
        {
            if (source != null && source.CanFreeze && !source.IsFrozen)
            {
                source.Freeze();
            }

            return source;
        }

        #endregion
    }
}
