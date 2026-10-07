using System;
using System.Collections.Generic;

namespace PSText.Models
{
    /// <summary>
    /// 标注编辑命令（新增 / 移动 / 改参数 / 删除）：只保存**对象列表的前后快照**，不存像素。
    ///
    /// 为什么这里可以不碰像素：标注是**叠加层**，画面 = 底图 + 全部对象实时渲染。
    /// 所以"把箭头改成蓝色"这件事只改对象列表，重绘由渲染层自动完成 ——
    /// 这也正是"能选中再改参数"得以成立的原因（像素一旦画死就改不动了）。
    ///
    /// 内存上极便宜：对象只有几个，一次快照是几百字节量级，和整幅快照完全不是一个量级。
    /// </summary>
    public sealed class AnnotationEditCommand : IEditCommand
    {
        private static readonly EditState[] NoStates = new EditState[0];

        private readonly List<AnnotationObject> _before;
        private readonly List<AnnotationObject> _after;
        private readonly Action<List<AnnotationObject>> _apply;

        public AnnotationEditCommand(
            string label,
            IEnumerable<AnnotationObject> before,
            IEnumerable<AnnotationObject> after,
            Action<List<AnnotationObject>> apply)
        {
            if (apply == null)
            {
                throw new ArgumentNullException("apply");
            }

            Label = string.IsNullOrWhiteSpace(label) ? "标注" : label;
            _before = CloneList(before);
            _after = CloneList(after);
            _apply = apply;
            Timestamp = DateTime.Now;
        }

        public string Label { get; private set; }

        public DateTime Timestamp { get; private set; }

        public IEnumerable<EditState> States
        {
            get { return NoStates; }
        }

        public long ByteSize
        {
            get { return EstimateSize(_before) + EstimateSize(_after); }
        }

        public void Redo()
        {
            _apply(CloneList(_after));
        }

        public void Undo()
        {
            _apply(CloneList(_before));
        }

        /// <summary>深拷贝一份列表（命令内部与外部互不影响）。</summary>
        internal static List<AnnotationObject> CloneList(IEnumerable<AnnotationObject> source)
        {
            List<AnnotationObject> copy = new List<AnnotationObject>();

            if (source != null)
            {
                foreach (AnnotationObject item in source)
                {
                    if (item != null)
                    {
                        copy.Add(item.Clone());
                    }
                }
            }

            return copy;
        }

        internal static long EstimateSize(List<AnnotationObject> list)
        {
            if (list == null)
            {
                return 0L;
            }

            long total = 0L;

            foreach (AnnotationObject item in list)
            {
                total += 64L;

                if (!string.IsNullOrEmpty(item.Text))
                {
                    total += item.Text.Length * 2L;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// 标注合并（烘焙）命令：撤销时要**同时还原像素和对象列表**。
    ///
    /// 为什么需要它：合并会把标注画进像素并清空对象列表。
    /// 只还原像素的话，撤销之后标注就"凭空消失"了（列表还是空的）；
    /// 只还原列表的话，标注会在画面上出现两份（像素里一份 + 叠加层一份）。
    /// 因此这两件事必须由同一条命令一起负责。
    /// </summary>
    public sealed class AnnotationFlattenCommand : IEditCommand
    {
        private readonly EditState[] _states;
        private readonly List<AnnotationObject> _beforeObjects;
        private readonly List<AnnotationObject> _afterObjects;
        private readonly Action<EditState, List<AnnotationObject>> _apply;

        public AnnotationFlattenCommand(
            string label,
            EditState before,
            EditState after,
            IEnumerable<AnnotationObject> beforeObjects,
            IEnumerable<AnnotationObject> afterObjects,
            Action<EditState, List<AnnotationObject>> apply)
        {
            if (before == null)
            {
                throw new ArgumentNullException("before");
            }

            if (after == null)
            {
                throw new ArgumentNullException("after");
            }

            if (apply == null)
            {
                throw new ArgumentNullException("apply");
            }

            Label = string.IsNullOrWhiteSpace(label) ? "合并标注" : label;
            _states = new[] { before, after };
            _beforeObjects = AnnotationEditCommand.CloneList(beforeObjects);
            _afterObjects = AnnotationEditCommand.CloneList(afterObjects);
            _apply = apply;
            Timestamp = DateTime.Now;
        }

        public string Label { get; private set; }

        public DateTime Timestamp { get; private set; }

        public IEnumerable<EditState> States
        {
            get { return _states; }
        }

        public long ByteSize
        {
            get
            {
                return AnnotationEditCommand.EstimateSize(_beforeObjects)
                       + AnnotationEditCommand.EstimateSize(_afterObjects);
            }
        }

        public EditState Before
        {
            get { return _states[0]; }
        }

        public EditState After
        {
            get { return _states[1]; }
        }

        public void Redo()
        {
            _apply(_states[1], AnnotationEditCommand.CloneList(_afterObjects));
        }

        public void Undo()
        {
            _apply(_states[0], AnnotationEditCommand.CloneList(_beforeObjects));
        }
    }
}
