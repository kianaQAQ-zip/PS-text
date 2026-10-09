using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace PSText.Infrastructure.Converters
{
    /// <summary>
    /// bool → Visibility 转换器。
    /// 参数为 "Invert" 时反转逻辑；参数为 "Hidden" 时用 Hidden 代替 Collapsed。
    /// </summary>
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool && (bool)value;
            string option = parameter as string;

            if (!string.IsNullOrEmpty(option)
                && option.IndexOf("invert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                flag = !flag;
            }

            if (flag)
            {
                return Visibility.Visible;
            }

            bool useHidden = !string.IsNullOrEmpty(option)
                             && option.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0;

            return useHidden ? Visibility.Hidden : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isVisible = value is Visibility && (Visibility)value == Visibility.Visible;
            string option = parameter as string;

            if (!string.IsNullOrEmpty(option)
                && option.IndexOf("invert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isVisible = !isVisible;
            }

            return isVisible;
        }
    }

    /// <summary>
    /// 多值相乘，用于“容器尺寸 = 图片像素 × 缩放”。
    /// 任一输入为 NaN / Infinity 时返回 0，避免布局异常。
    /// </summary>
    public sealed class MultiplyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0)
            {
                return 0.0;
            }

            double product = 1.0;

            foreach (object value in values)
            {
                double number;

                try
                {
                    number = value == null ? 0.0 : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return 0.0;
                }

                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    return 0.0;
                }

                product *= number;
            }

            if (double.IsNaN(product) || double.IsInfinity(product) || product < 0.0)
            {
                return 0.0;
            }

            return product;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            // 单向绑定，无需反向转换。
            return null;
        }
    }

    /// <summary>
    /// 判断值是否等于参数（支持枚举与字符串），常用于“单选按钮 ↔ 枚举属性”的绑定。
    /// </summary>
    public sealed class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
            {
                return false;
            }

            string left = value.ToString();
            string right = parameter.ToString();

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 反向转换由命令处理，这里不实现（避免绑定回写把枚举改坏）。
            return System.Windows.Data.Binding.DoNothing;
        }
    }

    /// <summary>
    /// 光标名称 → <see cref="Cursor"/>。
    ///
    /// 为什么让 ViewModel 出"名字"而不是直接出 Cursor：光标属于控件外观，
    /// 按项目约定 code-behind 只转发手势、不设置外观，所以这里用一层转换
    /// 把"该显示什么光标"这个判断留在 ViewModel、把 WPF 类型挡在外面。
    /// 空名（没有手柄命中）统一显示为十字准星 —— 与标注模式的默认光标一致。
    /// </summary>
    public sealed class CursorNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string name = value as string;

            if (string.IsNullOrEmpty(name))
            {
                return Cursors.Cross;
            }

            switch (name)
            {
                case "SizeNWSE":
                    return Cursors.SizeNWSE;
                case "SizeNESW":
                    return Cursors.SizeNESW;
                case "SizeNS":
                    return Cursors.SizeNS;
                case "SizeWE":
                    return Cursors.SizeWE;
                default:
                    return Cursors.Cross;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return System.Windows.Data.Binding.DoNothing;
        }
    }

    /// <summary>非空字符串 → Visible（用于提示文本的显隐）。</summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = value as string;

            if (!string.IsNullOrWhiteSpace(text))
            {
                return Visibility.Visible;
            }

            string option = parameter as string;

            if (!string.IsNullOrEmpty(option)
                && option.IndexOf("invert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Visibility.Visible;
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 判断集合 / 数值是否为空，用于“最近文件为空”提示等场景。
    /// </summary>
    public sealed class IsEmptyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isEmpty;

            ICollection<object> collection = value as ICollection<object>;
            if (collection != null)
            {
                isEmpty = collection.Count == 0;
            }
            else
            {
                isEmpty = value == null || string.IsNullOrEmpty(value as string);
            }

            string option = parameter as string;
            if (!string.IsNullOrEmpty(option)
                && option.IndexOf("invert", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isEmpty = !isEmpty;
            }

            return isEmpty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
