using System;
using System.Windows.Data;
using System.Windows.Markup;
using PSText.Services;

namespace PSText.Infrastructure.Localization
{
    /// <summary>
    /// XAML 里的取文案标记扩展：<c>Text="{loc:Tr OpenImage}"</c>。
    ///
    /// 关键点：它**返回的不是字符串，而是一个绑定**。
    /// 直接返回字符串的话，值在 XAML 加载时就被固定下来，运行时切换语言界面不会变；
    /// 返回绑定之后，语言一变、服务发一次索引器变更通知，所有用了它的地方一起刷新。
    ///
    /// 这也是不采用 `{x:Static}` 绑 .resx 的原因 —— 那条路只能在启动时定死语言。
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TranslateExtension : MarkupExtension
    {
        /// <summary>
        /// 只留这一个构造函数。
        ///
        /// 之前同时留了无参版本，结果是 <c>{loc:Tr OpenImage}</c> 里的位置参数没能绑到 Key 上
        /// （解析器选了无参构造），Key 为 null，XAML 编译期报"键不能为 null"。
        /// 标记扩展用位置参数时，必须只有一个可匹配的构造函数。
        /// </summary>
        public TranslateExtension(string key)
        {
            Key = key;
        }

        /// <summary>文案键。</summary>
        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
            {
                return string.Empty;
            }

            Binding binding = new Binding
            {
                Path = new System.Windows.PropertyPath("[" + Key + "]"),
                Source = LocalizationService.Instance,
                Mode = BindingMode.OneWay
            };

            return binding.ProvideValue(serviceProvider);
        }
    }
}
