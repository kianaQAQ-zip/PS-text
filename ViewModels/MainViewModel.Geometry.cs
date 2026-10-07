using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using PSText.Infrastructure;
using PSText.Services.Filters;

namespace PSText.ViewModels
{
    /// <summary>图像尺寸的输入单位。</summary>
    public enum ResizeUnit
    {
        /// <summary>像素（最常用）。</summary>
        Pixels = 0,

        /// <summary>百分比（100% = 原尺寸）。</summary>
        Percent = 1,

        /// <summary>厘米：按图片自身 DPI 换算，因此"打印出来"的物理尺寸是准的。</summary>
        Centimeters = 2
    }

    /// <summary>任意角度旋转后空白角的填充方式。</summary>
    public enum RotationFill
    {
        /// <summary>透明（另存为 PNG 时可保留）。</summary>
        Transparent = 0,

        White = 1,

        Black = 2,

        Gray = 3
    }

    /// <summary>
    /// MainViewModel 的「尺寸与旋转」部分（partial，对应 M1）。
    ///
    /// 覆盖两项能力：
    ///   1. **图像大小**：像素 / 百分比 / 厘米三种单位、锁定宽高比、三档插值方式；
    ///   2. **任意角度旋转**：-180°~180°、四档空白角填充、可自动裁掉空白角。
    ///
    /// 两点设计约定：
    ///   - 单位换算的"真值"始终是**像素**。界面上的值只是同一份像素尺寸在不同单位下的表达，
    ///     因此切换单位不会悄悄改变实际目标尺寸（先换算回像素，再按新单位重新表达）。
    ///   - 校验（尺寸是否可用）放在这里而不是滤镜里：滤镜抛异常会弹"执行失败"对话框，
    ///     而超限属于可预期的用户输入，应该表现为"应用按钮不可用 + 一行说明"。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>输入值下限（避免 0 或负数）。</summary>
        private const double MinResizeValue = 0.1;

        /// <summary>输入值上限（像素单位下即最大像素数；真正的闸门是 Resampler.IsValidSize）。</summary>
        private const double MaxResizeValue = 100000.0;

        private ResizeUnit _resizeUnit = ResizeUnit.Pixels;
        private double _resizeTargetWidth = 100.0;
        private double _resizeTargetHeight = 100.0;
        private bool _lockAspectRatio = true;
        private ResampleKernel _resizeKernel = ResampleKernel.Bicubic;
        private int _lastSyncedPixelWidth;
        private int _lastSyncedPixelHeight;

        private double _rotationAngle;
        private RotationFill _rotationFill = RotationFill.Transparent;
        private bool _rotationCropToInscribed = true;

        #region 命令

        /// <summary>按当前参数应用尺寸缩放。</summary>
        public ICommand ApplyResizeCommand { get; private set; }

        /// <summary>快捷设置缩放百分比（25 / 50 / 100 / 200）。</summary>
        public ICommand ApplyResizePresetCommand { get; private set; }

        /// <summary>把目标尺寸恢复为原图尺寸（100%）。</summary>
        public ICommand ResetResizeCommand { get; private set; }

        /// <summary>按当前角度旋转。</summary>
        public ICommand ApplyRotationCommand { get; private set; }

        #endregion

        /// <summary>装配尺寸与旋转相关命令（在构造函数中调用一次）。</summary>
        private void InitializeGeometryCommands()
        {
            ApplyResizeCommand = new RelayCommand(RunApplyResize, () => CanApplyResize);

            ApplyResizePresetCommand = new RelayCommand<double>(
                percent =>
                {
                    // 百分比预设只在"百分比"单位下直接可用；其他单位下先换算成等效值。
                    SetResizePercent(percent);
                },
                percent => HasDocument && !IsBusy);

            ResetResizeCommand = new RelayCommand(
                () => SyncResizeTargetsToDocument(true),
                () => HasDocument);

            ApplyRotationCommand = new RelayCommand(RunApplyRotation, () => CanApplyRotation);

            // 文档换了一张图（尺寸变化）时，把目标尺寸重新同步为"原尺寸"。
            // 用自身 PropertyChanged 而不是改 Document 的 setter：Document 在另一个 partial 里，
            // 这里不应该去动它的实现。
            PropertyChanged += OnSelfPropertyChanged;
        }

        private void OnSelfPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Document" || e.PropertyName == "HasDocument")
            {
                SyncResizeTargetsToDocument(false);
            }
        }

        #region 尺寸：状态

        /// <summary>输入单位。</summary>
        public ResizeUnit ResizeUnit
        {
            get { return _resizeUnit; }
            private set
            {
                if (value == _resizeUnit)
                {
                    return;
                }

                // 先把当前目标换算成像素，切换后再按新单位重新表达：
                // 这样"切单位"只是换个说法，不会改变实际要输出的像素尺寸。
                double widthPixels = ToPixelsX(_resizeTargetWidth);
                double heightPixels = ToPixelsY(_resizeTargetHeight);

                _resizeUnit = value;
                OnPropertyChanged("ResizeUnitIndex");

                _resizeTargetWidth = ClampResizeValue(FromPixelsX(widthPixels));
                _resizeTargetHeight = ClampResizeValue(FromPixelsY(heightPixels));

                OnPropertyChanged("ResizeTargetWidth");
                OnPropertyChanged("ResizeTargetHeight");
                NotifyResizeDerived();
            }
        }

        /// <summary>供下拉框绑定的单位索引（0 像素 / 1 百分比 / 2 厘米）。</summary>
        public int ResizeUnitIndex
        {
            get { return (int)_resizeUnit; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 2 ? 2 : value);
                ResizeUnit = (ResizeUnit)clamped;
            }
        }

        /// <summary>目标宽度（按当前单位）。</summary>
        public double ResizeTargetWidth
        {
            get { return _resizeTargetWidth; }
            set
            {
                double clamped = ClampResizeValue(value);

                if (Math.Abs(clamped - _resizeTargetWidth) < 1e-9)
                {
                    return;
                }

                _resizeTargetWidth = clamped;
                OnPropertyChanged("ResizeTargetWidth");

                if (_lockAspectRatio)
                {
                    // 在像素空间里换算比例，对三种单位都成立（百分比与厘米都是线性的）。
                    double widthPixels = ToPixelsX(clamped);
                    double heightPixels = OriginalHeightPixels > 0.0 && OriginalWidthPixels > 0.0
                        ? widthPixels * OriginalHeightPixels / OriginalWidthPixels
                        : widthPixels;

                    _resizeTargetHeight = ClampResizeValue(FromPixelsY(heightPixels));
                    OnPropertyChanged("ResizeTargetHeight");
                }

                NotifyResizeDerived();
            }
        }

        /// <summary>目标高度（按当前单位）。</summary>
        public double ResizeTargetHeight
        {
            get { return _resizeTargetHeight; }
            set
            {
                double clamped = ClampResizeValue(value);

                if (Math.Abs(clamped - _resizeTargetHeight) < 1e-9)
                {
                    return;
                }

                _resizeTargetHeight = clamped;
                OnPropertyChanged("ResizeTargetHeight");

                if (_lockAspectRatio)
                {
                    double heightPixels = ToPixelsY(clamped);
                    double widthPixels = OriginalHeightPixels > 0.0 && OriginalWidthPixels > 0.0
                        ? heightPixels * OriginalWidthPixels / OriginalHeightPixels
                        : heightPixels;

                    _resizeTargetWidth = ClampResizeValue(FromPixelsX(widthPixels));
                    OnPropertyChanged("ResizeTargetWidth");
                }

                NotifyResizeDerived();
            }
        }

        /// <summary>是否锁定宽高比。</summary>
        public bool LockAspectRatio
        {
            get { return _lockAspectRatio; }
            set
            {
                if (!SetProperty(ref _lockAspectRatio, value, "LockAspectRatio"))
                {
                    return;
                }

                // 刚打开锁定时立刻把高度对齐到宽度，避免"开关是开的、比例却是错的"
                if (value)
                {
                    ResizeTargetWidth = _resizeTargetWidth;
                }

                NotifyResizeDerived();
            }
        }

        /// <summary>插值方式（重采样核）。</summary>
        public ResampleKernel ResizeKernel
        {
            get { return _resizeKernel; }
            set
            {
                if (SetProperty(ref _resizeKernel, value, "ResizeKernel"))
                {
                    OnPropertyChanged("ResizeKernelIndex");
                }
            }
        }

        /// <summary>供下拉框绑定的插值方式索引（0 三次立方 / 1 双线性 / 2 邻近）。</summary>
        public int ResizeKernelIndex
        {
            get { return (int)_resizeKernel; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 2 ? 2 : value);
                ResizeKernel = (ResampleKernel)clamped;
            }
        }

        /// <summary>当前文档的原始像素宽度。</summary>
        public double OriginalWidthPixels
        {
            get { return _document == null ? 0.0 : _document.PixelWidth; }
        }

        /// <summary>当前文档的原始像素高度。</summary>
        public double OriginalHeightPixels
        {
            get { return _document == null ? 0.0 : _document.PixelHeight; }
        }

        /// <summary>换算用到的横向 DPI（未打开图片时按 96 计）。</summary>
        private double DocumentDpiX
        {
            get { return _document == null || _document.DpiX <= 0.5 ? 96.0 : _document.DpiX; }
        }

        /// <summary>换算用到的纵向 DPI。</summary>
        private double DocumentDpiY
        {
            get { return _document == null || _document.DpiY <= 0.5 ? 96.0 : _document.DpiY; }
        }

        /// <summary>目标像素宽度（已取整并至少为 1）。</summary>
        public int ComputedResizeWidthPixels
        {
            get { return ToPixelCount(ToPixelsX(_resizeTargetWidth)); }
        }

        /// <summary>目标像素高度（已取整并至少为 1）。</summary>
        public int ComputedResizeHeightPixels
        {
            get { return ToPixelCount(ToPixelsY(_resizeTargetHeight)); }
        }

        /// <summary>是否满足应用尺寸缩放的条件。</summary>
        public bool CanApplyResize
        {
            get
            {
                if (!HasDocument || IsBusy)
                {
                    return false;
                }

                int width = ComputedResizeWidthPixels;
                int height = ComputedResizeHeightPixels;

                if (!Resampler.IsValidSize(width, height))
                {
                    return false;
                }

                // 尺寸没变就不必产生一步无意义的历史
                return width != _document.PixelWidth || height != _document.PixelHeight;
            }
        }

        /// <summary>原始尺寸说明（供面板显示）。</summary>
        public string ResizeOriginalText
        {
            get
            {
                if (_document == null)
                {
                    return "未打开图片";
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "原始：{0} × {1} px　{2:0.0} × {3:0.0} cm　{4:0.#} DPI",
                    _document.PixelWidth,
                    _document.PixelHeight,
                    _document.PixelWidth / DocumentDpiX * 2.54,
                    _document.PixelHeight / DocumentDpiY * 2.54,
                    DocumentDpiX);
            }
        }

        /// <summary>目标尺寸说明（供面板显示）。</summary>
        public string ResizeResultText
        {
            get
            {
                if (_document == null)
                {
                    return string.Empty;
                }

                int width = ComputedResizeWidthPixels;
                int height = ComputedResizeHeightPixels;

                if (!Resampler.IsValidSize(width, height))
                {
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        "输出尺寸超出可处理范围（单边上限 {0} px、总像素上限 {1} 千万）",
                        Resampler.MaxDimension,
                        Resampler.MaxPixels / 10000000L);
                }

                if (width == _document.PixelWidth && height == _document.PixelHeight)
                {
                    return "与原图尺寸相同，无需应用";
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "将输出：{0} × {1} px　{2:0.0} × {3:0.0} cm",
                    width,
                    height,
                    width / DocumentDpiX * 2.54,
                    height / DocumentDpiY * 2.54);
            }
        }

        private void NotifyResizeDerived()
        {
            OnPropertyChanged("ComputedResizeWidthPixels");
            OnPropertyChanged("ComputedResizeHeightPixels");
            OnPropertyChanged("CanApplyResize");
            OnPropertyChanged("ResizeOriginalText");
            OnPropertyChanged("ResizeResultText");
            RelayCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region 旋转：状态

        /// <summary>
        /// 旋转角度（顺时针为正，-180 ~ 180）。
        ///
        /// 为什么不叫 RotationAngle：那会和 <see cref="PSText.Services.Filters.RotationAngle"/>
        /// 枚举同名。同名的实例属性会遮蔽类型名，导致本类其它 partial 里写
        /// `RotationAngle.Clockwise90` 时被解析成这个 double 属性而编译失败。
        /// </summary>
        public double RotationDegrees
        {
            get { return _rotationAngle; }
            set
            {
                double clamped = double.IsNaN(value) ? 0.0 : value;

                if (clamped > 180.0)
                {
                    clamped = 180.0;
                }
                else if (clamped < -180.0)
                {
                    clamped = -180.0;
                }

                if (Math.Abs(clamped - _rotationAngle) < 1e-9)
                {
                    return;
                }

                _rotationAngle = clamped;
                OnPropertyChanged("RotationDegrees");
                OnPropertyChanged("CanApplyRotation");
                OnPropertyChanged("RotationResultText");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>空白角填充方式。</summary>
        public RotationFill RotationFill
        {
            get { return _rotationFill; }
            set
            {
                if (SetProperty(ref _rotationFill, value, "RotationFill"))
                {
                    OnPropertyChanged("RotationFillIndex");
                    OnPropertyChanged("RotationResultText");
                }
            }
        }

        /// <summary>供下拉框绑定的填充方式索引（0 透明 / 1 白色 / 2 黑色 / 3 灰色）。</summary>
        public int RotationFillIndex
        {
            get { return (int)_rotationFill; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 3 ? 3 : value);
                RotationFill = (RotationFill)clamped;
            }
        }

        /// <summary>是否自动裁掉旋转产生的空白角（保持原图宽高比）。</summary>
        public bool RotationCropToInscribed
        {
            get { return _rotationCropToInscribed; }
            set
            {
                if (SetProperty(ref _rotationCropToInscribed, value, "RotationCropToInscribed"))
                {
                    OnPropertyChanged("RotationResultText");
                }
            }
        }

        /// <summary>是否满足应用旋转的条件。</summary>
        public bool CanApplyRotation
        {
            get
            {
                if (!HasDocument || IsBusy)
                {
                    return false;
                }

                // 角度为 0（或 360° 的整数倍）时应用没有意义
                if (Math.Abs(_rotationAngle) < 1e-6)
                {
                    return false;
                }

                int width;
                int height;
                GeometryFilters.CalcRotatedSize(
                    _document.PixelWidth,
                    _document.PixelHeight,
                    _rotationAngle,
                    _rotationCropToInscribed,
                    out width,
                    out height);

                return Resampler.IsValidSize(width, height);
            }
        }

        /// <summary>旋转结果尺寸说明（供面板显示）。</summary>
        public string RotationResultText
        {
            get
            {
                if (_document == null)
                {
                    return string.Empty;
                }

                int width;
                int height;
                GeometryFilters.CalcRotatedSize(
                    _document.PixelWidth,
                    _document.PixelHeight,
                    _rotationAngle,
                    _rotationCropToInscribed,
                    out width,
                    out height);

                if (!Resampler.IsValidSize(width, height))
                {
                    return "旋转后的画布超出可处理范围，请减小角度或先缩小图片";
                }

                bool isRightAngle = Math.Abs(_rotationAngle % 90.0) < 1e-6;
                string suffix;

                if (isRightAngle)
                {
                    suffix = "· 90° 倍数，无画质损失，无空白角";
                }
                else if (_rotationCropToInscribed)
                {
                    suffix = "· 已裁掉空白角";
                }
                else
                {
                    suffix = "· 四角填充：" + GetRotationFillDisplayName(_rotationFill);
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "将输出：{0} × {1} px　{2}",
                    width,
                    height,
                    suffix);
            }
        }

        private static string GetRotationFillDisplayName(RotationFill fill)
        {
            switch (fill)
            {
                case RotationFill.White:
                    return "白色";
                case RotationFill.Black:
                    return "黑色";
                case RotationFill.Gray:
                    return "灰色";
                default:
                    return "透明";
            }
        }

        #endregion

        #region 尺寸：换算与同步

        /// <summary>把当前单位下的横向值换算为像素。</summary>
        private double ToPixelsX(double value)
        {
            switch (_resizeUnit)
            {
                case ResizeUnit.Percent:
                    return OriginalWidthPixels * value / 100.0;
                case ResizeUnit.Centimeters:
                    return value / 2.54 * DocumentDpiX;
                default:
                    return value;
            }
        }

        /// <summary>把当前单位下的纵向值换算为像素。</summary>
        private double ToPixelsY(double value)
        {
            switch (_resizeUnit)
            {
                case ResizeUnit.Percent:
                    return OriginalHeightPixels * value / 100.0;
                case ResizeUnit.Centimeters:
                    return value / 2.54 * DocumentDpiY;
                default:
                    return value;
            }
        }

        /// <summary>把横向像素值换算回当前单位。</summary>
        private double FromPixelsX(double pixels)
        {
            switch (_resizeUnit)
            {
                case ResizeUnit.Percent:
                    return OriginalWidthPixels > 0.0 ? pixels / OriginalWidthPixels * 100.0 : 100.0;
                case ResizeUnit.Centimeters:
                    return pixels / DocumentDpiX * 2.54;
                default:
                    return pixels;
            }
        }

        /// <summary>把纵向像素值换算回当前单位。</summary>
        private double FromPixelsY(double pixels)
        {
            switch (_resizeUnit)
            {
                case ResizeUnit.Percent:
                    return OriginalHeightPixels > 0.0 ? pixels / OriginalHeightPixels * 100.0 : 100.0;
                case ResizeUnit.Centimeters:
                    return pixels / DocumentDpiY * 2.54;
                default:
                    return pixels;
            }
        }

        private static double ClampResizeValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return MinResizeValue;
            }

            if (value < MinResizeValue)
            {
                return MinResizeValue;
            }

            if (value > MaxResizeValue)
            {
                return MaxResizeValue;
            }

            // 取 3 位小数：百分比 / 厘米换算容易产出 33.333333333333336 这类值，
            // 直接在输入框里显示会很难看。3 位小数对像素尺寸的影响远小于 1px。
            return Math.Round(value, 3);
        }

        private static int ToPixelCount(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 1.0)
            {
                return 1;
            }

            double rounded = Math.Round(value);

            if (rounded > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)rounded;
        }

        /// <summary>
        /// 把目标尺寸同步为当前文档的原尺寸（100%）。
        /// </summary>
        /// <param name="force">true = 无论像素尺寸是否变化都同步（用于"恢复原尺寸"按钮）。</param>
        private void SyncResizeTargetsToDocument(bool force)
        {
            if (_document == null)
            {
                _lastSyncedPixelWidth = 0;
                _lastSyncedPixelHeight = 0;
                NotifyResizeDerived();
                return;
            }

            // 只在像素尺寸真的变了才重置：否则用户正在输入时，
            // 一次无关的编辑（例如亮度调整也会替换 Document）会把他填的数值清掉。
            if (!force
                && _document.PixelWidth == _lastSyncedPixelWidth
                && _document.PixelHeight == _lastSyncedPixelHeight)
            {
                return;
            }

            _lastSyncedPixelWidth = _document.PixelWidth;
            _lastSyncedPixelHeight = _document.PixelHeight;

            _resizeTargetWidth = ClampResizeValue(FromPixelsX(_document.PixelWidth));
            _resizeTargetHeight = ClampResizeValue(FromPixelsY(_document.PixelHeight));

            OnPropertyChanged("ResizeTargetWidth");
            OnPropertyChanged("ResizeTargetHeight");
            NotifyResizeDerived();
        }

        /// <summary>按百分比设置目标尺寸（锁宽高比时宽高一起变）。</summary>
        private void SetResizePercent(double percent)
        {
            if (percent <= 0.0 || _document == null)
            {
                return;
            }

            double widthPixels = OriginalWidthPixels * percent / 100.0;
            double heightPixels = OriginalHeightPixels * percent / 100.0;

            _resizeTargetWidth = ClampResizeValue(FromPixelsX(widthPixels));
            _resizeTargetHeight = ClampResizeValue(FromPixelsY(heightPixels));

            OnPropertyChanged("ResizeTargetWidth");
            OnPropertyChanged("ResizeTargetHeight");
            NotifyResizeDerived();

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                "目标尺寸已设为原图的 {0:0.#}%（{1} × {2} px）",
                percent,
                ComputedResizeWidthPixels,
                ComputedResizeHeightPixels);
        }

        #endregion

        #region 应用

        private void RunApplyResize()
        {
            if (!CanApplyResize)
            {
                return;
            }

            int width = ComputedResizeWidthPixels;
            int height = ComputedResizeHeightPixels;
            ResampleKernel kernel = _resizeKernel;

            string label = string.Format(
                CultureInfo.CurrentCulture,
                "调整尺寸 {0}×{1}",
                width,
                height);

            GeometryFilters geometry = _geometryFilters;
            _pendingOperation = ApplyResizeInternalAsync(label, geometry, width, height, kernel);
        }

        private async System.Threading.Tasks.Task ApplyResizeInternalAsync(
            string label,
            GeometryFilters geometry,
            int width,
            int height,
            ResampleKernel kernel)
        {
            // ApplyOneShotAsync 内部已经 try/catch 全包并会弹窗，异常不会逃逸。
            // 通知在这里补：尺寸变了要按新尺寸重新适配缩放，否则"适应窗口"会失效。
            await ApplyOneShotAsync(label, buffer => geometry.ResizeAsync(buffer, width, height, kernel))
                .ConfigureAwait(true);

            RefitAfterGeometryChange();
        }

        private void RunApplyRotation()
        {
            if (!CanApplyRotation)
            {
                return;
            }

            double angle = _rotationAngle;
            Color? background = ResolveRotationBackground();
            bool crop = _rotationCropToInscribed;

            string label = string.Format(CultureInfo.CurrentCulture, "旋转 {0:0.#}°", angle);

            GeometryFilters geometry = _geometryFilters;
            _pendingOperation = ApplyRotationInternalAsync(label, geometry, angle, background, crop);
        }

        private async System.Threading.Tasks.Task ApplyRotationInternalAsync(
            string label,
            GeometryFilters geometry,
            double angle,
            Color? background,
            bool crop)
        {
            await ApplyOneShotAsync(
                label,
                buffer => geometry.RotateArbitraryAsync(buffer, angle, background, crop)).ConfigureAwait(true);

            RefitAfterGeometryChange();
        }

        private Color? ResolveRotationBackground()
        {
            switch (_rotationFill)
            {
                case RotationFill.White:
                    return Colors.White;
                case RotationFill.Black:
                    return Colors.Black;
                case RotationFill.Gray:
                    return Color.FromRgb(0x80, 0x80, 0x80);
                default:
                    return null;
            }
        }

        /// <summary>
        /// 几何变换改变了画布尺寸后，若当前处于"适应窗口"，按新尺寸重新计算缩放。
        /// 否则把 4000px 缩到 800px 之后，画面会只占窗口的四分之一。
        /// </summary>
        private void RefitAfterGeometryChange()
        {
            if (_document == null || _zoomMode != ZoomMode.FitToWindow)
            {
                return;
            }

            ZoomFactor = CalculateFitZoom();
        }

        #endregion
    }
}
