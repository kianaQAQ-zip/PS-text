using System;
using System.Threading;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 遮盖类标注（马赛克 / 模糊）的像素处理。
    ///
    /// 与其它滤镜的根本区别：它不是"对整幅图做一次处理"，而是**为某一块区域现场生成素材**。
    ///
    /// 为什么不做"整幅预处理 + 缓存"：12MP 图整体像素化是一份 48 MB 的位图，
    /// 而遮盖标注通常只有几百像素见方。按区域现场生成是几十 KB、亚毫秒级，
    /// 因此可以跟着标注拖动逐帧重算，不需要任何跨帧缓存，也就没有"底图变了忘了失效"的风险。
    ///
    /// 由此带来一个必须守住的性质：**块网格锚定在整幅图的 (0,0)**。
    /// 若按区域自己的左上角起算，标注每移动一个像素、所有块边界就跟着挪，
    /// 拖动时马赛克会"闪烁流动"。所以 <see cref="PixelateRegion"/> 会先把区域
    /// **向外对齐到块边界**再处理，并把实际起点通过 out 参数回传，
    /// 由调用方用它来摆放笔刷（保证相位与"整幅图一起像素化"完全一致）。
    /// </summary>
    public static class MosaicFilter
    {
        /// <summary>马赛克块边长下限（像素）。</summary>
        public const int MinBlockSize = 2;

        /// <summary>马赛克块边长上限（像素）。</summary>
        public const int MaxBlockSize = 200;

        /// <summary>模糊半径下限（像素）。</summary>
        public const double MinBlurRadius = 0.5;

        /// <summary>模糊半径上限（像素）。</summary>
        public const double MaxBlurRadius = 120.0;

        /// <summary>块边长收敛到合法区间。</summary>
        public static int ClampBlockSize(int value)
        {
            if (value < MinBlockSize)
            {
                return MinBlockSize;
            }

            return value > MaxBlockSize ? MaxBlockSize : value;
        }

        /// <summary>模糊半径收敛到合法区间。</summary>
        public static double ClampBlurRadius(double value)
        {
            if (double.IsNaN(value) || value < MinBlurRadius)
            {
                return MinBlurRadius;
            }

            return value > MaxBlurRadius ? MaxBlurRadius : value;
        }

        /// <summary>
        /// 对指定矩形做**块平均**（马赛克）。
        ///
        /// 返回的缓冲覆盖"向外对齐到块边界"后的区域，因此比请求的矩形略大；
        /// 实际起点由 <paramref name="originX"/> / <paramref name="originY"/> 回传，
        /// 调用方按它摆放笔刷即可（多出来的边角会被几何裁剪掉，不影响观感）。
        /// </summary>
        /// <returns>处理结果；请求区域完全落在图像之外时返回 null。</returns>
        public static PixelBuffer PixelateRegion(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            int blockSize,
            out int originX,
            out int originY,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            originX = 0;
            originY = 0;

            int block = ClampBlockSize(blockSize);

            if (!TryGetVisibleBounds(source, x, y, width, height, out int left, out int top, out int right, out int bottom))
            {
                return null;
            }

            // 向外对齐到块边界。注意是**对 (0,0) 取整**，不是对区域左上角取整 ——
            // 这正是"平移标注不改变马赛克相位"的来源。
            originX = left / block * block;
            originY = top / block * block;

            int endX = Math.Min(source.Width, (right + block - 1) / block * block);
            int endY = Math.Min(source.Height, (bottom + block - 1) / block * block);

            int outWidth = endX - originX;
            int outHeight = endY - originY;

            if (outWidth < 1 || outHeight < 1)
            {
                return null;
            }

            byte[] output = new byte[outWidth * outHeight * 4];
            byte[] row = new byte[source.Stride];
            int stride = source.Stride;

            int blockColumns = (outWidth + block - 1) / block;

            // 逐"块行"处理：块行内先把每块的通道和累加起来，再一次性把整块涂成同一个值。
            // 这样每一行只需要 CopyRow 一次（若在块内循环里逐行取数，同一行会被重复取 numBlocks 次）。
            int[] sums = new int[blockColumns * 4];
            int[] counts = new int[blockColumns];

            for (int blockTop = originY; blockTop < endY; blockTop += block)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int blockBottom = Math.Min(blockTop + block, source.Height);

                Array.Clear(sums, 0, sums.Length);
                Array.Clear(counts, 0, counts.Length);

                for (int sourceY = blockTop; sourceY < blockBottom; sourceY++)
                {
                    source.CopyRow(sourceY, row, 0, stride);

                    for (int column = 0; column < blockColumns; column++)
                    {
                        int blockLeft = originX + column * block;
                        int blockRight = Math.Min(blockLeft + block, endX);
                        int offset = blockLeft * 4;

                        for (int sourceX = blockLeft; sourceX < blockRight; sourceX++)
                        {
                            sums[column * 4] += row[offset];
                            sums[column * 4 + 1] += row[offset + 1];
                            sums[column * 4 + 2] += row[offset + 2];
                            sums[column * 4 + 3] += row[offset + 3];
                            offset += 4;
                        }

                        counts[column] += blockRight - blockLeft;
                    }
                }

                for (int column = 0; column < blockColumns; column++)
                {
                    int count = counts[column];

                    if (count <= 0)
                    {
                        continue;
                    }

                    // 四舍五入而不是截断：纯色输入必须原样还原（常值解），
                    // 截断在奇数块上会掉 1 个色阶。
                    byte blue = (byte)((sums[column * 4] + count / 2) / count);
                    byte green = (byte)((sums[column * 4 + 1] + count / 2) / count);
                    byte red = (byte)((sums[column * 4 + 2] + count / 2) / count);
                    byte alpha = (byte)((sums[column * 4 + 3] + count / 2) / count);

                    int blockLeft = originX + column * block;
                    int blockRight = Math.Min(blockLeft + block, endX);
                    int destinationOffset = (blockTop - originY) * outWidth * 4 + (blockLeft - originX) * 4;
                    int rowStep = outWidth * 4;

                    for (int destinationY = blockTop; destinationY < blockBottom; destinationY++)
                    {
                        int index = destinationOffset;

                        for (int destinationX = blockLeft; destinationX < blockRight; destinationX++)
                        {
                            output[index] = blue;
                            output[index + 1] = green;
                            output[index + 2] = red;
                            output[index + 3] = alpha;
                            index += 4;
                        }

                        destinationOffset += rowStep;
                    }
                }
            }

            return new PixelBuffer(output, outWidth, outHeight);
        }

        /// <summary>
        /// 对指定矩形做高斯模糊。
        ///
        /// 会先向外多取 <c>ceil(radius) + 2</c> 像素再模糊、最后裁回请求矩形 ——
        /// 否则区域边界会用镜像像素补位，在边界上留下一圈"假"颜色（越小的标注越明显）。
        /// </summary>
        /// <returns>处理结果；请求区域完全落在图像之外时返回 null。</returns>
        public static PixelBuffer BlurRegion(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            double radius,
            out int originX,
            out int originY,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            originX = 0;
            originY = 0;

            double effectiveRadius = ClampBlurRadius(radius);

            if (!TryGetVisibleBounds(source, x, y, width, height, out int left, out int top, out int right, out int bottom))
            {
                return null;
            }

            // 外扩量：高斯核视觉半径就是 radius（3σ），再多 2 像素留余量。
            int expand = (int)Math.Ceiling(effectiveRadius) + 2;

            int sliceLeft = Math.Max(0, left - expand);
            int sliceTop = Math.Max(0, top - expand);
            int sliceRight = Math.Min(source.Width, right + expand);
            int sliceBottom = Math.Min(source.Height, bottom + expand);

            PixelBuffer slice = CopyRegion(source, sliceLeft, sliceTop, sliceRight - sliceLeft, sliceBottom - sliceTop, cancellationToken);

            if (slice == null)
            {
                return null;
            }

            PixelBuffer blurred = new BlurFilters().GaussianBlur(slice, effectiveRadius, cancellationToken);

            // 裁回请求矩形
            originX = left;
            originY = top;

            return CopyRegion(blurred, left - sliceLeft, top - sliceTop, right - left, bottom - top, cancellationToken);
        }

        /// <summary>块的通道平均值（供自检直接核对，避免测试自己再算一遍）。</summary>
        public static int AverageChannel(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            int channel)
        {
            if (source == null || channel < 0 || channel > 3)
            {
                return 0;
            }

            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            int right = Math.Min(source.Width, x + width);
            int bottom = Math.Min(source.Height, y + height);

            if (right <= left || bottom <= top)
            {
                return 0;
            }

            byte[] row = new byte[source.Stride];
            long sum = 0;
            long count = 0;

            for (int sourceY = top; sourceY < bottom; sourceY++)
            {
                source.CopyRow(sourceY, row, 0, source.Stride);
                int offset = left * 4 + channel;

                for (int sourceX = left; sourceX < right; sourceX++)
                {
                    sum += row[offset];
                    count++;
                    offset += 4;
                }
            }

            if (count == 0)
            {
                return 0;
            }

            return (int)((sum + count / 2) / count);
        }

        private static bool TryGetVisibleBounds(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            out int left,
            out int top,
            out int right,
            out int bottom)
        {
            left = Math.Max(0, x);
            top = Math.Max(0, y);
            right = Math.Min(source.Width, x + width);
            bottom = Math.Min(source.Height, y + height);

            return right > left && bottom > top && width > 0 && height > 0;
        }

        /// <summary>取矩形区域（会收敛到图像范围内）。区域为空时返回 null。</summary>
        private static PixelBuffer CopyRegion(
            IReadOnlyPixelBuffer source,
            int x,
            int y,
            int width,
            int height,
            CancellationToken cancellationToken)
        {
            if (!TryGetVisibleBounds(source, x, y, width, height, out int left, out int top, out int right, out int bottom))
            {
                return null;
            }

            int regionWidth = right - left;
            int regionHeight = bottom - top;
            int regionStride = regionWidth * 4;

            byte[] output = new byte[regionStride * regionHeight];
            byte[] row = new byte[source.Stride];

            for (int sourceY = top; sourceY < bottom; sourceY++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                source.CopyRow(sourceY, row, 0, source.Stride);
                Buffer.BlockCopy(row, left * 4, output, (sourceY - top) * regionStride, regionStride);
            }

            return new PixelBuffer(output, regionWidth, regionHeight);
        }
    }
}
