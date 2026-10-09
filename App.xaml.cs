using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PSText.Services;
using PSText.Services.Interfaces;
using PSText.ViewModels;
using PSText.Views;
namespace PSText
{
    /// <summary>
    /// 应用程序：组装服务（手工依赖注入）、挂接全局异常处理、创建主窗口。
    /// 不引入第三方 DI 容器，减少 Win7 环境下的部署依赖。
    /// 入口在 Program.Main，这里只提供 Run(args) 以便自检模式先于窗口创建执行。
    /// </summary>
    public partial class App : Application
    {
        private IDialogService _dialogService;
        private ISettingsService _settingsService;
        private int _selfTestExitCode;

        /// <summary>供自检与调试读取的命令行参数。</summary>
        public static string[] StartupArguments { get; private set; }

        static App()
        {
            StartupArguments = new string[0];
        }

        /// <summary>应用程序主流程。返回进程退出码。</summary>
        public int Run(string[] args)
        {
            StartupArguments = args ?? new string[0];
            CommandLineOptions options = CommandLineOptions.Parse(StartupArguments);

            // 全局异常兜底：任何未处理异常都要给出提示而不是静默退出。
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            _dialogService = new WpfDialogService();

            // 设置需要在创建窗口之前读取：主题偏好会决定合并哪套配色字典。
            _settingsService = new XmlSettingsService();
            AppSettings settings = _settingsService.Load();

            // ---- 无界面模式：自检 / 注册关联 / 注销关联 / 查关联状态 / 查运行环境 ----
            // 放在创建窗口之前，既省掉一整套 WPF 初始化，也让这些开关可以被脚本直接调用。
            if (options.IsHeadless)
            {
                return RunHeadless(options);
            }

            // ---- 组合根：服务 → ViewModel → View ----
            IImageService imageService = new WpfImageService();
            IPrintService printService = new WpfPrintService();
            IDispatcherService dispatcherService = new WpfDispatcherService(Dispatcher);

            MainViewModel viewModel = new MainViewModel(imageService, _dialogService, dispatcherService);
            viewModel.PrintService = printService;
            viewModel.SettingsService = _settingsService;
            viewModel.FileAssociationService = new FileAssociationService();
            viewModel.RuntimeInfo = DotNetRuntimeProbe.Probe();

            // 主题与通用样式必须在创建窗口前合并（见 ThemeManager 的说明：窗口解析阶段的
            // StaticResource 查不到 App.xaml 里静态合并的字典，必须代码合并）。
            ThemeManager.Apply(this, settings.ThemePreference);

            MainWindow window = new MainWindow { DataContext = viewModel };

            // 支持通过命令行传入图片路径（例如“打开方式”关联）
            string initialFile = options.ImagePath;
            if (!string.IsNullOrWhiteSpace(initialFile))
            {
                window.Loaded += async (sender, eventArgs) => await viewModel.LoadFromPathAsync(initialFile);
            }

            // 运行环境提醒。
            //
            // 为什么这条提示是必要的：本程序没有 App.config，声明的是 CLR v4.0，
            // 因此在只装了 .NET 4.5 / 4.6.2 的 Win7 机器上 **exe 能启动**，却会在用到
            // 4.8 才有的 API 时半路崩溃 —— 用户看到的是"用着用着就闪退"，无从判断原因。
            // 启动时把实际检测到的版本说清楚，比事后翻崩溃日志友好得多。
            if (viewModel.RuntimeInfo.ProbeSucceeded && !viewModel.RuntimeInfo.IsNet48OrLater)
            {
                window.Loaded += (sender, eventArgs) => _dialogService.ShowInformation(
                    BuildRuntimeWarning(viewModel.RuntimeInfo),
                    "运行环境提示");
            }

            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            // 进入消息循环；窗口关闭后返回。
            int exitCode = base.Run();
            return _selfTestExitCode != 0 ? _selfTestExitCode : exitCode;
        }

        #region 无界面模式

        /// <summary>
        /// 处理不需要界面的启动模式。
        ///
        /// 这些开关存在的意义不只是给开发者用：免安装版要做"绿色部署"，
        /// 就得能在安装脚本里直接注册文件关联，而不是让部署的人挨个点菜单。
        /// </summary>
        private int RunHeadless(CommandLineOptions options)
        {
            ConsoleBridge console = new ConsoleBridge();

            switch (options.Mode)
            {
                case StartupMode.SelfTest:
                    // 自检里也会构造真实 Window（用于验证 XAML 资源能被解析），
                    // 因此必须先合并主题字典，否则窗口会因为找不到共享样式而抛 XamlParseException。
                    ThemeManager.Apply(this, AppTheme.Light);
                    _selfTestExitCode = SelfTest.Run(StartupArguments);
                    Shutdown(_selfTestExitCode);
                    return _selfTestExitCode;

                case StartupMode.RegisterAssociation:
                {
                    IFileAssociationService service = new FileAssociationService();
                    FileAssociationResult result = service.Register();
                    console.Report("PS-text 文件关联", result.Message, !result.Success);
                    Shutdown(result.Success ? 0 : 1);
                    return result.Success ? 0 : 1;
                }

                case StartupMode.UnregisterAssociation:
                {
                    IFileAssociationService service = new FileAssociationService();
                    FileAssociationResult result = service.Unregister();
                    console.Report("PS-text 文件关联", result.Message, !result.Success);
                    Shutdown(result.Success ? 0 : 1);
                    return result.Success ? 0 : 1;
                }

                case StartupMode.AssociationStatus:
                {
                    IFileAssociationService service = new FileAssociationService();
                    string text = DescribeAssociationState(service);
                    console.Report("PS-text 文件关联", text, false);
                    Shutdown(0);
                    return 0;
                }

                default:
                {
                    RuntimeEnvironmentInfo info = DotNetRuntimeProbe.Probe();
                    info.SettingsFilePath = _settingsService != null ? _settingsService.SettingsFilePath : null;
                    info.LogDirectory = CrashLogger.LogDirectory;

                    console.Report("PS-text 运行环境", info.ToDisplayText(), false);
                    Shutdown(info.IsNet48OrLater ? 0 : 1);
                    return info.IsNet48OrLater ? 0 : 1;
                }
            }
        }

        private static string DescribeAssociationState(IFileAssociationService service)
        {
            FileAssociationState state = service.GetState();
            StringBuilder builder = new StringBuilder();

            switch (state)
            {
                case FileAssociationState.Registered:
                    builder.AppendLine("状态：已注册（指向当前这一份程序）");
                    break;
                case FileAssociationState.RegisteredForAnotherCopy:
                    builder.AppendLine("状态：已注册，但指向**另一份**程序");
                    builder.AppendLine("（绿色版被移动过之后会出现这种情况，重新注册即可修正）");
                    break;
                default:
                    builder.AppendLine("状态：未注册");
                    break;
            }

            builder.AppendLine("ProgID：" + service.ProgId);
            builder.AppendLine("打开命令：" + (service.RegisteredCommandText ?? "（无）"));
            builder.AppendLine();
            builder.AppendLine("注册：PS-text.exe --register");
            builder.AppendLine("注销：PS-text.exe --unregister");

            return builder.ToString().TrimEnd();
        }

        private static string BuildRuntimeWarning(RuntimeEnvironmentInfo info)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "检测到当前系统安装的 .NET Framework 版本低于 4.8。\n\n"
                + "检测到：{0}（Release {1}）\n"
                + "本程序需要：.NET Framework 4.8 或更高\n\n"
                + "程序仍然可以启动，但部分功能可能会异常或在使用中闪退。\n"
                + "建议先安装 .NET Framework 4.8 再使用：\n{2}\n\n"
                + "（Windows 7 SP1 请选择“4.8 运行时”，不要选 4.8.1 及以上，它们不支持 Win7。）",
                info.NetVersionText ?? "未知",
                info.NetRelease,
                DotNetRuntimeProbe.DownloadUrl);
        }

        #endregion

        protected override void OnExit(ExitEventArgs e)
        {
            // 退出时落盘主题偏好：即使用户从未主动切主题（沿用了“跟随系统”的默认值），
            // 也把当前状态写下来，保证下次启动行为一致。
            try
            {
                if (_settingsService != null)
                {
                    AppSettings settings = _settingsService.Load();
                    settings.ThemePreference = ThemeManager.Preference;
                    _settingsService.Save(settings);
                }
            }
            catch (Exception ex)
            {
                // 退出阶段的设置写入失败不应影响关闭流程
                System.Diagnostics.Debug.WriteLine("[App] 退出时保存设置失败: " + ex.Message);
            }

            ThemeManager.Persist(this);
            base.OnExit(e);
        }

        #region 全局异常处理

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 已在 ViewModel 内处理的异常不会走到这里；能到这里说明是意料之外的问题。
            e.Handled = true;
            ReportFatal("程序运行中出现未处理的错误。", e.Exception);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            ReportFatal("程序遇到严重错误，即将退出。", exception);
        }

        private void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            // 观察异常，避免在 .NET 4.8 上触发进程终止策略。
            e.SetObserved();
            System.Diagnostics.Debug.WriteLine("[App] 未观察的任务异常: " + e.Exception);

            // Debug 输出在 Release 下不可见，这里同样落盘，便于排查后台任务的静默失败。
            CrashLogger.Log("未被观察的任务异常（已被观察处理，程序继续运行）。", e.Exception);
        }

        private void ReportFatal(string message, Exception exception)
        {
            string detail = BuildDetail(message, exception);
            System.Diagnostics.Debug.WriteLine("[App] " + detail);

            // 关键：Release 下 Debug 输出不可见，必须落盘，否则用户报错时无从分析。
            string logPath = CrashLogger.Log(message, exception);

            try
            {
                if (_dialogService != null)
                {
                    string messageWithPath = message;

                    if (!string.IsNullOrEmpty(logPath))
                    {
                        messageWithPath += "\n\n详细日志已保存到：\n" + logPath;
                    }

                    _dialogService.ShowException(messageWithPath, exception, "PS-text 错误");
                }
            }
            catch (Exception)
            {
                // 连弹窗都失败时只能放弃提示，避免二次异常。
            }
        }

        private static string BuildDetail(string message, Exception exception)
        {
            StringBuilder builder = new StringBuilder(message);
            builder.AppendLine();

            while (exception != null)
            {
                builder.AppendLine(exception.GetType().Name + ": " + exception.Message);
                exception = exception.InnerException;
            }

            return builder.ToString();
        }

        #endregion
    }
}
