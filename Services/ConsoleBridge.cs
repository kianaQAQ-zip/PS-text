using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PSText.Services
{
    /// <summary>
    /// 无界面模式的输出通道。
    ///
    /// 本程序是 **WinExe**（GUI 子系统），进程本身不带控制台，
    /// 因此从命令行运行时直接 <c>Console.WriteLine</c> 会把内容丢进虚空 ——
    /// 用户看到"命令执行了，但什么都没输出"。这里做两件事：
    ///   1. 用 <c>AttachConsole(ATTACH_PARENT_PROCESS)</c> 附着到父进程（cmd / PowerShell）的控制台；
    ///   2. 附着失败时（比如从资源管理器双击、或父进程没有控制台）退回弹窗，
    ///      保证结果**一定**能被人看到。
    /// </summary>
    internal sealed class ConsoleBridge
    {
        private const int AttachParentProcess = -1;

        private readonly TextWriter _writer;

        public ConsoleBridge()
        {
            try
            {
                AttachConsole(AttachParentProcess);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            _writer = TryOpenStandardOutput();
        }

        /// <summary>是否成功拿到可写的标准输出（true 表示走控制台，false 表示要靠弹窗）。</summary>
        public bool HasConsole
        {
            get { return _writer != null; }
        }

        /// <summary>写一行。没有控制台时什么都不做（由调用方改用弹窗）。</summary>
        public void WriteLine(string text)
        {
            if (_writer == null)
            {
                return;
            }

            _writer.WriteLine(text);
            _writer.Flush();
        }

        /// <summary>优先写控制台，写不了就弹窗 —— 保证结果一定可见。</summary>
        public void Report(string title, string text, bool isError)
        {
            if (_writer != null)
            {
                _writer.WriteLine(text);
                _writer.Flush();
                return;
            }

            try
            {
                MessageBox.Show(
                    text,
                    title,
                    MessageBoxButton.OK,
                    isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception)
            {
                // 连弹窗都不行（极端受限环境）就只能放弃提示。
            }
        }

        private static TextWriter TryOpenStandardOutput()
        {
            try
            {
                Stream stream = Console.OpenStandardOutput();

                if (stream == null || !stream.CanWrite)
                {
                    return null;
                }

                // 用系统 ANSI 代码页（简中系统即 GBK）：与 cmd 的默认代码页一致，
                // 写中文不会乱码。若强行用 UTF-8，未 chcp 65001 的控制台会显示成乱码。
                return new StreamWriter(stream, Encoding.Default) { AutoFlush = true };
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
    }
}
