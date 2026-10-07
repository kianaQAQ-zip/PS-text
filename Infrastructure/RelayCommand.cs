using System;
using System.Windows.Input;

namespace PSText.Infrastructure
{
    /// <summary>
    /// 同步命令实现（ICommand）。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(
                execute == null ? (Action<object>)null : _ => execute(),
                canExecute == null ? (Predicate<object>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            if (_execute == null)
            {
                return false;
            }

            try
            {
                return _canExecute == null || _canExecute(parameter);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[RelayCommand] CanExecute 异常: " + ex);
                return false;
            }
        }

        public void Execute(object parameter)
        {
            if (_execute == null)
            {
                return;
            }

            try
            {
                _execute(parameter);
            }
            catch (Exception ex)
            {
                // 命令内部异常不应导致进程崩溃，交由上层统一处理（App.DispatcherUnhandledException）。
                throw new InvalidOperationException("命令执行失败: " + ex.Message, ex);
            }
        }

        /// <summary>手动触发 CanExecute 重新求值。</summary>
        public static void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
