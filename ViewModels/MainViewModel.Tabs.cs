using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using PSText.Services;
using PSText.Infrastructure;
using PSText.Models;

namespace PSText.ViewModels
{
    /// <summary>
    /// MainViewModel 的「多文档标签页」部分。
    ///
    /// 实现思路（也是这次改造能做得比较稳的原因）：
    ///   **把按文档隔离的状态收进 <see cref="DocumentSession"/>，再让 MainViewModel 里
    ///   那批原本的私有字段变成"转发到当前会话"的私有属性。**
    ///   于是几百处调用点一行都不用改，类里所有代码看起来仍然在操作"当前文档"，
    ///   而"切换标签 = 换一整套状态"自动成立。
    ///
    /// 两条不变量：
    ///   1. **至少有一个标签**：关掉最后一个标签时把它重置为"空会话"而不是移除，
    ///      因此 <see cref="ActiveSession"/> 永不为 null，转发属性一律不用判空；
    ///   2. **切换标签必须重建界面状态**：撤销步数、调整滑块、标注列表、缩放比例
    ///      全都属于"某个文档"，切过去之后要整体刷新（见 <see cref="RefreshAfterSessionChange"/>）。
    /// </summary>
    public sealed partial class MainViewModel
    {
        #region 状态

        /// <summary>打开的标签页（至少一个，可能是空会话）。</summary>
        public ObservableCollection<DocumentSession> Sessions
        {
            get { return _sessions; }
        }

        /// <summary>当前标签页。永远不为 null。</summary>
        public DocumentSession ActiveSession
        {
            get { return _activeSession; }
        }

        /// <summary>是否开着多个标签（单个时可以把标签栏收起来）。</summary>
        public bool HasMultipleSessions
        {
            get { return _sessions.Count > 1; }
        }

        /// <summary>是否存在任何一个标签有未保存的修改。</summary>
        public bool HasAnyUnsavedChanges
        {
            get
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].Document != null && _sessions[i].Document.IsDirty)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        #endregion

        #region 命令

        /// <summary>在新标签页里打开图片。</summary>
        public ICommand OpenInNewTabCommand { get; private set; }

        /// <summary>切换到指定标签页。</summary>
        public ICommand ActivateTabCommand { get; private set; }

        /// <summary>关闭指定标签页。</summary>
        public ICommand CloseTabCommand { get; private set; }

        /// <summary>关闭当前标签页。</summary>
        public ICommand CloseCurrentTabCommand { get; private set; }

        /// <summary>把当前标签往左移一格（Ctrl+Shift+PageUp）。</summary>
        public ICommand MoveTabLeftCommand { get; private set; }

        /// <summary>把当前标签往右移一格（Ctrl+Shift+PageDown）。</summary>
        public ICommand MoveTabRightCommand { get; private set; }

        private void InitializeTabCommands()
        {
            OpenInNewTabCommand = CreateAsyncCommand(OpenImageAsync, () => !IsBusy);

            ActivateTabCommand = new RelayCommand<DocumentSession>(
                session => ActivateSession(session),
                session => session != null && !ReferenceEquals(session, _activeSession));

            CloseTabCommand = new RelayCommand<DocumentSession>(
                session => RunCloseTab(session),
                session => session != null && !IsBusy);

            CloseCurrentTabCommand = new RelayCommand(
                () => RunCloseTab(_activeSession),
                () => !IsBusy);

            MoveTabLeftCommand = new RelayCommand(
                () => MoveActiveTab(-1),
                () => CanMoveActiveTab(-1));

            MoveTabRightCommand = new RelayCommand(
                () => MoveActiveTab(1),
                () => CanMoveActiveTab(1));
        }

        #endregion

        #region 会话生命周期

        /// <summary>建立一个会话，并把它的通知接到界面上。</summary>
        /// <summary>
        /// 把第 <paramref name="fromIndex"/> 条会话移动到最终的第 <paramref name="toIndex"/> 位。
        ///
        /// 刻意**不改 ActiveSession** —— 拖动排序只调整顺序，不该顺手把当前文档切走。
        /// （标签页那边的既有纪律是"切换标签必须整体刷新"，而这里根本没有切换，
        ///   所以除了集合本身的变更通知之外不需要刷新任何东西。）
        /// </summary>
        public void MoveSession(int fromIndex, int toIndex)
        {
            int count = Sessions.Count;

            if (fromIndex < 0 || fromIndex >= count)
            {
                return;
            }

            if (toIndex < 0)
            {
                toIndex = 0;
            }
            else if (toIndex > count - 1)
            {
                toIndex = count - 1;
            }

            if (fromIndex == toIndex)
            {
                return;
            }

            Sessions.Move(fromIndex, toIndex);
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>把当前标签左右挪一格。越界时不动（按钮也会相应置灰）。</summary>
        private void MoveActiveTab(int delta)
        {
            int index = Sessions.IndexOf(ActiveSession);
            int target = index + delta;

            if (index < 0 || target < 0 || target >= Sessions.Count)
            {
                return;
            }

            MoveSession(index, target);
            StatusMessage = Loc.T("Tab_Moved", ActiveSession.TabTitle);
        }

        private bool CanMoveActiveTab(int delta)
        {
            int index = Sessions.IndexOf(ActiveSession);
            int target = index + delta;

            return index >= 0 && target >= 0 && target < Sessions.Count;
        }

        private DocumentSession CreateSession(ImageDocument document)
        {
            DocumentSession session = new DocumentSession();
            session.Document = document;

            // 只有**当前标签**的变化才该驱动界面。
            // 后台标签的历史 / 参数变化只更新它自己的标签标题 —— 否则切回来时
            // 界面显示的是别人的撤销步数与滑块位置（这正是按文档隔离要解决的问题本身）。
            session.Adjustments.Changed += (sender, args) =>
            {
                session.RefreshTabCaption();

                if (ReferenceEquals(session, _activeSession))
                {
                    OnAdjustmentsChanged();
                }
            };

            session.History.Changed += (sender, args) =>
            {
                session.RefreshTabCaption();

                if (ReferenceEquals(session, _activeSession))
                {
                    OnHistoryChanged();
                }
            };

            return session;
        }

        /// <summary>切换当前标签页。</summary>
        private void ActivateSession(DocumentSession session)
        {
            if (session == null || !_sessions.Contains(session) || ReferenceEquals(session, _activeSession))
            {
                return;
            }

            // 防抖计时器是全局的，切走时必须停掉：
            // 否则上一次拖动滑块的"提交"会落到新标签上，把它的历史改坏。
            _previewTimer.Stop();

            // 裁剪 / 修补这些模式的状态绑在具体文档上，切标签时一律退出，
            // 免得在新文档上看到一个属于旧文档的裁剪框。
            CancelAnnotationGesture();

            if (IsCropping)
            {
                CancelCrop();
            }

            if (IsRetouchMode)
            {
                ExitRetouch();
            }

            if (_activeSession != null)
            {
                _activeSession.IsActiveTab = false;
            }

            _activeSession = session;
            _activeSession.IsActiveTab = true;

            OnPropertyChanged("ActiveSession");
            OnPropertyChanged("HasMultipleSessions");
            RefreshAfterSessionChange();
        }

        /// <summary>
        /// 切换标签后整体刷新界面状态。
        ///
        /// 这里**故意写得"啰嗦"**：凡是与具体文档有关的绑定都要显式通知一遍。
        /// 少通知一个的后果是"切过去之后某一处还显示上一个文档的值"，
        /// 而这种错位很难靠肉眼定位到是哪个属性漏了。
        /// </summary>
        private void RefreshAfterSessionChange()
        {
            // 文档与视图
            OnPropertyChanged("Document");
            OnPropertyChanged("CurrentBitmap");
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
            OnPropertyChanged("WindowTitle");
            OnPropertyChanged("StatusText");
            OnPropertyChanged("HasAnyUnsavedChanges");

            OnPropertyChanged("ZoomFactor");
            OnPropertyChanged("ZoomPercentText");
            OnPropertyChanged("ZoomMode");
            OnPropertyChanged("IsFitToWindow");
            OnPropertyChanged("IsActualSize");

            // 撤销 / 重做
            OnHistoryChanged();

            // 调整参数（抑制渲染：切标签不该触发一次重新渲染）
            _suppressAdjustmentRender = true;
            try
            {
                OnAdjustmentsChanged();
            }
            finally
            {
                _suppressAdjustmentRender = false;
            }

            // 标注
            OnPropertyChanged("Annotations");
            OnPropertyChanged("AnnotationCount");
            OnPropertyChanged("SelectedAnnotationIndex");
            OnPropertyChanged("HasSelectedAnnotation");
            OnPropertyChanged("SelectedAnnotationInfoText");
            RebuildAnnotationHandles();

            // 筛选器 / 打印等命令的可执行状态
            RelayCommand.RaiseCanExecuteChanged();
        }

        #endregion

        #region 打开与关闭

        /// <summary>关闭当前标签页（先确认未保存的修改）。</summary>
        private async void RunCloseTab(DocumentSession session)
        {
            // 由 UI 命令触发；内部全包 try/catch，异常不会逃逸到 UI 线程。
            try
            {
                await CloseSessionAsync(session).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Tabs_CloseFailed"), ex, true);
            }
        }

        /// <summary>关闭指定标签页；返回是否真的关掉了。</summary>
        public async Task<bool> CloseSessionAsync(DocumentSession session)
        {
            if (session == null || !_sessions.Contains(session))
            {
                return false;
            }

            // 先把要关的那个标签切到前台再确认 —— 这样用户点"保存"时
            // 保存的一定是他正在看的那张图，而不是别的标签。
            if (!ReferenceEquals(session, _activeSession))
            {
                ActivateSession(session);
            }

            if (session.Document != null && session.Document.IsDirty)
            {
                if (!await ConfirmActiveDocumentAsync().ConfigureAwait(true))
                {
                    return false;
                }
            }

            _sessions.Remove(session);

            if (_sessions.Count == 0)
            {
                // 维持"至少一个标签"的不变量：把这个对象洗净后留用，
                // 而不是从集合里移除（那样 ActiveSession 就会变成 null）。
                session.Reset();
                _sessions.Add(session);
                _activeSession = null;
                ActivateSession(session);
            }
            else if (ReferenceEquals(_activeSession, session))
            {
                ActivateSession(_sessions[0]);
            }
            else
            {
                OnPropertyChanged("HasMultipleSessions");
            }

            return true;
        }

        #endregion
    }
}
