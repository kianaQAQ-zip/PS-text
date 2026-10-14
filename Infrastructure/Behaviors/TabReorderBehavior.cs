using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PSText.ViewModels;

namespace PSText.Infrastructure.Behaviors
{
    /// <summary>
    /// 标签栏拖动排序（附加属性）。
    ///
    /// 两个必须处理好的点，否则这个功能会变成负优化：
    ///
    ///   1. **位移阈值**。标签本身是个按钮，按下就切标签。若一按下就进入拖动，
    ///      "点一下切标签"会变成"拖了一下没动"——用户会以为点击失灵。
    ///      所以只有横向位移超过阈值才认作拖动。
    ///
    ///   2. **抢走按钮的鼠标捕获**。按钮在按下时会自己捕获鼠标，松手时触发 Click。
    ///      越过阈值的那一刻把捕获抢到标签栏上，按钮就收不到这次 Click 了 ——
    ///      于是"拖完松手"不会顺手激活那个标签。这次拖动只做排序，不该改变当前文档。
    ///
    /// 拖动过程只负责算出**目标下标**，真正的移动交给 ViewModel
    /// （<c>MainViewModel.MoveSession</c>）—— 这样移动语义可以被无头自检直接覆盖，
    /// 不必去模拟鼠标。
    /// </summary>
    public static class TabReorder
    {
        /// <summary>认作拖动所需的最小横向位移（像素）。</summary>
        public const double DragThreshold = 4.0;

        /// <summary>拖出标签栏多远算"放弃"（像素）。留一点余量避免手抖误判。</summary>
        private const double LeaveMargin = 24.0;

        #region IsEnabled 附加属性

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TabReorder),
            new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            return (bool)element.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject element, bool value)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            element.SetValue(IsEnabledProperty, value);
        }

        #endregion

        #region 内部状态

        private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
            "State",
            typeof(DragState),
            typeof(TabReorder),
            new PropertyMetadata(null));

        private sealed class DragState
        {
            public int SourceIndex = -1;
            public Point StartPoint;
            public bool IsDragging;
            public InsertionMarkerAdorner Marker;
            public bool HadCapture;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ItemsControl itemsControl = d as ItemsControl;

            if (itemsControl == null)
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                itemsControl.PreviewMouseMove += OnPreviewMouseMove;
                itemsControl.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
                itemsControl.LostMouseCapture += OnLostMouseCapture;
            }
            else
            {
                itemsControl.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                itemsControl.PreviewMouseMove -= OnPreviewMouseMove;
                itemsControl.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
                itemsControl.LostMouseCapture -= OnLostMouseCapture;
                CleanUp(itemsControl);
            }
        }

        #endregion

        #region 拖动流程

        private static DragState GetState(ItemsControl itemsControl, bool create)
        {
            DragState state = (DragState)itemsControl.GetValue(StateProperty);

            if (state == null && create)
            {
                state = new DragState();
                itemsControl.SetValue(StateProperty, state);
            }

            return state;
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ItemsControl itemsControl = (ItemsControl)sender;
            int index = IndexOfContainer(e.OriginalSource as DependencyObject, itemsControl);

            if (index < 0)
            {
                // 点在标签栏的空白处（或滚动条上）——不参与排序
                return;
            }

            DragState state = GetState(itemsControl, true);
            state.SourceIndex = index;
            state.StartPoint = e.GetPosition(itemsControl);
            state.IsDragging = false;

            // 这里刻意**不**捕获鼠标：捕获了就吃掉了按钮的点击，点标签切不过去。
        }

        private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            ItemsControl itemsControl = (ItemsControl)sender;
            DragState state = GetState(itemsControl, false);

            if (state == null || state.SourceIndex < 0)
            {
                return;
            }

            Point point = e.GetPosition(itemsControl);

            if (!state.IsDragging)
            {
                if (Math.Abs(point.X - state.StartPoint.X) < DragThreshold)
                {
                    return;
                }

                BeginDrag(itemsControl, state);
            }

            UpdateMarker(itemsControl, state, point);
        }

        private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ItemsControl itemsControl = (ItemsControl)sender;
            DragState state = GetState(itemsControl, false);

            if (state == null || state.SourceIndex < 0)
            {
                return;
            }

            if (state.IsDragging)
            {
                Point point = e.GetPosition(itemsControl);
                int slot = SlotAtPoint(itemsControl, point);

                CleanUp(itemsControl);

                if (slot >= 0)
                {
                    MoveItem(itemsControl, state.SourceIndex, slot);
                }

                // 松手后不要再让按钮收到这次 Click —— 拖动只排序，不改当前文档。
                e.Handled = true;
            }

            state.SourceIndex = -1;
            state.IsDragging = false;
        }

        private static void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            ItemsControl itemsControl = (ItemsControl)sender;
            DragState state = GetState(itemsControl, false);

            if (state != null && state.IsDragging)
            {
                CleanUp(itemsControl);
                state.SourceIndex = -1;
                state.IsDragging = false;
            }
        }

        private static void BeginDrag(ItemsControl itemsControl, DragState state)
        {
            state.IsDragging = true;
            state.HadCapture = Mouse.Captured == itemsControl;

            // 抢走按钮的鼠标捕获：这样松手时按钮不会触发 Click。
            Mouse.Capture(itemsControl, CaptureMode.Element);
            itemsControl.Cursor = Cursors.SizeWE;

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(itemsControl);

            if (layer != null)
            {
                state.Marker = new InsertionMarkerAdorner(itemsControl);
                layer.Add(state.Marker);
            }
        }

        private static void UpdateMarker(ItemsControl itemsControl, DragState state, Point point)
        {
            if (state.Marker == null)
            {
                return;
            }

            int slot = SlotAtPoint(itemsControl, point);

            // 拖出标签栏：不画指示线，松手就是取消（标签回原位）
            state.Marker.MoveTo(slot < 0 ? double.NaN : SlotMarkerX(itemsControl, slot));
        }

        private static void CleanUp(ItemsControl itemsControl)
        {
            DragState state = GetState(itemsControl, false);

            if (state != null && state.Marker != null)
            {
                AdornerLayer layer = AdornerLayer.GetAdornerLayer(itemsControl);

                if (layer != null)
                {
                    layer.Remove(state.Marker);
                }

                state.Marker = null;
            }

            itemsControl.ClearValue(FrameworkElement.CursorProperty);

            if (Mouse.Captured == itemsControl)
            {
                Mouse.Capture(null);
            }
        }

        private static void MoveItem(ItemsControl itemsControl, int sourceIndex, int slot)
        {
            MainViewModel viewModel = itemsControl.DataContext as MainViewModel;

            if (viewModel == null)
            {
                return;
            }

            int target = ResolveTargetIndex(sourceIndex, slot);

            if (target < 0 || target == sourceIndex)
            {
                return;
            }

            viewModel.MoveSession(sourceIndex, target);
        }

        #endregion

        #region 下标换算（纯函数，自检直接覆盖）

        /// <summary>
        /// 把"插入到第 slot 条之前"换算成 <c>ObservableCollection.Move</c> 的目标下标。
        ///
        /// 两者不是一回事：`Move` 是"先摘出来再插进去"，往前拖时源项自己占了一个位置，
        /// 所以要减 1。这一步算错的表现是"往右拖一格只动了半格"这种很别扭的手感，
        /// 而且不容易一眼看出来 —— 单独拎出来做成纯函数，自检逐条钉住。
        /// </summary>
        /// <param name="sourceIndex">被拖动项当前下标。</param>
        /// <param name="slot">插入位置：0 表示插到最前面，等于项数表示插到最后。</param>
        /// <returns>目标下标；无法判断时返回 -1。</returns>
        public static int ResolveTargetIndex(int sourceIndex, int slot)
        {
            if (sourceIndex < 0 || slot < 0)
            {
                return -1;
            }

            int target = slot;

            if (target > sourceIndex)
            {
                target--;
            }

            return target < 0 ? -1 : target;
        }

        /// <summary>指针位置对应插入到第几条之前（0..项数）；拖出标签栏返回 -1。</summary>
        internal static int SlotAtPoint(ItemsControl itemsControl, Point point)
        {
            if (itemsControl == null)
            {
                return -1;
            }

            int count = itemsControl.Items.Count;

            if (count == 0)
            {
                return -1;
            }

            Size size = itemsControl.RenderSize;

            // 拖出标签栏（上下左右任一个方向）就算放弃 —— 松手时标签回原位
            if (point.X < -LeaveMargin || point.X > size.Width + LeaveMargin
                || point.Y < -LeaveMargin || point.Y > size.Height + LeaveMargin)
            {
                return -1;
            }

            for (int i = 0; i < count; i++)
            {
                FrameworkElement container = ContainerAt(itemsControl, i);

                if (container == null)
                {
                    continue;
                }

                Point topLeft = container.TranslatePoint(new Point(0, 0), itemsControl);
                double center = topLeft.X + container.ActualWidth / 2.0;

                if (point.X < center)
                {
                    return i;
                }
            }

            return count;
        }

        /// <summary>插入指示线应当画在哪个 x 坐标（相对标签栏）。</summary>
        private static double SlotMarkerX(ItemsControl itemsControl, int slot)
        {
            int count = itemsControl.Items.Count;

            if (count == 0)
            {
                return 0.0;
            }

            if (slot >= count)
            {
                FrameworkElement last = ContainerAt(itemsControl, count - 1);
                return last == null ? 0.0 : last.TranslatePoint(new Point(last.ActualWidth, 0), itemsControl).X;
            }

            FrameworkElement container = ContainerAt(itemsControl, slot);
            return container == null ? 0.0 : container.TranslatePoint(new Point(0, 0), itemsControl).X;
        }

        private static FrameworkElement ContainerAt(ItemsControl itemsControl, int index)
        {
            if (index < 0 || index >= itemsControl.Items.Count)
            {
                return null;
            }

            return itemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        }

        private static int IndexOfContainer(DependencyObject source, ItemsControl itemsControl)
        {
            DependencyObject current = source;

            while (current != null && !ReferenceEquals(current, itemsControl))
            {
                ContentPresenter presenter = current as ContentPresenter;

                if (presenter != null)
                {
                    int index = itemsControl.ItemContainerGenerator.IndexFromContainer(presenter);

                    if (index >= 0)
                    {
                        return index;
                    }
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return -1;
        }

        #endregion

        /// <summary>
        /// 插入位置指示线。
        ///
        /// 用 Adorner 而不是往可视树里塞一个矩形：Adorner 画在独立的层上，
        /// 既不会被布局挤压，也不会因为标签栏滚动而错位。
        /// </summary>
        private sealed class InsertionMarkerAdorner : Adorner
        {
            private static readonly Pen MarkerPen = CreatePen();

            private double _x = double.NaN;

            public InsertionMarkerAdorner(UIElement adornedElement)
                : base(adornedElement)
            {
                IsHitTestVisible = false;
            }

            public void MoveTo(double x)
            {
                _x = x;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                if (double.IsNaN(_x) || drawingContext == null)
                {
                    return;
                }

                double height = AdornedElement.RenderSize.Height;

                drawingContext.DrawLine(MarkerPen, new Point(_x, 1.0), new Point(_x, height - 1.0));
            }

            private static Pen CreatePen()
            {
                Brush brush = new SolidColorBrush(Color.FromRgb(0x2F, 0x7D, 0xD1));
                brush.Freeze();

                Pen pen = new Pen(brush, 2.0);
                pen.Freeze();

                return pen;
            }
        }
    }
}
