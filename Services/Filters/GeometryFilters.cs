using System;
using System.Threading;
using System.Threading.Tasks;
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
    /// 几何变换滤镜（需求 P1-7）：裁剪、旋转（90° 倍数）、水平 / 垂直翻转。
    ///
    /// 全部为纯函数且**不做插值**（角度均为 90° 倍数、裁剪按整数像素），
    /// 因此不会引入任何画质损失。裁剪区域会自动收敛到图像范围内，
    /// 非法输入返回原图副本而不是抛异常（Win7 上用户操作更容易出现越界）。
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
