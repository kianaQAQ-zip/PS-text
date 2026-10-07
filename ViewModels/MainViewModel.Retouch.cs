using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using PSText.Infrastructure;
using PSText.Models;
using PSText.Services.Filters;
using Rect = System.Windows.Rect;

namespace PSText.ViewModels
{
    /// <summary>修补模式下可选的工具。</summary>
    public enum RetouchTool
    {
        /// <summary>智能填充：框选后按周围像素重建（纯色 / 渐变背景效果最好）。</summary>
        Inpaint = 0,

        /// <summary>仿制图章：手动指定源纹理后涂抹（纹理背景的兜底手段）。</summary>
        CloneStamp = 1
    }

    /// <summary>
    /// MainViewModel 的「修补 / 消除」部分（partial，对应 M2 的第一块）。
    ///
    /// 定位：办公图片处理里"把不要的东西从画面上拿走"这件事 —— 水印、印章、日期戳、
    /// 编号标签、污渍。
    ///
    /// 两个工具，分工明确：
    ///   - **智能填充**（本文件）：框选后按周围像素重建，纯色 / 渐变背景上效果最好；
    ///   - **仿制图章**（见 MainViewModel.Stamp.cs）：手动指定源纹理后涂抹，
    ///     用于纹理背景上调和填充会留平滑斑块的情况。
    ///
    /// 交互模型（智能填充）：
    ///   1. 进入修补模式 → 在画布上**拖拽出矩形**框住要消掉的东西；
    ///   2. 可以连续框选多处（标记是叠加的），标记以半透明红色显示；
    ///   3. 点「智能填充」一次性处理 —— **一次操作只产生一步历史**，可整体撤销。
    ///
    /// 为什么用矩形而不是笔刷涂抹：办公场景的水印 / 印章是成片出现的，
    /// 而调和填充只在"标记范围内"重建像素，**多框一点是无害的**
    /// （周围同色的地方会被填成同样的颜色，本来也不需要保护）。
    /// 矩形因此比精修轮廓更省事，也不容易涂坏。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>矩形选区最小边长（像素）：太小多半是误点。</summary>
        private const double MinRetouchRectSize = 2.0;

        private readonly ObservableCollection<Rect> _retouchRects = new ObservableCollection<Rect>();

        private RetouchTool _retouchTool = RetouchTool.Inpaint;
        private bool _isRetouchMode;
        private byte[] _retouchMask;
        private long _retouchMaskPixels;
        private Rect _retouchBounds;
        private int _retouchPixelWidth;
        private int _retouchPixelHeight;

        private bool _isRetouchDragging;
        private double _retouchStartX;
        private double _retouchStartY;
        private Rect _pendingRect;
        private bool _hasPendingRect;

        #region 命令

        /// <summary>进入修补模式。</summary>
        public ICommand BeginRetouchCommand { get; private set; }

        /// <summary>退出修补模式（保留标记）。</summary>
        public ICommand ExitRetouchCommand { get; private set; }

        /// <summary>对当前标记执行智能填充。</summary>
        public ICommand ApplyInpaintCommand { get; private set; }

        /// <summary>清除全部标记。</summary>
        public ICommand ClearRetouchMaskCommand { get; private set; }

        #endregion

        /// <summary>装配修补相关命令（在构造函数中调用一次）。</summary>
        private void InitializeRetouchCommands()
        {
            BeginRetouchCommand = new RelayCommand(BeginRetouch, () => HasDocument && !IsBusy && !IsRetouchMode);
            ExitRetouchCommand = new RelayCommand(ExitRetouch, () => IsRetouchMode);
            ApplyInpaintCommand = new RelayCommand(RunApplyInpaint, () => CanApplyInpaint);
            ClearRetouchMaskCommand = new RelayCommand(ClearRetouchMask, () => HasRetouchMask && !IsBusy);

            // 换了图片（或画布尺寸变了）就丢弃标记：旧坐标对新画面没有意义。
            // 只在"像素尺寸真的变化"时丢弃，这样调整亮度之类的编辑不会把标记清掉。
            PropertyChanged += OnRetouchPropertyChanged;
        }

        private void OnRetouchPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Document" && e.PropertyName != "HasDocument")
            {
                return;
            }

            if (_document == null)
            {
                _retouchPixelWidth = 0;
                _retouchPixelHeight = 0;
                IsRetouchMode = false;

                if (HasRetouchMask)
                {
                    DiscardRetouchMask();
                }

                return;
            }

            if (_document.PixelWidth == _retouchPixelWidth && _document.PixelHeight == _retouchPixelHeight)
            {
                return;
            }

            _retouchPixelWidth = _document.PixelWidth;
            _retouchPixelHeight = _document.PixelHeight;

            if (HasRetouchMask)
            {
                DiscardRetouchMask();
            }
        }

        #region 状态

        /// <summary>是否处于修补模式（画布上会出现标记层）。</summary>
        public bool IsRetouchMode
        {
            get { return _isRetouchMode; }
            private set
            {
                if (SetProperty(ref _isRetouchMode, value, "IsRetouchMode"))
                {
                    OnPropertyChanged("CanApplyInpaint");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>当前修补工具。</summary>
        public RetouchTool RetouchTool
        {
            get { return _retouchTool; }
            private set
            {
                if (value == _retouchTool)
                {
                    return;
                }

                // 切换工具时丢掉进行中的临时状态，避免"框选到一半切到图章"这类错位
                CancelRetouchGesture();

                _retouchTool = value;

                OnPropertyChanged("RetouchTool");
                OnPropertyChanged("RetouchToolIndex");
                OnPropertyChanged("IsCloneStampTool");
                OnPropertyChanged("IsBrushCursorVisible");
                OnPropertyChanged("CanApplyInpaint");
                RelayCommand.RaiseCanExecuteChanged();

                StatusMessage = value == RetouchTool.CloneStamp
                    ? "仿制图章：按住 Alt 在画布上点一下取源，再拖拽涂抹"
                    : "智能填充：在画布上拖拽矩形框住要去掉的内容";
            }
        }

        /// <summary>供下拉框绑定的工具索引（0 智能填充 / 1 仿制图章）。</summary>
        public int RetouchToolIndex
        {
            get { return (int)_retouchTool; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 1 ? 1 : value);
                RetouchTool = (RetouchTool)clamped;
            }
        }

        /// <summary>当前是否为仿制图章工具。</summary>
        public bool IsCloneStampTool
        {
            get { return _retouchTool == RetouchTool.CloneStamp; }
        }

        /// <summary>已提交的标记矩形（供界面叠加显示）。</summary>
        public ObservableCollection<Rect> RetouchRects
        {
            get { return _retouchRects; }
        }

        /// <summary>正在拖拽中的矩形（供界面实时预览）。</summary>
        public Rect PendingRect
        {
            get { return _pendingRect; }
            private set
            {
                _pendingRect = value;
                OnPropertyChanged("PendingRect");
            }
        }

        /// <summary>是否正在拖拽标记矩形。</summary>
        public bool HasPendingRect
        {
            get { return _hasPendingRect; }
            private set
            {
                if (SetProperty(ref _hasPendingRect, value, "HasPendingRect"))
                {
                    OnPropertyChanged("PendingRect");
                }
            }
        }

        /// <summary>是否有标记。</summary>
        public bool HasRetouchMask
        {
            get { return _retouchMaskPixels > 0; }
        }

        /// <summary>标记的像素总数（多个矩形重叠处只计一次）。</summary>
        public long RetouchMaskPixels
        {
            get { return _retouchMaskPixels; }
        }

        /// <summary>标记的处数（矩形个数）。</summary>
        public int RetouchMarkCount
        {
            get { return _retouchRects.Count; }
        }

        /// <summary>标记说明文本（供面板显示）。</summary>
        public string RetouchInfoText
        {
            get
            {
                if (_document == null)
                {
                    return "未打开图片";
                }

                if (_retouchMaskPixels <= 0)
                {
                    return "尚未标记任何区域。在画布上拖拽出矩形框住水印 / 印章，可连续框选多处。";
                }

                int boundsWidth = (int)Math.Ceiling(_retouchBounds.Width);
                int boundsHeight = (int)Math.Ceiling(_retouchBounds.Height);
                bool solvable = InpaintFilter.IsSolvable(boundsWidth, boundsHeight);

                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    "已标记 {0} 处，共 {1} 像素；求解范围 {2} × {3} px。",
                    _retouchRects.Count,
                    _retouchMaskPixels,
                    boundsWidth,
                    boundsHeight);

                if (!solvable)
                {
                    message += string.Format(
                        CultureInfo.CurrentCulture,
                        " 超出单次处理上限（{0} 万像素），请分批标记。",
                        InpaintFilter.MaxSolvePixels / 10000L);
                }

                return message;
            }
        }

        /// <summary>是否满足执行智能填充的条件。</summary>
        public bool CanApplyInpaint
        {
            get
            {
                // 标记只在智能填充工具下才能产生，因此换到仿制图章后这个按钮就不该可用
                if (_retouchTool != RetouchTool.Inpaint)
                {
                    return false;
                }

                if (!HasDocument || IsBusy || !IsRetouchMode || _retouchMaskPixels <= 0)
                {
                    return false;
                }

                int boundsWidth = (int)Math.Ceiling(_retouchBounds.Width);
                int boundsHeight = (int)Math.Ceiling(_retouchBounds.Height);

                return InpaintFilter.IsSolvable(boundsWidth, boundsHeight);
            }
        }

        #endregion

        #region 模式切换

        private void BeginRetouch()
        {
            if (_document == null)
            {
                return;
            }

            // 与裁剪互斥：两个模式都在画布上抓鼠标，同时开着会互相抢事件
            if (IsCropping)
            {
                CancelCrop();
            }

            CancelRetouchGesture();

            IsRetouchMode = true;
            StatusMessage = _retouchTool == RetouchTool.CloneStamp
                ? "仿制图章：按住 Alt 在画布上点一下取源，再拖拽涂抹"
                : "智能填充：在画布上拖拽框住要去掉的水印 / 印章，然后点“智能填充”";
        }

        private void ExitRetouch()
        {
            IsRetouchMode = false;
            CancelRetouchGesture();
            HideRetouchCursor();
            StatusMessage = "已退出修补模式（标记已保留，可再次进入继续处理）";
        }

        #endregion

        #region 画布交互（由 View 回传图像像素坐标）

        /// <summary>
        /// 开始一次画布手势。由 View 转发坐标与修饰键，具体动作交给这里按当前工具决定 ——
        /// 这样 View 只是一条"哑"的转发通道，不需要判断当前是哪个工具。
        /// </summary>
        /// <param name="setSourcePoint">是否按住了 Alt（仿制图章下表示"取源"）。</param>
        public void BeginRetouchGesture(double imageX, double imageY, bool setSourcePoint)
        {
            if (!_isRetouchMode || _document == null)
            {
                return;
            }

            if (_retouchTool == RetouchTool.CloneStamp)
            {
                if (setSourcePoint)
                {
                    SetCloneStampSource(imageX, imageY);
                    return;
                }

                BeginStampStroke(imageX, imageY);
                return;
            }

            BeginRetouchSelect(imageX, imageY);
        }

        /// <summary>更新一次画布手势。</summary>
        public void UpdateRetouchGesture(double imageX, double imageY)
        {
            if (_retouchTool == RetouchTool.CloneStamp)
            {
                UpdateStampStroke(imageX, imageY);
                return;
            }

            UpdateRetouchSelect(imageX, imageY);
        }

        /// <summary>结束一次画布手势。</summary>
        public void EndRetouchGesture()
        {
            if (_retouchTool == RetouchTool.CloneStamp)
            {
                EndStampStroke();
                return;
            }

            EndRetouchSelect();
        }

        /// <summary>丢弃进行中的框选 / 涂抹（切换工具、退出模式时调用）。</summary>
        private void CancelRetouchGesture()
        {
            _isRetouchDragging = false;
            HasPendingRect = false;
            PendingRect = new Rect(0.0, 0.0, 0.0, 0.0);

            if (_strokeActive)
            {
                _strokeActive = false;
                _strokePoints.Clear();
                _brushTrailPoints = new System.Windows.Media.PointCollection();
                RaiseTrailChanged();
            }
        }

        /// <summary>开始拖拽标记矩形。</summary>
        public void BeginRetouchSelect(double imageX, double imageY)
        {
            if (!_isRetouchMode || _document == null)
            {
                return;
            }

            _isRetouchDragging = true;
            _retouchStartX = ClampToImageX(imageX);
            _retouchStartY = ClampToImageY(imageY);
            PendingRect = new Rect(_retouchStartX, _retouchStartY, 0.0, 0.0);
            HasPendingRect = true;
        }

        /// <summary>拖拽中：更新待提交矩形。</summary>
        public void UpdateRetouchSelect(double imageX, double imageY)
        {
            if (!_isRetouchDragging || _document == null)
            {
                return;
            }

            double currentX = ClampToImageX(imageX);
            double currentY = ClampToImageY(imageY);

            double left = Math.Min(_retouchStartX, currentX);
            double top = Math.Min(_retouchStartY, currentY);

            PendingRect = new Rect(
                left,
                top,
                Math.Abs(currentX - _retouchStartX),
                Math.Abs(currentY - _retouchStartY));
        }

        /// <summary>结束拖拽：把矩形并入标记（太小的矩形忽略）。</summary>
        public void EndRetouchSelect()
        {
            if (!_isRetouchDragging)
            {
                return;
            }

            _isRetouchDragging = false;

            Rect rect = _pendingRect;
            HasPendingRect = false;
            PendingRect = new Rect(0.0, 0.0, 0.0, 0.0);

            if (rect.Width < MinRetouchRectSize || rect.Height < MinRetouchRectSize)
            {
                return;
            }

            AddRetouchRect(rect);
        }

        private double ClampToImageX(double imageX)
        {
            if (double.IsNaN(imageX))
            {
                return 0.0;
            }

            double width = _document == null ? 0.0 : _document.PixelWidth;
            return imageX < 0.0 ? 0.0 : (imageX > width ? width : imageX);
        }

        private double ClampToImageY(double imageY)
        {
            if (double.IsNaN(imageY))
            {
                return 0.0;
            }

            double height = _document == null ? 0.0 : _document.PixelHeight;
            return imageY < 0.0 ? 0.0 : (imageY > height ? height : imageY);
        }

        #endregion

        #region 标记（掩膜）

        /// <summary>加入一个标记矩形。</summary>
        public void AddRetouchRect(Rect rect)
        {
            if (_document == null)
            {
                return;
            }

            int width = _document.PixelWidth;
            int height = _document.PixelHeight;

            int left = Clamp((int)Math.Floor(rect.Left), 0, width - 1);
            int top = Clamp((int)Math.Floor(rect.Top), 0, height - 1);
            int right = Clamp((int)Math.Ceiling(rect.Right), left + 1, width);
            int bottom = Clamp((int)Math.Ceiling(rect.Bottom), top + 1, height);

            EnsureRetouchMask(width, height);

            // 同样标记"本次真正新增"的像素数：重叠部分不重复计数，
            // 因为要重建的像素数是并集大小，而不是各矩形面积之和。
            long added = 0;

            for (int y = top; y < bottom; y++)
            {
                int rowOffset = y * width;

                for (int x = left; x < right; x++)
                {
                    int index = rowOffset + x;

                    if (_retouchMask[index] == 0)
                    {
                        _retouchMask[index] = 1;
                        added++;
                    }
                }
            }

            Rect normalized = new Rect(left, top, right - left, bottom - top);
            _retouchRects.Add(normalized);
            _retouchMaskPixels += added;
            _retouchBounds = _retouchBounds.IsEmpty ? normalized : Rect.Union(_retouchBounds, normalized);

            NotifyRetouchChanged();

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                "已标记第 {0} 处（{1} × {2} px），可继续框选或直接点“智能填充”",
                _retouchRects.Count,
                right - left,
                bottom - top);
        }

        /// <summary>清除全部标记（不改动图像）。</summary>
        private void ClearRetouchMask()
        {
            DiscardRetouchMask();
            StatusMessage = "已清除标记";
        }

        private void DiscardRetouchMask()
        {
            _retouchRects.Clear();
            _retouchMask = null;
            _retouchMaskPixels = 0;
            _retouchBounds = default(Rect);

            HasPendingRect = false;
            PendingRect = new Rect(0.0, 0.0, 0.0, 0.0);
            _isRetouchDragging = false;

            NotifyRetouchChanged();
        }

        private void EnsureRetouchMask(int width, int height)
        {
            int required = width * height;

            if (_retouchMask == null || _retouchMask.Length != required)
            {
                _retouchMask = new byte[required];
            }
        }

        private void NotifyRetouchChanged()
        {
            OnPropertyChanged("HasRetouchMask");
            OnPropertyChanged("RetouchMarkCount");
            OnPropertyChanged("RetouchMaskPixels");
            OnPropertyChanged("RetouchInfoText");
            OnPropertyChanged("CanApplyInpaint");
            RelayCommand.RaiseCanExecuteChanged();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        #endregion

        #region 执行

        private void RunApplyInpaint()
        {
            if (!CanApplyInpaint || _retouchMask == null)
            {
                return;
            }

            // 拷一份掩膜交给后台：填充期间用户可能继续框选，不能共享同一个数组
            byte[] mask = new byte[_retouchMask.Length];
            Buffer.BlockCopy(_retouchMask, 0, mask, 0, mask.Length);

            ImageDocument before = _document;
            _pendingOperation = ApplyInpaintInternalAsync(mask, before);
        }

        private async System.Threading.Tasks.Task ApplyInpaintInternalAsync(byte[] mask, ImageDocument before)
        {
            // ApplyOneShotAsync 内部已 try/catch 全包并会弹窗，异常不会逃逸。
            await ApplyOneShotAsync("智能填充", buffer => InpaintFilter.InpaintAsync(buffer, mask))
                .ConfigureAwait(true);

            // 只有真的提交成功（Document 被替换）才丢弃标记 —— 失败时保留标记，用户不必重画。
            if (!ReferenceEquals(_document, before))
            {
                DiscardRetouchMask();
            }
        }

        #endregion
    }
}
