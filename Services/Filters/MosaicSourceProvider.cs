using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.Imaging;
using PSText.Models;

namespace PSText.Services.Filters
{
    /// <summary>
    /// 遮盖类标注的"素材"来源。
    ///
    /// 抽出接口是为了让**叠加层预览与最终合并取到同一份素材** —— 这与
    /// <see cref="AnnotationVisualBuilder"/> 让两边共用同一份几何是同一个道理：
    /// 如果预览和合并各生成一份，两者的马赛克相位、模糊程度就可能不一致，
    /// 变成"所见非所得"，而这类问题几乎无法靠肉眼发现。
    /// </summary>
    public interface IMosaicSourceProvider
    {
        /// <summary>为遮盖标注生成填充笔刷；素材不可用时返回 null。</summary>
        Brush CreateCoverBrush(AnnotationObject item);
    }

    /// <summary>
    /// 按需生成遮盖素材（马赛克 / 模糊）。
    ///
    /// 关键设计：**源像素是"拉取"而不是"推送"的**。
    /// 构造时传一个取像素的委托，每次生成素材时现取现用 ——
    /// 这样就不存在"底图换了、缓存忘了失效"这一类缺陷（那是最典型的隐性 bug 来源），
    /// 也不需要在每个改动底图的地方都记得通知一次。
    ///
    /// 代价是每次生成素材都会取一次源像素缓冲；但那个缓冲在 ViewModel 里本身就有缓存
    /// （与调整预览共用），取到的是同一个对象，开销只是一次引用比较。
    /// </summary>
    public sealed class MosaicSourceProvider : IMosaicSourceProvider
    {
        private readonly Func<PixelBuffer> _sourceAccessor;

        public MosaicSourceProvider(Func<PixelBuffer> sourceAccessor)
        {
            _sourceAccessor = sourceAccessor;
        }

        public Brush CreateCoverBrush(AnnotationObject item)
        {
            if (item == null || _sourceAccessor == null)
            {
                return null;
            }

            PixelBuffer source = _sourceAccessor();

            if (source == null || source.Width < 1 || source.Height < 1)
            {
                return null;
            }

            int left = (int)Math.Floor(item.Left);
            int top = (int)Math.Floor(item.Top);
            int width = (int)Math.Ceiling(item.Width);
            int height = (int)Math.Ceiling(item.Height);

            if (width < 1 || height < 1)
            {
                return null;
            }

            int originX;
            int originY;
            PixelBuffer cover;

            if (item.MosaicStyle == MosaicStyle.Blur)
            {
                cover = MosaicFilter.BlurRegion(
                    source, left, top, width, height, item.CoverSize,
                    out originX, out originY, CancellationToken.None);
            }
            else
            {
                cover = MosaicFilter.PixelateRegion(
                    source, left, top, width, height, (int)Math.Round(item.CoverSize),
                    out originX, out originY, CancellationToken.None);
            }

            if (cover == null)
            {
                return null;
            }

            // 素材位图**必须用 96 DPI**：ImageBrush 的 Viewbox 单位是 DIP，
            // 只有 96 DPI 时 1 DIP 才正好等于 1 图像像素，Viewbox 才能直接写图像坐标。
            BitmapSource bitmap = PixelBuffer.ToBitmap(cover, 96.0, 96.0);

            if (bitmap == null)
            {
                return null;
            }

            Rect rect = new Rect(originX, originY, cover.Width, cover.Height);

            // Viewbox 与 Viewport 取**同一个矩形**、Stretch=Fill ⇒ 恒等映射。
            // 这样几何里每个点取到的都是素材上完全相同位置的像素，
            // 马赛克方块不会被二次采样糊掉，相位也与整幅图对齐。
            ImageBrush brush = new ImageBrush(bitmap)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = rect,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = rect,
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };

            brush.Freeze();

            return brush;
        }
    }
}
