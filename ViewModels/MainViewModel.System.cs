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
                    return "文件关联不可用";
                }

                switch (_fileAssociationService.GetState())
                {
                    case FileAssociationState.Registered:
                        return "文件关联：已注册";
                    case FileAssociationState.RegisteredForAnotherCopy:
                        return "文件关联：指向另一份程序";
                    default:
                        return "文件关联：未注册";
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
                ? "把 PS-text 登记到图片文件的“打开方式”列表里，并添加右键菜单项。\n\n"
                  + "· 只写当前用户的注册表，**不需要管理员权限**；\n"
                  + "· **不会**改动你现有的默认打开程序；\n"
                  + "· 右键菜单会多出“用 PS-text 编辑”与“用 PS-text 打印”两项\n"
                  + "  （打印只到预览为止，不会直接送打印机）；\n"
                  + "· 想让它成为默认打开程序，可在系统「设置默认程序」里自行选择。\n\n"
                  + "确定要注册吗？"
                : "移除 PS-text 在图片“打开方式”列表与右键菜单里的登记。\n\n"
                  + "已保存的图片文件不受影响，之后仍可通过系统的“打开方式 → 选择其他应用”使用。\n\n"
                  + "确定要注销吗？";

            if (_dialogService.Confirm(confirmMessage, "文件关联") != ConfirmResult.Yes)
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
                    _dialogService.ShowInformation(result.Message, "文件关联");
                }
                else
                {
                    _dialogService.ShowError(result.Message, "文件关联失败");
                }
            }
            catch (Exception ex)
            {
                HandleError(register ? "注册文件关联失败。" : "注销文件关联失败。", ex, true);
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
                        stateText = "已注册，并且指向当前这一份程序。\n"
                                    + "在图片上右键 →「打开方式」就能看到 PS-text。";
                        break;
                    case FileAssociationState.RegisteredForAnotherCopy:
                        stateText = "已注册，但登记的是**另一份**程序：\n"
                                    + (service.RegisteredCommandText ?? "（读不到命令）") + "\n\n"
                                    + "典型原因是绿色版被移动或复制过。重新点一次“注册文件关联”即可指向当前这份。";
                        break;
                    default:
                        stateText = "尚未注册。\n"
                                    + "注册之后，PS-text 会出现在图片文件“打开方式”的候选列表里。";
                        break;
                }

                string text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\n\nProgID：{1}\n关联扩展名：{2} 种\n右键菜单：{3}\n打开命令：{4}\n\n"
                    + "命令行等价操作：\n  PS-text.exe --register\n  PS-text.exe --unregister\n  PS-text.exe --assoc-status",
                    stateText,
                    service.ProgId,
                    service.SupportedExtensionCount,
                    service.HasContextMenu ? "已添加“用 PS-text 编辑 / 打印”" : "未添加",
                    service.RegisteredCommandText ?? "（无）");

                _dialogService.ShowInformation(text, "文件关联状态");
            }
            catch (Exception ex)
            {
                HandleError("读取文件关联状态失败。", ex, true);
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
                    _dialogService.ShowInformation(result.Message, "设置默认程序");
                }
            }
            catch (Exception ex)
            {
                HandleError("打开“设置默认程序”失败。", ex, true);
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
                _dialogService.ShowInformation(info.ToDisplayText(), "运行环境");
            }
            catch (Exception ex)
            {
                HandleError("读取运行环境信息失败。", ex, true);
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
                        "日志目录还不存在 —— 说明程序至今没有发生过崩溃。\n\n"
                        + "（日志只在出现异常时才会写入，正常的保存/打印不会留下日志。）",
                        "日志文件夹");
                    return;
                }

                Process.Start("explorer.exe", "\"" + directory + "\"");
                StatusMessage = "已打开日志目录：" + directory;
            }
            catch (Exception ex)
            {
                HandleError("打开日志文件夹失败。", ex, true);
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

            return "PS-text 图片编辑器\n"
                   + "· 支持 JPG / PNG / BMP / TIFF / GIF 的加载与保存\n"
                   + "· 滚轮缩放（10% ~ 1000%）、按住鼠标拖动或空格键平移\n"
                   + "· 双击画布在“适应窗口 / 原始大小”之间切换\n"
                   + "· 亮度 / 对比度 / 饱和度 / 色温实时预览，可撤销 30 步\n"
                   + "· 批量流水线、非破坏性标注、修补 / 消除、打印与批量打印\n\n"
                   + "快捷键：Ctrl+O 打开，Ctrl+S 保存，Ctrl+Z / Ctrl+Y 撤销重做，Ctrl+P 打印\n\n"
                   + "——— 运行环境 ———\n"
                   + (info == null ? "（探测失败）" : info.ToDisplayText());
        }

        #endregion
    }
}
