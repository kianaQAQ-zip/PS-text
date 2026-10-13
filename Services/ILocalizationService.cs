using System.Collections.Generic;

namespace PSText.Services
{
    /// <summary>
    /// 界面语言。
    ///
    /// 设计要点（也是这一版 i18n 的全部设计）：
    ///
    ///   1. **查不到就返回键名本身**，绝不返回空串。少一个翻译时用户看到的是一串
    ///      英文键名（"MissingKey" 这种）——难看但能一眼看出是漏翻，
    ///      而返回空串会变成"某个按钮莫名其妙不见了"，那才难查。
    ///   2. **记录所有没查到的键**，供自检断言"这次跑过的流程里没有任何漏翻"。
    ///      这条让"完整迁移"从一个承诺变成可验证的事实。
    ///   3. **取值走索引器**，配合 WPF 绑定就能在运行时切换语言而不重启：
    ///      标记扩展返回的是一个绑定，切换时统一发一次属性变更即可。
    /// </summary>
    public interface ILocalizationService
    {
        /// <summary>当前语言（"zh-CN" / "en-US"）。</summary>
        string CurrentCulture { get; }

        /// <summary>可选语言列表（代码 + 显示名）。</summary>
        IList<LanguageOption> AvailableLanguages { get; }

        /// <summary>取翻译文本；查不到时返回键名本身。</summary>
        /// <param name="key">文案键。</param>
        string this[string key] { get; }

        /// <summary>本次运行中所有没查到的键（自检用）。</summary>
        IList<string> MissingKeys { get; }

        /// <summary>语言变化通知（界面据此刷新）。</summary>
        event System.EventHandler LanguageChanged;

        /// <summary>切换语言。</summary>
        void SetLanguage(string culture);

        /// <summary>清空"没查到的键"记录（自检在检查前调用）。</summary>
        void ResetMissingKeys();
    }

    /// <summary>一个可选语言。</summary>
    public sealed class LanguageOption
    {
        public LanguageOption(string culture, string displayName)
        {
            Culture = culture;
            DisplayName = displayName;
        }

        /// <summary>语言代码，同时是资源字典的键。</summary>
        public string Culture { get; private set; }

        /// <summary>在下拉框里显示的名字（用该语言自己写，例如"English"、"中文"）。</summary>
        public string DisplayName { get; private set; }
    }
}
