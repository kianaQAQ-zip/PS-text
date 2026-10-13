using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using PSText.Infrastructure;
using PSText.Services;
using PSText.Services.Interfaces;

namespace PSText.ViewModels
{
    /// <summary>
    /// MainViewModel 的「系统集成」部分（M4）。
    ///
    /// 包含三件互相独立、但都属于"把程序装进系统"的事情：
    ///   1. **文件关联**：注册 / 注销 / 查状态 / 跳转到系统的"设置默认程序"；
    ///   2. **运行环境诊断**：把 .NET 版本、进程位数、路径一次性列清楚，替代"闪退后猜原因"；
    ///   3. **日志目录**：出问题时能一键把日志文件夹打开，用户才能把日志发出来。
    ///
    /// 这三件都不改用户的图片数据，因此都不需要撤销，也不进历史。
    /// </summary>
    public sealed partial class MainViewModel
    {
        private IFileAssociationService _fileAssociationService;

        /// <summary>
        /// 文件关联服务。由组合根（App）注入；未注入时相关命令自动不可用
        /// （保证自检环境与命令行模式不依赖注册表）。
        /// </summary>
        public IFileAssociationService FileAssociationService
        {
            get { return _fileAssociationService; }
            set
            {
                _fileAssociationService = value;
                OnPropertyChanged("FileAssociationStatusText");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>启动时探测到的运行环境（供"关于"与"检测运行环境"使用）。</summary>
        public RuntimeEnvironmentInfo RuntimeInfo { get; set; }

        /// <summary>当前文件关联状态的一行描述（状态栏/菜单提示可用）。</summary>
        public string FileAssociationStatusText
        {
            get
            {
                if (_fileAssociationService == null)
                {
                    return Loc.T("Assoc_Unavailable");
                }

                switch (_fileAssociationService.GetState())
                {
                    case FileAssociationState.Registered:
                        return Loc.T("Assoc_StateRegistered");
                    case FileAssociationState.RegisteredForAnotherCopy:
                        return Loc.T("Assoc_StateOther");
                    default:
                        return Loc.T("Assoc_StateUnregistered");
                }
            }
        }

        #region 命令

        /// <summary>注册文件关联。</summary>
        public ICommand RegisterFileAssociationCommand { get; private set; }

        /// <summary>注销文件关联。</summary>
        public ICommand UnregisterFileAssociationCommand { get; private set; }

        /// <summary>查看文件关联状态。</summary>
        public ICommand ShowFileAssociationStatusCommand { get; private set; }

        /// <summary>打开 Windows「设置默认程序」界面。</summary>
        public ICommand OpenDefaultProgramsCommand { get; private set; }

        /// <summary>显示运行环境诊断信息。</summary>
        public ICommand ShowRuntimeInfoCommand { get; private set; }

        /// <summary>打开日志文件夹。</summary>
        public ICommand OpenLogFolderCommand { get; private set; }

        /// <summary>装配系统集成相关命令（在构造函数中调用一次）。</summary>
        private void InitializeSystemCommands()
        {
            RegisterFileAssociationCommand = new RelayCommand(
                () => RunAssociationAction(true),
                () => _fileAssociationService != null && !IsBusy);

            UnregisterFileAssociationCommand = new RelayCommand(
                () => RunAssociationAction(false),
                () => _fileAssociationService != null && !IsBusy);

            ShowFileAssociationStatusCommand = new RelayCommand(
                ShowAssociationStatus,
                () => _fileAssociationService != null);

            OpenDefaultProgramsCommand = new RelayCommand(
                OpenDefaultPrograms,
                () => _fileAssociationService != null);

            ShowRuntimeInfoCommand = new RelayCommand(ShowRuntimeInfo);

            OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        }

        #endregion

        #region 文件关联

        private void RunAssociationAction(bool register)
        {
            IFileAssociationService service = _fileAssociationService;

            if (service == null)
            {
                return;
            }

            string confirmMessage = register
                ? Loc.T("Assoc_RegisterConfirm")
                  + Loc.T("Assoc_RegisterBullet1")
                  + Loc.T("Assoc_RegisterBullet2")
                  + Loc.T("Assoc_RegisterBullet3")
                  + Loc.T("Assoc_RegisterBullet4")
                  + Loc.T("Assoc_RegisterBullet5")
                  + Loc.T("Assoc_RegisterAsk")
                : Loc.T("Assoc_UnregisterConfirm")
                  + Loc.T("Assoc_UnregisterHint")
                  + Loc.T("Assoc_UnregisterAsk");

            if (_dialogService.Confirm(confirmMessage, Loc.T("Menu_FileAssociation")) != ConfirmResult.Yes)
            {
                return;
            }

            try
            {
                FileAssociationResult result = register ? service.Register() : service.Unregister();

                StatusMessage = result.Message;
                OnPropertyChanged("FileAssociationStatusText");

                if (result.Success)
                {
                    _dialogService.ShowInformation(result.Message, Loc.T("Menu_FileAssociation"));
                }
                else
                {
                    _dialogService.ShowError(result.Message, Loc.T("Assoc_Failed"));
                }
            }
            catch (Exception ex)
            {
                HandleError(register ? Loc.T("Assoc_ErrorRegister") : Loc.T("Assoc_ErrorUnregister"), ex, true);
            }
        }

        private void ShowAssociationStatus()
        {
            IFileAssociationService service = _fileAssociationService;

            if (service == null)
            {
                return;
            }

            try
            {
                FileAssociationState state = service.GetState();
                string stateText;

                switch (state)
                {
                    case FileAssociationState.Registered:
                        stateText = Loc.T("Assoc_RegisteredHere")
                                    + Loc.T("Assoc_RightClickHint");
                        break;
                    case FileAssociationState.RegisteredForAnotherCopy:
                        stateText = Loc.T("Assoc_RegisteredOther")
                                    + (service.RegisteredCommandText ?? Loc.T("Assoc_NoCommand")) + "\n\n"
                                    + Loc.T("Assoc_Moved");
                        break;
                    default:
                        stateText = Loc.T("Assoc_NotRegistered")
                                    + Loc.T("Assoc_ClickToRegister");
                        break;
                }

                string text = string.Format(
                    CultureInfo.InvariantCulture,
                    Loc.T("Runtime_StatusLine")
                    + Loc.T("Runtime_CliHeader"),
                    stateText,
                    service.ProgId,
                    service.SupportedExtensionCount,
                    service.HasContextMenu ? Loc.T("Assoc_MenuAdded") : Loc.T("Assoc_MenuNotAdded"),
                    service.RegisteredCommandText ?? Loc.T("Common_None"));

                _dialogService.ShowInformation(text, Loc.T("Assoc_StatusTitle"));
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Assoc_ErrorReadState"), ex, true);
            }
        }

        private void OpenDefaultPrograms()
        {
            IFileAssociationService service = _fileAssociationService;

            if (service == null)
            {
                return;
            }

            try
            {
                FileAssociationResult result = service.OpenDefaultProgramsSettings();

                if (result.Success)
                {
                    StatusMessage = result.Message;
                }
                else
                {
                    _dialogService.ShowInformation(result.Message, Loc.T("Assoc_DefaultApps"));
                }
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Assoc_OpenFailed"), ex, true);
            }
        }

        #endregion

        #region 运行环境与日志

        private void ShowRuntimeInfo()
        {
            try
            {
                RuntimeEnvironmentInfo info = RuntimeInfo;

                if (info == null)
                {
                    info = DotNetRuntimeProbe.Probe();
                }

                FillDiagnosticPaths(info);
                _dialogService.ShowInformation(info.ToDisplayText(), Loc.T("Runtime_Title"));
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Runtime_ReadFailed"), ex, true);
            }
        }

        private void OpenLogFolder()
        {
            try
            {
                string directory = CrashLogger.LogDirectory;

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    _dialogService.ShowInformation(
                        Loc.T("Runtime_LogEmpty")
                        + Loc.T("Runtime_LogNote"),
                        Loc.T("Runtime_LogFolderTitle"));
                    return;
                }

                Process.Start("explorer.exe", "\"" + directory + "\"");
                StatusMessage = Loc.T("Runtime_LogFolderOpened") + directory;
            }
            catch (Exception ex)
            {
                HandleError(Loc.T("Runtime_LogFolderFailed"), ex, true);
            }
        }

        /// <summary>把设置文件与日志目录补进诊断信息（这两个值只有 ViewModel 这一层知道）。</summary>
        private void FillDiagnosticPaths(RuntimeEnvironmentInfo info)
        {
            if (info == null)
            {
                return;
            }

            try
            {
                info.SettingsFilePath = _settingsService != null ? _settingsService.SettingsFilePath : null;
            }
            catch (Exception)
            {
                info.SettingsFilePath = null;
            }

            info.LogDirectory = CrashLogger.LogDirectory;
        }

        /// <summary>构造「关于」对话框的文本（原 ShowAbout 的内容 + 诊断信息）。</summary>
        internal string BuildAboutText()
        {
            RuntimeEnvironmentInfo info = RuntimeInfo;

            if (info == null)
            {
                try
                {
                    info = DotNetRuntimeProbe.Probe();
                }
                catch (Exception)
                {
                    info = null;
                }
            }

            FillDiagnosticPaths(info);

            return Loc.T("About_AppName")
                   + Loc.T("About_Feature1")
                   + Loc.T("About_Feature4")
                   + Loc.T("About_Feature5")
                   + Loc.T("About_Feature2")
                   + Loc.T("About_Feature3")
                   + Loc.T("About_Shortcuts")
                   + Loc.T("Runtime_Header")
                   + (info == null ? Loc.T("Assoc_ProbeFailed") : info.ToDisplayText());
        }

        #endregion
    }
}
