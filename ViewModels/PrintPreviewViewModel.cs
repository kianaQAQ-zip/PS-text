using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PSText.Infrastructure;
using PSText.Services.Printing;

namespace PSText.ViewModels
{
    /// <summary>
    /// 打印预览窗口的 ViewModel（需求 P2-10）。
    ///
    /// 职责：
    ///   - 持有版面计算所需的全部输入（纸张、硬边距、图像像素与 DPI、布局模式、缩放、偏移）
    ///   - 把版面结果换算成"预览显示坐标"供界面绑定
    ///   - 提供拖拽 / 缩放 / 切换布局的操作入口（View 只负责回传像素位移，不做业务判断）
    ///
    /// 预览显示的缩放是独立的显示比例，与实际打印尺寸无关，因此不影响打印清晰度。
    /// </summary>
    public sealed class PrintPreviewViewModel : ObservableObject
    {
        private readonly PrintViewState _viewState = new PrintViewState();
        private readonly Size _paperSizeDips;
        private readonly Thickness _hardMarginDips;
        private readonly int _imagePixelWidth;
        private readonly int _imagePixelHeight;
        private readonly double _imageDpiX;
        private readonly double _imageDpiY;

        private PrintLayout _layout;
        private double _displayScale = 1.0;

        public PrintPreviewViewModel(
            BitmapSource bitmap,
            double imageDpiX,
            double imageDpiY,
            Size paperSizeDips,
            Thickness hardMarginDips,
            string printerName)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            Bitmap = bitmap;
            _imagePixelWidth = bitmap.PixelWidth;
            _imagePixelHeight = bitmap.PixelHeight;
            _imageDpiX = imageDpiX > 0.5 ? imageDpiX : 96.0;
            _imageDpiY = imageDpiY > 0.5 ? imageDpiY : 96.0;
            _paperSizeDips = paperSizeDips;
            _hardMarginDips = hardMarginDips;
            PrinterName = printerName;

            SetLayoutModeCommand = new RelayCommand<PrintLayoutMode>(mode => LayoutMode = mode);
            ZoomInCommand = new RelayCommand(() => Zoom(1.1), () => Scale < 8.0);
            ZoomOutCommand = new RelayCommand(() => Zoom(1.0 / 1.1), () => Scale > 0.05);
            ResetCommand = new RelayCommand(Reset);

            Recalculate();
        }

        #region 输入

        /// <summary>待打印位图。</summary>
        public BitmapSource Bitmap { get; private set; }

        /// <summary>打印机名称（显示用）。</summary>
        public string PrinterName { get; private set; }

        /// <summary>纸张尺寸（DIP）。</summary>
        public Size PaperSizeDips
        {
            get { return _paperSizeDips; }
        }

        #endregion

        #region 可调状态

        /// <summary>布局模式。</summary>
        public PrintLayoutMode LayoutMode
        {
            get { return _viewState.Mode; }
            set
            {
                if (_viewState.Mode == value)
                {
                    return;
                }

                _viewState.Mode = value;
                Reset();
                OnPropertyChanged("LayoutMode");
            }
        }

        /// <summary>用户缩放系数。</summary>
        public double Scale
        {
            get { return _viewState.Scale; }
            set
            {
                double clamped = value;

                if (double.IsNaN(clamped) || double.IsInfinity(clamped) || clamped <= 0.0)
                {
                    clamped = 1.0;
                }

                if (clamped < 0.05)
                {
                    clamped = 0.05;
                }

                if (clamped > 8.0)
                {
                    clamped = 8.0;
                }

                if (Math.Abs(_viewState.Scale - clamped) < 1e-6)
                {
                    return;
                }

                _viewState.Scale = clamped;

                // 缩放后原来的偏移可能不合法，重新钳制一次
                Recalculate();
                OnPropertyChanged("Scale");
                OnPropertyChanged("ScaleText");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>缩放百分比文本。</summary>
        public string ScaleText
        {
            get { return (Scale * 100.0).ToString("0") + "%"; }
        }

        /// <summary>预览显示比例（相对纸张 1:1 的显示缩放，可设置以便按窗口大小调整）。</summary>
        public double DisplayScale
        {
            get { return _displayScale; }
            set
            {
                // 兜底，避免出现 0 或 NaN 导致预览消失
                double clamped = value;

                if (double.IsNaN(clamped) || double.IsInfinity(clamped) || clamped <= 0.001)
                {
                    clamped = 0.5;
                }

                if (clamped > 4.0)
                {
                    clamped = 4.0;
                }

                if (SetProperty(ref _displayScale, clamped, "DisplayScale"))
                {
                    OnPropertyChanged("DisplayPaperWidth");
                    OnPropertyChanged("DisplayPaperHeight");
                    OnPropertyChanged("DisplayPaperThickness");
                    OnPropertyChanged("DisplayImageBounds");
                    OnPropertyChanged("DisplayPrintableBounds");
                }
            }
        }

        #endregion

        #region 数据

        /// <summary>版面计算结果。</summary>
        public PrintLayout Layout
        {
            get { return _layout; }
            private set
            {
                if (SetProperty(ref _layout, value, "Layout"))
                {
                    OnPropertyChanged("DisplayImageBounds");
                    OnPropertyChanged("DisplayPrintableBounds");
                    OnPropertyChanged("PageCount");
                    OnPropertyChanged("PageText");
                    OnPropertyChanged("EffectiveDpiText");
                    OnPropertyChanged("IsClipped");
                    OnPropertyChanged("WarningText");
                }
            }
        }

        /// <summary>供界面显示的纸张宽度（已按显示比例换算）。</summary>
        public double DisplayPaperWidth
        {
            get { return _paperSizeDips.Width * _displayScale; }
        }

        /// <summary>供界面显示的纸张高度。</summary>
        public double DisplayPaperHeight
        {
            get { return _paperSizeDips.Height * _displayScale; }
        }

        /// <summary>纸张描边粗细（细线随缩放保持可见）。</summary>
        public double DisplayPaperThickness
        {
            get { return 1.0; }
        }

        /// <summary>图像在预览画布上的矩形（显示坐标）。</summary>
        public Rect DisplayImageBounds
        {
            get
            {
                if (_layout == null || _layout.ImageBounds.IsEmpty)
                {
                    return Rect.Empty;
                }

                return new Rect(
                    _layout.ImageBounds.X * _displayScale,
                    _layout.ImageBounds.Y * _displayScale,
                    _layout.ImageBounds.Width * _displayScale,
                    _layout.ImageBounds.Height * _displayScale);
            }
        }

        /// <summary>可打印区域在预览画布上的矩形（显示坐标）。</summary>
        public Rect DisplayPrintableBounds
        {
            get
            {
                if (_layout == null)
                {
                    return Rect.Empty;
                }

                return new Rect(
                    _layout.PrintableArea.X * _displayScale,
                    _layout.PrintableArea.Y * _displayScale,
                    _layout.PrintableArea.Width * _displayScale,
                    _layout.PrintableArea.Height * _displayScale);
            }
        }

        /// <summary>页数。</summary>
        public int PageCount
        {
            get { return _layout == null ? 1 : _layout.PageCount; }
        }

        /// <summary>页数文本。</summary>
        public string PageText
        {
            get
            {
                if (_layout == null)
                {
                    return "1 页";
                }

                if (_layout.PageCount <= 1)
                {
                    return "1 页";
                }

                return string.Format(
                    "{0} 页（{1} × {2}）",
                    _layout.PageCount,
                    _layout.PageColumns,
                    _layout.PageRows);
            }
        }

        /// <summary>实际打印分辨率文本（用于提示模糊风险）。</summary>
        public string EffectiveDpiText
        {
            get
            {
                if (_layout == null)
                {
                    return "—";
                }

                return string.Format("{0:0} × {1:0} DPI", _layout.EffectiveDpiX, _layout.EffectiveDpiY);
            }
        }

        /// <summary>图像是否被纸张裁剪。</summary>
        public bool IsClipped
        {
            get { return _layout != null && _layout.IsClipped; }
        }

        /// <summary>提示文本（裁剪 / 分辨率过低）。</summary>
        public string WarningText
        {
            get
            {
                if (_layout == null)
                {
                    return string.Empty;
                }

                string warning = string.Empty;

                if (_layout.PageCount > 1)
                {
                    warning = "图像超出可打印区域，将分多页打印。";
                }
                else if (_layout.IsClipped)
                {
                    warning = "图像超出纸张范围，超出部分不会打印。";
                }

                // 低于 150 DPI 时打印通常能看出模糊
                if (_layout.EffectiveDpiX > 0.0 && _layout.EffectiveDpiX < 150.0)
                {
                    string resolution = string.Format(
                        "当前打印分辨率约 {0:0} DPI，低于 150 DPI 可能偏模糊；可缩小打印尺寸或降低图片尺寸。",
                        _layout.EffectiveDpiX);

                    warning = string.IsNullOrEmpty(warning) ? resolution : warning + " " + resolution;
                }

                return warning;
            }
        }

        #endregion

        #region 命令

        public ICommand SetLayoutModeCommand { get; private set; }

        public ICommand ZoomInCommand { get; private set; }

        public ICommand ZoomOutCommand { get; private set; }

        public ICommand ResetCommand { get; private set; }

        #endregion

        #region 操作

        /// <summary>按显示比例把预览整体缩放到窗口大小。</summary>
        public void FitDisplayTo(double availableWidth, double availableHeight)
        {
            if (availableWidth <= 10.0 || availableHeight <= 10.0)
            {
                return;
            }

            double margin = 48.0;
            double scaleX = (availableWidth - margin) / Math.Max(1.0, _paperSizeDips.Width);
            double scaleY = (availableHeight - margin) / Math.Max(1.0, _paperSizeDips.Height);
            double scale = Math.Min(scaleX, scaleY);

            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.01)
            {
                scale = 0.5;
            }

            DisplayScale = Math.Min(scale, 2.0);
        }

        /// <summary>
        /// 应用一次拖拽。
        /// </summary>
        /// <param name="displayDeltaX">鼠标在预览画布上的横向位移（显示坐标）。</param>
        /// <param name="displayDeltaY">纵向位移。</param>
        public void DragBy(double displayDeltaX, double displayDeltaY)
        {
            if (_layout == null || _displayScale <= 0.001)
            {
                return;
            }

            // 显示坐标 → DIP
            double dipDeltaX = displayDeltaX / _displayScale;
            double dipDeltaY = displayDeltaY / _displayScale;

            _viewState.OffsetX += dipDeltaX;
            _viewState.OffsetY += dipDeltaY;

            Recalculate();
        }

        /// <summary>重置到当前布局模式的默认状态。</summary>
        public void Reset()
        {
            _viewState.Reset(_viewState.Mode);
            Recalculate();
            OnPropertyChanged("Scale");
            OnPropertyChanged("ScaleText");
        }

        private void Zoom(double factor)
        {
            Scale = _viewState.Scale * factor;
        }

        /// <summary>重新计算版面并通知所有派生属性。</summary>
        private void Recalculate()
        {
            Layout = PrintLayout.Create(
                _viewState,
                _paperSizeDips,
                _hardMarginDips,
                _imagePixelWidth,
                _imagePixelHeight,
                _imageDpiX,
                _imageDpiY);
        }

        #endregion
    }
}
