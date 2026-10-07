using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using PSText.Infrastructure;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services.Filters;

namespace PSText.ViewModels
{
    /// <summary>
    /// MainViewModel 的「高级滤镜与几何变换」部分（partial）。
    ///
    /// 覆盖需求 P1-5 / P1-6 / P1-7 / P1-8：
    ///   - 反色、灰度、二值化
    ///   - 高斯模糊（可调半径）、USM 锐化（半径 / 强度 / 阈值）
    ///   - 自由裁剪、90° 旋转、水平 / 垂直翻转
    ///   - 预设边框、文字水印
    ///
    /// 全部通过 <see cref="ApplyOneShotAsync"/> 走同一条流程：
    /// 提交待处理预览 → 记录撤销目标 → 后台纯函数计算 → 写入历史。
    /// 因此每一步都是可撤销的，并且不会阻塞 UI 线程。
    /// </summary>
    public sealed partial class MainViewModel
    {
        private readonly BasicFilters _basicFilters = new BasicFilters();
        private readonly BlurFilters _blurFilters = new BlurFilters();
        private readonly GeometryFilters _geometryFilters = new GeometryFilters();
        private readonly BorderFilters _borderFilters = new BorderFilters();
        private readonly TextOverlayFilter _textOverlayFilter = new TextOverlayFilter();

        private string _textContent = "PS-text";
        private string _textFontFamily = "Microsoft YaHei UI";
        private double _textFontSize = 48.0;
        private Color _textColor = Colors.White;
        private TextAnchor _textAnchor = TextAnchor.BottomRight;
        private double _textMargin = 24.0;
        private bool _textBold = true;
        private bool _textItalic;
        private bool _textShadow = true;

        /// <summary>最近一次一次性编辑的异步操作（供等待 / 自检）。</summary>
        private Task _pendingOperation;

        private int _threshold = 128;
        private double _blurRadius = 8.0;
        private double _sharpenAmount = 120.0;
        private int _sharpenThreshold = 4;
        private double _sharpenRadius = 2.0;
        private int _borderWidth = 24;
        private BorderStyle _borderStyle = BorderStyle.Solid;
        private Color _borderColor = Color.FromRgb(0xFF, 0xFF, 0xFF);

        #region 命令

        /// <summary>反色。</summary>
        public ICommand InvertCommand { get; private set; }

        /// <summary>灰度。</summary>
        public ICommand GrayscaleCommand { get; private set; }

        /// <summary>按当前阈值执行二值化。</summary>
        public ICommand ThresholdCommand { get; private set; }

        /// <summary>按当前半径执行高斯模糊。</summary>
        public ICommand BlurCommand { get; private set; }

        /// <summary>按当前参数执行 USM 锐化。</summary>
        public ICommand SharpenCommand { get; private set; }

        /// <summary>旋转 90°（顺时针）。</summary>
        public ICommand RotateClockwiseCommand { get; private set; }

        /// <summary>旋转 90°（逆时针）。</summary>
        public ICommand RotateCounterClockwiseCommand { get; private set; }

        /// <summary>旋转 180°。</summary>
        public ICommand Rotate180Command { get; private set; }

        /// <summary>水平翻转。</summary>
        public ICommand FlipHorizontalCommand { get; private set; }

        /// <summary>垂直翻转。</summary>
        public ICommand FlipVerticalCommand { get; private set; }

        /// <summary>应用预设边框。</summary>
        public ICommand ApplyBorderCommand { get; private set; }

        /// <summary>进入裁剪模式。</summary>
        public ICommand BeginCropCommand { get; private set; }

        /// <summary>取消裁剪。</summary>
        public ICommand CancelCropCommand { get; private set; }

        /// <summary>应用裁剪。</summary>
        public ICommand ApplyCropCommand { get; private set; }

        /// <summary>把当前文字叠加到画面（可撤销）。</summary>
        public ICommand ApplyTextCommand { get; private set; }

        #endregion

        #region 文字叠加参数

        /// <summary>要叠加的文字内容。</summary>
        public string TextContent
        {
            get { return _textContent; }
            set { SetProperty(ref _textContent, value, "TextContent"); }
        }

        /// <summary>文字字体名。</summary>
        public string TextFontFamily
        {
            get { return _textFontFamily; }
            set { SetProperty(ref _textFontFamily, value, "TextFontFamily"); }
        }

        /// <summary>文字字号（像素）。</summary>
        public double TextFontSize
        {
            get { return _textFontSize; }
            set { SetProperty(ref _textFontSize, ClampRange(value, 6.0, 400.0), "TextFontSize"); }
        }

        /// <summary>文字颜色。</summary>
        public Color TextColor
        {
            get { return _textColor; }
            set
            {
                if (SetProperty(ref _textColor, value, "TextColor"))
                {
                    OnPropertyChanged("TextColorBrush");
                }
            }
        }

        /// <summary>文字颜色的画刷（供界面色块绑定）。</summary>
        public Brush TextColorBrush
        {
            get { return new SolidColorBrush(_textColor); }
        }

        /// <summary>锚点位置。</summary>
        public TextAnchor TextAnchor
        {
            get { return _textAnchor; }
            set
            {
                if (SetProperty(ref _textAnchor, value, "TextAnchor"))
                {
                    OnPropertyChanged("TextAnchorIndex");
                }
            }
        }

        /// <summary>供下拉框绑定的锚点索引（0~8）。</summary>
        public int TextAnchorIndex
        {
            get { return (int)_textAnchor; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 8 ? 8 : value);
                TextAnchor = (TextAnchor)clamped;
            }
        }

        /// <summary>距边缘的边距（像素）。</summary>
        public double TextMargin
        {
            get { return _textMargin; }
            set { SetProperty(ref _textMargin, ClampRange(value, 0.0, 400.0), "TextMargin"); }
        }

        /// <summary>是否加粗。</summary>
        public bool TextBold
        {
            get { return _textBold; }
            set { SetProperty(ref _textBold, value, "TextBold"); }
        }

        /// <summary>是否斜体。</summary>
        public bool TextItalic
        {
            get { return _textItalic; }
            set { SetProperty(ref _textItalic, value, "TextItalic"); }
        }

        /// <summary>是否带投影。</summary>
        public bool TextShadow
        {
            get { return _textShadow; }
            set { SetProperty(ref _textShadow, value, "TextShadow"); }
        }

        #endregion

        /// <summary>
        /// 装配高级滤镜相关命令（在构造函数中调用一次）。
        /// </summary>
        private void InitializeFilterCommands()
        {
            InvertCommand = CreateFilterCommand(
                "反色",
                () => buffer => _basicFilters.InvertAsync(buffer),
                () => HasDocument);

            GrayscaleCommand = CreateFilterCommand(
                "灰度",
                () => buffer => _basicFilters.GrayscaleAsync(buffer),
                () => HasDocument);

            ThresholdCommand = CreateFilterCommand(
                "二值化",
                () => buffer => _basicFilters.ThresholdAsync(buffer, _threshold),
                () => HasDocument);

            BlurCommand = CreateFilterCommand(
                "高斯模糊",
                () => buffer => _blurFilters.GaussianBlurAsync(buffer, _blurRadius),
                () => HasDocument);

            SharpenCommand = CreateFilterCommand(
                "USM 锐化",
                () => buffer => _blurFilters.UnsharpMaskAsync(
                    buffer,
                    _sharpenRadius,
                    _sharpenAmount,
                    _sharpenThreshold),
                () => HasDocument);

            RotateClockwiseCommand = CreateGeometryCommand(
                "旋转 90°",
                buffer => _geometryFilters.RotateAsync(buffer, RotationAngle.Clockwise90));

            RotateCounterClockwiseCommand = CreateGeometryCommand(
                "旋转 -90°",
                buffer => _geometryFilters.RotateAsync(buffer, RotationAngle.Clockwise270));

            Rotate180Command = CreateGeometryCommand(
                "旋转 180°",
                buffer => _geometryFilters.RotateAsync(buffer, RotationAngle.Clockwise180));

            FlipHorizontalCommand = CreateGeometryCommand(
                "水平翻转",
                buffer => _geometryFilters.FlipHorizontalAsync(buffer));

            FlipVerticalCommand = CreateGeometryCommand(
                "垂直翻转",
                buffer => _geometryFilters.FlipVerticalAsync(buffer));

            ApplyBorderCommand = CreateFilterCommand(
                "添加边框",
                () => buffer => _borderFilters.ApplyAsync(
                    buffer,
                    _borderStyle,
                    _borderWidth,
                    _borderColor,
                    Color.FromArgb(150, 0, 0, 0)),
                () => HasDocument);

            // 裁剪：进入 / 取消 / 应用
            BeginCropCommand = new RelayCommand(BeginCrop, () => HasDocument && !IsBusy && !IsCropping);
            CancelCropCommand = new RelayCommand(CancelCrop, () => IsCropping);
            ApplyCropCommand = new RelayCommand(
                () => RunCropAsync(),
                () => IsCropping && !IsBusy && IsCropAreaValid);

            // 文字叠加
            ApplyTextCommand = new RelayCommand(
                RunApplyText,
                () => HasDocument && !IsBusy && !string.IsNullOrWhiteSpace(_textContent));
        }

        /// <summary>
        /// 叠加文字。文字排版依赖 WPF 字体渲染，因此必须在 UI 线程构造 RenderTargetBitmap；
        /// ApplyOneShotAsync 在 UI 线程上调用 transform，而 TextOverlayFilter 内部同步完成渲染，
        /// 因此这里不需要额外切换线程。
        /// </summary>
        private void RunApplyText()
        {
            if (string.IsNullOrWhiteSpace(_textContent) || _document == null)
            {
                return;
            }

            _pendingOperation = RunApplyTextInternalAsync();
        }

        private async Task RunApplyTextInternalAsync()
        {
            try
            {
                TextOverlayOptions options = new TextOverlayOptions
                {
                    Text = _textContent,
                    FontFamilyName = _textFontFamily,
                    FontSize = _textFontSize,
                    Color = _textColor,
                    Anchor = _textAnchor,
                    Margin = _textMargin,
                    Bold = _textBold,
                    Italic = _textItalic,
                    Shadow = _textShadow
                };

                double dpiX = _document != null ? _document.DpiX : 96.0;
                double dpiY = _document != null ? _document.DpiY : 96.0;

                await ApplyOneShotAsync(
                    "添加文字",
                    buffer => _textOverlayFilter.ApplyAsync(buffer, options, dpiX, dpiY)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                HandleError("添加文字失败。", ex, true);
            }
        }

        private ICommand CreateFilterCommand(
            string label,
            Func<Func<PixelBuffer, Task<PixelBuffer>>> factory,
            Func<bool> canExecute)
        {
            return new RelayCommand(
                () => RunFilterAsync(label, factory()),
                () => canExecute() && !IsBusy);
        }

        private ICommand CreateGeometryCommand(
            string label,
            Func<PixelBuffer, Task<PixelBuffer>> transform)
        {
            return new RelayCommand(
                () => RunFilterAsync(label, transform),
                () => HasDocument && !IsBusy);
        }

        /// <summary>
        /// 最近一次“一次性编辑”的异步操作。
        ///
        /// 界面命令是即发即忘的（ICommand.Execute 无法返回 Task），但保存 / 打印前
        /// 以及自检都需要能等待它完成，因此把内部 Task 暴露出来。
        /// </summary>
        public Task PendingOperation
        {
            get { return _pendingOperation ?? Task.FromResult(0); }
        }

        /// <summary>
        /// 执行一次性滤镜。这是由 UI 命令触发的“即发即忘”流程，
        /// 内部已由 ApplyOneShotAsync 用 try/catch 全包，异常不会逃逸到 UI 线程。
        /// </summary>
        private void RunFilterAsync(string label, Func<PixelBuffer, Task<PixelBuffer>> transform)
        {
            if (transform == null)
            {
                return;
            }

            _pendingOperation = RunFilterInternalAsync(label, transform);
        }

        private async Task RunFilterInternalAsync(string label, Func<PixelBuffer, Task<PixelBuffer>> transform)
        {
            await ApplyOneShotAsync(label, transform).ConfigureAwait(true);
        }

        #region 参数

        /// <summary>二值化阈值（0~255）。</summary>
        public int Threshold
        {
            get { return _threshold; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 255 ? 255 : value);
                SetProperty(ref _threshold, clamped, "Threshold");
            }
        }

        /// <summary>高斯模糊半径（像素）。</summary>
        public double BlurRadius
        {
            get { return _blurRadius; }
            set { SetProperty(ref _blurRadius, ClampRange(value, 0.0, 200.0), "BlurRadius"); }
        }

        /// <summary>USM 锐化强度（百分比）。</summary>
        public double SharpenAmount
        {
            get { return _sharpenAmount; }
            set { SetProperty(ref _sharpenAmount, ClampRange(value, 0.0, 500.0), "SharpenAmount"); }
        }

        /// <summary>USM 锐化阈值（差值小于该值不锐化）。</summary>
        public int SharpenThreshold
        {
            get { return _sharpenThreshold; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 255 ? 255 : value);
                SetProperty(ref _sharpenThreshold, clamped, "SharpenThreshold");
            }
        }

        /// <summary>USM 锐化半径（像素）。</summary>
        public double SharpenRadius
        {
            get { return _sharpenRadius; }
            set { SetProperty(ref _sharpenRadius, ClampRange(value, 0.5, 50.0), "SharpenRadius"); }
        }

        /// <summary>边框宽度（像素）。</summary>
        public int BorderWidth
        {
            get { return _borderWidth; }
            set
            {
                int clamped = value < 1 ? 1 : (value > 400 ? 400 : value);
                SetProperty(ref _borderWidth, clamped, "BorderWidth");
            }
        }

        /// <summary>边框样式。</summary>
        public BorderStyle BorderStyle
        {
            get { return _borderStyle; }
            set
            {
                if (SetProperty(ref _borderStyle, value, "BorderStyle"))
                {
                    OnPropertyChanged("BorderStyleIndex");
                }
            }
        }

        /// <summary>供下拉框绑定的样式的索引（0 实线 / 1 内衬 / 2 阴影）。</summary>
        public int BorderStyleIndex
        {
            get
            {
                switch (_borderStyle)
                {
                    case BorderStyle.InnerLine:
                        return 1;
                    case BorderStyle.Shadow:
                        return 2;
                    default:
                        return 0;
                }
            }

            set
            {
                switch (value)
                {
                    case 1:
                        BorderStyle = BorderStyle.InnerLine;
                        break;
                    case 2:
                        BorderStyle = BorderStyle.Shadow;
                        break;
                    default:
                        BorderStyle = BorderStyle.Solid;
                        break;
                }
            }
        }

        /// <summary>边框颜色（供颜色选择使用）。</summary>
        public Color BorderColor
        {
            get { return _borderColor; }
            set
            {
                if (SetProperty(ref _borderColor, value, "BorderColor"))
                {
                    OnPropertyChanged("BorderColorBrush");
                }
            }
        }

        /// <summary>边框颜色的画刷（供界面色块绑定）。</summary>
        public Brush BorderColorBrush
        {
            get { return new SolidColorBrush(_borderColor); }
        }

        private static double ClampRange(double value, double min, double max)
        {
            if (double.IsNaN(value))
            {
                return min;
            }

            return value < min ? min : (value > max ? max : value);
        }

        #endregion
    }
}
