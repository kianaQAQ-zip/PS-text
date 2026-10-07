using System;
using System.Collections.Generic;

namespace PSText.Services.Interfaces
{
    /// <summary>打印机信息（只暴露界面需要的字段）。</summary>
    public sealed class PrinterInfo
    {
        public PrinterInfo(string name, bool isDefault, bool isAvailable)
        {
            Name = name;
            IsDefault = isDefault;
            IsAvailable = isAvailable;
        }

        /// <summary>打印机名称（显示用）。</summary>
        public string Name { get; private set; }

        /// <summary>是否为系统默认打印机。</summary>
        public bool IsDefault { get; private set; }

        /// <summary>当前是否可用（脱机 / 驱动异常时为 false）。</summary>
        public bool IsAvailable { get; private set; }
    }

    /// <summary>
    /// 打印服务（需求 P2-9 / P2-10 / P2-11 / P2-12）。
    ///
    /// 抽象成接口的目的：打印机交互在自检 / 单元测试环境不可用，
    /// 用桩实现替换后即可测试版面计算与分页逻辑。
    /// </summary>
    public interface IPrintService
    {
        /// <summary>枚举本机打印机。</summary>
        IReadOnlyList<PrinterInfo> GetPrinters();

        /// <summary>
        /// 弹出系统打印对话框（选择打印机 / 份数 / 页面范围 / 方向）。
        /// 返回 null 表示用户取消。
        /// </summary>
        PrintRequest ShowPrintDialog(string documentTitle);

        /// <summary>
        /// 使用上次 <see cref="ShowPrintDialog"/> 选择的打印机执行打印。
        /// </summary>
        /// <param name="request">对话框返回的请求。</param>
        /// <param name="bitmap">要打印的位图。</param>
        /// <param name="imageDpiX">图像水平 DPI（决定物理尺寸）。</param>
        /// <param name="imageDpiY">图像垂直 DPI。</param>
        /// <param name="layout">版面计算结果。</param>
        void Print(PrintRequest request, System.Windows.Media.Imaging.BitmapSource bitmap, double imageDpiX, double imageDpiY, Printing.PrintLayout layout);
    }

    /// <summary>一次打印请求（由系统打印对话框产生）。</summary>
    public sealed class PrintRequest
    {
        /// <summary>单页尺寸（DIP，含不可打印边距）。</summary>
        public System.Windows.Size PageSizeDips { get; set; }

        /// <summary>打印机硬边距（DIP）。</summary>
        public System.Windows.Thickness HardMarginDips { get; set; }

        /// <summary>可打印区域尺寸（DIP）。</summary>
        public System.Windows.Size PrintableAreaDips { get; set; }

        /// <summary>打印机名称（用于批量打印）。</summary>
        public string PrinterName { get; set; }

        /// <summary>份数。</summary>
        public int Copies { get; set; }

        /// <summary>用户是否选择了页面范围。</summary>
        public bool HasPageRange { get; set; }

        /// <summary>起始页（1 起）。</summary>
        public int PageFrom { get; set; }

        /// <summary>结束页（含）。</summary>
        public int PageTo { get; set; }

        /// <summary>系统对话框返回的原生对象（实现内部使用）。</summary>
        internal object NativeDialog { get; set; }
    }
}
