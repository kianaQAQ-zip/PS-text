using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PSText.Infrastructure;
using PSText.Services;
using PSText.Services.Interfaces;

namespace PSText.ViewModels
{
    /// <summary>最近文件列表项。</summary>
    public sealed class RecentFileItem : ObservableObject
    {
        private readonly Func<string, Task> _openCallback;
        private bool _isMissing;

        public RecentFileItem(string filePath, Func<string, Task> openCallback = null)
        {
            FilePath = filePath;
            _openCallback = openCallback;

            // 记录时先探一次是否存在；不存在时在菜单里给出提示
            _isMissing = !SafeExists(filePath);

            OpenCommand = new RelayCommand(Open, () => !_isMissing);
        }

        /// <summary>完整路径。</summary>
        public string FilePath { get; private set; }

        /// <summary>显示名（文件名）。</summary>
        public string FileName
        {
            get
            {
                try
                {
                    string name = Path.GetFileName(FilePath);
                    return string.IsNullOrEmpty(name) ? FilePath : name;
                }
                catch (ArgumentException)
                {
                    return FilePath;
                }
            }
        }

        /// <summary>菜单项显示文本（文件缺失时加标记）。</summary>
        public string DisplayName
        {
            get { return _isMissing ? FileName + Loc.T("Settings_RecentGone") : FileName; }
        }

        /// <summary>所在目录（提示用）。</summary>
        public string Directory
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(FilePath) ?? string.Empty;
                }
                catch (ArgumentException)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>文件是否已不存在。</summary>
        public bool IsMissing
        {
            get { return _isMissing; }
        }

        /// <summary>打开该项（供菜单直接绑定，避免依赖祖先 DataContext）。</summary>
        public ICommand OpenCommand { get; private set; }

        private async void Open()
        {
            // 打开前再确认一次（列表生成后文件可能被删除）
            _isMissing = !SafeExists(FilePath);

            if (_isMissing)
            {
                OnPropertyChanged("IsMissing");
                OnPropertyChanged("DisplayName");
                RelayCommand.RaiseCanExecuteChanged();
                return;
            }

            if (_openCallback != null)
            {
                await _openCallback(FilePath).ConfigureAwait(true);
            }
        }

        /// <summary>让界面刷新存在性状态。</summary>
        public void RefreshExistence()
        {
            bool missing = !SafeExists(FilePath);

            if (missing == _isMissing)
            {
                return;
            }

            _isMissing = missing;
            OnPropertyChanged("IsMissing");
            OnPropertyChanged("DisplayName");
            RelayCommand.RaiseCanExecuteChanged();
        }

        private static bool SafeExists(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public override string ToString()
        {
            return FileName;
        }
    }

    /// <summary>
    /// MainViewModel 的「设置 / 主题 / 最近文件 / 进度」部分（partial，需求 P3-13、P3-15、P3-16）。
    /// </summary>
    public sealed partial class MainViewModel
    {
        private readonly ObservableCollection<RecentFileItem> _recentFiles =
            new ObservableCollection<RecentFileItem>();

        private ISettingsService _settingsService;
        private AppSettings _settings = new AppSettings();
        private ThemePreference _themePreference = ThemePreference.FollowSystem;
        private bool _isProgressVisible;
        private double _progressValue;
        private string _progressMessage;

        #region 服务

        /// <summary>
        /// 设置服务。由组合根（App）注入；未注入时使用内存默认值（自检环境友好）。
        /// </summary>
        public ISettingsService SettingsService
        {
            get { return _settingsService; }
            set
            {
                _settingsService = value;
                LoadSettings();
            }
        }

        #endregion

        #region 命令

        /// <summary>在浅色 / 深色之间快速切换主题。</summary>
        public ICommand ToggleThemeCommand { get; private set; }

        /// <summary>把主题设为“跟随系统”。</summary>
        public ICommand FollowSystemThemeCommand { get; private set; }

        /// <summary>按索引设置主题偏好（0 跟随系统 / 1 浅色 / 2 深色），供菜单 CommandParameter 使用。</summary>
        public ICommand SetThemeCommand { get; private set; }

        /// <summary>清空最近文件列表。</summary>
        public ICommand ClearRecentFilesCommand { get; private set; }

        /// <summary>装配设置相关命令（在构造函数中调用一次）。</summary>
        private void InitializeSettingsCommands()
        {
            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            FollowSystemThemeCommand = new RelayCommand(
                () => SetThemePreference(ThemePreference.FollowSystem));

            SetThemeCommand = new RelayCommand<int>(index =>
            {
                int clamped = index < 0 ? 0 : (index > 2 ? 2 : index);
                SetThemePreference((ThemePreference)clamped);
            });

            ClearRecentFilesCommand = new RelayCommand(
                ClearRecentFiles,
                () => _recentFiles.Count > 0);
        }

        #endregion

        #region 最近文件

        /// <summary>最近打开的文件列表（最多 10 个，最新在前）。</summary>
        public ObservableCollection<RecentFileItem> RecentFiles
        {
            get { return _recentFiles; }
        }

        /// <summary>最近文件是否为空。</summary>
        public bool HasRecentFiles
        {
            get { return _recentFiles.Count > 0; }
        }

        /// <summary>把路径记入最近文件（去重 + 置顶 + 限长 + 立即持久化）。</summary>
        public void AddRecentFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            _settings.AddRecentFile(filePath);
            RefreshRecentFiles();
            SaveSettings();
        }

        /// <summary>从最近文件列表中移除一项。</summary>
        public void RemoveRecentFile(string filePath)
        {
            _settings.RemoveRecentFile(filePath);
            RefreshRecentFiles();
            SaveSettings();
        }

        /// <summary>清空最近文件列表。</summary>
        public void ClearRecentFiles()
        {
            _settings.RecentFiles = new List<string>();
            RefreshRecentFiles();
            SaveSettings();
        }

        /// <summary>
        /// 打开一个最近文件。文件已被删除时提示并自动移出列表。
        /// </summary>
        public async Task OpenRecentFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (!File.Exists(filePath))
            {
                ConfirmResult result = _dialogService.Confirm(
                    Loc.T("Settings_RecentMissing") + filePath + Loc.T("Settings_RecentRemove"),
                    Loc.T("Settings_RecentOpen"));

                if (result != ConfirmResult.Cancel)
                {
                    RemoveRecentFile(filePath);
                }

                return;
            }

            await LoadFromPathAsync(filePath).ConfigureAwait(true);
        }

        private void RefreshRecentFiles()
        {
            _recentFiles.Clear();

            List<string> paths = _settings.RecentFiles;

            if (paths == null)
            {
                OnPropertyChanged("HasRecentFiles");
                return;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(paths[i]))
                {
                    _recentFiles.Add(new RecentFileItem(paths[i], OpenRecentFileAsync));
                }
            }

            OnPropertyChanged("HasRecentFiles");
        }

        /// <summary>重新探测所有最近文件的存在性（界面刷新用）。</summary>
        public void RefreshRecentFileExistence()
        {
            for (int i = 0; i < _recentFiles.Count; i++)
            {
                _recentFiles[i].RefreshExistence();
            }
        }

        #endregion

        #region 主题

        /// <summary>当前主题偏好。</summary>
        public ThemePreference ThemePreference
        {
            get { return _themePreference; }
            private set
            {
                if (SetProperty(ref _themePreference, value, "ThemePreference"))
                {
                    OnPropertyChanged("ThemePreferenceIndex");
                    OnPropertyChanged("ThemeDisplayText");
                    OnPropertyChanged("IsLightTheme");
                    OnPropertyChanged("IsDarkTheme");
                    OnPropertyChanged("IsSystemTheme");
                }
            }
        }

        /// <summary>供下拉框绑定的偏好索引（0 跟随系统 / 1 浅色 / 2 深色）。</summary>
        public int ThemePreferenceIndex
        {
            get { return (int)_themePreference; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 2 ? 2 : value);
                SetThemePreference((ThemePreference)clamped);
            }
        }

        /// <summary>主题显示文本（含“跟随系统”时的实际结果）。</summary>
        public string ThemeDisplayText
        {
            get
            {
                string name = ThemeManager.GetPreferenceDisplayName(_themePreference);

                if (_themePreference == ThemePreference.FollowSystem)
                {
                    name += Loc.T("Settings_CurrentSuffix") + ThemeManager.GetThemeDisplayName(ThemeManager.CurrentTheme) + "）";
                }

                return name;
            }
        }

        public bool IsLightTheme
        {
            get { return _themePreference == ThemePreference.Light; }
        }

        public bool IsDarkTheme
        {
            get { return _themePreference == ThemePreference.Dark; }
        }

        public bool IsSystemTheme
        {
            get { return _themePreference == ThemePreference.FollowSystem; }
        }

        /// <summary>
        /// 切换主题偏好：立即生效并持久化。
        /// </summary>
        public void SetThemePreference(ThemePreference preference)
        {
            ThemePreference = preference;

            try
            {
                ThemeManager.SetPreference(Application.Current, preference);
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Settings_ThemeFailed"), ex, false);
            }

            _settings.ThemePreference = preference;
            SaveSettings();

            // 切换后重新通知一次（跟随系统的实际结果可能变化）
            OnPropertyChanged("ThemeDisplayText");
            StatusMessage = Loc.T("Settings_ThemeChanged") + ThemeDisplayText;
        }

        /// <summary>在浅色 / 深色之间快速切换（快捷键与按钮用）。</summary>
        public void ToggleTheme()
        {
            AppTheme target = ThemeManager.CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            SetThemePreference(target == AppTheme.Dark ? ThemePreference.Dark : ThemePreference.Light);
        }

        #endregion

        #region 进度反馈

        /// <summary>是否显示进度界面（需求 P3-15：耗时操作才有反馈）。</summary>
        public bool IsProgressVisible
        {
            get { return _isProgressVisible; }
            private set { SetProperty(ref _isProgressVisible, value, "IsProgressVisible"); }
        }

        /// <summary>进度值（0~100）。</summary>
        public double ProgressValue
        {
            get { return _progressValue; }
            private set
            {
                if (SetProperty(ref _progressValue, value, "ProgressValue"))
                {
                    OnPropertyChanged("ProgressText");
                }
            }
        }

        /// <summary>进度提示文字。</summary>
        public string ProgressMessage
        {
            get { return _progressMessage; }
            private set
            {
                if (SetProperty(ref _progressMessage, value, "ProgressMessage"))
                {
                    OnPropertyChanged("ProgressText");
                }
            }
        }

        /// <summary>进度文本（百分比 + 说明）。</summary>
        public string ProgressText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_progressMessage))
                {
                    return string.Format("{0}（{1:0}%）", _progressMessage, _progressValue);
                }

                return string.Format("{0:0}%", _progressValue);
            }
        }

        /// <summary>
        /// 直接设置进度显示状态。
        /// 正式代码请使用 <see cref="CreateProgressReporter"/>；这里供自检验证绑定属性。
        /// </summary>
        internal void SetProgressForTest(double percent, string message)
        {
            ProgressMessage = message;
            ProgressValue = percent;
            IsProgressVisible = true;
        }

        /// <summary>创建用于耗时操作的进度上报器。</summary>
        private ProgressReporter CreateProgressReporter(string initialMessage)
        {
            return new ProgressReporter(
                _dispatcherService,
                info =>
                {
                    ProgressValue = info.Percent;

                    if (!string.IsNullOrWhiteSpace(info.Message))
                    {
                        ProgressMessage = info.Message;
                    }
                },
                () =>
                {
                    ProgressMessage = initialMessage;
                    ProgressValue = 0.0;
                    IsProgressVisible = true;
                },
                () =>
                {
                    IsProgressVisible = false;
                    ProgressValue = 0.0;
                    ProgressMessage = null;
                });
        }

        #endregion

        #region 设置读写

        private void LoadSettings()
        {
            try
            {
                _settings = _settingsService != null ? _settingsService.Load() : new AppSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MainViewModel] 读取设置失败: " + ex.Message);
                _settings = new AppSettings();
            }

            if (_settings == null)
            {
                _settings = new AppSettings();
            }

            ThemePreference = _settings.ThemePreference;
            RefreshRecentFiles();
        }

        private void SaveSettings()
        {
            if (_settingsService == null)
            {
                return;
            }

            try
            {
                _settings.ThemePreference = _themePreference;

                // 只记"适应窗口 / 原始大小"这两种**意图**。
                // 自由缩放（Free）记的是用户随手滚出来的比例，既没有稳定的语义、
                // 下张图尺寸也未必一样，记下来只会让人莫名其妙。
                _settings.SavedZoomMode = _zoomMode == ZoomMode.Free
                    ? (int)ZoomMode.FitToWindow
                    : (int)_zoomMode;

                _settingsService.Save(_settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MainViewModel] 保存设置失败: " + ex.Message);
            }
        }

        #endregion

        #region 界面语言

        /// <summary>可选语言（供菜单绑定）。</summary>
        public System.Collections.Generic.IList<LanguageOption> AvailableLanguages
        {
            get { return LocalizationService.Instance.AvailableLanguages; }
        }

        /// <summary>当前界面语言代码。</summary>
        public string CurrentLanguage
        {
            get { return LocalizationService.Instance.CurrentCulture; }
        }

        /// <summary>当前是否中文（菜单勾选用）。</summary>
        public bool IsLanguageChinese
        {
            get { return string.Equals(CurrentLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase); }
            set { if (value) SetLanguage("zh-CN"); }
        }

        /// <summary>当前是否英文（菜单勾选用）。</summary>
        public bool IsLanguageEnglish
        {
            get { return string.Equals(CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase); }
            set { if (value) SetLanguage("en-US"); }
        }

        /// <summary>
        /// 切换界面语言并记住选择。
        ///
        /// XAML 那一侧不需要逐个刷新：语言字典是整体换掉的，
        /// 所有 <c>{DynamicResource}</c> 会自动重新求值。
        /// 需要我做的只有两件事：换字典（服务内部做）与把偏好写进设置。
        /// </summary>
        private void SetLanguage(string culture)
        {
            if (string.Equals(culture, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LocalizationService.Instance.SetLanguage(culture);

            if (_settings != null)
            {
                _settings.LanguageCulture = culture;
            }

            SaveSettings();

            OnPropertyChanged("CurrentLanguage");
            OnPropertyChanged("IsLanguageChinese");
            OnPropertyChanged("IsLanguageEnglish");

            // 状态栏那几行是 ViewModel 拼出来的，不跟着 XAML 刷新，得手动重发一遍。
            RaiseLocalizedTextPropertiesChanged();

            StatusMessage = Loc.T("Status_LanguageChanged", culture);
        }

        /// <summary>语言切换后重发"由代码拼出来的"(非 XAML 绑定)文本。</summary>
        private void RaiseLocalizedTextPropertiesChanged()
        {
            OnPropertyChanged("SelectedAnnotationInfoText");
            OnPropertyChanged("WindowTitle");
            OnPropertyChanged("FormatText");
            OnPropertyChanged("DpiText");
            OnPropertyChanged("PhysicalSizeText");
            OnPropertyChanged("ZoomPercentText");
            OnPropertyChanged("HasSelectedAnnotation");
            RefreshRecentFiles();
        }

        #endregion

        #region 窗口 / 面板状态记忆

        /// <summary>右侧面板宽度的下限（与 MainWindow.xaml 里那一列的 MinWidth 保持一致）。</summary>
        public const double MinimumPanelWidth = 220.0;

        /// <summary>右侧面板宽度的上限 —— 再宽就把画布挤没了。</summary>
        public const double MaximumPanelWidth = 640.0;

        /// <summary>
        /// 保存过的窗口矩形；没保存过时返回 null。
        ///
        /// 注意这里**不做屏幕校验** —— 校验属于界面层（要知道各显示器的工作区），
        /// 由 <see cref="Services.WindowPlacement.TryFit"/> 负责。
        /// ViewModel 只负责"把上次记下来的数字交出来 / 收回去"。
        /// </summary>
        public Rect? SavedWindowBounds
        {
            get
            {
                if (_settings == null || !_settings.HasWindowBounds)
                {
                    return null;
                }

                return new Rect(
                    _settings.WindowLeft,
                    _settings.WindowTop,
                    _settings.WindowWidth,
                    _settings.WindowHeight);
            }
        }

        /// <summary>保存过的最大化状态。</summary>
        public bool SavedWindowMaximized
        {
            get { return _settings != null && _settings.WindowMaximized; }
        }

        /// <summary>保存过的右侧面板宽度；没有有效值时返回 0。</summary>
        public double SavedPanelWidth
        {
            get
            {
                if (_settings == null)
                {
                    return 0.0;
                }

                double width = _settings.PanelWidth;

                // 面板宽度给上下限：太小会挤到看不见，太大就把画布压没了。
                // 下限与 XAML 里那一列的 MinWidth 对齐，避免"存进去的值反而被界面拒绝"。
                if (double.IsNaN(width) || width < MinimumPanelWidth)
                {
                    return 0.0;
                }

                return width > MaximumPanelWidth ? MaximumPanelWidth : width;
            }
        }

        /// <summary>
        /// 记住窗口与面板状态。由界面层在关闭前调用（几何只有界面层知道）。
        /// </summary>
        public void SaveWindowState(Rect bounds, bool maximized, double panelWidth)
        {
            if (_settings == null || _settingsService == null)
            {
                return;
            }

            try
            {
                if (bounds.Width >= WindowPlacement.MinimumWidth
                    && bounds.Height >= WindowPlacement.MinimumHeight)
                {
                    _settings.WindowLeft = bounds.Left;
                    _settings.WindowTop = bounds.Top;
                    _settings.WindowWidth = bounds.Width;
                    _settings.WindowHeight = bounds.Height;
                }

                _settings.WindowMaximized = maximized;

                // 收敛而不是"超限就丢弃"：丢弃会让上一次的旧值悄悄留着，
                // 表现为"我明明把面板拖得很宽，下次打开还是旧的宽度"。
                if (!double.IsNaN(panelWidth) && panelWidth > 0.0)
                {
                    _settings.PanelWidth = Math.Max(MinimumPanelWidth, Math.Min(MaximumPanelWidth, panelWidth));
                }

                SaveSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MainViewModel] 保存窗口状态失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 上次用过的缩放模式（没有记录时返回"适应窗口"）。
        ///
        /// 放在这里而不是直接读设置对象：调用方（加载图片时）不该关心"设置里存的是 int"。
        /// </summary>
        public ZoomMode PreferredZoomMode
        {
            get
            {
                if (_settings == null || !_settings.HasZoomModeSaved)
                {
                    return ZoomMode.FitToWindow;
                }

                int value = _settings.SavedZoomMode;

                if (value < 0 || value > (int)ZoomMode.ActualSize)
                {
                    return ZoomMode.FitToWindow;
                }

                return (ZoomMode)value;
            }
        }

        #endregion
    }
}
