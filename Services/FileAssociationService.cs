using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PSText.Services.Interfaces;

namespace PSText.Services
{
    /// <summary>
    /// 文件关联实现（HKCU）。
    ///
    /// 注册下来的东西分四块，缺任何一块都会导致"看起来注册了但用不了"：
    ///
    ///   1. <c>Software\Classes\PSText.Image</c>：ProgID 本体 —— 默认值（友好名称）、
    ///      <c>DefaultIcon</c>、<c>shell\open\command</c>。这是"打开方式"真正要执行的东西。
    ///   2. <c>Software\Classes\Applications\PS-text.exe</c>：让"打开方式"列表按**程序**而不是
    ///      按 ProgID 也能找到我们，并声明 <c>SupportedTypes</c>。
    ///   3. <c>Software\Classes\.jpg\OpenWithProgids\PSText.Image</c>：把 ProgID 挂到各个扩展名上。
    ///      这是"出现在打开方式列表里"的关键；**只写这一层不会改默认关联**，正是我们要的。
    ///   4. <c>Software\RegisteredApplications</c> + <c>Software\PS-text\Capabilities</c>：
    ///      Windows 7 起「默认程序」控制面板只认这套结构，没有它就进不了"设置默认程序"列表，
    ///      那个"打开默认程序设置"按钮也就白点了。
    ///
    /// 注册表基路径与根键都做成可注入的：自检会传一个测试子键，从而把整条写入 / 读取 / 清理
    /// 流程真正跑一遍，**而绝不碰用户真实的打开方式**。这不是为了测试而测试 ——
    /// 关联写错位置的代价是用户的 .jpg 打不开了。
    /// </summary>
    public sealed class FileAssociationService : IFileAssociationService
    {
        /// <summary>ProgID。用 PS 前缀命名空间，避免与别的程序撞车。</summary>
        public const string ProgramId = "PSText.Image";

        /// <summary>RegisteredApplications 下的值名，也是传给"默认程序"界面的名字。</summary>
        public const string ApplicationRegistryName = "PS-text";

        /// <summary>友好名称（"打开方式"里显示的名字）。</summary>
        public const string FriendlyName = "PS-text 图片编辑器";

        /// <summary>关联的扩展名（内部使用；对外只暴露数量，避免外部改到这张表）。</summary>
        private static readonly string[] Extensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif",
            ".png",
            ".bmp", ".dib",
            ".gif",
            ".tif", ".tiff"
        };

        /// <summary>关联的扩展名数量。</summary>
        public int SupportedExtensionCount
        {
            get { return Extensions.Length; }
        }

        /// <summary>关联的扩展名（副本）。</summary>
        public static string[] GetSupportedExtensions()
        {
            return (string[])Extensions.Clone();
        }

        private const int ShcneAssocChanged = 0x08000000;
        private const uint ShcnfIdList = 0x0000;
        private const uint ShcnfFlush = 0x1000;

        private readonly RegistryKey _hive;
        private readonly string _softwarePath;
        private readonly string _exePath;

        /// <summary>正式运行时使用：HKCU\Software，exe 取当前进程。</summary>
        public FileAssociationService()
            : this(null, null, null)
        {
        }

        /// <summary>自检用：注入根键与软件基路径，隔离在测试子键里。</summary>
        public FileAssociationService(string exePath, RegistryKey hive, string softwarePath)
        {
            _hive = hive ?? Registry.CurrentUser;

            string path = softwarePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "Software";
            }

            _softwarePath = path.Trim().TrimEnd('\\');
            _exePath = ResolveExePath(exePath);
        }

        public string ProgId
        {
            get { return ProgramId; }
        }

        /// <summary>测试隔离用：本次实例真正操作的注册表根路径。</summary>
        public string SoftwarePath
        {
            get { return _softwarePath; }
        }

        /// <summary>本实例登记的 exe 路径。</summary>
        public string ExePath
        {
            get { return _exePath; }
        }

        private string ClassesPath
        {
            get { return _softwarePath + @"\Classes"; }
        }

        private string ProgIdPath
        {
            get { return ClassesPath + @"\" + ProgramId; }
        }

        private string ApplicationPath
        {
            get { return ClassesPath + @"\Applications\PS-text.exe"; }
        }

        private string RegisteredApplicationsPath
        {
            get { return _softwarePath + @"\RegisteredApplications"; }
        }

        private string CapabilitiesRootPath
        {
            get { return _softwarePath + @"\PS-text"; }
        }

        private string CapabilitiesPath
        {
            get { return CapabilitiesRootPath + @"\Capabilities"; }
        }

        #region 状态查询

        public FileAssociationState GetState()
        {
            string command = RegisteredCommandText;

            if (string.IsNullOrWhiteSpace(command))
            {
                return FileAssociationState.NotRegistered;
            }

            string registeredExe = ExtractExecutablePath(command);

            if (string.IsNullOrEmpty(registeredExe))
            {
                return FileAssociationState.RegisteredForAnotherCopy;
            }

            try
            {
                bool same = string.Equals(
                    Path.GetFullPath(registeredExe),
                    Path.GetFullPath(_exePath),
                    StringComparison.OrdinalIgnoreCase);

                return same ? FileAssociationState.Registered : FileAssociationState.RegisteredForAnotherCopy;
            }
            catch (ArgumentException)
            {
                return FileAssociationState.RegisteredForAnotherCopy;
            }
            catch (NotSupportedException)
            {
                return FileAssociationState.RegisteredForAnotherCopy;
            }
            catch (PathTooLongException)
            {
                return FileAssociationState.RegisteredForAnotherCopy;
            }
        }

        public string RegisteredCommandText
        {
            get { return ReadString(_hive, ProgIdPath + @"\shell\open\command", null); }
        }

        #endregion

        #region 注册 / 注销

        public FileAssociationResult Register()
        {
            if (string.IsNullOrWhiteSpace(_exePath))
            {
                return FileAssociationResult.Fail("无法确定程序自身的位置，注册失败。");
            }

            if (!File.Exists(_exePath))
            {
                return FileAssociationResult.Fail(
                    "找不到程序文件：" + Environment.NewLine + _exePath + Environment.NewLine
                    + "绿色版本请不要在运行期间移动或删除 exe —— 请重新解压后再注册。");
            }

            try
            {
                string command = Quote(_exePath) + " \"%1\"";

                // 1) ProgID 本体
                using (RegistryKey key = CreateKey(_hive, ProgIdPath))
                {
                    key.SetValue(null, "PS-text 图片", RegistryValueKind.String);
                }

                using (RegistryKey key = CreateKey(_hive, ProgIdPath + @"\DefaultIcon"))
                {
                    key.SetValue(null, Quote(_exePath) + ",0", RegistryValueKind.String);
                }

                using (RegistryKey key = CreateKey(_hive, ProgIdPath + @"\shell\open\command"))
                {
                    key.SetValue(null, command, RegistryValueKind.String);
                }

                // 2) Applications\PS-text.exe
                using (RegistryKey key = CreateKey(_hive, ApplicationPath))
                {
                    key.SetValue("FriendlyAppName", FriendlyName, RegistryValueKind.String);
                }

                using (RegistryKey key = CreateKey(_hive, ApplicationPath + @"\shell\open\command"))
                {
                    key.SetValue(null, command, RegistryValueKind.String);
                }

                using (RegistryKey key = CreateKey(_hive, ApplicationPath + @"\SupportedTypes"))
                {
                    for (int i = 0; i < Extensions.Length; i++)
                    {
                        key.SetValue(Extensions[i], string.Empty, RegistryValueKind.String);
                    }
                }

                // 3) 各扩展名的 OpenWithProgids（只登记"可以打开"，不改默认）
                for (int i = 0; i < Extensions.Length; i++)
                {
                    using (RegistryKey key = CreateKey(
                        _hive, ClassesPath + @"\" + Extensions[i] + @"\OpenWithProgids"))
                    {
                        // 规范要求：值名为 ProgID、数据为空、类型 REG_NONE。
                        key.SetValue(ProgramId, new byte[0], RegistryValueKind.None);
                    }
                }

                // 4) Capabilities（进"设置默认程序"列表的前提）
                using (RegistryKey key = CreateKey(_hive, CapabilitiesPath))
                {
                    key.SetValue("ApplicationName", FriendlyName, RegistryValueKind.String);
                    key.SetValue(
                        "ApplicationDescription",
                        "轻量办公图片处理工具：批量、水印、标注、修补、打印（Windows 7 兼容）",
                        RegistryValueKind.String);
                }

                using (RegistryKey key = CreateKey(_hive, CapabilitiesPath + @"\FileAssociations"))
                {
                    for (int i = 0; i < Extensions.Length; i++)
                    {
                        key.SetValue(Extensions[i], ProgramId, RegistryValueKind.String);
                    }
                }

                using (RegistryKey key = CreateKey(_hive, RegisteredApplicationsPath))
                {
                    key.SetValue(
                        ApplicationRegistryName,
                        CapabilitiesPath,
                        RegistryValueKind.String);
                }

                NotifyShell();
                return FileAssociationResult.Ok(BuildRegisterSummary());
            }
            catch (UnauthorizedAccessException ex)
            {
                return FileAssociationResult.Fail(
                    "没有权限写入注册表，注册失败。\n\n"
                    + "可能的原因：\n"
                    + "· 组策略 / 安全软件禁止程序写注册表；\n"
                    + "· 当前账户是受限账户。\n\n"
                    + "文件关联写的是当前用户（HKCU），正常情况下不需要管理员权限；\n"
                    + "如果这里被拒绝，请检查安全软件或改用管理员身份运行后再注册。\n\n"
                    + "（" + ex.Message + "）");
            }
            catch (System.Security.SecurityException ex)
            {
                return FileAssociationResult.Fail("安全策略禁止写入注册表，注册失败：" + ex.Message);
            }
            catch (IOException ex)
            {
                return FileAssociationResult.Fail("写入注册表时出错，注册失败：" + ex.Message);
            }
        }

        public FileAssociationResult Unregister()
        {
            try
            {
                DeleteTree(_hive, ProgIdPath);
                DeleteTree(_hive, ApplicationPath);

                // 从各扩展名的 OpenWithProgids 里摘掉自己；摘完若整条键空了就删掉，
                // 避免在用户注册表里留下一个空壳（有些系统工具会把它读成"有个未知程序"）。
                for (int i = 0; i < Extensions.Length; i++)
                {
                    string keyPath = ClassesPath + @"\" + Extensions[i] + @"\OpenWithProgids";

                    using (RegistryKey key = OpenKey(_hive, keyPath, true))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        try
                        {
                            key.DeleteValue(ProgramId, false);
                        }
                        catch (ArgumentException)
                        {
                            // 值名非法（理论上不会走到）；忽略。
                        }
                    }

                    DeleteKeyIfEmpty(_hive, keyPath);
                }

                DeleteTree(_hive, CapabilitiesPath);

                // 只在确实空了的时候才删 PS-text 这一层，别把将来可能放在这里的别的数据带走。
                bool capabilitiesRootEmpty;
                using (RegistryKey key = OpenKey(_hive, CapabilitiesRootPath, false))
                {
                    capabilitiesRootEmpty = key != null && key.SubKeyCount == 0 && key.ValueCount == 0;
                }

                if (capabilitiesRootEmpty)
                {
                    DeleteTree(_hive, CapabilitiesRootPath);
                }

                // 注意：这里**不**删除 RegisteredApplications 这个键本身 ——
                // 它与其它程序共用，即使暂时为空也属于系统结构，不该由我们来清理。
                using (RegistryKey key = OpenKey(_hive, RegisteredApplicationsPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(ApplicationRegistryName, false);
                    }
                }

                NotifyShell();
                return FileAssociationResult.Ok("已注销文件关联。文件仍可通过“打开方式 → 选择其他应用”使用。");
            }
            catch (UnauthorizedAccessException ex)
            {
                return FileAssociationResult.Fail("没有权限修改注册表，注销失败：" + ex.Message);
            }
            catch (System.Security.SecurityException ex)
            {
                return FileAssociationResult.Fail("安全策略禁止修改注册表，注销失败：" + ex.Message);
            }
            catch (IOException ex)
            {
                return FileAssociationResult.Fail("修改注册表时出错，注销失败：" + ex.Message);
            }
        }

        #endregion

        #region 默认程序界面

        [ComImport]
        [Guid("1968106d-f3b5-44cf-890e-116fcb9ecef1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationAssociationRegistrationUI
        {
            [PreserveSig]
            int LaunchAdvancedAssociationUI([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);
        }

        [ComImport]
        [Guid("1f76a169-f994-40ac-8fc8-0959e8874710")]
        [ClassInterface(ClassInterfaceType.None)]
        private class ApplicationAssociationRegistrationUI
        {
        }

        public FileAssociationResult OpenDefaultProgramsSettings()
        {
            if (GetState() != FileAssociationState.Registered)
            {
                return FileAssociationResult.Fail("请先注册文件关联，然后才能在这里把 PS-text 设为默认打开程序。");
            }

            try
            {
                IApplicationAssociationRegistrationUI registration =
                    (IApplicationAssociationRegistrationUI)new ApplicationAssociationRegistrationUI();

                int hr = registration.LaunchAdvancedAssociationUI(ApplicationRegistryName);

                if (hr != 0)
                {
                    return FileAssociationResult.Fail(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "系统未能打开“设置默认程序”界面（HRESULT 0x{0:X8}）。请从控制面板 → 默认程序进入。",
                            hr));
                }

                return FileAssociationResult.Ok(
                    "已打开系统的“设置默认程序”界面。在列表里选择 PS-text，"
                    + "即可把 PS-text 设为 jpg / png 等图片的默认打开程序。");
            }
            catch (COMException ex)
            {
                return FileAssociationResult.Fail(
                    "当前系统不支持直接打开“设置默认程序”界面，请从控制面板 → 默认程序手动设置。"
                    + Environment.NewLine + "（" + ex.Message + "）");
            }
            catch (InvalidCastException ex)
            {
                return FileAssociationResult.Fail(
                    "当前系统不支持直接打开“设置默认程序”界面，请从控制面板 → 默认程序手动设置。"
                    + Environment.NewLine + "（" + ex.Message + "）");
            }
        }

        #endregion

        #region 注册表辅助

        private static RegistryKey CreateKey(RegistryKey hive, string path)
        {
            RegistryKey key = hive.CreateSubKey(path);

            if (key == null)
            {
                throw new IOException("无法创建注册表项：" + path);
            }

            return key;
        }

        private static RegistryKey OpenKey(RegistryKey hive, string path, bool writable)
        {
            return hive.OpenSubKey(path, writable);
        }

        private static void DeleteTree(RegistryKey hive, string path)
        {
            try
            {
                hive.DeleteSubKeyTree(path, false);
            }
            catch (ArgumentException)
            {
                // 路径为空或非法：忽略。
            }
        }

        private static void DeleteKeyIfEmpty(RegistryKey hive, string path)
        {
            using (RegistryKey key = hive.OpenSubKey(path, false))
            {
                if (key == null)
                {
                    return;
                }

                if (key.SubKeyCount > 0 || key.ValueCount > 0)
                {
                    return;
                }
            }

            DeleteTree(hive, path);
        }

        private static string ReadString(RegistryKey hive, string path, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(path, false))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    object value = key.GetValue(valueName);
                    return value as string;
                }
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>把路径包成带引号的形式（路径可能含空格）。</summary>
        internal static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        /// <summary>从 <c>"C:\a b\PS-text.exe" "%1"</c> 里取出可执行文件路径。</summary>
        internal static string ExtractExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string trimmed = command.Trim();

            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = trimmed.IndexOf('"', 1);
                return end > 1 ? trimmed.Substring(1, end - 1) : null;
            }

            int space = trimmed.IndexOf(' ');
            return space > 0 ? trimmed.Substring(0, space) : trimmed;
        }

        private string BuildRegisterSummary()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "已注册文件关联。{0} 现在会出现在 {1} 种图片扩展名的“打开方式”列表里"
                + "（默认打开程序不会被改动，如需设为默认请在系统的“设置默认程序”里选择）。",
                FriendlyName,
                Extensions.Length);
        }

        private static void NotifyShell()
        {
            // 不通知资源管理器的话，注册表的改动要等下次登录才生效 ——
            // 用户会认为"点了没反应"。
            try
            {
                SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlush, IntPtr.Zero, IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private static string ResolveExePath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath;
            }

            try
            {
                Assembly assembly = Assembly.GetEntryAssembly();

                if (assembly != null)
                {
                    return assembly.Location;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}
