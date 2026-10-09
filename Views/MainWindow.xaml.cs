using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PSText.Infrastructure.Behaviors;
using PSText.ViewModels;

namespace PSText.Views
{
    /// <summary>
    /// 主窗口代码后置。
    ///
    /// 职责边界（严格遵守“XAML 使用 DataBinding，code-behind 不直接操作 UI 控件”）：
    ///   - 这里只做三件事：把视口尺寸 / 屏幕 DPI 回传给 ViewModel、转发键盘与鼠标手势、处理文件拖放；
    ///   - 不设置任何控件的外观属性，不做业务逻辑，所有状态都在 ViewModel 中；
    ///   - 缩放与平移由 CanvasInteraction 行为通过附加属性双向绑定 ZoomFactor / IsHandToolActive。
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _spaceKeyDown;

        public MainWindow()
        {
            InitializeComponent();

            Loaded += OnWindowLoaded;
            DpiChanged += OnWindowDpiChanged;
            PreviewKeyDown += OnWindowPreviewKeyDown;
            PreviewKeyUp += OnWindowPreviewKeyUp;
            PreviewMouseLeftButtonDown += OnWindowPreviewMouseLeftButtonDown;
            DragOver += OnWindowDragOver;
            Drop += OnWindowDrop;
        }

        /// <summary>强类型访问 ViewModel（仅用于调用回传方法，不做 UI 操作）。</summary>
        private MainViewModel ViewModel
        {
            get { return DataContext as MainViewModel; }
        }

        #region 视口 / DPI 回传

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            ReportScreenDpi();
            ReportViewportSize();

            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.NotifyInitialized();
            }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ReportViewportSize();
        }

        /// <summary>
        /// 画布加载完成：把键盘焦点交给 ScrollViewer，使滚轮缩放无需先点一下画布。
        /// 同时挂接画布交互行为（附加属性在 XAML 中已绑定）。
        /// </summary>
        private void OnCanvasScrollViewerLoaded(object sender, RoutedEventArgs e)
        {
            CanvasInteraction.Attach(CanvasScrollViewer);

            ReportViewportSize();

            if (CanvasScrollViewer != null)
            {
                CanvasScrollViewer.Focus();
            }
        }

        private void OnWindowDpiChanged(object sender, DpiChangedEventArgs e)
        {
            // 每显示器 DPI（Windows 10+）：跨屏拖动后重新计算“原始大小”。
            ReportScreenDpi();
        }

        /// <summary>把画布视口尺寸回传 ViewModel（适应窗口缩放需要）。</summary>
        private void ReportViewportSize()
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || CanvasScrollViewer == null)
            {
                return;
            }

            viewModel.UpdateViewportSize(CanvasScrollViewer.ActualWidth, CanvasScrollViewer.ActualHeight);
        }

        /// <summary>把当前屏幕 DPI 回传 ViewModel。</summary>
        private void ReportScreenDpi()
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                viewModel.UpdateScreenDpi(dpi.PixelsPerInchX, dpi.PixelsPerInchY);
            }
            catch (Exception)
            {
                // 极端情况下（无渲染设备）退回 96 DPI。
                viewModel.UpdateScreenDpi(96.0, 96.0);
            }
        }

        #endregion

        #region 键盘手势

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null)
            {
                return;
            }

            bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            // Ctrl 组合键始终生效（即使在文本框里）
            if (control)
            {
                switch (e.Key)
                {
                    case Key.Z:
                        viewModel.Undo();
                        e.Handled = true;
                        return;

                    case Key.Y:
                        viewModel.Redo();
                        e.Handled = true;
                        return;

                    case Key.S:
                        if (shift)
                        {
                            ExecuteCommand(viewModel.SaveAsCommand);
                        }
                        else
                        {
                            ExecuteCommand(viewModel.SaveCommand);
                        }

                        e.Handled = true;
                        return;

                    case Key.O:
                        ExecuteCommand(viewModel.OpenCommand);
                        e.Handled = true;
                        return;

                    case Key.P:
                        ExecuteCommand(viewModel.PrintPreviewCommand);
                        e.Handled = true;
                        return;

                    case Key.T:
                        viewModel.ToggleTheme();
                        e.Handled = true;
                        return;

                    case Key.R:
                        viewModel.RefreshRecentFileExistence();
                        StatusHint("已刷新最近文件状态");
                        e.Handled = true;
                        return;

                    case Key.D0:
                    case Key.NumPad0:
                        viewModel.FitToWindow();
                        e.Handled = true;
                        return;

                    case Key.D1:
                    case Key.NumPad1:
                        viewModel.ActualSize();
                        e.Handled = true;
                        return;

                    case Key.Add:
                    case Key.OemPlus:
                        viewModel.ZoomIn();
                        e.Handled = true;
                        return;

                    case Key.Subtract:
                    case Key.OemMinus:
                        viewModel.ZoomOut();
                        e.Handled = true;
                        return;

                    default:
                        return;
                }
            }

            // 以下是无修饰键的快捷方式：在文本框里输入时必须让路，
            // 否则输入文字时按空格会变成抓手、按 +/- 会变成缩放（需求 P3-14）。
            if (IsTextInputFocused())
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Space:
                    // 空格：临时切换抓手工具
                    if (!_spaceKeyDown)
                    {
                        _spaceKeyDown = true;
                        viewModel.SetHandTool(true);
                    }

                    e.Handled = true;
                    break;

                case Key.Add:
                case Key.OemPlus:
                    viewModel.ZoomIn();
                    e.Handled = true;
                    break;

                case Key.Subtract:
                case Key.OemMinus:
                    viewModel.ZoomOut();
                    e.Handled = true;
                    break;

                case Key.D0:
                case Key.NumPad0:
                    viewModel.FitToWindow();
                    e.Handled = true;
                    break;

                case Key.D1:
                case Key.NumPad1:
                    viewModel.ActualSize();
                    e.Handled = true;
                    break;

                case Key.Left:
                    ScrollCanvas(-PanStep, 0);
                    e.Handled = true;
                    break;

                case Key.Right:
                    ScrollCanvas(PanStep, 0);
                    e.Handled = true;
                    break;

                case Key.Up:
                    ScrollCanvas(0, -PanStep);
                    e.Handled = true;
                    break;

                case Key.Down:
                    ScrollCanvas(0, PanStep);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    if (viewModel.IsCropping)
                    {
                        ExecuteCommand(viewModel.CancelCropCommand);
                        e.Handled = true;
                    }

                    break;

                default:
                    break;
            }
        }

        /// <summary>方向键平移的步长（DIP）。</summary>
        private const double PanStep = 40.0;

        /// <summary>当前焦点是否在文本输入控件里（用于让出无修饰键快捷方式）。</summary>
        private bool IsTextInputFocused()
        {
            try
            {
                IInputElement focused = Keyboard.FocusedElement;

                if (focused == null)
                {
                    return false;
                }

                if (focused is System.Windows.Controls.TextBox
                    || focused is System.Windows.Controls.PasswordBox
                    || focused is System.Windows.Controls.Primitives.TextBoxBase)
                {
                    return true;
                }

                // 可编辑的组合框
                System.Windows.Controls.ComboBox comboBox = focused as System.Windows.Controls.ComboBox;

                if (comboBox != null && comboBox.IsEditable)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 焦点查询失败时按“不在输入框”处理（宁可让快捷键生效）
            }

            return false;
        }

        /// <summary>执行命令（不可执行时给出状态提示，避免快捷键“像坏了”）。</summary>
        private void ExecuteCommand(System.Windows.Input.ICommand command)
        {
            if (command == null)
            {
                return;
            }

            if (command.CanExecute(null))
            {
                command.Execute(null);
                return;
            }

            StatusHint("当前状态下该操作不可用");
        }

        private void StatusHint(string message)
        {
            MainViewModel viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.ShowHint(message);
            }
        }

        /// <summary>方向键平移画布。</summary>
        private void ScrollCanvas(double deltaX, double deltaY)
        {
            if (CanvasScrollViewer == null)
            {
                return;
            }

            CanvasScrollViewer.ScrollToHorizontalOffset(CanvasScrollViewer.HorizontalOffset + deltaX);
            CanvasScrollViewer.ScrollToVerticalOffset(CanvasScrollViewer.VerticalOffset + deltaY);
        }

        /// <summary>菜单“退出”。</summary>
        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 关闭前的未保存确认：只有拿到明确许可才真正关闭。
        ///
        /// 为什么要拦两次：Closing 是同步事件，而“保存”必须是异步的（编码走线程池）。
        /// 因此先 Cancel 掉这次关闭，问完用户后再置 _closeApproved 并调用 Close() 放行。
        /// 决策逻辑在 ViewModel.ConfirmCloseAsync（那里才知道 IsDirty 与保存语义），
        /// 这里只负责拦截与放行，符合 code-behind 不写业务逻辑的约定。
        /// </summary>
        private bool _closeApproved;

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (e.Cancel || _closeApproved)
            {
                return;
            }

            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsDirty)
            {
                return;
            }

            e.Cancel = true;

            // 说明：这是除 Drop 之外第二处允许的 async void —— lambda 内已用 try/catch 全包，
            // 异常不会逃逸到 UI 线程。
            Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (await viewModel.ConfirmCloseAsync())
                    {
                        _closeApproved = true;
                        Close();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[MainWindow] 关闭确认失败: " + ex);
                }
            });
        }

        private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
            {
                return;
            }

            _spaceKeyDown = false;

            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.SetHandTool(false);
            }

            e.Handled = true;
        }

        #endregion

        #region 双击切换缩放模式

        private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2)
            {
                return;
            }

            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.HasDocument)
            {
                return;
            }

            // 修补 / 标注模式下双击是在框选或绘制，不应该顺带切换缩放
            if (viewModel.IsRetouchMode || viewModel.IsAnnotationMode)
            {
                return;
            }

            // 双击画布：适应窗口 ⇄ 原始大小
            if (viewModel.IsFitToWindow)
            {
                viewModel.ActualSize();
            }
            else
            {
                viewModel.FitToWindow();
            }

            e.Handled = true;
        }

        #endregion

        #region 颜色预设（仅转发给 ViewModel，不在此处保存状态）

        private void OnBorderColorWhite(object sender, RoutedEventArgs e)
        {
            SetBorderColor(Colors.White);
        }

        private void OnBorderColorBlack(object sender, RoutedEventArgs e)
        {
            SetBorderColor(Colors.Black);
        }

        private void OnBorderColorGray(object sender, RoutedEventArgs e)
        {
            SetBorderColor(Color.FromRgb(0xD9, 0xD9, 0xD9));
        }

        private void OnBorderColorCream(object sender, RoutedEventArgs e)
        {
            SetBorderColor(Color.FromRgb(0xF5, 0xEF, 0xE0));
        }

        private void SetBorderColor(Color color)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.BorderColor = color;
            }
        }

        private void OnTextColorWhite(object sender, RoutedEventArgs e)
        {
            SetTextColor(Colors.White);
        }

        private void OnTextColorBlack(object sender, RoutedEventArgs e)
        {
            SetTextColor(Colors.Black);
        }

        private void OnTextColorYellow(object sender, RoutedEventArgs e)
        {
            SetTextColor(Color.FromRgb(0xFF, 0xD5, 0x2E));
        }

        private void OnTextColorRed(object sender, RoutedEventArgs e)
        {
            SetTextColor(Color.FromRgb(0xE0, 0x3A, 0x3A));
        }

        private void SetTextColor(Color color)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.TextColor = color;
            }
        }

        #endregion

        #region 批量步骤的颜色预设（同样只转发）

        private void OnBatchBorderColorWhite(object sender, RoutedEventArgs e)
        {
            SetBatchBorderColor(Colors.White);
        }

        private void OnBatchBorderColorBlack(object sender, RoutedEventArgs e)
        {
            SetBatchBorderColor(Colors.Black);
        }

        private void OnBatchBorderColorGray(object sender, RoutedEventArgs e)
        {
            SetBatchBorderColor(Color.FromRgb(0xD9, 0xD9, 0xD9));
        }

        private void OnBatchBorderColorCream(object sender, RoutedEventArgs e)
        {
            SetBatchBorderColor(Color.FromRgb(0xF5, 0xEF, 0xE0));
        }

        private void SetBatchBorderColor(Color color)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.SetBatchBorderColor(color);
            }
        }

        private void OnBatchWatermarkColorWhite(object sender, RoutedEventArgs e)
        {
            SetBatchWatermarkColor(Colors.White);
        }

        private void OnBatchWatermarkColorBlack(object sender, RoutedEventArgs e)
        {
            SetBatchWatermarkColor(Colors.Black);
        }

        private void OnBatchWatermarkColorGray(object sender, RoutedEventArgs e)
        {
            SetBatchWatermarkColor(Color.FromRgb(0xC0, 0xC0, 0xC0));
        }

        private void OnBatchWatermarkColorYellow(object sender, RoutedEventArgs e)
        {
            SetBatchWatermarkColor(Color.FromRgb(0xFF, 0xD5, 0x2E));
        }

        private void OnBatchWatermarkColorRed(object sender, RoutedEventArgs e)
        {
            SetBatchWatermarkColor(Color.FromRgb(0xE0, 0x3A, 0x3A));
        }

        private void SetBatchWatermarkColor(Color color)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.SetBatchWatermarkColor(color);
            }
        }

        #endregion

        #region 裁剪框交互

        /// <summary>
        /// 裁剪遮罩层与图像 1:1（整体随缩放放大），因此鼠标坐标就是图像像素坐标，
        /// 但 Canvas 的 ActualWidth 是“未缩放”的布局尺寸，需要按 ZoomFactor 反算。
        /// </summary>
        private bool TryGetCropImagePoint(MouseEventArgs e, out double imageX, out double imageY)
        {
            imageX = 0.0;
            imageY = 0.0;

            MainViewModel viewModel = ViewModel;
            if (viewModel == null || CropOverlay == null)
            {
                return false;
            }

            double zoom = viewModel.ZoomFactor;
            if (zoom <= 0.0)
            {
                return false;
            }

            Point position = e.GetPosition(CropOverlay);
            imageX = position.X / zoom;
            imageY = position.Y / zoom;
            return true;
        }

        private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsCropping)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetCropImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            string handle = viewModel.HitTestCropHandle(imageX, imageY);
            viewModel.BeginCropDrag(handle, imageX, imageY);
            CropOverlay.CaptureMouse();
            e.Handled = true;
        }

        private void OnCropMouseMove(object sender, MouseEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsCropping)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetCropImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                viewModel.UpdateCropDrag(imageX, imageY);
                e.Handled = true;
                return;
            }

            // 未拖动时给出光标提示，便于发现手柄
            string handle = viewModel.HitTestCropHandle(imageX, imageY);
            CropOverlay.Cursor = GetCropCursor(handle);
        }

        private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel != null)
            {
                viewModel.EndCropDrag();
            }

            if (CropOverlay != null && Mouse.Captured == CropOverlay)
            {
                CropOverlay.ReleaseMouseCapture();
            }
        }

        private static Cursor GetCropCursor(string handle)
        {
            switch (handle)
            {
                case "nw":
                case "se":
                    return Cursors.SizeNWSE;
                case "ne":
                case "sw":
                    return Cursors.SizeNESW;
                case "n":
                case "s":
                    return Cursors.SizeNS;
                case "w":
                case "e":
                    return Cursors.SizeWE;
                default:
                    return Cursors.SizeAll;
            }
        }

        #endregion

        #region 修补标记交互

        /// <summary>
        /// 把鼠标位置换算成图像像素坐标。
        /// 修补标记层与图像 1:1（整体随 ZoomFactor 缩放），所以只要除以缩放比即可。
        /// </summary>
        private bool TryGetRetouchImagePoint(MouseEventArgs e, out double imageX, out double imageY)
        {
            imageX = 0.0;
            imageY = 0.0;

            MainViewModel viewModel = ViewModel;
            if (viewModel == null || RetouchOverlay == null)
            {
                return false;
            }

            double zoom = viewModel.ZoomFactor;
            if (zoom <= 0.0)
            {
                return false;
            }

            Point position = e.GetPosition(RetouchOverlay);
            imageX = position.X / zoom;
            imageY = position.Y / zoom;
            return true;
        }

        private void OnRetouchMouseDown(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsRetouchMode)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetRetouchImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            // Alt 在仿制图章下表示“取源”。修饰键的判断放在这里（View 的本职工作就是转发手势），
            // 至于“取源还是涂抹”由 ViewModel 按当前工具决定。
            bool setSourcePoint = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            viewModel.BeginRetouchGesture(imageX, imageY, setSourcePoint);
            RetouchOverlay.CaptureMouse();
            e.Handled = true;
        }

        private void OnRetouchMouseMove(object sender, MouseEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsRetouchMode)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetRetouchImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            // 笔刷光标要跟随鼠标，所以未按下时也要回传位置
            viewModel.UpdateRetouchCursor(imageX, imageY);

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            viewModel.UpdateRetouchGesture(imageX, imageY);
            e.Handled = true;
        }

        private void OnRetouchMouseUp(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.EndRetouchGesture();
            }

            if (RetouchOverlay != null && Mouse.Captured == RetouchOverlay)
            {
                RetouchOverlay.ReleaseMouseCapture();
            }
        }

        private void OnRetouchMouseLeave(object sender, MouseEventArgs e)
        {
            MainViewModel viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.HideRetouchCursor();
            }
        }

        #endregion

        #region 标注交互

        /// <summary>把鼠标位置换算成图像像素坐标（标注层与图像 1:1，随 ZoomFactor 缩放）。</summary>
        private bool TryGetAnnotationImagePoint(MouseEventArgs e, out double imageX, out double imageY)
        {
            imageX = 0.0;
            imageY = 0.0;

            MainViewModel viewModel = ViewModel;
            if (viewModel == null || AnnotationOverlay == null)
            {
                return false;
            }

            double zoom = viewModel.ZoomFactor;
            if (zoom <= 0.0)
            {
                return false;
            }

            Point position = e.GetPosition(AnnotationOverlay);
            imageX = position.X / zoom;
            imageY = position.Y / zoom;
            return true;
        }

        private void OnAnnotationMouseDown(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsAnnotationMode)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetAnnotationImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            viewModel.BeginAnnotationGesture(imageX, imageY);
            AnnotationOverlay.CaptureMouse();
            e.Handled = true;
        }

        private void OnAnnotationMouseMove(object sender, MouseEventArgs e)
        {
            MainViewModel viewModel = ViewModel;
            if (viewModel == null || !viewModel.IsAnnotationMode)
            {
                return;
            }

            double imageX;
            double imageY;

            if (!TryGetAnnotationImagePoint(e, out imageX, out imageY))
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // 悬停：让指针形状反映"这里能拖角改尺寸"。
                // 只把坐标转过去，具体显示什么光标由 ViewModel 决定（外观不在这里设置）。
                viewModel.UpdateAnnotationCursor(imageX, imageY);
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;

            viewModel.UpdateAnnotationGesture(
                imageX,
                imageY,
                (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift,
                (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt);

            e.Handled = true;
        }

        private void OnAnnotationMouseUp(object sender, MouseButtonEventArgs e)
        {
            MainViewModel viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.EndAnnotationGesture();
            }

            if (AnnotationOverlay != null && Mouse.Captured == AnnotationOverlay)
            {
                AnnotationOverlay.ReleaseMouseCapture();
            }
        }

        private void OnAnnotationColorRed(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Color.FromRgb(0xE2, 0x4B, 0x4A));
        }

        private void OnAnnotationColorYellow(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Color.FromRgb(0xEF, 0x9F, 0x27));
        }

        private void OnAnnotationColorGreen(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Color.FromRgb(0x1D, 0x9E, 0x75));
        }

        private void OnAnnotationColorBlue(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Color.FromRgb(0x37, 0x8A, 0xDD));
        }

        private void OnAnnotationColorWhite(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Colors.White);
        }

        private void OnAnnotationColorBlack(object sender, RoutedEventArgs e)
        {
            SetAnnotationColor(Colors.Black);
        }

        /// <summary>
        /// 设置标注颜色。若当前有选中的标注，ViewModel 会立刻把新颜色应用上去
        /// （这正是“选中再改参数”的用法）。
        /// </summary>
        private void SetAnnotationColor(Color color)
        {
            MainViewModel viewModel = ViewModel;

            if (viewModel != null)
            {
                viewModel.AnnotationColor = color;
            }
        }

        #endregion

        #region 文件拖放

        private void OnWindowDragOver(object sender, DragEventArgs e)
        {
            e.Effects = TryGetDroppedImage(e.Data) == null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void OnWindowDrop(object sender, DragEventArgs e)
        {
            // 说明：这是唯一允许的 async void —— 事件处理器签名要求且已用 try/catch 全包，
            // 内部 await 的是 Task 形式的 ViewModel 方法，不存在异常逃逸。
            try
            {
                string filePath = TryGetDroppedImage(e.Data);
                MainViewModel viewModel = ViewModel;

                if (filePath == null || viewModel == null)
                {
                    return;
                }

                await viewModel.LoadFromPathAsync(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MainWindow] 拖放加载失败: " + ex);
            }
        }

        /// <summary>从拖放数据中取出第一个受支持的图片路径。</summary>
        private static string TryGetDroppedImage(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            string[] files = data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                return null;
            }

            List<string> supported = new List<string>();
            foreach (string file in files)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                try
                {
                    if (System.IO.File.Exists(file)
                        && Models.ImageFileFormatHelper.FromPath(file) != Models.ImageFileFormat.Unknown)
                    {
                        supported.Add(file);
                    }
                }
                catch (ArgumentException)
                {
                    // 非法路径忽略。
                }
            }

            return supported.Count == 0 ? null : supported[0];
        }

        #endregion
    }
}
