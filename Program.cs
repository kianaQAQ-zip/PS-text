using System;
using System.Runtime.InteropServices;

namespace PSText
{
    /// <summary>
    /// 显式程序入口。
    ///
    /// 为什么要自己写 Main：
    ///   1. 必须标注 [STAThread]，WPF 与剪贴板 / 打印 / 文件对话框都要求 STA；
    ///   2. 自检模式（--selftest）要在加载 App.xaml 资源字典之前就能运行，
    ///      这样即使某个资源字典写错，自检仍能给出可读的诊断结果，而不是启动即崩溃；
    ///   3. 便于对入口处做最后一道异常兜底。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                App application = new App();
                return application.Run(args);
            }
            catch (Exception ex)
            {
                // 最外层兜底：统一走 CrashLogger 落盘，保证“双击无反应”这类问题可排查。
                // （日志位置：%LOCALAPPDATA%\PS-text\logs，不可写时回退 %TEMP%\PS-text-logs）
                Services.CrashLogger.Log("程序在启动或消息循环阶段发生未处理异常。", ex);
                return -1;
            }
        }
    }
}
