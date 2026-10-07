using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services.Filters;
using Rect = System.Windows.Rect;

namespace PSText.ViewModels
{
    /// <summary>
    /// 叠加层里的一个标注（把 AnnotationObject 翻译成可直接绑定的画刷与几何）。
    ///
    /// 拖动过程中只刷新被改动的**那一个**对象，而不是重建整个集合 ——
    /// 每个对象都要构造 Geometry / FormattedText，逐帧全量重建会造成明显的 GC 抖动。
    /// </summary>
    public sealed class AnnotationItemViewModel : ObservableObject
    {
        private AnnotationObject _source;
        private AnnotationVisual _visual;
        private bool _isSelected;

        public AnnotationItemViewModel(AnnotationObject source)
        {
            _source = source;
            _visual = AnnotationVisualBuilder.Build(source);
        }

        public AnnotationObject Source
        {
            get { return _source; }
        }

        /// <summary>用新的对象内容刷新（拖动 / 改参数时调用）。</summary>
        public void Update(AnnotationObject source)
        {
            if (source == null)
            {
                return;
            }

            _source = source;
            _visual = AnnotationVisualBuilder.Build(source);

            OnPropertyChanged("Geometry");
            OnPropertyChanged("FillBrush");
            OnPropertyChanged("StrokeBrush");
            OnPropertyChanged("StrokeThickness");
            OnPropertyChanged("LabelGeometry");
            OnPropertyChanged("LabelBrush");
        }

        public Geometry Geometry
        {
            get { return _visual.Geometry; }
        }

        public Brush FillBrush
        {
            get { return _visual.Fill; }
        }

        public Brush StrokeBrush
        {
            get { return _visual.Stroke; }
        }

        public double StrokeThickness
        {
            get { return _visual.StrokeThickness; }
        }

        public Geometry LabelGeometry
        {
            get { return _visual.LabelGeometry; }
        }

        public Brush LabelBrush
        {
            get { return _visual.LabelBrush; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetProperty(ref _isSelected, value, "IsSelected"); }
        }

        /// <summary>选中框的位置（比几何略大，避免贴着对象边缘看不出来）。</summary>
        public double SelectionLeft
        {
            get { return _source.VisualBounds.Left; }
        }

        public double SelectionTop
        {
            get { return _source.VisualBounds.Top; }
        }

        public double SelectionWidth
        {
            get { return _source.VisualBounds.Width; }
        }

        public double SelectionHeight
        {
            get { return _source.VisualBounds.Height; }
        }

        /// <summary>选中框的派生属性一起刷新。</summary>
        public void RefreshSelectionBounds()
        {
            OnPropertyChanged("SelectionLeft");
            OnPropertyChanged("SelectionTop");
            OnPropertyChanged("SelectionWidth");
            OnPropertyChanged("SelectionHeight");
        }
    }

    /// <summary>
    /// MainViewModel 的「标注」部分（partial，M2b-2）。
    ///
    /// 核心是**非破坏性**：标注以对象形式存在于叠加层，画面 = 底图 + 全部对象实时渲染，
    /// 因此可以随时选中某个标注改颜色 / 挪位置 / 删除，而不需要"回退像素"。
    ///
    /// 两条已拍板的约定：
    ///   1. **破坏性操作（滤镜 / 裁剪 / 缩放 / 旋转 / 打印）之前自动合并**。
    ///      合并会把标注烘进像素并清空对象列表 —— 简单、行为可解释；
    ///      不这么做的话，裁剪之后所有标注坐标就全错位了。
    ///   2. **历史里存对象列表快照**，不存像素。对象只有几个，快照是几百字节量级。
    ///      但"合并"这一步必须同时还原像素与列表，否则撤销后标注会凭空消失或出现两份。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>点击创建时，需要拖拽的标注类型的默认尺寸。</summary>
        private const double DefaultAnnotationSize = 90.0;

        /// <summary>小于该拖动距离视为"点击"而不是"拖拽"。</summary>
        private const double ClickThreshold = 4.0;

        private readonly List<AnnotationObject> _annotations = new List<AnnotationObject>();
        private readonly ObservableCollection<AnnotationItemViewModel> _annotationItems =
            new ObservableCollection<AnnotationItemViewModel>();

        private bool _isAnnotationMode;
        private AnnotationKind _annotationTool = AnnotationKind.Arrow;
        private Color _annotationColor = Color.FromRgb(0xE2, 0x4B, 0x4A);
        private double _annotationStrokeWidth = 4.0;
        private double _annotationFontSize = 28.0;
        private string _annotationText = "标注文字";

        private int _selectedAnnotationIndex = -1;
        private bool _isAnnotationDragging;
        private bool _isMovingAnnotation;
        private double _annotationGestureStartX;
        private double _annotationGestureStartY;
        private AnnotationObject _pendingAnnotation;
        private AnnotationItemViewModel _pendingItem;
        private AnnotationObject _movingOriginal;
        private List<AnnotationObject> _gestureSnapshot;

        #region 命令

        public ICommand BeginAnnotationCommand { get; private set; }

        public ICommand ExitAnnotationCommand { get; private set; }

        public ICommand DeleteSelectedAnnotationCommand { get; private set; }

        public ICommand ClearAnnotationsCommand { get; private set; }

        /// <summary>把标注合并进画面（非破坏性 → 破坏性）。</summary>
        public ICommand FlattenAnnotationsCommand { get; private set; }

        #endregion

        private void InitializeAnnotationCommands()
        {
            BeginAnnotationCommand = new RelayCommand(BeginAnnotation, () => HasDocument && !IsBusy && !IsAnnotationMode);
            ExitAnnotationCommand = new RelayCommand(ExitAnnotation, () => IsAnnotationMode);
            DeleteSelectedAnnotationCommand = new RelayCommand(DeleteSelectedAnnotation, () => HasSelectedAnnotation && !IsBusy);
            ClearAnnotationsCommand = new RelayCommand(() => CommitAnnotationChange("清空标注", new List<AnnotationObject>()), () => AnnotationCount > 0 && !IsBusy);
            FlattenAnnotationsCommand = new RelayCommand(
                () => FlattenAnnotations("合并标注"),
                () => AnnotationCount > 0 && !IsBusy);
        }

        #region 状态

        /// <summary>叠加层要渲染的标注集合。</summary>
        public ObservableCollection<AnnotationItemViewModel> Annotations
        {
            get { return _annotationItems; }
        }

        /// <summary>是否处于标注模式。</summary>
        public bool IsAnnotationMode
        {
            get { return _isAnnotationMode; }
            private set
            {
                if (SetProperty(ref _isAnnotationMode, value, "IsAnnotationMode"))
                {
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>当前标注类型。</summary>
        public AnnotationKind AnnotationTool
        {
            get { return _annotationTool; }
            private set
            {
                if (value == _annotationTool)
                {
                    return;
                }

                CancelAnnotationGesture();
                _annotationTool = value;

                OnPropertyChanged("AnnotationTool");
                OnPropertyChanged("AnnotationToolIndex");

                StatusMessage = "标注工具：" + GetAnnotationToolName(value);
            }
        }

        /// <summary>供下拉框绑定的类型索引。</summary>
        public int AnnotationToolIndex
        {
            get { return (int)_annotationTool; }
            set
            {
                int clamped = value < 0 ? 0 : (value > 5 ? 5 : value);
                AnnotationTool = (AnnotationKind)clamped;
            }
        }

        /// <summary>标注主色。</summary>
        public Color AnnotationColor
        {
            get { return _annotationColor; }
            set
            {
                if (SetProperty(ref _annotationColor, value, "AnnotationColor"))
                {
                    OnPropertyChanged("AnnotationColorBrush");
                    ApplyAnnotationParametersToSelection();
                }
            }
        }

        /// <summary>主色的画刷（供色块显示 / code-behind 点击设置）。</summary>
        public Brush AnnotationColorBrush
        {
            get { return new SolidColorBrush(_annotationColor); }
        }

        public double AnnotationStrokeWidth
        {
            get { return _annotationStrokeWidth; }
            set
            {
                double clamped = double.IsNaN(value) ? 2.0 : value;

                if (clamped < 1.0)
                {
                    clamped = 1.0;
                }
                else if (clamped > 40.0)
                {
                    clamped = 40.0;
                }

                if (SetProperty(ref _annotationStrokeWidth, clamped, "AnnotationStrokeWidth"))
                {
                    ApplyAnnotationParametersToSelection();
                }
            }
        }

        public double AnnotationFontSize
        {
            get { return _annotationFontSize; }
            set
            {
                double clamped = double.IsNaN(value) ? 14.0 : value;

                if (clamped < 8.0)
                {
                    clamped = 8.0;
                }
                else if (clamped > 200.0)
                {
                    clamped = 200.0;
                }

                if (SetProperty(ref _annotationFontSize, clamped, "AnnotationFontSize"))
                {
                    ApplyAnnotationParametersToSelection();
                }
            }
        }

        /// <summary>文字标注的内容。</summary>
        public string AnnotationText
        {
            get { return _annotationText; }
            set { SetProperty(ref _annotationText, value, "AnnotationText"); }
        }

        /// <summary>标注数量。</summary>
        public int AnnotationCount
        {
            get { return _annotations.Count; }
        }

        /// <summary>当前选中的下标（-1 表示未选中）。</summary>
        public int SelectedAnnotationIndex
        {
            get { return _selectedAnnotationIndex; }
            private set
            {
                if (_selectedAnnotationIndex == value)
                {
                    return;
                }

                int previous = _selectedAnnotationIndex;
                _selectedAnnotationIndex = value;

                if (previous >= 0 && previous < _annotationItems.Count)
                {
                    _annotationItems[previous].IsSelected = false;
                }

                if (value >= 0 && value < _annotationItems.Count)
                {
                    _annotationItems[value].IsSelected = true;
                }

                OnPropertyChanged("SelectedAnnotationIndex");
                OnPropertyChanged("HasSelectedAnnotation");
                OnPropertyChanged("SelectedAnnotationInfoText");
                OnPropertyChanged("AnnotationSelectionLeft");
                OnPropertyChanged("AnnotationSelectionTop");
                OnPropertyChanged("AnnotationSelectionWidth");
                OnPropertyChanged("AnnotationSelectionHeight");
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        public bool HasSelectedAnnotation
        {
            get { return _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < _annotations.Count; }
        }

        /// <summary>选中框（供叠加层画虚线框）。</summary>
        public double AnnotationSelectionLeft
        {
            get { return HasSelectedAnnotation ? _annotations[_selectedAnnotationIndex].VisualBounds.Left : 0.0; }
        }

        public double AnnotationSelectionTop
        {
            get { return HasSelectedAnnotation ? _annotations[_selectedAnnotationIndex].VisualBounds.Top : 0.0; }
        }

        public double AnnotationSelectionWidth
        {
            get { return HasSelectedAnnotation ? _annotations[_selectedAnnotationIndex].VisualBounds.Width : 0.0; }
        }

        public double AnnotationSelectionHeight
        {
            get { return HasSelectedAnnotation ? _annotations[_selectedAnnotationIndex].VisualBounds.Height : 0.0; }
        }

        public string SelectedAnnotationInfoText
        {
            get
            {
                if (!HasSelectedAnnotation)
                {
                    return "未选中任何标注。点击画布上的标注即可选中，选中后可改颜色 / 线宽 / 字号，或拖动挪位。";
                }

                AnnotationObject item = _annotations[_selectedAnnotationIndex];

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "已选中第 {0} 个标注（{1}）",
                    _selectedAnnotationIndex + 1,
                    GetAnnotationToolName(item.Kind));
            }
        }

        #endregion

        #region 模式

        private void BeginAnnotation()
        {
            if (_document == null)
            {
                return;
            }

            if (IsCropping)
            {
                CancelCrop();
            }

            if (IsRetouchMode)
            {
                ExitRetouch();
            }

            CancelAnnotationGesture();

            IsAnnotationMode = true;
            StatusMessage = "标注模式：在画布上拖拽绘制" + GetAnnotationToolName(_annotationTool) + "；点击已画的标注可选中并改参数";
        }

        private void ExitAnnotation()
        {
            IsAnnotationMode = false;
            CancelAnnotationGesture();
            SelectedAnnotationIndex = -1;
            StatusMessage = "已退出标注模式（标注已保留，再次进入可继续编辑）";
        }

        #endregion

        #region 画布交互

        /// <summary>开始一次标注手势：命中已有标注则移动它，否则开始绘制新标注。</summary>
        public void BeginAnnotationGesture(double imageX, double imageY)
        {
            if (!_isAnnotationMode || _document == null)
            {
                return;
            }

            _gestureSnapshot = AnnotationEditCommand.CloneList(_annotations);
            _isAnnotationDragging = true;
            _annotationGestureStartX = ClampToImageX(imageX);
            _annotationGestureStartY = ClampToImageY(imageY);

            int hit = HitTestAnnotation(imageX, imageY);

            if (hit >= 0)
            {
                // 选中并开始拖动
                SelectedAnnotationIndex = hit;
                _isMovingAnnotation = true;
                _movingOriginal = _annotations[hit].Clone();
                return;
            }

            SelectedAnnotationIndex = -1;
            _isMovingAnnotation = false;

            // 新建：先放一个待提交对象，拖动过程中实时更新
            _pendingAnnotation = new AnnotationObject
            {
                Kind = _annotationTool,
                X1 = _annotationGestureStartX,
                Y1 = _annotationGestureStartY,
                X2 = _annotationGestureStartX,
                Y2 = _annotationGestureStartY,
                Color = _annotationColor,
                StrokeWidth = _annotationStrokeWidth,
                FontSize = _annotationFontSize,
                Text = _annotationTool == AnnotationKind.Text
                    ? _annotationText
                    : (string)null
            };

            if (_annotationTool == AnnotationKind.NumberBadge)
            {
                _pendingAnnotation.Text = NextBadgeNumber().ToString(CultureInfo.InvariantCulture);
            }

            _pendingItem = new AnnotationItemViewModel(_pendingAnnotation);
            _annotationItems.Add(_pendingItem);
        }

        /// <summary>拖拽中：更新待绘制对象的尺寸 / 位置。</summary>
        public void UpdateAnnotationGesture(double imageX, double imageY)
        {
            if (!_isAnnotationDragging || _document == null)
            {
                return;
            }

            double x = ClampToImageX(imageX);
            double y = ClampToImageY(imageY);

            if (_isMovingAnnotation)
            {
                if (!HasSelectedAnnotation || _movingOriginal == null)
                {
                    return;
                }

                double dx = x - _annotationGestureStartX;
                double dy = y - _annotationGestureStartY;

                AnnotationObject moved = _movingOriginal.Clone();
                moved.X1 = _movingOriginal.X1 + dx;
                moved.Y1 = _movingOriginal.Y1 + dy;
                moved.X2 = _movingOriginal.X2 + dx;
                moved.Y2 = _movingOriginal.Y2 + dy;

                _annotations[_selectedAnnotationIndex] = moved;
                _annotationItems[_selectedAnnotationIndex].Update(moved);
                _annotationItems[_selectedAnnotationIndex].RefreshSelectionBounds();

                OnPropertyChanged("AnnotationSelectionLeft");
                OnPropertyChanged("AnnotationSelectionTop");
                return;
            }

            if (_pendingAnnotation == null)
            {
                return;
            }

            _pendingAnnotation.X2 = x;
            _pendingAnnotation.Y2 = y;
            _pendingItem.Update(_pendingAnnotation);
        }

        /// <summary>结束手势：提交新标注或移动结果（各占一步历史）。</summary>
        public void EndAnnotationGesture()
        {
            if (!_isAnnotationDragging)
            {
                return;
            }

            _isAnnotationDragging = false;

            if (_isMovingAnnotation)
            {
                _isMovingAnnotation = false;
                _movingOriginal = null;

                EnsureMinimumSize(_annotations.ElementAtOrDefault(_selectedAnnotationIndex));

                string label = "移动标注";
                CommitAnnotationChange(label, _annotations, _gestureSnapshot);
                _gestureSnapshot = null;
                return;
            }

            if (_pendingAnnotation == null || _pendingItem == null)
            {
                _gestureSnapshot = null;
                return;
            }

            AnnotationObject created = _pendingAnnotation;
            _pendingAnnotation = null;

            // 只点了一下：给个默认尺寸，避免"点了没反应"
            if (created.Width < ClickThreshold && created.Height < ClickThreshold)
            {
                if (created.Kind == AnnotationKind.Text || created.Kind == AnnotationKind.NumberBadge)
                {
                    created.X2 = created.X1 + 1.0;
                    created.Y2 = created.Y1 + 1.0;
                }
                else
                {
                    created.X2 = created.X1 + DefaultAnnotationSize;
                    created.Y2 = created.Y1 + DefaultAnnotationSize * 0.7;
                }
            }

            _annotations.Add(created);
            _annotationItems.Remove(_pendingItem);
            _pendingItem = null;

            string createdLabel = "添加" + GetAnnotationToolName(created.Kind);
            CommitAnnotationChange(createdLabel, _annotations, _gestureSnapshot);
            _gestureSnapshot = null;

            // 新建后自动选中，便于立刻改参数
            RebuildAnnotationItems(_annotations);
            SelectedAnnotationIndex = _annotations.Count - 1;
        }

        /// <summary>放弃进行中的手势（切换工具 / 退出模式）。</summary>
        private void CancelAnnotationGesture()
        {
            if (_pendingItem != null)
            {
                _annotationItems.Remove(_pendingItem);
                _pendingItem = null;
            }

            _pendingAnnotation = null;
            _isAnnotationDragging = false;
            _isMovingAnnotation = false;
            _movingOriginal = null;
            _gestureSnapshot = null;
        }

        /// <summary>命中测试：从后往前找（后画的在上层）。</summary>
        private int HitTestAnnotation(double x, double y)
        {
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                if (_annotations[i].HitTest(x, y, 0.0))
                {
                    return i;
                }
            }

            return -1;
        }

        private static void EnsureMinimumSize(AnnotationObject item)
        {
            if (item == null)
            {
                return;
            }

            if (item.Kind == AnnotationKind.Text || item.Kind == AnnotationKind.NumberBadge)
            {
                return;
            }

            if (item.Width < ClickThreshold && item.Height < ClickThreshold)
            {
                item.X2 = item.X1 + DefaultAnnotationSize;
                item.Y2 = item.Y1 + DefaultAnnotationSize * 0.7;
            }
        }

        private int NextBadgeNumber()
        {
            int max = 0;

            foreach (AnnotationObject item in _annotations)
            {
                if (item.Kind != AnnotationKind.NumberBadge || string.IsNullOrEmpty(item.Text))
                {
                    continue;
                }

                int value;

                if (int.TryParse(item.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > max)
                {
                    max = value;
                }
            }

            return max + 1;
        }

        #endregion

        #region 编辑与历史

        /// <summary>删除选中的标注。</summary>
        private void DeleteSelectedAnnotation()
        {
            if (!HasSelectedAnnotation)
            {
                return;
            }

            List<AnnotationObject> before = AnnotationEditCommand.CloneList(_annotations);
            _annotations.RemoveAt(_selectedAnnotationIndex);

            CommitAnnotationChange("删除标注", _annotations, before);
            SelectedAnnotationIndex = -1;
        }

        /// <summary>把当前参数应用到选中的标注（改颜色 / 线宽 / 字号）。</summary>
        private void ApplyAnnotationParametersToSelection()
        {
            if (!HasSelectedAnnotation || _document == null)
            {
                return;
            }

            AnnotationObject current = _annotations[_selectedAnnotationIndex];

            if (Math.Abs(current.StrokeWidth - _annotationStrokeWidth) < 1e-9
                && Math.Abs(current.FontSize - _annotationFontSize) < 1e-9
                && current.Color == _annotationColor)
            {
                return;
            }

            List<AnnotationObject> before = AnnotationEditCommand.CloneList(_annotations);

            AnnotationObject updated = current.Clone();
            updated.Color = _annotationColor;
            updated.StrokeWidth = _annotationStrokeWidth;
            updated.FontSize = _annotationFontSize;

            _annotations[_selectedAnnotationIndex] = updated;
            _annotationItems[_selectedAnnotationIndex].Update(updated);
            _annotationItems[_selectedAnnotationIndex].RefreshSelectionBounds();

            CommitAnnotationChange("修改标注参数", _annotations, before, rebuildItems: false);
        }

        /// <summary>
        /// 提交一次标注变化：用对象列表快照压入一步历史。
        /// </summary>
        /// <param name="before">改动前的列表快照；为 null 时不压历史（例如刚打开文档）。</param>
        private void CommitAnnotationChange(
            string label,
            List<AnnotationObject> after,
            List<AnnotationObject> before = null,
            bool rebuildItems = true)
        {
            if (rebuildItems)
            {
                RebuildAnnotationItems(after);
            }

            if (before != null)
            {
                _history.Push(
                    new AnnotationEditCommand(label, before, after, ApplyAnnotationList),
                    null);
            }

            OnPropertyChanged("AnnotationCount");
            OnPropertyChanged("SelectedAnnotationInfoText");
            RelayCommand.RaiseCanExecuteChanged();

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                "已{0}（当前 {1} 个标注）",
                label,
                _annotations.Count);
        }

        /// <summary>历史回调：整体替换对象列表（撤销 / 重做都走这里）。</summary>
        private void ApplyAnnotationList(List<AnnotationObject> objects)
        {
            _annotations.Clear();
            _annotations.AddRange(objects);

            RebuildAnnotationItems(_annotations);
            SelectedAnnotationIndex = -1;

            OnPropertyChanged("AnnotationCount");
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void RebuildAnnotationItems(IEnumerable<AnnotationObject> objects)
        {
            _annotationItems.Clear();

            if (objects != null)
            {
                foreach (AnnotationObject item in objects)
                {
                    _annotationItems.Add(new AnnotationItemViewModel(item));
                }
            }
        }

        #endregion

        #region 合并（烘焙）

        /// <summary>
        /// 把标注合并进画面（破坏性操作前会自动调用）。
        /// 返回是否真的合并了。
        ///
        /// 注意：这里**不做 IsBusy 判断** —— 它正是被 ApplyOneShotAsync 在 IsBusy=true 期间调用的。
        /// 命令的 CanExecute 已经挡掉了"忙碌时手动点合并"的情况。
        /// </summary>
        public bool FlattenAnnotations(string label)
        {
            if (_document == null || _annotations.Count == 0)
            {
                return false;
            }

            EditState before = CreateCurrentStateSnapshot();

            if (before == null)
            {
                return false;
            }

            List<AnnotationObject> beforeObjects = AnnotationEditCommand.CloneList(_annotations);

            PixelBuffer source = PixelBuffer.FromBitmap(_document.Bitmap);
            PixelBuffer composited = AnnotationRenderer.Render(source, _annotations);

            EditState after = EditState.Create(
                composited,
                _committedAdjustments,
                _document.DpiX,
                _document.DpiY);

            if (after.Snapshot == null)
            {
                return false;
            }

            ApplyAnnotationSnapshot(after, new List<AnnotationObject>());

            _history.Push(
                new AnnotationFlattenCommand(
                    label,
                    before,
                    after,
                    beforeObjects,
                    new List<AnnotationObject>(),
                    ApplyAnnotationSnapshot),
                after);

            StatusMessage = label + "完成（" + _history.MemoryUsageText + "）";

            return true;
        }

        /// <summary>合并命令的撤销 / 重做回调：像素与对象列表必须一起还原。</summary>
        private void ApplyAnnotationSnapshot(EditState state, List<AnnotationObject> objects)
        {
            RestoreState(state);

            _annotations.Clear();
            _annotations.AddRange(objects);

            RebuildAnnotationItems(_annotations);
            SelectedAnnotationIndex = -1;

            OnPropertyChanged("AnnotationCount");
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 返回"标注已烘进像素"的文档副本，**不修改当前文档、不进历史**。
        /// 保存时用它，这样导出文件包含标注，而编辑现场仍然保持非破坏性。
        /// </summary>
        private ImageDocument WithAnnotationsBaked(ImageDocument source)
        {
            if (source == null || _annotations.Count == 0)
            {
                return source;
            }

            try
            {
                PixelBuffer basePixels = PixelBuffer.FromBitmap(source.Bitmap);
                PixelBuffer composited = AnnotationRenderer.Render(basePixels, _annotations);

                BitmapSource bitmap = PixelBuffer.ToBitmap(composited, source.DpiX, source.DpiY);

                return source.WithBitmap(bitmap).WithDpi(source.DpiX, source.DpiY);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Annotation] 保存前烘焙标注失败: " + ex.Message);
                return source;
            }
        }

        #endregion

        private static string GetAnnotationToolName(AnnotationKind kind)
        {
            switch (kind)
            {
                case AnnotationKind.Rectangle:
                    return "矩形";
                case AnnotationKind.Ellipse:
                    return "椭圆";
                case AnnotationKind.NumberBadge:
                    return "序号";
                case AnnotationKind.Highlight:
                    return "高亮";
                case AnnotationKind.Text:
                    return "文字";
                default:
                    return "箭头";
            }
        }
    }

    /// <summary>List 的 ElementAtOrDefault 简写（避免引入 Linq 的额外 using 歧义）。</summary>
    internal static class AnnotationListExtensions
    {
        public static T ElementAtOrDefault<T>(this List<T> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count)
            {
                return default(T);
            }

            return list[index];
        }
    }
}
