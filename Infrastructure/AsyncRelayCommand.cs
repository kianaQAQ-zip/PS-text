using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PSText.Infrastructure
{
    /// <summary>
    /// 异步命令实现：以 Task 承载异步逻辑，避免 async void。
    /// - 执行期间默认禁用自身，防止重入
    /// - 内部异常通过 ErrorHandler 上报，不向 UI 线程抛出
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;
        private bool _isRunning;

        /// <summary>执行过程中发生的异常回调（由 ViewModel 注入，用于弹窗提示）。</summary>
        public Action<Exception> ErrorHandler { get; set; }

        /// <summary>执行状态变化回调（用于显示 Loading 遮罩）。</summary>
        public Action<bool> RunningChanged { get; set; }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool> canExecute = null)
            : this(
                executeAsync == null ? (Func<object, Task>)null : _ => executeAsync(),
                canExecute == null ? (Predicate<object>)null : _ => canExecute())
        {
        }

        public AsyncRelayCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        /// <summary>当前是否正在执行。</summary>
        public bool IsRunning
        {
            get { return _isRunning; }
        }

        public bool CanExecute(object parameter)
        {
            if (_executeAsync == null || _isRunning)
            {
                return false;
            }

            try
            {
                return _canExecute == null || _canExecute(parameter);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[AsyncRelayCommand] CanExecute 异常: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 注意：这里刻意声明为 void（ICommand 契约要求），但内部把全部工作交给
        /// ExecuteAsync 返回的 Task，不存在 async void 的异常吞没与不可等待问题。
        /// </summary>
        public void Execute(object parameter)
        {
            if (_executeAsync == null)
            {
                return;
            }

            Task task = ExecuteAsync(parameter);
            if (task == null)
            {
                return;
            }

            // 观察异常：ExecuteAsync 内部已捕获并转交 ErrorHandler，此处仅兜底。
            task.ContinueWith(
                t => System.Diagnostics.Debug.WriteLine("[AsyncRelayCommand] 未观察异常: " + t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        /// <summary>可等待的执行入口，便于测试与串联。</summary>
        public async Task ExecuteAsync(object parameter)
        {
            if (_executeAsync == null || _isRunning)
            {
                return;
            }

            _isRunning = true;
            NotifyRunningChanged(true);

            try
            {
                await _executeAsync(parameter).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消，不作为错误处理。
            }
            catch (Exception ex)
            {
                Action<Exception> handler = ErrorHandler;
                if (handler != null)
                {
                    handler(ex);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AsyncRelayCommand] 执行异常: " + ex);
                }
            }
            finally
            {
                _isRunning = false;
                NotifyRunningChanged(false);
            }
        }

        private void NotifyRunningChanged(bool running)
        {
            Action<bool> handler = RunningChanged;
            if (handler != null)
            {
                handler(running);
            }

            CommandManager.InvalidateRequerySuggested();
        }
    }
}
