using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PSText.Services.Batch
{
    /// <summary>
    /// 批量导出的命名策略：**原名 + 后缀**，遇到冲突自动加序号。
    ///
    /// 三条必须守住的规则（自检逐条钉住）：
    ///   1. **绝不覆盖任何已有文件**：不只在磁盘上比对，还要比对本次运行已经分配出去的路径 ——
    ///      否则"来自不同文件夹的两个同名文件"会在同一次运行里互相覆盖，
    ///      而磁盘上根本还没生成第二个文件，File.Exists 查不出来。
    ///   2. **绝不覆盖源文件本身**：后缀被清空时，目标名会与原名重合，必须能识别出来。
    ///   3. 结果路径一定落在指定的输出目录内（不因为原名里带路径分隔符而跑出去）。
    /// </summary>
    public sealed class BatchOutputNaming
    {
        private const int MaxAttempts = 10000;

        public BatchOutputNaming(string directory, string suffix, string forcedExtension)
        {
            Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
            Suffix = SanitizeFileNamePart(suffix);
            ForcedExtension = NormalizeExtension(forcedExtension);
        }

        /// <summary>输出目录。</summary>
        public string Directory { get; private set; }

        /// <summary>文件名后缀（已过滤非法字符）。</summary>
        public string Suffix { get; private set; }

        /// <summary>强制扩展名（含点）；为 null 表示沿用源文件扩展名。</summary>
        public string ForcedExtension { get; private set; }

        /// <summary>
        /// 为一张源图分配输出路径。
        /// </summary>
        /// <param name="sourcePath">源文件路径。</param>
        /// <param name="reservedPaths">
        /// 本次运行已分配出去的路径集合。**必须**用 <see cref="StringComparer.OrdinalIgnoreCase"/> 构造，
        /// 否则 Windows 下的大小写差异会被当成两个不同的文件。
        /// </param>
        /// <returns>可安全写入的绝对路径。</returns>
        public string NextOutputPath(string sourcePath, ISet<string> reservedPaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("源文件路径不能为空。", "sourcePath");
            }

            if (string.IsNullOrWhiteSpace(Directory))
            {
                throw new InvalidOperationException("尚未指定输出目录。");
            }

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "image";
            }

            string extension = ForcedExtension ?? Path.GetExtension(sourcePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string suffix = attempt == 1
                    ? Suffix
                    : Suffix + "_" + attempt.ToString(CultureInfo.InvariantCulture);

                string candidate = Path.Combine(Directory, baseName + suffix + extension);

                if (IsAvailable(candidate, sourcePath, reservedPaths))
                {
                    return candidate;
                }
            }

            throw new IOException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "无法为「{0}」找到可用的输出文件名（尝试了 {1} 次）。",
                    Path.GetFileName(sourcePath),
                    MaxAttempts));
        }

        /// <summary>目标路径文本（供界面预览显示，形如 “D:\out\photo_批量.jpg”）。</summary>
        public string PreviewPath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(Directory))
            {
                return null;
            }

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "image";
            }

            string extension = ForcedExtension ?? Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            return Path.Combine(Directory, baseName + Suffix + extension);
        }

        private static bool IsAvailable(string candidate, string sourcePath, ISet<string> reservedPaths)
        {
            // 规则 2：绝不覆盖源文件本身。
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 规则 1a：本次运行已分配。
            if (reservedPaths != null && reservedPaths.Contains(candidate))
            {
                return false;
            }

            // 规则 1b：磁盘上已存在。
            try
            {
                return !File.Exists(candidate);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 过滤文件名/后缀里的非法字符。
        ///
        /// 后缀是用户手输的，直接拼进路径会抛 ArgumentException（Win7 上表现为"另存为失败"），
        /// 因此这里主动替换掉而不是靠调用方约束。
        /// </summary>
        public static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool bad = false;

                for (int j = 0; j < invalid.Length; j++)
                {
                    if (invalid[j] == c)
                    {
                        bad = true;
                        break;
                    }
                }

                builder.Append(bad ? '_' : c);
            }

            return builder.ToString().Trim();
        }

        /// <summary>把扩展名规范化成 “.jpg” 形式；空值返回 null（表示沿用源扩展名）。</summary>
        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            string normalized = extension.Trim();
            normalized = SanitizeFileNamePart(normalized);

            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.StartsWith(".", StringComparison.Ordinal) ? normalized : "." + normalized;
        }
    }
}
