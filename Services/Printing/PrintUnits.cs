using System;

namespace PSText.Services.Printing
{
    /// <summary>
    /// 打印相关的单位换算。
    ///
    /// 涉及三套单位，混淆会导致打印尺寸错误或模糊，这里集中处理：
    ///   * 像素（px）      —— 图像原始像素
    ///   * DIP（设备无关单位）—— WPF 的布局单位，1 DIP = 1/96 英寸
    ///   * 毫米（mm）      —— 纸张尺寸用毫米最直观
    ///
    /// 关键点：把“图像像素”换算成 DIP 必须用**图像自身的 DPI**（图像 DPI ÷ 96），
    /// 这样 300 DPI 的扫描件在纸上的物理尺寸才正确，而不是被当成 96 DPI 放大 3 倍。
    /// </summary>
    public static class PrintUnits
    {
        /// <summary>每英寸的 DIP 数（WPF 约定）。</summary>
        public const double DipsPerInch = 96.0;

        /// <summary>每英寸的毫米数。</summary>
        public const double MillimetresPerInch = 25.4;

        /// <summary>毫米 → DIP。</summary>
        public static double MillimetresToDips(double millimetres)
        {
            return millimetres / MillimetresPerInch * DipsPerInch;
        }

        /// <summary>DIP → 毫米。</summary>
        public static double DipsToMillimetres(double dips)
        {
            return dips / DipsPerInch * MillimetresPerInch;
        }

        /// <summary>英寸 → DIP。</summary>
        public static double InchesToDips(double inches)
        {
            return inches * DipsPerInch;
        }

        /// <summary>DIP → 英寸。</summary>
        public static double DipsToInches(double dips)
        {
            return dips / DipsPerInch;
        }

        /// <summary>
        /// 图像像素 → DIP（使用图像自身 DPI）。
        /// 例如 300 DPI 图像的 300 像素等于 1 英寸 = 96 DIP。
        /// </summary>
        public static double PixelsToDips(double pixels, double imageDpi)
        {
            double dpi = imageDpi > 0.5 && !double.IsNaN(imageDpi) ? imageDpi : DipsPerInch;
            return pixels / dpi * DipsPerInch;
        }

        /// <summary>图像 DPI → 每 DIP 的像素数（用于判断打印清晰度）。</summary>
        public static double PixelsPerDip(double imageDpi)
        {
            double dpi = imageDpi > 0.5 && !double.IsNaN(imageDpi) ? imageDpi : DipsPerInch;
            return dpi / DipsPerInch;
        }

        /// <summary>
        /// 计算打印输出的横向有效分辨率（每英寸像素数）。
        /// 用于给用户提示“是否会被放大导致模糊”。
        /// </summary>
        /// <param name="imagePixelWidth">图像像素宽度。</param>
        /// <param name="printedWidthDips">打印到纸上的宽度（DIP）。</param>
        public static double EffectiveDpi(int imagePixelWidth, double printedWidthDips)
        {
            if (printedWidthDips <= 0.001)
            {
                return 0.0;
            }

            return imagePixelWidth / DipsToInches(printedWidthDips);
        }
    }
}
