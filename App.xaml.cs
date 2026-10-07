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

            // 全局异常兜底：任何未处理异常都要给出提示而不是静默退出。
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            _dialogService = new WpfDialogService();

            // 设置需要在创建窗口之前读取：主题偏好会决定合并哪套配色字典。
            _settingsService = new XmlSettingsService();
            AppSettings settings = _settingsService.Load();

            // 自检模式：不创建窗口，跑完用例后直接返回退出码，便于自动化验证。
            if (HasArgument("--selftest"))
            {
                // 自检里也会构造真实 Window（用于验证 XAML 资源能被解析），
                // 因此必须先合并主题字典，否则窗口会因为找不到共享样式而抛 XamlParseException。
                ThemeManager.Apply(this, AppTheme.Light);
                _selfTestExitCode = SelfTest.Run(StartupArguments);
                Shutdown(_selfTestExitCode);
                return _selfTestExitCode;
            }

            // ---- 组合根：服务 → ViewModel → View ----
            IImageService imageService = new WpfImageService();
            IPrintService printService = new WpfPrintService();
            IDispatcherService dispatcherService = new WpfDispatcherService(Dispatcher);

            MainViewModel viewModel = new MainViewModel(imageService, _dialogService, dispatcherService);
            viewModel.PrintService = printService;
            viewModel.SettingsService = _settingsService;

            // 主题与通用样式必须在创建窗口前合并（见 ThemeManager 的说明：窗口解析阶段的
            // StaticResource 查不到 App.xaml 里静态合并的字典，必须代码合并）。
            ThemeManager.Apply(this, settings.ThemePreference);

            MainWindow window = new MainWindow { DataContext = viewModel };

            // 支持通过命令行传入图片路径（例如“打开方式”关联）
            string initialFile = GetFirstImageArgument(StartupArguments);
            if (!string.IsNullOrWhiteSpace(initialFile))
            {
                window.Loaded += async (sender, eventArgs) => await viewModel.LoadFromPathAsync(initialFile);
            }

            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            // 进入消息循环；窗口关闭后返回。
            int exitCode = base.Run();
            return _selfTestExitCode != 0 ? _selfTestExitCode : exitCode;
        }

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

        #region 参数解析

        private static bool HasArgument(string name)
        {
            string[] arguments = StartupArguments;
            if (arguments == null)
            {
                return false;
            }

            foreach (string argument in arguments)
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetFirstImageArgument(string[] arguments)
        {
            if (arguments == null)
            {
                return null;
            }

            foreach (string argument in arguments)
            {
                if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (System.IO.File.Exists(argument))
                    {
                        return argument;
                    }
                }
                catch (ArgumentException)
                {
                    // 非法路径忽略。
                }
            }

            return null;
        }

        #endregion

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
