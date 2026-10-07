using System;
using System.Windows.Media.Imaging;

namespace PSText.Models
{
    /// <summary>
    /// 图片文档快照（不可变）。
    /// 设计说明：编辑操作产生新的快照而不是就地修改，撤销 / 重做只需保存“位图 + 参数”，
    /// 避免为每一步历史复制整张位图（内存优化见后续 HistoryManager）。
    /// </summary>
    public sealed class ImageDocument
    {
        public ImageDocument(
            BitmapSource bitmap,
            string filePath,
            ImageFileFormat format,
            double dpiX,
            double dpiY,
            long fileSizeBytes,
            bool isDirty)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            // 冻结后位图不可变，可安全地在后台线程读取 / 编解码。
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            Bitmap = bitmap;
            FilePath = filePath;
            Format = format;
            FileSizeBytes = fileSizeBytes;
            IsDirty = isDirty;

            DpiX = dpiX > 0.5 && !double.IsNaN(dpiX) ? dpiX : 96.0;
            DpiY = dpiY > 0.5 && !double.IsNaN(dpiY) ? dpiY : 96.0;
        }

        /// <summary>当前显示 / 打印用的位图。</summary>
        public BitmapSource Bitmap { get; private set; }

        /// <summary>关联的源文件路径；新建图片为 null。</summary>
        public string FilePath { get; private set; }

        /// <summary>文档格式（保存时默认沿用）。</summary>
        public ImageFileFormat Format { get; private set; }

        /// <summary>水平 DPI。</summary>
        public double DpiX { get; private set; }

        /// <summary>垂直 DPI。</summary>
        public double DpiY { get; private set; }

        /// <summary>源文件字节数。</summary>
        public long FileSizeBytes { get; private set; }

        /// <summary>是否存在未保存的修改。</summary>
        public bool IsDirty { get; private set; }

        public int PixelWidth
        {
            get { return Bitmap.PixelWidth; }
        }

        public int PixelHeight
        {
            get { return Bitmap.PixelHeight; }
        }

        /// <summary>文件名（无路径）；无文件时返回“未命名”。</summary>
        public string FileName
        {
            get
            {
                if (string.IsNullOrEmpty(FilePath))
                {
                    return "未命名";
                }

                try
                {
                    string name = System.IO.Path.GetFileName(FilePath);
                    return string.IsNullOrEmpty(name) ? FilePath : name;
                }
                catch (ArgumentException)
                {
                    // 路径非法（例如包含非法字符）时退回原字符串，避免界面报错。
                    return FilePath;
                }
            }
        }

        /// <summary>像素尺寸文本，例如 “1920 × 1080 px”。</summary>
        public string PixelSizeText
        {
            get { return string.Format("{0} × {1} px", PixelWidth, PixelHeight); }
        }

        /// <summary>DPI 文本，例如 “300 × 300 DPI”。</summary>
        public string DpiText
        {
            get { return string.Format("{0:0.#} × {1:0.#} DPI", DpiX, DpiY); }
        }

        /// <summary>物理尺寸文本（按 DPI 换算，单位英寸 / 厘米）。</summary>
        public string PhysicalSizeText
        {
            get
            {
                double widthInch = PixelWidth / DpiX;
                double heightInch = PixelHeight / DpiY;
                return string.Format(
                    "{0:0.##} × {1:0.##} 英寸 ({2:0.#} × {3:0.#} 厘米)",
                    widthInch,
                    heightInch,
                    widthInch * 2.54,
                    heightInch * 2.54);
            }
        }

        /// <summary>文件大小文本。</summary>
        public string FileSizeText
        {
            get
            {
                if (FileSizeBytes <= 0)
                {
                    return "—";
                }

                double size = FileSizeBytes;
                string[] units = { "B", "KB", "MB", "GB" };
                int unitIndex = 0;

                while (size >= 1024.0 && unitIndex < units.Length - 1)
                {
                    size /= 1024.0;
                    unitIndex++;
                }

                return string.Format("{0:0.##} {1}", size, units[unitIndex]);
            }
        }

        /// <summary>由加载结果创建文档快照。</summary>
        public static ImageDocument FromLoadResult(ImageLoadResult loadResult)
        {
            if (loadResult == null)
            {
                throw new ArgumentNullException("loadResult");
            }

            return new ImageDocument(
                loadResult.Bitmap,
                loadResult.FilePath,
                loadResult.Format,
                loadResult.DpiX,
                loadResult.DpiY,
                loadResult.FileSizeBytes,
                false);
        }

        /// <summary>替换位图（编辑后）并标记为已修改。</summary>
        public ImageDocument WithBitmap(BitmapSource newBitmap)
        {
            if (newBitmap == null)
            {
                throw new ArgumentNullException("newBitmap");
            }

            return new ImageDocument(newBitmap, FilePath, Format, DpiX, DpiY, FileSizeBytes, true);
        }

        /// <summary>标记保存完成（清除脏标记，同时更新路径 / 格式 / 文件大小）。</summary>
        public ImageDocument MarkSaved(string filePath, ImageFileFormat format, long fileSizeBytes)
        {
            return new ImageDocument(
                Bitmap,
                string.IsNullOrWhiteSpace(filePath) ? FilePath : filePath,
                format,
                DpiX,
                DpiY,
                fileSizeBytes,
                false);
        }

        /// <summary>设置脏标记。</summary>
        public ImageDocument WithDirty(bool isDirty)
        {
            if (IsDirty == isDirty)
            {
                return this;
            }

            return new ImageDocument(Bitmap, FilePath, Format, DpiX, DpiY, FileSizeBytes, isDirty);
        }

        /// <summary>更新 DPI（不改变像素数据）。</summary>
        public ImageDocument WithDpi(double dpiX, double dpiY)
        {
            return new ImageDocument(Bitmap, FilePath, Format, dpiX, dpiY, FileSizeBytes, true);
        }
    }
}
