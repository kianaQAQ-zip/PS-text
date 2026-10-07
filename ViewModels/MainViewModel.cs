using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSText.Infrastructure;
using PSText.Infrastructure.Behaviors;
using PSText.Infrastructure.History;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services.Filters;
using PSText.Services.Interfaces;

namespace PSText.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：负责图片加载 / 保存、画布缩放平移与状态栏信息。
    ///
    /// 本类为 partial，按职责拆成两个文件：
    ///   - MainViewModel.cs              加载 / 保存、画布缩放平移、状态栏、命令装配
    ///   - MainViewModel.Adjustments.cs  基础调整、调整会话、降采样预览、撤销 / 重做
    ///
    /// 设计约定：
    ///   - 不引用任何 UI 控件类型，只通过 IDialogService / IDispatcherService 与界面交互；
    ///   - 所有异步操作均为 Task，不使用 async void（唯一例外见 Adjustments 文件中的说明）；
    ///   - 位图用“不可变快照”方式替换（ImageDocument），便于撤销 / 重做。
    /// </summary>
    public sealed partial class MainViewModel : ObservableObject
    {
        /// <summary>缩放下限（与 CanvasInteraction 保持一致）。</summary>
        public const double MinZoom = CanvasInteraction.MinZoom;

        /// <summary>缩放上限。</summary>
        public const double MaxZoom = CanvasInteraction.MaxZoom;

        private const double ZoomStep = 1.25;

        /// <summary>拖动滑块时的预览降采样上限（像素）。越小越流畅，越大越清晰。</summary>
        private const int PreviewMaxSize = 1400;

        /// <summary>滑块停止变化多久后提交全分辨率结果（毫秒）。</summary>
        private const int PreviewCommitDelayMs = 220;

        private readonly IImageService _imageService;
        private readonly IDialogService _dialogService;
        private readonly IDispatcherService _dispatcherService;
        private readonly AdjustmentsFilter _adjustmentsFilter = new AdjustmentsFilter();
        private readonly HistoryManager _history = new HistoryManager(30, 256L * 1024 * 1024);
        private readonly AdjustmentsHolder _adjustments = new AdjustmentsHolder();
        private readonly AsyncRelayCommand _openCommand;
        private readonly AsyncRelayCommand _saveCommand;
        private readonly AsyncRelayCommand _saveAsCommand;

        /// <summary>预览提交防抖计时器（只在 UI 线程使用）。</summary>
        private readonly DispatcherTimer _previewTimer;

        /// <summary>进入调整前解码的源像素缓冲（全分辨率），用于“从基准重算”。</summary>
        private PixelBuffer _sourceBuffer;

        /// <summary>当前基准缓冲：非空表示正在一次连续调整会话中（撤销应回到 _baseState）。</summary>
        private PixelBuffer _baseBuffer;

        /// <summary>惰性创建的降采样缓冲（拖动滑块时的预览用）。</summary>
        private PixelBuffer _previewBuffer;

        /// <summary>基准状态（连续调整会话的撤销目标）。</summary>
        private EditState _baseState;

        /// <summary>与当前 Document 位图对应的状态（撤销 / 重做的状态链指针）。</summary>
        private EditState _renderedState;

        /// <summary>只有“基准状态”已被历史记录拥有时，才允许把会话合并进上一条命令。</summary>
        private bool _baseStateInHistory;

        /// <summary>渲染序号，用于丢弃过期的异步渲染结果。</summary>
        private int _renderRevision;

        /// <summary>最近一次已渲染完成的调整参数修订号。</summary>
        private int _lastRenderedRevision = -1;

        /// <summary>最近一次已渲染的调整参数（用于跳过重复的预览渲染）。</summary>
        private PixelAdjustments _lastRenderedAdjustments;

        /// <summary>
        /// 最近一次**以全分辨率提交**的调整参数。
        ///
        /// 必须与 _lastRenderedAdjustments 分开：后者可能只是一次降采样预览的结果。
        /// 如果提交渲染也拿预览的进度去判断"无需重算"，就会出现
        /// “预览画出来了、但提交被跳过”，于是历史里没有这一步（曾偶发此缺陷）。
        /// </summary>
        private PixelAdjustments _lastCommittedAdjustments = PixelAdjustments.Neutral;

        /// <summary>
        /// 当前 Document 位图所对应的调整参数（“已提交”语义）。
        ///
        /// 必须与 _lastRenderedAdjustments 区分开：后者会被降采样预览更新，
        /// 而本字段只在真正提交、撤销、重做、加载时更新。
        /// 若用预览值当作调整会话的基准，撤销就会恢复成“已经调整过”的画面（曾踩此坑）。
        /// </summary>
        private PixelAdjustments _committedAdjustments = PixelAdjustments.Neutral;

        /// <summary>当前正在显示的是降采样预览位图。</summary>
        private bool _isPreviewing;

        /// <summary>最近一次预览位图（仅用于诊断 / 避免重复创建）。</summary>
        private BitmapSource _lastPreviewBitmap;

        /// <summary>抑制“参数变化 → 请求渲染”（撤销恢复参数时使用）。</summary>
        private bool _suppressAdjustmentRender;

        /// <summary>最近一次提交未入历史的原因（null 表示正常）。用于诊断偶发竞态。</summary>
        public string LastCommitDiagnostic { get; private set; }

        private ImageDocument _document;
        private double _zoomFactor = 1.0;
        private ZoomMode _zoomMode = ZoomMode.FitToWindow;
        private double _viewWidth;
        private double _viewHeight;
        private double _screenDpiX = 96.0;
        private double _screenDpiY = 96.0;
        private bool _isHandToolActive;
        private bool _isBusy;
        private bool _isRendering;
        private string _statusMessage;
        private string _lastError;

        public MainViewModel(
            IImageService imageService,
            IDialogService dialogService,
            IDispatcherService dispatcherService)
        {
            if (imageService == null)
            {
                throw new ArgumentNullException("imageService");
            }

            if (dialogService == null)
            {
                throw new ArgumentNullException("dialogService");
            }

            if (dispatcherService == null)
            {
                throw new ArgumentNullException("dispatcherService");
            }

            _imageService = imageService;
            _dialogService = dialogService;
            _dispatcherService = dispatcherService;

            _openCommand = CreateAsyncCommand(OpenImageAsync, () => !IsBusy);
            _saveCommand = CreateAsyncCommand(SaveAsync, () => HasDocument && !IsBusy);
            _saveAsCommand = CreateAsyncCommand(SaveAsAsync, () => HasDocument && !IsBusy);

            FitToWindowCommand = new RelayCommand(FitToWindow, () => HasDocument);
            ActualSizeCommand = new RelayCommand(ActualSize, () => HasDocument);
            ZoomInCommand = new RelayCommand(ZoomIn, () => HasDocument && ZoomFactor < MaxZoom - 1e-6);
            ZoomOutCommand = new RelayCommand(ZoomOut, () => HasDocument && ZoomFactor > MinZoom + 1e-6);
            ToggleHandToolCommand = new RelayCommand(ToggleHandTool, () => HasDocument);
            ShowAboutCommand = new RelayCommand(ShowAbout);

            UndoCommand = new RelayCommand(Undo, () => _history.CanUndo && !IsBusy);
            RedoCommand = new RelayCommand(Redo, () => _history.CanRedo && !IsBusy);
            ResetAdjustmentsCommand = new RelayCommand(
                () => SetAdjustment(0, 0, 0, 0),
                () => HasDocument && !IsBusy);

            _adjustments.Changed += (sender, args) => OnAdjustmentsChanged();
            _history.Changed += (sender, args) => OnHistoryChanged();

            // 高级滤镜与裁剪命令（见 MainViewModel.Filters.cs / MainViewModel.Crop.cs）
            InitializeFilterCommands();

            // 尺寸缩放与任意角度旋转命令（见 MainViewModel.Geometry.cs）
            InitializeGeometryCommands();

            // 修补 / 消除（智能填充）命令（见 MainViewModel.Retouch.cs）
            InitializeRetouchCommands();

            // 标注（非破坏性对象）命令（见 MainViewModel.Annotation.cs）
            InitializeAnnotationCommands();

            // 打印与批量打印命令（见 MainViewModel.Print.cs）
            InitializePrintCommands();

            // 设置 / 主题 / 最近文件命令（见 MainViewModel.Settings.cs）
            InitializeSettingsCommands();

            _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PreviewCommitDelayMs)
            };
            _previewTimer.Tick += (sender, args) =>
            {
                _previewTimer.Stop();
                RequestRender(true);
            };
        }

        /// <summary>ViewModel 初始化完成（由 View 触发），用于首次计算适应窗口缩放。</summary>
        public event EventHandler Initialized;

        #region 命令

        public ICommand OpenCommand
        {
            get { return _openCommand; }
        }

        public ICommand SaveCommand
        {
            get { return _saveCommand; }
        }

        public ICommand SaveAsCommand
        {
            get { return _saveAsCommand; }
        }

        public ICommand FitToWindowCommand { get; private set; }

        public ICommand ActualSizeCommand { get; private set; }

        public ICommand ZoomInCommand { get; private set; }

        public ICommand ZoomOutCommand { get; private set; }

        public ICommand ToggleHandToolCommand { get; private set; }

        public ICommand ShowAboutCommand { get; private set; }

        /// <summary>撤销（Ctrl+Z）。</summary>
        public ICommand UndoCommand { get; private set; }

        /// <summary>重做（Ctrl+Y）。</summary>
        public ICommand RedoCommand { get; private set; }

        /// <summary>把四项基础调整复位为中性。</summary>
        public ICommand ResetAdjustmentsCommand { get; private set; }

        #endregion

        #region 文档状态

        /// <summary>当前图片文档快照；未打开图片时为 null。</summary>
        public ImageDocument Document
        {
            get { return _document; }
            private set
            {
                if (ReferenceEquals(_document, value))
                {
                    return;
                }

                _document = value;
                OnPropertyChanged("Document");
                OnPropertyChanged("HasDocument");
                OnPropertyChanged("ImagePixelWidth");
                OnPropertyChanged("ImagePixelHeight");
                OnPropertyChanged("IsDirty");
                OnPropertyChanged("PixelSizeText");
                OnPropertyChanged("DpiText");
                OnPropertyChanged("PhysicalSizeText");
                OnPropertyChanged("FileSizeText");
                OnPropertyChanged("FileName");
                OnPropertyChanged("FormatText");
                OnPropertyChanged("StatusText");
                OnPropertyChanged("WindowTitle");

                // 位图被整体替换（加载 / 撤销 / 重做 / 调整提交）时，缓存的像素缓冲必须失效。
                // 例外：降采样预览替换显示位图时必须保留缓冲，否则滑块每动一下都要重新解码。
                InvalidateAdjustmentCache(_isPreviewing);
            }
        }

        /// <summary>是否已打开图片。</summary>
        public bool HasDocument
        {
            get { return _document != null; }
        }

        /// <summary>当前位图；未打开时为 null（供 Image.Source 绑定）。</summary>
        public BitmapSource CurrentBitmap
        {
            get { return _document == null ? null : _document.Bitmap; }
        }

        /// <summary>图片像素宽度（未打开时为 0，用于计算画布容器尺寸）。</summary>
        public double ImagePixelWidth
        {
            get { return _document == null ? 0.0 : _document.PixelWidth; }
        }

        /// <summary>图片像素高度。</summary>
        public double ImagePixelHeight
        {
            get { return _document == null ? 0.0 : _document.PixelHeight; }
        }

        /// <summary>是否存在未保存修改。</summary>
        public bool IsDirty
        {
            get { return _document != null && _document.IsDirty; }
        }

        public string FileName
        {
            get { return _document == null ? "未打开图片" : _document.FileName; }
        }

        public string PixelSizeText
        {
            get { return _document == null ? "—" : _document.PixelSizeText; }
        }

        public string DpiText
        {
            get { return _document == null ? "—" : _document.DpiText; }
        }

        public string PhysicalSizeText
        {
            get { return _document == null ? "—" : _document.PhysicalSizeText; }
        }

        public string FileSizeText
        {
            get { return _document == null ? "—" : _document.FileSizeText; }
        }

        public string FormatText
        {
            get
            {
                if (_document == null)
                {
                    return "—";
                }

                return ImageFileFormatHelper.GetDisplayName(_document.Format);
            }
        }

        /// <summary>窗口标题（含未保存标记）。</summary>
        public string WindowTitle
        {
            get
            {
                if (_document == null)
                {
                    return "PS-text 图片编辑器";
                }

                return string.Format(
                    "{0}{1} - PS-text",
                    _document.FileName,
                    _document.IsDirty ? " *" : string.Empty);
            }
        }

        #endregion

        #region 视图状态

        /// <summary>当前缩放比例（1.0 = 100%）。绑定到画布与状态栏。</summary>
        public double ZoomFactor
        {
            get { return _zoomFactor; }
            set
            {
                double clamped = ClampZoom(value);
                if (Math.Abs(clamped - _zoomFactor) < 1e-9)
                {
                    return;
                }

                _zoomFactor = clamped;
                OnPropertyChanged("ZoomFactor");
                OnPropertyChanged("ZoomPercentText");
                OnPropertyChanged("StatusText");
            }
        }

        /// <summary>缩放百分比文本，例如 “133%”。</summary>
        public string ZoomPercentText
        {
            get { return (_zoomFactor * 100.0).ToString("0.#", CultureInfo.CurrentCulture) + "%"; }
        }

        /// <summary>当前缩放模式。</summary>
        public ZoomMode ZoomMode
        {
            get { return _zoomMode; }
            private set
            {
                if (SetProperty(ref _zoomMode, value, "ZoomMode"))
                {
                    OnPropertyChanged("IsFitToWindow");
                    OnPropertyChanged("IsActualSize");
                }
            }
        }

        public bool IsFitToWindow
        {
            get { return _zoomMode == ZoomMode.FitToWindow; }
        }

        public bool IsActualSize
        {
            get { return _zoomMode == ZoomMode.ActualSize; }
        }

        /// <summary>是否处于抓手（平移）工具状态，绑定到 CanvasInteraction.HandTool。</summary>
        public bool IsHandToolActive
        {
            get { return _isHandToolActive; }
            private set { SetProperty(ref _isHandToolActive, value); }
        }

        /// <summary>是否有耗时操作进行中（用于 Loading 遮罩）。</summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            private set { SetProperty(ref _isBusy, value); }
        }

        /// <summary>状态栏提示信息（加载完成 / 错误等）。</summary>
        public string StatusMessage
        {
            get { return _statusMessage; }
            private set
            {
                if (SetProperty(ref _statusMessage, value))
                {
                    OnPropertyChanged("StatusText");
                }
            }
        }

        /// <summary>最近一次错误信息，便于排查（不弹窗时使用）。</summary>
        public string LastError
        {
            get { return _lastError; }
            private set { SetProperty(ref _lastError, value); }
        }

        /// <summary>由 View 调用，在状态栏显示一条临时提示（不改变文档状态）。</summary>
        public void ShowHint(string message)
        {
            StatusMessage = message;
        }

        /// <summary>状态栏组合文本。</summary>
        public string StatusText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_statusMessage))
                {
                    return _statusMessage;
                }

                if (_document == null)
                {
                    return "就绪：请打开一张图片（Ctrl+O）";
                }

                return string.Format(
                    "{0} · {1} · {2} · 缩放 {3}",
                    _document.FileName,
                    _document.PixelSizeText,
                    _document.DpiText,
                    ZoomPercentText);
            }
        }

        /// <summary>屏幕水平 DPI（“原始大小”按屏幕像素计算时需要）。</summary>
        public double ScreenDpiX
        {
            get { return _screenDpiX; }
        }

        /// <summary>屏幕垂直 DPI。</summary>
        public double ScreenDpiY
        {
            get { return _screenDpiY; }
        }

        #endregion

        #region 视图回传

        /// <summary>
        /// 由 View 在 SizeChanged 时调用，回传画布视口大小。
        /// ViewModel 只接收数值，不接触任何 UI 控件。
        /// </summary>
        public void UpdateViewportSize(double width, double height)
        {
            if (double.IsNaN(width) || double.IsNaN(height) || width < 0.0 || height < 0.0)
            {
                return;
            }

            bool changed = Math.Abs(_viewWidth - width) > 0.5 || Math.Abs(_viewHeight - height) > 0.5;
            _viewWidth = width;
            _viewHeight = height;

            if (!changed)
            {
                return;
            }

            if (_zoomMode == ZoomMode.FitToWindow)
            {
                ZoomFactor = CalculateFitZoom();
            }
        }

        /// <summary>由 View 在 Loaded / DpiChanged 时回传屏幕 DPI。</summary>
        public void UpdateScreenDpi(double dpiX, double dpiY)
        {
            if (dpiX <= 0.5 || double.IsNaN(dpiX))
            {
                dpiX = 96.0;
            }

            if (dpiY <= 0.5 || double.IsNaN(dpiY))
            {
                dpiY = 96.0;
            }

            bool changed = Math.Abs(_screenDpiX - dpiX) > 0.01 || Math.Abs(_screenDpiY - dpiY) > 0.01;
            _screenDpiX = dpiX;
            _screenDpiY = dpiY;

            if (changed && _zoomMode == ZoomMode.ActualSize)
            {
                ZoomFactor = CalculateActualSizeZoom();
            }
        }

        /// <summary>View 初始化完成后调用，触发首个“适应窗口”缩放。</summary>
        public void NotifyInitialized()
        {
            ZoomFactor = _zoomMode == ZoomMode.FitToWindow
                ? CalculateFitZoom()
                : (_zoomMode == ZoomMode.ActualSize ? CalculateActualSizeZoom() : ZoomFactor);

            EventHandler handler = Initialized;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        #endregion

        #region 缩放操作

        /// <summary>适应窗口（保留一点边距，避免贴边）。</summary>
        public void FitToWindow()
        {
            ZoomMode = ZoomMode.FitToWindow;
            ZoomFactor = CalculateFitZoom();
            StatusMessage = "已适应窗口显示";
        }

        /// <summary>原始大小：1 个图片像素对应 1 个屏幕像素。</summary>
        public void ActualSize()
        {
            ZoomMode = ZoomMode.ActualSize;
            ZoomFactor = CalculateActualSizeZoom();
            StatusMessage = "已切换到原始大小（100% 物理像素）";
        }

        /// <summary>放大一档。</summary>
        public void ZoomIn()
        {
            SetZoom(ZoomFactor * ZoomStep, true);
        }

        /// <summary>缩小一档。</summary>
        public void ZoomOut()
        {
            SetZoom(ZoomFactor / ZoomStep, true);
        }

        /// <summary>设置抓手工具状态（Space 键切换）。</summary>
        public void SetHandTool(bool active)
        {
            IsHandToolActive = active;
        }

        /// <summary>切换抓手工具。</summary>
        public void ToggleHandTool()
        {
            IsHandToolActive = !IsHandToolActive;
        }

        /// <summary>
        /// 设置缩放；manual 为 true 时切到自由模式（窗口变化不再自动调整）。
        /// </summary>
        private void SetZoom(double value, bool manual)
        {
            double clamped = ClampZoom(value);

            if (manual)
            {
                ZoomMode = ZoomMode.Free;
            }

            ZoomFactor = clamped;
            StatusMessage = null;
        }

        /// <summary>计算适应窗口的缩放比例（留 2% 边距）。</summary>
        private double CalculateFitZoom()
        {
            if (_document == null || _viewWidth <= 1.0 || _viewHeight <= 1.0)
            {
                return ClampZoom(_zoomFactor);
            }

            const double margin = 0.98;
            double zoom = Math.Min(_viewWidth / _document.PixelWidth, _viewHeight / _document.PixelHeight) * margin;
            return ClampZoom(zoom);
        }

        /// <summary>
        /// 计算“原始大小”缩放：把图片的物理尺寸映射到屏幕物理像素。
        /// 公式：屏幕 DPI / 图片 DPI（图片 300DPI、屏幕 96DPI 时显示为 32%，但物理尺寸正确）。
        /// 结果四舍五入到 3 位小数：图片 DPI 常带有换算误差（例如 96 DPI 被存成 95.9866，
        /// 直接相除得到 1.00014，界面会显示 100% 但内部比例不干净）。
        /// 3 位小数足以保留真实的 DPI 差异（如 300 DPI 图片 → 0.32），又能吸收这类浮点抖动。
        /// </summary>
        private double CalculateActualSizeZoom()
        {
            if (_document == null)
            {
                return 1.0;
            }

            double scaleX = _document.DpiX > 0.5 ? _screenDpiX / _document.DpiX : 1.0;
            double scaleY = _document.DpiY > 0.5 ? _screenDpiY / _document.DpiY : 1.0;
            double scale = Math.Min(scaleX, scaleY);

            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0.0)
            {
                return 1.0;
            }

            return ClampZoom(Math.Round(scale, 3));
        }

        private static double ClampZoom(double zoom)
        {
            if (double.IsNaN(zoom) || double.IsInfinity(zoom) || zoom <= 0.0)
            {
                return 1.0;
            }

            return Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
        }

        #endregion

        #region 打开 / 保存

        private async Task OpenImageAsync()
        {
            string initialDirectory = _document != null && !string.IsNullOrEmpty(_document.FilePath)
                ? SafeGetDirectory(_document.FilePath)
                : null;

            string filePath = _dialogService.ShowOpenImageDialog("打开图片", initialDirectory);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            await LoadFromPathAsync(filePath).ConfigureAwait(true);
        }

        /// <summary>从指定路径加载图片（供命令与拖放 / 自检复用）。</summary>
        public async Task LoadFromPathAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (!File.Exists(filePath))
            {
                _dialogService.ShowError("文件不存在：" + filePath, "打开失败");
                return;
            }

            IsBusy = true;
            StatusMessage = "正在加载：" + Path.GetFileName(filePath);

            try
            {
                ImageLoadResult result = await _imageService.LoadAsync(filePath, CancellationToken.None).ConfigureAwait(true);

                // 打开新图片：清空撤销历史，并把调整参数复位为中性。
                _previewTimer.Stop();
                _renderRevision++;
                ResetDocumentState();

                Document = ImageDocument.FromLoadResult(result);

                // 加载成功才记入最近文件（需求 P3-16）
                if (!string.IsNullOrEmpty(result.FilePath))
                {
                    AddRecentFile(result.FilePath);
                }

                await _dispatcherService.InvokeAsync(() =>
                {
                    ZoomMode = ZoomMode.FitToWindow;
                    ZoomFactor = CalculateFitZoom();

                    StatusMessage = result.OrientationNormalized
                        ? string.Format(
                            "已加载 {0}（{1}，{2}，已按 EXIF 方向校正）",
                            Document.FileName,
                            Document.PixelSizeText,
                            Document.DpiText)
                        : string.Format(
                            "已加载 {0}（{1}，{2}）",
                            Document.FileName,
                            Document.PixelSizeText,
                            Document.DpiText);
                }).ConfigureAwait(true);
            }
            catch (FileNotFoundException ex)
            {
                HandleError("图片文件已被移动或删除。", ex, false);
            }
            catch (NotSupportedException ex)
            {
                HandleError("不支持该图片格式，或文件已损坏。", ex, true);
            }
            catch (UnauthorizedAccessException ex)
            {
                HandleError("没有权限读取该文件。", ex, true);
            }
            catch (IOException ex)
            {
                HandleError("读取文件失败，文件可能正被其他程序占用。", ex, true);
            }
            catch (Exception ex)
            {
                HandleError("加载图片失败。", ex, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>保存到当前路径（无路径时转为“另存为”）。</summary>
        private async Task SaveAsync()
        {
            ImageDocument initial = _document;
            if (initial == null)
            {
                return;
            }

            // 尚未关联文件（例如从拖放加载的新文档）→ 走“另存为”。
            if (string.IsNullOrEmpty(initial.FilePath))
            {
                await SaveAsAsync().ConfigureAwait(true);
                return;
            }

            string filePath = initial.FilePath;
            ImageFileFormat format = initial.Format;

            // 先把滑块产生的预览提交为全分辨率结果，避免保存到降采样图。
            await FlushPendingPreviewsAsync().ConfigureAwait(true);

            // 关键：提交会替换 Document 的位图，必须在提交之后重新取快照再保存，
            // 否则写入文件的是提交前的（可能是降采样预览的）位图。
            ImageDocument snapshot = _document;
            if (snapshot == null)
            {
                return;
            }

            await SaveToPathAsync(snapshot, filePath, format, false).ConfigureAwait(true);
        }

        private async Task SaveAsAsync()
        {
            if (_document == null)
            {
                return;
            }

            // 同上：另存为也必须基于全分辨率结果。
            await FlushPendingPreviewsAsync().ConfigureAwait(true);

            if (_document == null)
            {
                return;
            }

            ImageFileFormat targetFormat = _document.Format == ImageFileFormat.Unknown
                ? ImageFileFormat.Png
                : _document.Format;

            string defaultExtension = ImageFileFormatHelper.GetDefaultExtension(targetFormat);
            string suggestedName = Path.GetFileNameWithoutExtension(_document.FileName) + defaultExtension;
            string initialDirectory = SafeGetDirectory(_document.FilePath);

            string filePath = _dialogService.ShowSaveImageDialog(
                "另存为",
                initialDirectory,
                suggestedName,
                BuildSaveFilter(),
                defaultExtension);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            ImageFileFormat format = _imageService.DetectFormat(filePath);
            if (format == ImageFileFormat.Unknown)
            {
                format = targetFormat;
            }

            // 同样在提交之后重新取快照。
            ImageDocument snapshot = _document;
            if (snapshot == null)
            {
                return;
            }

            await SaveToPathAsync(snapshot, filePath, format, true).ConfigureAwait(true);
        }

        /// <summary>
        /// 保存指定快照到目标路径。isNewPath 为 true 表示目标路径发生变化（另存为 / 首次保存）。
        /// 保存过程保持原始像素尺寸与 DPI。
        /// </summary>
        /// <param name="snapshot">要保存的文档快照（调用方需在提交预览之后获取）。</param>
        private async Task SaveToPathAsync(
            ImageDocument snapshot,
            string filePath,
            ImageFileFormat format,
            bool isNewPath)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            // 导出文件必须包含标注。这里用"烘进标注的副本"，编辑现场仍然保持非破坏性 ——
            // 用户存完盘后还能回头改那个箭头的颜色。
            snapshot = WithAnnotationsBaked(snapshot);

            IsBusy = true;
            StatusMessage = "正在保存：" + Path.GetFileName(filePath);

            try
            {
                ImageSaveOptions options = new ImageSaveOptions
                {
                    Format = format,
                    PreserveDpi = true,
                    QualityLevel = 92,
                    TiffCompression = "Lzw"
                };

                long written = await _imageService
                    .SaveAsync(snapshot.Bitmap, filePath, options, CancellationToken.None)
                    .ConfigureAwait(true);

                // 目标路径变化时，文档需要同步新的路径与格式。
                if (isNewPath)
                {
                    Document = new ImageDocument(
                        snapshot.Bitmap,
                        filePath,
                        format,
                        snapshot.DpiX,
                        snapshot.DpiY,
                        written,
                        false);
                }
                else
                {
                    Document = snapshot.MarkSaved(filePath, format, written);
                }

                StatusMessage = string.Format("已保存：{0}（{1}）", Document.FileName, Document.FileSizeText);
            }
            catch (UnauthorizedAccessException ex)
            {
                HandleError("没有权限写入该位置，请换一个目录。", ex, true);
            }
            catch (IOException ex)
            {
                HandleError("写入文件失败，目标文件可能正被其他程序占用。", ex, true);
            }
            catch (Exception ex)
            {
                HandleError("保存失败。", ex, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string BuildSaveFilter()
        {
            return "PNG 图片|*.png"
                   + "|JPEG 图片|*.jpg;*.jpeg"
                   + "|BMP 图片|*.bmp"
                   + "|TIFF 图片|*.tif;*.tiff"
                   + "|GIF 图片|*.gif";
        }

        /// <summary>
        /// 关闭窗口前的“未保存修改”确认。返回 true 表示允许关闭。
        ///
        /// 设计说明：
        ///   - 何时需要保存由 ViewModel 判断（它才知道 IsDirty 的语义）；
        ///   - 怎么问用户交给 IDialogService，因此本方法不接触任何控件，可被自检直接调用；
        ///   - 用户选“保存”时走与 Ctrl+S 完全相同的 SaveAsync（含“先提交降采样预览”），
        ///     保存失败或用户在另存为对话框里取消时 IsDirty 仍为 true，于是拒绝关闭。
        /// </summary>
        public async Task<bool> ConfirmCloseAsync()
        {
            if (!IsDirty)
            {
                return true;
            }

            ConfirmResult result = _dialogService.Confirm(
                "当前图片有未保存的修改。\n\n"
                + "选择“是”保存后关闭，选择“否”放弃修改，选择“取消”返回继续编辑。",
                "有未保存的修改");

            if (result == ConfirmResult.Cancel)
            {
                return false;
            }

            if (result == ConfirmResult.No)
            {
                // 用户明确放弃修改
                return true;
            }

            await SaveAsync().ConfigureAwait(true);
            return !IsDirty;
        }

        #endregion

        #region 辅助

        /// <summary>创建带统一异常处理的异步命令。</summary>
        private AsyncRelayCommand CreateAsyncCommand(Func<Task> action, Func<bool> canExecute)
        {
            AsyncRelayCommand command = new AsyncRelayCommand(action, canExecute);
            command.ErrorHandler = ex => HandleError("操作执行失败。", ex, true);
            return command;
        }

        /// <summary>
        /// 统一异常处理：记录日志 + 按需弹窗，绝不把异常抛到 UI 线程导致崩溃。
        /// </summary>
        private void HandleError(string message, Exception exception, bool showDialog)
        {
            string detail = exception == null ? message : message + " " + exception.Message;
            LastError = detail;
            StatusMessage = detail;
            System.Diagnostics.Debug.WriteLine("[MainViewModel] " + detail + " -> " + exception);

            if (!showDialog)
            {
                return;
            }

            try
            {
                _dialogService.ShowException(message, exception, "出错了");
            }
            catch (Exception dialogException)
            {
                // 弹窗本身失败时不能再次抛出。
                System.Diagnostics.Debug.WriteLine("[MainViewModel] 弹窗失败: " + dialogException);
            }
        }

        private static string SafeGetDirectory(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(filePath);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private void ShowAbout()
        {
            _dialogService.ShowInformation(
                "PS-text 图片编辑器\n\n"
                + "· 支持 JPG / PNG / BMP / TIFF / GIF 的加载与保存\n"
                + "· 滚轮缩放（10% ~ 1000%）、按住鼠标拖动或空格键平移\n"
                + "· 双击画布在“适应窗口 / 原始大小”之间切换\n"
                + "· 亮度 / 对比度 / 饱和度 / 色温实时预览，可撤销 30 步\n\n"
                + "快捷键：Ctrl+O 打开，Ctrl+S 保存，Ctrl+Z / Ctrl+Y 撤销重做，Ctrl+P 打印",
                "关于 PS-text");
        }

        #endregion
    }

    /// <summary>
    /// 调整参数的持有者：负责提供变更通知。
    ///
    /// 为什么不直接用 PixelAdjustments 做绑定源：
    ///   PixelAdjustments 是不可变值对象（每次调整都产生新实例），
    ///   而 WPF 绑定需要稳定的对象 + PropertyChanged 通知；
    ///   这里用持有者把“不可变值”包装成“可观察状态”，
    ///   同时用 Revision 单调递增，便于丢弃过期的异步渲染结果。
    /// </summary>
    internal sealed class AdjustmentsHolder
    {
        private PixelAdjustments _value = PixelAdjustments.Neutral;

        /// <summary>值发生变化时触发。</summary>
        public event EventHandler Changed;

        /// <summary>当前值。</summary>
        public PixelAdjustments Value
        {
            get { return _value; }
        }

        /// <summary>修订号（每次变化递增，用于异步渲染的过期判断）。</summary>
        public int Revision { get; private set; }

        /// <summary>设置新值；值未变化时不触发通知。</summary>
        public void Set(PixelAdjustments value)
        {
            PixelAdjustments effective = value ?? PixelAdjustments.Neutral;

            if (effective.Equals(_value))
            {
                return;
            }

            _value = effective;
            Revision++;

            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
