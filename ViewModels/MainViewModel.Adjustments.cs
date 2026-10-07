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
    /// MainViewModel 的「基础调整 + 撤销重做」部分（partial 拆分，便于维护）。
    ///
    /// 与 MainViewModel.cs 共享同一实例状态；拆分依据是职责：
    ///   - 本文件：调整参数、调整会话、降采样预览、撤销 / 重做的对外命令
    ///   - MainViewModel.cs：加载 / 保存、画布缩放平移、状态栏
    /// </summary>
    public sealed partial class MainViewModel
    {
        #region 基础调整（亮度 / 对比度 / 饱和度 / 色温）

        /// <summary>亮度，-100 ~ +100。</summary>
        public double Brightness
        {
            get { return _adjustments.Value.Brightness; }
            set { SetAdjustment(value, null, null, null); }
        }

        /// <summary>对比度，-100 ~ +100。</summary>
        public double Contrast
        {
            get { return _adjustments.Value.Contrast; }
            set { SetAdjustment(null, value, null, null); }
        }

        /// <summary>饱和度，-100 ~ +100。</summary>
        public double Saturation
        {
            get { return _adjustments.Value.Saturation; }
            set { SetAdjustment(null, null, value, null); }
        }

        /// <summary>色温，-100 ~ +100。</summary>
        public double Temperature
        {
            get { return _adjustments.Value.Temperature; }
            set { SetAdjustment(null, null, null, value); }
        }

        /// <summary>当前是否存在非中性调整。</summary>
        public bool HasAdjustments
        {
            get { return !_adjustments.Value.IsNeutral; }
        }

        /// <summary>当前调整参数的文本描述。</summary>
        public string AdjustmentsText
        {
            get { return _adjustments.Value.ToDisplayString(); }
        }

        /// <summary>是否正在渲染（用于状态提示，不阻塞界面）。</summary>
        public bool IsRendering
        {
            get { return _isRendering; }
            private set { SetProperty(ref _isRendering, value); }
        }

        /// <summary>可撤销步数。</summary>
        public int UndoCount
        {
            get { return _history.UndoCount; }
        }

        /// <summary>可重做步数。</summary>
        public int RedoCount
        {
            get { return _history.RedoCount; }
        }

        /// <summary>历史内存占用的可读文本。</summary>
        public string HistoryMemoryText
        {
            get { return _history.MemoryUsageText; }
        }

        /// <summary>历史状态摘要（用于状态栏 / 工具提示）。</summary>
        public string HistoryText
        {
            get
            {
                if (_history.UndoCount == 0 && _history.RedoCount == 0)
                {
                    return string.Format("历史 0/{0} 步 · {1}", _history.MaxSteps, _history.MemoryUsageText);
                }

                return string.Format(
                    "历史 {0}/{1} 步 · {2}{3}",
                    _history.UndoCount,
                    _history.MaxSteps,
                    _history.MemoryUsageText,
                    _history.CanRedo ? " · 可重做 " + _history.RedoCount : string.Empty);
            }
        }

        /// <summary>
        /// 由界面滑块调用设置调整参数（延迟到达时不会重复处理同一个值）。
        /// </summary>
        public void SetAdjustment(double? brightness, double? contrast, double? saturation, double? temperature)
        {
            if (!HasDocument || _adjustments.Value == null)
            {
                return;
            }

            PixelAdjustments current = _adjustments.Value;

            PixelAdjustments updated = new PixelAdjustments(
                brightness ?? current.Brightness,
                contrast ?? current.Contrast,
                saturation ?? current.Saturation,
                temperature ?? current.Temperature);

            if (updated.Equals(current))
            {
                return;
            }

            _adjustments.Set(updated);
        }

        /// <summary>
        /// 打开新文档时重置编辑状态：清空历史、参数复位、缓冲失效。
        /// </summary>
        private void ResetDocumentState()
        {
            InvalidateAdjustmentCache(false);
            _history.Reset(null);
            _committedAdjustments = PixelAdjustments.Neutral;
            _lastCommittedAdjustments = PixelAdjustments.Neutral;

            _suppressAdjustmentRender = true;
            try
            {
                _adjustments.Set(PixelAdjustments.Neutral);
            }
            finally
            {
                _suppressAdjustmentRender = false;
            }
        }

        /// <summary>
        /// 立即提交待处理的预览（不再等待防抖计时器）。
        /// 保存 / 打印之前必须调用，保证拿到的是全分辨率结果，而不是降采样预览。
        /// </summary>
        public async Task FlushPendingPreviewsAsync()
        {
            _previewTimer.Stop();

            if (!HasDocument)
            {
                return;
            }

            _renderRevision++;
            await RenderAdjustmentsAsync(_renderRevision, true).ConfigureAwait(true);
        }

        /// <summary>参数变化：刷新绑定并安排渲染（拖动时降采样，停止后全分辨率）。</summary>
        private void OnAdjustmentsChanged()
        {
            OnPropertyChanged("Brightness");
            OnPropertyChanged("Contrast");
            OnPropertyChanged("Saturation");
            OnPropertyChanged("Temperature");
            OnPropertyChanged("HasAdjustments");
            OnPropertyChanged("AdjustmentsText");

            // 由撤销 / 重做恢复参数时只刷新界面，不重新渲染（位图已经从历史恢复）。
            if (_suppressAdjustmentRender)
            {
                return;
            }

            // 先固化基准（必须在任何预览替换显示位图之前完成），再安排渲染。
            EnsureAdjustmentSession();

            // 重置防抖计时器：拖得越快，预览越连贯；停下 220ms 后再提交全分辨率。
            _previewTimer.Stop();
            _previewTimer.Start();

            RequestRender(false);
        }

        /// <summary>
        /// 请求渲染。commit 为 true 表示这是一次“最终提交”（使用全分辨率缓冲）。
        /// </summary>
        private void RequestRender(bool commit)
        {
            if (!HasDocument)
            {
                return;
            }

            _renderRevision++;
            RunRenderAsync(_renderRevision, commit);
        }

        /// <summary>
        /// 开启一次调整会话：在**用户第一次改动参数时**就把当前已提交画面固化为基准。
        ///
        /// 为什么必须在这一刻固化：随后的降采样预览会临时替换显示位图，
        /// 如果等到提交时再去取“当前位图”，取到的就是预览画面而不是原始画面，
        /// 撤销就会恢复成“已经调整过”的图（这正是之前的缺陷）。
        /// </summary>
        private void EnsureAdjustmentSession()
        {
            if (_baseBuffer != null || _document == null)
            {
                return;
            }

            _baseBuffer = EnsureSourceBuffer(_document);
            _baseState = CreateBaseStateFromRender(_document, _committedAdjustments);
            _baseStateInHistory = false;
        }

        /// <summary>结束当前连续调整会话（下一次调整会建立新的基准与新的历史记录）。</summary>
        private void EndAdjustmentSession()
        {
            _baseBuffer = null;
            _baseState = null;
            _baseStateInHistory = false;
        }

        private async void RunRenderAsync(int revision, bool commit)
        {
            // 说明：这是本类中唯一的 async void —— 由 UI 事件（滑块 / 计时器）触发，
            // 无法返回 Task；内部已用 try/catch 全包，不会让异常逃逸到 UI 线程。
            try
            {
                await RenderAdjustmentsAsync(revision, commit).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                HandleError("应用调整失败。", ex, true);
            }
        }

        private async Task RenderAdjustmentsAsync(int revision, bool commit)
        {
            ImageDocument currentDocument = _document;
            if (currentDocument == null)
            {
                return;
            }

            PixelBuffer source = EnsureSourceBuffer(currentDocument);
            if (source == null)
            {
                return;
            }

            PixelAdjustments adjustments = _adjustments.Value ?? PixelAdjustments.Neutral;

            // 跳过判断必须区分预览与提交：
            //   * 预览：只要画面上已经渲染过这组参数即可跳过；
            //   * 提交：只有当**全分辨率结果**已经是这组参数时才能跳过，
            //     否则会被一次飞行中的预览"骗过"而丢掉这一步历史。
            if (commit)
            {
                if (adjustments.Equals(_lastCommittedAdjustments))
                {
                    return;
                }
            }
            else if (_lastRenderedRevision >= 0 && adjustments.Equals(_lastRenderedAdjustments))
            {
                return;
            }

            try
            {
                IsRendering = true;

                PixelBuffer working = commit ? source : EnsurePreviewBuffer(source);

                // 像素运算在后台线程完成（纯函数，不接触 UI）。
                PixelBuffer adjusted = await _adjustmentsFilter
                    .ApplyAsync(working, adjustments, null, CancellationToken.None)
                    .ConfigureAwait(true);

                if (revision != _renderRevision)
                {
                    // 已有更新的请求在飞行中，丢弃本次结果。
                    return;
                }

                BitmapSource bitmap = PixelBuffer.ToBitmap(adjusted, currentDocument.DpiX, currentDocument.DpiY);

                if (commit)
                {
                    CommitAdjustments(bitmap, adjusted, adjustments);
                }
                else if (revision == _renderRevision)
                {
                    // 预览：只替换显示位图，不写入历史、不改变文档的像素尺寸与 DPI。
                    //
                    // 关键：必须再次确认自己仍是最新请求。降采样的预览比全分辨率提交快得多，
                    // 若用户在预览尚未完成时松手（触发提交），预览的续体会晚于提交落地，
                    // 从而把“已提交的全分辨率画面”覆盖成“过期的预览画面”（Release 下会稳定复现）。
                    SetPreviewBitmap(bitmap);
                }
            }
            finally
            {
                _lastRenderedAdjustments = adjustments;

                if (revision == _renderRevision)
                {
                    _lastRenderedRevision = revision;
                    IsRendering = false;
                }
            }
        }

        /// <summary>
        /// 提交一次调整结果：与基准状态组成一条撤销命令（内存优化：快照全程复用）。
        /// </summary>
        private void CommitAdjustments(
            BitmapSource bitmap,
            PixelBuffer adjustedBuffer,
            PixelAdjustments adjustments)
        {
            ImageDocument currentDocument = _document;
            if (currentDocument == null)
            {
                return;
            }

            // 基准已在会话开始时固化（EnsureAdjustmentSession）；提交阶段绝不能再取当前位图，
            // 因为此前的预览可能已经替换过显示位图。
            PixelBuffer baseBuffer = _baseBuffer;
            EditState baseState = _baseState;

            if (baseBuffer == null || baseState == null)
            {
                // 无法建立基准（例如快照不可用）：退化为直接替换位图，不产生历史记录。
                Document = currentDocument.WithBitmap(bitmap).WithDpi(currentDocument.DpiX, currentDocument.DpiY);
                _committedAdjustments = adjustments;
                LastCommitDiagnostic = string.Format(
                    "无基准：baseBuffer={0} baseState={1} adjustedBuffer={2}",
                    baseBuffer == null ? "null" : "ok",
                    baseState == null ? "null" : "ok",
                    adjustedBuffer == null ? "null" : adjustedBuffer.Width + "x" + adjustedBuffer.Height);
                return;
            }

            EditState newState = EditState.Create(adjustedBuffer, adjustments, currentDocument.DpiX, currentDocument.DpiY);
            if (newState.Snapshot == null)
            {
                // 压缩失败（例如极端尺寸）：仍然显示结果，但不入历史，避免内存失控。
                Document = currentDocument.WithBitmap(bitmap).WithDpi(currentDocument.DpiX, currentDocument.DpiY);
                _committedAdjustments = adjustments;
                _lastError = "无法创建历史快照，本次调整不会进入撤销历史。";
                LastCommitDiagnostic = "快照压缩失败：" + adjustedBuffer.Width + "x" + adjustedBuffer.Height;
                return;
            }

            LastCommitDiagnostic = null;

            Document = currentDocument.WithBitmap(bitmap).WithDpi(currentDocument.DpiX, currentDocument.DpiY);
            _renderedState = newState;

            // 文档位图已经对应这组参数：更新“已提交”语义，供下一次调整会话作为基准。
            _committedAdjustments = adjustments;
            _lastCommittedAdjustments = adjustments;

            if (_baseStateInHistory && _history.CanUndo)
            {
                // 会话合并：把上一条命令的 after 直接推进到新的状态。
                ContinueAdjustmentCommand(newState, adjustments);
            }
            else
            {
                Action<EditState> applier = RestoreState;
                PixelAdjustmentCommand command = new PixelAdjustmentCommand(
                    BuildAdjustmentLabel(baseState.Adjustments, adjustments),
                    baseState,
                    newState,
                    applier);

                _history.Push(command, newState);
                _baseStateInHistory = true;
            }

            StatusMessage = string.Format(
                "已应用 {0}（{1}）",
                adjustments.ToDisplayString(),
                _history.MemoryUsageText);
        }

        /// <summary>
        /// 连续调整合并：更新上一条命令的 after 指向，避免每次拖动都产生一条历史。
        /// </summary>
        private void ContinueAdjustmentCommand(EditState newState, PixelAdjustments adjustments)
        {
            PixelAdjustmentCommand previous = _history.LastCommand as PixelAdjustmentCommand;
            if (previous == null)
            {
                _baseStateInHistory = false;
                return;
            }

            Action<EditState> applier = RestoreState;
            PixelAdjustmentCommand merged = new PixelAdjustmentCommand(
                BuildAdjustmentLabel(previous.Before.Adjustments, adjustments),
                previous.Before,
                newState,
                applier);

            _history.ReplaceLastCommand(merged, newState);
        }

        /// <summary>构造“上一次已渲染位图”对应的基准状态。</summary>
        private EditState CreateBaseStateFromRender(ImageDocument document, PixelAdjustments previousRendered)
        {
            PixelBuffer pixels = PixelBuffer.FromBitmap(document.Bitmap);
            return EditState.Create(pixels, previousRendered, document.DpiX, document.DpiY);
        }

        /// <summary>
        /// 提交一次「一次性位图变换」并写入历史（反色 / 模糊 / 裁剪 / 旋转 / 边框 / 文字等）。
        ///
        /// 与调整命令不同：这类操作无法用参数回放，因此直接保存“变换前”和“变换后”两个状态。
        /// 变换前状态由调用方传入（必须是本次变换的输入，通常是变换开始前的当前画面）。
        /// </summary>
        /// <param name="label">历史记录中显示的名称。</param>
        /// <param name="before">变换前的状态。</param>
        /// <param name="transformed">变换后的像素缓冲。</param>
        private bool CommitBufferEdit(string label, EditState before, PixelBuffer transformed)
        {
            ImageDocument currentDocument = _document;
            if (currentDocument == null || before == null || transformed == null)
            {
                return false;
            }

            EditState after = EditState.Create(
                transformed,
                _committedAdjustments,
                currentDocument.DpiX,
                currentDocument.DpiY);

            if (after.Snapshot == null)
            {
                _lastError = "无法创建历史快照，本次编辑不会进入撤销历史。";
                return false;
            }

            BitmapSource bitmap = PixelBuffer.ToBitmap(transformed, currentDocument.DpiX, currentDocument.DpiY);

            Document = currentDocument.WithBitmap(bitmap).WithDpi(currentDocument.DpiX, currentDocument.DpiY);
            _renderedState = after;

            // 一次性编辑会打断当前的调整会话：下一次滑块调整从新画面重新建立基准。
            EndAdjustmentSession();

            _history.Push(new BufferEditCommand(label, before, after, RestoreState), after);

            StatusMessage = string.Format(
                "已应用「{0}」（{1}，{2}）",
                label,
                Document.PixelSizeText,
                _history.MemoryUsageText);

            return true;
        }

        /// <summary>
        /// 建立“当前画面”的状态快照，作为一次性编辑的撤销目标。
        /// 内部使用同步像素读取，调用点应已在后台线程或已接受其开销。
        /// </summary>
        private EditState CreateCurrentStateSnapshot()
        {
            ImageDocument currentDocument = _document;
            if (currentDocument == null)
            {
                return null;
            }

            PixelBuffer pixels = PixelBuffer.FromBitmap(currentDocument.Bitmap);
            return EditState.Create(pixels, _committedAdjustments, currentDocument.DpiX, currentDocument.DpiY);
        }

        /// <summary>
        /// 一次性滤镜的公共流程：提交待处理预览 → 记录撤销目标 → 后台计算 → 提交结果。
        /// </summary>
        /// <param name="label">操作名称。</param>
        /// <param name="transform">像素变换（纯函数，在后台线程执行）。</param>
        private async Task ApplyOneShotAsync(string label, Func<PixelBuffer, Task<PixelBuffer>> transform)
        {
            if (!HasDocument || IsBusy)
            {
                return;
            }

            if (transform == null)
            {
                throw new ArgumentNullException("transform");
            }

            IsBusy = true;

            try
            {
                // 先把滑块预览提交为全分辨率，保证编辑基于确定的画面。
                await FlushPendingPreviewsAsync().ConfigureAwait(true);

                if (_document == null)
                {
                    return;
                }

                EditState before = CreateCurrentStateSnapshot();
                PixelBuffer source = PixelBuffer.FromBitmap(_document.Bitmap);
                StatusMessage = "正在处理：" + label;

                PixelBuffer transformed = await transform(source).ConfigureAwait(true);

                if (transformed == null)
                {
                    return;
                }

                CommitBufferEdit(label, before, transformed);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "已取消：" + label;
            }
            catch (Exception ex)
            {
                HandleError("执行「" + label + "」失败。", ex, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>撤销后恢复状态：替换位图、同步滑块参数、刷新缓冲。</summary>
        private void RestoreState(EditState state)
        {
            if (state == null)
            {
                return;
            }

            ImageDocument currentDocument = _document;
            if (currentDocument == null)
            {
                return;
            }

            BitmapSource bitmap = state.ToBitmap();
            if (bitmap == null)
            {
                throw new InvalidOperationException("历史快照已失效，无法恢复该步骤。");
            }

            _suppressAdjustmentRender = true;
            try
            {
                _adjustments.Set(state.Adjustments);
            }
            finally
            {
                _suppressAdjustmentRender = false;
            }

            _sourceBuffer = null;
            _previewBuffer = null;
            _baseBuffer = null;
            _baseState = null;
            _baseStateInHistory = false;
            _renderedState = state;
            _committedAdjustments = state.Adjustments;
            _lastCommittedAdjustments = state.Adjustments;

            Document = currentDocument.WithBitmap(bitmap).WithDpi(state.DpiX, state.DpiY);

            // 撤销 / 重做后清空“已渲染参数”记录，保证下一次调整一定会真正渲染。
            _lastRenderedRevision = -1;
            _lastRenderedAdjustments = null;
        }

        /// <summary>撤销。</summary>
        public void Undo()
        {
            if (!_history.CanUndo)
            {
                return;
            }

            // 先把当前会话收尾，再撤销，否则会丢掉最后一段调整。
            _previewTimer.Stop();
            EndAdjustmentSession();
            _history.Undo();
            StatusMessage = "已撤销";
        }

        /// <summary>重做。</summary>
        public void Redo()
        {
            if (!_history.CanRedo)
            {
                return;
            }

            _previewTimer.Stop();
            EndAdjustmentSession();
            _history.Redo();
            StatusMessage = "已重做";
        }

        private void OnHistoryChanged()
        {
            OnPropertyChanged("UndoCount");
            OnPropertyChanged("RedoCount");
            OnPropertyChanged("HistoryMemoryText");
            OnPropertyChanged("HistoryText");
            RelayCommand.RaiseCanExecuteChanged();
        }

        private static string BuildAdjustmentLabel(PixelAdjustments from, PixelAdjustments to)
        {
            // 只列出真正变化的项，读起来更清楚。
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();

            if (Math.Abs(to.Brightness - from.Brightness) > 0.001)
            {
                parts.Add("亮度 " + to.Brightness.ToString("+0;-0;0"));
            }

            if (Math.Abs(to.Contrast - from.Contrast) > 0.001)
            {
                parts.Add("对比度 " + to.Contrast.ToString("+0;-0;0"));
            }

            if (Math.Abs(to.Saturation - from.Saturation) > 0.001)
            {
                parts.Add("饱和度 " + to.Saturation.ToString("+0;-0;0"));
            }

            if (Math.Abs(to.Temperature - from.Temperature) > 0.001)
            {
                parts.Add("色温 " + to.Temperature.ToString("+0;-0;0"));
            }

            return parts.Count == 0 ? "基础调整" : string.Join(" / ", parts.ToArray());
        }

        #endregion

        #region 像素缓冲缓存与预览

        /// <summary>
        /// 让调整相关的缓存失效。keepBuffers 为 true 时保留源 / 基准缓冲
        /// （调整提交后自己替换了 Document 位图，此时缓存仍然有效，必须保留，
        ///  否则会退化成“在已调整结果上再次调整”，造成画质累积损失）。
        /// </summary>
        private void InvalidateAdjustmentCache(bool keepBuffers)
        {
            _lastRenderedRevision = -1;
            _lastRenderedAdjustments = null;
            _lastPreviewBitmap = null;

            if (keepBuffers)
            {
                return;
            }

            _sourceBuffer = null;
            _previewBuffer = null;
            _baseBuffer = null;
            _baseState = null;
            _baseStateInHistory = false;
            _renderedState = null;
        }

        /// <summary>确保源像素缓冲可用（惰性从当前文档位图解码）。</summary>
        private PixelBuffer EnsureSourceBuffer(ImageDocument document)
        {
            if (document == null)
            {
                return null;
            }

            if (_sourceBuffer == null || _sourceBuffer.Width != document.PixelWidth || _sourceBuffer.Height != document.PixelHeight)
            {
                _sourceBuffer = PixelBuffer.FromBitmap(document.Bitmap);
                _previewBuffer = null;
            }

            return _sourceBuffer;
        }

        /// <summary>惰性创建降采样缓冲（拖动滑块时用它做实时预览）。</summary>
        private PixelBuffer EnsurePreviewBuffer(PixelBuffer source)
        {
            if (source == null)
            {
                return null;
            }

            if (_previewBuffer == null)
            {
                _previewBuffer = source.GetPreview(PreviewMaxSize, PreviewMaxSize);
            }

            return _previewBuffer;
        }

        /// <summary>设置预览位图（不改变文档的像素尺寸与 DPI，也不写历史）。</summary>
        private void SetPreviewBitmap(BitmapSource bitmap)
        {
            if (bitmap == null)
            {
                return;
            }

            ImageDocument currentDocument = _document;
            if (currentDocument == null)
            {
                return;
            }

            _lastPreviewBitmap = bitmap;

            // 预览位图的分辨率与文档不同，但界面按 PixelWidth × ZoomFactor 布局，
            // 因此这里同步一个“仅用于显示”的文档快照（IsDirty 保持原值，不代表已提交）。
            ImageDocument previewDocument = new ImageDocument(
                bitmap,
                currentDocument.FilePath,
                currentDocument.Format,
                currentDocument.DpiX,
                currentDocument.DpiY,
                currentDocument.FileSizeBytes,
                currentDocument.IsDirty);

            _isPreviewing = true;
            try
            {
                Document = previewDocument;
            }
            finally
            {
                _isPreviewing = false;
            }
        }

        #endregion
    }
}
