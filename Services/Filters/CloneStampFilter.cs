using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>笔迹上的一个采样点（图像像素坐标）。这里不依赖任何 WPF 类型，便于自检直接构造。</summary>
    public struct StampPoint
    {
        public double X;
        public double Y;

        public StampPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>一个矩形区域（像素坐标）。</summary>
    public struct PixelRegion
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public bool IsEmpty
        {
            get { return Width <= 0 || Height <= 0; }
        }
    }

    /// <summary>
    /// 仿制图章：把"源位置"的像素搬到"笔迹位置"，用于纹理背景上手动修补。
    ///
    /// 智能填充（调和扩散）在纯色 / 渐变背景上效果最好，但它本身是**平滑**的，
    /// 遇到草地、织物、人脸这类纹理就会留下平滑斑块 —— 那正是这个工具存在的理由：
    /// 由人来指定"拿哪一块纹理来补"。
    ///
    /// 三个关键实现点：
    ///   1. **覆盖率按"像素到笔迹折线的距离"算，而不是逐个笔刷点叠加**。
    ///      同一点上叠加多次会让颜色越涂越浓（累积），而距离场天然只算一次最大值。
    ///   2. **源像素一律取自原始图像**，不读已经涂过的结果，否则会出现"自我复制"的拖影。
    ///   3. 源位置由**固定偏移**决定（源点 - 起笔点），所以一笔之内是纯粹的平移搬运，
    ///      不会因为笔迹弯曲而扭曲纹理。
    ///
    /// 只有真正被涂到的像素才会改变，因此返回的包围盒可以用来做区域历史（而不是整幅快照）。
    /// </summary>
    public static class CloneStampFilter
    {
        /// <summary>按笔迹涂抹（同步纯函数）。</summary>
        /// <param name="source">原始图像（源像素从这里取，绝不读输出）。</param>
        /// <param name="points">笔迹采样点（至少 1 个）。</param>
        /// <param name="radius">笔刷半径（像素）。</param>
        /// <param name="hardness">硬度 0~1：1 = 硬边，越小边缘过渡越软。</param>
        /// <param name="offsetX">源位置相对笔迹位置的偏移。</param>
        /// <param name="offsetY">同上。</param>
        /// <param name="painted">输出：实际被涂到的包围盒（未被涂到时为 Width = 0）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public static PixelBuffer Stamp(
            IReadOnlyPixelBuffer source,
            IReadOnlyList<StampPoint> points,
            double radius,
            double hardness,
            int offsetX,
            int offsetY,
            out PixelRegion painted,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            painted = new PixelRegion();

            int width = source.Width;
            int height = source.Height;
            byte[] sourcePixels = source.GetPixels();
            byte[] output = new byte[sourcePixels.Length];
            Buffer.BlockCopy(sourcePixels, 0, output, 0, sourcePixels.Length);

            if (points == null || points.Count == 0)
            {
                return new PixelBuffer(output, width, height);
            }

            double brushRadius = radius < 0.5 ? 0.5 : radius;
            double innerRatio = Clamp01(hardness);

            // ---- 1) 笔迹包围盒（外扩一个半径） ----
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                double x = points[i].X;
                double y = points[i].Y;

                if (x < minX)
                {
                    minX = x;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }

            int left = (int)Math.Floor(minX - brushRadius);
            int top = (int)Math.Floor(minY - brushRadius);
            int right = (int)Math.Ceiling(maxX + brushRadius);
            int bottom = (int)Math.Ceiling(maxY + brushRadius);

            if (left < 0)
            {
                left = 0;
            }

            if (top < 0)
            {
                top = 0;
            }

            if (right > width - 1)
            {
                right = width - 1;
            }

            if (bottom > height - 1)
            {
                bottom = height - 1;
            }

            if (right < left || bottom < top)
            {
                return new PixelBuffer(output, width, height);
            }

            double radiusSquared = brushRadius * brushRadius;
            int paintedLeft = int.MaxValue;
            int paintedTop = int.MaxValue;
            int paintedRight = -1;
            int paintedBottom = -1;

            // ---- 2) 逐像素计算到笔迹的距离，按覆盖率把源像素混合进来 ----
            for (int y = top; y <= bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rowOffset = y * width;

                for (int x = left; x <= right; x++)
                {
                    double distance = DistanceToPolyline(points, x + 0.5, y + 0.5);

                    if (distance > brushRadius)
                    {
                        continue;
                    }

                    double coverage = Coverage(distance / brushRadius, innerRatio);

                    if (coverage <= 0.0)
                    {
                        continue;
                    }

                    int sourceX = x + offsetX;
                    int sourceY = y + offsetY;

                    // 源越界的位置不涂（避免把画布外的"空白"搬进来）
                    if (sourceX < 0 || sourceY < 0 || sourceX >= width || sourceY >= height)
                    {
                        continue;
                    }

                    int targetIndex = (rowOffset + x) * 4;
                    int sourceIndex = (sourceY * width + sourceX) * 4;
                    bool changed = false;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        double original = output[targetIndex + channel];
                        double replacement = sourcePixels[sourceIndex + channel];
                        int blended = ToByte(original + (replacement - original) * coverage);

                        if (blended != output[targetIndex + channel])
                        {
                            output[targetIndex + channel] = (byte)blended;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        if (x < paintedLeft)
                        {
                            paintedLeft = x;
                        }

                        if (x > paintedRight)
                        {
                            paintedRight = x;
                        }

                        if (y < paintedTop)
                        {
                            paintedTop = y;
                        }

                        if (y > paintedBottom)
                        {
                            paintedBottom = y;
                        }
                    }
                }
            }

            if (paintedRight >= paintedLeft && paintedBottom >= paintedTop)
            {
                painted = new PixelRegion
                {
                    X = paintedLeft,
                    Y = paintedTop,
                    Width = paintedRight - paintedLeft + 1,
                    Height = paintedBottom - paintedTop + 1
                };
            }

            return new PixelBuffer(output, width, height);
        }

        /// <summary>异步版本。</summary>
        public static Task<PixelBuffer> StampAsync(
            IReadOnlyPixelBuffer source,
            IReadOnlyList<StampPoint> points,
            double radius,
            double hardness,
            int offsetX,
            int offsetY,
            Action<PixelRegion> onPainted,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(
                () =>
                {
                    PixelRegion painted;
                    PixelBuffer result = Stamp(
                        source, points, radius, hardness, offsetX, offsetY, out painted, cancellationToken);

                    if (onPainted != null)
                    {
                        onPainted(painted);
                    }

                    return result;
                },
                cancellationToken);
        }

        /// <summary>点到笔迹折线的最短距离；只有一个点时退化为点到点距离。</summary>
        private static double DistanceToPolyline(IReadOnlyList<StampPoint> points, double x, double y)
        {
            if (points.Count == 1)
            {
                double dx = x - points[0].X;
                double dy = y - points[0].Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }

            double best = double.MaxValue;

            for (int i = 0; i < points.Count - 1; i++)
            {
                double distance = DistanceToSegment(x, y, points[i], points[i + 1]);

                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        /// <summary>点到线段的最短距离。</summary>
        private static double DistanceToSegment(double x, double y, StampPoint a, StampPoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;

            if (lengthSquared <= 1e-12)
            {
                double px = x - a.X;
                double py = y - a.Y;
                return Math.Sqrt(px * px + py * py);
            }

            // 把点投影到线段上，参数 t 夹到 [0, 1]
            double t = ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared;

            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            double closestX = a.X + t * dx;
            double closestY = a.Y + t * dy;
            double ex = x - closestX;
            double ey = y - closestY;

            return Math.Sqrt(ex * ex + ey * ey);
        }

        /// <summary>
        /// 覆盖率：比率 &lt;= inner 时满覆盖，之后用 smoothstep 平滑过渡到 0。
        /// smoothstep 而不是线性，是为了让笔刷边缘看起来自然（没有可见的硬圈）。
        /// </summary>
        private static double Coverage(double ratio, double inner)
        {
            if (ratio <= inner)
            {
                return 1.0;
            }

            if (ratio >= 1.0)
            {
                return 0.0;
            }

            double t = (ratio - inner) / (1.0 - inner);
            double smooth = t * t * (3.0 - 2.0 * t);

            return 1.0 - smooth;
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value))
            {
                return 0.0;
            }

            if (value < 0.0)
            {
                return 0.0;
            }

            return value > 1.0 ? 1.0 : value;
        }

        private static int ToByte(double value)
        {
            int rounded = (int)Math.Round(value);

            if (rounded <= 0)
            {
                return 0;
            }

            return rounded >= 255 ? 255 : rounded;
        }
    }
}
