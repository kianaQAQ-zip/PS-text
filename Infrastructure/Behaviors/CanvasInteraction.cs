using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PSText.Infrastructure.Behaviors
{
    /// <summary>
    /// 画布交互行为（附加属性，可复用、可绑定）：
    ///   - 滚轮缩放，以鼠标位置为锚点，范围 0.1x ~ 10x
    ///   - 拖拽平移（抓手工具 / 鼠标中键）
    ///   - 双击切换 适应窗口 / 原始大小
    ///   - 缩放与平移状态双向绑定到 ViewModel
    ///
    /// 实现要点：ScrollViewer 的滚动范围由内容（Container）决定，容器尺寸 = 原始像素 × 缩放，
    /// 因此平移直接复用 ScrollViewer 的滚动偏移，滚动条可见性交给 WPF，无需手工计算虚拟范围。
    /// </summary>
    public static class CanvasInteraction
    {
        /// <summary>缩放下限。</summary>
        public const double MinZoom = 0.1;

        /// <summary>缩放上限。</summary>
        public const double MaxZoom = 10.0;

        #region Zoom 附加属性

        public static readonly DependencyProperty ZoomProperty = DependencyProperty.RegisterAttached(
            "Zoom",
            typeof(double),
            typeof(CanvasInteraction),
            new FrameworkPropertyMetadata(
                1.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnZoomChanged,
                CoerceZoom));

        public static double GetZoom(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            return (double)element.GetValue(ZoomProperty);
        }

        public static void SetZoom(DependencyObject element, double value)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            element.SetValue(ZoomProperty, value);
        }

        private static object CoerceZoom(DependencyObject d, object baseValue)
        {
            double value = (double)baseValue;

            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                return 1.0;
            }

            return Math.Max(MinZoom, Math.Min(MaxZoom, value));
        }

        #endregion

        #region HandTool 附加属性

        public static readonly DependencyProperty HandToolProperty = DependencyProperty.RegisterAttached(
            "HandTool",
            typeof(bool),
            typeof(CanvasInteraction),
            new FrameworkPropertyMetadata(false, OnHandToolChanged));

        public static bool GetHandTool(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            return (bool)element.GetValue(HandToolProperty);
        }

        public static void SetHandTool(DependencyObject element, bool value)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            element.SetValue(HandToolProperty, value);
        }

        #endregion

        #region ImagePixelWidth / ImagePixelHeight（容器尺寸计算依据）

        public static readonly DependencyProperty ImagePixelWidthProperty = DependencyProperty.RegisterAttached(
            "ImagePixelWidth",
            typeof(double),
            typeof(CanvasInteraction),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static double GetImagePixelWidth(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            return (double)element.GetValue(ImagePixelWidthProperty);
        }

        public static void SetImagePixelWidth(DependencyObject element, double value)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            element.SetValue(ImagePixelWidthProperty, value);
        }

        public static readonly DependencyProperty ImagePixelHeightProperty = DependencyProperty.RegisterAttached(
            "ImagePixelHeight",
            typeof(double),
            typeof(CanvasInteraction),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static double GetImagePixelHeight(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            return (double)element.GetValue(ImagePixelHeightProperty);
        }

        public static void SetImagePixelHeight(DependencyObject element, double value)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            element.SetValue(ImagePixelHeightProperty, value);
        }

        #endregion

        #region 事件挂接

        private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ScrollViewer viewer = d as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            // 由代码直接改 Zoom 时，锚点位于视图中心（例如“适应窗口”“+/-”按钮）。
            SetPendingAnchor(viewer, viewer.ViewportWidth / 2.0, viewer.ViewportHeight / 2.0);
            ScheduleApplyAnchor(viewer);
        }

        private static void OnHandToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ScrollViewer viewer = d as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            viewer.Cursor = (bool)e.NewValue ? Cursors.Hand : Cursors.Arrow;
        }

        private static void EnsureHooked(ScrollViewer viewer)
        {
            if (viewer == null || viewer.GetValue(HookedProperty) is bool && (bool)viewer.GetValue(HookedProperty))
            {
                return;
            }

            viewer.SetValue(HookedProperty, true);
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.PreviewMouseDown += OnPreviewMouseDown;
            viewer.PreviewMouseMove += OnPreviewMouseMove;
            viewer.PreviewMouseUp += OnPreviewMouseUp;
            viewer.LostMouseCapture += OnLostMouseCapture;
            viewer.SizeChanged += OnSizeChanged;
        }

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked",
            typeof(bool),
            typeof(CanvasInteraction),
            new PropertyMetadata(false));

        /// <summary>在 XAML 中调用一次即可完成挂接（例如 ScrollViewer 上的 Loaded 事件）。</summary>
        public static void Attach(ScrollViewer viewer)
        {
            EnsureHooked(viewer);
        }

        #endregion

        #region 交互处理

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null || e.Delta == 0)
            {
                return;
            }

            EnsureHooked(viewer);

            double current = GetZoom(viewer);
            // 每一格滚轮乘/除 1.15，手感接近 Photoshop。
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            double target = Math.Max(MinZoom, Math.Min(MaxZoom, current * factor));

            if (Math.Abs(target - current) < 1e-9)
            {
                e.Handled = true;
                return;
            }

            Point position = e.GetPosition(viewer);

            // 先记录锚点，再改 Zoom：Zoom 变化会触发 OnZoomChanged 覆盖锚点，因此这里顺序相反。
            SetZoomInternal(viewer, target, position.X, position.Y);
            e.Handled = true;
        }

        /// <summary>
        /// 内部设置缩放并绑定到指定锚点，避免 OnZoomChanged 使用视图中心锚点。
        /// </summary>
        private static void SetZoomInternal(ScrollViewer viewer, double target, double anchorX, double anchorY)
        {
            _suppressAnchorReset = true;
            try
            {
                SetPendingAnchor(viewer, anchorX, anchorY);
                SetZoom(viewer, target);
            }
            finally
            {
                _suppressAnchorReset = false;
            }

            ScheduleApplyAnchor(viewer);
        }

        private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            EnsureHooked(viewer);

            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
            {
                // 双击：适应窗口 / 原始大小 由 View 通过命令处理，这里不拦截。
                return;
            }

            bool panRequested = e.ChangedButton == MouseButton.Middle
                                || (e.ChangedButton == MouseButton.Left && GetHandTool(viewer));

            if (!panRequested)
            {
                return;
            }

            _dragStart = e.GetPosition(viewer);
            _dragStartOffsetX = viewer.HorizontalOffset;
            _dragStartOffsetY = viewer.VerticalOffset;
            _isPanning = true;
            viewer.Cursor = Cursors.SizeAll;
            viewer.CaptureMouse();
            e.Handled = true;
        }

        private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null || !_isPanning || e.MiddleButton != MouseButtonState.Pressed && !GetHandTool(viewer))
            {
                // 拖动过程中若按键已松开（例如丢事件），立即结束平移。
                if (_isPanning && e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
                {
                    EndPan(viewer);
                }

                return;
            }

            Point current = e.GetPosition(viewer);
            double deltaX = current.X - _dragStart.X;
            double deltaY = current.Y - _dragStart.Y;

            // 拖动方向与内容移动方向一致（抓手语义）。
            viewer.ScrollToHorizontalOffset(_dragStartOffsetX - deltaX);
            viewer.ScrollToVerticalOffset(_dragStartOffsetY - deltaY);
            e.Handled = true;
        }

        private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            EndPan(viewer);
        }

        private static void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            EndPan(viewer);
        }

        private static void EndPan(ScrollViewer viewer)
        {
            if (!_isPanning)
            {
                return;
            }

            _isPanning = false;

            if (viewer != null)
            {
                if (Mouse.Captured == viewer)
                {
                    viewer.ReleaseMouseCapture();
                }

                viewer.Cursor = GetHandTool(viewer) ? Cursors.Hand : Cursors.Arrow;
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScrollViewer viewer = sender as ScrollViewer;
            if (viewer == null)
            {
                return;
            }

            // 视口变化会改变可滚动范围，需要重新把锚点对齐，避免内容整体跑偏。
            SetPendingAnchor(viewer, viewer.ViewportWidth / 2.0, viewer.ViewportHeight / 2.0);
            ScheduleApplyAnchor(viewer);
        }

        #endregion

        #region 锚点对齐

        private static readonly DependencyProperty PendingAnchorProperty = DependencyProperty.RegisterAttached(
            "PendingAnchor",
            typeof(Point),
            typeof(CanvasInteraction),
            new PropertyMetadata(new Point(double.NaN, double.NaN)));

        private static readonly DependencyProperty AnchorScheduledProperty = DependencyProperty.RegisterAttached(
            "AnchorScheduled",
            typeof(bool),
            typeof(CanvasInteraction),
            new PropertyMetadata(false));

        private static bool _suppressAnchorReset;
        private static bool _isPanning;
        private static Point _dragStart;
        private static double _dragStartOffsetX;
        private static double _dragStartOffsetY;

        private static void SetPendingAnchor(ScrollViewer viewer, double x, double y)
        {
            if (viewer == null)
            {
                return;
            }

            if (_suppressAnchorReset)
            {
                return;
            }

            viewer.SetValue(PendingAnchorProperty, new Point(x, y));
        }

        private static void ScheduleApplyAnchor(ScrollViewer viewer)
        {
            if (viewer == null)
            {
                return;
            }

            if (viewer.GetValue(AnchorScheduledProperty) is bool && (bool)viewer.GetValue(AnchorScheduledProperty))
            {
                return;
            }

            viewer.SetValue(AnchorScheduledProperty, true);
            viewer.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    viewer.SetValue(AnchorScheduledProperty, false);
                    ApplyAnchor(viewer);
                }));
        }

        /// <summary>
        /// 把“锚点（视口坐标）”映射回“内容坐标”并保持不动。
        /// newOffset = scrollablePoint − anchor，最后交给 ScrollViewer 自行夹取到合法范围。
        /// </summary>
        private static void ApplyAnchor(ScrollViewer viewer)
        {
            if (viewer == null || !viewer.IsLoaded)
            {
                return;
            }

            Point anchor = (Point)viewer.GetValue(PendingAnchorProperty);
            if (double.IsNaN(anchor.X) || double.IsNaN(anchor.Y))
            {
                return;
            }

            double extentWidth = viewer.ExtentWidth;
            double extentHeight = viewer.ExtentHeight;
            double viewportWidth = viewer.ViewportWidth;
            double viewportHeight = viewer.ViewportHeight;

            if (extentWidth <= 0.0 || extentHeight <= 0.0 || viewportWidth <= 0.0 || viewportHeight <= 0.0)
            {
                return;
            }

            // 内容在视口中的左上角坐标（内容比视口小时水平/垂直居中）。
            double originX = Math.Min(0.0, (viewportWidth - extentWidth) / 2.0);
            double originY = Math.Min(0.0, (viewportHeight - extentHeight) / 2.0);

            double scrollableX = anchor.X - originX;
            double scrollableY = anchor.Y - originY;

            double offsetX = scrollableX - anchor.X;
            double offsetY = scrollableY - anchor.Y;

            double maxX = Math.Max(0.0, extentWidth - viewportWidth);
            double maxY = Math.Max(0.0, extentHeight - viewportHeight);

            viewer.ScrollToHorizontalOffset(Clamp(offsetX, 0.0, maxX));
            viewer.ScrollToVerticalOffset(Clamp(offsetY, 0.0, maxY));
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        #endregion
    }
}
