using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace PSText.Models
{
    /// <summary>
    /// 区域编辑命令：只保存**改动包围盒内的前后像素**，而不是整幅快照。
    ///
    /// 为什么需要它：仿制图章 / 修补画笔这类工具是"一笔一步"的高频小范围编辑。
    /// 若每一笔都存整幅压缩快照，12MP 图上约 17 MB/步，涂几十笔就把历史预算吃满；
    /// 而一笔 200×200 的笔迹只有 160 KB，相差两个数量级。
    ///
    /// 适用前提：命令执行期间**画布尺寸不变**（包围盒坐标才有意义）。
    /// 裁剪 / 缩放 / 旋转这类改变尺寸的操作仍走 BufferEditCommand（整幅快照）。
    /// </summary>
    public sealed class RegionEditCommand : IEditCommand
    {
        private static readonly EditState[] NoStates = new EditState[0];

        private readonly int _imageWidth;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _before;
        private readonly byte[] _after;
        private readonly Action<int, int, int, int, byte[]> _applyRegion;

        /// <summary>
        /// 构造区域编辑命令。
        /// </summary>
        /// <param name="label">操作名（界面提示用）。</param>
        /// <param name="imageWidth">画布宽度（用于把包围盒换算成缓冲下标）。</param>
        /// <param name="x">包围盒左上角 X。</param>
        /// <param name="y">包围盒左上角 Y。</param>
        /// <param name="width">包围盒宽度。</param>
        /// <param name="height">包围盒高度。</param>
        /// <param name="before">改动前的区域像素（Bgra32，长度 = width × height × 4）。</param>
        /// <param name="after">改动后的区域像素。</param>
        /// <param name="applyRegion">把区域写回画布的回调（撤销与重做共用）。</param>
        public RegionEditCommand(
            string label,
            int imageWidth,
            int x,
            int y,
            int width,
            int height,
            byte[] before,
            byte[] after,
            Action<int, int, int, int, byte[]> applyRegion)
        {
            if (before == null)
            {
                throw new ArgumentNullException("before");
            }

            if (after == null)
            {
                throw new ArgumentNullException("after");
            }

            if (applyRegion == null)
            {
                throw new ArgumentNullException("applyRegion");
            }

            if (imageWidth <= 0 || width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "区域尺寸必须为正数。");
            }

            int required = width * height * 4;

            if (before.Length < required || after.Length < required)
            {
                throw new ArgumentException(
                    string.Format("区域像素长度不足：需要 {0} 字节。", required));
            }

            Label = string.IsNullOrWhiteSpace(label) ? "修补" : label;
            _imageWidth = imageWidth;
            _x = x;
            _y = y;
            _width = width;
            _height = height;
            _before = before;
            _after = after;
            _applyRegion = applyRegion;
            Timestamp = DateTime.Now;
        }

        public string Label { get; private set; }

        public DateTime Timestamp { get; private set; }

        /// <summary>本命令不引用任何整幅快照（这正是它的意义所在）。</summary>
        public IEnumerable<EditState> States
        {
            get { return NoStates; }
        }

        /// <summary>本命令自己占用的字节数（前后两份区域像素）。</summary>
        public long ByteSize
        {
            get { return (long)_before.LongLength + _after.LongLength; }
        }

        /// <summary>命令创建时的画布宽度（诊断用）。</summary>
        public int ImageWidth
        {
            get { return _imageWidth; }
        }

        /// <summary>改动包围盒（供诊断 / 自检读取）。</summary>
        public int RegionX
        {
            get { return _x; }
        }

        public int RegionY
        {
            get { return _y; }
        }

        public int RegionWidth
        {
            get { return _width; }
        }

        public int RegionHeight
        {
            get { return _height; }
        }

        public void Redo()
        {
            _applyRegion(_x, _y, _width, _height, _after);
        }

        public void Undo()
        {
            _applyRegion(_x, _y, _width, _height, _before);
        }
    }
}
