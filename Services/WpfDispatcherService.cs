using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using PSText.Services.Interfaces;

namespace PSText.Services
{
    /// <summary>
    /// 基于 WPF Dispatcher 的 UI 线程调度实现。
    /// </summary>
    public sealed class WpfDispatcherService : IDispatcherService
    {
        private readonly Dispatcher _dispatcher;

        public WpfDispatcherService(Dispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException("dispatcher");
            }

            _dispatcher = dispatcher;
        }

        /// <summary>使用当前线程的 Dispatcher 构造（需在 UI 线程调用）。</summary>
        public static WpfDispatcherService CreateCurrent()
        {
            return new WpfDispatcherService(Dispatcher.CurrentDispatcher);
        }

        public Dispatcher Dispatcher
        {
            get { return _dispatcher; }
        }

        public bool IsOnUiThread
        {
            get { return _dispatcher.CheckAccess(); }
        }

        public void Invoke(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _dispatcher.Invoke(action);
        }

        public Task InvokeAsync(Action action)
        {
            if (action == null)
            {
                return Task.FromResult(0);
            }

            if (_dispatcher.CheckAccess())
            {
                action();
                return Task.FromResult(0);
            }

            return _dispatcher.InvokeAsync(action).Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> function)
        {
            if (function == null)
            {
                return Task.FromResult(default(T));
            }

            if (_dispatcher.CheckAccess())
            {
                return Task.FromResult(function());
            }

            return _dispatcher.InvokeAsync(function).Task;
        }
    }
}
