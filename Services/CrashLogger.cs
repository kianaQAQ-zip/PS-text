using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace PSText.Services
{
    /// <summary>
    /// 崩溃日志落盘。
    ///
    /// 为什么需要它：全局异常兜底原本只写 Debug.WriteLine，而 Debug 输出在 Release 下
    /// 完全不可见 —— 用户遇到“点了就闪退”时，我们手上没有任何可分析的线索。
    ///
    /// 位置策略（沿用 XmlSettingsService 的回退思路）：
    ///   1. 首选 %LOCALAPPDATA%\PS-text\logs\crash-yyyyMMdd.log；
    ///   2. 目录不可写（受限环境 / 无权限）时回退 %TEMP%\PS-text-logs\。
    /// 只追加不覆盖；单文件超过 1MB 时轮转一份 .1 备份，避免日志无限膨胀。
    /// 全程不抛异常：写日志失败绝不能让原本的错误处理再崩一次。
    /// </summary>
    public static class CrashLogger
    {
        /// <summary>单个日志文件的体积上限（超出后轮转为 .1）。</summary>
        private const long MaxBytes = 1024 * 1024;

        private static readonly object SyncRoot = new object();

        /// <summary>首次解析成功后缓存，避免每次写日志都去试探目录权限。</summary>
        private static string _resolvedDirectory;

        /// <summary>当前生效的日志目录（显示给用户 / 诊断用）。</summary>
        public static string LogDirectory
        {
            get { return ResolveDirectory(); }
        }

        /// <summary>
        /// 记录一条崩溃日志。返回实际写入的文件路径；连日志都写不了时返回 null。
        /// </summary>
        public static string Log(string message, Exception exception)
        {
            string directory = ResolveDirectory();

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            string fileName = "crash-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
            string filePath = Path.Combine(directory, fileName);

            try
            {
                lock (SyncRoot)
                {
                    RotateIfTooLarge(filePath);
                    File.AppendAllText(filePath, BuildEntry(message, exception), Encoding.UTF8);
                }

                return filePath;
            }
            catch (Exception)
            {
                // 日志本身写不进去时只能放弃，不能再抛。
                return null;
            }
        }

        /// <summary>拼装一条日志：时间 / 版本 / 运行环境 / 异常链 / 完整堆栈。</summary>
        private static string BuildEntry(string message, Exception exception)
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("================================================================");
            builder.Append("[时间] ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            builder.Append("[版本] ").AppendLine(GetVersionText());
            builder.Append("[环境] ").AppendLine(GetEnvironmentText());
            builder.Append("[消息] ").AppendLine(string.IsNullOrWhiteSpace(message) ? "(无)" : message);

            if (exception == null)
            {
                builder.AppendLine("[异常] (无异常对象)");
            }
            else
            {
                int depth = 0;
                Exception current = exception;

                while (current != null)
                {
                    builder.Append("[异常 ").Append(depth).Append("] ")
                           .Append(current.GetType().FullName).Append(": ")
                           .AppendLine(current.Message ?? string.Empty);
                    builder.AppendLine(current.StackTrace ?? "(无堆栈)");

                    current = current.InnerException;
                    depth++;
                }
            }

            builder.AppendLine();
            return builder.ToString();
        }

        private static string GetVersionText()
        {
            try
            {
                Assembly assembly = Assembly.GetEntryAssembly();

                if (assembly == null)
                {
                    return "(未知)";
                }

                AssemblyName name = assembly.GetName();
                return (name.Name ?? "PS-text") + " " + (name.Version == null ? "(无版本)" : name.Version.ToString());
            }
            catch (Exception)
            {
                return "(未知)";
            }
        }

        private static string GetEnvironmentText()
        {
            try
            {
                string bitness = Environment.Is64BitProcess ? "64 位进程" : "32 位进程";
                return "Framework " + Environment.Version
                       + " · " + bitness
                       + " · " + Environment.OSVersion.VersionString;
            }
            catch (Exception)
            {
                return "(未知)";
            }
        }

        /// <summary>超过上限时把当前文件轮转为 .1（覆盖上一份备份），保证日志不会无限增长。</summary>
        private static void RotateIfTooLarge(string filePath)
        {
            try
            {
                FileInfo info = new FileInfo(filePath);

                if (!info.Exists || info.Length <= MaxBytes)
                {
                    return;
                }

                string backup = filePath + ".1";

                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                File.Move(filePath, backup);
            }
            catch (Exception)
            {
                // 轮转失败不阻塞写入（继续追加到原文件即可）。
            }
        }

        /// <summary>
        /// 解析日志目录：优先用户本地 AppData，写不进去再退到临时目录。
        /// 解析结果会被缓存；两次都失败时返回 null。
        /// </summary>
        private static string ResolveDirectory()
        {
            if (!string.IsNullOrEmpty(_resolvedDirectory))
            {
                return _resolvedDirectory;
            }

            lock (SyncRoot)
            {
                if (!string.IsNullOrEmpty(_resolvedDirectory))
                {
                    return _resolvedDirectory;
                }

                string localAppData = null;

                try
                {
                    localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }
                catch (Exception)
                {
                    // 极少数环境下 GetFolderPath 会抛异常
                }

                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    string preferred = Path.Combine(Path.Combine(localAppData, "PS-text"), "logs");

                    if (EnsureWritable(preferred))
                    {
                        _resolvedDirectory = preferred;
                        return _resolvedDirectory;
                    }
                }

                try
                {
                    string fallback = Path.Combine(Path.GetTempPath(), "PS-text-logs");

                    if (EnsureWritable(fallback))
                    {
                        _resolvedDirectory = fallback;
                        return _resolvedDirectory;
                    }
                }
                catch (Exception)
                {
                    // 临时目录都拿不到就只能放弃
                }

                return null;
            }
        }

        /// <summary>确认目录存在且真的可写（只建目录不够，受限环境可能建了也写不进去）。</summary>
        private static bool EnsureWritable(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string probe = Path.Combine(directory, ".write-probe");

                using (FileStream stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }

                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
