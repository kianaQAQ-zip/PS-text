using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace PSText.Services
{
    /// <summary>
    /// 运行时环境探测结果（不可变）。
    /// </summary>
    public sealed class RuntimeEnvironmentInfo
    {
        public RuntimeEnvironmentInfo(
            bool probeSucceeded,
            int netRelease,
            string netVersionText,
            string clrVersionText,
            string osText,
            bool is64BitProcess,
            string exePath,
            string exeVersionText)
        {
            ProbeSucceeded = probeSucceeded;
            NetRelease = netRelease;
            NetVersionText = netVersionText;
            ClrVersionText = clrVersionText;
            OsText = osText;
            Is64BitProcess = is64BitProcess;
            ExePath = exePath;
            ExeVersionText = exeVersionText;
        }

        /// <summary>注册表是否读取成功。false 表示无法判定，而不是"版本太低"。</summary>
        public bool ProbeSucceeded { get; private set; }

        /// <summary>.NET Framework 的 Release 值（0 表示读不到）。</summary>
        public int NetRelease { get; private set; }

        /// <summary>注册表里的 Version 文本（如 “4.8.09221”）；读不到时为 null。</summary>
        public string NetVersionText { get; private set; }

        /// <summary>CLR 版本。</summary>
        public string ClrVersionText { get; private set; }

        /// <summary>操作系统版本描述。</summary>
        public string OsText { get; private set; }

        /// <summary>当前进程是否为 64 位。</summary>
        public bool Is64BitProcess { get; private set; }

        /// <summary>可执行文件路径。</summary>
        public string ExePath { get; private set; }

        /// <summary>可执行文件版本。</summary>
        public string ExeVersionText { get; private set; }

        /// <summary>是否满足 .NET Framework 4.8 及以上。</summary>
        public bool IsNet48OrLater
        {
            get { return ProbeSucceeded && NetRelease >= DotNetRuntimeProbe.Net48MinimumRelease; }
        }

        /// <summary>进程位数文本。</summary>
        public string ProcessBitsText
        {
            get { return Is64BitProcess ? "64 位" : "32 位"; }
        }

        /// <summary>多行诊断文本（“关于”与“检测运行环境”共用）。</summary>
        public string ToDisplayText()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("PS-text 版本：" + (ExeVersionText ?? "未知"));
            builder.AppendLine("程序路径：" + (ExePath ?? "未知"));
            builder.AppendLine();

            if (ProbeSucceeded)
            {
                builder.AppendLine(".NET Framework：" + (NetVersionText ?? "未知")
                                   + "（Release " + NetRelease.ToString(CultureInfo.InvariantCulture) + "）");
            }
            else
            {
                builder.AppendLine(".NET Framework：无法读取（注册表不可访问）");
            }

            builder.AppendLine("CLR 版本：" + (ClrVersionText ?? "未知"));
            builder.AppendLine("进程位数：" + ProcessBitsText);
            builder.AppendLine("操作系统：" + (OsText ?? "未知"));
            builder.AppendLine();
            builder.AppendLine("设置文件：" + (SettingsFilePath ?? "未知"));
            builder.AppendLine("日志目录：" + (LogDirectory ?? "未知"));

            if (!IsNet48OrLater)
            {
                builder.AppendLine();

                if (ProbeSucceeded)
                {
                    builder.AppendLine("⚠ 检测到 .NET Framework 版本低于 4.8。");
                    builder.AppendLine("本程序需要 .NET Framework 4.8 才能完整运行，");
                    builder.AppendLine("可能出现功能异常或崩溃，建议先安装：");
                }
                else
                {
                    builder.AppendLine("⚠ 无法确认 .NET Framework 版本。");
                    builder.AppendLine("若程序出现异常，建议确认已安装 4.8 或更高版本：");
                }

                builder.AppendLine(DotNetRuntimeProbe.DownloadUrl);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>设置文件路径（由调用方填充，避免这里反向依赖设置服务）。</summary>
        public string SettingsFilePath { get; set; }

        /// <summary>日志目录（由调用方填充）。</summary>
        public string LogDirectory { get; set; }
    }

    /// <summary>
    /// .NET Framework 运行时探测。
    ///
    /// **为什么必须读注册表，而不是 <c>Environment.Version</c>**：
    /// 任何 .NET Framework 4.x（4.0 / 4.5 / 4.6.2 / 4.7.2 / 4.8）上，
    /// <c>Environment.Version</c> 都返回 **4.0.30319** —— 它报的是 CLR 的版本，不是框架的版本。
    /// 拿它做"是否 4.8"的判断会永远得出错误结论（看上去"刚好够"，实际上不够）。
    /// 唯一可靠的来源是注册表 NDP\v4\Full 下的 <c>Release</c> 值。
    ///
    /// **另一个坑**：本程序以 32 位运行（Prefer32Bit），在 64 位系统上访问
    /// <c>HKLM\SOFTWARE</c> 会被重定向到 <c>Wow6432Node</c>，而 .NET Framework 的键
    /// 并不保证在所有系统上都双向镜像。因此这里**两个视图都读**，取 Release 较大的那个。
    /// </summary>
    public static class DotNetRuntimeProbe
    {
        /// <summary>.NET Framework 4.8（含 4.8.1）的 Release 下限。</summary>
        public const int Net48MinimumRelease = 528040;

        /// <summary>官方下载页（Win7 用户应选 “.NET Framework 4.8 运行时”，而不是 4.8.1+）。</summary>
        public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        /// <summary>NDP 注册表子键（相对于 HKLM\SOFTWARE）。</summary>
        public const string FullSubKeyPath = @"Microsoft\NET Framework Setup\NDP\v4\Full";

        /// <summary>探测当前运行时环境。任何一步失败都会被降级为"读不到"，绝不抛异常。</summary>
        public static RuntimeEnvironmentInfo Probe(string exePath = null)
        {
            bool probeSucceeded;
            int release;
            string versionText;
            TryReadRelease(out probeSucceeded, out release, out versionText);

            string clrVersion = null;
            try
            {
                clrVersion = Environment.Version.ToString();
            }
            catch (Exception)
            {
                clrVersion = null;
            }

            string exeVersion = null;
            try
            {
                Assembly assembly = Assembly.GetEntryAssembly();
                if (assembly != null)
                {
                    Version version = assembly.GetName().Version;

                    // 优先用信息版本（我们在 csproj 里显式声明），它比 1.0.0.0 这种程序集版本可读。
                    object[] attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                    if (attributes != null && attributes.Length > 0)
                    {
                        AssemblyInformationalVersionAttribute info =
                            attributes[0] as AssemblyInformationalVersionAttribute;
                        if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
                        {
                            exeVersion = info.InformationalVersion;
                        }
                    }

                    if (string.IsNullOrEmpty(exeVersion) && version != null)
                    {
                        exeVersion = version.ToString();
                    }
                }
            }
            catch (Exception)
            {
                exeVersion = null;
            }

            string resolvedExePath = exePath;
            if (string.IsNullOrWhiteSpace(resolvedExePath))
            {
                try
                {
                    resolvedExePath = Assembly.GetEntryAssembly() != null
                        ? Assembly.GetEntryAssembly().Location
                        : null;
                }
                catch (Exception)
                {
                    resolvedExePath = null;
                }
            }

            return new RuntimeEnvironmentInfo(
                probeSucceeded,
                release,
                versionText,
                clrVersion,
                DescribeOs(),
                IntPtr.Size == 8,
                resolvedExePath,
                exeVersion);
        }

        /// <summary>
        /// 读取 .NET Framework 的 Release 与 Version。
        /// 32 位与 64 位两个注册表视图都会尝试，取 Release 较大者。
        /// </summary>
        public static bool TryReadRelease(out bool probeSucceeded, out int release, out string versionText)
        {
            probeSucceeded = false;
            release = 0;
            versionText = null;

            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };

            for (int i = 0; i < views.Length; i++)
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, views[i]))
                    {
                        if (baseKey == null)
                        {
                            continue;
                        }

                        using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\" + FullSubKeyPath, false))
                        {
                            if (key == null)
                            {
                                continue;
                            }

                            object rawRelease = key.GetValue("Release");
                            if (rawRelease == null)
                            {
                                continue;
                            }

                            int value;
                            if (!TryConvertToInt(rawRelease, out value))
                            {
                                continue;
                            }

                            probeSucceeded = true;

                            if (value > release)
                            {
                                release = value;
                            }

                            object rawVersion = key.GetValue("Version");
                            string text = rawVersion as string;

                            if (!string.IsNullOrWhiteSpace(text) && string.IsNullOrEmpty(versionText))
                            {
                                versionText = text;
                            }
                        }
                    }
                }
                catch (System.Security.SecurityException)
                {
                    // 受限环境（组策略 / 沙箱）读不到就换下一个视图，最终降级为"读不到"。
                }
                catch (System.IO.IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return probeSucceeded;
        }

        private static bool TryConvertToInt(object raw, out int value)
        {
            value = 0;

            if (raw is int)
            {
                value = (int)raw;
                return true;
            }

            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static string DescribeOs()
        {
            try
            {
                OperatingSystem os = Environment.OSVersion;

                // 注意：只有 manifest 里声明了 supportedOS 才会拿到真实版本，
                // 否则 Win8.1 以上会被谎报成 6.2（本项目的 app.manifest 已声明 Win7~Win11）。
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1}.{2}（内部版本 {3}）",
                    os.Platform,
                    os.Version.Major,
                    os.Version.Minor,
                    os.Version.Build);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
