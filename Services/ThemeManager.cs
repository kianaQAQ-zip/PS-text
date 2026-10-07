using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PSText.Services
{
    /// <summary>应用主题（实际生效的配色）。</summary>
    public enum AppTheme
    {
        Light,
        Dark
    }

    /// <summary>
    /// 主题与通用资源管理（需求 P3-13）。
    ///
    /// 重要说明（踩坑记录）：
    ///   Application.Resources.MergedDictionaries 中合并的字典，在窗口 BAML 解析阶段
    ///   通过 StaticResource 查找并不可靠（实测会抛“找不到名为 Xxx 的资源”）。
    ///   因此这里在创建窗口之前，用代码把“通用样式字典 + 主题字典”合并进
    ///   Application.Resources；经验证这样窗口解析阶段的 StaticResource 可以正常命中。
    ///
    /// 主题切换在运行时生效：所有控件通过 DynamicResource 引用配色键，
    /// 切换时先移除旧主题字典再加入新字典，界面会立即刷新。
    ///
    /// 若将来接入 MaterialDesign，只需在 <see cref="Apply"/> 里额外合并其
    /// BundledTheme 字典（Design 令牌键名保持一致即可），本类接口无需改动。
    /// </summary>
    public static class ThemeManager
    {
        private const string BaseSource = "pack://application:,,,/PS-text;component/Resources/Theme.xaml";
        private const string LightSource = "pack://application:,,,/PS-text;component/Resources/Theme.Light.xaml";
        private const string DarkSource = "pack://application:,,,/PS-text;component/Resources/Theme.Dark.xaml";

        /// <summary>Windows 10 1809+ 的“应用主题”注册表项。</summary>
        private const string PersonalizeKeyPath =
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const string AppsUseLightThemeValue = "AppsUseLightTheme";

        private static readonly object SyncRoot = new object();
        private static readonly List<ResourceDictionary> ActiveDictionaries = new List<ResourceDictionary>();

        /// <summary>当前实际生效的主题。</summary>
        public static AppTheme CurrentTheme { get; private set; }

        /// <summary>当前主题偏好（可能为“跟随系统”）。</summary>
        public static ThemePreference Preference { get; private set; }

        /// <summary>主题发生变化（供界面刷新单选状态）。</summary>
        public static event EventHandler ThemeChanged;

        static ThemeManager()
        {
            CurrentTheme = AppTheme.Light;
            Preference = ThemePreference.FollowSystem;
        }

        /// <summary>
        /// 应用主题（按偏好解析出实际主题）。必须在使用样式的窗口创建之前调用一次。
        /// </summary>
        public static void Apply(Application application, ThemePreference preference)
        {
            Preference = preference;
            ApplyResolved(application, ResolveTheme(preference));
        }

        /// <summary>兼容旧调用：直接指定具体主题。</summary>
        public static void Apply(Application application, AppTheme theme)
        {
            Preference = theme == AppTheme.Dark ? ThemePreference.Dark : ThemePreference.Light;
            ApplyResolved(application, theme);
        }

        /// <summary>切换主题偏好并立即生效（运行时可调用）。</summary>
        public static void SetPreference(Application application, ThemePreference preference)
        {
            Preference = preference;
            ApplyResolved(application ?? Application.Current, ResolveTheme(preference));
        }

        /// <summary>把“偏好”解析为“实际主题”。</summary>
        public static AppTheme ResolveTheme(ThemePreference preference)
        {
            switch (preference)
            {
                case ThemePreference.Dark:
                    return AppTheme.Dark;
                case ThemePreference.Light:
                    return AppTheme.Light;
                default:
                    return GetSystemTheme();
            }
        }

        /// <summary>
        /// 读取系统主题偏好。
        /// Windows 10 1809+ 有 AppsUseLightTheme；更早的系统（含 Win7）没有该值，
        /// 此时按浅色处理（Win7 的窗口配色由经典主题控制，无法可靠判定“深色模式”）。
        /// </summary>
        public static AppTheme GetSystemTheme()
        {
            try
            {
                object value = Registry.GetValue(PersonalizeKeyPath, AppsUseLightThemeValue, null);

                if (value is int)
                {
                    return (int)value == 0 ? AppTheme.Dark : AppTheme.Light;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ThemeManager] 读取系统主题失败，按浅色处理: " + ex.Message);
            }

            return AppTheme.Light;
        }

        /// <summary>列出可选偏好（供界面绑定）。</summary>
        public static IReadOnlyList<ThemePreference> GetAvailablePreferences()
        {
            return new[] { ThemePreference.FollowSystem, ThemePreference.Light, ThemePreference.Dark };
        }

        /// <summary>主题偏好的中文显示名。</summary>
        public static string GetPreferenceDisplayName(ThemePreference preference)
        {
            switch (preference)
            {
                case ThemePreference.Dark:
                    return "深色";
                case ThemePreference.Light:
                    return "浅色";
                default:
                    return "跟随系统";
            }
        }

        /// <summary>实际主题的中文显示名。</summary>
        public static string GetThemeDisplayName(AppTheme theme)
        {
            return theme == AppTheme.Dark ? "深色" : "浅色";
        }

        /// <summary>
        /// 取“自定义主题字典”里的某个画刷颜色。
        /// 注意：不要用 Application.TryFindResource，因为合并字典的先后顺序会让它命中的
        /// 可能是另一套主题的同名键；这里显式从当前生效的那本字典里取。
        /// </summary>
        public static bool TryGetThemeBrushColor(string key, out Color color)
        {
            color = Colors.Transparent;

            lock (SyncRoot)
            {
                System.Windows.Application current = System.Windows.Application.Current;

                if (current == null)
                {
                    return false;
                }

                // ActiveDictionaries 的最后一项是主题配色字典（通用样式在前）
                for (int i = ActiveDictionaries.Count - 1; i >= 0; i--)
                {
                    ResourceDictionary dictionary = ActiveDictionaries[i];

                    if (dictionary.Contains(key))
                    {
                        SolidColorBrush brush = dictionary[key] as SolidColorBrush;

                        if (brush != null)
                        {
                            color = brush.Color;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 校验某一个颜色键在“当前主题字典”与“应用解析结果”两处是否一致。
        /// 用于发现“键只在一套主题里定义”或“控件内写死了色值”这类问题。
        /// </summary>
        public static bool IsTokenResolvedConsistently(string key)
        {
            Color expected;

            if (!TryGetThemeBrushColor(key, out expected))
            {
                return false;
            }

            System.Windows.Application current = System.Windows.Application.Current;

            if (current == null)
            {
                return false;
            }

            SolidColorBrush resolved = current.TryFindResource(key) as SolidColorBrush;

            return resolved != null && resolved.Color == expected;
        }

        /// <summary>读取画布背景色（主题相关），失败时返回中性灰。</summary>
        public static Color GetCanvasBackgroundColor()
        {
            try
            {
                Application current = Application.Current;

                if (current != null)
                {
                    SolidColorBrush brush = current.TryFindResource("CanvasBackgroundBrush") as SolidColorBrush;

                    if (brush != null)
                    {
                        return brush.Color;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略：视为未定义。
            }

            return Color.FromRgb(0x2B, 0x2B, 0x2B);
        }

        /// <summary>持久化主题选择（实际写入由 AppSettings 负责，这里保留兼容入口）。</summary>
        public static void Persist(Application application)
        {
            // 设置的实际持久化在 MainViewModel / App 中通过 ISettingsService 完成。
        }

        private static void ApplyResolved(Application application, AppTheme theme)
        {
            Application target = application ?? Application.Current;

            if (target == null)
            {
                CurrentTheme = theme;
                return;
            }

            lock (SyncRoot)
            {
                // 先移除上一次合并的字典，避免资源重复定义（后加入的同名资源会覆盖先加入的）。
                for (int i = ActiveDictionaries.Count - 1; i >= 0; i--)
                {
                    target.Resources.MergedDictionaries.Remove(ActiveDictionaries[i]);
                }

                ActiveDictionaries.Clear();

                // 顺序很重要：通用样式在前，主题配色在后（配色字典优先）。
                Merge(target, BaseSource);
                Merge(target, theme == AppTheme.Dark ? DarkSource : LightSource);

                CurrentTheme = theme;
            }

            EventHandler handler = ThemeChanged;

            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        private static void Merge(Application target, string source)
        {
            ResourceDictionary dictionary = TryLoad(source);

            if (dictionary == null)
            {
                return;
            }

            target.Resources.MergedDictionaries.Add(dictionary);
            ActiveDictionaries.Add(dictionary);
        }

        private static ResourceDictionary TryLoad(string source)
        {
            try
            {
                return new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ThemeManager] 主题加载失败 " + source + ": " + ex.Message);
                return null;
            }
        }
    }
}
