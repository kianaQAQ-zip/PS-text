using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>旋转角度（只支持 90 的整数倍，保证无损）。</summary>
    public enum RotationAngle
    {
        None = 0,
        Clockwise90 = 90,
        Clockwise180 = 180,
        Clockwise270 = 270
    }

    /// <summary>
    /// 几何变换滤镜（需求 P1-7 + M1）：裁剪、90° 倍数旋转、水平 / 垂直翻转，
    /// 以及 M1 新增的**任意尺寸缩放**与**任意角度旋转**。
    ///
    /// 90° 倍数旋转与裁剪为纯函数且**不做插值**（角度为 90° 倍数、裁剪按整数像素），
    /// 因此不会引入任何画质损失；缩放与任意角度旋转必然涉及重采样，
    /// 其质量由 <see cref="Resampler"/> 的核选择与预乘 alpha 处理保证。
    /// 裁剪区域会自动收敛到图像范围内，非法输入返回原图副本而不是抛异常
    /// （Win7 上用户操作更容易出现越界）。
    /// </summary>
    public sealed class GeometryFilters
    {
        /// <summary>水平翻转（左右镜像）。</summary>
        public Task<PixelBuffer> FlipHorizontalAsync(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => FlipHorizontal(source, cancellationToken), cancellationToken);
        }

        /// <summary>水平翻转（同步纯函数）。</summary>
        public PixelBuffer FlipHorizontal(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            byte[] output = PixelParallel.AllocateOutput(source);
            int width = source.Width;
            int stride = source.Stride;

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int sourceIndex = x * 4;
                        int targetIndex = outputOffset + (width - 1 - x) * 4;

                        output[targetIndex] = rowBuffer[sourceIndex];
                        output[targetIndex + 1] = rowBuffer[sourceIndex + 1];
                        output[targetIndex + 2] = rowBuffer[sourceIndex + 2];
                        output[targetIndex + 3] = rowBuffer[sourceIndex + 3];
                    }
                },
                true,
                cancellationToken);

            return new PixelBuffer(output, source.Width, source.Height);
        }

        /// <summary>垂直翻转（上下镜像）。</summary>
        public Task<PixelBuffer> FlipVerticalAsync(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => FlipVertical(source, cancellationToken), cancellationToken);
        }

        /// <summary>垂直翻转（同步纯函数）。</summary>
        public PixelBuffer FlipVertical(
            IReadOnlyPixelBuffer source,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            byte[] output = PixelParallel.AllocateOutput(source);
            int height = source.Height;
            int stride = source.Stride;

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) =>
                {
                    // 行号镜像：目标行 = 高度 - 1 - 源行
                    Buffer.BlockCopy(rowBuffer, 0, output, (height - 1 - y) * stride, stride);
                },
                true,
                cancellationToken);

            return new PixelBuffer(output, source.Width, source.Height);
        }

        /// <summary>按 90° 倍数旋转（顺时针）。</summary>
        public Task<PixelBuffer> RotateAsync(
            IReadOnlyPixelBuffer source,
            RotationAngle angle,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Rotate(source, angle, cancellationToken), cancellationToken);
        }

        /// <summary>按 90° 倍数旋转（顺时针，同步纯函数）。</summary>
        public PixelBuffer Rotate(
            IReadOnlyPixelBuffer source,
            RotationAngle angle,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            switch (angle)
            {
                case RotationAngle.Clockwise90:
                    return Rotate90(source, cancellationToken);
                case RotationAngle.Clockwise180:
                    return Rotate180(source, cancellationToken);
                case RotationAngle.Clockwise270:
                    return Rotate270(source, cancellationToken);
                default:
                    return Copy(source, cancellationToken);
            }
        }

        /// <summary>
        /// 裁剪到指定矩形。区域会自动收敛到图像范围内；
        /// 若收敛后面积为 0（例如完全越界）则返回原图副本。
        /// </summary>
        /// <param name="source">源缓冲。</param>
        /// <param name="x">左上角 X。</param>
        /// <param name="y">左上角 Y。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> CropAsync(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Crop(source, x, y, width, height, cancellationToken), cancellationToken);
        }

        /// <summary>裁剪（同步纯函数）。</summary>
        public PixelBuffer Crop(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(source.Width, x + width);
            int bottom = Math.Min(source.Height, y + height);

            int cropWidth = right - left;
            int cropHeight = bottom - top;

            if (cropWidth <= 0 || cropHeight <= 0)
            {
                // 非法 / 越界的裁剪区域：返回原图副本，避免上层拿到 0 尺寸位图。
                return Copy(source, cancellationToken);
            }

            if (cropWidth == source.Width && cropHeight == source.Height && left == 0 && top == 0)
            {
                return Copy(source, cancellationToken);
            }

            byte[] output = new byte[cropWidth * cropHeight * 4];
            byte[] rowBuffer = new byte[source.Stride];

            for (int row = 0; row < cropHeight; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                source.CopyRow(top + row, rowBuffer, 0, source.Stride);
                Buffer.BlockCopy(rowBuffer, left * 4, output, row * cropWidth * 4, cropWidth * 4);
            }

            return new PixelBuffer(output, cropWidth, cropHeight);
        }

        #region 尺寸缩放（M1）

        /// <summary>
        /// 把图像缩放到指定像素尺寸。
        ///
        /// 只是 <see cref="Resampler"/> 的转发：几何滤镜保持"纯函数 + 可取消"的同一套约定，
        /// 重采样算法本身集中在 Resampler 里，便于自检单独钉住它。
        /// </summary>
        public Task<PixelBuffer> ResizeAsync(
            IReadOnlyPixelBuffer source,
            int width,
            int height,
            ResampleKernel kernel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(() => Resampler.Resize(source, width, height, kernel, cancellationToken), cancellationToken);
        }

        /// <summary>缩放（同步纯函数）。</summary>
        public PixelBuffer Resize(
            IReadOnlyPixelBuffer source,
            int width,
            int height,
            ResampleKernel kernel,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Resampler.Resize(source, width, height, kernel, cancellationToken);
        }

        #endregion

        #region 任意角度旋转（M1）

        /// <summary>
        /// 计算任意角度旋转后的画布尺寸。
        /// </summary>
        /// <param name="cropToInscribed">
        /// true = 输出"自动裁掉空白角"后的尺寸（保持原图宽高比的内接矩形）；
        /// false = 输出包含整幅旋转结果的画布（四角用背景填充）。
        /// </param>
        public static void CalcRotatedSize(
            int width,
            int height,
            double angleDegrees,
            bool cropToInscribed,
            out int outWidth,
            out int outHeight)
        {
            if (width <= 0 || height <= 0)
            {
                outWidth = Math.Max(1, width);
                outHeight = Math.Max(1, height);
                return;
            }

            double angle = NormalizeAngle(angleDegrees);
            double radians = angle * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(radians));
            double sin = Math.Abs(Math.Sin(radians));

            double targetWidth;
            double targetHeight;

            RotationAngle rightAngle;
            bool isRightAngle = TryGetRightAngle(angle, out rightAngle);

            if (cropToInscribed && !isRightAngle)
            {
                // 让"保持原宽高比的中心矩形"旋转 -angle 后仍能落回原图内。
                //
                // 推导：一个 W×H 的矩形旋转 θ 后，其轴对齐包围盒为
                //   (W|cosθ| + H|sinθ|) × (W|sinθ| + H|cosθ|)。
                // 凸多边形的极值点就是顶点，因此"旋转后的矩形落在原图 w×h 内"
                // 等价于"它的包围盒落在 w×h 内"。代入 W = s·w、H = s·h 解 s 得：
                //   s ≤ w / (w|cosθ| + h|sinθ|)  且  s ≤ h / (w|sinθ| + h|cosθ|)
                // 取两者较小值即为最大内接比例。
                double denominatorX = width * cos + height * sin;
                double denominatorY = width * sin + height * cos;

                double scaleX = denominatorX > 1e-9 ? width / denominatorX : 1.0;
                double scaleY = denominatorY > 1e-9 ? height / denominatorY : 1.0;
                double scale = Math.Min(scaleX, scaleY);

                targetWidth = width * scale;
                targetHeight = height * scale;
            }
            else
            {
                // 包围盒：90° 的整数倍时正好等于宽高互换，因此这里无需特判。
                targetWidth = width * cos + height * sin;
                targetHeight = width * sin + height * cos;
            }

            outWidth = Math.Max(1, (int)Math.Round(targetWidth));
            outHeight = Math.Max(1, (int)Math.Round(targetHeight));
        }

        /// <summary>按任意角度旋转（异步）。</summary>
        /// <param name="background">四角填充色；null 表示透明。</param>
        /// <param name="cropToInscribed">true = 自动裁掉旋转产生的空白角（保持原图宽高比）。</param>
        public Task<PixelBuffer> RotateArbitraryAsync(
            IReadOnlyPixelBuffer source,
            double angleDegrees,
            Color? background,
            bool cropToInscribed,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(
                () => RotateArbitrary(source, angleDegrees, background, cropToInscribed, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// 按任意角度旋转（顺时针为正，同步纯函数）。
        ///
        /// 实现要点：
        ///   - 用**逆映射**（对每个输出像素反算源坐标）而不是正映射，避免正映射留下未填充的孔洞；
        ///   - 采样用双线性，且与 Resampler 一样**先预乘 alpha 再还原**，
        ///     否则透明图旋转后边缘会出现黑边（透明像素的 RGB 通常是 0）；
        ///   - 90° 的整数倍直接交给既有的无损旋转路径：既快又逐像素无损，
        ///     而且此时"没有空白角"，不该被裁。
        /// </summary>
        public PixelBuffer RotateArbitrary(
            IReadOnlyPixelBuffer source,
            double angleDegrees,
            Color? background,
            bool cropToInscribed,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            double angle = NormalizeAngle(angleDegrees);

            RotationAngle rightAngle;

            if (TryGetRightAngle(angle, out rightAngle))
            {
                return Rotate(source, rightAngle, cancellationToken);
            }

            int outWidth;
            int outHeight;
            CalcRotatedSize(source.Width, source.Height, angle, cropToInscribed, out outWidth, out outHeight);

            if (!Resampler.IsValidSize(outWidth, outHeight))
            {
                throw new ArgumentOutOfRangeException(
                    "angleDegrees",
                    string.Format("旋转后的画布过大：{0} × {1}。", outWidth, outHeight));
            }

            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            int sourceStride = source.Stride;

            // 预乘 alpha 的源副本（旋转会在半透明边缘插值，直通 alpha 会被"看不见的黑"拉暗）
            byte[] sourcePixels = new byte[sourceStride * sourceHeight];

            for (int y = 0; y < sourceHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(y, sourcePixels, y * sourceStride, sourceStride);
            }

            Premultiply(sourcePixels);

            byte[] output = new byte[outWidth * outHeight * 4];

            int backgroundB = 0;
            int backgroundG = 0;
            int backgroundR = 0;
            int backgroundA = 0;

            if (background.HasValue)
            {
                backgroundA = background.Value.A;
                backgroundB = background.Value.B * backgroundA / 255;
                backgroundG = background.Value.G * backgroundA / 255;
                backgroundR = background.Value.R * backgroundA / 255;
            }

            double radians = angle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double sourceCenterX = sourceWidth / 2.0;
            double sourceCenterY = sourceHeight / 2.0;
            double outputCenterX = outWidth / 2.0;
            double outputCenterY = outHeight / 2.0;

            Parallel.For(
                0,
                outHeight,
                new ParallelOptions { CancellationToken = cancellationToken },
                y =>
                {
                    int rowOffset = y * outWidth * 4;
                    double outputV = y + 0.5 - outputCenterY;

                    for (int x = 0; x < outWidth; x++)
                    {
                        double outputU = x + 0.5 - outputCenterX;

                        // 逆旋转：把输出点映射回源坐标（顺时针 θ 的正向映射是 R(θ)，逆映射即 R(-θ)）
                        double sourceU = outputU * cos + outputV * sin;
                        double sourceV = -outputU * sin + outputV * cos;

                        double sampleX = sourceU + sourceCenterX - 0.5;
                        double sampleY = sourceV + sourceCenterY - 0.5;

                        int targetIndex = rowOffset + x * 4;

                        if (sampleX < -0.5 || sampleY < -0.5
                            || sampleX > sourceWidth - 0.5 || sampleY > sourceHeight - 0.5)
                        {
                            output[targetIndex] = (byte)backgroundB;
                            output[targetIndex + 1] = (byte)backgroundG;
                            output[targetIndex + 2] = (byte)backgroundR;
                            output[targetIndex + 3] = (byte)backgroundA;
                            continue;
                        }

                        SampleBilinear(
                            sourcePixels,
                            sourceWidth,
                            sourceHeight,
                            sourceStride,
                            sampleX,
                            sampleY,
                            output,
                            targetIndex);
                    }
                });

            Unpremultiply(output);
            return new PixelBuffer(output, outWidth, outHeight);
        }

        /// <summary>双线性取样（索引越界按边缘延伸，避免边缘出现一圈暗边）。</summary>
        private static void SampleBilinear(
            byte[] pixels,
            int width,
            int height,
            int stride,
            double x,
            double y,
            byte[] destination,
            int destinationIndex)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);

            double fx = x - x0;
            double fy = y - y0;

            int left = ClampIndex(x0, width);
            int right = ClampIndex(x0 + 1, width);
            int top = ClampIndex(y0, height);
            int bottom = ClampIndex(y0 + 1, height);

            int indexTopLeft = top * stride + left * 4;
            int indexTopRight = top * stride + right * 4;
            int indexBottomLeft = bottom * stride + left * 4;
            int indexBottomRight = bottom * stride + right * 4;

            double weightTopLeft = (1.0 - fx) * (1.0 - fy);
            double weightTopRight = fx * (1.0 - fy);
            double weightBottomLeft = (1.0 - fx) * fy;
            double weightBottomRight = fx * fy;

            for (int channel = 0; channel < 4; channel++)
            {
                double value = pixels[indexTopLeft + channel] * weightTopLeft
                               + pixels[indexTopRight + channel] * weightTopRight
                               + pixels[indexBottomLeft + channel] * weightBottomLeft
                               + pixels[indexBottomRight + channel] * weightBottomRight;

                destination[destinationIndex + channel] = ToByte(value);
            }
        }

        private static int ClampIndex(int value, int length)
        {
            if (value < 0)
            {
                return 0;
            }

            return value >= length ? length - 1 : value;
        }

        /// <summary>把 BGRA 就地转成预乘 alpha。</summary>
        private static void Premultiply(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];

                if (alpha == 255)
                {
                    continue;
                }

                if (alpha == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    continue;
                }

                pixels[i] = (byte)(pixels[i] * alpha / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
            }
        }

        /// <summary>把预乘 alpha 就地还原为直通 alpha。</summary>
        private static void Unpremultiply(byte[] pixels)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];

                if (alpha == 255)
                {
                    continue;
                }

                if (alpha == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    continue;
                }

                pixels[i] = ToByte(pixels[i] * 255.0 / alpha);
                pixels[i + 1] = ToByte(pixels[i + 1] * 255.0 / alpha);
                pixels[i + 2] = ToByte(pixels[i + 2] * 255.0 / alpha);
            }
        }

        private static byte ToByte(double value)
        {
            int rounded = (int)Math.Round(value);

            if (rounded <= 0)
            {
                return 0;
            }

            return rounded >= 255 ? (byte)255 : (byte)rounded;
        }

        /// <summary>归一化到 (-180, 180]。</summary>
        private static double NormalizeAngle(double degrees)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees))
            {
                return 0.0;
            }

            double angle = degrees % 360.0;

            if (angle > 180.0)
            {
                angle -= 360.0;
            }
            else if (angle <= -180.0)
            {
                angle += 360.0;
            }

            return angle;
        }

        /// <summary>角度是否为 90° 的整数倍（是则可用无损路径）。</summary>
        private static bool TryGetRightAngle(double angle, out RotationAngle result)
        {
            double quarters = Math.Round(angle / 90.0);

            if (Math.Abs(angle - quarters * 90.0) > 1e-6)
            {
                result = RotationAngle.None;
                return false;
            }

            int normalized = (((int)quarters % 4) + 4) % 4;

            switch (normalized)
            {
                case 1:
                    result = RotationAngle.Clockwise90;
                    return true;
                case 2:
                    result = RotationAngle.Clockwise180;
                    return true;
                case 3:
                    result = RotationAngle.Clockwise270;
                    return true;
                default:
                    result = RotationAngle.None;
                    return true;
            }
        }

        #endregion

        #region 旋转实现

        /// <summary>
        /// 顺时针 90°：目标尺寸为 (h, w)，
        /// 映射关系 dst(x, y) = src(y, srcHeight - 1 - x) 的等价形式为
        /// dst(x, y) = src(x_src = y, y_src = srcHeight - 1 - x)。
        /// </summary>
        private static PixelBuffer Rotate90(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            int targetWidth = sourceHeight;
            int targetHeight = sourceWidth;

            byte[] output = new byte[targetWidth * targetHeight * 4];
            byte[] sourceRow = new byte[source.Stride];

            // 按源行遍历：源行 y_src 上的像素会落到目标列 x_dst = sourceHeight - 1 - y_src
            for (int y = 0; y < sourceHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                source.CopyRow(y, sourceRow, 0, source.Stride);
                int targetColumn = sourceHeight - 1 - y;

                for (int x = 0; x < sourceWidth; x++)
                {
                    int sourceIndex = x * 4;
                    int targetIndex = (x * targetWidth + targetColumn) * 4;

                    output[targetIndex] = sourceRow[sourceIndex];
                    output[targetIndex + 1] = sourceRow[sourceIndex + 1];
                    output[targetIndex + 2] = sourceRow[sourceIndex + 2];
                    output[targetIndex + 3] = sourceRow[sourceIndex + 3];
                }
            }

            return new PixelBuffer(output, targetWidth, targetHeight);
        }

        /// <summary>顺时针 180°：直接按行列双向镜像，避免多次分配中间缓冲。</summary>
        private static PixelBuffer Rotate180(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            int width = source.Width;
            int height = source.Height;
            int stride = source.Stride;

            byte[] output = PixelParallel.AllocateOutput(source);

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) =>
                {
                    int targetRow = (height - 1 - y) * stride;

                    for (int x = 0; x < width; x++)
                    {
                        int sourceIndex = x * 4;
                        int targetIndex = targetRow + (width - 1 - x) * 4;

                        output[targetIndex] = rowBuffer[sourceIndex];
                        output[targetIndex + 1] = rowBuffer[sourceIndex + 1];
                        output[targetIndex + 2] = rowBuffer[sourceIndex + 2];
                        output[targetIndex + 3] = rowBuffer[sourceIndex + 3];
                    }
                },
                true,
                cancellationToken);

            return new PixelBuffer(output, width, height);
        }

        /// <summary>顺时针 270°：等价于逆时针 90°，也可由三次 90° 得到。</summary>
        private static PixelBuffer Rotate270(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            PixelBuffer first = Rotate90(source, cancellationToken);
            PixelBuffer second = Rotate90(first, cancellationToken);
            return Rotate90(second, cancellationToken);
        }

        #endregion

        /// <summary>拷贝为独立缓冲。</summary>
        private static PixelBuffer Copy(IReadOnlyPixelBuffer source, CancellationToken cancellationToken)
        {
            byte[] output = PixelParallel.AllocateOutput(source);

            PixelParallel.ForEachRow(
                source,
                output,
                (y, rowBuffer, outputOffset) => Buffer.BlockCopy(rowBuffer, 0, output, outputOffset, source.Stride),
                true,
                cancellationToken);

            return new PixelBuffer(output, source.Width, source.Height);
        }
    }
}
