using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using PSText.Infrastructure.Imaging;

namespace PSText.Services.Filters
{
    /// <summary>边框样式。</summary>
    public enum BorderStyle
    {
        /// <summary>无边框（不处理）。</summary>
        None = 0,

        /// <summary>纯色实线边框。</summary>
        Solid,

        /// <summary>内白外黑（类似冲印相片的压边效果）。</summary>
        InnerLine,

        /// <summary>柔和阴影（边框外侧渐隐）。</summary>
        Shadow
    }

    /// <summary>
    /// 边框滤镜（需求 P1-8）：在图像四周叠加预设样式的边框。
    ///
    /// 实现方式：输出画布在原图尺寸上各加一圈边距，原图居中绘制，
    /// 因此边框不会遮挡画面内容（照片类处理更符合预期）。
    /// 纯函数，不修改输入。
    /// </summary>
    public sealed class BorderFilters
    {
        /// <summary>添加边框（异步）。</summary>
        /// <param name="source">源缓冲。</param>
        /// <param name="style">边框样式。</param>
        /// <param name="width">边框宽度（像素）。</param>
        /// <param name="color">主边框颜色。</param>
        /// <param name="secondaryColor">辅助颜色（内衬 / 阴影强度）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task<PixelBuffer> ApplyAsync(
            IReadOnlyPixelBuffer source,
            BorderStyle style,
            int width,
            Color color,
            Color secondaryColor,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            return Task.Run(
                () => Apply(source, style, width, color, secondaryColor, cancellationToken),
                cancellationToken);
        }

        /// <summary>添加边框（同步纯函数）。</summary>
        public PixelBuffer Apply(
            IReadOnlyPixelBuffer source,
            BorderStyle style,
            int width,
            Color color,
            Color secondaryColor,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (style == BorderStyle.None || width <= 0)
            {
                return Copy(source, cancellationToken);
            }

            int border = width;
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;

            // 阴影样式需要额外的外圈渐隐空间。
            int extra = style == BorderStyle.Shadow ? border : 0;
            int targetWidth = sourceWidth + (border + extra) * 2;
            int targetHeight = sourceHeight + (border + extra) * 2;

            byte[] output = new byte[targetWidth * targetHeight * 4];

            // 1) 铺底色
            byte[] background = new byte[targetWidth * targetHeight * 4];
            Color fillColor = style == BorderStyle.InnerLine ? Colors.Black : color;
            FillCanvas(background, targetWidth, targetHeight, fillColor, cancellationToken);

            Buffer.BlockCopy(background, 0, output, 0, background.Length);

            // 2) 内衬（InnerLine：紧贴图像的一圈白色细线）
            if (style == BorderStyle.InnerLine && border >= 2)
            {
                DrawInsetFrame(
                    output,
                    targetWidth,
                    targetHeight,
                    border + extra,
                    2,
                    secondaryColor,
                    cancellationToken);
            }

            // 3) 阴影：外圈按距离渐隐到全透明（叠加在原底色之上）
            if (style == BorderStyle.Shadow)
            {
                DrawShadow(output, targetWidth, targetHeight, border + extra, border, secondaryColor, cancellationToken);
            }

            // 4) 原图居中绘制
            int offsetX = border + extra;
            int offsetY = border + extra;
            byte[] sourceRow = new byte[source.Stride];

            for (int y = 0; y < sourceHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                source.CopyRow(y, sourceRow, 0, source.Stride);
                Buffer.BlockCopy(
                    sourceRow,
                    0,
                    output,
                    ((y + offsetY) * targetWidth + offsetX) * 4,
                    sourceWidth * 4);
            }

            return new PixelBuffer(output, targetWidth, targetHeight);
        }

        private static void FillCanvas(byte[] canvas, int width, int height, Color color, CancellationToken cancellationToken)
        {
            byte pixelB = color.B;
            byte pixelG = color.G;
            byte pixelR = color.R;
            byte pixelA = color.A == 0 ? (byte)255 : color.A;

            for (int i = 0; i < canvas.Length; i += 4)
            {
                canvas[i] = pixelB;
                canvas[i + 1] = pixelG;
                canvas[i + 2] = pixelR;
                canvas[i + 3] = pixelA;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>在指定内缩位置画一圈指定粗细的实线框。</summary>
        private static void DrawInsetFrame(
            byte[] canvas,
            int width,
            int height,
            int inset,
            int thickness,
            Color color,
            CancellationToken cancellationToken)
        {
            int left = inset;
            int top = inset;
            int right = width - 1 - inset;
            int bottom = height - 1 - inset;

            if (left > right || top > bottom)
            {
                return;
            }

            for (int t = 0; t < thickness; t++)
            {
                int y0 = top + t;
                int y1 = bottom - t;
                int x0 = left + t;
                int x1 = right - t;

                if (y0 > y1 || x0 > x1)
                {
                    break;
                }

                SetPixel(canvas, width, x0, y0, color);
                SetPixel(canvas, width, x1, y0, color);
                SetPixel(canvas, width, x0, y1, color);
                SetPixel(canvas, width, x1, y1, color);

                for (int x = x0; x <= x1; x++)
                {
                    SetPixel(canvas, width, x, y0, color);
                    SetPixel(canvas, width, x, y1, color);
                }

                for (int y = y0; y <= y1; y++)
                {
                    SetPixel(canvas, width, x0, y, color);
                    SetPixel(canvas, width, x1, y, color);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// 阴影：在图像区域外做线性渐隐。
        /// 使用源图像的 Alpha 通道按距离衰减，效果近似柔和投影。
        /// </summary>
        private static void DrawShadow(
            byte[] canvas,
            int width,
            int height,
            int imageInset,
            int borderWidth,
            Color shadowColor,
            CancellationToken cancellationToken)
        {
            int strength = shadowColor.A == 0 ? 120 : shadowColor.A;

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 到图像区域的距离
                int distanceY = 0;

                if (y < imageInset)
                {
                    distanceY = imageInset - y;
                }
                else if (y >= height - imageInset)
                {
                    distanceY = y - (height - imageInset) + 1;
                }

                for (int x = 0; x < width; x++)
                {
                    int distanceX = 0;

                    if (x < imageInset)
                    {
                        distanceX = imageInset - x;
                    }
                    else if (x >= width - imageInset)
                    {
                        distanceX = x - (width - imageInset) + 1;
                    }

                    int distance = Math.Max(distanceX, distanceY);

                    if (distance <= 0)
                    {
                        continue;
                    }

                    // 距图像越远越淡：距离 >= borderWidth 时完全淡出
                    double ratio = 1.0 - (distance / (double)Math.Max(1, borderWidth));
                    if (ratio <= 0.0)
                    {
                        continue;
                    }

                    int alpha = (int)(strength * ratio);
                    BlendPixel(canvas, width, x, y, shadowColor, alpha);
                }
            }
        }

        private static void SetPixel(byte[] canvas, int width, int x, int y, Color color)
        {
            int index = (y * width + x) * 4;

            if (index < 0 || index + 3 >= canvas.Length)
            {
                return;
            }

            canvas[index] = color.B;
            canvas[index + 1] = color.G;
            canvas[index + 2] = color.R;
            canvas[index + 3] = color.A == 0 ? (byte)255 : color.A;
        }

        /// <summary>把颜色按其 Alpha 比例混合到画布上（简单 source-over）。</summary>
        private static void BlendPixel(byte[] canvas, int width, int x, int y, Color color, int alpha)
        {
            if (alpha <= 0)
            {
                return;
            }

            if (alpha > 255)
            {
                alpha = 255;
            }

            int index = (y * width + x) * 4;
            int inverse = 255 - alpha;

            canvas[index] = (byte)((color.B * alpha + canvas[index] * inverse) / 255);
            canvas[index + 1] = (byte)((color.G * alpha + canvas[index + 1] * inverse) / 255);
            canvas[index + 2] = (byte)((color.R * alpha + canvas[index + 2] * inverse) / 255);
            canvas[index + 3] = 255;
        }

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
