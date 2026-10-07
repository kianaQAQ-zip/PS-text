using System;
using System.Collections.Generic;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PSText.Services.Interfaces;
using PSText.Services.Printing;

namespace PSText.Services
{
    /// <summary>
    /// 基于 WPF 打印管线的打印服务实现。
    ///
    /// DPI 说明（需求 P2-11）：
    ///   WPF 的 DocumentPaginator 以 DIP（1/96 英寸）为单位排版，
    ///   打印管线会把它映射到打印机的实际分辨率。
    ///   因此这里**只按 DIP 排版**，绝不手动乘打印机 DPI，
    ///   否则会发生二次缩放（先按 96 放大、再按打印机 DPI 放大）导致模糊。
    ///   图像的物理尺寸由图像自身 DPI 通过 PrintUnits.PixelsToDips 决定，
    ///   所以"打印使用图片原始 DPI、不因屏幕缩放而模糊"自然成立。
    /// </summary>
    public sealed class WpfPrintService : IPrintService
    {
        public IReadOnlyList<PrinterInfo> GetPrinters()
        {
            List<PrinterInfo> printers = new List<PrinterInfo>();

            try
            {
                using (LocalPrintServer server = new LocalPrintServer())
                {
                    string defaultName = null;

                    try
                    {
                        defaultName = server.DefaultPrintQueue != null ? server.DefaultPrintQueue.FullName : null;
                    }
                    catch (Exception)
                    {
                        // 无默认打印机时忽略
                        defaultName = null;
                    }

                    PrintQueueCollection queues = server.GetPrintQueues(
                        new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });

                    foreach (PrintQueue queue in queues)
                    {
                        bool available = true;
                        string name = queue.FullName;

                        try
                        {
                            available = !queue.IsOffline && !queue.IsInError;
                        }
                        catch (Exception)
                        {
                            // 个别驱动在查询状态时会抛异常，视为可用以保证不丢失条目
                            available = true;
                        }

                        printers.Add(new PrinterInfo(name, name == defaultName, available));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WpfPrintService] 枚举打印机失败: " + ex.Message);
            }

            return printers;
        }

        public PrintRequest ShowPrintDialog(string documentTitle)
        {
            PrintDialog dialog = new PrintDialog();

            if (!string.IsNullOrWhiteSpace(documentTitle))
            {
                try
                {
                    dialog.PrintTicket = dialog.PrintTicket ?? new PrintTicket();
                }
                catch (Exception)
                {
                    // 部分 Win7 驱动在读取 PrintTicket 时会抛异常，忽略即可
                }
            }

            // 允许用户选择页面范围（多页打印时有用）
            bool? confirmed;

            try
            {
                dialog.UserPageRangeEnabled = true;
                confirmed = dialog.ShowDialog();
            }
            catch (Exception)
            {
                // 极端情况下（无打印机 / 驱动异常）退回最简对话框
                dialog.UserPageRangeEnabled = false;
                confirmed = dialog.ShowDialog();
            }

            if (confirmed != true)
            {
                return null;
            }

            PrintRequest request = new PrintRequest
            {
                NativeDialog = dialog,
                PrinterName = SafeGetPrinterName(dialog),
                Copies = Math.Max(1, SafeGetCopies(dialog)),
                HasPageRange = false,
                PageFrom = 1,
                PageTo = int.MaxValue
            };

            try
            {
                if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
                {
                    request.HasPageRange = true;
                    request.PageFrom = Math.Max(1, dialog.PageRange.PageFrom);
                    request.PageTo = Math.Max(request.PageFrom, dialog.PageRange.PageTo);
                }
            }
            catch (Exception)
            {
                request.HasPageRange = false;
            }

            // 可打印区域与硬边距：PrintDialog 暴露的是"可打印区域尺寸"，
            // 纸张尺寸 = 可打印区域 + 硬边距，而硬边距可由 PrintQueue 的
            // PrintCapabilities 得到；拿不到时按经验值（每边 0.25 英寸）兜底。
            try
            {
                double printableWidth = dialog.PrintableAreaWidth;
                double printableHeight = dialog.PrintableAreaHeight;

                Thickness hardMargin = TryGetHardMargin(dialog) ?? new Thickness(
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25));

                request.PrintableAreaDips = new Size(printableWidth, printableHeight);
                request.HardMarginDips = hardMargin;
                request.PageSizeDips = new Size(
                    printableWidth + hardMargin.Left + hardMargin.Right,
                    printableHeight + hardMargin.Top + hardMargin.Bottom);
            }
            catch (Exception)
            {
                // 兜底：A4 + 0.25 英寸边距
                Thickness fallbackMargin = new Thickness(
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25));

                request.HardMarginDips = fallbackMargin;
                request.PrintableAreaDips = new Size(
                    PrintUnits.MillimetresToDips(210) - fallbackMargin.Left - fallbackMargin.Right,
                    PrintUnits.MillimetresToDips(297) - fallbackMargin.Top - fallbackMargin.Bottom);
                request.PageSizeDips = new Size(
                    PrintUnits.MillimetresToDips(210),
                    PrintUnits.MillimetresToDips(297));
            }

            return request;
        }

        public void Print(
            PrintRequest request,
            BitmapSource bitmap,
            double imageDpiX,
            double imageDpiY,
            PrintLayout layout)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (bitmap == null)
            {
                throw new ArgumentNullException("bitmap");
            }

            if (layout == null)
            {
                throw new ArgumentNullException("layout");
            }

            PrintDialog dialog = request.NativeDialog as PrintDialog;

            ImagePrintPaginator paginator = new ImagePrintPaginator(bitmap, layout, request.PageSizeDips);

            if (dialog != null)
            {
                // 走系统对话框：份数 / 页面范围由对话框负责
                dialog.PrintDocument(paginator, "PS-text 打印");
                return;
            }

            // 无对话框（批量打印复用的路径）：直接发送到指定打印机
            SendToPrinter(request.PrinterName, paginator, request.Copies);
        }

        /// <summary>不弹对话框，直接把分页器送到指定打印机（供批量打印使用）。</summary>
        public void PrintDirect(
            string printerName,
            BitmapSource bitmap,
            PrintLayout layout,
            Size pageSizeDips,
            int copies)
        {
            ImagePrintPaginator paginator = new ImagePrintPaginator(bitmap, layout, pageSizeDips);
            SendToPrinter(printerName, paginator, copies);
        }

        private static void SendToPrinter(string printerName, ImagePrintPaginator paginator, int copies)
        {
            try
            {
                using (LocalPrintServer server = new LocalPrintServer())
                {
                    PrintQueue queue = string.IsNullOrWhiteSpace(printerName)
                        ? server.DefaultPrintQueue
                        : new PrintQueue(server, printerName);

                    if (queue == null)
                    {
                        throw new InvalidOperationException("找不到可用的打印机。");
                    }

                    int effectiveCopies = copies < 1 ? 1 : copies;

                    // 依次提交多份（比依赖 PrintTicket.CopyCount 更可靠，部分驱动不支持后者）
                    for (int i = 0; i < effectiveCopies; i++)
                    {
                        System.Windows.Xps.XpsDocumentWriter writer =
                            PrintQueue.CreateXpsDocumentWriter(queue);

                        writer.Write(paginator, queue.DefaultPrintTicket);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("打印失败：" + ex.Message, ex);
            }
        }

        private static string SafeGetPrinterName(PrintDialog dialog)
        {
            try
            {
                return dialog.PrintQueue != null ? dialog.PrintQueue.FullName : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int SafeGetCopies(PrintDialog dialog)
        {
            try
            {
                return dialog.PrintTicket != null ? dialog.PrintTicket.CopyCount ?? 1 : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        /// <summary>尽量从驱动查询硬边距；失败返回 null。</summary>
        private static Thickness? TryGetHardMargin(PrintDialog dialog)
        {
            try
            {
                PrintQueue queue = dialog.PrintQueue;
                if (queue == null)
                {
                    return null;
                }

                PrintCapabilities capabilities = queue.GetPrintCapabilities();

                if (capabilities == null)
                {
                    return null;
                }

                // PrintCapabilities 在不同驱动上字段可用性差异较大，
                // 只有全部取到才采用，否则交给调用方兜底。
                double left = capabilities.PageImageableArea != null
                    ? capabilities.PageImageableArea.OriginWidth
                    : double.NaN;

                double top = capabilities.PageImageableArea != null
                    ? capabilities.PageImageableArea.OriginHeight
                    : double.NaN;

                if (double.IsNaN(left) || double.IsNaN(top))
                {
                    return null;
                }

                // 对称假设：多数打印机左右/上下边距一致
                return new Thickness(left, top, left, top);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
