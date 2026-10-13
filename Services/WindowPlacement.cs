using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace PSText.Services
{
    /// <summary>
    /// 窗口位置的恢复与校验。
    ///
    /// 为什么需要"校验"这一步：显示器配置是会变的 —— 用户在家里用双屏、到公司用单屏，
    /// 保存下来的窗口坐标很可能落在**已经不存在的显示器**上。
    /// 那时窗口会出现在可视区域之外，用户看到的现象是"程序启动了但没界面"，
    /// 比不恢复位置糟糕得多。所以恢复之前必须先验证它还在不在屏幕上。
    ///
    /// 纯逻辑（<see cref="TryFit"/>）与系统调用（<see cref="GetWorkAreasInDips"/>）分开：
    /// 前者可以直接在自检里逐条钉住，不需要真的去接显示器。
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>窗口窄于 / 矮于这个尺寸就认为保存的值不可用（明显异常）。</summary>
        public const double MinimumWidth = 240.0;

        public const double MinimumHeight = 160.0;

        /// <summary>
        /// 把保存的窗口矩形收敛到某个显示器的工作区内。
        ///
        /// 规则（刻意简单可预测）：
        ///   * 与任何工作区都**没有重叠** ⇒ 返回 false（调用方应当回到主屏居中）；
        ///   * 与多个都有重叠 ⇒ 选重叠面积最大的那个（即"上次所在的那块屏"）；
        ///   * 比工作区还大 ⇒ 先缩到工作区尺寸；
        ///   * 最后整体夹进工作区 —— 这条同时保证了标题栏一定在屏幕内（能被拖动）。
        /// </summary>
        /// <param name="window">保存下来的窗口矩形（DIP）。</param>
        /// <param name="workAreas">各显示器的工作区（DIP）。</param>
        /// <param name="fitted">收敛后的矩形。</param>
        public static bool TryFit(Rect window, IList<Rect> workAreas, out Rect fitted)
        {
            fitted = window;

            if (workAreas == null || workAreas.Count == 0)
            {
                return false;
            }

            if (double.IsNaN(window.Width) || double.IsNaN(window.Height)
                || double.IsInfinity(window.Width) || double.IsInfinity(window.Height)
                || window.Width < MinimumWidth || window.Height < MinimumHeight)
            {
                return false;
            }

            Rect best = Rect.Empty;
            double bestOverlap = 0.0;

            for (int i = 0; i < workAreas.Count; i++)
            {
                Rect area = workAreas[i];

                if (area.IsEmpty || area.Width < 1.0 || area.Height < 1.0)
                {
                    continue;
                }

                Rect overlap = Rect.Intersect(window, area);

                if (overlap.IsEmpty)
                {
                    continue;
                }

                double size = overlap.Width * overlap.Height;

                if (size > bestOverlap)
                {
                    bestOverlap = size;
                    best = area;
                }
            }

            if (bestOverlap <= 0.0 || best.IsEmpty)
            {
                // 完全落在已经不存在的显示器上
                return false;
            }

            double width = Math.Min(window.Width, best.Width);
            double height = Math.Min(window.Height, best.Height);

            double left = Clamp(window.Left, best.Left, best.Right - width);
            double top = Clamp(window.Top, best.Top, best.Bottom - height);

            fitted = new Rect(left, top, width, height);
            return true;
        }

        /// <summary>取所有显示器的工作区（已折算成 WPF 的 DIP 坐标）。</summary>
        public static IList<Rect> GetWorkAreasInDips(Visual reference)
        {
            List<Rect> areas = new List<Rect>();
            List<RECT> raw = new List<RECT>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data)
                {
                    MONITORINFO info = new MONITORINFO();
                    info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

                    if (GetMonitorInfo(monitor, ref info))
                    {
                        raw.Add(info.rcWork);
                    }
                    else
                    {
                        raw.Add(rect);
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 枚举失败时退回"整个虚拟屏幕"—— 宁可粗一点，也不要无谓地放弃恢复位置。
                raw.Clear();
            }

            double scaleX = 1.0;
            double scaleY = 1.0;

            if (reference != null)
            {
                try
                {
                    PresentationSource source = PresentationSource.FromVisual(reference);

                    if (source != null && source.CompositionTarget != null)
                    {
                        scaleX = source.CompositionTarget.TransformToDevice.M11;
                        scaleY = source.CompositionTarget.TransformToDevice.M22;
                    }
                }
                catch (Exception)
                {
                }
            }

            if (scaleX <= 0.0001)
            {
                scaleX = 1.0;
            }

            if (scaleY <= 0.0001)
            {
                scaleY = 1.0;
            }

            for (int i = 0; i < raw.Count; i++)
            {
                // 显示器信息是物理像素，而 WPF 的窗口坐标是 DIP —— 不折算的话，
                // 在 150% 缩放的机器上会把窗口夹到屏幕左上角的一小块区域里。
                areas.Add(new Rect(
                    raw[i].Left / scaleX,
                    raw[i].Top / scaleY,
                    (raw[i].Right - raw[i].Left) / scaleX,
                    (raw[i].Bottom - raw[i].Top) / scaleY));
            }

            if (areas.Count == 0)
            {
                areas.Add(new Rect(
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth,
                    SystemParameters.VirtualScreenHeight));
            }

            return areas;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (maximum < minimum)
            {
                return minimum;
            }

            if (double.IsNaN(value) || value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
        }

        #region 系统调用

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        #endregion
    }
}
