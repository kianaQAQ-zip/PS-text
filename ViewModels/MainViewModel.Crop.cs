using System;
using System.Threading.Tasks;
using System.Windows.Input;
using PSText.Infrastructure;
using PSText.Infrastructure.Imaging;
using PSText.Services.Filters;

namespace PSText.ViewModels
{
    /// <summary>
    /// MainViewModel 的「自由裁剪」部分（partial，对应需求 P1-7）。
    ///
    /// 交互模型：View 只负责把鼠标的按下 / 拖动 / 抬起坐标回传，
    /// 所有裁剪框状态与钳制逻辑都在这里，View 不参与业务判断。
    /// 裁剪框带三分线辅助（由 View 依据 CropX/Y/Width/Height 绘制）。
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>裁剪框最小边长（像素）。</summary>
        private const int MinCropSize = 8;

        private bool _isCropping;
        private double _cropX;
        private double _cropY;
        private double _cropWidth;
        private double _cropHeight;
        private bool _isDraggingCrop;
        private double _dragOriginX;
        private double _dragOriginY;
        private double _dragStartX;
        private double _dragStartY;
        private string _activeCropHandle = "move";

        /// <summary>是否处于裁剪模式。</summary>
        public bool IsCropping
        {
            get { return _isCropping; }
            private set
            {
                if (SetProperty(ref _isCropping, value, "IsCropping"))
                {
                    OnPropertyChanged("IsCropAreaValid");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>裁剪框左上角 X（图像像素坐标）。</summary>
        public double CropX
        {
            get { return _cropX; }
            private set
            {
                if (SetProperty(ref _cropX, value, "CropX"))
                {
                    OnPropertyChanged("CropInfoText");
                }
            }
        }

        /// <summary>裁剪框左上角 Y。</summary>
        public double CropY
        {
            get { return _cropY; }
            private set
            {
                if (SetProperty(ref _cropY, value, "CropY"))
                {
                    OnPropertyChanged("CropInfoText");
                }
            }
        }

        /// <summary>裁剪框宽度。</summary>
        public double CropWidth
        {
            get { return _cropWidth; }
            private set
            {
                if (SetProperty(ref _cropWidth, value, "CropWidth"))
                {
                    OnPropertyChanged("CropInfoText");
                    OnPropertyChanged("IsCropAreaValid");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>裁剪框高度。</summary>
        public double CropHeight
        {
            get { return _cropHeight; }
            private set
            {
                if (SetProperty(ref _cropHeight, value, "CropHeight"))
                {
                    OnPropertyChanged("CropInfoText");
                    OnPropertyChanged("IsCropAreaValid");
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>裁剪框尺寸是否符合最小要求。</summary>
        public bool IsCropAreaValid
        {
            get { return _cropWidth >= MinCropSize && _cropHeight >= MinCropSize; }
        }

        /// <summary>裁剪框矩形（供 XAML 的 RectangleGeometry 直接绑定）。</summary>
        public System.Windows.Rect CropRect
        {
            get { return new System.Windows.Rect(_cropX, _cropY, _cropWidth, _cropHeight); }
        }

        /// <summary>三分线：第一条竖线相对裁剪框左边缘的偏移。</summary>
        public double CropOneThirdX1
        {
            get { return _cropWidth / 3.0; }
        }

        /// <summary>三分线：第二条竖线相对裁剪框左边缘的偏移。</summary>
        public double CropTwoThirdX1
        {
            get { return _cropWidth * 2.0 / 3.0; }
        }

        /// <summary>三分线：第一条横线相对裁剪框上边缘的偏移。</summary>
        public double CropOneThirdY1
        {
            get { return _cropHeight / 3.0; }
        }

        /// <summary>三分线：第二条横线相对裁剪框上边缘的偏移。</summary>
        public double CropTwoThirdY1
        {
            get { return _cropHeight * 2.0 / 3.0; }
        }

        /// <summary>裁剪框信息文本（供状态栏 / 面板显示）。</summary>
        public string CropInfoText
        {
            get
            {
                if (!_isCropping)
                {
                    return string.Empty;
                }

                return string.Format(
                    "裁剪区域：{0:0} × {1:0} px  @ ({2:0}, {3:0})",
                    _cropWidth,
                    _cropHeight,
                    _cropX,
                    _cropY);
            }
        }

        /// <summary>
        /// 进入裁剪模式：默认选中整幅图，用户可直接拖动调整。
        /// </summary>
        private void BeginCrop()
        {
            if (_document == null)
            {
                return;
            }

            IsCropping = true;
            SetCropRect(0, 0, _document.PixelWidth, _document.PixelHeight);
            StatusMessage = "裁剪模式：拖动框内移动，拖动边角缩放，然后点“应用裁剪”";
        }

        /// <summary>退出裁剪模式（不改变图像）。</summary>
        private void CancelCrop()
        {
            IsCropping = false;
            _isDraggingCrop = false;
            StatusMessage = "已取消裁剪";
        }

        /// <summary>由 View 在鼠标按下时调用，开始拖动 / 缩放裁剪框。</summary>
        /// <param name="handle">手柄名称：move / nw / ne / sw / se / n / s / w / e。</param>
        /// <param name="imageX">鼠标位置（图像像素坐标）。</param>
        /// <param name="imageY">鼠标位置（图像像素坐标）。</param>
        public void BeginCropDrag(string handle, double imageX, double imageY)
        {
            if (!_isCropping || _document == null)
            {
                return;
            }

            _activeCropHandle = string.IsNullOrWhiteSpace(handle) ? "move" : handle;
            _isDraggingCrop = true;
            _dragOriginX = _cropX;
            _dragOriginY = _cropY;
            _dragStartX = imageX;
            _dragStartY = imageY;
        }

        /// <summary>
        /// 拖动中：根据手柄类型更新裁剪框，并把结果钳制在图像范围内。
        /// </summary>
        public void UpdateCropDrag(double imageX, double imageY)
        {
            if (!_isCropping || !_isDraggingCrop || _document == null)
            {
                return;
            }

            double deltaX = imageX - _dragStartX;
            double deltaY = imageY - _dragStartY;

            double imageWidth = _document.PixelWidth;
            double imageHeight = _document.PixelHeight;

            double left = _dragOriginX;
            double top = _dragOriginY;
            double right = _dragOriginX + _cropWidth;
            double bottom = _dragOriginY + _cropHeight;

            switch (_activeCropHandle)
            {
                case "move":
                    left += deltaX;
                    top += deltaY;
                    right += deltaX;
                    bottom += deltaY;

                    // 整体移动时保持尺寸，因此先限制位移范围
                    if (left < 0)
                    {
                        right -= left;
                        left = 0;
                    }

                    if (top < 0)
                    {
                        bottom -= top;
                        top = 0;
                    }

                    if (right > imageWidth)
                    {
                        left -= right - imageWidth;
                        right = imageWidth;
                    }

                    if (bottom > imageHeight)
                    {
                        top -= bottom - imageHeight;
                        bottom = imageHeight;
                    }

                    break;

                default:
                    if (_activeCropHandle.Contains("w"))
                    {
                        left = Math.Min(left + deltaX, right - MinCropSize);
                    }

                    if (_activeCropHandle.Contains("e"))
                    {
                        right = Math.Max(right + deltaX, left + MinCropSize);
                    }

                    if (_activeCropHandle.Contains("n"))
                    {
                        top = Math.Min(top + deltaY, bottom - MinCropSize);
                    }

                    if (_activeCropHandle.Contains("s"))
                    {
                        bottom = Math.Max(bottom + deltaY, top + MinCropSize);
                    }

                    break;
            }

            // 统一钳制到图像范围
            left = Math.Max(0.0, Math.Min(left, imageWidth - MinCropSize));
            top = Math.Max(0.0, Math.Min(top, imageHeight - MinCropSize));
            right = Math.Max(left + MinCropSize, Math.Min(right, imageWidth));
            bottom = Math.Max(top + MinCropSize, Math.Min(bottom, imageHeight));

            SetCropRect(left, top, right - left, bottom - top);
        }

        /// <summary>结束拖动。</summary>
        public void EndCropDrag()
        {
            _isDraggingCrop = false;
        }

        /// <summary>直接设置裁剪框（四边都会被钳制到图像范围内）。</summary>
        public void SetCropRect(double x, double y, double width, double height)
        {
            if (_document == null)
            {
                return;
            }

            double imageWidth = _document.PixelWidth;
            double imageHeight = _document.PixelHeight;

            double left = Math.Max(0.0, Math.Min(x, imageWidth - MinCropSize));
            double top = Math.Max(0.0, Math.Min(y, imageHeight - MinCropSize));
            double actualWidth = Math.Max(MinCropSize, Math.Min(width, imageWidth - left));
            double actualHeight = Math.Max(MinCropSize, Math.Min(height, imageHeight - top));

            CropX = left;
            CropY = top;
            CropWidth = actualWidth;
            CropHeight = actualHeight;

            // 依赖裁剪框几何的派生属性需要一并通知
            OnPropertyChanged("CropRect");
            OnPropertyChanged("CropOneThirdX1");
            OnPropertyChanged("CropTwoThirdX1");
            OnPropertyChanged("CropOneThirdY1");
            OnPropertyChanged("CropTwoThirdY1");
        }

        /// <summary>
        /// 判断鼠标落在裁剪框的哪个手柄上。
        /// 命中容差按“图像像素”给（12px），因此缩放到很小的时候仍然好点。
        /// </summary>
        public string HitTestCropHandle(double imageX, double imageY)
        {
            if (!_isCropping)
            {
                return "move";
            }

            const double tolerance = 12.0;

            bool nearLeft = Math.Abs(imageX - _cropX) <= tolerance;
            bool nearRight = Math.Abs(imageX - (_cropX + _cropWidth)) <= tolerance;
            bool nearTop = Math.Abs(imageY - _cropY) <= tolerance;
            bool nearBottom = Math.Abs(imageY - (_cropY + _cropHeight)) <= tolerance;

            bool insideX = imageX >= _cropX - tolerance && imageX <= _cropX + _cropWidth + tolerance;
            bool insideY = imageY >= _cropY - tolerance && imageY <= _cropY + _cropHeight + tolerance;

            if (insideX && insideY)
            {
                // 角 / 边手柄优先于整体移动
                if (nearLeft && nearTop)
                {
                    return "nw";
                }

                if (nearRight && nearTop)
                {
                    return "ne";
                }

                if (nearLeft && nearBottom)
                {
                    return "sw";
                }

                if (nearRight && nearBottom)
                {
                    return "se";
                }

                if (nearLeft)
                {
                    return "w";
                }

                if (nearRight)
                {
                    return "e";
                }

                if (nearTop)
                {
                    return "n";
                }

                if (nearBottom)
                {
                    return "s";
                }

                return "move";
            }

            // 框外也允许整体移动（手感更接近常见截图工具）
            return "move";
        }

        /// <summary>执行裁剪（把裁剪框转换为整数像素后调用几何滤镜）。</summary>
        private void RunCropAsync()
        {
            if (!_isCropping || _document == null)
            {
                return;
            }

            int x = (int)Math.Round(_cropX);
            int y = (int)Math.Round(_cropY);
            int width = (int)Math.Round(_cropWidth);
            int height = (int)Math.Round(_cropHeight);

            if (width < MinCropSize || height < MinCropSize)
            {
                StatusMessage = "裁剪区域太小，请重新选择";
                return;
            }

            IsCropping = false;

            GeometryFilters geometry = _geometryFilters;
            _pendingOperation = ApplyOneShotAsync(
                "裁剪",
                buffer => geometry.CropAsync(buffer, x, y, width, height));
        }
    }
}
