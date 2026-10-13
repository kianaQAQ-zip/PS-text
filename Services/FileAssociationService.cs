using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PSText.Services.Interfaces;
using PSText.Services;

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

        /// <summary>右键菜单里"编辑"动词的键名。</summary>
        public const string EditVerbName = "PSText.Edit";

        /// <summary>右键菜单里"打印"动词的键名。</summary>
        public const string PrintVerbName = "PSText.Print";

        /// <summary>友好名称（"打开方式"里显示的名字）。</summary>
        /// <summary>写进注册表的显示名。跟着界面语言走，所以是 static readonly 而不是 const。</summary>
        public static string FriendlyName
        {
            get { return Loc.T("App_Title"); }
        }

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

        /// <summary>
        /// 右键菜单动词的父键。
        ///
        /// 挂在 <c>SystemFileAssociations\image</c> 下，而不是逐个扩展名各挂一份：
        /// 这一处就能覆盖所有"感知类型为图片"的文件（jpg / png / bmp / gif / tif …），
        /// 注销时也只有一个地方要清理，不容易留下残渣。
        /// </summary>
        private string ShellVerbsRootPath
        {
            get { return ClassesPath + @"\SystemFileAssociations\image\shell"; }
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

        /// <summary>右键菜单动词是否已登记（两个都在才算）。</summary>
        public bool HasContextMenu
        {
            get
            {
                return !string.IsNullOrWhiteSpace(
                           ReadString(_hive, ShellVerbsRootPath + @"\" + EditVerbName + @"\command", null))
                       && !string.IsNullOrWhiteSpace(
                           ReadString(_hive, ShellVerbsRootPath + @"\" + PrintVerbName + @"\command", null));
            }
        }

        #endregion

        #region 注册 / 注销

        public FileAssociationResult Register()
        {
            if (string.IsNullOrWhiteSpace(_exePath))
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorNoExe"));
            }

            if (!File.Exists(_exePath))
            {
                return FileAssociationResult.Fail(
                    Loc.T("Assoc_ErrorExeMissing") + Environment.NewLine + _exePath + Environment.NewLine
                    + Loc.T("Assoc_MovedExeWarning"));
            }

            try
            {
                string command = Quote(_exePath) + " \"%1\"";

                // 1) ProgID 本体
                using (RegistryKey key = CreateKey(_hive, ProgIdPath))
                {
                    key.SetValue(null, Loc.T("Assoc_ProgIdFriendly"), RegistryValueKind.String);
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
                        Loc.T("About_Tagline"),
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

                // 5) 右键菜单（"用 PS-text 编辑 / 打印"）
                WriteShellVerbs();

                NotifyShell();
                return FileAssociationResult.Ok(BuildRegisterSummary());
            }
            catch (UnauthorizedAccessException ex)
            {
                return FileAssociationResult.Fail(
                    Loc.T("Assoc_ErrorDeniedWrite")
                    + Loc.T("Assoc_ErrorReasonIntro")
                    + Loc.T("Assoc_ErrorReasonPolicy")
                    + Loc.T("Assoc_ErrorReasonAccount")
                    + Loc.T("Assoc_ErrorReasonHkcu")
                    + Loc.T("Assoc_ErrorAdvice")
                    + "（" + ex.Message + "）");
            }
            catch (System.Security.SecurityException ex)
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorPolicyWrite") + ex.Message);
            }
            catch (IOException ex)
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorWrite") + ex.Message);
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

                // 右键菜单：动词键与它的 command 子键一起删；shell 空了就删掉，
                // 但**不动** SystemFileAssociations\image 这一层 —— 那是系统结构，
                // 别的程序也可能往里挂东西，不该由我们来清理。
                DeleteTree(_hive, ShellVerbsRootPath + @"\" + EditVerbName);
                DeleteTree(_hive, ShellVerbsRootPath + @"\" + PrintVerbName);
                DeleteKeyIfEmpty(_hive, ShellVerbsRootPath);

                NotifyShell();
                return FileAssociationResult.Ok(Loc.T("Assoc_UnregisterOk"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorDeniedDelete") + ex.Message);
            }
            catch (System.Security.SecurityException ex)
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorPolicyDelete") + ex.Message);
            }
            catch (IOException ex)
            {
                return FileAssociationResult.Fail(Loc.T("Assoc_ErrorDelete") + ex.Message);
            }
        }

        #endregion

        #region 右键菜单

        /// <summary>
        /// 写两个右键菜单动词。
        ///
        /// 用 <c>MUIVerb</c> 而不是默认值来写显示名：默认值在资源管理器里
        /// 会走一套老的解析规则（会被当成"动词名"再去找本地化字符串），
        /// MUIVerb 才是给用户看的那个名字。
        ///
        /// 不加 <c>Extended</c> 值，因此**不需要按住 Shift** 就能看到。
        /// </summary>
        private void WriteShellVerbs()
        {
            string exe = Quote(_exePath);
            string icon = exe + ",0";

            using (RegistryKey key = CreateKey(_hive, ShellVerbsRootPath + @"\" + EditVerbName))
            {
                key.SetValue("MUIVerb", Loc.T("Assoc_VerbEdit"), RegistryValueKind.String);
                key.SetValue("Icon", icon, RegistryValueKind.String);
            }

            using (RegistryKey key = CreateKey(_hive, ShellVerbsRootPath + @"\" + EditVerbName + @"\command"))
            {
                key.SetValue(null, exe + " \"%1\"", RegistryValueKind.String);
            }

            using (RegistryKey key = CreateKey(_hive, ShellVerbsRootPath + @"\" + PrintVerbName))
            {
                key.SetValue("MUIVerb", Loc.T("Assoc_VerbPrint"), RegistryValueKind.String);
                key.SetValue("Icon", icon, RegistryValueKind.String);
            }

            using (RegistryKey key = CreateKey(_hive, ShellVerbsRootPath + @"\" + PrintVerbName + @"\command"))
            {
                // --print：打开图片后直接进打印预览。
                // 刻意**不做**静默打印：Windows 自己的图片"打印"动词也是弹向导的，
                // 悄悄把文件送去打印机会造成"怎么多出来一沓纸"的事故。
                key.SetValue(null, exe + " --print \"%1\"", RegistryValueKind.String);
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
                return FileAssociationResult.Fail(Loc.T("Assoc_NeedRegisterFirst"));
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
                            Loc.T("Assoc_OpenDefaultAppsFailed"),
                            hr));
                }

                return FileAssociationResult.Ok(
                    Loc.T("Assoc_OpenDefaultAppsOk")
                    + Loc.T("Assoc_OpenDefaultAppsHint"));
            }
            catch (COMException ex)
            {
                return FileAssociationResult.Fail(
                    Loc.T("Assoc_OpenDefaultAppsUnsupported")
                    + Environment.NewLine + "（" + ex.Message + "）");
            }
            catch (InvalidCastException ex)
            {
                return FileAssociationResult.Fail(
                    Loc.T("Assoc_OpenDefaultAppsUnsupported")
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
                throw new IOException(Loc.T("Assoc_ErrorCreateKey") + path);
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
                Loc.T("Assoc_RegisterOk")
                + Loc.T("Assoc_ContextMenuNote")
                + Loc.T("Assoc_RegisterNote"),
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
