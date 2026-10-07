using System;
using System.Collections.Generic;

namespace PSText.Services.Interfaces
{
    /// <summary>三步确认对话框的结果。</summary>
    public enum ConfirmResult
    {
        Yes,
        No,
        Cancel
    }

    /// <summary>
    /// 对话框服务：把 UI 弹窗与 ViewModel 解耦，便于测试时替换为桩实现。
    /// 实现必须保证所有方法都在 UI 线程调用。
    /// </summary>
    public interface IDialogService
    {
        /// <summary>选择单个图片文件；取消返回 null。</summary>
        string ShowOpenImageDialog(string title, string initialDirectory);

        /// <summary>选择多个图片文件；取消返回空集合。</summary>
        IReadOnlyList<string> ShowOpenImagesDialog(string title, string initialDirectory);

        /// <summary>选择保存路径；取消返回 null。</summary>
        string ShowSaveImageDialog(string title, string initialDirectory, string suggestedFileName, string filter, string defaultExtension);

        /// <summary>选择文件夹；取消返回 null。</summary>
        string ShowFolderDialog(string title, string initialDirectory);

        /// <summary>确认对话框（是 / 否 / 取消）。</summary>
        ConfirmResult Confirm(string message, string title);

        /// <summary>错误提示。</summary>
        void ShowError(string message, string title);

        /// <summary>普通信息提示。</summary>
        void ShowInformation(string message, string title);

        /// <summary>严重错误（附带异常详情）。</summary>
        void ShowException(string message, Exception exception, string title);
    }
}
