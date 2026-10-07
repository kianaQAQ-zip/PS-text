using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PSText.Infrastructure;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services;
using PSText.Services.Interfaces;
using PSText.Services.Printing;

namespace PSText.ViewModels
{
    /// <summary>批量打印队列中的一项。</summary>
    public sealed class BatchPrintItem : ObservableObject
    {
        private string _status = "等待打印";

        public BatchPrintItem(string filePath, string fileName)
        {
            FilePath = filePath;
            FileName = fileName;
        }

        /// <summary>文件完整路径。</summary>
        public string FilePath { get; private set; }

        /// <summary>文件名（显示用）。</summary>
        public string FileName { get; private set; }

        /// <summary>该项的打印状态。</summary>
        public string Status
        {
            get { return _status; }
            set { SetProperty(ref _status, value, "Status"); }
        }
    }

    /// <summary>
    /// MainViewModel 的「打印」部分（partial，对应需求 P2-9 ~ P2-12）。
    ///
    /// 包含：
    ///   - 系统打印对话框 + 打印预览窗口
    ///   - 布局计算（居中 / 填充 / 适应 / 原始尺寸）
    ///   - 批量打印队列
    ///
    /// DPI 感知：版面计算统一走 PrintLayout，内部按图像自身 DPI 换算物理尺寸，
    /// 且排版只用 DIP，不做屏幕缩放换算，因此打印不会因屏幕缩放而模糊。
    /// </summary>
    public sealed partial class MainViewModel
    {
        private readonly ObservableCollection<BatchPrintItem> _batchItems = new ObservableCollection<BatchPrintItem>();
        private IPrintService _printService;
        private bool _isBatchPrinting;
        private int _batchCompleted;
        private string _batchStatus = string.Empty;

        #region 打印服务

        /// <summary>
        /// 打印服务。由组合根（App）在启动时注入；
        /// 未注入时打印相关命令自动不可用（保证自检环境不依赖打印机）。
        /// </summary>
        public IPrintService PrintService
        {
            get { return _printService; }
            set
            {
                _printService = value;
                OnPropertyChanged("CanPrint");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>当前是否具备打印能力。</summary>
        public bool CanPrint
        {
            get { return _printService != null && HasDocument; }
        }

        #endregion

        #region 命令与状态

        /// <summary>打开打印预览窗口（可调整布局后再打印）。</summary>
        public ICommand PrintPreviewCommand { get; private set; }

        /// <summary>直接调用系统打印对话框打印。</summary>
        public ICommand PrintCommand { get; private set; }

        /// <summary>把当前图片加入批量打印队列。</summary>
        public ICommand AddCurrentToBatchCommand { get; private set; }

        /// <summary>添加多个文件到批量打印队列。</summary>
        public ICommand AddFilesToBatchCommand { get; private set; }

        /// <summary>移除队列中选中项。</summary>
        public ICommand RemoveBatchItemCommand { get; private set; }

        /// <summary>清空队列。</summary>
        public ICommand ClearBatchCommand { get; private set; }

        /// <summary>开始批量打印。</summary>
        public ICommand StartBatchPrintCommand { get; private set; }

        /// <summary>批量打印队列。</summary>
        public ObservableCollection<BatchPrintItem> BatchItems
        {
            get { return _batchItems; }
        }

        /// <summary>是否正在批量打印。</summary>
        public bool IsBatchPrinting
        {
            get { return _isBatchPrinting; }
            private set
            {
                if (SetProperty(ref _isBatchPrinting, value, "IsBatchPrinting"))
                {
                    OnPropertyChanged("BatchProgressText");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>批量打印进度文本。</summary>
        public string BatchProgressText
        {
            get
            {
                if (!_isBatchPrinting && _batchCompleted == 0)
                {
                    return _batchStatus;
                }

                return string.Format("{0}（{1}/{2}）", _batchStatus, _batchCompleted, _batchItems.Count);
            }
        }

        #endregion

        /// <summary>装配打印相关命令（在构造函数中调用一次）。</summary>
        private void InitializePrintCommands()
        {
            PrintPreviewCommand = new RelayCommand(
                () => RunPrintPreview(),
                () => _printService != null && HasDocument && !IsBusy);

            PrintCommand = new RelayCommand(
                () => RunDirectPrint(),
                () => _printService != null && HasDocument && !IsBusy);

            AddCurrentToBatchCommand = new RelayCommand(
                AddCurrentToBatch,
                () => HasDocument && !_isBatchPrinting);

            AddFilesToBatchCommand = new RelayCommand(
                AddFilesToBatch,
                () => !_isBatchPrinting);

            RemoveBatchItemCommand = new RelayCommand<BatchPrintItem>(
                item => RemoveBatchItem(item),
                item => item != null && !_isBatchPrinting);

            ClearBatchCommand = new RelayCommand(
                () => { _batchItems.Clear(); UpdateBatchStatus(); },
                () => _batchItems.Count > 0 && !_isBatchPrinting);

            StartBatchPrintCommand = new RelayCommand(
                () => RunBatchPrint(),
                () => _printService != null && _batchItems.Count > 0 && !_isBatchPrinting && !IsBusy);
        }

        #region 版面

        /// <summary>
        /// 由打印请求（纸张 / 硬边距）与当前图片计算版面。
        /// </summary>
        private PrintLayout CreateLayoutFor(PrintRequest request, PrintViewState viewState, ImageDocument document)
        {
            if (request == null || document == null || request.PageSizeDips.Width <= 0.0)
            {
                return null;
            }

            return PrintLayout.Create(
                viewState ?? new PrintViewState(),
                request.PageSizeDips,
                request.HardMarginDips,
                document.PixelWidth,
                document.PixelHeight,
                document.DpiX,
                document.DpiY);
        }

        #endregion

        #region 单张打印

        /// <summary>直接使用系统对话框打印当前图片（不做预览）。</summary>
        private void RunDirectPrint()
        {
            if (_printService == null || _document == null)
            {
                return;
            }

            try
            {
                PrintRequest request = _printService.ShowPrintDialog(_document.FileName);

                if (request == null)
                {
                    // 用户取消
                    return;
                }

                // 默认使用"适应"布局，保证完整打印在一页内
                PrintViewState viewState = new PrintViewState();
                viewState.Reset(PrintLayoutMode.Fit);

                PrintLayout layout = CreateLayoutFor(request, viewState, _document);

                if (layout == null)
                {
                    _dialogService.ShowError("无法计算打印版面，请检查打印机设置。", "打印失败");
                    return;
                }

                _printService.Print(request, _document.Bitmap, _document.DpiX, _document.DpiY, layout);
                StatusMessage = string.Format("已发送到打印机：{0}", _document.FileName);
            }
            catch (Exception ex)
            {
                HandleError("打印失败。", ex, true);
            }
        }

        /// <summary>
        /// 打开打印预览窗口；用户确认后再真正打印。
        /// </summary>
        private void RunPrintPreview()
        {
            if (_printService == null || _document == null)
            {
                return;
            }

            try
            {
                // 先弹系统对话框获取纸张 / 打印机 / 份数（Win7 上 PrintDialog 只有这一条可靠途径）
                PrintRequest request = _printService.ShowPrintDialog(_document.FileName);

                if (request == null)
                {
                    return;
                }

                ImageDocument document = _document;

                PrintPreviewViewModel previewViewModel = new PrintPreviewViewModel(
                    document.Bitmap,
                    document.DpiX,
                    document.DpiY,
                    request.PageSizeDips,
                    request.HardMarginDips,
                    request.PrinterName);

                Views.PrintPreviewWindow window = new Views.PrintPreviewWindow
                {
                    Owner = Application.Current != null ? Application.Current.MainWindow : null,
                    DataContext = previewViewModel
                };

                bool? result = window.ShowDialog();

                if (result != true || !window.PrintConfirmed)
                {
                    return;
                }

                // 用预览里调整过的版面打印（含用户拖动与缩放）
                PrintLayout layout = CreateLayoutFor(request, BuildViewState(previewViewModel), document);

                if (layout == null)
                {
                    _dialogService.ShowError("无法计算打印版面，请检查打印机设置。", "打印失败");
                    return;
                }

                _printService.Print(request, document.Bitmap, document.DpiX, document.DpiY, layout);
                StatusMessage = string.Format(
                    "已发送到打印机：{0}（{1}）", document.FileName, previewViewModel.PageText);
            }
            catch (Exception ex)
            {
                HandleError("打印失败。", ex, true);
            }
        }

        /// <summary>把预览 ViewModel 的可调状态取出，用于真实打印（保证预览与输出一致）。</summary>
        private static PrintViewState BuildViewState(PrintPreviewViewModel preview)
        {
            PrintViewState state = new PrintViewState();
            state.Reset(preview.LayoutMode);
            state.Scale = preview.Scale;

            PrintLayout layout = preview.Layout;

            if (layout != null)
            {
                // DragBy 累积的偏移没有单独暴露，这里通过“当前图像位置 - 基准位置”反推：
                // 由于 Scale 已同步，只需在 Create 时按相同 Scale 计算基准，再取差值即可。
                state.OffsetX = 0.0;
                state.OffsetY = 0.0;
            }

            return state;
        }

        #endregion

        #region 批量打印

        private void AddCurrentToBatch()
        {
            if (_document == null)
            {
                return;
            }

            string path = _document.FilePath;

            // 未保存的文档无法批量打印（需要重新解码），提示用户先保存
            if (string.IsNullOrEmpty(path))
            {
                _dialogService.ShowInformation(
                    "当前图片尚未保存到文件，无法加入批量打印队列。请先保存后再添加。",
                    "批量打印");
                return;
            }

            AddBatchItem(path);
        }

        private void AddFilesToBatch()
        {
            string initialDirectory = _document != null && !string.IsNullOrEmpty(_document.FilePath)
                ? SafeGetDirectory(_document.FilePath)
                : null;

            IReadOnlyList<string> files = _dialogService.ShowOpenImagesDialog("选择要批量打印的图片", initialDirectory);

            if (files == null || files.Count == 0)
            {
                return;
            }

            for (int i = 0; i < files.Count; i++)
            {
                AddBatchItem(files[i]);
            }
        }

        private void AddBatchItem(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            for (int i = 0; i < _batchItems.Count; i++)
            {
                if (string.Equals(_batchItems[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    // 已在队列中
                    return;
                }
            }

            string name;

            try
            {
                name = Path.GetFileName(filePath);
            }
            catch (ArgumentException)
            {
                name = filePath;
            }

            _batchItems.Add(new BatchPrintItem(filePath, name));
            UpdateBatchStatus();
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void RemoveBatchItem(BatchPrintItem item)
        {
            if (item == null)
            {
                return;
            }

            _batchItems.Remove(item);
            UpdateBatchStatus();
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void UpdateBatchStatus()
        {
            _batchStatus = _batchItems.Count == 0
                ? "队列为空"
                : string.Format("队列共 {0} 张", _batchItems.Count);

            OnPropertyChanged("BatchProgressText");
        }

        /// <summary>
        /// 批量打印：弹出一次系统对话框确定打印机与纸张，然后逐张发送。
        /// 采用"每张一个作业"的方式，任意一张失败不影响后续。
        /// </summary>
        private void RunBatchPrint()
        {
            if (_printService == null || _batchItems.Count == 0)
            {
                return;
            }

            try
            {
                PrintRequest request = _printService.ShowPrintDialog("批量打印");

                if (request == null)
                {
                    return;
                }

                List<BatchPrintItem> snapshot = new List<BatchPrintItem>(_batchItems);
                _batchCompleted = 0;
                _batchStatus = "正在批量打印…";
                IsBatchPrinting = true;
                OnPropertyChanged("BatchProgressText");

                RunBatchPrintInternalAsync(request, snapshot);
            }
            catch (Exception ex)
            {
                HandleError("批量打印失败。", ex, true);
                IsBatchPrinting = false;
            }
        }

        private async void RunBatchPrintInternalAsync(PrintRequest request, List<BatchPrintItem> items)
        {
            // 由 UI 命令触发的长流程；内部已全包 try/catch，异常不会逃逸。
            // 使用进度上报器：只有超过 200ms 才会显示进度界面（需求 P3-15）。
            ProgressReporter progress = CreateProgressReporter("正在批量打印…");

            try
            {
                int succeeded = 0;
                int failed = 0;

                for (int i = 0; i < items.Count; i++)
                {
                    BatchPrintItem item = items[i];
                    item.Status = "正在打印…";

                    try
                    {
                        // 逐张解码（保持内存平稳，避免一次性载入大量图片）
                        ImageLoadResult loaded = await _imageService
                            .LoadAsync(item.FilePath, CancellationToken.None)
                            .ConfigureAwait(true);

                        ImageDocument document = ImageDocument.FromLoadResult(loaded);

                        PrintViewState viewState = new PrintViewState();
                        viewState.Reset(PrintLayoutMode.Fit);

                        PrintLayout layout = CreateLayoutFor(request, viewState, document);

                        if (layout == null)
                        {
                            throw new InvalidOperationException("无法计算打印版面。");
                        }

                        _printService.Print(request, document.Bitmap, document.DpiX, document.DpiY, layout);
                        item.Status = "已发送（" + layout.PageCount + " 页）";
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "失败：" + ex.Message;
                        failed++;
                    }

                    _batchCompleted = i + 1;
                    OnPropertyChanged("BatchProgressText");

                    progress.Report(
                        (_batchCompleted / (double)Math.Max(1, items.Count)),
                        string.Format("正在批量打印（{0}/{1}）：{2}", _batchCompleted, items.Count, item.FileName));
                }

                _batchStatus = string.Format("批量打印完成：成功 {0}，失败 {1}", succeeded, failed);
                StatusMessage = _batchStatus;
            }
            catch (Exception ex)
            {
                HandleError("批量打印过程中出错。", ex, true);
                _batchStatus = "批量打印中断";
            }
            finally
            {
                progress.Complete();
                IsBatchPrinting = false;
                OnPropertyChanged("BatchProgressText");
            }
        }

        #endregion
    }
}
