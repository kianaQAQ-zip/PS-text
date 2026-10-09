using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.Imaging;
using PSText.Models;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 把标注对象合并（烘焙）进像素。
    ///
    /// 只在两个时机用到：用户点「合并到画面」，或者执行破坏性操作（滤镜 / 裁剪 / 缩放 / 旋转 / 打印）之前。
    /// 平时标注只是一个叠加层，**不动底图像素** —— 这正是"非破坏性"的含义。
    ///
    /// 两个实现要点：
    ///   1. 几何来自 <see cref="AnnotationVisualBuilder"/>，与叠加层预览**共用同一份**，保证所见即所得；
    ///   2. 只渲染所有标注的并集包围盒，而不是整幅图 —— 标注通常只占画面一小块，
    ///      12MP 图上这样能省掉几乎全部开销。
    ///
    /// 必须在 UI 线程调用（RenderTargetBitmap 要求）。
    /// </summary>
    public static class AnnotationRenderer
    {
        /// <summary>把标注合并进一份像素拷贝；没有标注时原样返回。</summary>
        /// <param name="source">底图像素。</param>
        /// <param name="objects">标注对象列表。</param>
        /// <param name="coverProvider">
        /// 遮盖类标注的素材来源。必须与叠加层用的是**同一个实例**，
        /// 否则预览与合并的马赛克相位可能不一致（所见非所得）。
        /// </param>
        public static PixelBuffer Render(
            PixelBuffer source,
            IReadOnlyList<AnnotationObject> objects,
            IMosaicSourceProvider coverProvider = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            if (objects == null || objects.Count == 0)
            {
                return source;
            }

            int width = source.Width;
            int height = source.Height;

            // ---- 1) 求并集包围盒 ----
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            int drawable = 0;

            for (int i = 0; i < objects.Count; i++)
            {
                AnnotationObject item = objects[i];

                if (item == null)
                {
                    continue;
                }

                Rect bounds = item.VisualBounds;

                if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
                {
                    continue;
                }

                drawable++;
                minX = Math.Min(minX, bounds.Left);
                minY = Math.Min(minY, bounds.Top);
                maxX = Math.Max(maxX, bounds.Right);
                maxY = Math.Max(maxY, bounds.Bottom);
            }

            if (drawable == 0)
            {
                return source;
            }

            int left = Math.Max(0, (int)Math.Floor(minX));
            int top = Math.Max(0, (int)Math.Floor(minY));
            int right = Math.Min(width, (int)Math.Ceiling(maxX));
            int bottom = Math.Min(height, (int)Math.Ceiling(maxY));

            int regionWidth = right - left;
            int regionHeight = bottom - top;

            if (regionWidth <= 0 || regionHeight <= 0)
            {
                return source;
            }

            // ---- 2) 画到一张只覆盖包围盒的图上（96 DPI ⇒ 1 DIP = 1 像素） ----
            DrawingVisual drawingVisual = new DrawingVisual();

            using (DrawingContext context = drawingVisual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(-left, -top));

                for (int i = 0; i < objects.Count; i++)
                {
                    AnnotationObject item = objects[i];

                    if (item == null)
                    {
                        continue;
                    }

                    AnnotationVisual visual = AnnotationVisualBuilder.Build(item, coverProvider);

                    if (visual.Geometry != null && visual.Geometry != Geometry.Empty)
                    {
                        Pen pen = null;

                        if (visual.Stroke != null && visual.StrokeThickness > 0.0)
                        {
                            pen = new Pen(visual.Stroke, visual.StrokeThickness)
                            {
                                LineJoin = PenLineJoin.Round,
                                StartLineCap = PenLineCap.Round,
                                EndLineCap = PenLineCap.Round
                            };
                            pen.Freeze();
                        }

                        context.DrawGeometry(visual.Fill, pen, visual.Geometry);
                    }

                    if (visual.LabelGeometry != null)
                    {
                        context.DrawGeometry(visual.LabelBrush, null, visual.LabelGeometry);
                    }
                }

                context.Pop();
            }

            RenderTargetBitmap target = new RenderTargetBitmap(
                regionWidth,
                regionHeight,
                96.0,
                96.0,
                PixelFormats.Pbgra32);

            target.Render(drawingVisual);

            // ---- 3) 把渲染结果按 alpha 合成到源像素上 ----
            int regionStride = regionWidth * 4;
            byte[] overlay = new byte[regionStride * regionHeight];

            target.CopyPixels(
                new Int32Rect(0, 0, regionWidth, regionHeight),
                overlay,
                regionStride,
                0);

            byte[] output = source.GetPixelsCopy();
            int imageStride = width * 4;

            for (int row = 0; row < regionHeight; row++)
            {
                int overlayOffset = row * regionStride;
                int outputOffset = (top + row) * imageStride + left * 4;

                for (int column = 0; column < regionWidth; column++)
                {
                    int sourceIndex = overlayOffset + column * 4;
                    int targetIndex = outputOffset + column * 4;

                    int sourceAlpha = overlay[sourceIndex + 3];

                    if (sourceAlpha == 0)
                    {
                        continue;
                    }

                    if (sourceAlpha == 255)
                    {
                        output[targetIndex] = overlay[sourceIndex];
                        output[targetIndex + 1] = overlay[sourceIndex + 1];
                        output[targetIndex + 2] = overlay[sourceIndex + 2];
                        output[targetIndex + 3] = 255;
                        continue;
                    }

                    // 叠加层是预乘 alpha（Pbgra32），目标可能是透明图，
                    // 因此按预乘公式合成后再还原，避免半透明边缘发灰。
                    double overlayAlpha = sourceAlpha / 255.0;
                    int targetAlpha = output[targetIndex + 3];
                    double targetFactor = targetAlpha / 255.0 * (1.0 - overlayAlpha);

                    for (int channel = 0; channel < 3; channel++)
                    {
                        double premultiplied = overlay[sourceIndex + channel]
                                               + output[targetIndex + channel] * targetFactor;

                        output[targetIndex + channel] = ToByte(premultiplied);
                    }

                    double outAlpha = sourceAlpha + targetAlpha * (1.0 - overlayAlpha);
                    output[targetIndex + 3] = ToByte(outAlpha);
                }
            }

            return new PixelBuffer(output, width, height);
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
    }
}
