using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PSText.Services.Interfaces
{
    /// <summary>
    /// UI 线程调度抽象。
    /// 目的：ViewModel 不直接引用 Dispatcher，便于单元测试与替换；
    ///      所有耗时操作的进度回调都通过它回到 UI 线程。
    /// </summary>
    public interface IDispatcherService
    {
        /// <summary>当前是否在 UI 线程。</summary>
        bool IsOnUiThread { get; }

        /// <summary>同步（阻塞）在 UI 线程执行；已在 UI 线程则直接执行。</summary>
        void Invoke(Action action);

        /// <summary>异步投递到 UI 线程执行。</summary>
        Task InvokeAsync(Action action);

        /// <summary>异步投递到 UI 线程执行并返回结果。</summary>
        Task<T> InvokeAsync<T>(Func<T> function);

        /// <summary>获取底层 Dispatcher（少数必须构造 UI 对象的场景使用）。</summary>
        Dispatcher Dispatcher { get; }
    }
}
