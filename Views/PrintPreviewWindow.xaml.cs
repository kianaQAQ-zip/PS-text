using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PSText.ViewModels;

namespace PSText.Views
{
    /// <summary>
    /// 独立打印预览窗口（需求 P2-10）。
    ///
    /// 职责边界：
    ///   - 本窗口只负责把鼠标位移回传给 ViewModel、以及触发“打印 / 取消”；
    ///     版面计算、偏移钳制、页数统计全部在 PrintPreviewViewModel 中完成；
    ///   - 预览的显示比例是纯显示行为，与实际打印尺寸无关，不影响清晰度。
    /// </summary>
    public partial class PrintPreviewWindow : Window
    {
        private bool _isDragging;
        private Point _lastMousePosition;

        public PrintPreviewWindow()
        {
            InitializeComponent();

            Loaded += OnWindowLoaded;
            SizeChanged += OnPreviewSizeChanged;
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        /// <summary>用户点击“打印”后置为 true（由调用方决定如何打印）。</summary>
        public bool PrintConfirmed { get; private set; }

        private PrintPreviewViewModel ViewModel
        {
            get { return DataContext as PrintPreviewViewModel; }
        }

        #region 生命周期

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            FitPreviewToWindow();
        }

        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            FitPreviewToWindow();
        }

        private void FitPreviewToWindow()
        {
            PrintPreviewViewModel viewModel = ViewModel;

            if (viewModel == null || PreviewScroll == null)
            {
                return;
            }

            viewModel.FitDisplayTo(PreviewScroll.ActualWidth, PreviewScroll.ActualHeight);
        }

        #endregion

        #region 拖拽与滚轮

        private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _lastMousePosition = e.GetPosition(PreviewCanvas);
            PreviewImage.CaptureMouse();
            e.Handled = true;
        }

        private void OnImageMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            PrintPreviewViewModel viewModel = ViewModel;

            if (viewModel == null)
            {
                return;
            }

            Point current = e.GetPosition(PreviewCanvas);
            double deltaX = current.X - _lastMousePosition.X;
            double deltaY = current.Y - _lastMousePosition.Y;

            _lastMousePosition = current;

            // ViewModel 负责把显示位移换算成 DIP 并钳制到合法范围
            viewModel.DragBy(deltaX, deltaY);
            e.Handled = true;
        }

        private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;

            if (PreviewImage != null && Mouse.Captured == PreviewImage)
            {
                PreviewImage.ReleaseMouseCapture();
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            PrintPreviewViewModel viewModel = ViewModel;

            if (viewModel == null || e.Delta == 0)
            {
                return;
            }

            // 预览缩放：以 1.1 为步长，锚点不精确对中（预览场景足够）
            viewModel.Scale = e.Delta > 0 ? viewModel.Scale * 1.1 : viewModel.Scale / 1.1;
            e.Handled = true;
        }

        #endregion

        #region 按钮

        private void OnPrintClick(object sender, RoutedEventArgs e)
        {
            PrintConfirmed = true;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            PrintConfirmed = false;
            DialogResult = false;
        }

        #endregion

        protected override void OnClosing(CancelEventArgs e)
        {
            // 用窗口右上角关闭时视为取消
            if (DialogResult == null)
            {
                PrintConfirmed = false;
            }

            base.OnClosing(e);
        }
    }
}
