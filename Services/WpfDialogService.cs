using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PSText.Services.Interfaces;

namespace PSText.Services
{
    /// <summary>
    /// 基于 WPF / Win32 通用对话框的对话框服务实现。
    /// Win7 兼容说明：
    ///   - 不使用 Vista 之后才有的 IFileDialog/CommonOpenFileDialog；
    ///   - 文件夹选择通过 FolderBrowserDialog（System.Windows.Forms）实现，
    ///     Win7 上行为稳定，避免 COM 互操作差异。
    /// </summary>
    public sealed class WpfDialogService : IDialogService
    {
        private const string ImageOpenFilter =
            "所有支持的图片|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.tif;*.tiff;*.gif"
            + "|JPEG 图片|*.jpg;*.jpeg;*.jpe;*.jfif"
            + "|PNG 图片|*.png"
            + "|BMP 图片|*.bmp;*.dib"
            + "|TIFF 图片|*.tif;*.tiff"
            + "|GIF 图片|*.gif"
            + "|所有文件|*.*";

        public string ShowOpenImageDialog(string title, string initialDirectory)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "打开图片" : title,
                Filter = ImageOpenFilter,
                Multiselect = false,
                CheckFileExists = true,
                CheckPathExists = true
            };

            ApplyInitialDirectory(dialog, initialDirectory);

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public IReadOnlyList<string> ShowOpenImagesDialog(string title, string initialDirectory)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "打开图片" : title,
                Filter = ImageOpenFilter,
                Multiselect = true,
                CheckFileExists = true,
                CheckPathExists = true
            };

            ApplyInitialDirectory(dialog, initialDirectory);

            if (dialog.ShowDialog() != true)
            {
                return new List<string>();
            }

            return dialog.FileNames ?? (IReadOnlyList<string>)new List<string>();
        }

        public string ShowSaveImageDialog(
            string title,
            string initialDirectory,
            string suggestedFileName,
            string filter,
            string defaultExtension)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "保存图片" : title,
                Filter = string.IsNullOrWhiteSpace(filter) ? ImageOpenFilter : filter,
                FileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "未命名.png" : suggestedFileName,
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true
            };

            if (!string.IsNullOrWhiteSpace(defaultExtension))
            {
                dialog.DefaultExt = defaultExtension.TrimStart('.');
            }

            ApplyInitialDirectory(dialog, initialDirectory);

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string ShowFolderDialog(string title, string initialDirectory)
        {
            // 使用 WinForms 的 FolderBrowserDialog：Win7 上最稳定，无 COM 版本差异。
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title;
                dialog.ShowNewFolderButton = true;

                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    dialog.SelectedPath = initialDirectory;
                }

                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        public ConfirmResult Confirm(string message, string title)
        {
            MessageBoxResult result = MessageBox.Show(
                message ?? string.Empty,
                string.IsNullOrWhiteSpace(title) ? "确认" : title,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            switch (result)
            {
                case MessageBoxResult.Yes:
                    return ConfirmResult.Yes;
                case MessageBoxResult.No:
                    return ConfirmResult.No;
                default:
                    return ConfirmResult.Cancel;
            }
        }

        public void ShowError(string message, string title)
        {
            MessageBox.Show(
                message ?? string.Empty,
                string.IsNullOrWhiteSpace(title) ? "错误" : title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        public void ShowInformation(string message, string title)
        {
            MessageBox.Show(
                message ?? string.Empty,
                string.IsNullOrWhiteSpace(title) ? "提示" : title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        public void ShowException(string message, Exception exception, string title)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(message ?? "发生未预期的错误。");

            if (exception != null)
            {
                builder.AppendLine();
                builder.AppendLine("错误详情：");
                builder.AppendLine(exception.Message);

                if (exception.InnerException != null)
                {
                    builder.AppendLine("内部错误：" + exception.InnerException.Message);
                }
            }

            ShowError(builder.ToString(), title);
        }

        /// <summary>设置初始目录；目录不存在时静默忽略（避免弹窗报错）。</summary>
        private static void ApplyInitialDirectory(FileDialog dialog, string initialDirectory)
        {
            if (dialog == null || string.IsNullOrWhiteSpace(initialDirectory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(initialDirectory))
                {
                    dialog.InitialDirectory = initialDirectory;
                }
            }
            catch (ArgumentException)
            {
                // 非法路径忽略即可。
            }
            catch (IOException)
            {
                // 网络路径不可达时忽略。
            }
        }
    }
}
