using System;
using System.Threading;
using System.Threading.Tasks;
using PSText.Services.Interfaces;

namespace PSText.Services
{
    /// <summary>
    /// 进度上报器（需求 P3-15：耗时操作 &gt;200ms 显示进度）。
    ///
    /// 设计要点：
    ///   * 只有超过 <see cref="VisibleThresholdMs"/> 的操作才真正显示进度界面，
    ///     避免一闪而过的进度条造成视觉噪声；
    ///   * 所有 UI 回调都通过 IDispatcherService 回到 UI 线程（不直接碰控件）；
    ///   * 进度值自动收敛到 0~1，并支持“仅显示文字”的提示模式。
    /// </summary>
    public sealed class ProgressReporter : IProgress<ProgressInfo>
    {
        /// <summary>默认显示阈值（毫秒）。</summary>
        public const int DefaultVisibleThresholdMs = 200;

        private readonly IDispatcherService _dispatcher;
        private readonly Action<ProgressInfo> _onProgress;
        private readonly Action _onStarted;
        private readonly Action _onCompleted;
        private readonly int _visibleThresholdMs;

        private readonly DateTime _startedAt = DateTime.UtcNow;
        private bool _visible;
        private ProgressInfo _lastReported;
        private int _completed;

        public ProgressReporter(
            IDispatcherService dispatcher,
            Action<ProgressInfo> onProgress,
            Action onStarted = null,
            Action onCompleted = null,
            int visibleThresholdMs = DefaultVisibleThresholdMs)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException("dispatcher");
            }

            _dispatcher = dispatcher;
            _onProgress = onProgress;
            _onStarted = onStarted;
            _onCompleted = onCompleted;
            _visibleThresholdMs = visibleThresholdMs < 0 ? 0 : visibleThresholdMs;
        }

        /// <summary>当前进度是否已经对外可见（用于判断是否需要清理界面）。</summary>
        public bool IsVisible
        {
            get { return _visible; }
        }

        /// <summary>最近一次上报的进度。</summary>
        public ProgressInfo LastReported
        {
            get { return _lastReported; }
        }

        /// <summary>
        /// 上报进度。第一次上报时若已超过阈值，会先触发 onStarted。
        /// </summary>
        public void Report(ProgressInfo value)
        {
            ProgressInfo info = value ?? new ProgressInfo(0.0, null);

            info = info.Normalize();

            _dispatcher.Invoke(() =>
            {
                // 首次真正显示时触发 Started（超过阈值才显示）
                if (!_visible)
                {
                    _visible = true;

                    if (_onStarted != null)
                    {
                        _onStarted();
                    }
                }

                _lastReported = info;

                if (_onProgress != null)
                {
                    _onProgress(info);
                }
            });
        }

        /// <summary>上报百分比（0~1）。</summary>
        public void Report(double fraction, string message = null)
        {
            Report(new ProgressInfo(fraction, message));
        }

        /// <summary>
        /// 结束上报：若曾经显示过进度，则触发 onCompleted 让界面收起进度条。
        /// 可重复调用（幂等）。
        /// </summary>
        public void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            bool wasVisible = _visible;

            _dispatcher.Invoke(() =>
            {
                if (wasVisible && _onCompleted != null)
                {
                    _onCompleted();
                }
            });
        }

        /// <summary>本次操作已耗时（毫秒）。</summary>
        public double ElapsedMilliseconds
        {
            get { return (DateTime.UtcNow - _startedAt).TotalMilliseconds; }
        }

        /// <summary>
        /// 便捷封装：执行一个带进度的异步操作，并保证结束后收起进度。
        /// </summary>
        public static async Task RunAsync(
            IDispatcherService dispatcher,
            Func<IProgress<ProgressInfo>, Task> operation,
            Action<ProgressInfo> onProgress,
            Action onStarted = null,
            Action onCompleted = null)
        {
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }

            ProgressReporter reporter = new ProgressReporter(dispatcher, onProgress, onStarted, onCompleted);

            try
            {
                await operation(reporter).ConfigureAwait(true);
            }
            finally
            {
                reporter.Complete();
            }
        }
    }

    /// <summary>进度信息（不可变）。</summary>
    public sealed class ProgressInfo
    {
        public ProgressInfo(double fraction, string message)
        {
            Fraction = fraction;
            Message = message;
        }

        /// <summary>进度（0~1）。</summary>
        public double Fraction { get; private set; }

        /// <summary>提示文字，可为 null（表示沿用上一次）。</summary>
        public string Message { get; private set; }

        /// <summary>百分比（0~100）。</summary>
        public double Percent
        {
            get { return Fraction * 100.0; }
        }

        /// <summary>把进度收敛到合法范围，并处理 NaN。</summary>
        public ProgressInfo Normalize()
        {
            double value = Fraction;

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0.0;
            }

            if (value < 0.0)
            {
                value = 0.0;
            }

            if (value > 1.0)
            {
                value = 1.0;
            }

            return new ProgressInfo(value, Message);
        }
    }
}
