using System;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.History;
using PSText.Infrastructure.Imaging;

namespace PSText.Models
{
    /// <summary>
    /// 编辑状态：位图快照 + 当前累计的调整参数。
    ///
    /// 为什么同时存“位图快照”和“参数”：
    ///   - 位图快照保证任何类型的编辑（滤镜 / 裁剪 / 旋转 / 文字）都能被撤销；
    ///   - 参数让撤销后滑块位置能正确回显，并显示“亮度 +12 / 对比度 -5”这类描述。
    /// 关键点：整条调整链都从同一个基准图（基准状态）重算，因此反复调整不会累积画质损失。
    /// </summary>
    public sealed class EditState
    {
        private EditState(BitmapSnapshot snapshot, PixelAdjustments adjustments, double dpiX, double dpiY)
        {
            Snapshot = snapshot;
            Adjustments = adjustments ?? PixelAdjustments.Neutral;
            DpiX = dpiX > 0.5 ? dpiX : 96.0;
            DpiY = dpiY > 0.5 ? dpiY : 96.0;
        }

        /// <summary>压缩位图快照（内存优化的核心）。</summary>
        public BitmapSnapshot Snapshot { get; private set; }

        /// <summary>该状态下累计的基础调整参数。</summary>
        public PixelAdjustments Adjustments { get; private set; }

        public double DpiX { get; private set; }

        public double DpiY { get; private set; }

        /// <summary>占用的内存字节数。</summary>
        public long ByteSize
        {
            get { return Snapshot == null ? 0L : Snapshot.ByteSize; }
        }

        public static EditState Create(PixelBuffer buffer, PixelAdjustments adjustments, double dpiX, double dpiY)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            BitmapSnapshot snapshot = BitmapSnapshot.TryCreate(
                buffer.GetPixels(),
                buffer.Width,
                buffer.Height,
                dpiX,
                dpiY);

            return new EditState(snapshot, adjustments, dpiX, dpiY);
        }

        /// <summary>由已有快照构造（用于共享同一份快照，避免重复占用内存）。</summary>
        public static EditState FromSnapshot(BitmapSnapshot snapshot, PixelAdjustments adjustments, double dpiX, double dpiY)
        {
            return new EditState(snapshot, adjustments, dpiX, dpiY);
        }

        /// <summary>还原为位图（快照为空时返回 null，由调用方降级处理）。</summary>
        public BitmapSource ToBitmap()
        {
            return Snapshot == null ? null : Snapshot.ToBitmap();
        }

        /// <summary>还原为像素缓冲（供滤镜继续处理）。</summary>
        public PixelBuffer ToPixelBuffer()
        {
            return Snapshot == null ? null : Snapshot.ToPixelBuffer();
        }
    }

    /// <summary>
    /// 编辑命令接口（命令模式，需求 P0-3）。
    ///
    /// 约定：命令只保存“状态对象的引用”，不复制像素数据；
    /// 因此同一份快照可被多个命令共享（链式编辑时前一步的 after 就是后一步的 before），
    /// 这是内存优化的关键——20 步历史只需要 21 份快照，而不是 40 份。
    /// </summary>
    public interface IEditCommand
    {
        /// <summary>操作名称，用于界面提示（例如“基础调整”）。</summary>
        string Label { get; }

        /// <summary>操作发生的时间。</summary>
        DateTime Timestamp { get; }

        /// <summary>命令所引用的全部状态（供 HistoryManager 统一统计与裁剪内存）。</summary>
        System.Collections.Generic.IEnumerable<EditState> States { get; }

        /// <summary>执行（重做）。</summary>
        void Redo();

        /// <summary>撤销。</summary>
        void Undo();
    }

    /// <summary>
    /// 基础调整命令：在原图状态与调整结果状态之间切换。
    /// </summary>
    public sealed class PixelAdjustmentCommand : IEditCommand
    {
        private readonly EditState[] _states;
        private readonly Action<EditState> _applyState;

        public PixelAdjustmentCommand(string label, EditState before, EditState after, Action<EditState> applyState)
        {
            if (before == null)
            {
                throw new ArgumentNullException("before");
            }

            if (after == null)
            {
                throw new ArgumentNullException("after");
            }

            Label = string.IsNullOrWhiteSpace(label) ? "基础调整" : label;
            _states = new[] { before, after };
            _applyState = applyState;
            Timestamp = DateTime.Now;
        }

        public string Label { get; private set; }

        public DateTime Timestamp { get; private set; }

        public System.Collections.Generic.IEnumerable<EditState> States
        {
            get { return _states; }
        }

        /// <summary>调整前的状态（撤销目标）。</summary>
        public EditState Before
        {
            get { return _states[0]; }
        }

        /// <summary>调整后的状态（重做目标）。</summary>
        public EditState After
        {
            get { return _states[1]; }
        }

        public void Redo()
        {
            Apply(_states[1]);
        }

        public void Undo()
        {
            Apply(_states[0]);
        }

        private void Apply(EditState state)
        {
            Action<EditState> applier = _applyState;
            if (applier == null)
            {
                throw new InvalidOperationException("命令未绑定状态回调，无法撤销 / 重做。");
            }

            applier(state);
        }
    }

    /// <summary>
    /// 通用位图编辑命令：适用于任何“一次性、不可参数化回放”的编辑
    /// （反色 / 灰度 / 二值化 / 模糊 / 锐化 / 裁剪 / 旋转 / 翻转 / 边框 / 文字）。
    ///
    /// 与 PixelAdjustmentCommand 的区别：
    ///   - 调整命令保存“基准状态 + 最终状态”，重做时直接切到最终状态；
    ///   - 本命令同样保存前后两个状态，但语义上不区分“基准 / 会话”，
    ///     每一次操作都是独立的一步历史（撤销即回到操作前）。
    /// 两者共享同一份快照对象，因此内存占用没有额外开销。
    /// </summary>
    public sealed class BufferEditCommand : IEditCommand
    {
        private readonly EditState[] _states;
        private readonly Action<EditState> _applyState;

        public BufferEditCommand(string label, EditState before, EditState after, Action<EditState> applyState)
        {
            if (before == null)
            {
                throw new ArgumentNullException("before");
            }

            if (after == null)
            {
                throw new ArgumentNullException("after");
            }

            Label = string.IsNullOrWhiteSpace(label) ? "编辑" : label;
            _states = new[] { before, after };
            _applyState = applyState;
            Timestamp = DateTime.Now;
        }

        public string Label { get; private set; }

        public DateTime Timestamp { get; private set; }

        public System.Collections.Generic.IEnumerable<EditState> States
        {
            get { return _states; }
        }

        /// <summary>编辑前的状态（撤销目标）。</summary>
        public EditState Before
        {
            get { return _states[0]; }
        }

        /// <summary>编辑后的状态（重做目标）。</summary>
        public EditState After
        {
            get { return _states[1]; }
        }

        public void Redo()
        {
            Apply(_states[1]);
        }

        public void Undo()
        {
            Apply(_states[0]);
        }

        private void Apply(EditState state)
        {
            Action<EditState> applier = _applyState;
            if (applier == null)
            {
                throw new InvalidOperationException("命令未绑定状态回调，无法撤销 / 重做。");
            }

            applier(state);
        }
    }
}
