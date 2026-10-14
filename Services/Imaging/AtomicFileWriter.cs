using System;
using System.IO;

namespace PSText.Services.Imaging
{
    /// <summary>
    /// 原子写文件：先写同目录下的临时文件，成功后整体替换目标。
    ///
    /// 为什么不能直接 `FileMode.Create` 写目标文件：那个模式会**立刻把目标截断成 0 字节**。
    /// 万一编码中途失败（内存不足、编码器抛异常、进程被杀、盘满），
    /// 用户原来那张图就没了 —— 而我们本来是想**保存**它的。
    /// 先写临时文件再替换，最坏情况也只是留下一个临时文件，目标文件保持完好。
    ///
    /// 单独拎成一个类是为了能被直接测：让写入过程故意抛异常，
    /// 然后断言"原文件字节不变 + 不留下临时文件"这两条 —— 这是这套机制存在的全部意义。
    /// </summary>
    public static class AtomicFileWriter
    {
        /// <summary>临时文件后缀。带上前缀便于用户一眼看出是我们留下的。</summary>
        public const string TempSuffix = ".pstext-tmp";

        /// <summary>
        /// 原子地写出目标文件。
        /// </summary>
        /// <param name="targetPath">最终文件路径。</param>
        /// <param name="write">写入委托：往给定的流里写内容。抛异常即视为失败，目标文件不会被改动。</param>
        public static void Write(string targetPath, Action<Stream> write)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("targetPath", "targetPath");
            }

            if (write == null)
            {
                throw new ArgumentNullException("write");
            }

            string directory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = targetPath + TempSuffix;
            bool replaced = false;

            try
            {
                using (FileStream stream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush();
                }

                Replace(tempPath, targetPath);
                replaced = true;
            }
            finally
            {
                // 任何一条路径上失败都不留垃圾：否则目标目录里会慢慢堆满 .pstext-tmp。
                if (!replaced)
                {
                    TryDelete(tempPath);
                }
            }
        }

        /// <summary>把临时文件替换到目标位置。</summary>
        private static void Replace(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                // Replace 是真正原子的：替换过程中目标路径始终指向"要么旧内容、要么新内容"。
                File.Replace(tempPath, targetPath, null);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                       || ex is PlatformNotSupportedException)
            {
                // 个别文件系统（网络盘 / FAT）不支持 Replace。
                // 退回"删旧 + 改名"：不是原子的，但至少内容已经完整地写好在那儿了。
                File.Delete(targetPath);
                File.Move(tempPath, targetPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
