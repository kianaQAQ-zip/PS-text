using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace PSText.Services
{
    /// <summary>
    /// 基于资源字典的本地化实现。
    ///
    /// 为什么用 <see cref="ResourceDictionary"/> 而不是 .resx：
    ///   * WPF 的 `{x:Static}` 绑 resx 只在加载时求值一次，**切换语言不会刷新界面**；
    ///     要做到运行时切换，本来就得走"可通知的索引器 + 标记扩展"这条路；
    ///   * 资源字典是纯 XAML，改文案不需要重新生成代码，diff 也友好；
    ///   * 零依赖（resx 也行，但要多一层设计器文件）。
    ///
    /// 字典放在 Resources/Strings.<culture>.xaml，键是英文语义名。
    /// </summary>
    public sealed class LocalizationService : ILocalizationService, INotifyPropertyChanged
    {
        /// <summary>默认语言。</summary>
        public const string DefaultCulture = "zh-CN";

        private readonly List<string> _missingKeys = new List<string>();
        private readonly List<LanguageOption> _languages = new List<LanguageOption>();
        private readonly Dictionary<string, ResourceDictionary> _cache =
            new Dictionary<string, ResourceDictionary>(StringComparer.OrdinalIgnoreCase);

        private string _currentCulture = DefaultCulture;

        public LocalizationService()
        {
            _languages.Add(new LanguageOption("zh-CN", "中文（简体）"));
            _languages.Add(new LanguageOption("en-US", "English"));
        }

        /// <summary>进程内共享的实例（界面与 ViewModel 都用它，避免把接口一路透传下去）。</summary>
        public static LocalizationService Instance { get; } = new LocalizationService();

        private readonly object _syncRoot = new object();
        private ResourceDictionary _appliedDictionary;

        /// <summary>
        /// 把当前语言的字典挂到应用资源里 —— 这是 XAML 里 <c>{DynamicResource Xxx}</c> 生效的前提。
        ///
        /// 用 DynamicResource 而不是绑定：切语言时只要把整本字典换掉，
        /// 所有引用它的地方会自动刷新。这是 WPF 本地化的正统做法，
        /// 不需要每个界面元素都持一个可通知对象。
        ///
        /// 没有 Application 时（无界面自检）静默跳过：那时界面本来就不存在。
        /// </summary>
        public void ApplyResources(Application application)
        {
            Application target = application ?? Application.Current;

            if (target == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                // 先摘掉上一次挂的字典，避免同名资源重复定义（后加入的会覆盖先加入的）。
                if (_appliedDictionary != null)
                {
                    target.Resources.MergedDictionaries.Remove(_appliedDictionary);
                    _appliedDictionary = null;
                }

                ResourceDictionary dictionary = GetDictionary(_currentCulture);

                if (dictionary != null)
                {
                    target.Resources.MergedDictionaries.Add(dictionary);
                    _appliedDictionary = dictionary;
                }
            }
        }

        public string CurrentCulture
        {
            get { return _currentCulture; }
        }

        public IList<LanguageOption> AvailableLanguages
        {
            get { return _languages; }
        }

        public IList<string> MissingKeys
        {
            get { return _missingKeys; }
        }

        public event EventHandler LanguageChanged;

        /// <summary>
        /// 索引器变更通知。
        ///
        /// 名字必须是 <c>Item[]</c>（WPF 约定的索引器属性名）——
        /// XAML 里的 <c>{loc:Tr X}</c> 绑的是 <c>[X]</c> 这个路径，
        /// 发这个通知才能让所有取文案的地方一起刷新。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        public string this[string key]
        {
            get { return Lookup(key); }
        }

        /// <summary>
        /// 取文案。查不到时：记一笔 + 返回键名本身。
        ///
        /// 返回键名而不是空串是刻意的：空串会变成"界面上某个按钮不见了"，
        /// 而键名至少能让人一眼看出是漏翻，也方便照着键去字典里补。
        /// </summary>
        public string Lookup(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            ResourceDictionary dictionary = GetDictionary(_currentCulture);

            if (dictionary != null && dictionary.Contains(key))
            {
                string value = dictionary[key] as string;

                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            // 当前语言没有时退回默认语言：英文版少翻一条，也不该让用户看到键名。
            if (!string.Equals(_currentCulture, DefaultCulture, StringComparison.OrdinalIgnoreCase))
            {
                ResourceDictionary fallback = GetDictionary(DefaultCulture);

                if (fallback != null && fallback.Contains(key))
                {
                    string value = fallback[key] as string;

                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }

            if (!_missingKeys.Contains(key))
            {
                _missingKeys.Add(key);
            }

            return key;
        }

        public void SetLanguage(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
            {
                culture = DefaultCulture;
            }

            if (string.Equals(culture, _currentCulture, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentCulture = culture;

            try
            {
                // 让 WPF 的日期 / 数字格式也跟着切（不然英文界面里会出现中文日期）
                CultureInfo cultureInfo = CultureInfo.GetCultureInfo(culture);
                CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
                CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
                System.Threading.Thread.CurrentThread.CurrentCulture = cultureInfo;
                System.Threading.Thread.CurrentThread.CurrentUICulture = cultureInfo;
            }
            catch (Exception)
            {
            }

            EventHandler handler = LanguageChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            PropertyChangedEventHandler propertyHandler = PropertyChanged;

            if (propertyHandler != null)
            {
                propertyHandler(this, new PropertyChangedEventArgs("Item[]"));
            }

            // XAML 侧靠换字典刷新，所以这里必须跟着换一次
            ApplyResources(null);
        }

        public void ResetMissingKeys()
        {
            _missingKeys.Clear();
        }

        /// <summary>某种语言的字典里有多少条文案（自检用）。</summary>
        public int GetKeyCount(string culture)
        {
            ResourceDictionary dictionary = GetDictionary(culture);
            return dictionary == null ? 0 : dictionary.Count;
        }

        /// <summary>某种语言的全部键（自检用来比对两套字典是否一一对应）。</summary>
        public IList<string> GetKeys(string culture)
        {
            List<string> keys = new List<string>();
            ResourceDictionary dictionary = GetDictionary(culture);

            if (dictionary == null)
            {
                return keys;
            }

            foreach (object key in dictionary.Keys)
            {
                string text = key as string;

                if (text != null)
                {
                    keys.Add(text);
                }
            }

            return keys;
        }

        private ResourceDictionary GetDictionary(string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                return null;
            }

            ResourceDictionary cached;

            if (_cache.TryGetValue(culture, out cached))
            {
                return cached;
            }

            ResourceDictionary loaded = null;

            try
            {
                Uri uri = new Uri(
                    "pack://application:,,,/PS-text;component/Resources/Strings." + culture + ".xaml",
                    UriKind.Absolute);

                loaded = new ResourceDictionary { Source = uri };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LocalizationService] 载入语言字典失败 " + culture + ": " + ex.Message);
            }

            _cache[culture] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// 全局取文案的快捷入口。
    ///
    /// 这里用静态门面而不是把 <see cref="ILocalizationService"/> 注入到每个类：
    /// 本地化是**进程级**的关注点（和主题一样），逐层透传只会让十几个构造函数
    /// 都多一个参数、几百处调用点都要改，收益为零。
    /// 项目里 <c>ThemeManager</c> 已经是同样的先例。
    /// </summary>
    public static class Loc
    {
        /// <summary>按当前语言取文案。</summary>
        public static string T(string key)
        {
            return LocalizationService.Instance.Lookup(key);
        }

        /// <summary>按当前语言取文案并做一次格式化。</summary>
        public static string T(string key, params object[] args)
        {
            string format = LocalizationService.Instance.Lookup(key);

            if (args == null || args.Length == 0)
            {
                return format;
            }

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                // 翻译里漏了占位符时不要让界面崩，把原文交出去即可。
                return format;
            }
        }
    }
}
