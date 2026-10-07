using System;
using System.Windows.Input;

namespace PSText.Infrastructure
{
    /// <summary>
    /// 带参数的同步命令。
    /// 常用于"单选式"操作（例如打印布局模式），XAML 里通过 CommandParameter 传值。
    /// 支持把字符串参数自动解析为枚举，便于纯 XAML 使用。
    /// </summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Predicate<T> _canExecute;

        public RelayCommand(Action<T> execute, Predicate<T> canExecute = null)
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

            if (_canExecute == null)
            {
                return true;
            }

            T value;

            if (TryConvert(parameter, out value))
            {
                return _canExecute(value);
            }

            return false;
        }

        public void Execute(object parameter)
        {
            if (_execute == null)
            {
                return;
            }

            T value;

            if (TryConvert(parameter, out value))
            {
                _execute(value);
            }
        }

        private static bool TryConvert(object parameter, out T value)
        {
            if (parameter is T)
            {
                value = (T)parameter;
                return true;
            }

            if (parameter == null)
            {
                value = default(T);
                return true;
            }

            // 支持从 XAML 传入字符串：枚举解析 + 基础类型转换
            if (parameter is string)
            {
                string text = ((string)parameter).Trim();

                if (typeof(T).IsEnum)
                {
                    try
                    {
                        value = (T)Enum.Parse(typeof(T), text, true);
                        return true;
                    }
                    catch (ArgumentException)
                    {
                        value = default(T);
                        return false;
                    }
                }

                try
                {
                    value = (T)Convert.ChangeType(text, typeof(T));
                    return true;
                }
                catch (Exception)
                {
                    value = default(T);
                    return false;
                }
            }

            value = default(T);
            return false;
        }
    }
}
