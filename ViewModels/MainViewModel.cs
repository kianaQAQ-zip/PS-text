using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    ///   - 异步操作以 Task 为主；async void 仅限"由 UI 事件 / 计时器触发且内部全包 try/catch"
    ///     的入口（见 Adjustments 的 RunRenderAsync）；批量的两个长流程刻意返回 Task，
    ///     以便自检能等到它结束（见 Batch 文件说明）；
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
        private readonly AsyncRelayCommand _openCommand;
        private readonly AsyncRelayCommand _saveCommand;
        private readonly AsyncRelayCommand _saveAsCommand;

        /// <summary>预览提交防抖计时器（只在 UI 线程使用）。</summary>
        private readonly DispatcherTimer _previewTimer;

        /// <summary>
        /// 打开的标签页。
        ///
        /// 不变量：**至少有一个会话，且 ActiveSession 永不为 null** ——
        /// 关闭最后一个标签时会把它重置为"空会话"，而不是从集合里移除。
        /// 这条不变量让下面所有转发属性都不需要判空，比到处写 null 检查可靠得多。
        /// </summary>
        private readonly ObservableCollection<DocumentSession> _sessions =
            new ObservableCollection<DocumentSession>();

        private DocumentSession _activeSession;

        // ============================================================
        //  按文档隔离的状态
        //
        //  下面这些原本是**字段**，现在改成转发到 ActiveSession 的**属性**。
        //  这样做的好处是：几百处调用点一行都不用改，而"切换标签 = 换一整套状态"
        //  自动成立。类里所有代码看起来仍然像在操作"当前文档"，
        //  但实际读写的是当前标签的工作台。
        // ============================================================

        private ImageDocument _document
        {
            get { return _activeSession.Document; }
            set
            {
                if (ReferenceEquals(_activeSession.Document, value))
                {
                    return;
                }

                _activeSession.Document = value;
                OnDocumentReplaced();
            }
        }

        private HistoryManager _history
        {
            get { return _activeSession.History; }
        }

        private List<AnnotationObject> _annotations
        {
            get { return _activeSession.Annotations; }
        }

        private ObservableCollection<AnnotationItemViewModel> _annotationItems
        {
            get { return _activeSession.AnnotationItems; }
        }

        private AdjustmentsHolder _adjustments
        {
            get { return _activeSession.Adjustments; }
        }

        private int _selectedAnnotationIndex
        {
            get { return _activeSession.SelectedAnnotationIndex; }
            set { _activeSession.SelectedAnnotationIndex = value; }
        }

        private PixelBuffer _sourceBuffer
        {
            get { return _activeSession.SourceBuffer; }
            set { _activeSession.SourceBuffer = value; }
        }

        private PixelBuffer _previewBuffer
        {
            get { return _activeSession.PreviewBuffer; }
            set { _activeSession.PreviewBuffer = value; }
        }

        private PixelBuffer _baseBuffer
        {
            get { return _activeSession.BaseBuffer; }
            set { _activeSession.BaseBuffer = value; }
        }

        private EditState _baseState
        {
            get { return _activeSession.BaseState; }
            set { _activeSession.BaseState = value; }
        }

        private EditState _renderedState
        {
            get { return _activeSession.RenderedState; }
            set { _activeSession.RenderedState = value; }
        }

        private bool _baseStateInHistory
        {
            get { return _activeSession.BaseStateInHistory; }
            set { _activeSession.BaseStateInHistory = value; }
        }

        private int _renderRevision
        {
            get { return _activeSession.RenderRevision; }
            set { _activeSession.RenderRevision = value; }
        }

        private int _lastRenderedRevision
        {
            get { return _activeSession.LastRenderedRevision; }
            set { _activeSession.LastRenderedRevision = value; }
        }

        private PixelAdjustments _lastRenderedAdjustments
        {
            get { return _activeSession.LastRenderedAdjustments; }
            set { _activeSession.LastRenderedAdjustments = value; }
        }

        private PixelAdjustments _lastCommittedAdjustments
        {
            get { return _activeSession.LastCommittedAdjustments; }
            set { _activeSession.LastCommittedAdjustments = value; }
        }

        private PixelAdjustments _committedAdjustments
        {
            get { return _activeSession.CommittedAdjustments; }
            set { _activeSession.CommittedAdjustments = value; }
        }

        private double _zoomFactor
        {
            get { return _activeSession.ZoomFactor; }
            set { _activeSession.ZoomFactor = value; }
        }

        private ZoomMode _zoomMode
        {
            get { return _activeSession.ZoomMode; }
            set { _activeSession.ZoomMode = value; }
        }

        // ============================================================
        //  全局（跨标签共享）状态
        // ============================================================

        /// <summary>当前正在显示的是降采样预览位图。</summary>
        private bool _isPreviewing;

        /// <summary>最近一次预览位图（仅用于诊断 / 避免重复创建）。</summary>
        private BitmapSource _lastPreviewBitmap;

        /// <summary>抑制“参数变化 → 请求渲染”（撤销恢复参数时使用）。</summary>
        private bool _suppressAdjustmentRender;

        /// <summary>最近一次提交未入历史的原因（null 表示正常）。用于诊断偶发竞态。</summary>
        public string LastCommitDiagnostic { get; private set; }

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

            // 必须**最先**建立空会话：下面所有 InitializeXxxCommands() 都会通过转发属性
            // 读到 _activeSession，晚一步就会空引用（转发属性刻意不判空，见字段区的说明）。
            _activeSession = CreateSession(null);
            _activeSession.IsActiveTab = true;
            _sessions.Add(_activeSession);

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

            // 批量流水线命令（见 MainViewModel.Batch.cs）
            InitializeBatchCommands();

            // 系统集成：文件关联 / 运行环境 / 日志目录（见 MainViewModel.System.cs）
            InitializeSystemCommands();

            // 系统 / 主题 / 最近文件命令（见 MainViewModel.Settings.cs）
            InitializeSettingsCommands();

            // 多文档标签页命令（见 MainViewModel.Tabs.cs）
            InitializeTabCommands();

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
            private set { _document = value; }
        }

        /// <summary>
        /// 文档被替换后的统一处理。
        ///
        /// 放在**转发属性**的 setter 里而不是公开属性里：这样无论从哪条路改文档
        /// （公开属性、还是类内部直接写 _document），通知与缓存失效都不会漏 ——
        /// 漏掉的后果是"界面还显示旧尺寸"或"滑块每动一下都重新解码"这类难查的问题。
        /// </summary>
        private void OnDocumentReplaced()
        {
            OnPropertyChanged("Document");
            OnPropertyChanged("HasDocument");
            OnPropertyChanged("CurrentBitmap");
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

            _activeSession.RefreshTabCaption();

            // 位图被整体替换（加载 / 撤销 / 重做 / 调整提交）时，缓存的像素缓冲必须失效。
            // 例外：降采样预览替换显示位图时必须保留缓冲，否则滑块每动一下都要重新解码。
            InvalidateAdjustmentCache(_isPreviewing);
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

                // 标注手柄要按**屏幕像素**保持恒定大小，因此缩放一变就得重算它们的尺寸。
                // （见 MainViewModel.Annotation.cs；没打开图片时集合为 null，那边会自己防护。）
                RebuildAnnotationHandles();
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
                // 不能用 SetProperty(ref ...)：_zoomMode 现在是转发到会话的属性，
                // 不是字段，没法按引用传递。
                if (_zoomMode == value)
                {
                    return;
                }

                _zoomMode = value;

                OnPropertyChanged("ZoomMode");
                OnPropertyChanged("IsFitToWindow");
                OnPropertyChanged("IsActualSize");
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

            // 当前标签已经装了图 → 开一个新标签再加载，不要顶掉正在编辑的那张。
            // 反之（空标签）直接复用它，免得每次打开都白留一个空标签。
            DocumentSession created = null;

            if (_activeSession.HasDocument)
            {
                created = CreateSession(null);
                _sessions.Add(created);
                ActivateSession(created);
            }

            IsBusy = true;
            StatusMessage = "正在加载：" + Path.GetFileName(filePath);
            bool loaded = false;

            try
            {
                ImageLoadResult result = await _imageService.LoadAsync(filePath, CancellationToken.None).ConfigureAwait(true);

                // 打开新图片：清空撤销历史，并把调整参数复位为中性。
                _previewTimer.Stop();
                _renderRevision++;
                ResetDocumentState();

                Document = ImageDocument.FromLoadResult(result);
                loaded = true;

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

                // 加载失败时把刚建的空标签撤掉，别在标签栏里留一个空白页。
                if (created != null && !loaded)
                {
                    _sessions.Remove(created);
                    ActivateSession(_sessions[0]);
                }
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
            // 多文档下要确认的是**全部**未保存的文档：只看当前标签的话，
            // 关窗口会把别的标签里没保存的修改悄悄丢掉。
            for (int i = 0; i < _sessions.Count; i++)
            {
                DocumentSession session = _sessions[i];

                if (session.Document == null || !session.Document.IsDirty)
                {
                    continue;
                }

                ActivateSession(session);

                if (!await ConfirmActiveDocumentAsync().ConfigureAwait(true))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>确认当前标签里未保存的修改。返回 true 表示可以继续（已保存或用户放弃）。</summary>
        private async Task<bool> ConfirmActiveDocumentAsync()
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
            // 文本在 MainViewModel.System.cs 里拼：那里能拿到运行环境与日志路径，
            // 用户报问题时"关于"里的这几行往往就是最关键的信息。
            _dialogService.ShowInformation(BuildAboutText(), "关于 PS-text");
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
