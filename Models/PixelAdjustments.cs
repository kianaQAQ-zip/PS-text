using System;

namespace PSText.Models
{
    /// <summary>
    /// 基础调整参数（不可变）。
    ///
    /// 参数语义（均以“中性值”为原点，便于界面滑块直接绑定）：
    ///   Brightness  -100 ~ +100，0 为不变
    ///   Contrast    -100 ~ +100，0 为不变
    ///   Saturation  -100 ~ +100，0 为不变（-100 等同灰度）
    ///   Temperature -100 ~ +100，0 为不变（负值偏冷 / 蓝，正值偏暖 / 红）
    /// </summary>
    public sealed class PixelAdjustments : IEquatable<PixelAdjustments>
    {
        /// <summary>中性参数（不影响画面）。</summary>
        public static readonly PixelAdjustments Neutral = new PixelAdjustments(0, 0, 0, 0);

        public PixelAdjustments(double brightness, double contrast, double saturation, double temperature)
        {
            Brightness = Clamp(brightness);
            Contrast = Clamp(contrast);
            Saturation = Clamp(saturation);
            Temperature = Clamp(temperature);
        }

        /// <summary>亮度，-100 ~ +100。</summary>
        public double Brightness { get; private set; }

        /// <summary>对比度，-100 ~ +100。</summary>
        public double Contrast { get; private set; }

        /// <summary>饱和度，-100 ~ +100。</summary>
        public double Saturation { get; private set; }

        /// <summary>色温，-100 ~ +100。</summary>
        public double Temperature { get; private set; }

        /// <summary>是否为中性参数（无需处理）。</summary>
        public bool IsNeutral
        {
            get
            {
                return Math.Abs(Brightness) < 0.001
                       && Math.Abs(Contrast) < 0.001
                       && Math.Abs(Saturation) < 0.001
                       && Math.Abs(Temperature) < 0.001;
            }
        }

        /// <summary>按字段创建新实例（用于界面逐个调整）。</summary>
        public PixelAdjustments With(
            double? brightness = null,
            double? contrast = null,
            double? saturation = null,
            double? temperature = null)
        {
            return new PixelAdjustments(
                brightness ?? Brightness,
                contrast ?? Contrast,
                saturation ?? Saturation,
                temperature ?? Temperature);
        }

        /// <summary>用于状态栏 / 历史记录的简短描述。</summary>
        public string ToDisplayString()
        {
            if (IsNeutral)
            {
                return "无调整";
            }

            return string.Format(
                "亮度 {0:0} / 对比度 {1:0} / 饱和度 {2:0} / 色温 {3:0}",
                Brightness,
                Contrast,
                Saturation,
                Temperature);
        }

        public bool Equals(PixelAdjustments other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return Math.Abs(Brightness - other.Brightness) < 0.001
                   && Math.Abs(Contrast - other.Contrast) < 0.001
                   && Math.Abs(Saturation - other.Saturation) < 0.001
                   && Math.Abs(Temperature - other.Temperature) < 0.001;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PixelAdjustments);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Brightness.GetHashCode();
                hash = (hash * 397) ^ Contrast.GetHashCode();
                hash = (hash * 397) ^ Saturation.GetHashCode();
                hash = (hash * 397) ^ Temperature.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return ToDisplayString();
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value))
            {
                return 0.0;
            }

            if (value < -100.0)
            {
                return -100.0;
            }

            return value > 100.0 ? 100.0 : value;
        }
    }
}
