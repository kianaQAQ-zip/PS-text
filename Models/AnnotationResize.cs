using System;

namespace PSText.Models
{
    /// <summary>标注缩放手柄的位置（四角 + 四边中点）。</summary>
    public enum AnnotationHandle
    {
        None = 0,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    /// <summary>
    /// 缩放计算（纯函数）。
    ///
    /// 单独抽出来是因为它全是"边界与钳制"的逻辑 —— 不允许翻转、最小尺寸、
    /// 保持宽高比、以中心为基准，四者叠在一起时分支很多，
    /// 混在鼠标事件处理里基本没法验证。做成纯函数后自检可以直接钉住每一条。
    ///
    /// 约定：**不允许翻转**。左边缘拖过右边缘时钳制在最小尺寸上，而不是把盒子翻过来 ——
    /// 翻转会让手柄瞬间跳到对面，手感非常糟糕，而且对箭头来说"方向反了"更是灾难。
    /// </summary>
    public static class AnnotationResizeCalculator
    {
        /// <summary>缩放下限（图像像素）。低于这个尺寸手柄会互相重叠，也没法再抓。</summary>
        public const double MinimumSize = 6.0;

        /// <summary>手柄中心到对象盒的偏移是否算命中（图像像素）。</summary>
        public const double DefaultHitTolerance = 8.0;

        /// <summary>
        /// 由原始盒 + 手柄 + 指针位置算出新的盒。
        /// </summary>
        /// <param name="x1">原始起点。</param>
        /// <param name="y1">原始起点。</param>
        /// <param name="x2">原始终点。</param>
        /// <param name="y2">原始终点。</param>
        /// <param name="handle">被拖动的手柄。</param>
        /// <param name="pointerX">指针图像坐标。</param>
        /// <param name="pointerY">指针图像坐标。</param>
        /// <param name="keepAspectRatio">保持宽高比（只对四角手柄生效）。</param>
        /// <param name="fromCenter">以中心为基准，两边对称变化。</param>
        /// <param name="left">新盒左边界。</param>
        /// <param name="top">新盒上边界。</param>
        /// <param name="right">新盒右边界。</param>
        /// <param name="bottom">新盒下边界。</param>
        /// <returns>手柄或指针非法时返回 false，调用方应保持原样。</returns>
        public static bool TryComputeBounds(
            double x1,
            double y1,
            double x2,
            double y2,
            AnnotationHandle handle,
            double pointerX,
            double pointerY,
            bool keepAspectRatio,
            bool fromCenter,
            out double left,
            out double top,
            out double right,
            out double bottom)
        {
            left = Math.Min(x1, x2);
            right = Math.Max(x1, x2);
            top = Math.Min(y1, y2);
            bottom = Math.Max(y1, y2);

            if (handle == AnnotationHandle.None)
            {
                return false;
            }

            if (double.IsNaN(pointerX) || double.IsNaN(pointerY)
                || double.IsInfinity(pointerX) || double.IsInfinity(pointerY))
            {
                return false;
            }

            double width = right - left;
            double height = bottom - top;

            bool movesLeft = handle == AnnotationHandle.TopLeft
                             || handle == AnnotationHandle.Left
                             || handle == AnnotationHandle.BottomLeft;

            bool movesRight = handle == AnnotationHandle.TopRight
                              || handle == AnnotationHandle.Right
                              || handle == AnnotationHandle.BottomRight;

            bool movesTop = handle == AnnotationHandle.TopLeft
                            || handle == AnnotationHandle.Top
                            || handle == AnnotationHandle.TopRight;

            bool movesBottom = handle == AnnotationHandle.BottomLeft
                               || handle == AnnotationHandle.Bottom
                               || handle == AnnotationHandle.BottomRight;

            if (fromCenter)
            {
                // 以中心为基准：手柄在左还是在右都不重要，只看"离中心多远"。
                // 这样两边对称变化，天然不会出现翻转，也不需要在别处再兜一次最小尺寸。
                double centerX = (left + right) / 2.0;
                double centerY = (top + bottom) / 2.0;

                if (movesLeft || movesRight)
                {
                    double half = Math.Max(Math.Abs(pointerX - centerX), MinimumSize / 2.0);
                    left = centerX - half;
                    right = centerX + half;
                }

                if (movesTop || movesBottom)
                {
                    double half = Math.Max(Math.Abs(pointerY - centerY), MinimumSize / 2.0);
                    top = centerY - half;
                    bottom = centerY + half;
                }
            }
            else
            {
                if (movesLeft)
                {
                    left = Math.Min(pointerX, right - MinimumSize);
                }
                else if (movesRight)
                {
                    right = Math.Max(pointerX, left + MinimumSize);
                }

                if (movesTop)
                {
                    top = Math.Min(pointerY, bottom - MinimumSize);
                }
                else if (movesBottom)
                {
                    bottom = Math.Max(pointerY, top + MinimumSize);
                }
            }

            bool isCorner = (movesLeft || movesRight) && (movesTop || movesBottom);

            if (keepAspectRatio && isCorner && width > 0.5 && height > 0.5)
            {
                // 固定"对角"那个角，按原始宽高比推导另一个方向。
                // 对边中点没有宽高比可言，所以只对角生效。
                double aspect = width / height;
                double anchorX = movesLeft ? right : left;
                double anchorY = movesTop ? bottom : top;

                double candidateWidth = Math.Abs(pointerX - anchorX);
                double candidateHeight = Math.Abs(pointerY - anchorY);

                if (candidateWidth / aspect > candidateHeight)
                {
                    candidateHeight = candidateWidth / aspect;
                }
                else
                {
                    candidateWidth = candidateHeight * aspect;
                }

                candidateWidth = Math.Max(candidateWidth, MinimumSize);
                candidateHeight = Math.Max(candidateHeight, MinimumSize);

                if (movesLeft)
                {
                    left = anchorX - candidateWidth;
                }
                else
                {
                    right = anchorX + candidateWidth;
                }

                if (movesTop)
                {
                    top = anchorY - candidateHeight;
                }
                else
                {
                    bottom = anchorY + candidateHeight;
                }
            }

            // 兜底：极端输入（退化盒 + 手写指针）下仍要保证不塌陷
            if (right - left < MinimumSize)
            {
                if (movesRight)
                {
                    right = left + MinimumSize;
                }
                else
                {
                    left = right - MinimumSize;
                }
            }

            if (bottom - top < MinimumSize)
            {
                if (movesBottom)
                {
                    bottom = top + MinimumSize;
                }
                else
                {
                    top = bottom - MinimumSize;
                }
            }

            return true;
        }

        /// <summary>取手柄在盒上的坐标（用于绘制与命中测试）。</summary>
        public static void GetHandlePoint(
            AnnotationHandle handle,
            double left,
            double top,
            double right,
            double bottom,
            out double x,
            out double y)
        {
            double centerX = (left + right) / 2.0;
            double centerY = (top + bottom) / 2.0;

            switch (handle)
            {
                case AnnotationHandle.TopLeft:
                    x = left;
                    y = top;
                    return;
                case AnnotationHandle.Top:
                    x = centerX;
                    y = top;
                    return;
                case AnnotationHandle.TopRight:
                    x = right;
                    y = top;
                    return;
                case AnnotationHandle.Right:
                    x = right;
                    y = centerY;
                    return;
                case AnnotationHandle.BottomRight:
                    x = right;
                    y = bottom;
                    return;
                case AnnotationHandle.Bottom:
                    x = centerX;
                    y = bottom;
                    return;
                case AnnotationHandle.BottomLeft:
                    x = left;
                    y = bottom;
                    return;
                default:
                    x = left;
                    y = centerY;
                    return;
            }
        }

        /// <summary>八个手柄（按顺时针排列，便于自检与界面按固定顺序渲染）。</summary>
        public static AnnotationHandle[] AllHandles()
        {
            return new[]
            {
                AnnotationHandle.TopLeft,
                AnnotationHandle.Top,
                AnnotationHandle.TopRight,
                AnnotationHandle.Right,
                AnnotationHandle.BottomRight,
                AnnotationHandle.Bottom,
                AnnotationHandle.BottomLeft,
                AnnotationHandle.Left
            };
        }

        /// <summary>鼠标悬停在该手柄上时应显示的指针形状名（供界面切换光标）。</summary>
        public static string GetCursorName(AnnotationHandle handle)
        {
            switch (handle)
            {
                case AnnotationHandle.TopLeft:
                case AnnotationHandle.BottomRight:
                    return "SizeNWSE";
                case AnnotationHandle.TopRight:
                case AnnotationHandle.BottomLeft:
                    return "SizeNESW";
                case AnnotationHandle.Top:
                case AnnotationHandle.Bottom:
                    return "SizeNS";
                case AnnotationHandle.Left:
                case AnnotationHandle.Right:
                    return "SizeWE";
                default:
                    return null;
            }
        }
    }
}
