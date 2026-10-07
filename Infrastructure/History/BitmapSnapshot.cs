using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.Imaging;

namespace PSText.Infrastructure.History
{
    /// <summary>
    /// 压缩位图快照（不可变）。
    ///
    /// 内存优化策略（对应需求 P0-3）：历史记录不保存未压缩的完整位图副本
    /// （2400×1600 的原始像素就要 15 MB，20 步就是 300 MB），
    /// 而是保存**压缩后的字节流**：
    ///   - 优先使用 **JPEG XR（HD Photo）无损**编码：`WmpBitmapEncoder` 是 WPF/WIC 自带的
    ///     JPEG XR 编解码器（**不是 WebP** —— .NET Framework 的 WIC 并未内置 WebP），
    ///     照片类图片无损压缩通常比 PNG 小 20%~40%；
    ///   - JPEG XR 不可用或解回校验不通过时回退 PNG（同样无损）。
    /// 经验值：照片类图片压缩后约为原始像素的 5%~15%，因此 20 步历史通常只需几十 MB。
    /// </summary>
    public sealed class BitmapSnapshot
    {
        private BitmapSnapshot(byte[] data, int width, int height, double dpiX, double dpiY, bool isJpegXr, long rawByteCount)
        {
            Data = data;
            Width = width;
            Height = height;
            DpiX = dpiX;
            DpiY = dpiY;
            IsJpegXr = isJpegXr;
            RawByteCount = rawByteCount;
        }

        /// <summary>压缩后的字节流。</summary>
        public byte[] Data { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public double DpiX { get; private set; }

        public double DpiY { get; private set; }

        /// <summary>是否使用 JPEG XR（HD Photo）压缩；false 表示回退到了 PNG。</summary>
        public bool IsJpegXr { get; private set; }

        /// <summary>未压缩时的像素字节数（用于评估压缩率）。</summary>
        public long RawByteCount { get; private set; }

        /// <summary>快照占用的内存字节数。</summary>
        public long ByteSize
        {
            get { return Data == null ? 0L : Data.LongLength; }
        }

        /// <summary>压缩率（0~1，越小越省内存）。</summary>
        public double CompressionRatio
        {
            get
            {
                if (RawByteCount <= 0)
                {
                    return 0.0;
                }

                return ByteSize / (double)RawByteCount;
            }
        }

        /// <summary>
        /// 从像素数据创建快照。失败时返回 null（调用方自行决定降级策略），不抛异常。
        /// </summary>
        public static BitmapSnapshot TryCreate(byte[] pixels, int width, int height, double dpiX, double dpiY)
        {
            if (pixels == null || width <= 0 || height <= 0)
            {
                return null;
            }

            try
            {
                BitmapSource source = BitmapSource.Create(
                    width,
                    height,
                    dpiX,
                    dpiY,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    width * 4);

                BitmapFrame frame = BitmapFrame.Create(source, null, null, null);
                long rawByteCount = (long)width * height * 4;

                // 1) 先尝试 JPEG XR 无损（体积通常比 PNG 小）：必须能原样解回，否则视为不可用。
                byte[] jpegXr = TryEncodeJpegXrLossless(frame);

                if (jpegXr != null && VerifyLossless(jpegXr, width, height, pixels))
                {
                    return new BitmapSnapshot(jpegXr, width, height, dpiX, dpiY, true, rawByteCount);
                }

                // 2) 回退 PNG（同样无损，兼容性最好）。
                byte[] png = TryEncode(frame, new PngBitmapEncoder());

                if (png != null)
                {
                    return new BitmapSnapshot(png, width, height, dpiX, dpiY, false, rawByteCount);
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BitmapSnapshot] 生成快照失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// JPEG XR（HD Photo）无损编码。
        ///
        /// 由 `WmpBitmapEncoder`（WIC 自带的 JPEG XR 编解码器）在 Lossless 模式下完成，
        /// 因此不引入任何第三方库。注意它不是 WebP。
        /// </summary>
        private static byte[] TryEncodeJpegXrLossless(BitmapFrame frame)
        {
            try
            {
                WmpBitmapEncoder encoder = new WmpBitmapEncoder
                {
                    Lossless = true,
                    UseCodecOptions = false
                };

                return EncodeToBytes(encoder, frame);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BitmapSnapshot] JPEG XR 编码不可用，改用 PNG: " + ex.Message);
                return null;
            }
        }

        private static byte[] TryEncode(BitmapFrame frame, BitmapEncoder encoder)
        {
            try
            {
                return EncodeToBytes(encoder, frame);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BitmapSnapshot] 编码失败: " + ex.Message);
                return null;
            }
        }

        private static byte[] EncodeToBytes(BitmapEncoder encoder, BitmapFrame frame)
        {
            encoder.Frames.Add(frame);

            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);

                if (stream.Length <= 0)
                {
                    return null;
                }

                return stream.ToArray();
            }
        }

        /// <summary>
        /// 校验 JPEG XR 压缩是否真的无损：解回后逐像素与原始数据比较（统一转 Bgra32 再比）。
        ///
        /// 为什么要校验：WmpBitmapEncoder（JPEG XR）在部分 Windows 7 环境下行为不一致。
        /// 与其相信它「应该无损」，不如编码后立刻解一次逐像素核对；
        /// 一旦发现差异就退回 PNG，保证撤销历史永远不会悄悄丢画质。
        /// English: Verifies the JPEG XR payload is truly pixel-exact by decoding it back
        /// and comparing against the source; falls back to PNG on any mismatch.
        /// </summary>
        private static bool VerifyLossless(byte[] data, int width, int height, byte[] originalPixels)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
                {
                    BitmapDecoder decoder = new WmpBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames == null || decoder.Frames.Count == 0)
                    {
                        return false;
                    }

                    BitmapSource frame = decoder.Frames[0];

                    if (frame.PixelWidth != width || frame.PixelHeight != height)
                    {
                        return false;
                    }

                    // 解码结果可能是 Bgr24 / Bgra32 等，统一转 Bgra32 后再比较。
                    BitmapSource comparable = frame;
                    if (frame.Format != PixelFormats.Bgra32)
                    {
                        FormatConvertedBitmap converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0.0);
                        converted.Freeze();
                        comparable = converted;
                    }

                    int stride = width * 4;
                    byte[] decoded = new byte[stride * height];

                    for (int y = 0; y < height; y++)
                    {
                        comparable.CopyPixels(
                            new System.Windows.Int32Rect(0, y, width, 1),
                            decoded,
                            stride,
                            y * stride);
                    }

                    for (int i = 0; i < decoded.Length; i++)
                    {
                        if (decoded[i] != originalPixels[i])
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[BitmapSnapshot] JPEG XR 不是无损（首个差异位于字节 " + i + "），改用 PNG。");
                            return false;
                        }
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BitmapSnapshot] JPEG XR 回读校验失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 解压为位图。失败时抛异常，由调用方决定如何提示。
        /// </summary>
        public BitmapSource ToBitmap()
        {
            if (Data == null || Data.LongLength == 0)
            {
                throw new InvalidOperationException("快照数据为空。");
            }

            using (MemoryStream stream = new MemoryStream(Data, false))
            {
                BitmapDecoder decoder = IsJpegXr
                    ? (BitmapDecoder)new WmpBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad)
                    : new PngBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                if (decoder.Frames == null || decoder.Frames.Count == 0)
                {
                    throw new InvalidOperationException("快照无法解码。");
                }

                BitmapSource frame = decoder.Frames[0];

                if (frame.CanFreeze && !frame.IsFrozen)
                {
                    frame.Freeze();
                }

                return frame;
            }
        }

        /// <summary>解压为像素缓冲（供滤镜链继续处理）。</summary>
        public PixelBuffer ToPixelBuffer()
        {
            return PixelBuffer.FromBitmap(ToBitmap());
        }
    }
}
