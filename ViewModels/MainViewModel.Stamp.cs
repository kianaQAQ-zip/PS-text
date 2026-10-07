using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services.Filters;

namespace PSText.ViewModels
{
    /// <summary>
    /// MainViewModel 的「仿制图章」部分（partial，M2b 的第一块）。
    ///
    /// 它是智能填充的手动兜底：调和扩散在纯色 / 渐变背景上最好，但**纹理背景**上会留下平滑斑块，
    /// 那时只能由人指定"拿哪一块纹理来补" —— 这就是仿制图章。
    ///
    /// 交互：按住 **Alt** 在画布上点一下取源 → 在别处拖拽涂抹，每松开一次鼠标记一步历史。
    ///
    /// 两个工程上的关键决定：
    ///   1. **每笔只存改动包围盒的前后像素**（RegionEditCommand），而不是整幅快照。
    ///      12MP 图上整幅快照约 17MB/步，涂几十笔就把历史预算吃满；而一笔 200×200 只有 160KB。
    ///   2. **不逐帧重建整幅位图**：拖拽过程中只在叠加层上画一条"笔刷粗细的折线"作为预览
    ///      （它和实际涂抹范围严格一致），松手才真正计算像素。这样拖动时 UI 不会因为
    ///      反复创建整幅 BitmapSource 而卡顿。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>笔迹采样点的上限（超过则丢弃过密的点，避免距离场计算量失控）。</summary>
        private const int MaxStrokePoints = 800;

        private double _brushRadius = 24.0;
        private double _brushHardness = 0.6;

        private bool _strokeActive;
        private readonly List<StampPoint> _strokePoints = new List<StampPoint>();
        private double _strokeOriginX;
        private double _strokeOriginY;

        private bool _hasCloneStampSource;
        private double _cloneStampSourceX;
        private double _cloneStampSourceY;

        private double _brushCursorX;
        private double _brushCursorY;
        private bool _brushCursorVisible;

        private PointCollection _brushTrailPoints = new PointCollection();

        #region 笔刷参数

        /// <summary>笔刷半径（像素）。</summary>
        public double BrushRadius
        {
            get { return _brushRadius; }
            set
            {
                double clamped = double.IsNaN(value) ? 8.0 : value;

                if (clamped < 2.0)
                {
                    clamped = 2.0;
                }
                else if (clamped > 200.0)
                {
                    clamped = 200.0;
                }

                if (Math.Abs(clamped - _brushRadius) < 1e-9)
                {
                    return;
                }

                _brushRadius = clamped;
                OnPropertyChanged("BrushRadius");
                OnPropertyChanged("BrushDiameter");
                OnPropertyChanged("ClipCloneStampInfoText");
            }
        }

        /// <summary>笔刷直径（供叠加层画圆 / 画折线用）。</summary>
        public double BrushDiameter
        {
            get { return _brushRadius * 2.0; }
        }

        /// <summary>笔刷硬度 0~1（1 = 硬边）。</summary>
        public double BrushHardness
        {
            get { return _brushHardness; }
            set
            {
                double clamped = double.IsNaN(value) ? 0.0 : value;

                if (clamped < 0.0)
                {
                    clamped = 0.0;
                }
                else if (clamped > 1.0)
                {
                    clamped = 1.0;
                }

                if (SetProperty(ref _brushHardness, clamped, "BrushHardness"))
                {
                    OnPropertyChanged("BrushHardnessPercentText");
                    OnPropertyChanged("ClipCloneStampInfoText");
                }
            }
        }

        /// <summary>硬度的百分比文本。</summary>
        public string BrushHardnessPercentText
        {
            get { return (int)Math.Round(_brushHardness * 100.0) + "%"; }
        }

        /// <summary>是否已经取过源点。</summary>
        public bool HasCloneStampSource
        {
            get { return _hasCloneStampSource; }
        }

        /// <summary>源点标记的位置（供叠加层显示）。</summary>
        public double CloneStampSourceLeft
        {
            get { return _cloneStampSourceX; }
        }

        /// <summary>源点标记的位置（供叠加层显示）。</summary>
        public double CloneStampSourceTop
        {
            get { return _cloneStampSourceY; }
        }

        /// <summary>笔刷光标左上角（叠加层按图像像素坐标摆放）。</summary>
        public double BrushCursorLeft
        {
            get { return _brushCursorX - _brushRadius; }
        }

        /// <summary>笔刷光标左上角。</summary>
        public double BrushCursorTop
        {
            get { return _brushCursorY - _brushRadius; }
        }

        /// <summary>是否显示笔刷光标。</summary>
        public bool IsBrushCursorVisible
        {
            get { return _brushCursorVisible && IsCloneStampTool && IsRetouchMode; }
        }

        /// <summary>笔迹预览折线的采样点。厚度用 BrushDiameter 描边后即等于实际涂抹范围。</summary>
        public PointCollection BrushTrailPoints
        {
            get { return _brushTrailPoints; }
        }

        /// <summary>是否正在涂抹（供叠加层决定是否显示笔迹）。</summary>
        public bool HasStrokeTrail
        {
            get { return _strokeActive && _brushTrailPoints.Count > 0; }
        }

        /// <summary>仿制图章的状态说明（供面板显示）。</summary>
        public string ClipCloneStampInfoText
        {
            get
            {
                string brush = string.Format(
                    CultureInfo.CurrentCulture,
                    "笔刷 {0:0} px · 硬度 {1}",
                    _brushRadius,
                    BrushHardnessPercentText);

                if (!_hasCloneStampSource)
                {
                    return brush + "。按住 Alt 在画布上点一下取源，然后在要去掉的地方拖拽涂抹。";
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}；源点 ({1:0}, {2:0})。在别处拖拽涂抹即可，每次松开鼠标记一步历史。",
                    brush,
                    _cloneStampSourceX,
                    _cloneStampSourceY);
            }
        }

        #endregion

        #region 源点与光标

        /// <summary>设置取源点（Alt + 单击）。</summary>
        public void SetCloneStampSource(double imageX, double imageY)
        {
            if (_document == null)
            {
                return;
            }

            double x = ClampToImageX(imageX);
            double y = ClampToImageY(imageY);

            _hasCloneStampSource = true;
            _cloneStampSourceX = x;
            _cloneStampSourceY = y;

            OnPropertyChanged("HasCloneStampSource");
            OnPropertyChanged("CloneStampSourceLeft");
            OnPropertyChanged("CloneStampSourceTop");
            OnPropertyChanged("ClipCloneStampInfoText");

            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                "已取源点 ({0:0}, {1:0})，现在在要去掉的地方拖拽涂抹",
                x,
                y);
        }

        /// <summary>更新笔刷光标位置（未按下鼠标时也会随鼠标移动）。</summary>
        public void UpdateRetouchCursor(double imageX, double imageY)
        {
            if (!_isRetouchMode || !IsCloneStampTool)
            {
                return;
            }

            double x = ClampToImageX(imageX);
            double y = ClampToImageY(imageY);

            if (Math.Abs(x - _brushCursorX) < 0.01 && Math.Abs(y - _brushCursorY) < 0.01)
            {
                return;
            }

            _brushCursorX = x;
            _brushCursorY = y;

            if (!_brushCursorVisible)
            {
                _brushCursorVisible = true;
                OnPropertyChanged("IsBrushCursorVisible");
            }

            OnPropertyChanged("BrushCursorLeft");
            OnPropertyChanged("BrushCursorTop");
        }

        /// <summary>隐藏笔刷光标（鼠标移出画布）。</summary>
        public void HideRetouchCursor()
        {
            if (!_brushCursorVisible)
            {
                return;
            }

            _brushCursorVisible = false;
            OnPropertyChanged("IsBrushCursorVisible");
        }

        #endregion

        #region 涂抹

        private void BeginStampStroke(double imageX, double imageY)
        {
            if (_document == null)
            {
                return;
            }

            _strokeActive = true;
            _strokePoints.Clear();

            _strokeOriginX = ClampToImageX(imageX);
            _strokeOriginY = ClampToImageY(imageY);

            _strokePoints.Add(new StampPoint(_strokeOriginX, _strokeOriginY));

            _brushTrailPoints = new PointCollection();
            _brushTrailPoints.Add(new System.Windows.Point(_strokeOriginX, _strokeOriginY));
            RaiseTrailChanged();

            StatusMessage = _hasCloneStampSource
                ? "正在涂抹……松开鼠标应用这一笔"
                : "尚未取源：按住 Alt 点一下画布指定要复制的位置";
        }

        private void UpdateStampStroke(double imageX, double imageY)
        {
            if (!_strokeActive || _document == null)
            {
                return;
            }

            double x = ClampToImageX(imageX);
            double y = ClampToImageY(imageY);

            // 采样点抽稀：距离过近的点对"到折线的距离"几乎没有贡献，
            // 但会让距离场计算量线性增长（每个像素都要遍历所有线段）。
            double minDistance = Math.Max(1.0, _brushRadius / 4.0);

            if (_strokePoints.Count > 0)
            {
                StampPoint last = _strokePoints[_strokePoints.Count - 1];
                double dx = x - last.X;
                double dy = y - last.Y;

                if (Math.Sqrt(dx * dx + dy * dy) < minDistance)
                {
                    return;
                }
            }

            if (_strokePoints.Count >= MaxStrokePoints)
            {
                return;
            }

            _strokePoints.Add(new StampPoint(x, y));
            _brushTrailPoints.Add(new System.Windows.Point(x, y));
            RaiseTrailChanged();
        }

        private void EndStampStroke()
        {
            if (!_strokeActive)
            {
                return;
            }

            _strokeActive = false;
            RaiseTrailChanged();

            List<StampPoint> points = new List<StampPoint>(_strokePoints);
            _strokePoints.Clear();
            _brushTrailPoints = new PointCollection();
            RaiseTrailChanged();

            if (points.Count == 0)
            {
                return;
            }

            if (!_hasCloneStampSource)
            {
                StatusMessage = "仿制图章：请先按住 Alt 在画布上点一下取源，再涂抹";
                return;
            }

            // 源位置 = 笔迹位置 + 固定偏移（偏移由"源点 − 起笔点"决定），
            // 因此一笔之内是纯粹的平移搬运，纹理不会被扭曲。
            int offsetX = (int)Math.Round(_cloneStampSourceX - _strokeOriginX);
            int offsetY = (int)Math.Round(_cloneStampSourceY - _strokeOriginY);

            double radius = _brushRadius;
            double hardness = _brushHardness;

            _pendingOperation = RunStampStrokeAsync(points, radius, hardness, offsetX, offsetY);
        }

        private void RaiseTrailChanged()
        {
            OnPropertyChanged("BrushTrailPoints");
            OnPropertyChanged("HasStrokeTrail");
        }

        #endregion

        #region 提交（区域历史）

        private async System.Threading.Tasks.Task RunStampStrokeAsync(
            List<StampPoint> points,
            double radius,
            double hardness,
            int offsetX,
            int offsetY)
        {
            if (!HasDocument || IsBusy || _document == null)
            {
                return;
            }

            IsBusy = true;

            try
            {
                int imageWidth = _document.PixelWidth;
                double dpiX = _document.DpiX;
                double dpiY = _document.DpiY;

                PixelBuffer source = PixelBuffer.FromBitmap(_document.Bitmap);
                StatusMessage = "正在涂抹……";

                PixelRegion painted = new PixelRegion();

                PixelBuffer result = await Task.Run(
                    () =>
                    {
                        PixelRegion bounds;
                        PixelBuffer stamped = CloneStampFilter.Stamp(
                            source,
                            points,
                            radius,
                            hardness,
                            offsetX,
                            offsetY,
                            out bounds,
                            CancellationToken.None);
                        painted = bounds;
                        return stamped;
                    }).ConfigureAwait(true);

                if (_document == null)
                {
                    return;
                }

                if (painted.IsEmpty)
                {
                    StatusMessage = "本次涂抹没有产生改动（可能源位置超出了画布）";
                    return;
                }

                byte[] beforeRegion = ExtractRegion(source.GetPixels(), imageWidth, painted);
                byte[] afterRegion = ExtractRegion(result.GetPixels(), imageWidth, painted);

                BitmapSource bitmap = PixelBuffer.ToBitmap(result, dpiX, dpiY);
                Document = _document.WithBitmap(bitmap).WithDpi(dpiX, dpiY);

                // 涂抹会打断当前的调整会话：下次拖滑块从新画面重新建立基准。
                EndAdjustmentSession();

                RegionEditCommand command = new RegionEditCommand(
                    "仿制图章",
                    imageWidth,
                    painted.X,
                    painted.Y,
                    painted.Width,
                    painted.Height,
                    beforeRegion,
                    afterRegion,
                    ApplyRegionToDocument);

                _history.Push(command, null);

                StatusMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    "已涂抹一笔（改动区域 {0} × {1}，历史占用 {2}）",
                    painted.Width,
                    painted.Height,
                    _history.MemoryUsageText);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "已取消涂抹";
            }
            catch (Exception ex)
            {
                HandleError("仿制图章涂抹失败。", ex, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>把区域像素抽取出来（供区域历史保存）。</summary>
        private static byte[] ExtractRegion(byte[] pixels, int imageWidth, PixelRegion region)
        {
            int rowBytes = region.Width * 4;
            byte[] result = new byte[rowBytes * region.Height];

            for (int row = 0; row < region.Height; row++)
            {
                int offset = ((region.Y + row) * imageWidth + region.X) * 4;
                Buffer.BlockCopy(pixels, offset, result, row * rowBytes, rowBytes);
            }

            return result;
        }

        /// <summary>
        /// 撤销 / 重做的写回回调：只把包围盒内的像素写回画布。
        /// 这比整幅快照方案多了一次位图重建，但省下的历史内存是两个数量级。
        /// </summary>
        private void ApplyRegionToDocument(int x, int y, int width, int height, byte[] region)
        {
            ImageDocument document = _document;

            if (document == null || region == null || width <= 0 || height <= 0)
            {
                return;
            }

            PixelBuffer buffer = PixelBuffer.FromBitmap(document.Bitmap);
            byte[] pixels = buffer.GetPixels();
            int rowBytes = width * 4;

            for (int row = 0; row < height; row++)
            {
                int offset = ((y + row) * buffer.Width + x) * 4;

                if (offset < 0 || offset + rowBytes > pixels.Length)
                {
                    continue;
                }

                Buffer.BlockCopy(region, row * rowBytes, pixels, offset, rowBytes);
            }

            Document = document
                .WithBitmap(PixelBuffer.ToBitmap(buffer, document.DpiX, document.DpiY))
                .WithDpi(document.DpiX, document.DpiY);
        }

        #endregion
    }
}
