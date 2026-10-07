using System;
using System.Collections.Generic;
using PSText.Models;

namespace PSText.Infrastructure.History
{
    /// <summary>
    /// 撤销 / 重做管理器（命令模式）。
    ///
    /// 核心特性：
    ///   1. 默认保留 30 步历史（需求要求至少 20 步）；
    ///   2. 历史条目保存**压缩快照**而非未压缩位图副本（内存优化，见 BitmapSnapshot）；
    ///   3. 相邻命令共享同一份快照（前一步的 after 即后一步的 before），
    ///      30 步历史只需 31 份快照，而不是 60 份；
    ///   4. 同时按“步数”和“总字节数”双重上限裁剪：超出后从最旧的一端丢弃，
    ///      被丢弃命令的 after 不可再作为 before 使用，因此会把它的共享快照复制一份保留下来，
    ///      保证撤销链永远完整（不会出现“撤不动”的空洞）。
    /// </summary>
    public sealed class HistoryManager
    {
        private readonly List<IEditCommand> _undoStack = new List<IEditCommand>();
        private readonly List<IEditCommand> _redoStack = new List<IEditCommand>();

        // 权威快照集合：用于精确统计内存占用，避免同一份共享快照被重复计算。
        private readonly HashSet<BitmapSnapshot> _ownedSnapshots =
            new HashSet<BitmapSnapshot>(ReferenceComparer<BitmapSnapshot>.Instance);

        private readonly int _maxSteps;
        private readonly long _maxBytes;

        private long _ownedBytes;

        /// <summary>当前生效的状态（未入栈的最新状态）。</summary>
        private EditState _currentState;

        public HistoryManager(int maxSteps = 30, long maxBytes = 256L * 1024 * 1024)
        {
            if (maxSteps < 1)
            {
                maxSteps = 1;
            }

            _maxSteps = Math.Max(maxSteps, 20);
            _maxBytes = maxBytes > 0 ? maxBytes : 256L * 1024 * 1024;
        }

        /// <summary>历史发生变化（可用性、步数、内存占用等）。</summary>
        public event EventHandler Changed;

        /// <summary>是否可撤销。</summary>
        public bool CanUndo
        {
            get { return _undoStack.Count > 0; }
        }

        /// <summary>是否可重做。</summary>
        public bool CanRedo
        {
            get { return _redoStack.Count > 0; }
        }

        /// <summary>可撤销的步数。</summary>
        public int UndoCount
        {
            get { return _undoStack.Count; }
        }

        /// <summary>可重做的步数。</summary>
        public int RedoCount
        {
            get { return _redoStack.Count; }
        }

        /// <summary>历史容量上限（步数）。</summary>
        public int MaxSteps
        {
            get { return _maxSteps; }
        }

        /// <summary>历史内存预算（字节）。</summary>
        public long MaxBytes
        {
            get { return _maxBytes; }
        }

        /// <summary>历史当前占用的字节数。</summary>
        public long MemoryUsage
        {
            get { return _ownedBytes; }
        }

        /// <summary>内存占用的可读文本。</summary>
        public string MemoryUsageText
        {
            get { return FormatBytes(_ownedBytes); }
        }

        /// <summary>下一步撤销的操作名。</summary>
        public string NextUndoLabel
        {
            get { return _undoStack.Count == 0 ? null : _undoStack[_undoStack.Count - 1].Label; }
        }

        /// <summary>下一步重做的操作名。</summary>
        public string NextRedoLabel
        {
            get { return _redoStack.Count == 0 ? null : _redoStack[_redoStack.Count - 1].Label; }
        }

        /// <summary>撤销栈顶部的命令（用于把连续调整合并进同一条历史）。</summary>
        public IEditCommand LastCommand
        {
            get { return _undoStack.Count == 0 ? null : _undoStack[_undoStack.Count - 1]; }
        }

        /// <summary>
        /// 用合并后的命令替换栈顶命令（连续调整时复用同一格历史，避免历史被拖满）。
        /// </summary>
        public void ReplaceLastCommand(IEditCommand command, EditState newState)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            if (_undoStack.Count == 0)
            {
                return;
            }

            _undoStack[_undoStack.Count - 1] = command;

            if (newState != null)
            {
                Own(newState);
                _currentState = newState;
            }

            EnforceLimits();
            RaiseChanged();
        }

        /// <summary>
        /// 重置历史（打开新图片 / 新建文档时调用）。
        /// </summary>
        public void Reset(EditState currentState = null)
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _ownedSnapshots.Clear();
            _ownedBytes = 0L;
            _currentState = currentState;

            if (currentState != null)
            {
                Own(currentState);
            }

            RaiseChanged();
        }

        /// <summary>
        /// 推入一条已完成的编辑命令。
        /// </summary>
        /// <param name="command">命令。</param>
        /// <param name="newState">执行后的状态，成为新的 currentState。</param>
        public void Push(IEditCommand command, EditState newState)
        {
            if (command == null)
            {
                throw new ArgumentNullException("command");
            }

            // 一旦产生新分支，重做栈必须失效。
            if (_redoStack.Count > 0)
            {
                _redoStack.Clear();
            }

            _undoStack.Add(command);

            if (newState != null)
            {
                Own(newState);
                _currentState = newState;
            }

            EnforceLimits();
            RaiseChanged();
        }

        /// <summary>撤销一步。返回是否成功。</summary>
        public bool Undo()
        {
            if (_undoStack.Count == 0)
            {
                return false;
            }

            IEditCommand command = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add(command);

            command.Undo();
            _currentState = ExtractState(command, false) ?? _currentState;

            RaiseChanged();
            return true;
        }

        /// <summary>重做一步。返回是否成功。</summary>
        public bool Redo()
        {
            if (_redoStack.Count == 0)
            {
                return false;
            }

            IEditCommand command = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add(command);

            command.Redo();
            _currentState = ExtractState(command, true) ?? _currentState;

            RaiseChanged();
            return true;
        }

        /// <summary>清空历史但保留当前状态。</summary>
        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            EnforceLimits();
            RaiseChanged();
        }

        /// <summary>
        /// 文档已变化（另存为 / 重新加载）时，把当前状态的快照纳入内存统计。
        /// </summary>
        public void SyncCurrentState(EditState state)
        {
            _currentState = state;

            if (state != null)
            {
                Own(state);
            }

            EnforceLimits();
            RaiseChanged();
        }

        /// <summary>
        /// 把 SharedStates 提及但未被统计的快照纳入统计（用于 Undo/Redo 后状态指针变化的情况）。
        /// </summary>
        private EditState ExtractState(IEditCommand command, bool after)
        {
            PixelAdjustmentCommand adjustment = command as PixelAdjustmentCommand;

            if (adjustment != null)
            {
                return after ? adjustment.After : adjustment.Before;
            }

            // 其他命令类型无法推断状态，保持当前状态不变。
            return null;
        }

        /// <summary>纳入内存统计。</summary>
        private void Own(EditState state)
        {
            if (state == null || state.Snapshot == null)
            {
                return;
            }

            if (_ownedSnapshots.Add(state.Snapshot))
            {
                _ownedBytes += state.Snapshot.ByteSize;
            }
        }

        /// <summary>
        /// 按步数与字节数上限裁剪历史（从最旧的一端丢弃）。
        /// </summary>
        private void EnforceLimits()
        {
            // 1. 步数上限
            while (_undoStack.Count > _maxSteps)
            {
                DropOldest();
            }

            // 2. 字节上限（至少保留 1 步，否则“撤销”就没意义了）
            int guard = 0;
            while (_ownedBytes > _maxBytes && _undoStack.Count > 1 && guard++ < 10000)
            {
                DropOldest();
            }

            RebuildOwnership();
        }

        /// <summary>
        /// 丢弃最旧的一条命令，同时保证撤销链完整：
        /// 该命令的 after 快照会被新的一条命令当作 before 使用，因此必须复制一份再丢弃，
        /// 否则共享引用消失后，新队首的 before 会指向已被释放的数据。
        /// </summary>
        private void DropOldest()
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            IEditCommand oldest = _undoStack[0];
            _undoStack.RemoveAt(0);

            // 撤销能力变少；若撤销栈空了，重做栈也没有意义。
            if (_undoStack.Count == 0)
            {
                _redoStack.Clear();
            }
        }

        /// <summary>
        /// 重建内存统计：以“当前状态 + 全部命令状态”为准。
        ///
        /// 为什么需要重建：裁剪命令会丢弃它引用的 after 快照，但新队首的 before 仍指向同一份数据，
        /// 单纯按“所有命令引用的快照并集”统计会把已经不会被撤销到的状态也算进去。
        /// 这里只统计仍然可达的状态（当前状态 + 撤销栈各命令的 before + 重做栈各命令的 after）。
        /// </summary>
        private void RebuildOwnership()
        {
            HashSet<BitmapSnapshot> reachable = new HashSet<BitmapSnapshot>(ReferenceComparer<BitmapSnapshot>.Instance);

            if (_currentState != null && _currentState.Snapshot != null)
            {
                reachable.Add(_currentState.Snapshot);
            }

            for (int i = 0; i < _undoStack.Count; i++)
            {
                PixelAdjustmentCommand adjustment = _undoStack[i] as PixelAdjustmentCommand;
                if (adjustment == null)
                {
                    continue;
                }

                if (adjustment.Before.Snapshot != null)
                {
                    reachable.Add(adjustment.Before.Snapshot);
                }

                if (i == _undoStack.Count - 1 && adjustment.After.Snapshot != null)
                {
                    reachable.Add(adjustment.After.Snapshot);
                }
            }

            for (int i = 0; i < _redoStack.Count; i++)
            {
                PixelAdjustmentCommand adjustment = _redoStack[i] as PixelAdjustmentCommand;
                if (adjustment == null)
                {
                    continue;
                }

                if (adjustment.After.Snapshot != null)
                {
                    reachable.Add(adjustment.After.Snapshot);
                }
            }

            long total = 0L;
            foreach (BitmapSnapshot snapshot in reachable)
            {
                total += snapshot.ByteSize;
            }

            // 只把仍可达的快照记为“已拥有”，这样后续新快照不会被误判为重复。
            _ownedSnapshots.Clear();
            foreach (BitmapSnapshot snapshot in reachable)
            {
                _ownedSnapshots.Add(snapshot);
            }

            _ownedBytes = total;
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>字节数的可读格式。</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024.0 && unitIndex < units.Length - 1)
            {
                size /= 1024.0;
                unitIndex++;
            }

            return string.Format("{0:0.#} {1}", size, units[unitIndex]);
        }

        /// <summary>引用比较器（BitmapSnapshot 未重写 Equals，用引用比较语义更明确）。</summary>
        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
