using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using PSText.Infrastructure;
using PSText.Infrastructure.History;
using PSText.Infrastructure.Imaging;
using PSText.Models;

namespace PSText.ViewModels
{
    /// <summary>
    /// 一个打开文档的**全部工作台状态**。
    ///
    /// 为什么需要它：多文档标签页要求每个标签各自持有"还没保存的编辑"——
    /// 撤销历史、标注对象、调整参数、缓冲区缓存、缩放与视图模式，全都得按文档隔离；
    /// 否则切到另一个标签再切回来，撤销历史就串了，"撤销"会撤回另一个文档的操作。
    ///
    /// 它与 <see cref="ImageDocument"/> 的分工：
    ///   * <see cref="ImageDocument"/> 是**不可变的像素快照**，只回答"画面现在长什么样"；
    ///   * <see cref="DocumentSession"/> 是**可变的工作台**，装着"走到这一步改过什么、
    ///     还能不能撤销、选中了哪个标注、视图缩放到多少"。
    /// 把两件事分开之后，"切换标签"就只是换一个工作台，像素快照本身完全不用动。
    ///
    /// 注意：这里的所有字段都是**可变**的（与 ImageDocument 相反），
    /// 因为工作台本来就是随操作不断变化的东西。
    /// </summary>
    public sealed class DocumentSession : ObservableObject
    {
        /// <summary>撤销步数上限（与单文档时期保持一致）。</summary>
        public const int MaxHistorySteps = 30;

        /// <summary>历史内存上限（字节）。</summary>
        public const long MaxHistoryBytes = 256L * 1024 * 1024;

        private ImageDocument _document;
        private bool _isActiveTab;

        public DocumentSession()
        {
            History = new HistoryManager(MaxHistorySteps, MaxHistoryBytes);
            Annotations = new List<AnnotationObject>();
            AnnotationItems = new ObservableCollection<AnnotationItemViewModel>();
            Adjustments = new AdjustmentsHolder();

            LastCommittedAdjustments = PixelAdjustments.Neutral;
            CommittedAdjustments = PixelAdjustments.Neutral;
            LastRenderedRevision = -1;
            ZoomFactor = 1.0;
            ZoomMode = ZoomMode.FitToWindow;
            SelectedAnnotationIndex = -1;
        }

        #region 文档与编辑状态

        /// <summary>当前画面（不可变快照）。空会话时为 null。</summary>
        public ImageDocument Document
        {
            get { return _document; }
            set
            {
                if (SetProperty(ref _document, value, "Document"))
                {
                    OnPropertyChanged("HasDocument");
                    OnPropertyChanged("DisplayName");
                    OnPropertyChanged("IsDirty");
                    OnPropertyChanged("TabTitle");
                }
            }
        }

        /// <summary>是否已打开图片。</summary>
        public bool HasDocument
        {
            get { return _document != null; }
        }

        /// <summary>未保存的修改。</summary>
        public bool IsDirty
        {
            get { return _document != null && _document.IsDirty; }
        }

        /// <summary>撤销 / 重做栈。</summary>
        public HistoryManager History { get; private set; }

        /// <summary>标注对象列表（非破坏性）。</summary>
        public List<AnnotationObject> Annotations { get; private set; }

        /// <summary>叠加层绑定用的标注视图模型集合。</summary>
        public ObservableCollection<AnnotationItemViewModel> AnnotationItems { get; private set; }

        /// <summary>选中的标注下标（-1 表示未选中）。</summary>
        public int SelectedAnnotationIndex { get; set; }

        /// <summary>
        /// 调整参数的持有者（负责变更通知）。
        ///
        /// 标 internal 是因为 <see cref="AdjustmentsHolder"/> 本身是内部实现细节，
        /// 没必要为了一个转发属性把它提升成公开 API。
        /// </summary>
        internal AdjustmentsHolder Adjustments { get; private set; }

        #endregion

        #region 调整会话与缓冲区缓存

        /// <summary>进入调整前解码的源像素缓冲（全分辨率）。</summary>
        public PixelBuffer SourceBuffer { get; set; }

        /// <summary>惰性创建的降采样缓冲（拖动滑块时的预览用）。</summary>
        public PixelBuffer PreviewBuffer { get; set; }

        /// <summary>当前基准缓冲：非空表示正在一次连续调整会话中。</summary>
        public PixelBuffer BaseBuffer { get; set; }

        /// <summary>基准状态（连续调整会话的撤销目标）。</summary>
        public EditState BaseState { get; set; }

        /// <summary>与当前 Document 位图对应的状态（撤销 / 重做的状态链指针）。</summary>
        public EditState RenderedState { get; set; }

        /// <summary>只有"基准状态"已被历史记录拥有时，才允许把会话合并进上一条命令。</summary>
        public bool BaseStateInHistory { get; set; }

        /// <summary>渲染序号，用于丢弃过期的异步渲染结果。</summary>
        public int RenderRevision { get; set; }

        /// <summary>最近一次已渲染完成的调整参数修订号。</summary>
        public int LastRenderedRevision { get; set; }

        /// <summary>最近一次已渲染的调整参数（用于跳过重复的预览渲染）。</summary>
        public PixelAdjustments LastRenderedAdjustments { get; set; }

        /// <summary>
        /// 最近一次**以全分辨率提交**的调整参数。
        ///
        /// 必须与 LastRenderedAdjustments 分开：后者可能只是一次降采样预览的结果。
        /// 如果提交渲染也拿预览的进度去判断"无需重算"，就会出现
        /// "预览画出来了、但提交被跳过"，于是历史里没有这一步（曾偶发此缺陷）。
        /// </summary>
        public PixelAdjustments LastCommittedAdjustments { get; set; }

        /// <summary>
        /// 当前 Document 位图所对应的调整参数（"已提交"语义）。
        ///
        /// 必须与 LastRenderedAdjustments 区分开：后者会被降采样预览更新，
        /// 而本字段只在真正提交、撤销、重做、加载时更新。
        /// 若用预览值当作调整会话的基准，撤销就会恢复成"已经调整过"的画面（曾踩此坑）。
        /// </summary>
        public PixelAdjustments CommittedAdjustments { get; set; }

        #endregion

        #region 视图状态

        /// <summary>缩放比例（1.0 = 100%）。</summary>
        public double ZoomFactor { get; set; }

        /// <summary>缩放模式（适应窗口 / 原始大小 / 自由）。</summary>
        public ZoomMode ZoomMode { get; set; }

        #endregion

        #region 标签页显示

        /// <summary>
        /// 是否是当前选中的标签（界面据此高亮）。
        ///
        /// 由 <c>MainViewModel.ActivateSession</c> 维护，而不是让界面去比较
        /// "这一项 == ActiveSession"：那样需要转换器，而且 ActiveSession 变化时
        /// 每一项都要重新求值，反而更容易漏刷新。
        /// </summary>
        public bool IsActiveTab
        {
            get { return _isActiveTab; }
            set { SetProperty(ref _isActiveTab, value, "IsActiveTab"); }
        }

        /// <summary>标签上显示的名字。</summary>
        public string DisplayName
        {
            get { return _document == null ? "（空）" : _document.FileName; }
        }

        /// <summary>标签标题（带未保存标记）。</summary>
        public string TabTitle
        {
            get { return IsDirty ? DisplayName + " *" : DisplayName; }
        }

        /// <summary>标签的工具提示。</summary>
        public string TabToolTip
        {
            get
            {
                if (_document == null)
                {
                    return "空标签页（打开图片后就有了）";
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\n{1} · {2}{3}",
                    _document.FilePath ?? "（尚未保存到文件）",
                    _document.PixelSizeText,
                    _document.DpiText,
                    IsDirty ? "\n有未保存的修改" : string.Empty);
            }
        }

        /// <summary>文档发生变化后刷新标签显示。</summary>
        public void RefreshTabCaption()
        {
            OnPropertyChanged("DisplayName");
            OnPropertyChanged("IsDirty");
            OnPropertyChanged("TabTitle");
            OnPropertyChanged("TabToolTip");
        }

        #endregion

        /// <summary>
        /// 清空为"空会话"（关闭最后一个标签时回到这个状态）。
        ///
        /// 保留同一个对象实例，是为了让 <c>MainViewModel.ActiveSession</c> 永远不为 null ——
        /// 所有转发属性因此都不需要判空，这比到处写 null 检查可靠得多。
        /// </summary>
        public void Reset()
        {
            Document = null;
            Annotations.Clear();
            AnnotationItems.Clear();
            History.Reset(null);
            Adjustments.Set(PixelAdjustments.Neutral);

            SourceBuffer = null;
            PreviewBuffer = null;
            BaseBuffer = null;
            BaseState = null;
            RenderedState = null;
            BaseStateInHistory = false;
            RenderRevision = 0;
            LastRenderedRevision = -1;
            LastRenderedAdjustments = null;
            LastCommittedAdjustments = PixelAdjustments.Neutral;
            CommittedAdjustments = PixelAdjustments.Neutral;

            SelectedAnnotationIndex = -1;
            ZoomFactor = 1.0;
            ZoomMode = ZoomMode.FitToWindow;

            RefreshTabCaption();
        }
    }
}
