using System;
using System.IO;

namespace PSText.Services
{
    /// <summary>启动模式。</summary>
    public enum StartupMode
    {
        /// <summary>正常打开主窗口（可能带上一个待打开的图片路径）。</summary>
        OpenWindow = 0,

        /// <summary>无界面自检（--selftest）。</summary>
        SelfTest,

        /// <summary>注册文件关联后退出（--register）。</summary>
        RegisterAssociation,

        /// <summary>注销文件关联后退出（--unregister）。</summary>
        UnregisterAssociation,

        /// <summary>打印关联状态后退出（--assoc-status）。</summary>
        AssociationStatus,

        /// <summary>打印运行环境信息后退出（--runtime）。</summary>
        RuntimeInfo
    }

    /// <summary>
    /// 命令行解析。
    ///
    /// 单独抽成一个纯函数类而不是散在 <c>App</c> 里，有两个实在的理由：
    ///   1. 它能被自检直接覆盖 —— 参数解析写错的表现是"双击打不开文件"或"启动就退出"，
    ///      属于必须钉住的行为；
    ///   2. 无界面模式（注册关联 / 查环境）是给**批量部署脚本**用的，
    ///      它的行为不该跟着窗口代码一起改。
    /// </summary>
    public sealed class CommandLineOptions
    {
        private CommandLineOptions(StartupMode mode, string imagePath, string[] rawArguments)
        {
            Mode = mode;
            ImagePath = imagePath;
            RawArguments = rawArguments ?? new string[0];
        }

        /// <summary>启动模式。</summary>
        public StartupMode Mode { get; private set; }

        /// <summary>要打开的图片路径（仅 OpenWindow 模式；没有则为 null）。</summary>
        public string ImagePath { get; private set; }

        /// <summary>原始参数。</summary>
        public string[] RawArguments { get; private set; }

        /// <summary>是否是无界面模式（不创建主窗口）。</summary>
        public bool IsHeadless
        {
            get { return Mode != StartupMode.OpenWindow; }
        }

        /// <summary>解析命令行。开关不区分大小写；未知开关被忽略。</summary>
        public static CommandLineOptions Parse(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return new CommandLineOptions(StartupMode.OpenWindow, null, args);
            }

            // 顺序即优先级：自检最高（CI 里可能顺带带上文件路径），
            // 其余开关互斥，先出现的赢。
            StartupMode mode = StartupMode.OpenWindow;
            string imagePath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (string.IsNullOrWhiteSpace(argument))
                {
                    continue;
                }

                if (TryMatchSwitch(argument, "--selftest"))
                {
                    return new CommandLineOptions(StartupMode.SelfTest, null, args);
                }

                if (mode == StartupMode.OpenWindow)
                {
                    if (TryMatchSwitch(argument, "--register"))
                    {
                        mode = StartupMode.RegisterAssociation;
                        continue;
                    }

                    if (TryMatchSwitch(argument, "--unregister"))
                    {
                        mode = StartupMode.UnregisterAssociation;
                        continue;
                    }

                    if (TryMatchSwitch(argument, "--assoc-status"))
                    {
                        mode = StartupMode.AssociationStatus;
                        continue;
                    }

                    if (TryMatchSwitch(argument, "--runtime"))
                    {
                        mode = StartupMode.RuntimeInfo;
                        continue;
                    }
                }

                // 非开关参数：当作图片路径（"打开方式"就是这么把文件传进来的）。
                if (argument.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (imagePath == null && FileExists(argument))
                {
                    imagePath = argument;
                }
            }

            if (mode != StartupMode.OpenWindow)
            {
                imagePath = null;
            }

            return new CommandLineOptions(mode, imagePath, args);
        }

        private static bool TryMatchSwitch(string argument, string name)
        {
            return string.Equals(argument, name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool FileExists(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }
    }
}
