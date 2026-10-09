using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSText.Infrastructure;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services;
using PSText.Services.Batch;
using PSText.Services.Filters;
using PSText.Services.Interfaces;

namespace PSText.ViewModels
{
    /// <summary>批量队列中的一项。</summary>
    public sealed class BatchItemViewModel : ObservableObject
    {
        private string _status = "待处理";
        private string _outputPath;
        private bool _isFailed;

        public BatchItemViewModel(string filePath)
        {
            FilePath = filePath;

            try
            {
                FileName = Path.GetFileName(filePath);
            }
            catch (ArgumentException)
            {
                FileName = filePath;
            }
        }

        /// <summary>源文件完整路径。</summary>
        public string FilePath { get; private set; }

        /// <summary>文件名（列表显示用）。</summary>
        public string FileName { get; private set; }

        /// <summary>该项的处理状态。</summary>
        public string Status
        {
            get { return _status; }
            set { SetProperty(ref _status, value, "Status"); }
        }

        /// <summary>实际写出的文件路径（完成后填充）。</summary>
        public string OutputPath
        {
            get { return _outputPath; }
            set { SetProperty(ref _outputPath, value, "OutputPath"); }
        }

        /// <summary>该项是否失败（失败项在界面里标红）。</summary>
        public bool IsFailed
        {
            get { return _isFailed; }
            set { SetProperty(ref _isFailed, value, "IsFailed"); }
        }
    }

    /// <summary>批量流水线列表里的一步（包装步骤本体，只负责通知与编号）。</summary>
    public sealed class BatchStepViewModel : ObservableObject
    {
        private string _orderText;

        public BatchStepViewModel(IBatchStep step, int order)
        {
            if (step == null)
            {
                throw new ArgumentNullException("step");
            }

            Step = step;
            _orderText = order.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>步骤本体（参数编辑直接改它）。</summary>
        public IBatchStep Step { get; private set; }

        public BatchStepKind Kind
        {
            get { return Step.Kind; }
        }

        public string DisplayName
        {
            get { return Step.DisplayName; }
        }

        /// <summary>参数摘要。</summary>
        public string Summary
        {
            get { return Step.Summary; }
        }

        /// <summary>列表里的序号文本。</summary>
        public string OrderText
        {
            get { return _orderText; }
            private set { SetProperty(ref _orderText, value, "OrderText"); }
        }

        /// <summary>步骤被就地修改后刷新列表显示。</summary>
        public void Refresh()
        {
            OnPropertyChanged("DisplayName");
            OnPropertyChanged("Summary");
        }

        internal void SetOrder(int order)
        {
            OrderText = order.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>「添加步骤」下拉项的展示模型。</summary>
    public sealed class BatchStepKindOption
    {
        public BatchStepKindOption(BatchStepKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public BatchStepKind Kind { get; private set; }

        public string Name { get; private set; }
    }

    /// <summary>输出格式下拉项；Format 为 Unknown 表示「沿用原格式」。</summary>
    public sealed class BatchOutputFormatOption
    {
        public BatchOutputFormatOption(ImageFileFormat format, string name)
        {
            Format = format;
            Name = name;
        }

        public ImageFileFormat Format { get; private set; }

        public string Name { get; private set; }
    }

    /// <summary>
    /// MainViewModel 的「批量流水线」部分（M3）。
    ///
    /// 结构：**有序步骤列表** 套到 **一批文件** 上，导出到目录。
    ///
    /// 三个刻意的设计决定：
    ///   1. **顺序是用户可见的**。先缩放后加水印 与 先加水印后缩放 得到的是不同的东西
    ///      （水印跟不跟着一起被缩小），把它藏成固定管道等于替用户做了个他看不见的决定。
    ///   2. **预览独立于主画布**。批量是"另一条流水线"，复用主画布会覆盖用户正在编辑的画面，
    ///      有未保存修改时会很麻烦。这里用降采样副本 + <see cref="BatchContext.Scale"/> 保证预览可信。
    ///   3. **原图绝不被改动**。批量只写新文件，因此天然可重来，也就不需要批量级撤销。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>批量预览的最大边长（像素）。取 720 是"看得清"与"切图够快"的折中。</summary>
        private const int BatchPreviewMaxSize = 720;

        /// <summary>预览防抖延迟（毫秒）。调参数时会连续触发，等手停下来再算。</summary>
        private const int BatchPreviewDelayMs = 260;

        private readonly ObservableCollection<BatchItemViewModel> _batchQueue =
            new ObservableCollection<BatchItemViewModel>();

        private readonly ObservableCollection<BatchStepViewModel> _batchSteps =
            new ObservableCollection<BatchStepViewModel>();

        private readonly List<BatchStepKindOption> _batchStepKindOptions = new List<BatchStepKindOption>
        {
            new BatchStepKindOption(BatchStepKind.Resize, "尺寸缩放"),
            new BatchStepKindOption(BatchStepKind.Adjustments, "基础调整"),
            new BatchStepKindOption(BatchStepKind.FlipRotate, "翻转旋转"),
            new BatchStepKindOption(BatchStepKind.Border, "边框"),
            new BatchStepKindOption(BatchStepKind.Watermark, "文字水印")
        };

        private readonly List<BatchOutputFormatOption> _batchOutputFormatOptions = new List<BatchOutputFormatOption>
        {
            new BatchOutputFormatOption(ImageFileFormat.Unknown, "沿用原格式"),
            new BatchOutputFormatOption(ImageFileFormat.Jpeg, "JPEG"),
            new BatchOutputFormatOption(ImageFileFormat.Png, "PNG"),
            new BatchOutputFormatOption(ImageFileFormat.Bmp, "BMP"),
            new BatchOutputFormatOption(ImageFileFormat.Tiff, "TIFF")
        };

        /// <summary>
        /// 本次运行已经分配出去的目标路径。
        ///
        /// 必须维护：来自不同文件夹的同名文件会在同一次运行里撞名，
        /// 而那时磁盘上还没有第二个文件，单靠 File.Exists 查不出来 —— 会静默互相覆盖。
        /// </summary>
        private readonly HashSet<string> _batchReservedOutputs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private DispatcherTimer _batchPreviewTimer;
        private CancellationTokenSource _batchRunCts;

        /// <summary>
        /// 正在进行的预览 / 批处理任务。
        ///
        /// 刻意持有引用而不是"发出去就不管"：一是避免 CS4014（项目保持零警告），
        /// 二是持有引用可以防止任务在被 GC 提前回收（fire-and-forget 的经典坑）。
        /// 两者内部都全包 try/catch，不会产生未观察异常。
        /// </summary>
        private Task _batchPreviewOperation;
        private Task _batchRunOperation;

        private BatchItemViewModel _selectedBatchItem;
        private BatchStepViewModel _selectedBatchStep;
        private BatchStepKind _batchNewStepKind = BatchStepKind.Resize;
        private BatchOutputFormatOption _batchOutputFormat;
        private string _batchOutputDirectory;
        private string _batchFileNameSuffix = "_批量";
        private int _batchJpegQuality = 92;
        private bool _batchIncludeSubfolders;
        private BitmapSource _batchPreviewImage;
        private string _batchPreviewText = "从上方队列里选一张图片，即可看到当前流水线的效果。";
        private bool _isBatchPreviewLoading;
        private int _batchPreviewRevision;
        private bool _isBatchRunning;
        private string _batchRunMessage = string.Empty;
        private int _batchRunCompleted;

        #region 集合与选中项

        /// <summary>待处理的文件队列。</summary>
        public ObservableCollection<BatchItemViewModel> BatchQueue
        {
            get { return _batchQueue; }
        }

        /// <summary>有序步骤列表。</summary>
        public ObservableCollection<BatchStepViewModel> BatchSteps
        {
            get { return _batchSteps; }
        }

        /// <summary>「添加步骤」可选类型。</summary>
        public IList<BatchStepKindOption> BatchStepKindOptions
        {
            get { return _batchStepKindOptions; }
        }

        /// <summary>输出格式可选值。</summary>
        public IList<BatchOutputFormatOption> BatchOutputFormatOptions
        {
            get { return _batchOutputFormatOptions; }
        }

        /// <summary>当前在队列里选中的项；变化时刷新预览。</summary>
        public BatchItemViewModel SelectedBatchItem
        {
            get { return _selectedBatchItem; }
            set
            {
                if (SetProperty(ref _selectedBatchItem, value, "SelectedBatchItem"))
                {
                    OnPropertyChanged("BatchTargetPathText");
                    RequestBatchPreview();
                }
            }
        }

        /// <summary>当前在步骤列表里选中的项；变化时切换参数编辑区。</summary>
        public BatchStepViewModel SelectedBatchStep
        {
            get { return _selectedBatchStep; }
            set
            {
                if (SetProperty(ref _selectedBatchStep, value, "SelectedBatchStep"))
                {
                    RaiseBatchEditPropertiesChanged();
                    RequestBatchPreview();
                }
            }
        }

        /// <summary>「添加步骤」下拉选中的类型。</summary>
        public BatchStepKind BatchNewStepKind
        {
            get { return _batchNewStepKind; }
            set { SetProperty(ref _batchNewStepKind, value, "BatchNewStepKind"); }
        }

        #endregion

        #region 输出设置

        /// <summary>输出目录。</summary>
        public string BatchOutputDirectory
        {
            get { return _batchOutputDirectory; }
            set
            {
                if (SetProperty(ref _batchOutputDirectory, value, "BatchOutputDirectory"))
                {
                    OnPropertyChanged("BatchOutputSummary");
                    OnPropertyChanged("BatchTargetPathText");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>输出格式（Unknown = 沿用原格式）。</summary>
        public BatchOutputFormatOption BatchOutputFormat
        {
            get
            {
                if (_batchOutputFormat == null)
                {
                    _batchOutputFormat = _batchOutputFormatOptions[0];
                }

                return _batchOutputFormat;
            }
            set
            {
                if (value != null && SetProperty(ref _batchOutputFormat, value, "BatchOutputFormat"))
                {
                    OnPropertyChanged("BatchOutputSummary");
                    OnPropertyChanged("IsBatchQualityEnabled");
                }
            }
        }

        /// <summary>文件名后缀（默认 “_批量”）。</summary>
        public string BatchFileNameSuffix
        {
            get { return _batchFileNameSuffix; }
            set
            {
                if (SetProperty(ref _batchFileNameSuffix, value, "BatchFileNameSuffix"))
                {
                    OnPropertyChanged("BatchOutputSummary");
                    OnPropertyChanged("BatchTargetPathText");
                }
            }
        }

        /// <summary>JPEG 输出质量（1~100）。</summary>
        public int BatchJpegQuality
        {
            get { return _batchJpegQuality; }
            set
            {
                int safe = value < 1 ? 1 : (value > 100 ? 100 : value);

                if (SetProperty(ref _batchJpegQuality, safe, "BatchJpegQuality"))
                {
                    OnPropertyChanged("BatchOutputSummary");
                }
            }
        }

        /// <summary>质量滑杆是否有效（只有 JPEG 用得上）。</summary>
        public bool IsBatchQualityEnabled
        {
            get { return BatchOutputFormat != null && BatchOutputFormat.Format == ImageFileFormat.Jpeg; }
        }

        /// <summary>添加文件夹时是否包含子文件夹。</summary>
        public bool BatchIncludeSubfolders
        {
            get { return _batchIncludeSubfolders; }
            set { SetProperty(ref _batchIncludeSubfolders, value, "BatchIncludeSubfolders"); }
        }

        /// <summary>输出设置的一行摘要。</summary>
        public string BatchOutputSummary
        {
            get
            {
                string format = BatchOutputFormat == null ? "沿用原格式" : BatchOutputFormat.Name;

                if (IsBatchQualityEnabled)
                {
                    format += "（质量 " + _batchJpegQuality.ToString(CultureInfo.InvariantCulture) + "）";
                }

                string directory = string.IsNullOrWhiteSpace(_batchOutputDirectory)
                    ? "尚未选择输出目录"
                    : _batchOutputDirectory;

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} · 后缀「{1}」 · {2}",
                    format,
                    string.IsNullOrEmpty(_batchFileNameSuffix) ? "（无）" : _batchFileNameSuffix,
                    directory);
            }
        }

        /// <summary>选中项预计的输出路径（界面上给个"这张会被写成什么"的直观提示）。</summary>
        public string BatchTargetPathText
        {
            get
            {
                if (_selectedBatchItem == null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(_batchOutputDirectory))
                {
                    return "选择输出目录后显示预计输出路径";
                }

                try
                {
                    ImageFileFormat format;
                    string extension;
                    string note;
                    ResolveBatchOutputSettings(_selectedBatchItem.FilePath, out format, out extension, out note);
                    BatchOutputNaming naming = new BatchOutputNaming(_batchOutputDirectory, _batchFileNameSuffix, extension);
                    return "预计输出：" + naming.PreviewPath(_selectedBatchItem.FilePath);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }
        }

        #endregion

        #region 预览

        /// <summary>预览图（降采样后套用当前流水线的结果）。</summary>
        public BitmapSource BatchPreviewImage
        {
            get { return _batchPreviewImage; }
            private set
            {
                if (SetProperty(ref _batchPreviewImage, value, "BatchPreviewImage"))
                {
                    OnPropertyChanged("HasBatchPreviewImage");
                }
            }
        }

        /// <summary>是否存在预览图。</summary>
        public bool HasBatchPreviewImage
        {
            get { return _batchPreviewImage != null; }
        }

        /// <summary>预览下方的说明文字。</summary>
        public string BatchPreviewText
        {
            get { return _batchPreviewText; }
            private set { SetProperty(ref _batchPreviewText, value, "BatchPreviewText"); }
        }

        /// <summary>预览推算出的实际输出宽度（像素）；无预览时为 0。</summary>
        public int BatchPreviewOutputWidth { get; private set; }

        /// <summary>预览推算出的实际输出高度（像素）；无预览时为 0。</summary>
        public int BatchPreviewOutputHeight { get; private set; }

        /// <summary>预览是否正在计算。</summary>
        public bool IsBatchPreviewLoading
        {
            get { return _isBatchPreviewLoading; }
            private set
            {
                if (SetProperty(ref _isBatchPreviewLoading, value, "IsBatchPreviewLoading"))
                {
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region 运行状态

        /// <summary>是否正在批量处理。</summary>
        public bool IsBatchRunning
        {
            get { return _isBatchRunning; }
            private set
            {
                if (SetProperty(ref _isBatchRunning, value, "IsBatchRunning"))
                {
                    OnPropertyChanged("BatchRunProgressText");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>批量处理的进度文本。</summary>
        public string BatchRunProgressText
        {
            get
            {
                if (!_isBatchRunning && _batchRunCompleted == 0)
                {
                    return _batchRunMessage;
                }

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}（{1}/{2}）",
                    _batchRunMessage,
                    _batchRunCompleted,
                    _batchQueue.Count);
            }
        }

        /// <summary>队列为空。</summary>
        public bool IsBatchQueueEmpty
        {
            get { return _batchQueue.Count == 0; }
        }

        /// <summary>当前流水线的可读描述（如 “尺寸缩放 → 文字水印”）。</summary>
        public string BatchStepChainText
        {
            get { return BatchPipeliner.Describe(SnapshotBatchSteps()); }
        }

        #endregion

        #region 命令

        /// <summary>添加文件到队列。</summary>
        public ICommand AddBatchFilesCommand { get; private set; }

        /// <summary>添加整个文件夹到队列。</summary>
        public ICommand AddBatchFolderCommand { get; private set; }

        /// <summary>把当前打开的图加入队列。</summary>
        public ICommand AddCurrentBatchItemCommand { get; private set; }

        /// <summary>移除队列选中项。</summary>
        public ICommand RemoveBatchQueueItemCommand { get; private set; }

        /// <summary>清空队列。</summary>
        public ICommand ClearBatchQueueCommand { get; private set; }

        /// <summary>添加一个步骤。</summary>
        public ICommand AddBatchStepCommand { get; private set; }

        /// <summary>移除选中步骤。</summary>
        public ICommand RemoveBatchStepCommand { get; private set; }

        /// <summary>步骤上移。</summary>
        public ICommand MoveBatchStepUpCommand { get; private set; }

        /// <summary>步骤下移。</summary>
        public ICommand MoveBatchStepDownCommand { get; private set; }

        /// <summary>清空步骤（退化为纯格式转换）。</summary>
        public ICommand ClearBatchStepsCommand { get; private set; }

        /// <summary>选择输出目录。</summary>
        public ICommand BrowseBatchOutputDirectoryCommand { get; private set; }

        /// <summary>立即刷新预览（不需要等防抖）。</summary>
        public ICommand RefreshBatchPreviewCommand { get; private set; }

        /// <summary>开始批量处理。</summary>
        public ICommand StartBatchRunCommand { get; private set; }

        /// <summary>取消批量处理。</summary>
        public ICommand CancelBatchRunCommand { get; private set; }

        /// <summary>装配批量相关命令（在构造函数中调用一次）。</summary>
        private void InitializeBatchCommands()
        {
            AddBatchFilesCommand = new RelayCommand(AddBatchFiles, () => !_isBatchRunning);
            AddBatchFolderCommand = new RelayCommand(AddBatchFolder, () => !_isBatchRunning);
            AddCurrentBatchItemCommand = new RelayCommand(
                AddCurrentBatchItem,
                () => HasDocument && !_isBatchRunning);

            RemoveBatchQueueItemCommand = new RelayCommand<BatchItemViewModel>(
                RemoveBatchQueueItem,
                item => item != null && !_isBatchRunning);

            ClearBatchQueueCommand = new RelayCommand(
                () =>
                {
                    _batchQueue.Clear();
                    SelectedBatchItem = null;
                    RefreshBatchQueueState();
                },
                () => _batchQueue.Count > 0 && !_isBatchRunning);

            AddBatchStepCommand = new RelayCommand(AddBatchStep, () => !_isBatchRunning);

            RemoveBatchStepCommand = new RelayCommand(
                RemoveBatchStep,
                () => _selectedBatchStep != null && !_isBatchRunning);

            MoveBatchStepUpCommand = new RelayCommand(
                () => MoveBatchStep(-1),
                () => CanMoveBatchStep(-1));

            MoveBatchStepDownCommand = new RelayCommand(
                () => MoveBatchStep(1),
                () => CanMoveBatchStep(1));

            ClearBatchStepsCommand = new RelayCommand(
                () =>
                {
                    _batchSteps.Clear();
                    SelectedBatchStep = null;
                    RefreshBatchStepState();
                },
                () => _batchSteps.Count > 0 && !_isBatchRunning);

            BrowseBatchOutputDirectoryCommand = new RelayCommand(
                BrowseBatchOutputDirectory,
                () => !_isBatchRunning);

            RefreshBatchPreviewCommand = new RelayCommand(
                () => BeginBatchPreviewRefresh(),
                () => !_isBatchPreviewLoading);

            StartBatchRunCommand = new RelayCommand(
                () => { _batchRunOperation = RunBatchAsync(); },
                () => _batchQueue.Count > 0
                      && !string.IsNullOrWhiteSpace(_batchOutputDirectory)
                      && !_isBatchRunning
                      && !IsBusy);

            CancelBatchRunCommand = new RelayCommand(
                () =>
                {
                    if (_batchRunCts != null)
                    {
                        _batchRunCts.Cancel();
                    }
                },
                () => _isBatchRunning);

            _batchPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(BatchPreviewDelayMs)
            };

            _batchPreviewTimer.Tick += (sender, args) =>
            {
                _batchPreviewTimer.Stop();
                BeginBatchPreviewRefresh();
            };
        }

        #endregion

        #region 队列操作

        private void AddBatchFiles()
        {
            string initialDirectory = _batchOutputDirectory;

            if (string.IsNullOrWhiteSpace(initialDirectory) && _selectedBatchItem != null)
            {
                initialDirectory = SafeGetDirectory(_selectedBatchItem.FilePath);
            }

            IReadOnlyList<string> files = _dialogService.ShowOpenImagesDialog("选择要批量处理的图片", initialDirectory);

            if (files == null || files.Count == 0)
            {
                return;
            }

            int added = 0;

            for (int i = 0; i < files.Count; i++)
            {
                if (AddBatchQueueItem(files[i]))
                {
                    added++;
                }
            }

            RefreshBatchQueueState();
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "已加入 {0} 张图片到批量队列。", added);
        }

        private void AddBatchFolder()
        {
            string folder = _dialogService.ShowFolderDialog("选择要批量处理的文件夹", _batchOutputDirectory);

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            SearchOption option = _batchIncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] candidates;

            try
            {
                candidates = Directory.GetFiles(folder, "*.*", option);
            }
            catch (Exception ex)
            {
                HandleError("读取文件夹失败。", ex, true);
                return;
            }

            int added = 0;

            for (int i = 0; i < candidates.Length; i++)
            {
                // 用统一的格式判定过滤，而不是分别列一堆通配符 —— 只支持的通配符会漏掉 .jfif/.dib 这类别名。
                if (ImageFileFormatHelper.FromPath(candidates[i]) == ImageFileFormat.Unknown)
                {
                    continue;
                }

                if (AddBatchQueueItem(candidates[i]))
                {
                    added++;
                }
            }

            RefreshBatchQueueState();

            if (added == 0)
            {
                _dialogService.ShowInformation(
                    "这个文件夹里没有找到支持的图片（jpg / png / bmp / tif / gif）。",
                    "批量处理");
            }
            else
            {
                StatusMessage = string.Format(CultureInfo.InvariantCulture, "已从文件夹加入 {0} 张图片。", added);
            }
        }

        private void AddCurrentBatchItem()
        {
            if (_document == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(_document.FilePath))
            {
                _dialogService.ShowInformation(
                    "当前图片尚未保存到文件，无法加入批量队列。请先保存后再添加。",
                    "批量处理");
                return;
            }

            if (AddBatchQueueItem(_document.FilePath))
            {
                RefreshBatchQueueState();
                StatusMessage = "已把当前图片加入批量队列。";
            }
            else
            {
                _dialogService.ShowInformation("这张图片已经在队列里了。", "批量处理");
            }
        }

        /// <summary>加入队列；重复（忽略大小写）返回 false。</summary>
        internal bool AddBatchQueueItem(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            for (int i = 0; i < _batchQueue.Count; i++)
            {
                if (string.Equals(_batchQueue[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            _batchQueue.Add(new BatchItemViewModel(filePath));

            // 第一张自动选中，省掉"还要点一下才有预览"的动作。
            if (_selectedBatchItem == null)
            {
                SelectedBatchItem = _batchQueue[0];
            }

            return true;
        }

        private void RemoveBatchQueueItem(BatchItemViewModel item)
        {
            if (item == null)
            {
                return;
            }

            _batchQueue.Remove(item);

            if (ReferenceEquals(_selectedBatchItem, item))
            {
                SelectedBatchItem = _batchQueue.Count > 0 ? _batchQueue[0] : null;
            }

            RefreshBatchQueueState();
        }

        private void RefreshBatchQueueState()
        {
            OnPropertyChanged("IsBatchQueueEmpty");
            OnPropertyChanged("BatchRunProgressText");
            OnPropertyChanged("BatchTargetPathText");
            RelayCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region 步骤操作

        private void AddBatchStep()
        {
            IBatchStep step = CreateBatchStep(_batchNewStepKind);
            BatchStepViewModel viewModel = new BatchStepViewModel(step, _batchSteps.Count + 1);
            _batchSteps.Add(viewModel);
            SelectedBatchStep = viewModel;
            RefreshBatchStepState();
            StatusMessage = string.Format(CultureInfo.InvariantCulture, "已添加步骤：{0}", step.DisplayName);
        }

        private static IBatchStep CreateBatchStep(BatchStepKind kind)
        {
            switch (kind)
            {
                case BatchStepKind.Adjustments:
                    return new BatchAdjustmentStep();
                case BatchStepKind.FlipRotate:
                    return new BatchFlipRotateStep();
                case BatchStepKind.Border:
                    return new BatchBorderStep();
                case BatchStepKind.Watermark:
                    return new BatchWatermarkStep();
                default:
                    return new BatchResizeStep();
            }
        }

        private void RemoveBatchStep()
        {
            BatchStepViewModel target = _selectedBatchStep;

            if (target == null)
            {
                return;
            }

            int index = _batchSteps.IndexOf(target);
            _batchSteps.Remove(target);

            if (_batchSteps.Count == 0)
            {
                SelectedBatchStep = null;
            }
            else
            {
                SelectedBatchStep = _batchSteps[index >= _batchSteps.Count ? _batchSteps.Count - 1 : index];
            }

            RefreshBatchStepState();
        }

        private bool CanMoveBatchStep(int delta)
        {
            if (_isBatchRunning || _selectedBatchStep == null)
            {
                return false;
            }

            int index = _batchSteps.IndexOf(_selectedBatchStep);
            int target = index + delta;

            return index >= 0 && target >= 0 && target < _batchSteps.Count;
        }

        private void MoveBatchStep(int delta)
        {
            if (!CanMoveBatchStep(delta))
            {
                return;
            }

            BatchStepViewModel item = _selectedBatchStep;
            int index = _batchSteps.IndexOf(item);
            int target = index + delta;

            _batchSteps.Move(index, target);
            RefreshBatchStepState();

            // SelectedBatchStep 本身没变，但按钮可用状态变了。
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void RefreshBatchStepState()
        {
            for (int i = 0; i < _batchSteps.Count; i++)
            {
                _batchSteps[i].SetOrder(i + 1);
            }

            OnPropertyChanged("BatchStepChainText");
            RaiseBatchEditPropertiesChanged();
            RequestBatchPreview();
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>把步骤列表取成一个**副本**，供流水线执行使用。</summary>
        private List<IBatchStep> SnapshotBatchSteps()
        {
            List<IBatchStep> steps = new List<IBatchStep>(_batchSteps.Count);

            for (int i = 0; i < _batchSteps.Count; i++)
            {
                steps.Add(_batchSteps[i].Step);
            }

            return steps;
        }

        /// <summary>就地修改选中步骤，并刷新显示与预览。</summary>
        private void UpdateSelectedBatchStep(Action<IBatchStep> mutate)
        {
            if (mutate == null)
            {
                return;
            }

            BatchStepViewModel viewModel = _selectedBatchStep;

            if (viewModel == null)
            {
                return;
            }

            mutate(viewModel.Step);
            viewModel.Refresh();
            OnPropertyChanged("BatchStepChainText");
            RequestBatchPreview();
        }

        #endregion

        #region 步骤参数编辑

        /// <summary>选中项是否是「尺寸缩放」步骤。</summary>
        public bool IsResizeStepSelected
        {
            get { return SelectedStepKindIs(BatchStepKind.Resize); }
        }

        /// <summary>选中项是否是「基础调整」步骤。</summary>
        public bool IsAdjustmentStepSelected
        {
            get { return SelectedStepKindIs(BatchStepKind.Adjustments); }
        }

        /// <summary>选中项是否是「翻转旋转」步骤。</summary>
        public bool IsFlipRotateStepSelected
        {
            get { return SelectedStepKindIs(BatchStepKind.FlipRotate); }
        }

        /// <summary>选中项是否是「边框」步骤。</summary>
        public bool IsBorderStepSelected
        {
            get { return SelectedStepKindIs(BatchStepKind.Border); }
        }

        /// <summary>选中项是否是「文字水印」步骤。</summary>
        public bool IsWatermarkStepSelected
        {
            get { return SelectedStepKindIs(BatchStepKind.Watermark); }
        }

        /// <summary>是否没有任何选中步骤（参数区显示提示文字）。</summary>
        public bool HasSelectedBatchStep
        {
            get { return _selectedBatchStep != null; }
        }

        private bool SelectedStepKindIs(BatchStepKind kind)
        {
            return _selectedBatchStep != null && _selectedBatchStep.Kind == kind;
        }

        /// <summary>选中步骤变化时一次性通知全部参数属性（避免每个 setter 里手工同步）。</summary>
        private void RaiseBatchEditPropertiesChanged()
        {
            string[] names =
            {
                "IsResizeStepSelected", "IsAdjustmentStepSelected", "IsFlipRotateStepSelected",
                "IsBorderStepSelected", "IsWatermarkStepSelected", "HasSelectedBatchStep",
                "BatchResizeMode", "BatchResizeValue", "BatchResizeWidth", "BatchResizeHeight",
                "BatchResizeKernel", "BatchResizeOnlyShrink",
                "BatchAdjustBrightness", "BatchAdjustContrast", "BatchAdjustSaturation", "BatchAdjustTemperature",
                "BatchFlipHorizontal", "BatchFlipVertical", "BatchRotateAngle",
                "BatchBorderStyle", "BatchBorderWidth", "BatchBorderColorText",
                "BatchWatermarkText", "BatchWatermarkFontSizeMode", "BatchWatermarkFontSize",
                "BatchWatermarkMargin", "BatchWatermarkOpacity", "BatchWatermarkAnchor",
                "BatchWatermarkBold", "BatchWatermarkShadow", "BatchWatermarkColorText"
            };

            for (int i = 0; i < names.Length; i++)
            {
                OnPropertyChanged(names[i]);
            }
        }

        private BatchResizeStep SelectedResizeStep
        {
            get { return _selectedBatchStep == null ? null : _selectedBatchStep.Step as BatchResizeStep; }
        }

        private BatchAdjustmentStep SelectedAdjustmentStep
        {
            get { return _selectedBatchStep == null ? null : _selectedBatchStep.Step as BatchAdjustmentStep; }
        }

        private BatchFlipRotateStep SelectedFlipRotateStep
        {
            get { return _selectedBatchStep == null ? null : _selectedBatchStep.Step as BatchFlipRotateStep; }
        }

        private BatchBorderStep SelectedBorderStep
        {
            get { return _selectedBatchStep == null ? null : _selectedBatchStep.Step as BatchBorderStep; }
        }

        private BatchWatermarkStep SelectedWatermarkStep
        {
            get { return _selectedBatchStep == null ? null : _selectedBatchStep.Step as BatchWatermarkStep; }
        }

        /// <summary>缩放模式。</summary>
        public BatchResizeMode BatchResizeMode
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null ? BatchResizeMode.LongEdge : step.Mode;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.Mode = value;
                    }
                });

                OnPropertyChanged("BatchResizeMode");
            }
        }

        /// <summary>Percent / LongEdge / FitWidth / FitHeight 模式的数值。</summary>
        public int BatchResizeValue
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null ? 1600 : step.Value;
            }
            set
            {
                int safe = value < 1 ? 1 : (value > Resampler.MaxDimension ? Resampler.MaxDimension : value);

                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.Value = safe;
                    }
                });

                OnPropertyChanged("BatchResizeValue");
            }
        }

        /// <summary>Exact 模式的目标宽度。</summary>
        public int BatchResizeWidth
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null ? 800 : step.Width;
            }
            set
            {
                int safe = value < 1 ? 1 : (value > Resampler.MaxDimension ? Resampler.MaxDimension : value);

                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.Width = safe;
                    }
                });

                OnPropertyChanged("BatchResizeWidth");
            }
        }

        /// <summary>Exact 模式的目标高度。</summary>
        public int BatchResizeHeight
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null ? 600 : step.Height;
            }
            set
            {
                int safe = value < 1 ? 1 : (value > Resampler.MaxDimension ? Resampler.MaxDimension : value);

                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.Height = safe;
                    }
                });

                OnPropertyChanged("BatchResizeHeight");
            }
        }

        /// <summary>重采样核。</summary>
        public ResampleKernel BatchResizeKernel
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null ? ResampleKernel.Bicubic : step.Kernel;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.Kernel = value;
                    }
                });

                OnPropertyChanged("BatchResizeKernel");
            }
        }

        /// <summary>只缩不放。</summary>
        public bool BatchResizeOnlyShrink
        {
            get
            {
                BatchResizeStep step = SelectedResizeStep;
                return step == null || step.OnlyShrink;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchResizeStep resize = step as BatchResizeStep;
                    if (resize != null)
                    {
                        resize.OnlyShrink = value;
                    }
                });

                OnPropertyChanged("BatchResizeOnlyShrink");
            }
        }

        /// <summary>批量调整：亮度。</summary>
        public double BatchAdjustBrightness
        {
            get { return GetSelectedAdjustment(a => a.Brightness); }
            set { SetSelectedAdjustments(value, null, null, null); OnPropertyChanged("BatchAdjustBrightness"); }
        }

        /// <summary>批量调整：对比度。</summary>
        public double BatchAdjustContrast
        {
            get { return GetSelectedAdjustment(a => a.Contrast); }
            set { SetSelectedAdjustments(null, value, null, null); OnPropertyChanged("BatchAdjustContrast"); }
        }

        /// <summary>批量调整：饱和度。</summary>
        public double BatchAdjustSaturation
        {
            get { return GetSelectedAdjustment(a => a.Saturation); }
            set { SetSelectedAdjustments(null, null, value, null); OnPropertyChanged("BatchAdjustSaturation"); }
        }

        /// <summary>批量调整：色温。</summary>
        public double BatchAdjustTemperature
        {
            get { return GetSelectedAdjustment(a => a.Temperature); }
            set { SetSelectedAdjustments(null, null, null, value); OnPropertyChanged("BatchAdjustTemperature"); }
        }

        private double GetSelectedAdjustment(Func<PixelAdjustments, double> selector)
        {
            BatchAdjustmentStep step = SelectedAdjustmentStep;
            PixelAdjustments value = step == null ? PixelAdjustments.Neutral : step.Adjustments;
            return selector(value ?? PixelAdjustments.Neutral);
        }

        private void SetSelectedAdjustments(double? brightness, double? contrast, double? saturation, double? temperature)
        {
            UpdateSelectedBatchStep(step =>
            {
                BatchAdjustmentStep adjustment = step as BatchAdjustmentStep;

                if (adjustment == null)
                {
                    return;
                }

                PixelAdjustments current = adjustment.Adjustments ?? PixelAdjustments.Neutral;
                adjustment.Adjustments = new PixelAdjustments(
                    brightness ?? current.Brightness,
                    contrast ?? current.Contrast,
                    saturation ?? current.Saturation,
                    temperature ?? current.Temperature);
            });
        }

        /// <summary>水平翻转。</summary>
        public bool BatchFlipHorizontal
        {
            get
            {
                BatchFlipRotateStep step = SelectedFlipRotateStep;
                return step != null && step.FlipHorizontal;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchFlipRotateStep flip = step as BatchFlipRotateStep;
                    if (flip != null)
                    {
                        flip.FlipHorizontal = value;
                    }
                });

                OnPropertyChanged("BatchFlipHorizontal");
            }
        }

        /// <summary>垂直翻转。</summary>
        public bool BatchFlipVertical
        {
            get
            {
                BatchFlipRotateStep step = SelectedFlipRotateStep;
                return step != null && step.FlipVertical;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchFlipRotateStep flip = step as BatchFlipRotateStep;
                    if (flip != null)
                    {
                        flip.FlipVertical = value;
                    }
                });

                OnPropertyChanged("BatchFlipVertical");
            }
        }

        /// <summary>顺时针 90° 倍数旋转。</summary>
        public RotationAngle BatchRotateAngle
        {
            get
            {
                BatchFlipRotateStep step = SelectedFlipRotateStep;
                return step == null ? RotationAngle.None : step.Angle;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchFlipRotateStep flip = step as BatchFlipRotateStep;
                    if (flip != null)
                    {
                        flip.Angle = value;
                    }
                });

                OnPropertyChanged("BatchRotateAngle");
            }
        }

        /// <summary>边框样式。</summary>
        public BorderStyle BatchBorderStyle
        {
            get
            {
                BatchBorderStep step = SelectedBorderStep;
                return step == null ? BorderStyle.Solid : step.Style;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchBorderStep border = step as BatchBorderStep;
                    if (border != null)
                    {
                        border.Style = value;
                    }
                });

                OnPropertyChanged("BatchBorderStyle");
            }
        }

        /// <summary>边框宽度（像素）。</summary>
        public int BatchBorderWidth
        {
            get
            {
                BatchBorderStep step = SelectedBorderStep;
                return step == null ? 12 : step.Width;
            }
            set
            {
                int safe = value < 0 ? 0 : (value > 2000 ? 2000 : value);

                UpdateSelectedBatchStep(step =>
                {
                    BatchBorderStep border = step as BatchBorderStep;
                    if (border != null)
                    {
                        border.Width = safe;
                    }
                });

                OnPropertyChanged("BatchBorderWidth");
            }
        }

        /// <summary>边框颜色文本（供界面按钮显示当前色）。</summary>
        public string BatchBorderColorText
        {
            get
            {
                BatchBorderStep step = SelectedBorderStep;
                return step == null ? "白色" : BatchColorNames.Describe(step.Color);
            }
        }

        /// <summary>水印文字。</summary>
        public string BatchWatermarkText
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? string.Empty : step.Text;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Text = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkText");
            }
        }

        /// <summary>水印字号计量方式。</summary>
        public WatermarkSizeMode BatchWatermarkFontSizeMode
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? WatermarkSizeMode.RelativeToWidth : step.SizeMode;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.SizeMode = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkFontSizeMode");
            }
        }

        /// <summary>水印字号（像素或宽度百分比，取决于计量方式）。</summary>
        public double BatchWatermarkFontSize
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? 5.0 : step.FontSize;
            }
            set
            {
                double safe = double.IsNaN(value) || value < 0.0 ? 0.0 : value;

                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.FontSize = safe;
                    }
                });

                OnPropertyChanged("BatchWatermarkFontSize");
            }
        }

        /// <summary>水印边距（计量方式与字号相同）。</summary>
        public double BatchWatermarkMargin
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? 2.0 : step.Margin;
            }
            set
            {
                double safe = double.IsNaN(value) || value < 0.0 ? 0.0 : value;

                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Margin = safe;
                    }
                });

                OnPropertyChanged("BatchWatermarkMargin");
            }
        }

        /// <summary>水印不透明度（0~255）。</summary>
        public byte BatchWatermarkOpacity
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? (byte)170 : step.Opacity;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Opacity = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkOpacity");
            }
        }

        /// <summary>水印锚点。</summary>
        public TextAnchor BatchWatermarkAnchor
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? TextAnchor.BottomRight : step.Anchor;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Anchor = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkAnchor");
            }
        }

        /// <summary>水印加粗。</summary>
        public bool BatchWatermarkBold
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null || step.Bold;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Bold = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkBold");
            }
        }

        /// <summary>水印是否加深色投影（浅色照片上更清楚，深色照片上反而变脏）。</summary>
        public bool BatchWatermarkShadow
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step != null && step.Shadow;
            }
            set
            {
                UpdateSelectedBatchStep(step =>
                {
                    BatchWatermarkStep watermark = step as BatchWatermarkStep;
                    if (watermark != null)
                    {
                        watermark.Shadow = value;
                    }
                });

                OnPropertyChanged("BatchWatermarkShadow");
            }
        }

        /// <summary>水印颜色文本。</summary>
        public string BatchWatermarkColorText
        {
            get
            {
                BatchWatermarkStep step = SelectedWatermarkStep;
                return step == null ? "白色" : BatchColorNames.Describe(step.Color);
            }
        }

        /// <summary>设置水印颜色（由界面上的颜色按钮调用）。</summary>
        public void SetBatchWatermarkColor(Color color)
        {
            UpdateSelectedBatchStep(step =>
            {
                BatchWatermarkStep watermark = step as BatchWatermarkStep;
                if (watermark != null)
                {
                    watermark.Color = color;
                }
            });

            OnPropertyChanged("BatchWatermarkColorText");
        }

        /// <summary>设置边框颜色（由界面上的颜色按钮调用）。</summary>
        public void SetBatchBorderColor(Color color)
        {
            UpdateSelectedBatchStep(step =>
            {
                BatchBorderStep border = step as BatchBorderStep;
                if (border != null)
                {
                    border.Color = color;
                }
            });

            OnPropertyChanged("BatchBorderColorText");
        }

        #endregion

        #region 输出目录

        private void BrowseBatchOutputDirectory()
        {
            string initial = _batchOutputDirectory;

            if (string.IsNullOrWhiteSpace(initial) && _document != null)
            {
                initial = SafeGetDirectory(_document.FilePath);
            }

            string folder = _dialogService.ShowFolderDialog("选择批量输出的文件夹", initial);

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            BatchOutputDirectory = folder;
        }

        /// <summary>
        /// 解析某张源图对应的输出格式与扩展名。
        ///
        /// GIF 特殊处理：GIF 只有 256 色且可能有多帧，把照片写回 GIF 会静默毁掉画质
        /// （多帧还会被压成单帧），因此"沿用原格式"遇到 GIF 时改写成 PNG 并给出说明。
        /// </summary>
        private void ResolveBatchOutputSettings(
            string sourcePath,
            out ImageFileFormat format,
            out string forcedExtension,
            out string note)
        {
            note = null;
            ImageFileFormat requested = BatchOutputFormat == null
                ? ImageFileFormat.Unknown
                : BatchOutputFormat.Format;

            if (requested == ImageFileFormat.Unknown)
            {
                format = ImageFileFormatHelper.FromPath(sourcePath);

                if (format == ImageFileFormat.Gif)
                {
                    format = ImageFileFormat.Png;
                    forcedExtension = ".png";
                    note = "源文件是 GIF，已改写为 PNG（GIF 只有 256 色且多帧会被压成单帧）";
                    return;
                }

                forcedExtension = null;

                if (format == ImageFileFormat.Unknown)
                {
                    format = ImageFileFormat.Png;
                    forcedExtension = ".png";
                    note = "无法识别源格式，已按 PNG 保存";
                }

                return;
            }

            format = requested;
            forcedExtension = ImageFileFormatHelper.GetDefaultExtension(format);
        }

        #endregion

        #region 预览

        /// <summary>安排一次预览刷新（带防抖）。</summary>
        private void RequestBatchPreview()
        {
            if (_batchPreviewTimer == null)
            {
                return;
            }

            _batchPreviewTimer.Stop();

            if (_selectedBatchItem == null)
            {
                BatchPreviewImage = null;
                BatchPreviewText = "从上方队列里选一张图片，即可看到当前流水线的效果。";
                return;
            }

            _batchPreviewTimer.Start();
        }

        /// <summary>立即刷新预览（跳过防抖）。</summary>
        private void BeginBatchPreviewRefresh()
        {
            _batchPreviewOperation = RunBatchPreviewAsync();
        }

        /// <summary>
        /// 生成预览。
        ///
        /// 为什么不复用 <c>LoadPreviewAsync</c> 取降采样图：那条路径**不做 EXIF 方向校正**
        /// （见 WpfImageService.Decode 的 decodePixelWidth 分支），手机竖拍的照片会在预览里躺倒，
        /// 而实际输出是按校正后的方向来的 —— 预览会骗人。这里改为整幅解码（方向正确）
        /// 再降采样，然后用真实的缩放比构造 <see cref="BatchContext"/>。
        ///
        /// 返回 Task 而非 async void，是为了让自检能断言"预览推算的输出尺寸 == 实际输出尺寸"
        /// 这件事，而不是只能肉眼看。
        /// </summary>
        internal async Task RunBatchPreviewAsync()
        {
            int revision = ++_batchPreviewRevision;
            BatchItemViewModel item = _selectedBatchItem;

            if (item == null)
            {
                BatchPreviewImage = null;
                BatchPreviewOutputWidth = 0;
                BatchPreviewOutputHeight = 0;
                BatchPreviewText = "从上方队列里选一张图片，即可看到当前流水线的效果。";
                return;
            }

            IsBatchPreviewLoading = true;

            try
            {
                ImageLoadResult loaded = await _imageService
                    .LoadAsync(item.FilePath, CancellationToken.None)
                    .ConfigureAwait(true);

                if (revision != _batchPreviewRevision)
                {
                    return;
                }

                ImageDocument document = ImageDocument.FromLoadResult(loaded);
                PixelBuffer full = PixelBuffer.FromBitmap(document.Bitmap);
                PixelBuffer small = full.GetPreview(BatchPreviewMaxSize, BatchPreviewMaxSize);

                // 真实缩放比：GetPreview 按整数因子抽样，所以这里是精确值。
                double scale = full.Width <= 0 ? 1.0 : small.Width / (double)full.Width;
                BatchContext context = new BatchContext(scale, document.DpiX, document.DpiY);
                List<IBatchStep> steps = SnapshotBatchSteps();

                PixelBuffer result = await Task
                    .Run(() => BatchPipeliner.Run(small, steps, context, CancellationToken.None))
                    .ConfigureAwait(true);

                if (revision != _batchPreviewRevision)
                {
                    return;
                }

                BatchPreviewImage = PixelBuffer.ToBitmap(result, 96.0, 96.0);

                BatchPreviewOutputWidth = scale <= 0.0 ? result.Width : (int)Math.Round(result.Width / scale);
                BatchPreviewOutputHeight = scale <= 0.0 ? result.Height : (int)Math.Round(result.Height / scale);

                BatchPreviewText = string.Format(
                    CultureInfo.InvariantCulture,
                    "预览（{0}×{1} 降采样）· 流水线：{2} · 实际输出约 {3} × {4} px",
                    small.Width,
                    small.Height,
                    BatchPipeliner.Describe(steps),
                    BatchPreviewOutputWidth,
                    BatchPreviewOutputHeight);
            }
            catch (Exception ex)
            {
                if (revision == _batchPreviewRevision)
                {
                    BatchPreviewImage = null;
                    BatchPreviewOutputWidth = 0;
                    BatchPreviewOutputHeight = 0;
                    BatchPreviewText = "预览失败：" + ex.Message;
                }
            }
            finally
            {
                if (revision == _batchPreviewRevision)
                {
                    IsBatchPreviewLoading = false;
                }
            }
        }

        #endregion

        #region 执行

        /// <summary>
        /// 执行一次批量处理。
        ///
        /// 返回 Task 而不是 async void，是为了让自检能真正等待它结束 ——
        /// "批量跑完没有"这件事必须可观测，否则端到端断言只能靠 sleep 猜。
        /// 命令入口用 `() => { RunBatchAsync(); }` 丢弃该 Task（内部已全包 try/catch）。
        /// </summary>
        internal async Task RunBatchAsync()
        {
            if (_isBatchRunning || _batchQueue.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_batchOutputDirectory))
            {
                _dialogService.ShowInformation("请先选择输出文件夹。", "批量处理");
                return;
            }

            List<IBatchStep> steps = SnapshotBatchSteps();
            List<BatchItemViewModel> items = new List<BatchItemViewModel>(_batchQueue);

            string suffix = BatchOutputNaming.SanitizeFileNamePart(_batchFileNameSuffix);
            string outputDirectory = _batchOutputDirectory;

            string formatText = BatchOutputFormat == null ? "沿用原格式" : BatchOutputFormat.Name;

            if (IsBatchQualityEnabled)
            {
                formatText += "（质量 " + _batchJpegQuality.ToString(CultureInfo.InvariantCulture) + "）";
            }

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "将处理 {0} 张图片。\n\n流水线：{1}\n输出格式：{2}\n文件名：原名 + 「{3}」（重名自动加序号）\n输出到：{4}\n\n原文件不会被改动。开始处理吗？",
                items.Count,
                BatchPipeliner.Describe(steps),
                formatText,
                string.IsNullOrEmpty(suffix) ? "（无后缀）" : suffix,
                outputDirectory);

            if (_dialogService.Confirm(message, "批量处理") != ConfirmResult.Yes)
            {
                return;
            }

            int succeeded = 0;
            int failed = 0;
            int canceled = 0;

            _batchReservedOutputs.Clear();
            _batchRunCompleted = 0;

            // 输出目录先建好：写在循环里会让"目录不可写"在每一张上都失败一次。
            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex)
            {
                HandleError("无法创建输出目录。", ex, true);
                return;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            _batchRunCts = cts;
            _batchRunMessage = "正在批量处理…";
            IsBatchRunning = true;
            OnPropertyChanged("BatchRunProgressText");

            ProgressReporter progress = CreateProgressReporter("正在批量处理…");

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    BatchItemViewModel item = items[i];

                    if (cts.IsCancellationRequested)
                    {
                        item.Status = "已取消";
                        canceled++;
                        _batchRunCompleted = i + 1;
                        OnPropertyChanged("BatchRunProgressText");
                        progress.Report((i + 1) / (double)items.Count, "已取消（" + (i + 1) + "/" + items.Count + "）");
                        continue;
                    }

                    item.IsFailed = false;
                    item.Status = "正在处理…";

                    try
                    {
                        ImageLoadResult loaded = await _imageService
                            .LoadAsync(item.FilePath, cts.Token)
                            .ConfigureAwait(true);

                        ImageDocument document = ImageDocument.FromLoadResult(loaded);

                        ImageFileFormat format;
                        string forcedExtension;
                        string formatNote;
                        ResolveBatchOutputSettings(item.FilePath, out format, out forcedExtension, out formatNote);

                        BatchOutputNaming naming = new BatchOutputNaming(outputDirectory, suffix, forcedExtension);
                        string outputPath = naming.NextOutputPath(item.FilePath, _batchReservedOutputs);

                        // 先占位再写盘：否则同一次运行里第二个同名文件会把第一个覆盖掉。
                        _batchReservedOutputs.Add(outputPath);

                        PixelBuffer source = PixelBuffer.FromBitmap(document.Bitmap);
                        CancellationToken token = cts.Token;

                        PixelBuffer result = await Task
                            .Run(() => BatchPipeliner.Run(source, steps, BatchContext.FullResolution, token), token)
                            .ConfigureAwait(true);

                        BitmapSource bitmap = PixelBuffer.ToBitmap(result, document.DpiX, document.DpiY);

                        ImageSaveOptions options = new ImageSaveOptions
                        {
                            Format = format,
                            QualityLevel = _batchJpegQuality,
                            TiffCompression = "LZW",
                            PreserveDpi = true
                        };

                        long bytes = await _imageService
                            .SaveAsync(bitmap, outputPath, options, token)
                            .ConfigureAwait(true);

                        item.OutputPath = outputPath;
                        item.Status = string.Format(
                            CultureInfo.InvariantCulture,
                            "完成 · {0} × {1} px · {2}{3}",
                            result.Width,
                            result.Height,
                            FormatBytes(bytes),
                            formatNote == null ? string.Empty : " · " + formatNote);

                        succeeded++;
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "已取消";
                        canceled++;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "失败：" + ex.Message;
                        item.IsFailed = true;
                        failed++;
                    }

                    _batchRunCompleted = i + 1;
                    OnPropertyChanged("BatchRunProgressText");
                    progress.Report(
                        _batchRunCompleted / (double)Math.Max(1, items.Count),
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "正在批量处理（{0}/{1}）：{2}",
                            _batchRunCompleted,
                            items.Count,
                            item.FileName));
                }

                _batchRunMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "批量处理完成：成功 {0}，失败 {1}{2}",
                    succeeded,
                    failed,
                    canceled > 0 ? "，取消 " + canceled : string.Empty);

                StatusMessage = _batchRunMessage;

                if (failed > 0)
                {
                    _dialogService.ShowInformation(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}\n\n失败项可在队列里查看原因（状态列标红）。",
                            _batchRunMessage),
                        "批量处理");
                }
            }
            catch (Exception ex)
            {
                HandleError("批量处理过程中出错。", ex, true);
                _batchRunMessage = "批量处理中断";
            }
            finally
            {
                progress.Complete();
                IsBatchRunning = false;

                if (_batchRunCts != null)
                {
                    _batchRunCts.Dispose();
                    _batchRunCts = null;
                }

                OnPropertyChanged("BatchRunProgressText");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "—";
            }

            double size = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int unitIndex = 0;

            while (size >= 1024.0 && unitIndex < units.Length - 1)
            {
                size /= 1024.0;
                unitIndex++;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unitIndex]);
        }

        #endregion
    }
}
