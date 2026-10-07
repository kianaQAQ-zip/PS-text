namespace PSText.ViewModels
{
    /// <summary>缩放模式：决定窗口尺寸变化时如何自动调整缩放比例。</summary>
    public enum ZoomMode
    {
        /// <summary>自由缩放（滚轮 / 按钮），窗口变化时不自动调整。</summary>
        Free = 0,

        /// <summary>适应窗口：按视口大小自动缩放，窗口变化时重新计算。</summary>
        FitToWindow,

        /// <summary>原始大小：1 个图片像素对应 1 个屏幕像素（考虑屏幕 DPI）。</summary>
        ActualSize
    }
}
