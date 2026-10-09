namespace PSText.Services.Interfaces
{
    /// <summary>文件关联的当前状态。</summary>
    public enum FileAssociationState
    {
        /// <summary>尚未注册。</summary>
        NotRegistered = 0,

        /// <summary>已注册，且指向当前这一份程序。</summary>
        Registered,

        /// <summary>
        /// 已注册，但登记的是**另一个位置**的 exe
        /// （典型场景：绿色版被移动过、或先解压到别处注册过一次）。
        /// 单独列出这一种，否则用户会看到"已注册"却怎么都打不开。
        /// </summary>
        RegisteredForAnotherCopy
    }

    /// <summary>关联操作的结果。</summary>
    public sealed class FileAssociationResult
    {
        private FileAssociationResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; private set; }

        public string Message { get; private set; }

        public static FileAssociationResult Ok(string message)
        {
            return new FileAssociationResult(true, message);
        }

        public static FileAssociationResult Fail(string message)
        {
            return new FileAssociationResult(false, message);
        }
    }

    /// <summary>
    /// 文件关联服务。
    ///
    /// 设计立场：**只登记"打开方式"，不抢默认关联**。
    /// 从 Windows Vista 起，程序就无权静默把自己设为默认打开程序，任何"偷偷改默认"的做法
    /// 都会被系统拦下或引发用户反感。正确做法是登记 ProgID 与 Capabilities，
    /// 让程序出现在"打开方式"列表和"设置默认程序"里，把决定权交给用户。
    ///
    /// 全部写入 HKCU，**不需要管理员权限**（绿色版本不该要求提权）。
    /// </summary>
    public interface IFileAssociationService
    {
        /// <summary>当前注册状态。</summary>
        FileAssociationState GetState();

        /// <summary>已登记的打开命令（未注册时为 null）。用于诊断。</summary>
        string RegisteredCommandText { get; }

        /// <summary>要登记的 ProgID（界面提示里会用到）。</summary>
        string ProgId { get; }

        /// <summary>关联的扩展名数量（界面提示里会用到）。</summary>
        int SupportedExtensionCount { get; }

        /// <summary>注册（幂等，可重复调用）。</summary>
        FileAssociationResult Register();

        /// <summary>注销（幂等）。</summary>
        FileAssociationResult Unregister();

        /// <summary>打开 Windows「默认程序」设置界面，让用户自行选择默认打开方式。</summary>
        FileAssociationResult OpenDefaultProgramsSettings();
    }
}
