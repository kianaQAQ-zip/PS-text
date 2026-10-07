using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace PSText.Services
{
    /// <summary>主题偏好（含“跟随系统”）。</summary>
    public enum ThemePreference
    {
        /// <summary>跟随系统（Win10 1809+ 读取注册表；Win7 无系统主题信息时回退浅色）。</summary>
        FollowSystem = 0,

        Light,

        Dark
    }

    /// <summary>
    /// 应用设置（需求 P3-13 / P3-16）。
    ///
    /// 持久化到 %APPDATA%\PS-text\settings.xml：
    ///   * 用 XML 而不是 JSON —— .NET Framework 自带 XmlSerializer，零依赖；
    ///   * 读取失败（文件损坏 / 无权限）时静默回退默认值，绝不让启动崩溃。
    /// </summary>
    [Serializable]
    public sealed class AppSettings
    {
        /// <summary>最近文件列表上限（需求要求 10 个）。</summary>
        public const int MaxRecentFiles = 10;

        public AppSettings()
        {
            ThemePreference = ThemePreference.FollowSystem;
            RecentFiles = new List<string>();
            UndoDepth = 30;
        }

        /// <summary>主题偏好。</summary>
        public ThemePreference ThemePreference { get; set; }

        /// <summary>最近打开的文件（最新的在最前）。</summary>
        public List<string> RecentFiles { get; set; }

        /// <summary>撤销步数上限。</summary>
        public int UndoDepth { get; set; }

        /// <summary>把路径加入最近文件列表：去重、置顶、限长。</summary>
        public void AddRecentFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            if (RecentFiles == null)
            {
                RecentFiles = new List<string>();
            }

            // 去重（不区分大小写，Windows 路径语义）
            for (int i = RecentFiles.Count - 1; i >= 0; i--)
            {
                if (string.Equals(RecentFiles[i], filePath, StringComparison.OrdinalIgnoreCase))
                {
                    RecentFiles.RemoveAt(i);
                }
            }

            RecentFiles.Insert(0, filePath);

            while (RecentFiles.Count > MaxRecentFiles)
            {
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }
        }

        /// <summary>移除一个最近文件（文件已被删除时使用）。</summary>
        public void RemoveRecentFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || RecentFiles == null)
            {
                return;
            }

            for (int i = RecentFiles.Count - 1; i >= 0; i--)
            {
                if (string.Equals(RecentFiles[i], filePath, StringComparison.OrdinalIgnoreCase))
                {
                    RecentFiles.RemoveAt(i);
                }
            }
        }

        /// <summary>复制一份（避免外部修改内部状态）。</summary>
        public AppSettings Clone()
        {
            AppSettings copy = new AppSettings
            {
                ThemePreference = ThemePreference,
                UndoDepth = UndoDepth,
                RecentFiles = new List<string>()
            };

            if (RecentFiles != null)
            {
                copy.RecentFiles.AddRange(RecentFiles);
            }

            return copy;
        }
    }

    /// <summary>设置存储抽象（便于测试时替换为内存实现）。</summary>
    public interface ISettingsService
    {
        /// <summary>读取设置（失败时返回默认值，不抛异常）。</summary>
        AppSettings Load();

        /// <summary>保存设置（失败时静默忽略，不抛异常）。</summary>
        void Save(AppSettings settings);

        /// <summary>设置文件的完整路径（显示 / 诊断用）。</summary>
        string SettingsFilePath { get; }
    }

    /// <summary>
    /// 基于 XML 文件的设置存储。
    ///
    /// 写入策略：先尝试默认位置（%APPDATA%\PS-text），
    /// 失败（受限环境 / 无权限）时回退到临时目录，保证“设置能存下来”这件事本身不被环境破坏。
    /// </summary>
    public sealed class XmlSettingsService : ISettingsService
    {
        private readonly object _syncRoot = new object();

        /// <summary>当前实际使用的文件路径（可能在第一次保存失败后发生回退）。</summary>
        private string _filePath;

        /// <summary>
        /// 是否启用“位置探针”。仅默认路径（正式运行）启用；
        /// 显式指定路径（测试 / 自检）不写探针，避免污染真实运行时的首选位置。
        /// </summary>
        private readonly bool _useLocationProbe;

        public XmlSettingsService()
            : this(ResolveInitialPath(), true)
        {
        }

        public XmlSettingsService(string filePath)
            : this(filePath, false)
        {
        }

        private XmlSettingsService(string filePath, bool useLocationProbe)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("设置文件路径不能为空。", "filePath");
            }

            _filePath = filePath;
            _useLocationProbe = useLocationProbe;
        }

        /// <summary>默认路径：%APPDATA%\PS-text\settings.xml（目录不可写时退回临时目录）。</summary>
        public static string DefaultFilePath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                if (!string.IsNullOrWhiteSpace(appData))
                {
                    return Path.Combine(Path.Combine(appData, "PS-text"), "settings.xml");
                }
            }
            catch (Exception)
            {
                // GetFolderPath 在极少数环境下会抛异常
            }

            return FallbackFilePath();
        }

        /// <summary>
        /// 回退路径的“探针”文件：记录上次实际写入成功的位置。
        ///
        /// 为什么需要它：如果只回退到临时目录而不记住，下次启动又会先去读默认位置、
        /// 读不到就回退成默认值 —— 用户的主题/最近文件会被静默重置（曾踩此坑）。
        /// </summary>
        private const string ProbeFileName = "PS-text-settings.location";

        /// <summary>回退路径（临时目录）。</summary>
        public static string FallbackFilePath()
        {
            return Path.Combine(Path.GetTempPath(), "PS-text-settings.xml");
        }

        /// <summary>探针文件路径。</summary>
        public static string ProbeFilePath()
        {
            return Path.Combine(Path.GetTempPath(), ProbeFileName);
        }

        /// <summary>
        /// 解析首选文件路径：优先使用探针记录的位置（上次写入成功的位置），
        /// 否则用默认位置。
        /// </summary>
        public static string ResolveInitialPath()
        {
            try
            {
                string probe = ProbeFilePath();

                if (File.Exists(probe))
                {
                    string recorded = File.ReadAllText(probe).Trim();

                    if (!string.IsNullOrWhiteSpace(recorded))
                    {
                        return recorded;
                    }
                }
            }
            catch (Exception)
            {
                // 探针不可读时退回默认位置
            }

            return DefaultFilePath();
        }

        /// <summary>记录实际生效的位置。</summary>
        private static void WriteProbe(string filePath)
        {
            try
            {
                File.WriteAllText(ProbeFilePath(), filePath);
            }
            catch (Exception)
            {
                // 探针写不进去不影响主流程
            }
        }

        public string SettingsFilePath
        {
            get { return _filePath; }
        }

        public AppSettings Load()
        {
            lock (_syncRoot)
            {
                try
                {
                    if (!File.Exists(_filePath))
                    {
                        return new AppSettings();
                    }

                    XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));

                    using (FileStream stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        object result = serializer.Deserialize(stream);
                        AppSettings settings = result as AppSettings;

                        if (settings == null)
                        {
                            return new AppSettings();
                        }

                        // 反序列化可能给出 null 列表
                        if (settings.RecentFiles == null)
                        {
                            settings.RecentFiles = new List<string>();
                        }

                        if (settings.UndoDepth <= 0)
                        {
                            settings.UndoDepth = 30;
                        }

                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    // 文件损坏 / 无权限 / XML 非法：回退默认值，绝不因设置读取失败而崩溃
                    System.Diagnostics.Debug.WriteLine("[XmlSettingsService] 读取设置失败，使用默认值: " + ex.Message);
                    return new AppSettings();
                }
            }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                // 先按当前路径写；失败则回退到临时目录再写一次。
                if (TryWrite(_filePath, settings))
                {
                    // 记录生效位置，保证下次启动能读到同一份设置
                    if (_useLocationProbe)
                    {
                        WriteProbe(_filePath);
                    }
                    return;
                }

                string fallback = FallbackFilePath();

                if (!string.Equals(fallback, _filePath, StringComparison.OrdinalIgnoreCase)
                    && TryWrite(fallback, settings))
                {
                    // 记录回退结果：下次启动才会直接读这里，否则用户设置会被静默重置
                    _filePath = fallback;

                    if (_useLocationProbe)
                    {
                        WriteProbe(fallback);
                    }
                    return;
                }

            }
        }

        /// <summary>尝试把设置写到指定文件；成功返回 true，失败不抛异常。</summary>
        private static bool TryWrite(string filePath, AppSettings settings)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath);

                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));

                // 先写临时文件再替换：避免写入中途失败把原设置截断成损坏文件
                string tempPath = filePath + ".tmp";

                using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, settings);
                    stream.Flush();
                }

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                File.Move(tempPath, filePath);
                return File.Exists(filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[XmlSettingsService] 写入设置失败 " + filePath + ": " + ex.Message);
                return false;
            }
        }
    }
}
