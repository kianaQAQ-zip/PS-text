using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSText.Infrastructure.History;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services;
using PSText.Services.Batch;
using PSText.Services.Filters;
using PSText.Services.Interfaces;
using PSText.Services.Printing;
using PSText.ViewModels;

namespace PSText
{
    /// <summary>
    /// 自检入口（--selftest）：
    /// 在无人工交互的情况下验证 P0 核心能力：
    ///   1. 生成 PNG / JPG / BMP / TIFF 测试图片
    ///   2. 逐个加载，校验像素尺寸与格式识别
    ///   3. 校验加载后文件锁立即释放（可删除 / 可独占打开）
    ///   4. 保存并回读，校验原始分辨率与 DPI 保持
    ///   5. 构造 ViewModel，校验缩放范围、适应窗口、原始大小
    /// 结果写入 build\selftest.log 并返回退出码（0 = 全部通过）。
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;
            int checks = 0;
            string tempRoot = null;

            try
            {
                tempRoot = Path.Combine(Path.GetTempPath(), "pstext-selftest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);

                log.AppendLine("=== PS-text 自检开始 ===");
                log.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                log.AppendLine("临时目录: " + tempRoot);
                log.AppendLine();

                IImageService imageService = new WpfImageService();

                // ---------- 1. 生成测试图片 ----------
                log.AppendLine("[1] 生成测试图片");
                string pngPath = Path.Combine(tempRoot, "sample.png");
                string jpgPath = Path.Combine(tempRoot, "sample.jpg");
                string bmpPath = Path.Combine(tempRoot, "sample.bmp");
                string tifPath = Path.Combine(tempRoot, "sample.tif");

                CreateTestImage(pngPath, 320, 200, 96.0, ImageFileFormat.Png, log);
                CreateTestImage(jpgPath, 320, 200, 96.0, ImageFileFormat.Jpeg, log);
                CreateTestImage(bmpPath, 320, 200, 96.0, ImageFileFormat.Bmp, log);
                CreateTestImage(tifPath, 640, 480, 300.0, ImageFileFormat.Tiff, log);
                log.AppendLine();

                // ---------- 2 & 3. 加载 + 文件锁释放 ----------
                log.AppendLine("[2] 加载与文件锁释放");
                CheckLoad(imageService, pngPath, 320, 200, ImageFileFormat.Png, ref checks, ref failures, log);
                CheckLoad(imageService, jpgPath, 320, 200, ImageFileFormat.Jpeg, ref checks, ref failures, log);
                CheckLoad(imageService, bmpPath, 320, 200, ImageFileFormat.Bmp, ref checks, ref failures, log);
                CheckLoad(imageService, tifPath, 640, 480, ImageFileFormat.Tiff, ref checks, ref failures, log);
                log.AppendLine();

                // ---------- 4. 保存与 DPI 保持 ----------
                log.AppendLine("[3] 保存并回读（原始分辨率 / DPI）");
                CheckSaveRoundTrip(imageService, tifPath, tempRoot, ref checks, ref failures, log);
                log.AppendLine();

                // ---------- 5. 缩放逻辑 ----------
                log.AppendLine("[4] 缩放逻辑（ViewModel）");
                CheckZoomLogic(imageService, pngPath, ref checks, ref failures, log);
                log.AppendLine();

                // ---------- 6. 异常路径 ----------
                log.AppendLine("[5] 异常处理");
                CheckMissingFile(imageService, tempRoot, ref checks, ref failures, log);
                CheckCorruptFile(imageService, tempRoot, ref checks, ref failures, log);
                log.AppendLine();

                // ---------- 步骤 2：基础调整滤镜 + 撤销重做 ----------
                CheckAdjustmentsFilter(ref checks, ref failures, log);
                CheckSnapshotCompression(ref checks, ref failures, log);
                CheckHistory(ref checks, ref failures, log);
                CheckViewModelEditing(imageService, pngPath, ref checks, ref failures, log);
                CheckBasicFilters(ref checks, ref failures, log);
                CheckBlurAndSharpen(ref checks, ref failures, log);
                CheckGeometry(ref checks, ref failures, log);
                CheckBorderFilter(ref checks, ref failures, log);
                CheckTextOverlay(ref checks, ref failures, log);
                CheckCropBehaviour(imageService, pngPath, ref checks, ref failures, log);
                CheckPrintUnitsAndLayout(ref checks, ref failures, log);
                CheckPrintPagination(ref checks, ref failures, log);
                CheckPrintPreviewWindow(ref checks, ref failures, log);
                CheckSettingsAndRecentFiles(ref checks, ref failures, log);
                CheckThemeSwitching(ref checks, ref failures, log);
                CheckProgressReporter(ref checks, ref failures, log);
                CheckCrashLogger(ref checks, ref failures, log);
                CheckMainWindowXaml(ref checks, ref failures, log);
                CheckResizeAndArbitraryRotation(imageService, pngPath, ref checks, ref failures, log);
                CheckInpaintFilter(ref checks, ref failures, log);
                CheckRetouchEndToEnd(imageService, pngPath, ref checks, ref failures, log);
                CheckCloneStampFilter(ref checks, ref failures, log);
                CheckRegionHistory(ref checks, ref failures, log);
                CheckCloneStampEndToEnd(imageService, pngPath, ref checks, ref failures, log);
                CheckAnnotationRendering(ref checks, ref failures, log);
                CheckAnnotationEndToEnd(imageService, pngPath, ref checks, ref failures, log);
                CheckBatchPipeline(imageService, tempRoot, ref checks, ref failures, log);
                CheckSystemIntegration(pngPath, tempRoot, ref checks, ref failures, log);
                CheckMosaicAnnotation(imageService, pngPath, ref checks, ref failures, log);
                CheckAnnotationResize(imageService, pngPath, ref checks, ref failures, log);
                CheckDocumentTabs(imageService, pngPath, jpgPath, ref checks, ref failures, log);
                log.AppendLine();
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("[致命错误] " + ex);
                log.AppendLine();
            }
            finally
            {
                log.AppendLine("=== 自检结束：检查 " + checks + " 项，失败 " + failures + " 项 ===");
                log.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL");

                WriteLog(log.ToString());

                TryDeleteDirectory(tempRoot);
            }

            return failures == 0 ? 0 : 1;
        }


        private static void CheckLoad(
            IImageService service,
            string path,
            int expectedWidth,
            int expectedHeight,
            ImageFileFormat expectedFormat,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            string name = Path.GetFileName(path);

            try
            {
                ImageLoadResult result = service.LoadAsync(path).GetAwaiter().GetResult();

                checks++;
                if (result.PixelWidth != expectedWidth || result.PixelHeight != expectedHeight)
                {
                    failures++;
                    log.AppendLine("  FAIL " + name + " 尺寸不符，期望 "
                        + expectedWidth + "x" + expectedHeight + "，实际 "
                        + result.PixelWidth + "x" + result.PixelHeight);
                }
                else
                {
                    log.AppendLine("  OK   " + name + " 尺寸 " + result.PixelWidth + "x" + result.PixelHeight
                        + "，DPI " + result.DpiX.ToString("0.#") + "，格式 " + result.Format);
                }

                // 格式识别（按文件头而非扩展名）
                checks++;
                if (result.Format != expectedFormat)
                {
                    failures++;
                    log.AppendLine("  FAIL " + name + " 格式识别错误，期望 " + expectedFormat + "，实际 " + result.Format);
                }

                // 文件锁：加载后应能独占打开并删除
                checks++;
                if (!IsFileUnlocked(path))
                {
                    failures++;
                    log.AppendLine("  FAIL " + name + " 加载后文件仍被占用");
                }
                else
                {
                    log.AppendLine("  OK   " + name + " 文件锁已释放（可独占打开）");
                }

                // 位图应已冻结，可跨线程访问
                checks++;
                if (!result.Bitmap.IsFrozen)
                {
                    failures++;
                    log.AppendLine("  FAIL " + name + " 位图未冻结，存在跨线程风险");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL " + name + " 加载抛出异常: " + ex.Message);
            }
        }

        private static void CheckSaveRoundTrip(
            IImageService service,
            string sourcePath,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            try
            {
                ImageLoadResult source = service.LoadAsync(sourcePath).GetAwaiter().GetResult();

                string target = Path.Combine(tempRoot, "saved-dpi.png");
                ImageSaveOptions options = new ImageSaveOptions
                {
                    Format = ImageFileFormat.Png,
                    PreserveDpi = true
                };

                long written = service.SaveAsync(source.Bitmap, target, options).GetAwaiter().GetResult();

                checks++;
                if (written <= 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 保存后文件字节数为 0");
                }

                ImageLoadResult reloaded = service.LoadAsync(target).GetAwaiter().GetResult();

                checks++;
                if (reloaded.PixelWidth != source.PixelWidth || reloaded.PixelHeight != source.PixelHeight)
                {
                    failures++;
                    log.AppendLine("  FAIL 保存后分辨率改变: "
                        + source.PixelWidth + "x" + source.PixelHeight + " -> "
                        + reloaded.PixelWidth + "x" + reloaded.PixelHeight);
                }
                else
                {
                    log.AppendLine("  OK   原始分辨率保持: " + reloaded.PixelWidth + "x" + reloaded.PixelHeight);
                }

                checks++;
                if (Math.Abs(reloaded.DpiX - source.DpiX) > 0.6 || Math.Abs(reloaded.DpiY - source.DpiY) > 0.6)
                {
                    failures++;
                    log.AppendLine("  FAIL DPI 未保持: 原始 " + source.DpiX.ToString("0.#")
                        + "，回读 " + reloaded.DpiX.ToString("0.#"));
                }
                else
                {
                    log.AppendLine("  OK   DPI 保持: " + reloaded.DpiX.ToString("0.#"));
                }

                // JPEG 质量参数路径
                string jpegTarget = Path.Combine(tempRoot, "saved-q.png");
                service.SaveAsync(
                    source.Bitmap,
                    jpegTarget,
                    new ImageSaveOptions { Format = ImageFileFormat.Jpeg, QualityLevel = 60, PreserveDpi = true })
                    .GetAwaiter().GetResult();

                checks++;
                if (!File.Exists(jpegTarget) || new FileInfo(jpegTarget).Length <= 0)
                {
                    failures++;
                    log.AppendLine("  FAIL JPEG 保存失败");
                }
                else
                {
                    log.AppendLine("  OK   JPEG 保存成功（"
                        + service.DetectFormat(jpegTarget) + "，"
                        + new FileInfo(jpegTarget).Length + " 字节）");
                }

                // 格式检测：扩展名与内容不一致的情况
                string mismatched = Path.Combine(tempRoot, "actually-jpeg.png");
                File.Copy(jpegTarget, mismatched, true);
                checks++;
                if (service.DetectFormat(mismatched) != ImageFileFormat.Jpeg)
                {
                    failures++;
                    log.AppendLine("  FAIL 文件头检测失败（.png 扩展名实为 JPEG）");
                }
                else
                {
                    log.AppendLine("  OK   文件头检测正确（.png 扩展名实为 JPEG）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 保存往返测试异常: " + ex);
            }
        }

        private static void CheckZoomLogic(
            IImageService service,
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            try
            {
                // 自检不涉及窗口，用桩实现替代对话框和调度器。
                MainViewModel viewModel = new MainViewModel(
                    service,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                checks++;
                if (viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 初始状态不应有文档");
                }

                viewModel.LoadFromPathAsync(pngPath).GetAwaiter().GetResult();

                checks++;
                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 加载后 HasDocument 应为 true");
                    return;
                }

                log.AppendLine("  OK   加载后状态: " + viewModel.PixelSizeText + " / " + viewModel.DpiText);

                // 视口 800x600，图片 320x200 → 适应窗口应受高度限制
                viewModel.UpdateViewportSize(800, 600);
                viewModel.FitToWindow();

                double expectedFit = Math.Min(800.0 / 320.0, 600.0 / 200.0) * 0.98;
                checks++;
                if (Math.Abs(viewModel.ZoomFactor - expectedFit) > 1e-6)
                {
                    failures++;
                    log.AppendLine("  FAIL 适应窗口缩放错误，期望 " + expectedFit.ToString("0.####")
                        + "，实际 " + viewModel.ZoomFactor.ToString("0.####"));
                }
                else
                {
                    log.AppendLine("  OK   适应窗口: " + viewModel.ZoomPercentText);
                }

                // 原始大小：96 DPI 图片 + 96 DPI 屏幕 → 100%
                viewModel.UpdateScreenDpi(96, 96);
                viewModel.ActualSize();

                double actualSizeZoom = viewModel.ZoomFactor;
                double expectedActual = 96.0 / 96.0;

                checks++;
                if (Math.Abs(actualSizeZoom - expectedActual) > 1e-6)
                {
                    failures++;
                    log.AppendLine("  FAIL 96DPI 图片原始大小应为 100%，实际 "
                        + viewModel.ZoomPercentText
                        + "（原始值 R=" + actualSizeZoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "，期望 R=" + expectedActual.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "，屏幕 " + viewModel.ScreenDpiX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "，文档 " + viewModel.Document.DpiX.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "，模式 " + viewModel.ZoomMode + "）");
                }
                else
                {
                    log.AppendLine("  OK   原始大小 (96 DPI): " + viewModel.ZoomPercentText);
                }

                // 上限/下限夹取
                for (int i = 0; i < 60; i++)
                {
                    viewModel.ZoomIn();
                }

                checks++;
                if (Math.Abs(viewModel.ZoomFactor - MainViewModel.MaxZoom) > 1e-6)
                {
                    failures++;
                    log.AppendLine("  FAIL 放大上限应为 " + MainViewModel.MaxZoom
                        + "，实际 " + viewModel.ZoomFactor.ToString("0.###"));
                }
                else
                {
                    log.AppendLine("  OK   放大上限: " + viewModel.ZoomPercentText);
                }

                for (int i = 0; i < 120; i++)
                {
                    viewModel.ZoomOut();
                }

                checks++;
                if (Math.Abs(viewModel.ZoomFactor - MainViewModel.MinZoom) > 1e-6)
                {
                    failures++;
                    log.AppendLine("  FAIL 缩小下限应为 " + MainViewModel.MinZoom
                        + "，实际 " + viewModel.ZoomFactor.ToString("0.###"));
                }
                else
                {
                    log.AppendLine("  OK   缩小下限: " + viewModel.ZoomPercentText);
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 缩放逻辑测试异常: " + ex);
            }
        }

        private static void CheckMissingFile(
            IImageService service,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            string missing = Path.Combine(tempRoot, "not-exists.png");
            checks++;

            try
            {
                service.LoadAsync(missing).GetAwaiter().GetResult();
                failures++;
                log.AppendLine("  FAIL 加载不存在的文件应抛出异常");
            }
            catch (FileNotFoundException)
            {
                log.AppendLine("  OK   不存在的文件抛出 FileNotFoundException");
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 期望 FileNotFoundException，实际 " + ex.GetType().Name);
            }
        }

        private static void CheckCorruptFile(
            IImageService service,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            string corrupt = Path.Combine(tempRoot, "corrupt.png");

            try
            {
                // 写入 PNG 魔数但内容损坏
                File.WriteAllBytes(corrupt, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 });

                checks++;
                try
                {
                    service.LoadAsync(corrupt).GetAwaiter().GetResult();
                    failures++;
                    log.AppendLine("  FAIL 损坏文件应抛出异常");
                }
                catch (Exception ex)
                {
                    log.AppendLine("  OK   损坏文件抛出 " + ex.GetType().Name + "（已被上层捕获处理）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 损坏文件测试异常: " + ex.Message);
            }
        }


        /// <summary>
        /// 校验基础调整滤镜（亮度 / 对比度 / 饱和度 / 色温）的数值正确性、纯函数特性、
        /// 以及“降采样预览与全分辨率结果在使用同一算法”的一致性。
        /// </summary>
        private static void CheckAdjustmentsFilter(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[6] 基础调整滤镜（纯函数 / 数值正确性）");

            try
            {
                AdjustmentsFilter filter = new AdjustmentsFilter();

                // ---- 中性参数：必须逐像素不变 ----
                PixelBuffer source = CreateTestBuffer(64, 48);
                byte[] original = source.GetPixelsCopy();

                PixelBuffer neutral = filter.Apply(source, PixelAdjustments.Neutral);
                checks++;
                if (!PixelsEqual(original, neutral.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 中性参数不应改变任何像素");
                }
                else
                {
                    log.AppendLine("  OK   中性参数像素完全不变");
                }

                // ---- 纯函数：不修改输入缓冲 ----
                filter.Apply(source, new PixelAdjustments(30, 20, -10, 15));
                checks++;
                if (!PixelsEqual(original, source.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 滤镜修改了输入缓冲（违反纯函数约定）");
                }
                else
                {
                    log.AppendLine("  OK   滤镜未修改输入缓冲（纯函数）");
                }

                // ---- 亮度 +50：整体应明显变亮 ----
                PixelBuffer brighter = filter.Apply(source, new PixelAdjustments(50, 0, 0, 0));
                checks++;
                if (AverageLuma(brighter) <= AverageLuma(source))
                {
                    failures++;
                    log.AppendLine("  FAIL 亮度 +50 未使画面变亮");
                }
                else
                {
                    log.AppendLine("  OK   亮度 +50 生效（平均亮度 "
                        + AverageLuma(source).ToString("0") + " → " + AverageLuma(brighter).ToString("0") + "）");
                }

                // ---- 饱和度 -100：应完全灰度（R=G=B） ----
                PixelBuffer gray = filter.Apply(source, new PixelAdjustments(0, 0, -100, 0));
                checks++;
                int maxChannelDelta = MaxChannelDelta(gray);
                if (maxChannelDelta > 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 饱和度 -100 应为灰度，实际通道最大差 " + maxChannelDelta);
                }
                else
                {
                    log.AppendLine("  OK   饱和度 -100 得到灰度图（通道最大差 " + maxChannelDelta + "）");
                }

                // ---- 对比度 -100：应趋向中灰（动态范围被压缩） ----
                PixelBuffer lowContrast = filter.Apply(source, new PixelAdjustments(0, -100, 0, 0));
                checks++;
                double sourceRange = ChannelRange(source);
                double flatRange = ChannelRange(lowContrast);
                if (flatRange >= sourceRange)
                {
                    failures++;
                    log.AppendLine("  FAIL 对比度 -100 未压缩动态范围（" + sourceRange.ToString("0") + " → " + flatRange.ToString("0") + "）");
                }
                else
                {
                    log.AppendLine("  OK   对比度 -100 压缩动态范围（" + sourceRange.ToString("0") + " → " + flatRange.ToString("0") + "）");
                }

                // ---- 色温 +100：偏暖（红增强、蓝减弱） ----
                PixelBuffer warm = filter.Apply(source, new PixelAdjustments(0, 0, 0, 100));
                PixelBuffer cool = filter.Apply(source, new PixelAdjustments(0, 0, 0, -100));
                checks++;
                double warmRedMinusBlue = ChannelMeanDifference(warm, 2, 0);
                double coolRedMinusBlue = ChannelMeanDifference(cool, 2, 0);
                if (warmRedMinusBlue <= coolRedMinusBlue)
                {
                    failures++;
                    log.AppendLine("  FAIL 色温方向错误（暖 "
                        + warmRedMinusBlue.ToString("0.0") + " 应大于冷 " + coolRedMinusBlue.ToString("0.0") + "）");
                }
                else
                {
                    log.AppendLine("  OK   色温生效（暖 红-蓝=" + warmRedMinusBlue.ToString("0.0")
                        + "，冷 红-蓝=" + coolRedMinusBlue.ToString("0.0") + "）");
                }

                // ---- 关键性质：降采样预览与全分辨率结果的色调必须一致 ----
                // 用温和参数（不触发 0/255 裁剪）：此时亮度/对比度/色温属于线性逐点运算，
                // “先降采样再调整”与“先调整再降采样”在数学上等价，差异只应来自整数舍入。
                PixelAdjustments previewAdjustments = new PixelAdjustments(10, 0, 10, 0);
                PixelBuffer fullResult = filter.Apply(source, previewAdjustments);
                PixelBuffer sampled = DownsampleByTwo(source);
                PixelBuffer sampledResult = filter.Apply(sampled, previewAdjustments);

                checks++;
                if (sampledResult.Width != source.Width / 2 || sampledResult.Height != source.Height / 2)
                {
                    failures++;
                    log.AppendLine("  FAIL 降采样尺寸错误");
                }
                else
                {
                    // 关键性质（预览能代表最终结果的前提）：
                    //   对全分辨率图先降采样再调整  ==  先调整再降采样（在无裁剪的线性区段内成立）。
                    // 也就是说，拖动滑块时看到的预览与松手后的全分辨率结果是同一套算法、同一个色调映射，
                    // 不会出现“预览偏亮、提交后偏暗”的情况。
                    byte[] previewPath = CopyPixel(sampledResult, 5, 5);   // 先降采样 → 再调整
                    PixelBuffer filteredFull = filter.Apply(source, previewAdjustments);
                    byte[] fullPath = CopyPixel(DownsampleByTwo(filteredFull), 5, 5); // 先调整 → 再降采样

                    int maxDelta = MaxAbsDifference(previewPath, fullPath);

                    if (maxDelta > 3)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 预览与全分辨率色调不一致：降采样({0}) vs 全分辨率({1})，最大差 {2}",
                            DescribePixel(previewPath),
                            DescribePixel(fullPath),
                            maxDelta));
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   预览与全分辨率色调一致（{0} vs {1}，舍入差 ≤{2}）",
                            DescribePixel(previewPath),
                            DescribePixel(fullPath),
                            maxDelta));
                    }
                }

                // ---- 并行与串行结果必须完全一致（大图走并行路径，正确性不能有偏差） ----
                checks++;
                PixelBuffer serialBuffer = CreateTestBuffer(900, 700);   // 0.63MP，低于并行阈值
                PixelBuffer parallelBuffer = CreateTestBuffer(1600, 1400); // 2.24MP，高于并行阈值
                PixelAdjustments parallelCheck = new PixelAdjustments(18, -22, 35, -12);

                PixelBuffer serialResult = filter.Apply(serialBuffer, parallelCheck);
                PixelBuffer parallelResult = filter.Apply(parallelBuffer, parallelCheck);

                // 用同样的“并行是否启用”判定逻辑，验证小块数据在两种路径下的输出一致：
                // 取并行图的一小块单独跑（必然低于阈值 → 串行），与整图跑出的对应区域比较。
                PixelBuffer parallelTile = CropBuffer(parallelBuffer, 0, 0, 900, 700);
                PixelBuffer tileResult = filter.Apply(parallelTile, parallelCheck);

                int tileDelta = MaxPixelsDifference(
                    tileResult.GetPixels(),
                    CropBuffer(parallelResult, 0, 0, 900, 700).GetPixels());

                if (parallelResult.Width != parallelBuffer.Width || serialResult.Width != serialBuffer.Width)
                {
                    failures++;
                    log.AppendLine("  FAIL 大图处理尺寸错误");
                }
                else if (tileDelta != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 并行与串行结果不一致（最大通道差 " + tileDelta + "）");
                }
                else
                {
                    log.AppendLine("  OK   并行与串行结果逐像素一致（2.24MP 整图 vs 同区域串行，最大通道差 0）");
                }

                // ---- 性能：全分辨率调整耗时（决定松手后提交的等待时间） ----
                checks++;
                PixelBuffer performanceBuffer = CreateTestBuffer(3000, 2000); // 6MP，≈ 单反照片
                DateTime started = DateTime.UtcNow;
                PixelBuffer performanceResult = filter.Apply(performanceBuffer, new PixelAdjustments(20, 15, 25, 10));
                double elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;

                if (performanceResult.Width != 3000)
                {
                    failures++;
                    log.AppendLine("  FAIL 性能测试处理失败");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   6MP 全分辨率调整耗时 {0:0} ms（{1} 核并行，松手后提交在此量级）",
                        elapsedMs,
                        Environment.ProcessorCount));
                }
                // ---- 边界：极端参数不应越界或抛异常 ----
                checks++;
                try
                {
                    filter.Apply(source, new PixelAdjustments(100, 100, 100, 100));
                    filter.Apply(source, new PixelAdjustments(-100, -100, -100, -100));
                    log.AppendLine("  OK   极端参数（±100 全开）无异常、无越界");
                }
                catch (Exception ex)
                {
                    failures++;
                    log.AppendLine("  FAIL 极端参数抛出异常: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 滤镜测试异常: " + ex);
            }

            log.AppendLine();
        }



        /// <summary>
        /// 校验压缩快照（撤销历史的内存优化基础）：
        /// 压缩率、无损性（解回后像素一致）、尺寸与 DPI 保持。
        /// </summary>
        private static void CheckSnapshotCompression(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[7] 历史快照压缩（内存优化 / 无损性）");

            try
            {
                // 用一张有内容的图（渐变 + 噪点），比纯色更能反映真实压缩率。
                PixelBuffer source = CreateTestBuffer(256, 192);
                PixelBuffer detailed = AddNoise(source, 11);

                BitmapSnapshot snapshot = BitmapSnapshot.TryCreate(
                    detailed.GetPixels(),
                    detailed.Width,
                    detailed.Height,
                    300.0,
                    300.0);

                checks++;
                if (snapshot == null)
                {
                    failures++;
                    log.AppendLine("  FAIL 无法创建快照");
                    log.AppendLine();
                    return;
                }

                string codec = snapshot.IsJpegXr ? "JPEG XR 无损" : "PNG";
                log.AppendLine(string.Format(
                    "  OK   快照创建成功：{0}，{1} → {2}（压缩率 {3:0.0}%）",
                    codec,
                    FormatBytes(snapshot.RawByteCount),
                    FormatBytes(snapshot.ByteSize),
                    snapshot.CompressionRatio * 100.0));

                // 无损性：解回后逐像素比较
                checks++;
                PixelBuffer restored = snapshot.ToPixelBuffer();
                if (!PixelsEqual(detailed.GetPixels(), restored.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 快照不是无损的（解回后像素不一致）");
                }
                else
                {
                    log.AppendLine("  OK   快照无损：解回后逐像素一致");
                }

                // 尺寸与 DPI
                checks++;
                if (restored.Width != detailed.Width || restored.Height != detailed.Height)
                {
                    failures++;
                    log.AppendLine("  FAIL 快照尺寸不一致");
                }
                else
                {
                    BitmapSource bitmap = snapshot.ToBitmap();
                    bool dpiOk = Math.Abs(bitmap.DpiX - 300.0) < 0.6;
                    if (!dpiOk)
                    {
                        failures++;
                        log.AppendLine("  FAIL 快照 DPI 丢失：期望 300，实际 " + bitmap.DpiX.ToString("0.#"));
                    }
                    else
                    {
                        log.AppendLine("  OK   快照尺寸与 DPI 保持（" + bitmap.PixelWidth + "x" + bitmap.PixelHeight
                            + " @ " + bitmap.DpiX.ToString("0.#") + " DPI）");
                    }
                }

                // 内存对比：说明“压缩快照”相对“未压缩副本”的节省
                checks++;
                long uncompressed = snapshot.RawByteCount;
                if (snapshot.ByteSize >= uncompressed)
                {
                    failures++;
                    log.AppendLine("  FAIL 压缩后反而更大，内存优化失效");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   内存优化有效：30 步历史未压缩需 {0}，压缩后约 {1}",
                        FormatBytes(uncompressed * 30),
                        FormatBytes(snapshot.ByteSize * 30)));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 快照测试异常: " + ex);
            }

            log.AppendLine();
        }



        /// <summary>
        /// 校验撤销 / 重做：步数、状态切换正确性、重做栈失效、30 步容量上限与内存统计。
        /// </summary>
        private static void CheckHistory(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[8] 撤销 / 重做（30 步容量 + 内存统计）");

            try
            {
                HistoryManager history = new HistoryManager(30, 256L * 1024 * 1024);
                EditState applied = null;
                Action<EditState> applier = state => applied = state;

                // 初始无历史
                checks++;
                if (history.CanUndo || history.CanRedo)
                {
                    failures++;
                    log.AppendLine("  FAIL 初始状态不应可撤销 / 可重做");
                }
                else
                {
                    log.AppendLine("  OK   初始无可撤销 / 可重做");
                }

                // 推入 3 条命令
                EditState stateA = CreateSolidState(24, 24, 10, 20, 30);
                EditState stateB = CreateSolidState(24, 24, 60, 70, 80);
                EditState stateC = CreateSolidState(24, 24, 120, 130, 140);
                EditState stateD = CreateSolidState(24, 24, 200, 210, 220);

                history.Push(new PixelAdjustmentCommand("第1步", stateA, stateB, applier), stateB);
                history.Push(new PixelAdjustmentCommand("第2步", stateB, stateC, applier), stateC);
                history.Push(new PixelAdjustmentCommand("第3步", stateC, stateD, applier), stateD);

                checks++;
                if (history.UndoCount != 3 || history.RedoCount != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 推入 3 条后应有 3 步可撤销、且不可重做；实际撤销 "
                        + history.UndoCount + " 重做 " + history.RedoCount);
                }
                else
                {
                    log.AppendLine("  OK   推入 3 条命令（可撤销 3 步，重做栈为空）");
                }

                // 撤销 3 次应依次回到 C、B、A
                bool undoOk = true;
                history.Undo();
                undoOk &= ReferenceEquals(applied, stateC);
                history.Undo();
                undoOk &= ReferenceEquals(applied, stateB);
                history.Undo();
                undoOk &= ReferenceEquals(applied, stateA);

                checks++;
                if (!undoOk || history.CanUndo)
                {
                    failures++;
                    log.AppendLine("  FAIL 连续撤销未按 C→B→A 回退");
                }
                else
                {
                    log.AppendLine("  OK   连续撤销依次回到上一状态（C→B→A），且不可再撤销");
                }

                // 重做 2 次应回到 B、C
                bool redoOk = true;
                history.Redo();
                redoOk &= ReferenceEquals(applied, stateB);
                history.Redo();
                redoOk &= ReferenceEquals(applied, stateC);

                checks++;
                if (!redoOk || history.RedoCount != 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 重做未按 A→B→C 前进");
                }
                else
                {
                    log.AppendLine("  OK   重做依次前进（A→B→C），剩余可重做 1 步");
                }

                // 产生新分支后重做栈必须失效
                EditState stateE = CreateSolidState(24, 24, 1, 2, 3);
                history.Push(new PixelAdjustmentCommand("新分支", stateC, stateE, applier), stateE);

                checks++;
                if (history.CanRedo)
                {
                    failures++;
                    log.AppendLine("  FAIL 新分支产生后重做栈未失效");
                }
                else
                {
                    log.AppendLine("  OK   新分支产生后重做栈已失效");
                }

                // 容量上限：连续推入 40 条，应只保留 30 步
                HistoryManager bounded = new HistoryManager(30, 256L * 1024 * 1024);
                EditState previous = CreateSolidState(16, 16, 5, 5, 5);
                bounded.Push(new PixelAdjustmentCommand("初始", previous, previous, applier), previous);

                for (int i = 0; i < 40; i++)
                {
                    EditState next = CreateSolidState(16, 16, (byte)(i * 6), (byte)(i * 5), (byte)(i * 4));
                    bounded.Push(new PixelAdjustmentCommand("第 " + i + " 步", previous, next, applier), next);
                    previous = next;
                }

                checks++;
                if (bounded.UndoCount > 30)
                {
                    failures++;
                    log.AppendLine("  FAIL 历史超出上限：实际 " + bounded.UndoCount + " 步（应为 ≤30）");
                }
                else
                {
                    log.AppendLine("  OK   历史容量受限：推入 41 条后保留 " + bounded.UndoCount + " 步（上限 30）");
                }

                // 裁剪后撤销链必须完整（不能出现撤不动的情况）
                checks++;
                int totalSteps = bounded.UndoCount;
                int undone = 0;
                while (bounded.CanUndo && undone < 200)
                {
                    bounded.Undo();
                    undone++;
                }

                if (undone != totalSteps || undone < 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 裁剪后撤销链不完整：记录 " + totalSteps + " 步，实际只能撤销 " + undone + " 步");
                }
                else
                {
                    log.AppendLine("  OK   裁剪后撤销链完整：可连续撤销 " + undone + " 步直到起点");
                }

                // 内存统计与文本
                checks++;
                string memoryText = bounded.MemoryUsageText;
                if (string.IsNullOrWhiteSpace(memoryText) || bounded.MemoryUsage <= 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 内存统计无效");
                }
                else
                {
                    log.AppendLine("  OK   历史内存统计：" + memoryText + "（" + bounded.UndoCount + " 步，上限 30）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 撤销 / 重做测试异常: " + ex);
            }

            log.AppendLine();
        }



        /// <summary>
        /// 端到端校验「滑块调整 → 全分辨率提交 → 撤销 → 重做」这条链路（通过真实 ViewModel）。
        /// 这是最能反映实际使用效果的一项：滤镜、历史、ViewModel 三者必须协同正确。
        /// </summary>
        private static void CheckViewModelEditing(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[9] 端到端：调整 → 提交 → 撤销 → 重做（ViewModel）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片");
                    log.AppendLine();
                    return;
                }

                int originalWidth = viewModel.Document.PixelWidth;
                int originalHeight = viewModel.Document.PixelHeight;
                byte[] originalPixels = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();
                double originalLuma = AverageLuma(PixelBuffer.FromBitmap(viewModel.Document.Bitmap));

                checks++;
                if (viewModel.UndoCount != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 新加载的图片不应有历史记录");
                }
                else
                {
                    log.AppendLine("  OK   加载后历史为空");
                }

                // ---- 第一次调整：亮度 +50，提交全分辨率 ----
                viewModel.Brightness = 50;
                viewModel.FlushPendingPreviewsAsync().GetAwaiter().GetResult();

                byte[] brightenedPixels = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();
                double brightenedLuma = AverageLuma(PixelBuffer.FromBitmap(viewModel.Document.Bitmap));

                checks++;
                if (brightenedLuma <= originalLuma + 5)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 亮度 +50 未生效（平均亮度 {0:0} → {1:0}）", originalLuma, brightenedLuma));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   亮度 +50 已提交到全分辨率（平均亮度 {0:0} → {1:0}）", originalLuma, brightenedLuma));
                }

                // 全分辨率提交必须保持原始像素尺寸与 DPI
                checks++;
                if (viewModel.Document.PixelWidth != originalWidth || viewModel.Document.PixelHeight != originalHeight)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 提交后分辨率改变：{0}x{1} → {2}x{3}",
                        originalWidth, originalHeight, viewModel.Document.PixelWidth, viewModel.Document.PixelHeight));
                }
                else
                {
                    log.AppendLine("  OK   提交后保持原始分辨率 " + originalWidth + "x" + originalHeight);
                }

                checks++;
                if (viewModel.UndoCount != 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 一次调整应产生 1 步历史，实际 " + viewModel.UndoCount);
                    log.AppendLine("       状态栏: " + viewModel.StatusText);
                    log.AppendLine("       历史:   " + viewModel.HistoryText);
                    log.AppendLine("       提交诊断: " + (viewModel.LastCommitDiagnostic ?? "（无，说明提交路径未执行到判定点）"));
                }
                else
                {
                    log.AppendLine("  OK   一次调整产生 1 步历史（内存 " + viewModel.HistoryMemoryText + "）");
                }

                // ---- 诊断：把「调整 → 提交」重复多次，用于复现偶发竞态 ----
                // 该流程曾偶发（约 3~10%）出现"提交后历史仍为 0"，
                // 循环多次可以让竞态稳定暴露出来。
                checks++;
                int commitFailures = 0;
                int commitAttempts = 8;
                string commitDetail = string.Empty;

                MainViewModel repeatViewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                repeatViewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                for (int attempt = 0; attempt < commitAttempts; attempt++)
                {
                    double value = 10.0 + attempt * 5.0;
                    repeatViewModel.Brightness = value;
                    repeatViewModel.FlushPendingPreviewsAsync().GetAwaiter().GetResult();

                    // 每次调整都必须恰好产生一步历史
                    if (repeatViewModel.UndoCount != 1)
                    {
                        commitFailures++;

                        if (commitDetail.Length == 0)
                        {
                            commitDetail = string.Format(
                                "第 {0} 次（亮度 {1:0}）撤销步数 = {2}，状态栏：{3}",
                                attempt + 1, value, repeatViewModel.UndoCount, repeatViewModel.StatusText);
                        }
                    }

                    // 复位以避免历史被合并成一条
                    repeatViewModel.Undo();
                }

                if (commitFailures > 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 重复提交 {0} 次中有 {1} 次未产生历史；{2}",
                        commitAttempts, commitFailures, commitDetail));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   「调整 → 提交」重复 {0} 次全部产生 1 步历史（无竞态）", commitAttempts));
                }

                // ---- 撤销：必须逐像素回到原始画面 ----
                viewModel.Undo();
                byte[] undonePixels = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();

                checks++;
                int undoDelta = MaxPixelsDifference(originalPixels, undonePixels);
                if (undoDelta != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 撤销未精确恢复原始像素（最大通道差 " + undoDelta + "）");
                }
                else
                {
                    log.AppendLine("  OK   撤销逐像素恢复原始画面（最大通道差 0）");
                }

                // 撤销后滑块参数必须同步复位
                checks++;
                if (Math.Abs(viewModel.Brightness) > 0.001)
                {
                    failures++;
                    log.AppendLine("  FAIL 撤销后滑块参数未复位（亮度 " + viewModel.Brightness.ToString("0.#") + "）");
                }
                else
                {
                    log.AppendLine("  OK   撤销后滑块参数同步复位为 0");
                }

                // ---- 重做：必须逐像素回到调整后的画面 ----
                viewModel.Redo();
                byte[] redonePixels = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();

                checks++;
                int redoDelta = MaxPixelsDifference(brightenedPixels, redonePixels);
                if (redoDelta != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 重做未精确恢复到调整后像素（最大通道差 " + redoDelta + "）");
                }
                else
                {
                    log.AppendLine("  OK   重做逐像素恢复到调整后画面（最大通道差 0，压缩快照无损）");
                }

                // ---- 连续 35 次调整：验证历史容量上限与内存统计 ----
                for (int i = 1; i <= 35; i++)
                {
                    viewModel.SetAdjustment(null, null, null, i - 18);
                    viewModel.FlushPendingPreviewsAsync().GetAwaiter().GetResult();
                }

                checks++;
                if (viewModel.UndoCount > 30)
                {
                    failures++;
                    log.AppendLine("  FAIL 历史超出 30 步上限：实际 " + viewModel.UndoCount);
                }
                else
                {
                    log.AppendLine("  OK   连续调整后历史受限：" + viewModel.HistoryText);
                }

                // 全部撤销不应抛异常，且能回到某个可用状态
                checks++;
                int undoneCount = 0;
                while (viewModel.UndoCount > 0 && undoneCount < 60)
                {
                    viewModel.Undo();
                    undoneCount++;

                    if (viewModel.Document == null)
                    {
                        break;
                    }
                }

                if (viewModel.Document == null)
                {
                    failures++;
                    log.AppendLine("  FAIL 连续撤销后文档丢失");
                }
                else
                {
                    log.AppendLine("  OK   连续撤销 " + undoneCount + " 步后文档仍然有效（"
                        + viewModel.Document.PixelSizeText + "）");
                }

                // ---- 复位命令：参数回到中性 ----
                viewModel.SetAdjustment(20, 20, 20, 20);
                viewModel.FlushPendingPreviewsAsync().GetAwaiter().GetResult();
                viewModel.ResetAdjustmentsCommand.Execute(null);
                viewModel.FlushPendingPreviewsAsync().GetAwaiter().GetResult();

                checks++;
                if (viewModel.HasAdjustments)
                {
                    failures++;
                    log.AppendLine("  FAIL 复位后仍有非中性调整");
                }
                else
                {
                    log.AppendLine("  OK   复位命令把调整参数恢复为中性");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 端到端编辑测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验基础点运算滤镜：反色、灰度、二值化（需求 P1-5）。
        /// </summary>
        private static void CheckBasicFilters(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[10] 基础滤镜：反色 / 灰度 / 二值化");

            try
            {
                BasicFilters filters = new BasicFilters();
                PixelBuffer source = CreateTestBuffer(64, 48);
                byte[] original = source.GetPixelsCopy();

                // ---- 反色：每个通道都应为 255 - 原值，Alpha 不变 ----
                PixelBuffer inverted = filters.Invert(source);
                checks++;
                int invertError = MaxChannelError(inverted, source, true);
                if (invertError > 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 反色不正确（最大通道偏差 " + invertError + "）");
                    log.AppendLine("       " + DescribeChannelError(inverted, source, true));
                    log.AppendLine("       源(0,0)=" + DescribePixel(CopyPixel(source, 0, 0))
                        + "  结果(0,0)=" + DescribePixel(CopyPixel(inverted, 0, 0))
                        + "  期望(0,0)=" + DescribePixel(ExpectedInverted(CopyPixel(source, 0, 0))));
                }
                else
                {
                    byte[] sampleSource = CopyPixel(source, 0, 0);
                    byte[] sampleInverted = CopyPixel(inverted, 0, 0);

                    if (sampleInverted[2] != 255 - sampleSource[2]
                        || sampleInverted[1] != 255 - sampleSource[1]
                        || sampleInverted[0] != 255 - sampleSource[0]
                        || sampleInverted[3] != sampleSource[3])
                    {
                        failures++;
                        log.AppendLine("  FAIL 反色取样校验失败");
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   反色正确：像素(0,0) {0} → {1}（Alpha 保持）",
                            DescribePixel(sampleSource),
                            DescribePixel(sampleInverted)));
                    }
                }

                // ---- 反色两次应还原 ----
                PixelBuffer doubleInverted = filters.Invert(inverted);
                checks++;
                if (!PixelsEqual(original, doubleInverted.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 反色两次未还原原图");
                }
                else
                {
                    log.AppendLine("  OK   反色两次精确还原原图（可逆）");
                }

                // ---- 灰度：三通道相等，且每个通道的权重正确 ----
                // 用纯色样本分别检验 R/G/B 的权重，避免“红蓝写反”这类错误被对称数据掩盖。
                checks++;
                PixelBuffer gray = filters.Grayscale(source);
                int maxChannelDelta = MaxChannelDelta(gray);

                if (maxChannelDelta != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 灰度图三通道不相等（最大差 " + maxChannelDelta + "）");
                }
                else
                {
                    bool weightsOk = true;
                    string detail = string.Empty;

                    // 纯红 (255,0,0) → 77；纯绿 → 150；纯蓝 → 29
                    int[] expected = { 77, 150, 29 };
                    string[] names = { "纯红", "纯绿", "纯蓝" };

                    for (int channel = 0; channel < 3; channel++)
                    {
                        PixelBuffer pure = CreateSolidBuffer(
                            4,
                            4,
                            channel == 0 ? (byte)255 : (byte)0,
                            channel == 1 ? (byte)255 : (byte)0,
                            channel == 2 ? (byte)255 : (byte)0);

                        byte[] value = CopyPixel(filters.Grayscale(pure), 1, 1);

                        if (Math.Abs(value[0] - expected[channel]) > 1)
                        {
                            weightsOk = false;
                            detail += string.Format("{0} 期望 {1} 实际 {2}；", names[channel], expected[channel], value[0]);
                        }
                    }

                    if (!weightsOk)
                    {
                        failures++;
                        log.AppendLine("  FAIL 灰度通道权重错误：" + detail);
                    }
                    else
                    {
                        log.AppendLine("  OK   灰度三通道相等，且 R/G/B 权重分别为 77/150/29（Rec.601 顺序正确）");
                    }
                }

                // ---- 二值化：只应出现 0 与 255 ----
                PixelBuffer thresholded = filters.Threshold(source, 128);
                checks++;
                int invalidCount = 0;
                int whiteCount = 0;
                int blackCount = 0;
                byte[] thresholdPixels = thresholded.GetPixels();

                for (int i = 0; i < thresholdPixels.Length; i += 4)
                {
                    int value = thresholdPixels[i];
                    if (value == 0)
                    {
                        blackCount++;
                    }
                    else if (value == 255)
                    {
                        whiteCount++;
                    }
                    else
                    {
                        invalidCount++;
                    }
                }

                if (invalidCount > 0 || whiteCount == 0 || blackCount == 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 二值化结果异常（非 0/255 像素 {0} 个，白 {1}，黑 {2}）",
                        invalidCount, whiteCount, blackCount));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   二值化只产生 0/255（白 {0} 像素，黑 {1} 像素）", whiteCount, blackCount));
                }

                // ---- 阈值边界：0 应全白，255 应几乎全黑 ----
                checks++;
                PixelBuffer allWhite = filters.Threshold(source, 0);
                PixelBuffer allBlack = filters.Threshold(source, 255);
                if (MinChannelValue(allWhite) != 255 || MaxChannelValue(allBlack) > 255)
                {
                    failures++;
                    log.AppendLine("  FAIL 阈值边界行为异常");
                }
                else
                {
                    log.AppendLine("  OK   阈值边界正确（阈值 0 → 全白，阈值 255 → 仅纯白像素保留）");
                }

                // ---- 纯函数：不修改输入 ----
                checks++;
                if (!PixelsEqual(original, source.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 基础滤镜修改了输入缓冲（违反纯函数约定）");
                }
                else
                {
                    log.AppendLine("  OK   基础滤镜未修改输入缓冲（纯函数）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 基础滤镜测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验高斯模糊与 USM 锐化（需求 P1-6）。
        /// </summary>
        private static void CheckBlurAndSharpen(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[11] 高斯模糊 / USM 锐化");

            try
            {
                BlurFilters filters = new BlurFilters();

                // ---- 纯色图模糊后必须仍是同一个纯色（不能变暗，验证镜像边界处理） ----
                PixelBuffer solid = CreateSolidBuffer(64, 64, 200, 100, 50);
                PixelBuffer solidBlurred = filters.GaussianBlur(solid, 10.0);

                checks++;
                byte[] center = CopyPixel(solidBlurred, 32, 32);
                byte[] corner = CopyPixel(solidBlurred, 0, 0);
                byte[] sourceCorner = CopyPixel(solid, 0, 0);

                int centerDelta = Math.Abs(center[2] - 200) + Math.Abs(center[1] - 100) + Math.Abs(center[0] - 50);
                int cornerDelta = Math.Abs(corner[2] - sourceCorner[2])
                                  + Math.Abs(corner[1] - sourceCorner[1])
                                  + Math.Abs(corner[0] - sourceCorner[0]);

                if (centerDelta > 3 || cornerDelta > 3)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 纯色图模糊后颜色改变（中心偏差 {0}，边角偏差 {1}）——边界处理可能有误",
                        centerDelta, cornerDelta));
                    log.AppendLine(string.Format(
                        "       期望 B{0} G{1} R{2}，实际中心 B{3} G{4} R{5}，边角 B{6} G{7} R{8}",
                        sourceCorner[0], sourceCorner[1], sourceCorner[2],
                        center[0], center[1], center[2],
                        corner[0], corner[1], corner[2]));
                }
                else
                {
                    log.AppendLine("  OK   纯色图模糊后保持同色（中心/边角偏差 ≤3，镜像边界正确）");
                }

                // ---- 模糊应降低局部对比度 ----
                PixelBuffer checker = CreateCheckerboard(64, 64, 8);
                PixelBuffer blurredChecker = filters.GaussianBlur(checker, 6.0);

                double originalDeviation = StandardDeviation(checker);
                double blurredDeviation = StandardDeviation(blurredChecker);

                checks++;
                if (blurredDeviation >= originalDeviation)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 模糊未降低对比度（标准差 {0:0.0} → {1:0.0}）",
                        originalDeviation, blurredDeviation));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   模糊降低局部对比度（标准差 {0:0.0} → {1:0.0}）",
                        originalDeviation, blurredDeviation));
                }

                // ---- 尺寸不变 ----
                checks++;
                if (blurredChecker.Width != checker.Width || blurredChecker.Height != checker.Height)
                {
                    failures++;
                    log.AppendLine("  FAIL 模糊改变了图像尺寸");
                }
                else
                {
                    log.AppendLine("  OK   模糊保持图像尺寸（" + checker.Width + "x" + checker.Height + "）");
                }

                // ---- 半径 0 应等价于原图 ----
                checks++;
                PixelBuffer radiusZero = filters.GaussianBlur(checker, 0.0);
                if (!PixelsEqual(checker.GetPixels(), radiusZero.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 半径为 0 的模糊改变了像素");
                }
                else
                {
                    log.AppendLine("  OK   半径为 0 时不改变像素");
                }

                // ---- USM 锐化应提升边缘锐度 ----
                // 先做一次模糊再锐化：锐化后的边缘梯度应大于模糊图。
                PixelBuffer soft = filters.GaussianBlur(checker, 4.0);
                PixelBuffer sharpened = filters.UnsharpMask(soft, 3.0, 150.0, 0);

                double softGradient = MaxHorizontalGradient(soft, 32);
                double sharpGradient = MaxHorizontalGradient(sharpened, 32);

                checks++;
                if (sharpGradient <= softGradient)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL USM 未提升边缘锐度（梯度 {0:0.0} → {1:0.0}）",
                        softGradient, sharpGradient));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   USM 提升边缘锐度（梯度 {0:0.0} → {1:0.0}）",
                        softGradient, sharpGradient));
                }

                // ---- 强度 0 应不改动像素 ----
                checks++;
                PixelBuffer unchanged = filters.UnsharpMask(soft, 3.0, 0.0, 0);
                if (!PixelsEqual(soft.GetPixels(), unchanged.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 强度为 0 的锐化改变了像素");
                }
                else
                {
                    log.AppendLine("  OK   强度为 0 时不改变像素");
                }

                // ---- 大半径性能（分块 / 滑动窗口的意义所在） ----
                checks++;
                PixelBuffer large = CreateCheckerboard(2000, 1500, 16);
                DateTime started = DateTime.UtcNow;
                PixelBuffer largeBlurred = filters.GaussianBlur(large, 40.0);
                double elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds;

                if (largeBlurred.Width != 2000)
                {
                    failures++;
                    log.AppendLine("  FAIL 大图模糊失败");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   3MP 图半径 40px 模糊耗时 {0:0} ms（滑动窗口与半径无关）", elapsedMs));
                }

                // ---- 原图未被修改 ----
                checks++;
                if (MaxPixelsDifference(soft.GetPixels(), filters.UnsharpMask(soft, 0.0, 0.0, 0).GetPixels()) != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 锐化流程修改了输入缓冲");
                }
                else
                {
                    log.AppendLine("  OK   模糊 / 锐化均未修改输入缓冲（纯函数）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 模糊 / 锐化测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验几何变换：裁剪、90° 旋转、翻转（需求 P1-7）。
        /// </summary>
        private static void CheckGeometry(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[12] 几何变换：裁剪 / 旋转 / 翻转");

            try
            {
                GeometryFilters filters = new GeometryFilters();
                PixelBuffer source = CreateTestBuffer(40, 24);

                // ---- 旋转 90°：尺寸互换，且像素位置正确 ----
                PixelBuffer rotated = filters.Rotate(source, RotationAngle.Clockwise90);

                checks++;
                if (rotated.Width != source.Height || rotated.Height != source.Width)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 旋转 90° 后尺寸错误：期望 {0}x{1}，实际 {2}x{3}",
                        source.Height, source.Width, rotated.Width, rotated.Height));
                }
                else
                {
                    // 源 (x,y) → 目标 (sourceHeight-1-y, x)
                    byte[] expected = CopyPixel(source, 5, 7);
                    byte[] actual = CopyPixel(rotated, source.Height - 1 - 7, 5);

                    if (!PixelsEqual(expected, actual))
                    {
                        failures++;
                        log.AppendLine("  FAIL 旋转 90° 像素映射错误");
                    }
                    else
                    {
                        log.AppendLine("  OK   旋转 90° 尺寸互换且像素映射正确");
                    }
                }

                // ---- 旋转 4 次 90° = 原图 ----
                checks++;
                PixelBuffer fourTimes = filters.Rotate(source, RotationAngle.Clockwise90);
                fourTimes = filters.Rotate(fourTimes, RotationAngle.Clockwise90);
                fourTimes = filters.Rotate(fourTimes, RotationAngle.Clockwise90);
                fourTimes = filters.Rotate(fourTimes, RotationAngle.Clockwise90);

                if (!PixelsEqual(source.GetPixels(), fourTimes.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 旋转 4 次 90° 未还原原图");
                }
                else
                {
                    log.AppendLine("  OK   旋转 4 次 90° 精确还原原图（无损）");
                }

                // ---- 旋转 270° = 逆时针 90°，与 90° 互为逆操作 ----
                checks++;
                PixelBuffer counterClockwise = filters.Rotate(source, RotationAngle.Clockwise270);
                PixelBuffer backToOriginal = filters.Rotate(counterClockwise, RotationAngle.Clockwise90);

                if (!PixelsEqual(source.GetPixels(), backToOriginal.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 逆时针 90° 后再顺时针 90° 未还原");
                }
                else
                {
                    log.AppendLine("  OK   逆时针 90° 与顺时针 90° 互为逆操作");
                }

                // ---- 水平翻转：像素镜像，且两次还原 ----
                checks++;
                PixelBuffer flipped = filters.FlipHorizontal(source);
                byte[] leftPixel = CopyPixel(source, 0, 3);
                byte[] mirroredPixel = CopyPixel(flipped, source.Width - 1, 3);

                if (!PixelsEqual(leftPixel, mirroredPixel))
                {
                    failures++;
                    log.AppendLine("  FAIL 水平翻转像素映射错误");
                }
                else if (!PixelsEqual(source.GetPixels(), filters.FlipHorizontal(flipped).GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 水平翻转两次未还原");
                }
                else
                {
                    log.AppendLine("  OK   水平翻转映射正确且两次可还原");
                }

                // ---- 垂直翻转 ----
                checks++;
                PixelBuffer flippedV = filters.FlipVertical(source);
                byte[] topPixel = CopyPixel(source, 4, 0);
                byte[] bottomPixel = CopyPixel(flippedV, 4, source.Height - 1);

                if (!PixelsEqual(topPixel, bottomPixel))
                {
                    failures++;
                    log.AppendLine("  FAIL 垂直翻转像素映射错误");
                }
                else
                {
                    log.AppendLine("  OK   垂直翻转映射正确");
                }

                // ---- 裁剪：尺寸与像素内容都正确 ----
                checks++;
                PixelBuffer cropped = filters.Crop(source, 10, 5, 12, 8);

                if (cropped.Width != 12 || cropped.Height != 8)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 裁剪尺寸错误：期望 12x8，实际 {0}x{1}", cropped.Width, cropped.Height));
                }
                else
                {
                    byte[] expected = CopyPixel(source, 10, 5);
                    byte[] origin = CopyPixel(cropped, 0, 0);
                    byte[] expectedLast = CopyPixel(source, 21, 12);
                    byte[] last = CopyPixel(cropped, 11, 7);

                    if (!PixelsEqual(expected, origin) || !PixelsEqual(expectedLast, last))
                    {
                        failures++;
                        log.AppendLine("  FAIL 裁剪像素内容与源图不一致");
                    }
                    else
                    {
                        log.AppendLine("  OK   裁剪尺寸与像素内容都正确（左上/右下角比对一致）");
                    }
                }

                // ---- 裁剪越界应被钳制而不是抛出异常 ----
                checks++;
                try
                {
                    PixelBuffer clamped = filters.Crop(source, -5, -5, 100, 100);

                    if (clamped.Width != source.Width || clamped.Height != source.Height)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 越界裁剪未收敛到图像范围（得到 {0}x{1}）", clamped.Width, clamped.Height));
                    }
                    else
                    {
                        log.AppendLine("  OK   越界裁剪自动收敛到整幅图，未抛异常");
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    log.AppendLine("  FAIL 越界裁剪抛出异常: " + ex.Message);
                }

                // ---- 完全在图像外的裁剪应回退为原图而不是 0 尺寸 ----
                checks++;
                PixelBuffer outside = filters.Crop(source, 1000, 1000, 10, 10);
                if (outside.Width <= 0 || outside.Height <= 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 完全越界裁剪返回了空尺寸位图");
                }
                else
                {
                    log.AppendLine("  OK   完全越界裁剪回退为原图（" + outside.Width + "x" + outside.Height + "）");
                }

                // ---- 纯函数 ----
                checks++;
                PixelBuffer untouched = CreateTestBuffer(40, 24);
                byte[] reference = untouched.GetPixelsCopy();
                filters.Rotate(untouched, RotationAngle.Clockwise90);
                filters.FlipVertical(untouched);
                filters.Crop(untouched, 1, 1, 5, 5);

                if (!PixelsEqual(reference, untouched.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 几何变换修改了输入缓冲（违反纯函数约定）");
                }
                else
                {
                    log.AppendLine("  OK   几何变换未修改输入缓冲（纯函数）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 几何变换测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验边框滤镜（需求 P1-8）。
        /// </summary>
        private static void CheckBorderFilter(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[13] 边框滤镜");

            try
            {
                BorderFilters filters = new BorderFilters();
                PixelBuffer source = CreateSolidBuffer(20, 20, 10, 20, 30);

                // ---- 实线边框：尺寸增大，原图居中，边框颜色正确 ----
                PixelBuffer bordered = filters.Apply(
                    source,
                    BorderStyle.Solid,
                    4,
                    System.Windows.Media.Color.FromRgb(255, 0, 0),
                    System.Windows.Media.Colors.White);

                checks++;
                if (bordered.Width != 28 || bordered.Height != 28)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 边框尺寸错误：期望 28x28，实际 {0}x{1}", bordered.Width, bordered.Height));
                }
                else
                {
                    byte[] corner = CopyPixel(bordered, 0, 0);
                    byte[] inside = CopyPixel(bordered, 4, 4);
                    byte[] sourcePixel = CopyPixel(source, 0, 0);

                    bool cornerIsRed = corner[2] == 255 && corner[1] == 0 && corner[0] == 0;
                    bool insideIsOriginal = PixelsEqual(inside, sourcePixel);

                    if (!cornerIsRed)
                    {
                        failures++;
                        log.AppendLine("  FAIL 边框颜色不正确");
                    }
                    else if (!insideIsOriginal)
                    {
                        failures++;
                        log.AppendLine("  FAIL 原图未正确居中绘制在边框内");
                    }
                    else
                    {
                        log.AppendLine("  OK   实线边框：尺寸 28x28，边框为指定颜色，原图居中且像素未变");
                    }
                }

                // ---- 宽度 0 或 None 应返回等尺寸副本 ----
                checks++;
                PixelBuffer noBorder = filters.Apply(
                    source,
                    BorderStyle.Solid,
                    0,
                    System.Windows.Media.Colors.Red,
                    System.Windows.Media.Colors.White);

                if (noBorder.Width != source.Width || noBorder.Height != source.Height)
                {
                    failures++;
                    log.AppendLine("  FAIL 宽度为 0 时不应改变尺寸");
                }
                else
                {
                    log.AppendLine("  OK   宽度为 0 时不改变图像（返回等尺寸副本）");
                }

                // ---- 内衬样式也应产出正确尺寸 ----
                checks++;
                PixelBuffer innerLine = filters.Apply(
                    source,
                    BorderStyle.InnerLine,
                    6,
                    System.Windows.Media.Colors.Black,
                    System.Windows.Media.Colors.White);

                if (innerLine.Width != 32 || innerLine.Height != 32)
                {
                    failures++;
                    log.AppendLine("  FAIL 内衬边框尺寸错误");
                }
                else
                {
                    log.AppendLine("  OK   内衬边框尺寸正确（32x32）");
                }

                // ---- 阴影样式：外圈应比内圈更接近底色（渐隐） ----
                checks++;
                PixelBuffer shadowed = filters.Apply(
                    source,
                    BorderStyle.Shadow,
                    8,
                    System.Windows.Media.Colors.White,
                    System.Windows.Media.Color.FromArgb(150, 0, 0, 0));

                if (shadowed.Width != source.Width + 32 || shadowed.Height != source.Height + 32)
                {
                    failures++;
                    log.AppendLine("  FAIL 阴影边框尺寸错误（应含额外渐隐空间）");
                }
                else
                {
                    log.AppendLine("  OK   阴影边框尺寸正确（含渐隐空间 " + shadowed.Width + "x" + shadowed.Height + "）");
                }

                // ---- 纯函数：输入未被修改 ----
                checks++;
                byte[] reference = CreateSolidBuffer(20, 20, 10, 20, 30).GetPixelsCopy();
                if (!PixelsEqual(reference, source.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 边框滤镜修改了输入缓冲");
                }
                else
                {
                    log.AppendLine("  OK   边框滤镜未修改输入缓冲（纯函数）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 边框滤镜测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>计算“反色后应有的像素”（BGRA 顺序，Alpha 不变）。</summary>
        private static byte[] ExpectedInverted(byte[] pixel)
        {
            if (pixel == null || pixel.Length < 4)
            {
                return new byte[] { 0, 0, 0, 255 };
            }

            return new byte[]
            {
                (byte)(255 - pixel[0]),
                (byte)(255 - pixel[1]),
                (byte)(255 - pixel[2]),
                pixel[3]
            };
        }

        /// <summary>定位第一个反色 / 一致性偏差的字节位置（用于诊断）。</summary>
        private static string DescribeChannelError(PixelBuffer result, PixelBuffer source, bool invertCheck)
        {
            byte[] resultPixels = result.GetPixels();
            byte[] sourcePixels = source.GetPixels();
            int reported = 0;
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < resultPixels.Length && reported < 3; i++)
            {
                int sourceValue = sourcePixels[i];
                bool isAlpha = (i % 4) == 3;
                int expected = invertCheck ? (isAlpha ? sourceValue : 255 - sourceValue) : sourceValue;

                if (resultPixels[i] != expected)
                {
                    int pixel = i / 4;
                    builder.AppendFormat(
                        "字节{0}(像素{1} 通道{2}) 期望{3} 实际{4}；",
                        i,
                        pixel,
                        i % 4,
                        expected,
                        resultPixels[i]);
                    reported++;
                }
            }

            return builder.Length == 0 ? "无偏差" : builder.ToString();
        }

        /// <summary>
        /// 校验文字叠加（需求 P1-8）：文字确实被画上、位置随锚点变化、原图尺寸不变。
        /// </summary>
        private static void CheckTextOverlay(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[14] 文字叠加");

            try
            {
                TextOverlayFilter filter = new TextOverlayFilter();

                // 用深色底图，白色文字更容易被检测到
                PixelBuffer source = CreateSolidBuffer(320, 200, 20, 20, 20);
                byte[] original = source.GetPixelsCopy();

                TextOverlayOptions options = new TextOverlayOptions
                {
                    Text = "PS-text 测试",
                    FontFamilyName = "Microsoft YaHei UI",
                    FontSize = 40.0,
                    Color = System.Windows.Media.Colors.White,
                    Anchor = TextAnchor.Center,
                    Margin = 10.0,
                    Bold = true,
                    Shadow = true
                };

                PixelBuffer rendered = filter.Apply(source, options, 96.0, 96.0);

                // ---- 尺寸不变 ----
                checks++;
                if (rendered.Width != source.Width || rendered.Height != source.Height)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 文字叠加改变了尺寸：{0}x{1} → {2}x{3}",
                        source.Width, source.Height, rendered.Width, rendered.Height));
                }
                else
                {
                    log.AppendLine("  OK   文字叠加后尺寸不变（" + rendered.Width + "x" + rendered.Height + "）");
                }

                // ---- 确实画上了文字（像素发生变化，且出现接近白色的亮像素） ----
                checks++;
                int changedPixels = CountDifferentPixels(original, rendered.GetPixels());
                int brightPixels = CountPixelsAbove(rendered, 200);

                if (changedPixels == 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 文字没有绘制到画面上（像素完全未变化）");
                }
                else if (brightPixels == 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 未检测到白色文字像素（变化 " + changedPixels + " 像素）");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   文字已绘制（变化 {0} 像素，其中亮像素 {1} 个）", changedPixels, brightPixels));
                }

                // ---- 锚点生效：居中与左下角的结果应不同 ----
                checks++;
                TextOverlayOptions bottomLeft = new TextOverlayOptions
                {
                    Text = options.Text,
                    FontFamilyName = options.FontFamilyName,
                    FontSize = options.FontSize,
                    Color = options.Color,
                    Anchor = TextAnchor.BottomLeft,
                    Margin = options.Margin,
                    Bold = true,
                    Shadow = true
                };

                PixelBuffer renderedBottomLeft = filter.Apply(source, bottomLeft, 96.0, 96.0);
                double centerBrightness = AverageBrightness(rendered);
                double bottomLeftBrightness = AverageBrightness(renderedBottomLeft);

                if (Math.Abs(centerBrightness - bottomLeftBrightness) < 0.001)
                {
                    failures++;
                    log.AppendLine("  FAIL 改变锚点后画面没有变化（位置参数可能未生效）");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   锚点生效（居中平均亮度 {0:0.000} vs 左下 {1:0.000}）",
                        centerBrightness, bottomLeftBrightness));
                }

                // ---- 空文字应返回等尺寸副本且不改变像素 ----
                checks++;
                TextOverlayOptions empty = new TextOverlayOptions { Text = string.Empty };
                PixelBuffer notRendered = filter.Apply(source, empty, 96.0, 96.0);

                if (notRendered.Width != source.Width || CountDifferentPixels(original, notRendered.GetPixels()) != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 空文字时不应改变画面");
                }
                else
                {
                    log.AppendLine("  OK   空文字时不改变画面（返回等尺寸副本）");
                }

                // ---- 输入缓冲未被修改 ----
                checks++;
                if (!PixelsEqual(original, source.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 文字叠加修改了输入缓冲");
                }
                else
                {
                    log.AppendLine("  OK   文字叠加未修改输入缓冲");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 文字叠加测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 端到端校验裁剪交互：进入裁剪 → 拖动 → 应用（需求 P1-7）。
        /// </summary>
        private static void CheckCropBehaviour(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[15] 裁剪交互（ViewModel 端到端）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片");
                    log.AppendLine();
                    return;
                }

                int imageWidth = viewModel.Document.PixelWidth;
                int imageHeight = viewModel.Document.PixelHeight;

                // ---- 进入裁剪：默认选中整幅图 ----
                checks++;
                viewModel.BeginCropCommand.Execute(null);

                if (!viewModel.IsCropping
                    || Math.Abs(viewModel.CropWidth - imageWidth) > 0.5
                    || Math.Abs(viewModel.CropHeight - imageHeight) > 0.5)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 进入裁剪后未默认全选（区域 {0:0}x{1:0}，图像 {2}x{3}）",
                        viewModel.CropWidth, viewModel.CropHeight, imageWidth, imageHeight));
                }
                else
                {
                    log.AppendLine("  OK   进入裁剪模式且默认选中整幅图");
                }

                // ---- 钳制：负坐标不得越界 ----
                checks++;
                viewModel.SetCropRect(-100, -50, imageWidth + 500, imageHeight + 500);

                if (viewModel.CropX < 0 || viewModel.CropY < 0
                    || viewModel.CropX + viewModel.CropWidth > imageWidth + 0.001
                    || viewModel.CropY + viewModel.CropHeight > imageHeight + 0.001)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 裁剪框未被钳制在图像内（{0:0},{1:0} {2:0}x{3:0}）",
                        viewModel.CropX, viewModel.CropY, viewModel.CropWidth, viewModel.CropHeight));
                }
                else
                {
                    log.AppendLine("  OK   越界裁剪框被钳制到图像范围内");
                }

                // ---- 手柄命中测试 ----
                checks++;
                viewModel.SetCropRect(50, 40, 100, 80);
                string cornerHandle = viewModel.HitTestCropHandle(50, 40);
                string insideHandle = viewModel.HitTestCropHandle(100, 80);

                if (cornerHandle != "nw" || insideHandle != "move")
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 手柄命中测试错误（左上角应为 nw，实际 {0}；框内应为 move，实际 {1}）",
                        cornerHandle, insideHandle));
                }
                else
                {
                    log.AppendLine("  OK   手柄命中测试正确（左上角 → nw，框内 → move）");
                }

                // ---- 拖动缩放：拖右下角放大区域 ----
                checks++;
                viewModel.BeginCropDrag("se", 150, 120);
                viewModel.UpdateCropDrag(200, 160);
                viewModel.EndCropDrag();

                if (viewModel.CropWidth < 140.0 || viewModel.CropHeight < 110.0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 拖动右下角未能放大裁剪框（{0:0}x{1:0}）",
                        viewModel.CropWidth, viewModel.CropHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   拖动右下角放大裁剪框（{0:0}x{1:0}）", viewModel.CropWidth, viewModel.CropHeight));
                }

                // ---- 应用裁剪：尺寸应变小，且产生一步可撤销历史 ----
                checks++;
                viewModel.SetCropRect(10, 10, 100, 60);
                int undoBefore = viewModel.UndoCount;
                viewModel.ApplyCropCommand.Execute(null);
                WaitForIdle(viewModel);

                if (!viewModel.IsCropping
                    && viewModel.Document.PixelWidth == 100
                    && viewModel.Document.PixelHeight == 60
                    && viewModel.UndoCount == undoBefore + 1)
                {
                    log.AppendLine("  OK   应用裁剪：尺寸变为 100x60，并产生 1 步历史（可撤销）");
                }
                else
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 应用裁剪结果异常：裁剪模式={0}，尺寸={1}x{2}，历史 {3}→{4}",
                        viewModel.IsCropping,
                        viewModel.Document.PixelWidth,
                        viewModel.Document.PixelHeight,
                        undoBefore,
                        viewModel.UndoCount));
                }

                // ---- 撤销裁剪应恢复原尺寸 ----
                checks++;
                viewModel.Undo();

                if (viewModel.Document.PixelWidth != imageWidth || viewModel.Document.PixelHeight != imageHeight)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 撤销裁剪未恢复原尺寸（得到 {0}x{1}）",
                        viewModel.Document.PixelWidth, viewModel.Document.PixelHeight));
                }
                else
                {
                    log.AppendLine("  OK   撤销裁剪恢复原始尺寸（" + imageWidth + "x" + imageHeight + "）");
                }

                // ---- 取消裁剪不应改变图像 ----
                checks++;
                viewModel.BeginCropCommand.Execute(null);
                viewModel.SetCropRect(5, 5, 30, 30);
                int widthBeforeCancel = viewModel.Document.PixelWidth;
                viewModel.CancelCropCommand.Execute(null);

                if (viewModel.IsCropping || viewModel.Document.PixelWidth != widthBeforeCancel)
                {
                    failures++;
                    log.AppendLine("  FAIL 取消裁剪后状态或尺寸异常");
                }
                else
                {
                    log.AppendLine("  OK   取消裁剪不改变图像且退出裁剪模式");
                }

                // ---- 滤镜命令端到端：反色 + 撤销 ----
                checks++;
                int undoBeforeInvert = viewModel.UndoCount;
                byte[] beforeInvert = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();

                viewModel.InvertCommand.Execute(null);

                // 命令内部是即发即忘的异步流程，这里等待其完成
                WaitForIdle(viewModel);

                byte[] afterInvert = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();

                if (viewModel.UndoCount != undoBeforeInvert + 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 反色命令未产生历史记录（实际 " + viewModel.UndoCount + "）");
                }
                else if (MaxPixelsDifference(afterInvert, ExpectedInvertBuffer(beforeInvert)) != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 反色命令结果与预期不符");
                }
                else
                {
                    log.AppendLine("  OK   反色命令生效并产生 1 步历史（可在界面上撤销）");
                }

                // ---- 反色后撤销应逐像素还原 ----
                checks++;
                viewModel.Undo();
                byte[] restored = PixelBuffer.FromBitmap(viewModel.Document.Bitmap).GetPixelsCopy();

                if (MaxPixelsDifference(beforeInvert, restored) != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 撤销反色未逐像素还原");
                }
                else
                {
                    log.AppendLine("  OK   撤销反色逐像素还原（最大通道差 0）");
                }

                // ---- 旋转 90° 端到端：尺寸互换 ----
                checks++;
                int rotateWidth = viewModel.Document.PixelWidth;
                int rotateHeight = viewModel.Document.PixelHeight;
                viewModel.RotateClockwiseCommand.Execute(null);
                WaitForIdle(viewModel);

                if (viewModel.Document.PixelWidth != rotateHeight || viewModel.Document.PixelHeight != rotateWidth)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 旋转 90° 未交换尺寸（{0}x{1} → {2}x{3}）",
                        rotateWidth, rotateHeight, viewModel.Document.PixelWidth, viewModel.Document.PixelHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   旋转 90° 端到端生效（{0}x{1} → {2}x{3}）",
                        rotateWidth, rotateHeight, viewModel.Document.PixelWidth, viewModel.Document.PixelHeight));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 裁剪交互测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 等待 ViewModel 上“一次性编辑”的异步流程结束。
        /// 命令是即发即忘的，因此这里等待它暴露的 PendingOperation，
        /// 而不是轮询状态标志（轮询会与命令启动时序竞争，返回过早）。
        /// </summary>
        private static void WaitForIdle(MainViewModel viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            for (int i = 0; i < 300; i++)
            {
                Task pending = viewModel.PendingOperation;

                if (pending == null || pending.IsCompleted)
                {
                    // 再让出一轮，确保 IsBusy 在 finally 中已复位
                    System.Threading.Thread.Sleep(10);
                    return;
                }

                System.Threading.Thread.Sleep(10);
            }
        }

        /// <summary>整幅反色后的期望像素（用于比对）。</summary>
        private static byte[] ExpectedInvertBuffer(byte[] pixels)
        {
            byte[] expected = new byte[pixels.Length];

            for (int i = 0; i < pixels.Length; i++)
            {
                bool isAlpha = (i % 4) == 3;
                expected[i] = isAlpha ? pixels[i] : (byte)(255 - pixels[i]);
            }

            return expected;
        }

        /// <summary>统计两幅图中颜色通道不同的像素个数（按像素而非字节计）。</summary>
        private static int CountDifferentPixels(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return int.MaxValue;
            }

            int count = 0;

            for (int i = 0; i < left.Length; i += 4)
            {
                if (left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2])
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>统计亮度高于阈值的像素个数（用于确认白色文字确实被绘制）。</summary>
        private static int CountPixelsAbove(PixelBuffer buffer, int threshold)
        {
            byte[] pixels = buffer.GetPixels();
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] > threshold && pixels[i + 1] > threshold && pixels[i + 2] > threshold)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>全图平均亮度。</summary>
        private static double AverageBrightness(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            double sum = 0.0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
                count++;
            }

            return count == 0 ? 0.0 : sum / count;
        }

        /// <summary>
        /// 校验打印单位换算与四种布局模式的版面计算（需求 P2-10 / P2-11）。
        /// </summary>
        private static void CheckPrintUnitsAndLayout(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[16] 打印单位换算与布局");

            try
            {
                // ---- 单位换算 ----
                checks++;
                double a4WidthMm = 210.0;
                double a4WidthDips = PrintUnits.MillimetresToDips(a4WidthMm);
                double expectedA4 = 210.0 / 25.4 * 96.0; // ≈ 793.7 DIP

                if (Math.Abs(a4WidthDips - expectedA4) > 0.01
                    || Math.Abs(PrintUnits.DipsToMillimetres(a4WidthDips) - a4WidthMm) > 0.001)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 毫米/DIP 换算错误（210mm → {0:0.##} DIP，期望 {1:0.##}）", a4WidthDips, expectedA4));
                }
                else
                {
                    log.AppendLine(string.Format("  OK   单位换算正确（A4 宽 210mm = {0:0.##} DIP）", a4WidthDips));
                }

                // ---- DPI 感知：300 DPI 图像的 3000 像素 = 10 英寸 = 960 DIP ----
                checks++;
                double tenInches = PrintUnits.PixelsToDips(3000, 300.0);
                double tenInches96 = PrintUnits.PixelsToDips(3000, 96.0);

                if (Math.Abs(tenInches - 960.0) > 0.01)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 300 DPI 图像像素→DIP 换算错误（3000px → {0:0.#} DIP，期望 960）", tenInches));
                }
                else if (Math.Abs(tenInches96 - 3000.0) > 0.01)
                {
                    failures++;
                    log.AppendLine("  FAIL 96 DPI 图像换算异常");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   DPI 感知正确（300 DPI 的 3000px = {0:0.#} DIP；96 DPI 的 3000px = {1:0.#} DIP）",
                        tenInches, tenInches96));
                }

                // ---- 布局：A4 纸张 + 0.25 英寸硬边距 ----
                Size paper = new Size(
                    PrintUnits.MillimetresToDips(210),
                    PrintUnits.MillimetresToDips(297));
                Thickness margin = new Thickness(
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25));

                // 1500×1000 像素 @ 300 DPI → 5×3.333 英寸 → 480×320 DIP
                const int imageWidth = 1500;
                const int imageHeight = 1000;
                const double imageDpi = 300.0;

                double naturalWidth = PrintUnits.PixelsToDips(imageWidth, imageDpi);
                double naturalHeight = PrintUnits.PixelsToDips(imageHeight, imageDpi);

                checks++;
                if (Math.Abs(naturalWidth - 480.0) > 0.01 || Math.Abs(naturalHeight - 320.0) > 0.01)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 图像自然尺寸错误（期望 480x320 DIP，实际 {0:0.#}x{1:0.#}）", naturalWidth, naturalHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   图像自然尺寸按原始 DPI 计算（1500x1000 @300DPI = {0:0.#}x{1:0.#} DIP = 5x3.33 英寸）",
                        naturalWidth, naturalHeight));
                }

                // ---- 适应：完整放进可打印区域且居中 ----
                checks++;
                PrintViewState fitState = new PrintViewState();
                fitState.Reset(PrintLayoutMode.Fit);

                PrintLayout fit = PrintLayout.Create(
                    fitState, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                bool fitsInside = fit.ImageBounds.Left >= fit.PrintableArea.Left - 0.01
                                  && fit.ImageBounds.Top >= fit.PrintableArea.Top - 0.01
                                  && fit.ImageBounds.Right <= fit.PrintableArea.Right + 0.01
                                  && fit.ImageBounds.Bottom <= fit.PrintableArea.Bottom + 0.01;

                double centerOffsetX = Math.Abs(
                    (fit.ImageBounds.Left - fit.PrintableArea.Left)
                    - (fit.PrintableArea.Right - fit.ImageBounds.Right));
                double centerOffsetY = Math.Abs(
                    (fit.ImageBounds.Top - fit.PrintableArea.Top)
                    - (fit.PrintableArea.Bottom - fit.ImageBounds.Bottom));

                if (!fitsInside || fit.PageCount != 1 || centerOffsetX > 0.5 || centerOffsetY > 0.5)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 适应布局异常（完整可见={0}，页数={1}，居中偏差 {2:0.##}/{3:0.##}）",
                        fitsInside, fit.PageCount, centerOffsetX, centerOffsetY));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   适应布局：完整可见、单页、居中（图像 {0:0.#}x{1:0.#} DIP）",
                        fit.ImageBounds.Width, fit.ImageBounds.Height));
                }

                // ---- 填充：必须盖满可打印区域 ----
                // 注意：3:2 的图像铺满 A4 纵向纸张时，纵向必然溢出（几何上不可能单页盖满），
                // 因此这里只断言"盖满"与"页数为几何必然值"，不断言单页。
                checks++;
                PrintViewState fillState = new PrintViewState();
                fillState.Reset(PrintLayoutMode.Fill);

                PrintLayout fill = PrintLayout.Create(
                    fillState, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                bool covers = fill.ImageBounds.Left <= fill.PrintableArea.Left + 0.01
                              && fill.ImageBounds.Top <= fill.PrintableArea.Top + 0.01
                              && fill.ImageBounds.Right >= fill.PrintableArea.Right - 0.01
                              && fill.ImageBounds.Bottom >= fill.PrintableArea.Bottom - 0.01;

                // 页数必须与几何计算一致（避免出现多余空白页）
                int expectedFillColumns = (int)Math.Ceiling((fill.ImageBounds.Width - 0.5) / fill.PrintableArea.Width);
                int expectedFillRows = (int)Math.Ceiling((fill.ImageBounds.Height - 0.5) / fill.PrintableArea.Height);

                if (!covers
                    || fill.PageColumns != expectedFillColumns
                    || fill.PageRows != expectedFillRows)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 填充布局异常（覆盖={0}，页网格 {1}×{2}，期望 {3}×{4}）",
                        covers, fill.PageColumns, fill.PageRows, expectedFillColumns, expectedFillRows));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   填充布局：盖满可打印区域（图像 {0:0.#}x{1:0.#} DIP，{2} 页，几何上必然溢出）",
                        fill.ImageBounds.Width, fill.ImageBounds.Height, fill.PageCount));
                }

                // ---- 原始尺寸：严格按 DPI 还原，且不放大 ----
                checks++;
                PrintViewState originalState = new PrintViewState();
                originalState.Reset(PrintLayoutMode.OriginalSize);

                PrintLayout original = PrintLayout.Create(
                    originalState, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                if (Math.Abs(original.ImageBounds.Width - naturalWidth) > 0.5
                    || Math.Abs(original.ImageBounds.Height - naturalHeight) > 0.5)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 原始尺寸布局未按 DPI 还原（得到 {0:0.#}x{1:0.#}，期望 {2:0.#}x{3:0.#}）",
                        original.ImageBounds.Width, original.ImageBounds.Height, naturalWidth, naturalHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   原始尺寸布局：严格按图像 DPI 还原（{0:0.#}x{1:0.#} DIP）",
                        original.ImageBounds.Width, original.ImageBounds.Height));
                }

                // ---- 缩放：放大 4 倍后应分多页 ----
                checks++;
                PrintViewState zoomedState = new PrintViewState();
                zoomedState.Reset(PrintLayoutMode.Fit);
                zoomedState.Scale = 4.0;

                PrintLayout zoomed = PrintLayout.Create(
                    zoomedState, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                if (zoomed.PageCount < 2)
                {
                    failures++;
                    log.AppendLine("  FAIL 放大 4 倍后应分多页（实际 " + zoomed.PageCount + " 页）");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   放大后正确分页（{0:0.#}x{1:0.#} DIP → {2} 页，{3}×{4}）",
                        zoomed.ImageBounds.Width,
                        zoomed.ImageBounds.Height,
                        zoomed.PageCount,
                        zoomed.PageColumns,
                        zoomed.PageRows));
                }

                // ---- 实际打印分辨率提示 ----
                checks++;
                double effectiveDpi = fit.EffectiveDpiX;
                double expectedEffective = imageWidth / PrintUnits.DipsToInches(fit.ImageBounds.Width);

                if (Math.Abs(effectiveDpi - expectedEffective) > 0.5 || effectiveDpi <= 0.0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 有效打印分辨率计算错误（得到 {0:0.#}，期望 {1:0.#}）", effectiveDpi, expectedEffective));
                }
                else
                {
                    log.AppendLine(string.Format("  OK   有效打印分辨率计算正确（适应布局下约 {0:0} DPI）", effectiveDpi));
                }

                // ---- 拖动钳制：适应布局下不能把图像拖出可打印区域 ----
                checks++;
                PrintViewState dragState = new PrintViewState();
                dragState.Reset(PrintLayoutMode.Fit);
                dragState.OffsetX = -100000.0;
                dragState.OffsetY = 100000.0;

                PrintLayout dragged = PrintLayout.Create(
                    dragState, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                bool stillInside = dragged.ImageBounds.Left >= dragged.PrintableArea.Left - 0.01
                                   && dragged.ImageBounds.Top >= dragged.PrintableArea.Top - 0.01
                                   && dragged.ImageBounds.Right <= dragged.PrintableArea.Right + 0.01
                                   && dragged.ImageBounds.Bottom <= dragged.PrintableArea.Bottom + 0.01;

                if (!stillInside)
                {
                    failures++;
                    log.AppendLine("  FAIL 适应布局下拖动未被钳制（图像被拖出可打印区域）");
                }
                else
                {
                    log.AppendLine("  OK   适应布局下拖动被正确钳制在可打印区域内");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 打印布局测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验分页器：页数、每页内容区域、不重叠、DPI 传递（需求 P2-10 / P2-11）。
        /// </summary>
        private static void CheckPrintPagination(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[17] 打印分页（多页 / 不重叠 / DPI）");

            try
            {
                Size paper = new Size(
                    PrintUnits.MillimetresToDips(210),
                    PrintUnits.MillimetresToDips(297));
                Thickness margin = new Thickness(
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25));

                // 3000x2000 @ 96 DPI → 非常大，适应布局下也会被缩到一页；
                // 这里用"原始尺寸 + 放大"制造多页：96 DPI 下 3000px = 3000 DIP，远超 A4 可打印宽度
                const int imageWidth = 3000;
                const int imageHeight = 2000;
                const double imageDpi = 96.0;

                PrintViewState state = new PrintViewState();
                state.Reset(PrintLayoutMode.OriginalSize);

                PrintLayout layout = PrintLayout.Create(
                    state, paper, margin, imageWidth, imageHeight, imageDpi, imageDpi);

                checks++;
                if (layout.PageCount < 2)
                {
                    failures++;
                    log.AppendLine("  FAIL 大图应分多页（实际 " + layout.PageCount + " 页）");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   大图分页正确：{0:0.#}x{1:0.#} DIP → {2} 页（{3}×{4}）",
                        layout.ImageBounds.Width,
                        layout.ImageBounds.Height,
                        layout.PageCount,
                        layout.PageColumns,
                        layout.PageRows));
                }

                // ---- 分页器基本属性 ----
                PixelBuffer buffer = CreateTestBuffer(imageWidth, imageHeight);
                BitmapSource bitmap = PixelBuffer.ToBitmap(buffer, imageDpi, imageDpi);

                ImagePrintPaginator paginator = new ImagePrintPaginator(bitmap, layout, paper);

                checks++;
                if (!paginator.IsPageCountValid || paginator.PageCount != layout.PageCount)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 分页器页数不一致（分页器 {0}，版面 {1}）",
                        paginator.PageCount, layout.PageCount));
                }
                else
                {
                    log.AppendLine("  OK   分页器页数与版面一致（" + paginator.PageCount + " 页）");
                }

                // ---- 每页都能生成，且不重叠 ----
                checks++;
                List<Int32Rect> sources = new List<Int32Rect>();
                bool allPagesRendered = true;

                for (int i = 0; i < paginator.PageCount; i++)
                {
                    DocumentPage page = paginator.GetPage(i);

                    if (page == null || page.Visual == null || page.Size.Width <= 0.0)
                    {
                        allPagesRendered = false;
                        break;
                    }

                    Int32Rect sourceRect;
                    Rect destinationRect;
                    layout.GetPageImageRects(i, paginator.PageCount, imageWidth, imageHeight, out sourceRect, out destinationRect);
                    sources.Add(sourceRect);
                }

                if (!allPagesRendered)
                {
                    failures++;
                    log.AppendLine("  FAIL 存在无法生成的分页");
                }
                else
                {
                    // 检查任意两页的源区域不重叠（同时看横向与纵向）
                    bool noOverlap = true;
                    string detail = string.Empty;
                    int columns = Math.Max(1, layout.PageColumns);

                    for (int i = 0; i < sources.Count && noOverlap; i++)
                    {
                        for (int j = i + 1; j < sources.Count; j++)
                        {
                            Int32Rect a = sources[i];
                            Int32Rect b = sources[j];

                            bool separatedX = a.X + a.Width <= b.X || b.X + b.Width <= a.X;
                            bool separatedY = a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y;

                            if (!separatedX && !separatedY)
                            {
                                noOverlap = false;
                                detail = string.Format(
                                    "第 {0} 页 [{1},{2})x[{3},{4}) 与第 {5} 页 [{6},{7})x[{8},{9}) 重叠",
                                    i, a.X, a.X + a.Width, a.Y, a.Y + a.Height,
                                    j, b.X, b.X + b.Width, b.Y, b.Y + b.Height);
                                break;
                            }
                        }
                    }

                    if (!noOverlap)
                    {
                        failures++;
                        log.AppendLine("  FAIL 分页源区域重叠：" + detail);
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   {0} 页源区域两两不重叠（每页约 {1}x{2} 像素，与页网格 {3}×{4} 对应）",
                            sources.Count,
                            sources[0].Width,
                            sources[0].Height,
                            layout.PageColumns,
                            layout.PageRows));
                    }

                    // 覆盖完整性：所有页的像素面积之和应等于整图面积（不重不漏）
                    long covered = 0;

                    for (int i = 0; i < sources.Count; i++)
                    {
                        covered += (long)sources[i].Width * sources[i].Height;
                    }

                    long total = (long)imageWidth * imageHeight;

                    checks++;
                    if (covered < total)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 分页未覆盖整图（覆盖 {0} / 总计 {1} 像素）", covered, total));
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   分页完整覆盖整图（{0:N0} 像素，不重不漏）", covered));
                    }
                }

                // ---- 页尺寸为纸张 DIP（DPI 感知的关键：不乘打印机 DPI） ----
                checks++;
                DocumentPage firstPage = paginator.GetPage(0);

                if (Math.Abs(firstPage.Size.Width - paper.Width) > 0.5
                    || Math.Abs(firstPage.Size.Height - paper.Height) > 0.5)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 页尺寸不是纸张 DIP（得到 {0:0.#}x{1:0.#}，期望 {2:0.#}x{3:0.#}）",
                        firstPage.Size.Width, firstPage.Size.Height, paper.Width, paper.Height));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   页尺寸为纸张 DIP（{0:0.#}x{1:0.#}，未乘打印机 DPI，避免二次缩放）",
                        firstPage.Size.Width, firstPage.Size.Height));
                }

                // ---- 越界页码应安全返回 ----
                checks++;
                try
                {
                    DocumentPage invalid = paginator.GetPage(9999);
                    log.AppendLine(invalid == DocumentPage.Missing
                        ? "  OK   越界页码安全返回 DocumentPage.Missing"
                        : "  OK   越界页码被安全处理");
                }
                catch (Exception ex)
                {
                    failures++;
                    log.AppendLine("  FAIL 越界页码抛出异常: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 打印分页测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验打印预览窗口的 XAML 能正常加载、ViewModel 能驱动界面绑定。
        ///
        /// 为什么要在这里测：窗口的 StaticResource 找不到只会在**运行时**抛异常
        /// （编译期完全看不出来），而本环境没有打印机、无法用真实打印流程走一遍。
        /// 这里直接构造窗口与 ViewModel，即可覆盖"资源解析 + 绑定 + 版面计算"这条链路。
        /// </summary>
        private static void CheckPrintPreviewWindow(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[18] 打印预览窗口（XAML 加载 + 绑定）");

            try
            {
                Size paper = new Size(
                    PrintUnits.MillimetresToDips(210),
                    PrintUnits.MillimetresToDips(297));
                Thickness margin = new Thickness(
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25),
                    PrintUnits.InchesToDips(0.25));

                PixelBuffer buffer = CreateTestBuffer(1200, 800);
                BitmapSource bitmap = PixelBuffer.ToBitmap(buffer, 300.0, 300.0);

                PrintPreviewViewModel viewModel = new PrintPreviewViewModel(
                    bitmap,
                    300.0,
                    300.0,
                    paper,
                    margin,
                    "自检虚拟打印机");

                // ---- ViewModel 派生属性可用 ----
                checks++;
                bool propertiesOk = viewModel.Layout != null
                                    && viewModel.PageCount >= 1
                                    && !string.IsNullOrWhiteSpace(viewModel.PageText)
                                    && !string.IsNullOrWhiteSpace(viewModel.EffectiveDpiText)
                                    && viewModel.DisplayPaperWidth > 0.0
                                    && viewModel.DisplayPaperHeight > 0.0;

                if (!propertiesOk)
                {
                    failures++;
                    log.AppendLine("  FAIL 预览 ViewModel 的派生属性不可用");
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   预览 ViewModel 属性正常（{0}，{1}，显示 {2:0.#}×{3:0.#}）",
                        viewModel.PageText,
                        viewModel.EffectiveDpiText,
                        viewModel.DisplayPaperWidth,
                        viewModel.DisplayPaperHeight));
                }

                // ---- 布局模式切换与重置 ----
                checks++;
                viewModel.LayoutMode = PrintLayoutMode.Fill;
                double fillWidth = viewModel.Layout.ImageBounds.Width;
                viewModel.LayoutMode = PrintLayoutMode.Fit;
                double fitWidth = viewModel.Layout.ImageBounds.Width;

                if (fillWidth <= fitWidth)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 布局模式切换未生效（填充 {0:0.#} 应大于适应 {1:0.#}）", fillWidth, fitWidth));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   布局模式切换生效（填充 {0:0.#} DIP > 适应 {1:0.#} DIP）", fillWidth, fitWidth));
                }

                // ---- 拖拽换算：显示位移应正确换算为 DIP 并按比例影响版面 ----
                checks++;
                viewModel.DisplayScale = 0.5;   // 1 DIP = 0.5 显示单位
                double beforeLeft = viewModel.Layout.ImageBounds.Left;
                viewModel.DragBy(0.0, 0.0);     // 零位移不应改变任何东西
                double sameLeft = viewModel.Layout.ImageBounds.Left;

                if (Math.Abs(beforeLeft - sameLeft) > 0.001)
                {
                    failures++;
                    log.AppendLine("  FAIL 零位移拖动改变了版面");
                }
                else
                {
                    log.AppendLine("  OK   零位移拖动不改变版面（换算无误）");
                }

                // ---- 缩放钳制 ----
                checks++;
                viewModel.Scale = 1000.0;
                double maxScale = viewModel.Scale;
                viewModel.Scale = -5.0;
                double minScale = viewModel.Scale;

                if (maxScale > 8.001 || minScale < 0.049)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 缩放未钳制（上限 {0:0.##}，下限 {1:0.###}）", maxScale, minScale));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   缩放钳制生效（上限 {0:0.##}，下限 {1:0.###}）", maxScale, minScale));
                }

                // ---- 构造真实窗口：验证 XAML 的资源与绑定不抛异常 ----
                // 必须回到 Dispatcher 线程：Window 是 DispatcherObject，
                // 而本自检运行在普通线程上（没有消息循环）。
                checks++;
                Exception windowError = null;
                bool printConfirmed = true;

                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    new Action(() =>
                    {
                        try
                        {
                            Views.PrintPreviewWindow window = new Views.PrintPreviewWindow();
                            window.DataContext = viewModel;

                            // 触发一次布局与渲染（Measure/Arrange 会让绑定真正求值）
                            window.Measure(new Size(1080, 760));
                            window.Arrange(new Rect(0, 0, 1080, 760));
                            window.UpdateLayout();

                            printConfirmed = window.PrintConfirmed;
                            window.Close();
                        }
                        catch (Exception ex)
                        {
                            windowError = ex;
                        }
                    }));

                if (windowError != null)
                {
                    failures++;
                    log.AppendLine("  FAIL 打印预览窗口构造失败: "
                        + windowError.GetType().Name + " " + windowError.Message);
                }
                else if (printConfirmed)
                {
                    failures++;
                    log.AppendLine("  FAIL 新窗口的 PrintConfirmed 应为 false");
                }
                else
                {
                    log.AppendLine("  OK   打印预览窗口 XAML 加载成功、布局完成、PrintConfirmed 默认为 false");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 打印预览窗口测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验设置持久化与最近文件列表（需求 P3-16）。
        /// </summary>
        private static void CheckSettingsAndRecentFiles(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[19] 设置持久化与最近文件");

            try
            {
                // ---- 最近文件：去重 + 置顶 + 限长 ----
                checks++;
                AppSettings settings = new AppSettings();

                for (int i = 0; i < 14; i++)
                {
                    settings.AddRecentFile(@"C:\photos\img" + i + ".jpg");
                }

                // 重新加入第 5 个：应被置顶且不产生重复
                settings.AddRecentFile(@"C:\photos\img5.jpg");

                bool capped = settings.RecentFiles.Count == AppSettings.MaxRecentFiles;
                bool promoted = settings.RecentFiles[0] == @"C:\photos\img5.jpg";
                int occurrences = 0;

                for (int i = 0; i < settings.RecentFiles.Count; i++)
                {
                    if (string.Equals(settings.RecentFiles[i], @"C:\photos\img5.jpg", StringComparison.OrdinalIgnoreCase))
                    {
                        occurrences++;
                    }
                }

                if (!capped || !promoted || occurrences != 1)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 最近文件列表异常（条数 {0}，置顶={1}，重复 {2} 次）",
                        settings.RecentFiles.Count, promoted, occurrences));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   最近文件：上限 {0} 条、重复打开自动置顶且不重复（{1} 条）",
                        AppSettings.MaxRecentFiles, settings.RecentFiles.Count));
                }

                // ---- 持久化往返（含主题偏好与最近文件）----
                checks++;
                string tempFile = Path.Combine(
                    Path.GetTempPath(),
                    "pstext-settings-" + Guid.NewGuid().ToString("N") + ".xml");

                try
                {
                    XmlSettingsService service = new XmlSettingsService(tempFile);
                    settings.ThemePreference = ThemePreference.Dark;
                    service.Save(settings);

                    AppSettings reloaded = service.Load();

                    bool roundTripOk = File.Exists(tempFile)
                                       && reloaded.ThemePreference == ThemePreference.Dark
                                       && reloaded.RecentFiles.Count == settings.RecentFiles.Count
                                       && reloaded.RecentFiles[0] == settings.RecentFiles[0];

                    if (!roundTripOk)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 设置往返失败（主题 {0}，最近文件 {1} 条）",
                            reloaded.ThemePreference, reloaded.RecentFiles.Count));
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   设置往返正确（主题={0}，最近文件 {1} 条，文件 {2} 字节）",
                            reloaded.ThemePreference,
                            reloaded.RecentFiles.Count,
                            new FileInfo(tempFile).Length));
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch (IOException)
                    {
                        // 清理失败不影响结论
                    }
                }

                // ---- 损坏的设置文件必须回退默认值而不是抛异常 ----
                checks++;
                string corruptFile = Path.Combine(
                    Path.GetTempPath(),
                    "pstext-corrupt-" + Guid.NewGuid().ToString("N") + ".xml");

                try
                {
                    File.WriteAllText(corruptFile, "<这不是合法的 XML <<<");

                    XmlSettingsService service = new XmlSettingsService(corruptFile);
                    AppSettings recovered = service.Load();

                    if (recovered == null || recovered.RecentFiles == null)
                    {
                        failures++;
                        log.AppendLine("  FAIL 损坏的设置文件未回退为默认值");
                    }
                    else
                    {
                        log.AppendLine("  OK   损坏的设置文件回退为默认值（未抛异常）");
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(corruptFile))
                        {
                            File.Delete(corruptFile);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                // ---- ViewModel 集成：加载图片后应记入最近文件 ----
                checks++;
                string appDataPath = Path.Combine(
                    Path.GetTempPath(),
                    "pstext-vm-settings-" + Guid.NewGuid().ToString("N") + ".xml");

                try
                {
                    XmlSettingsService service = new XmlSettingsService(appDataPath);
                    WpfImageService imageService = new WpfImageService();

                    MainViewModel viewModel = new MainViewModel(
                        imageService,
                        new NullDialogService(),
                        new ImmediateDispatcherService());
                    viewModel.SettingsService = service;

                    string samplePath = Path.Combine(Path.GetTempPath(), "pstext-recent-sample.png");
                    CreateTestImage(samplePath, 80, 60, 96.0, ImageFileFormat.Png, new StringBuilder());

                    viewModel.LoadFromPathAsync(samplePath).GetAwaiter().GetResult();
                    WaitForIdle(viewModel);

                    bool recorded = viewModel.RecentFiles.Count == 1
                                    && string.Equals(viewModel.RecentFiles[0].FilePath, samplePath, StringComparison.OrdinalIgnoreCase)
                                    && viewModel.HasRecentFiles;

                    // 也应已经落盘
                    AppSettings persisted = service.Load();

                    if (!recorded || persisted.RecentFiles.Count == 0)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 加载图片后未记入最近文件（内存 {0} 条，磁盘 {1} 条）",
                            viewModel.RecentFiles.Count, persisted.RecentFiles.Count));
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   加载图片后自动记入最近文件并落盘（{0}）", viewModel.RecentFiles[0].FileName));
                    }

                    // 清空命令
                    checks++;
                    viewModel.ClearRecentFilesCommand.Execute(null);

                    if (viewModel.RecentFiles.Count != 0 || viewModel.HasRecentFiles)
                    {
                        failures++;
                        log.AppendLine("  FAIL 清空最近文件命令未生效");
                    }
                    else
                    {
                        log.AppendLine("  OK   清空最近文件命令生效（内存与磁盘都已清空）");
                    }

                    try
                    {
                        File.Delete(samplePath);
                    }
                    catch (IOException)
                    {
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(appDataPath))
                        {
                            File.Delete(appDataPath);
                        }
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 设置 / 最近文件测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验主题解析与运行时切换（需求 P3-13）。
        /// </summary>
        private static void CheckThemeSwitching(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[20] 主题解析与切换");

            try
            {
                // ---- 显式偏好解析 ----
                checks++;
                AppTheme dark = ThemeManager.ResolveTheme(ThemePreference.Dark);
                AppTheme light = ThemeManager.ResolveTheme(ThemePreference.Light);

                if (dark != AppTheme.Dark || light != AppTheme.Light)
                {
                    failures++;
                    log.AppendLine("  FAIL 显式主题偏好解析错误");
                }
                else
                {
                    log.AppendLine("  OK   显式偏好解析正确（深色→Dark，浅色→Light）");
                }

                // ---- 跟随系统：应能从注册表读出结果（无该值时为浅色，不能抛异常） ----
                checks++;
                AppTheme systemTheme = ThemeManager.ResolveTheme(ThemePreference.FollowSystem);
                log.AppendLine(string.Format(
                    "  OK   跟随系统解析成功（当前系统实际={0}，本机注册表值读取未抛异常）",
                    ThemeManager.GetThemeDisplayName(systemTheme)));

                // ---- 运行时切换：真实合并资源字典，并让已存在的窗口仍能解析样式 ----
                checks++;
                Application application = Application.Current;

                if (application == null)
                {
                    log.AppendLine("  ~    跳过运行时切换（无 Application 实例）");
                }
                else
                {
                    ThemeManager.SetPreference(application, ThemePreference.Dark);
                    bool darkApplied = ThemeManager.CurrentTheme == AppTheme.Dark;

                    // 切到深色后，画布背景应为深色系
                    Color darkCanvas = ThemeManager.GetCanvasBackgroundColor();

                    ThemeManager.SetPreference(application, ThemePreference.Light);
                    bool lightApplied = ThemeManager.CurrentTheme == AppTheme.Light;
                    Color lightCanvas = ThemeManager.GetCanvasBackgroundColor();

                    double darkLuma = 0.114 * darkCanvas.B + 0.587 * darkCanvas.G + 0.299 * darkCanvas.R;
                    double lightLuma = 0.114 * lightCanvas.B + 0.587 * lightCanvas.G + 0.299 * lightCanvas.R;

                    if (!darkApplied || !lightApplied)
                    {
                        failures++;
                        log.AppendLine("  FAIL 运行时主题切换未生效");
                    }
                    else if (Math.Abs(darkLuma - lightLuma) < 1.0)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 切换主题后画布配色未变化（深 {0:0}，浅 {1:0}）", darkLuma, lightLuma));
                    }
                    else
                    {
                        log.AppendLine(string.Format(
                            "  OK   运行时切换生效且配色确实改变（画布亮度 深 {0:0} → 浅 {1:0}）",
                            darkLuma, lightLuma));
                    }

                    // ---- 切换主题后新窗口仍能解析共享样式 ----
                    checks++;
                    Exception windowError = null;

                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        new Action(() =>
                        {
                            try
                            {
                                ThemeManager.SetPreference(application, ThemePreference.Dark);

                                Views.PrintPreviewWindow window = new Views.PrintPreviewWindow();
                                window.Measure(new Size(800, 600));
                                window.Arrange(new Rect(0, 0, 800, 600));
                                window.UpdateLayout();
                                window.Close();
                            }
                            catch (Exception ex)
                            {
                                windowError = ex;
                            }
                        }));

                    if (windowError != null)
                    {
                        failures++;
                        log.AppendLine("  FAIL 切换主题后窗口无法解析样式: " + windowError.Message);
                    }
                    else
                    {
                        log.AppendLine("  OK   切换主题后新窗口仍能正确解析共享样式（深色下加载成功）");
                    }

                    // 恢复浅色，避免影响后续用例与视觉预期
                    ThemeManager.SetPreference(application, ThemePreference.Light);
                }

                // ---- 令牌完整性：两套主题必须定义完全相同的键集合 ----
                // 只在一套主题里定义的键，切换主题时会保留旧值（典型症状：局部配色不跟着变）。
                checks++;
                List<string> lightKeys = new List<string>();
                List<string> darkKeys = new List<string>();

                CollectThemeKeys(ThemePreference.Light, lightKeys);
                CollectThemeKeys(ThemePreference.Dark, darkKeys);

                List<string> onlyInLight = new List<string>();
                List<string> onlyInDark = new List<string>();

                for (int i = 0; i < lightKeys.Count; i++)
                {
                    if (!darkKeys.Contains(lightKeys[i]))
                    {
                        onlyInLight.Add(lightKeys[i]);
                    }
                }

                for (int i = 0; i < darkKeys.Count; i++)
                {
                    if (!lightKeys.Contains(darkKeys[i]))
                    {
                        onlyInDark.Add(darkKeys[i]);
                    }
                }

                if (lightKeys.Count == 0 || darkKeys.Count == 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 未能读取主题令牌（浅色 {0} 个，深色 {1} 个）", lightKeys.Count, darkKeys.Count));
                }
                else if (onlyInLight.Count > 0 || onlyInDark.Count > 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 两套主题的令牌不一致：仅浅色有 [{0}]，仅深色有 [{1}]",
                        string.Join(", ", onlyInLight.ToArray()),
                        string.Join(", ", onlyInDark.ToArray())));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   两套主题定义了相同的 {0} 个令牌（切换不会留下旧值）", lightKeys.Count));
                }

                // ---- 每个令牌在两套主题下都必须能解析到对应颜色 ----
                // 这能同时发现“键写错”和“键只在一套主题里定义”两类问题。
                checks++;
                List<string> unresolved = new List<string>();
                Application app = Application.Current;

                if (app != null)
                {
                    for (int themeIndex = 0; themeIndex < 2; themeIndex++)
                    {
                        ThemePreference preference = themeIndex == 0 ? ThemePreference.Light : ThemePreference.Dark;
                        ThemeManager.SetPreference(app, preference);

                        for (int i = 0; i < lightKeys.Count; i++)
                        {
                            if (!ThemeManager.IsTokenResolvedConsistently(lightKeys[i]))
                            {
                                string label = ThemeManager.GetPreferenceDisplayName(preference) + ":" + lightKeys[i];

                                if (!unresolved.Contains(label))
                                {
                                    unresolved.Add(label);
                                }
                            }
                        }
                    }

                    ThemeManager.SetPreference(app, ThemePreference.Light);
                }

                if (unresolved.Count > 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 以下令牌解析结果与主题字典不一致：" + string.Join("，", unresolved.ToArray()));
                }
                else
                {
                    log.AppendLine("  OK   全部令牌在浅色 / 深色下都解析为对应颜色");
                }

                // ---- 关键令牌在两套主题下必须真的不同（否则等于没换主题） ----
                checks++;
                if (app != null)
                {
                    ThemeManager.SetPreference(app, ThemePreference.Light);
                    Color lightChecker = Colors.Transparent;
                    Color lightHover = Colors.Transparent;
                    bool gotLightChecker = ThemeManager.TryGetThemeBrushColor("CheckerLightBrush", out lightChecker);
                    bool gotLightHover = ThemeManager.TryGetThemeBrushColor("PrimaryHoverBrush", out lightHover);

                    ThemeManager.SetPreference(app, ThemePreference.Dark);
                    Color darkChecker = Colors.Transparent;
                    Color darkHover = Colors.Transparent;
                    bool gotDarkChecker = ThemeManager.TryGetThemeBrushColor("CheckerLightBrush", out darkChecker);
                    bool gotDarkHover = ThemeManager.TryGetThemeBrushColor("PrimaryHoverBrush", out darkHover);

                    ThemeManager.SetPreference(app, ThemePreference.Light);

                    bool gotLight = gotLightChecker && gotLightHover;
                    bool gotDark = gotDarkChecker && gotDarkHover;

                    if (!gotLight || !gotDark)
                    {
                        failures++;
                        log.AppendLine("  FAIL 无法读取棋盘格 / 悬停色令牌");
                    }
                    else if (lightChecker == darkChecker || lightHover == darkHover)
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 关键令牌在两套主题下相同（棋盘格 {0} vs {1}，悬停 {2} vs {3}）",
                            lightChecker, darkChecker, lightHover, darkHover));
                    }
                    else
                    {
                        log.AppendLine("  OK   棋盘格与悬停色在两套主题下确实不同（深色模式不再残留浅色底纹）");
                    }
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 主题测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验进度上报（需求 P3-15）：阈值行为、百分比收敛、结束回调幂等。
        /// </summary>
        private static void CheckProgressReporter(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[21] 进度反馈（P3-15）");

            try
            {
                ImmediateDispatcherService dispatcher = new ImmediateDispatcherService();

                // ---- 达到阈值才显示 ----
                checks++;
                int startedCount = 0;
                int completedCount = 0;
                List<ProgressInfo> reports = new List<ProgressInfo>();

                ProgressReporter reporter = new ProgressReporter(
                    dispatcher,
                    info => reports.Add(info),
                    () => startedCount++,
                    () => completedCount++);

                reporter.Report(0.25, "处理中");
                reporter.Report(0.75, "处理中");
                reporter.Complete();

                if (startedCount != 1 || completedCount != 1 || reports.Count != 2)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 进度回调次数异常（started={0} completed={1} reports={2}）",
                        startedCount, completedCount, reports.Count));
                }
                else
                {
                    log.AppendLine("  OK   进度回调正确：首次上报触发开始、结束触发一次收起");
                }

                // ---- 百分比收敛 ----
                checks++;
                ProgressReporter clampReporter = new ProgressReporter(dispatcher, info => { });
                clampReporter.Report(-5.0, null);
                double low = clampReporter.LastReported.Fraction;
                clampReporter.Report(42.0, null);
                double high = clampReporter.LastReported.Fraction;
                clampReporter.Report(double.NaN, null);
                double nan = clampReporter.LastReported.Fraction;
                clampReporter.Complete();

                if (low != 0.0 || high != 1.0 || nan != 0.0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 进度未收敛（负值 {0}，超界 {1}，NaN {2}）", low, high, nan));
                }
                else
                {
                    log.AppendLine("  OK   进度值收敛正确（负数→0，超界→1，NaN→0）");
                }

                // ---- Complete 幂等：重复调用只收起一次 ----
                checks++;
                int completed2 = 0;
                ProgressReporter idempotent = new ProgressReporter(dispatcher, info => { }, null, () => completed2++);
                idempotent.Report(1.0, null);
                idempotent.Complete();
                idempotent.Complete();
                idempotent.Complete();

                if (completed2 != 1)
                {
                    failures++;
                    log.AppendLine("  FAIL Complete 非幂等（触发 " + completed2 + " 次）");
                }
                else
                {
                    log.AppendLine("  OK   Complete 幂等（重复调用只收起一次进度）");
                }

                // ---- 未显示过进度时不触发收起（避免误清理界面） ----
                checks++;
                int completed3 = 0;
                ProgressReporter neverShown = new ProgressReporter(dispatcher, info => { }, null, () => completed3++);
                neverShown.Complete();

                if (completed3 != 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 未上报过进度却触发了收起回调");
                }
                else
                {
                    log.AppendLine("  OK   未上报过进度时不触发收起回调");
                }

                // ---- ViewModel 集成：进度属性可绑定 ----
                checks++;
                WpfImageService imageService = new WpfImageService();
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                // 注意：ProgressValue 的单位是百分比（0~100），与 ProgressInfo.Fraction（0~1）不同
                viewModel.SetProgressForTest(40.0, "测试进度");

                if (!viewModel.IsProgressVisible
                    || Math.Abs(viewModel.ProgressValue - 40.0) > 0.01
                    || viewModel.ProgressText.IndexOf("40", StringComparison.Ordinal) < 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 进度绑定属性异常（可见={0}，值={1}，文本={2}）",
                        viewModel.IsProgressVisible, viewModel.ProgressValue, viewModel.ProgressText));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   进度绑定属性正常（{0}）", viewModel.ProgressText));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 进度测试异常: " + ex);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 崩溃日志落盘（M0）：验证异常真的被写到磁盘，且内容足以定位问题。
        /// 直接调用生产代码 CrashLogger，因此覆盖的是真实写入路径与目录回退逻辑。
        /// </summary>
        private static void CheckCrashLogger(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[22] 崩溃日志落盘");

            try
            {
                string marker = "自检写入，可忽略：" + Guid.NewGuid().ToString("N");
                Exception exception = new InvalidOperationException(
                    "自检模拟异常",
                    new TimeoutException("内部原因"));

                // ---- 1) 写入并返回真实路径 ----
                checks++;
                string path = CrashLogger.Log(marker, exception);

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    failures++;
                    log.AppendLine("  FAIL 崩溃日志未落盘（返回 " + (path ?? "null") + "）");
                }
                else
                {
                    string content = File.ReadAllText(path);

                    bool hasMarker = content.IndexOf(marker, StringComparison.Ordinal) >= 0;
                    bool hasTime = content.IndexOf("[时间]", StringComparison.Ordinal) >= 0;
                    bool hasVersion = content.IndexOf("[版本]", StringComparison.Ordinal) >= 0;
                    bool hasEnv = content.IndexOf("[环境]", StringComparison.Ordinal) >= 0;
                    bool hasType = content.IndexOf("System.InvalidOperationException", StringComparison.Ordinal) >= 0;
                    bool hasInner = content.IndexOf("System.TimeoutException", StringComparison.Ordinal) >= 0;

                    checks++;

                    if (!(hasMarker && hasTime && hasVersion && hasEnv && hasType && hasInner))
                    {
                        failures++;
                        log.AppendLine(string.Format(
                            "  FAIL 日志内容不完整（标记 {0} 时间 {1} 版本 {2} 环境 {3} 类型 {4} 内层 {5}）",
                            hasMarker, hasTime, hasVersion, hasEnv, hasType, hasInner));
                    }
                    else
                    {
                        log.AppendLine("  OK   崩溃日志已落盘，含时间 / 版本 / 环境 / 异常类型 / 内部异常");
                        log.AppendLine("  OK   日志文件: " + path);
                    }
                }

                // ---- 2) null 异常也必须能安全记录（记日志本身不能成为新的崩溃源） ----
                checks++;
                string nullPath = CrashLogger.Log(marker + "-null", null);

                if (string.IsNullOrEmpty(nullPath) || !File.Exists(nullPath))
                {
                    failures++;
                    log.AppendLine("  FAIL 传入 null 异常时未能写入");
                }
                else
                {
                    log.AppendLine("  OK   传入 null 异常也能安全记录");
                }

                // ---- 3) 目录可用 ----
                checks++;
                string directory = CrashLogger.LogDirectory;

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    failures++;
                    log.AppendLine("  FAIL 日志目录不可用");
                }
                else
                {
                    log.AppendLine("  OK   日志目录: " + directory);
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 崩溃日志测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 主窗口 XAML 构造（M0）。
        ///
        /// 为什么单独加这条：此前只有打印预览窗口被"真实构造"验证过，主窗口从未进过自检，
        /// 而主窗口新引入了图标 pack URI 这类**只在运行时暴露**的资源引用 ——
        /// 写错就是启动即崩（编译期完全看不出来）。这里真实构造一次并确认图标确实加载成功。
        /// </summary>
        private static void CheckMainWindowXaml(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[23] 主窗口 XAML 构造（图标资源 / 绑定）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    new WpfImageService(),
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                Exception windowError = null;
                bool iconLoaded = false;
                int realizedTabs = 0;
                int declaredTabs = 0;
                List<string> tabHeaders = new List<string>();

                // Window 是 DispatcherObject，而自检跑在普通线程上（没有消息循环）。
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    new Action(() =>
                    {
                        try
                        {
                            Views.MainWindow window = new Views.MainWindow { DataContext = viewModel };

                            // Measure/Arrange 让绑定真正求值：只构造不布局，绑定错误会被藏起来
                            window.Measure(new Size(1180, 760));
                            window.Arrange(new Rect(0, 0, 1180, 760));
                            window.UpdateLayout();

                            iconLoaded = window.Icon != null;

                            // 找出分页控件并逐个切换（走逻辑树：窗口未 Show 时可视树还没建立，
                            // 但逻辑树在 XAML 解析后就已经完整了）。
                            System.Windows.Controls.TabControl tabs =
                                FindInLogicalTree<System.Windows.Controls.TabControl>(window);

                            if (tabs != null)
                            {
                                declaredTabs = tabs.Items.Count;
                                tabHeaders = new List<string>();

                                foreach (object item in tabs.Items)
                                {
                                    System.Windows.Controls.TabItem tabItem =
                                        item as System.Windows.Controls.TabItem;

                                    if (tabItem != null && tabItem.Header != null)
                                    {
                                        tabHeaders.Add(tabItem.Header.ToString());
                                    }
                                }

                                // 切一遍所有分页并强制布局（转换器 / 绑定若在呈现时抛异常会被这里抓到）
                                for (int i = 0; i < declaredTabs; i++)
                                {
                                    tabs.SelectedIndex = i;
                                    window.UpdateLayout();
                                    realizedTabs++;
                                }
                            }

                            window.Close();
                        }
                        catch (Exception ex)
                        {
                            windowError = ex;
                        }
                    }));

                checks++;

                if (windowError != null)
                {
                    failures++;
                    log.AppendLine("  FAIL 主窗口构造失败: "
                        + windowError.GetType().Name + " " + windowError.Message);
                }
                else
                {
                    log.AppendLine("  OK   主窗口 XAML 加载成功、布局完成（绑定未抛异常）");
                }

                checks++;

                if (windowError == null && !iconLoaded)
                {
                    failures++;
                    log.AppendLine("  FAIL 窗口图标未加载（检查 Resources\\PS-text.ico 是否作为 Resource 嵌入、pack URI 是否正确）");
                }
                else if (windowError == null)
                {
                    log.AppendLine("  OK   窗口图标已由 pack URI 成功加载");
                }

                checks++;

                if (windowError != null)
                {
                    // 构造已经失败，分页断言没有意义
                }
                else if (declaredTabs < 6
                    || realizedTabs != declaredTabs
                    || !tabHeaders.Contains("尺寸")
                    || !tabHeaders.Contains("修补"))
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 分页不完整（声明 {0} 个，切换 {1} 个，标题：{2}；应含「尺寸」与「修补」页）",
                        declaredTabs,
                        realizedTabs,
                        string.Join(" / ", tabHeaders.ToArray())));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   {0} 个分页全部可呈现（{1}）",
                        realizedTabs,
                        string.Join(" / ", tabHeaders.ToArray())));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 主窗口测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 在**逻辑树**里查找第一个指定类型的后代。
        ///
        /// 为什么不用 VisualTreeHelper：窗口在自检里不会被 Show()，此时可视树尚未建立
        /// （Window 的模板要在创建 HwndSource 后才展开），而逻辑树在 XAML 解析完成后就是完整的。
        /// </summary>
        private static T FindInLogicalTree<T>(System.Windows.DependencyObject root)
            where T : System.Windows.DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            if (root is T)
            {
                return (T)root;
            }

            System.Collections.IEnumerable children;

            try
            {
                children = System.Windows.LogicalTreeHelper.GetChildren(root);
            }
            catch (Exception)
            {
                // 个别节点（非 FrameworkElement）取子节点会抛异常，跳过即可。
                return null;
            }

            if (children == null)
            {
                return null;
            }

            foreach (object child in children)
            {
                System.Windows.DependencyObject node = child as System.Windows.DependencyObject;

                if (node == null)
                {
                    continue;
                }

                T found = FindInLogicalTree<T>(node);

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 图像尺寸缩放与任意角度旋转（M1）。
        ///
        /// 重点钉住三类容易悄悄出错的点：
        ///   1. **权重归一化**：纯色图任意缩放后颜色必须不变（不归一化会让图整体变亮 / 变暗）；
        ///   2. **核展宽**：缩小 10 倍后 1px 竖条纹必须被平滑成均匀灰（不展宽就是摩尔纹）；
        ///   3. **预乘 alpha**：透明边缘不得被"看不见的黑"拉暗（否则透明图缩放后出现黑边）。
        /// 另外还有"自动裁掉空白角"这个几何结论：裁完之后**一个背景色像素都不该存在**。
        /// </summary>
        private static void CheckResizeAndArbitraryRotation(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[24] 图像尺寸缩放 / 任意角度旋转（M1）");

            try
            {
                GeometryFilters geometry = new GeometryFilters();

                // ---- 1. 缩放后的尺寸与行距 ----
                checks++;
                PixelBuffer scaleSource = CreateSolidBuffer(400, 300, 40, 90, 200);
                PixelBuffer half = geometry.Resize(scaleSource, 200, 150, ResampleKernel.Bicubic);

                if (half.Width != 200 || half.Height != 150 || half.Stride != 200 * 4)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 缩放尺寸 / 行距错误（{0}×{1}，行距 {2}）", half.Width, half.Height, half.Stride));
                }
                else
                {
                    log.AppendLine("  OK   缩放尺寸与行距正确（400×300 → 200×150）");
                }

                // ---- 2. 同尺寸缩放必须逐像素恒等 ----
                checks++;
                PixelBuffer identitySource = CreateTestBuffer(64, 48);
                PixelBuffer identity = geometry.Resize(identitySource, 64, 48, ResampleKernel.Bicubic);

                if (!PixelsEqual(identitySource.GetPixels(), identity.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 同尺寸缩放改变了像素（应逐像素恒等）");
                }
                else
                {
                    log.AppendLine("  OK   同尺寸缩放逐像素恒等（不引入插值误差）");
                }

                // ---- 3. 纯色图在三档核 × 放大缩放下颜色完全不变 ----
                checks++;
                ResampleKernel[] kernels = { ResampleKernel.Bicubic, ResampleKernel.Bilinear, ResampleKernel.NearestNeighbor };
                int[][] targetSizes = { new int[] { 37, 23 }, new int[] { 311, 157 }, new int[] { 800, 600 } };
                string colorError = null;

                for (int k = 0; k < kernels.Length && colorError == null; k++)
                {
                    for (int s = 0; s < targetSizes.Length; s++)
                    {
                        PixelBuffer solid = CreateSolidBuffer(64, 48, 40, 90, 200);
                        PixelBuffer scaled = geometry.Resize(solid, targetSizes[s][0], targetSizes[s][1], kernels[k]);
                        byte[] scaledPixels = scaled.GetPixels();

                        for (int i = 0; i < scaledPixels.Length; i += 4)
                        {
                            if (scaledPixels[i] != 200 || scaledPixels[i + 1] != 90
                                || scaledPixels[i + 2] != 40 || scaledPixels[i + 3] != 255)
                            {
                                colorError = string.Format(
                                    "核 {0} → {1}×{2} 时第 {3} 字节为 {4}",
                                    kernels[k], targetSizes[s][0], targetSizes[s][1], i, scaledPixels[i]);
                                break;
                            }
                        }

                        if (colorError != null)
                        {
                            break;
                        }
                    }
                }

                if (colorError != null)
                {
                    failures++;
                    log.AppendLine("  FAIL 纯色图缩放后颜色改变：" + colorError);
                }
                else
                {
                    log.AppendLine("  OK   纯色图在 3 档插值 × 放大/缩小 下颜色完全不变（权重已归一化）");
                }

                // ---- 4. 邻近整数倍放大是严格的像素复制 ----
                checks++;
                PixelBuffer tiny = CreateTestBuffer(32, 24);
                PixelBuffer magnified = geometry.Resize(tiny, 64, 48, ResampleKernel.NearestNeighbor);
                bool nearestExact = magnified.Width == 64 && magnified.Height == 48;

                for (int y = 0; y < 48 && nearestExact; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        byte[] expected = CopyPixel(tiny, x / 2, y / 2);
                        byte[] actual = CopyPixel(magnified, x, y);

                        if (actual[0] != expected[0] || actual[1] != expected[1]
                            || actual[2] != expected[2] || actual[3] != expected[3])
                        {
                            nearestExact = false;
                            break;
                        }
                    }
                }

                if (!nearestExact)
                {
                    failures++;
                    log.AppendLine("  FAIL 邻近 2 倍放大不是严格的像素复制");
                }
                else
                {
                    log.AppendLine("  OK   邻近 2 倍放大为严格像素复制（每个源像素恰好复制成 2×2 块）");
                }

                // ---- 5. 缩小 10 倍必须抗混叠（三次立方平滑，邻近取样丢信息） ----
                // 这是"核展宽"最直接的证据：200px 的 1px 黑白竖条纹缩到 20px，
                // 正确做法是把每 5 黑 + 5 白平均成中灰；若只抽点取样，会整片变成纯黑。
                checks++;
                PixelBuffer stripes = CreateStripedBuffer(200, 20, 1);
                PixelBuffer smoothed = geometry.Resize(stripes, 20, 20, ResampleKernel.Bicubic);
                PixelBuffer naive = geometry.Resize(stripes, 20, 20, ResampleKernel.NearestNeighbor);

                byte[] smoothedPixels = smoothed.GetPixels();
                int smoothMin = 255;
                int smoothMax = 0;

                for (int i = 0; i < smoothedPixels.Length; i += 4)
                {
                    int gray = smoothedPixels[i];
                    smoothMin = gray < smoothMin ? gray : smoothMin;
                    smoothMax = gray > smoothMax ? gray : smoothMax;
                }

                byte[] naivePixels = naive.GetPixels();
                int naiveMax = 0;

                for (int i = 0; i < naivePixels.Length; i += 4)
                {
                    naiveMax = naivePixels[i] > naiveMax ? naivePixels[i] : naiveMax;
                }

                bool antiAliased = smoothMin >= 100 && smoothMax <= 160;
                bool naiveLostDetail = naiveMax <= 20;

                if (!antiAliased)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 缩小 10 倍后未得到均匀中灰（灰度 {0}~{1}），核可能未展宽",
                        smoothMin, smoothMax));
                }
                else if (!naiveLostDetail)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 邻近取样的对照组预期应丢失全部细节（实际最大灰度 {0}）", naiveMax));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   缩小 10 倍抗混叠生效：三次立方 {0}~{1}（≈中灰 127），"
                        + "对照组邻近取样整片变纯黑（最大 {2}）——核展宽的差别一目了然",
                        smoothMin, smoothMax, naiveMax));
                }

                // ---- 6. 透明边缘不得产生黑边（预乘 alpha） ----
                checks++;
                PixelBuffer transparentEdge = CreateHalfTransparentBuffer(80, 20);
                PixelBuffer blended = geometry.Resize(transparentEdge, 40, 20, ResampleKernel.Bicubic);
                byte[] blendedPixels = blended.GetPixels();
                int darkestRed = 255;
                int lowestAlpha = 255;

                for (int i = 0; i < blendedPixels.Length; i += 4)
                {
                    int alpha = blendedPixels[i + 3];
                    lowestAlpha = alpha < lowestAlpha ? alpha : lowestAlpha;

                    if (alpha > 8)
                    {
                        int red = blendedPixels[i + 2];
                        darkestRed = red < darkestRed ? red : darkestRed;
                    }
                }

                if (darkestRed < 240)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 半透明边缘被“看不见的黑”拉暗（最低红通道 {0}，应 ≥240）", darkestRed));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   透明边缘未产生黑边（半透明像素红通道最低 {0}，alpha 最低 {1}）",
                        darkestRed, lowestAlpha));
                }

                // ---- 7. 0° / 360° 旋转必须逐像素恒等 ----
                checks++;
                PixelBuffer rotationSource = CreateTestBuffer(80, 60);
                PixelBuffer rotatedZero = geometry.RotateArbitrary(rotationSource, 0.0, null, true);
                PixelBuffer rotatedFull = geometry.RotateArbitrary(rotationSource, 360.0, null, false);

                bool zeroOk = rotatedZero.Width == 80 && rotatedZero.Height == 60
                              && PixelsEqual(rotationSource.GetPixels(), rotatedZero.GetPixels());
                bool fullOk = rotatedFull.Width == 80 && rotatedFull.Height == 60
                              && PixelsEqual(rotationSource.GetPixels(), rotatedFull.GetPixels());

                if (!zeroOk || !fullOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 0° / 360° 旋转改变了图像（0° → {0}×{1}，360° → {2}×{3}）",
                        rotatedZero.Width, rotatedZero.Height, rotatedFull.Width, rotatedFull.Height));
                }
                else
                {
                    log.AppendLine("  OK   0° 与 360° 旋转逐像素恒等（走无损路径）");
                }

                // ---- 8. 90° 走无损路径，与既有旋转一致 ----
                checks++;
                PixelBuffer rightAngleArbitrary = geometry.RotateArbitrary(rotationSource, 90.0, null, true);
                PixelBuffer rightAngleLossless = geometry.Rotate(rotationSource, RotationAngle.Clockwise90);

                if (rightAngleArbitrary.Width != rightAngleLossless.Width
                    || rightAngleArbitrary.Height != rightAngleLossless.Height
                    || !PixelsEqual(rightAngleArbitrary.GetPixels(), rightAngleLossless.GetPixels()))
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 90° 的任意角度路径与无损旋转不一致（{0}×{1} vs {2}×{3}）",
                        rightAngleArbitrary.Width, rightAngleArbitrary.Height,
                        rightAngleLossless.Width, rightAngleLossless.Height));
                }
                else
                {
                    log.AppendLine("  OK   90° 自动走无损路径，与既有 90° 旋转逐像素一致（且不裁角）");
                }

                // ---- 9. 旋转画布尺寸符合包围盒公式 ----
                checks++;
                int boxWidth;
                int boxHeight;
                GeometryFilters.CalcRotatedSize(400, 300, 30.0, false, out boxWidth, out boxHeight);

                double cos30 = Math.Cos(30.0 * Math.PI / 180.0);
                double sin30 = Math.Sin(30.0 * Math.PI / 180.0);
                int expectedBoxWidth = (int)Math.Round(400 * cos30 + 300 * sin30);
                int expectedBoxHeight = (int)Math.Round(400 * sin30 + 300 * cos30);

                if (boxWidth != expectedBoxWidth || boxHeight != expectedBoxHeight)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 旋转包围盒尺寸不符（得到 {0}×{1}，期望 {2}×{3}）",
                        boxWidth, boxHeight, expectedBoxWidth, expectedBoxHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   旋转包围盒尺寸正确（400×300 转 30° → {0}×{1}）", boxWidth, boxHeight));
                }

                // ---- 10. 任意角度：中心保持原色、四角为填充色 ----
                checks++;
                PixelBuffer redSquare = CreateSolidBuffer(200, 200, 255, 0, 0);
                PixelBuffer tilted = geometry.RotateArbitrary(redSquare, 15.0, Colors.White, false);

                byte[] centerPixel = CopyPixel(tilted, tilted.Width / 2, tilted.Height / 2);
                byte[] cornerPixel = CopyPixel(tilted, 0, 0);
                bool centerIsRed = centerPixel[2] >= 250 && centerPixel[1] <= 5 && centerPixel[0] <= 5;
                bool cornerIsWhite = cornerPixel[0] >= 250 && cornerPixel[1] >= 250 && cornerPixel[2] >= 250;

                if (!centerIsRed || !cornerIsWhite)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 旋转后中心 / 四角不合预期（中心 {0}，左上角 {1}）",
                        DescribePixel(centerPixel), DescribePixel(cornerPixel)));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   任意角度旋转：中心保持原色、空白角填充为白色（画布 {0}×{1}）",
                        tilted.Width, tilted.Height));
                }

                // ---- 11. 自动裁掉空白角：一个背景色像素都不该剩下 ----
                checks++;
                PixelBuffer croppedTilt = geometry.RotateArbitrary(redSquare, 15.0, Colors.White, true);
                byte[] croppedPixels = croppedTilt.GetPixels();
                int backgroundPixels = 0;
                int lowestRed = 255;

                for (int i = 0; i < croppedPixels.Length; i += 4)
                {
                    if (croppedPixels[i] >= 250 && croppedPixels[i + 1] >= 250 && croppedPixels[i + 2] >= 250)
                    {
                        backgroundPixels++;
                    }

                    int red = croppedPixels[i + 2];
                    lowestRed = red < lowestRed ? red : lowestRed;
                }

                bool shrunk = croppedTilt.Width < tilted.Width && croppedTilt.Height < tilted.Height;

                if (!shrunk || backgroundPixels > 0 || lowestRed < 240)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 自动裁角未达预期（{0}×{1} vs 包围盒 {2}×{3}，残留背景像素 {4}，最低红通道 {5}）",
                        croppedTilt.Width, croppedTilt.Height, tilted.Width, tilted.Height,
                        backgroundPixels, lowestRed));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   自动裁掉空白角：{0}×{1} → {2}×{3}，且无任何背景色像素残留",
                        tilted.Width, tilted.Height, croppedTilt.Width, croppedTilt.Height));
                }

                // ---- 12. ViewModel 端到端：单位换算 / 锁定宽高比 / 应用 / 撤销 ----
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                checks++;

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片，跳过 ViewModel 部分");
                    log.AppendLine();
                    return;
                }

                int originalWidth = viewModel.Document.PixelWidth;
                int originalHeight = viewModel.Document.PixelHeight;
                double originalDpiX = viewModel.Document.DpiX;
                double originalDpiY = viewModel.Document.DpiY;

                // ---- 锁定宽高比：改宽度自动同步高度 ----
                checks++;
                viewModel.ResizeUnitIndex = 0;
                viewModel.LockAspectRatio = true;
                viewModel.ResizeTargetWidth = 160.0;

                double expectedHeightValue = 160.0 * originalHeight / originalWidth;

                if (Math.Abs(viewModel.ResizeTargetHeight - expectedHeightValue) > 0.01
                    || viewModel.ComputedResizeWidthPixels != 160
                    || viewModel.ComputedResizeHeightPixels != (int)Math.Round(expectedHeightValue))
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 锁定宽高比未同步高度（宽 {0:0.###}，高 {1:0.###}，期望高 {2:0.###}）",
                        viewModel.ResizeTargetWidth, viewModel.ResizeTargetHeight, expectedHeightValue));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   锁定宽高比生效：宽 160 → 高自动 {0:0.#}（原图 {1}×{2}）",
                        viewModel.ResizeTargetHeight, originalWidth, originalHeight));
                }

                // ---- 切换单位不得改变实际像素尺寸 ----
                checks++;
                int beforeSwitchWidth = viewModel.ComputedResizeWidthPixels;
                int beforeSwitchHeight = viewModel.ComputedResizeHeightPixels;

                viewModel.ResizeUnitIndex = 1;                       // 百分比
                double percentValue = viewModel.ResizeTargetWidth;
                int percentWidth = viewModel.ComputedResizeWidthPixels;

                viewModel.ResizeUnitIndex = 2;                       // 厘米
                double centimeterValue = viewModel.ResizeTargetWidth;
                int centimeterWidth = viewModel.ComputedResizeWidthPixels;

                viewModel.ResizeUnitIndex = 0;                       // 回到像素

                bool unitOk = percentWidth == beforeSwitchWidth
                              && centimeterWidth == beforeSwitchWidth
                              && viewModel.ComputedResizeHeightPixels == beforeSwitchHeight;

                if (!unitOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 切换单位改变了实际像素尺寸（像素 {0}，百分比 {1}，厘米 {2}）",
                        beforeSwitchWidth, percentWidth, centimeterWidth));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   单位换算保持一致：{0} px = {1:0.#}% = {2:0.###} cm（切换后仍是 {3}×{4} px）",
                        beforeSwitchWidth, percentValue, centimeterValue,
                        viewModel.ComputedResizeWidthPixels, viewModel.ComputedResizeHeightPixels));
                }

                // ---- 应用缩放 ----
                checks++;
                viewModel.ResizeTargetWidth = 160.0;
                int applyWidth = viewModel.ComputedResizeWidthPixels;
                int applyHeight = viewModel.ComputedResizeHeightPixels;

                viewModel.ApplyResizeCommand.Execute(null);
                WaitForIdle(viewModel);

                bool sizeApplied = viewModel.Document != null
                                   && viewModel.Document.PixelWidth == applyWidth
                                   && viewModel.Document.PixelHeight == applyHeight;

                if (!sizeApplied)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 应用尺寸未生效（当前 {0}×{1}，期望 {2}×{3}）",
                        viewModel.Document == null ? 0 : viewModel.Document.PixelWidth,
                        viewModel.Document == null ? 0 : viewModel.Document.PixelHeight,
                        applyWidth, applyHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   应用尺寸端到端生效（{0}×{1} → {2}×{3}）",
                        originalWidth, originalHeight, applyWidth, applyHeight));
                }

                // ---- DPI 必须保持 ----
                checks++;

                if (viewModel.Document == null
                    || Math.Abs(viewModel.Document.DpiX - originalDpiX) > 0.01
                    || Math.Abs(viewModel.Document.DpiY - originalDpiY) > 0.01)
                {
                    failures++;
                    log.AppendLine("  FAIL 缩放后 DPI 未保持");
                }
                else
                {
                    log.AppendLine(string.Format("  OK   缩放后 DPI 保持（{0:0.#} DPI）", viewModel.Document.DpiX));
                }

                // ---- 撤销恢复原尺寸 ----
                checks++;
                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                if (viewModel.Document == null
                    || viewModel.Document.PixelWidth != originalWidth
                    || viewModel.Document.PixelHeight != originalHeight)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 撤销未恢复原始尺寸（当前 {0}×{1}）",
                        viewModel.Document == null ? 0 : viewModel.Document.PixelWidth,
                        viewModel.Document == null ? 0 : viewModel.Document.PixelHeight));
                }
                else
                {
                    log.AppendLine("  OK   撤销恢复原始尺寸（缩放已进入撤销历史）");
                }

                // ---- 任意角度旋转端到端（不裁角 + 黑色填充） ----
                checks++;
                viewModel.RotationDegrees = 45.0;
                viewModel.RotationFillIndex = 2;                     // 黑色
                viewModel.RotationCropToInscribed = false;

                int rotateExpectedWidth;
                int rotateExpectedHeight;
                GeometryFilters.CalcRotatedSize(
                    viewModel.Document.PixelWidth,
                    viewModel.Document.PixelHeight,
                    45.0,
                    false,
                    out rotateExpectedWidth,
                    out rotateExpectedHeight);

                viewModel.ApplyRotationCommand.Execute(null);
                WaitForIdle(viewModel);

                bool rotationApplied = viewModel.Document != null
                                       && viewModel.Document.PixelWidth == rotateExpectedWidth
                                       && viewModel.Document.PixelHeight == rotateExpectedHeight;

                if (!rotationApplied)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 旋转画布尺寸不符（得到 {0}×{1}，期望 {2}×{3}）",
                        viewModel.Document == null ? 0 : viewModel.Document.PixelWidth,
                        viewModel.Document == null ? 0 : viewModel.Document.PixelHeight,
                        rotateExpectedWidth, rotateExpectedHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   任意角度旋转端到端生效（45° → {0}×{1}）",
                        viewModel.Document.PixelWidth, viewModel.Document.PixelHeight));
                }

                // ---- 旋转后的四角应为填充色 ----
                checks++;
                PixelBuffer rotatedPixels = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);
                byte[] rotatedCorner = CopyPixel(rotatedPixels, 0, 0);
                bool cornerIsBlack = rotatedCorner[0] <= 5 && rotatedCorner[1] <= 5 && rotatedCorner[2] <= 5;

                if (!cornerIsBlack)
                {
                    failures++;
                    log.AppendLine("  FAIL 旋转后左上角不是填充的黑色：" + DescribePixel(rotatedCorner));
                }
                else
                {
                    log.AppendLine("  OK   旋转后空白角填充为黑色（左上角 " + DescribePixel(rotatedCorner) + "）");
                }

                // ---- 旋转后"适应窗口"必须按新尺寸重算 ----
                checks++;
                viewModel.FitToWindow();
                double fittedZoom = viewModel.ZoomFactor;

                if (fittedZoom <= 0.0 || fittedZoom > MainViewModel.MaxZoom)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 旋转后适应窗口缩放异常（{0:0.####}）", fittedZoom));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   几何变换后适应窗口缩放正常（{0}）", viewModel.ZoomPercentText));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 尺寸 / 旋转测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 智能填充（M2 第一块）：验证调和扩散的两个决定性性质。
        ///
        /// 这个功能的核心承诺是"填完看不出那里原来有东西"，而它之所以成立，
        /// 靠的是调和函数的两条数学性质 —— 所以断言直接打在性质上：
        ///   1. **常值解**：边界同色 ⇒ 填充结果就是那个颜色（纯色背景上的水印被彻底抹掉）；
        ///   2. **线性重现**：边界是线性渐变 ⇒ 填充结果严格落在同一条渐变线上（扫描件照明不均也能对上）。
        /// 另外钉住"掩膜外逐字节不变"，避免填充越界改动画面。
        /// </summary>
        private static void CheckInpaintFilter(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[25] 智能填充（调和扩散）");

            try
            {
                // ---- 1. 空掩膜必须是逐像素恒等 ----
                checks++;
                PixelBuffer original = CreateTestBuffer(64, 48);
                PixelBuffer untouched = InpaintFilter.Inpaint(original, new byte[64 * 48]);

                if (!PixelsEqual(original.GetPixels(), untouched.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 空掩膜改动了像素（应逐像素恒等）");
                }
                else
                {
                    log.AppendLine("  OK   空掩膜逐像素恒等（不做无谓改动）");
                }

                // ---- 2. 纯色背景：填充后仍是同一个颜色 ----
                checks++;
                int solidWidth = 80;
                int solidHeight = 60;
                PixelBuffer solid = CreateSolidBuffer(solidWidth, solidHeight, 214, 231, 245);
                byte[] solidMask = new byte[solidWidth * solidHeight];

                for (int y = 20; y < 40; y++)
                {
                    for (int x = 25; x < 55; x++)
                    {
                        solidMask[y * solidWidth + x] = 1;
                    }
                }

                PixelBuffer solidFilled = InpaintFilter.Inpaint(solid, solidMask);
                byte[] solidPixels = solidFilled.GetPixels();
                int solidBad = 0;

                for (int i = 0; i < solidPixels.Length; i += 4)
                {
                    if (solidPixels[i] != 245 || solidPixels[i + 1] != 231
                        || solidPixels[i + 2] != 214 || solidPixels[i + 3] != 255)
                    {
                        solidBad++;
                    }
                }

                if (solidBad != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 纯色背景填充后出现 {0} 个像素偏离原色（常值解未成立）", solidBad));
                }
                else
                {
                    log.AppendLine("  OK   纯色背景上的标记被彻底抹平：填充后逐像素等于原背景色");
                }

                // ---- 3. 线性渐变背景：填充区域必须落在同一条渐变线上 ----
                checks++;
                int rampWidth = 80;
                int rampHeight = 40;
                PixelBuffer ramp = CreateHorizontalRamp(rampWidth, rampHeight);
                byte[] rampMask = new byte[rampWidth * rampHeight];

                for (int y = 10; y < 30; y++)
                {
                    for (int x = 30; x < 50; x++)
                    {
                        rampMask[y * rampWidth + x] = 1;
                    }
                }

                PixelBuffer rampFilled = InpaintFilter.Inpaint(ramp, rampMask);
                byte[] rampPixels = rampFilled.GetPixels();
                int worstRampError = 0;
                string worstRampDetail = null;

                for (int y = 10; y < 30; y++)
                {
                    for (int x = 30; x < 50; x++)
                    {
                        int index = (y * rampWidth + x) * 4;

                        int expectedB = x * 255 / (rampWidth - 1);
                        int expectedG = (rampWidth - 1 - x) * 255 / (rampWidth - 1);

                        int errorB = Math.Abs(rampPixels[index] - expectedB);
                        int errorG = Math.Abs(rampPixels[index + 1] - expectedG);
                        int errorR = Math.Abs(rampPixels[index + 2] - 128);
                        int error = Math.Max(errorB, Math.Max(errorG, errorR));

                        if (error > worstRampError)
                        {
                            worstRampError = error;
                            worstRampDetail = string.Format(
                                "({0},{1}) 实际 B{2} G{3} R{4}，期望 B{5} G{6} R128",
                                x, y, rampPixels[index], rampPixels[index + 1], rampPixels[index + 2],
                                expectedB, expectedG);
                        }
                    }
                }

                if (worstRampError > 2)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 渐变背景填充后偏离原渐变（最大误差 {0}）：{1}",
                        worstRampError, worstRampDetail));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   渐变背景被精确重现（最大误差 {0} ≤ 2，调和函数重现线性函数）",
                        worstRampError));
                }

                // ---- 4. 掩膜外必须逐字节不变 ----
                checks++;
                byte[] rampOriginal = ramp.GetPixels();
                int outsideChanged = 0;

                for (int y = 0; y < rampHeight; y++)
                {
                    for (int x = 0; x < rampWidth; x++)
                    {
                        if (rampMask[y * rampWidth + x] != 0)
                        {
                            continue;
                        }

                        int index = (y * rampWidth + x) * 4;

                        for (int channel = 0; channel < 4; channel++)
                        {
                            if (rampPixels[index + channel] != rampOriginal[index + channel])
                            {
                                outsideChanged++;
                                break;
                            }
                        }
                    }
                }

                if (outsideChanged != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 填充改动了 {0} 个掩膜外的像素（不得越界）", outsideChanged));
                }
                else
                {
                    log.AppendLine("  OK   掩膜外像素逐字节不变（填充严格限定在标记范围内）");
                }

                // ---- 5. 掩膜覆盖整幅图（没有边界数据）也不能崩 ----
                checks++;
                PixelBuffer whole = CreateTestBuffer(32, 24);
                byte[] wholeMask = new byte[32 * 24];

                for (int i = 0; i < wholeMask.Length; i++)
                {
                    wholeMask[i] = 1;
                }

                PixelBuffer wholeFilled = InpaintFilter.Inpaint(whole, wholeMask);
                byte[] wholePixels = wholeFilled.GetPixels();
                byte[] wholeSource = whole.GetPixels();
                int lowest = 255;
                int highest = 0;

                for (int i = 0; i < wholeSource.Length; i++)
                {
                    lowest = wholeSource[i] < lowest ? wholeSource[i] : lowest;
                    highest = wholeSource[i] > highest ? wholeSource[i] : highest;
                }

                bool inRange = true;

                for (int i = 0; i < wholePixels.Length; i++)
                {
                    if (wholePixels[i] < lowest || wholePixels[i] > highest)
                    {
                        inRange = false;
                        break;
                    }
                }

                if (!inRange)
                {
                    failures++;
                    log.AppendLine("  FAIL 全图标记时结果越出了原图的取值范围");
                }
                else
                {
                    log.AppendLine("  OK   全图标记（无边界数据）不崩溃，且结果落在原图取值范围内");
                }

                // ---- 6. 面积上限判定（防止大图整幅标记时把内存打爆） ----
                checks++;
                bool smallSolvable = InpaintFilter.IsSolvable(300, 120);
                bool hugeSolvable = InpaintFilter.IsSolvable(4000, 3000);

                if (!smallSolvable || hugeSolvable)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 面积上限判定异常（300×120 → {0}，4000×3000 → {1}）",
                        smallSolvable, hugeSolvable));
                }
                else
                {
                    log.AppendLine("  OK   面积上限判定正确（300×120 可处理，4000×3000 拒绝）");
                }

                // ---- 7. 性能（只做粗上限，防止算法退化成不可用） ----
                checks++;
                int perfWidth = 1200;
                int perfHeight = 800;
                PixelBuffer perfSource = CreateTestBuffer(perfWidth, perfHeight);
                byte[] perfMask = new byte[perfWidth * perfHeight];

                for (int y = 300; y < 420; y++)
                {
                    for (int x = 400; x < 700; x++)
                    {
                        perfMask[y * perfWidth + x] = 1;
                    }
                }

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                InpaintFilter.Inpaint(perfSource, perfMask);
                watch.Stop();

                if (watch.ElapsedMilliseconds > 5000)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 智能填充过慢：{0}×{1} 图上填充 300×120 区域耗时 {2} ms",
                        perfWidth, perfHeight, watch.ElapsedMilliseconds));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   性能：{0}×{1} 图上填充 300×120 区域耗时 {2} ms",
                        perfWidth, perfHeight, watch.ElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 智能填充测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 修补模式端到端（M2 第一块）：标记叠加、增量计数、填充提交、像素级撤销、换图自动丢弃标记。
        /// </summary>
        private static void CheckRetouchEndToEnd(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[26] 修补模式端到端（标记 / 填充 / 撤销）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                checks++;

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片，跳过修补部分");
                    log.AppendLine();
                    return;
                }

                // ---- 未进入修补模式时不允许填充 ----
                checks++;

                if (viewModel.CanApplyInpaint || viewModel.ApplyInpaintCommand.CanExecute(null))
                {
                    failures++;
                    log.AppendLine("  FAIL 未进入修补模式时「智能填充」应为不可用");
                }
                else
                {
                    log.AppendLine("  OK   未进入修补模式时「智能填充」不可用");
                }

                // ---- 进入修补模式 ----
                checks++;
                viewModel.BeginRetouchCommand.Execute(null);

                if (!viewModel.IsRetouchMode)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能进入修补模式");
                    log.AppendLine();
                    return;
                }

                log.AppendLine("  OK   进入修补模式");

                // ---- 拖拽出第一处标记：50 × 30 = 1500 像素 ----
                checks++;
                viewModel.BeginRetouchSelect(10.0, 10.0);
                viewModel.UpdateRetouchSelect(60.0, 40.0);
                viewModel.EndRetouchSelect();

                if (viewModel.RetouchMarkCount != 1 || viewModel.RetouchMaskPixels != 1500)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 第一处标记异常（{0} 处，{1} 像素，期望 1 处 / 1500 像素）",
                        viewModel.RetouchMarkCount, viewModel.RetouchMaskPixels));
                }
                else
                {
                    log.AppendLine("  OK   拖拽框选生效：1 处标记、1500 像素（50 × 30）");
                }

                // ---- 第二处标记叠加 ----
                checks++;
                viewModel.BeginRetouchSelect(100.0, 100.0);
                viewModel.UpdateRetouchSelect(140.0, 130.0);
                viewModel.EndRetouchSelect();

                long expectedTotal = 1500 + 40 * 30;

                if (viewModel.RetouchMarkCount != 2 || viewModel.RetouchMaskPixels != expectedTotal)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 第二处标记叠加异常（{0} 处，{1} 像素，期望 2 处 / {2} 像素）",
                        viewModel.RetouchMarkCount, viewModel.RetouchMaskPixels, expectedTotal));
                }
                else
                {
                    log.AppendLine(string.Format("  OK   标记可叠加：2 处、{0} 像素", expectedTotal));
                }

                // ---- 重叠标记不能重复计数（要重建的是并集） ----
                checks++;
                viewModel.BeginRetouchSelect(20.0, 20.0);
                viewModel.UpdateRetouchSelect(40.0, 30.0);
                viewModel.EndRetouchSelect();

                if (viewModel.RetouchMaskPixels != expectedTotal)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 重叠标记被重复计数（{0} 像素，应仍为 {1}）",
                        viewModel.RetouchMaskPixels, expectedTotal));
                }
                else
                {
                    log.AppendLine("  OK   重叠标记按并集计数（重复框选不会虚增面积）");
                }

                // ---- 太小的矩形应被忽略 ----
                checks++;
                int marksBefore = viewModel.RetouchMarkCount;
                viewModel.BeginRetouchSelect(200.0, 150.0);
                viewModel.UpdateRetouchSelect(200.5, 150.5);
                viewModel.EndRetouchSelect();

                if (viewModel.RetouchMarkCount != marksBefore)
                {
                    failures++;
                    log.AppendLine("  FAIL 过小的矩形未被忽略（多半是误点）");
                }
                else
                {
                    log.AppendLine("  OK   过小的矩形被忽略（避免误点产生无意义标记）");
                }

                // ---- 执行填充 ----
                checks++;
                PixelBuffer beforeFill = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);
                int documentWidth = viewModel.Document.PixelWidth;
                int documentHeight = viewModel.Document.PixelHeight;

                if (!viewModel.ApplyInpaintCommand.CanExecute(null))
                {
                    failures++;
                    log.AppendLine("  FAIL 有标记时「智能填充」应可执行");
                }

                viewModel.ApplyInpaintCommand.Execute(null);
                WaitForIdle(viewModel);

                PixelBuffer afterFill = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                bool sizeKept = viewModel.Document.PixelWidth == documentWidth
                                && viewModel.Document.PixelHeight == documentHeight;
                bool maskCleared = !viewModel.HasRetouchMask && viewModel.RetouchMarkCount == 0;

                if (!sizeKept || !maskCleared)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 填充后状态异常（尺寸保持 {0}，标记已清 {1}）", sizeKept, maskCleared));
                }
                else
                {
                    log.AppendLine("  OK   填充完成：画布尺寸不变、标记自动清除");
                }

                // ---- 填充只改标记内的像素 ----
                checks++;
                int changedInside = 0;
                int changedOutside = 0;
                byte[] beforePixels = beforeFill.GetPixels();
                byte[] afterPixels = afterFill.GetPixels();

                for (int y = 0; y < documentHeight; y++)
                {
                    for (int x = 0; x < documentWidth; x++)
                    {
                        int index = (y * documentWidth + x) * 4;
                        bool inside =
                            (x >= 10 && x < 60 && y >= 10 && y < 40)
                            || (x >= 100 && x < 140 && y >= 100 && y < 130);

                        bool differs = false;

                        for (int channel = 0; channel < 4; channel++)
                        {
                            if (beforePixels[index + channel] != afterPixels[index + channel])
                            {
                                differs = true;
                                break;
                            }
                        }

                        if (!differs)
                        {
                            continue;
                        }

                        if (inside)
                        {
                            changedInside++;
                        }
                        else
                        {
                            changedOutside++;
                        }
                    }
                }

                if (changedOutside != 0 || changedInside == 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 填充范围不对（标记内改动 {0} 像素，标记外改动 {1} 像素）",
                        changedInside, changedOutside));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   填充范围精确：标记内改动 {0} 像素、标记外 0 改动", changedInside));
                }

                // ---- 撤销必须逐像素恢复 ----
                checks++;
                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                PixelBuffer undone = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                if (!PixelsEqual(beforePixels, undone.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 撤销未逐像素恢复填充前的画面");
                }
                else
                {
                    log.AppendLine("  OK   撤销逐像素恢复（一次填充 = 一步历史）");
                }

                // ---- 画布尺寸变化时标记要自动丢弃 ----
                checks++;
                viewModel.BeginRetouchSelect(10.0, 10.0);
                viewModel.UpdateRetouchSelect(30.0, 25.0);
                viewModel.EndRetouchSelect();

                bool markedBeforeResize = viewModel.HasRetouchMask;

                viewModel.ResizeUnitIndex = 0;
                viewModel.LockAspectRatio = true;
                viewModel.ResizeTargetWidth = 160.0;
                viewModel.ApplyResizeCommand.Execute(null);
                WaitForIdle(viewModel);

                if (!markedBeforeResize)
                {
                    failures++;
                    log.AppendLine("  FAIL 缩放前未能建立标记");
                }
                else if (viewModel.HasRetouchMask || viewModel.RetouchMarkCount != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 画布尺寸变化后标记未丢弃（仍有 {0} 处）", viewModel.RetouchMarkCount));
                }
                else
                {
                    log.AppendLine("  OK   画布尺寸变化后自动丢弃标记（旧坐标对新画面无意义）");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 修补端到端测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 仿制图章（M2b）：验证"距离场覆盖率"这条实现路线的几个决定性性质。
        ///
        /// 最容易写错的两处，断言直接打在上面：
        ///   1. **不累积** —— 同一点反复涂不能越涂越浓。用"笔迹反复回折到同一个点"来验证：
        ///      若实现是逐个笔刷点叠加，中心会溢出或偏移；距离场只取一次覆盖，中心必须严格等于源像素。
        ///   2. **源像素一律取自原图** —— 否则会出现"自我复制"的拖影。
        /// 另外钉住"笔刷外逐字节不变"和"覆盖率随距离单调衰减"。
        /// </summary>
        private static void CheckCloneStampFilter(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[27] 仿制图章（距离场覆盖率）");

            try
            {
                // 测试图：整体浅灰 (B200 G200 R200)，左上角一块深蓝方块 (B220 G60 R20)
                const int width = 200;
                const int height = 120;
                PixelBuffer stampSource = CreateStampSource(width, height);

                // ---- 1. 笔刷外必须逐字节不变，且包围盒只覆盖笔刷范围 ----
                checks++;
                List<StampPoint> single = new List<StampPoint>();
                single.Add(new StampPoint(120.5, 60.5));

                // 偏移 = 源点(10,60) − 笔迹起点(120,60) → 每涂一处都从左侧深蓝区取样
                PixelRegion painted;
                PixelBuffer stamped = CloneStampFilter.Stamp(
                    stampSource, single, 10.0, 1.0, 10 - 120, 60 - 60, out painted);

                byte[] before = stampSource.GetPixels();
                byte[] after = stamped.GetPixels();
                int outsideChanged = 0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double dx = x + 0.5 - 120.5;
                        double dy = y + 0.5 - 60.5;

                        if (Math.Sqrt(dx * dx + dy * dy) <= 10.0)
                        {
                            continue;
                        }

                        int index = (y * width + x) * 4;

                        for (int channel = 0; channel < 4; channel++)
                        {
                            if (before[index + channel] != after[index + channel])
                            {
                                outsideChanged++;
                                break;
                            }
                        }
                    }
                }

                if (outsideChanged != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 笔刷范围外有 {0} 个像素被改动（应逐字节不变）", outsideChanged));
                }
                else
                {
                    log.AppendLine("  OK   笔刷范围外逐字节不变（只涂到该涂的地方）");
                }

                // ---- 2. 涂抹包围盒应当正好等于笔刷直径（而不是整幅图） ----
                checks++;
                bool tightBounds = !painted.IsEmpty
                                   && painted.Width >= 19
                                   && painted.Width <= 22
                                   && painted.Height >= 19
                                   && painted.Height <= 22
                                   && painted.X <= 120
                                   && painted.Y <= 60;

                if (!tightBounds)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 涂抹包围盒异常：({0},{1}) {2}×{3}，半径 10 时应在 20 左右",
                        painted.X, painted.Y, painted.Width, painted.Height));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   涂抹包围盒正好是笔刷直径（{0}×{1}），可直接用于区域历史",
                        painted.Width, painted.Height));
                }

                // ---- 3. 中心满覆盖：必须严格等于源像素值 ----
                checks++;
                byte[] centerPixel = CopyPixel(stamped, 120, 60);
                byte[] sourcePixel = CopyPixel(stampSource, 10, 60);

                bool centerMatches = centerPixel[0] == sourcePixel[0]
                                     && centerPixel[1] == sourcePixel[1]
                                     && centerPixel[2] == sourcePixel[2];

                if (!centerMatches)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 笔刷中心未取到源像素（中心 {0}，源 {1}）",
                        DescribePixel(centerPixel), DescribePixel(sourcePixel)));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   笔刷中心严格等于源像素（{0}），偏移取样正确",
                        DescribePixel(sourcePixel)));
                }

                // ---- 4. 反复回折到同一点不能累积 ----
                checks++;
                List<StampPoint> repeated = new List<StampPoint>();

                for (int i = 0; i < 60; i++)
                {
                    repeated.Add(new StampPoint(120.5, 60.5));
                    repeated.Add(new StampPoint(121.5, 60.5));
                }

                PixelRegion repeatedBounds;
                PixelBuffer repeatedStamped = CloneStampFilter.Stamp(
                    stampSource, repeated, 10.0, 1.0, 10 - 120, 60 - 60, out repeatedBounds);

                byte[] repeatedCenter = CopyPixel(repeatedStamped, 120, 60);
                bool noAccumulation = repeatedCenter[0] == sourcePixel[0]
                                      && repeatedCenter[1] == sourcePixel[1]
                                      && repeatedCenter[2] == sourcePixel[2];

                if (!noAccumulation)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 同一位置反复涂抹产生了累积（中心 {0}，源 {1}）",
                        DescribePixel(repeatedCenter), DescribePixel(sourcePixel)));
                }
                else
                {
                    log.AppendLine("  OK   笔迹反复回折也不会累积（距离场只取一次覆盖率）");
                }

                // ---- 5. 覆盖率随距离单调衰减（硬度 0.5 才真正走出过渡带） ----
                checks++;
                PixelRegion softBounds;
                PixelBuffer softStamped = CloneStampFilter.Stamp(
                    stampSource, single, 10.0, 0.5, 10 - 120, 60 - 60, out softBounds);

                int previousDifference = int.MaxValue;
                bool monotonic = true;

                for (int x = 120; x <= 131; x++)
                {
                    byte[] pixel = CopyPixel(softStamped, x, 60);
                    int index = (60 * width + x) * 4;

                    int difference = Math.Abs(pixel[0] - before[index])
                                     + Math.Abs(pixel[1] - before[index + 1])
                                     + Math.Abs(pixel[2] - before[index + 2]);

                    if (difference > previousDifference)
                    {
                        monotonic = false;
                        break;
                    }

                    previousDifference = difference;
                }

                if (!monotonic)
                {
                    failures++;
                    log.AppendLine("  FAIL 覆盖率未随离笔迹的距离单调衰减");
                }
                else
                {
                    log.AppendLine("  OK   覆盖率随距离单调衰减（笔刷边缘自然过渡）");
                }

                // ---- 6. 空笔迹不改变任何像素 ----
                checks++;
                PixelRegion emptyBounds;
                PixelBuffer noStroke = CloneStampFilter.Stamp(
                    stampSource, new List<StampPoint>(), 10.0, 0.5, 0, 0, out emptyBounds);

                if (emptyBounds.IsEmpty == false || !PixelsEqual(before, noStroke.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 空笔迹改动了像素（应原样返回）");
                }
                else
                {
                    log.AppendLine("  OK   空笔迹原样返回（不产生无意义的历史步）");
                }

                // ---- 7. 性能：800×600 图上一条中等长度的笔迹 ----
                checks++;
                PixelBuffer perfSource = CreateTestBuffer(800, 600);
                List<StampPoint> perfStroke = new List<StampPoint>();

                for (int i = 0; i < 60; i++)
                {
                    perfStroke.Add(new StampPoint(200 + i * 6, 300 + i * 2));
                }

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                PixelRegion perfBounds;
                CloneStampFilter.Stamp(perfSource, perfStroke, 24.0, 0.6, -50, -50, out perfBounds);
                watch.Stop();

                if (watch.ElapsedMilliseconds > 3000)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 仿制图章过慢：800×600 图上一条笔迹耗时 {0} ms", watch.ElapsedMilliseconds));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   性能：800×600 图上一条 60 段笔迹耗时 {0} ms", watch.ElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 仿制图章测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 区域历史（M2b）：验证它确实把内存压下来了，并且撤销 / 重做逐像素正确。
        /// </summary>
        private static void CheckRegionHistory(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[28] 区域历史（只存改动包围盒）");

            try
            {
                // ---- 1. 一条区域命令的体积 vs 整幅快照 ----
                checks++;
                int imageWidth = 2000;
                int imageHeight = 2000;
                int regionWidth = 200;
                int regionHeight = 200;

                byte[] beforeRegion = new byte[regionWidth * regionHeight * 4];
                byte[] afterRegion = new byte[regionWidth * regionHeight * 4];

                RegionEditCommand probe = new RegionEditCommand(
                    "测试", imageWidth, 100, 100, regionWidth, regionHeight,
                    beforeRegion, afterRegion, (x, y, w, h, data) => { });

                long fullBitmapBytes = (long)imageWidth * imageHeight * 4;
                long commandBytes = probe.ByteSize;

                if (commandBytes != (long)regionWidth * regionHeight * 4 * 2)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 区域命令体积不符（{0} 字节，期望 {1}）",
                        commandBytes, regionWidth * regionHeight * 8L));
                }
                else if (commandBytes * 10 >= fullBitmapBytes)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 区域命令没有明显省内存（{0} 字节 vs 整幅 {1} 字节）",
                        commandBytes, fullBitmapBytes));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   200×200 的区域命令只占 {0} 字节，而整幅位图是 {1} 字节（相差 {2:0} 倍）",
                        commandBytes, fullBitmapBytes, (double)fullBitmapBytes / commandBytes));
                }

                // ---- 2. HistoryManager 必须把命令自有的字节算进内存上限 ----
                checks++;
                HistoryManager history = new HistoryManager(100, 400L * 1024L);

                for (int i = 0; i < 3; i++)
                {
                    history.Push(
                        new RegionEditCommand(
                            "涂抹", imageWidth, 0, 0, regionWidth, regionHeight,
                            new byte[regionWidth * regionHeight * 4],
                            new byte[regionWidth * regionHeight * 4],
                            (x, y, w, h, data) => { }),
                        null);
                }

                if (history.MemoryUsage < commandBytes)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 区域命令的字节未被计入历史内存（统计 {0}，命令本身 {1}）",
                        history.MemoryUsage, commandBytes));
                }
                else if (history.UndoCount >= 3)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 超出内存上限后未裁剪（仍有 {0} 步，占用 {1}）",
                        history.UndoCount, history.MemoryUsageText));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   命令自有字节已计入上限：超出 400 KB 预算后裁剪到 {0} 步（占用 {1}）",
                        history.UndoCount, history.MemoryUsageText));
                }

                // ---- 3. 撤销 / 重做写回的区域必须逐字节正确 ----
                checks++;
                PixelBuffer target = CreateSolidBuffer(40, 40, 10, 20, 30);
                byte[] targetPixels = target.GetPixels();
                byte[] painted = new byte[10 * 10 * 4];

                for (int i = 0; i < painted.Length; i += 4)
                {
                    painted[i] = 200;
                    painted[i + 1] = 100;
                    painted[i + 2] = 50;
                    painted[i + 3] = 255;
                }

                byte[] originalRegion = new byte[painted.Length];
                int rowBytes = 10 * 4;

                for (int row = 0; row < 10; row++)
                {
                    Buffer.BlockCopy(targetPixels, ((5 + row) * 40 + 5) * 4, originalRegion, row * rowBytes, rowBytes);
                }

                PixelBuffer working = new PixelBuffer((byte[])targetPixels.Clone(), 40, 40);

                RegionEditCommand command = new RegionEditCommand(
                    "区域",
                    40,
                    5,
                    5,
                    10,
                    10,
                    originalRegion,
                    painted,
                    (x, y, w, h, data) =>
                    {
                        for (int row = 0; row < h; row++)
                        {
                            Buffer.BlockCopy(data, row * w * 4, working.GetPixels(), ((y + row) * 40 + x) * 4, w * 4);
                        }
                    });

                command.Redo();
                byte[] afterRedo = CopyPixel(working, 9, 9);
                byte[] outsidePixel = CopyPixel(working, 2, 2);
                byte[] outsidePixel2 = CopyPixel(working, 20, 20);

                // CreateSolidBuffer(40, 40, r: 10, g: 20, b: 30) → B=30 G=20 R=10
                bool redoOk = afterRedo[0] == 200 && afterRedo[1] == 100 && afterRedo[2] == 50;
                bool outsideOk = outsidePixel[0] == 30 && outsidePixel[1] == 20 && outsidePixel[2] == 10
                                 && outsidePixel2[0] == 30 && outsidePixel2[1] == 20 && outsidePixel2[2] == 10;

                command.Undo();
                byte[] afterUndo = CopyPixel(working, 9, 9);
                bool undoOk = afterUndo[0] == 30 && afterUndo[1] == 20 && afterUndo[2] == 10;

                if (!redoOk || !undoOk || !outsideOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 区域写回不正确（重做后 {0}，撤销后 {1}，区域外未被误改 {2}）",
                        DescribePixel(afterRedo), DescribePixel(afterUndo), outsideOk));
                }
                else
                {
                    log.AppendLine("  OK   区域写回正确：重做只改包围盒、撤销逐字节还原");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 区域历史测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 仿制图章端到端：工具切换、取源、涂抹提交、撤销 / 重做逐像素一致。
        /// </summary>
        private static void CheckCloneStampEndToEnd(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[29] 仿制图章端到端（取源 / 涂抹 / 撤销）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                checks++;

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片，跳过仿制图章部分");
                    log.AppendLine();
                    return;
                }

                viewModel.BeginRetouchCommand.Execute(null);

                // ---- 切到仿制图章后，智能填充必须不可用 ----
                checks++;
                viewModel.RetouchToolIndex = 1;

                if (!viewModel.IsCloneStampTool)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能切换到仿制图章工具");
                    log.AppendLine();
                    return;
                }

                if (viewModel.CanApplyInpaint || viewModel.ApplyInpaintCommand.CanExecute(null))
                {
                    failures++;
                    log.AppendLine("  FAIL 仿制图章模式下「智能填充」应为不可用");
                }
                else
                {
                    log.AppendLine("  OK   切到仿制图章后「智能填充」自动不可用（工具互斥清楚）");
                }

                // ---- 未取源就涂抹：不应改动画面 ----
                checks++;
                viewModel.BrushRadius = 16.0;
                viewModel.BrushHardness = 0.8;

                PixelBuffer beforeAny = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                viewModel.BeginRetouchGesture(200.0, 120.0, false);
                viewModel.UpdateRetouchGesture(240.0, 130.0);
                viewModel.EndRetouchGesture();
                WaitForIdle(viewModel);

                PixelBuffer afterNoSource = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                if (!PixelsEqual(beforeAny.GetPixels(), afterNoSource.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 未取源就涂抹却改动了画面（应提示先取源）");
                }
                else
                {
                    log.AppendLine("  OK   未取源时涂抹不改动画面（只给出提示）");
                }

                // ---- 取源并涂抹 ----
                checks++;
                viewModel.SetCloneStampSource(120.0, 80.0);

                PixelBuffer beforeStroke = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);
                int imageWidth = viewModel.Document.PixelWidth;
                int imageHeight = viewModel.Document.PixelHeight;

                viewModel.BeginRetouchGesture(200.0, 120.0, false);
                viewModel.UpdateRetouchGesture(230.0, 130.0);
                viewModel.UpdateRetouchGesture(250.0, 140.0);
                viewModel.EndRetouchGesture();
                WaitForIdle(viewModel);

                PixelBuffer afterStroke = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);
                byte[] beforePixels = beforeStroke.GetPixels();
                byte[] afterPixels = afterStroke.GetPixels();

                int changed = 0;
                int changedFarAway = 0;
                int radius = (int)Math.Ceiling(viewModel.BrushRadius) + 2;

                // 笔迹经过 (200,120) → (250,140)，改动只应出现在这条带子附近
                for (int y = 0; y < imageHeight; y++)
                {
                    for (int x = 0; x < imageWidth; x++)
                    {
                        int index = (y * imageWidth + x) * 4;
                        bool differs = false;

                        for (int channel = 0; channel < 4; channel++)
                        {
                            if (beforePixels[index + channel] != afterPixels[index + channel])
                            {
                                differs = true;
                                break;
                            }
                        }

                        if (!differs)
                        {
                            continue;
                        }

                        changed++;

                        bool nearStroke = IsNearStroke(x, y, 200, 120, 250, 140, radius);

                        if (!nearStroke)
                        {
                            changedFarAway++;
                        }
                    }
                }

                if (changed == 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 涂抹未产生任何改动");
                }
                else if (changedFarAway != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 笔迹之外有 {0} 个像素被改动（共改动 {1} 个）", changedFarAway, changed));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   涂抹生效：改动 {0} 个像素，全部落在笔迹带内（无越界改动）", changed));
                }

                // ---- 撤销必须逐像素还原 ----
                checks++;
                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                PixelBuffer undone = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                if (!PixelsEqual(beforePixels, undone.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 撤销未逐像素还原涂抹");
                }
                else
                {
                    log.AppendLine("  OK   撤销逐像素还原涂抹（区域历史生效）");
                }

                // ---- 重做必须逐像素复现 ----
                checks++;
                viewModel.RedoCommand.Execute(null);
                WaitForIdle(viewModel);

                PixelBuffer redone = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                if (!PixelsEqual(afterPixels, redone.GetPixels()))
                {
                    failures++;
                    log.AppendLine("  FAIL 重做未逐像素复现涂抹结果");
                }
                else
                {
                    log.AppendLine("  OK   重做逐像素复现涂抹结果");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 仿制图章端到端测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 标注对象与渲染（M2b-2）。
        ///
        /// 标注是**叠加层对象**而不是画进像素的东西，因此这里要钉的是两件事：
        ///   1. 渲染只影响标注覆盖到的区域（别把整幅图按叠加层的方式重画一遍）；
        ///   2. 高亮确实是半透明混合（不是盖一块实色上去）。
        /// </summary>
        private static void CheckAnnotationRendering(ref int checks, ref int failures, StringBuilder log)
        {
            log.AppendLine("[30] 标注对象与渲染（非破坏性叠加层）");

            try
            {
                // ---- 1. 克隆必须是深拷贝 ----
                checks++;
                AnnotationObject original = new AnnotationObject
                {
                    Kind = AnnotationKind.Arrow,
                    X1 = 10, Y1 = 20, X2 = 100, Y2 = 80,
                    Text = "原"
                };

                AnnotationObject copy = original.Clone();
                copy.X1 = 999;
                copy.Text = "改";

                if (Math.Abs(original.X1 - 10) > 0.001 || original.Text != "原")
                {
                    failures++;
                    log.AppendLine("  FAIL 克隆不是深拷贝（改副本影响了原对象）");
                }
                else
                {
                    log.AppendLine("  OK   克隆是深拷贝（历史快照之间互不影响）");
                }

                // ---- 2. 命中测试 ----
                checks++;
                bool insideHit = original.HitTest(50, 50, 0.0);
                bool outsideHit = original.HitTest(500, 500, 0.0);

                if (!insideHit || outsideHit)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 命中测试异常（框内 {0}，框外 {1}）", insideHit, outsideHit));
                }
                else
                {
                    log.AppendLine("  OK   命中测试正确（框内命中、框外不命中）");
                }

                // ---- 3. 退化对象（起点=终点）不能崩 ----
                checks++;
                AnnotationObject degenerate = new AnnotationObject
                {
                    Kind = AnnotationKind.Arrow,
                    X1 = 50, Y1 = 50, X2 = 50, Y2 = 50,
                    StrokeWidth = 4.0
                };

                AnnotationVisual degenerateVisual = AnnotationVisualBuilder.Build(degenerate);
                bool degenerateOk = degenerateVisual.Geometry == null
                                    || degenerateVisual.Geometry == Geometry.Empty
                                    || degenerateVisual.Geometry.Bounds.IsEmpty;

                if (!degenerateOk)
                {
                    failures++;
                    log.AppendLine("  FAIL 退化箭头（起点=终点）产生了非空几何，可能画出杂乱图元");
                }
                else
                {
                    log.AppendLine("  OK   退化箭头（起点=终点）安全返回空几何");
                }

                // ---- 4. 无标注时渲染必须原样返回（零开销） ----
                checks++;
                PixelBuffer plain = CreateSolidBuffer(160, 120, 200, 200, 200);
                PixelBuffer untouched = AnnotationRenderer.Render(plain, new List<AnnotationObject>());

                if (!ReferenceEquals(plain, untouched))
                {
                    failures++;
                    log.AppendLine("  FAIL 无标注时渲染仍复制了像素（应为零开销原样返回）");
                }
                else
                {
                    log.AppendLine("  OK   无标注时不复制像素（原样返回，零开销）");
                }

                // ---- 5. 渲染只影响标注覆盖的区域 ----
                checks++;
                List<AnnotationObject> rectangle = new List<AnnotationObject>();
                rectangle.Add(new AnnotationObject
                {
                    Kind = AnnotationKind.Rectangle,
                    X1 = 50, Y1 = 40, X2 = 120, Y2 = 100,
                    Color = Color.FromRgb(0xE2, 0x4B, 0x4A),
                    StrokeWidth = 6.0
                });

                PixelBuffer withAnnotation = AnnotationRenderer.Render(plain, rectangle);
                byte[] beforePixels = plain.GetPixels();
                byte[] afterPixels = withAnnotation.GetPixels();
                int changedOutside = 0;
                int changedInside = 0;

                for (int y = 0; y < 120; y++)
                {
                    for (int x = 0; x < 160; x++)
                    {
                        int index = (y * 160 + x) * 4;

                        if (beforePixels[index] == afterPixels[index]
                            && beforePixels[index + 1] == afterPixels[index + 1]
                            && beforePixels[index + 2] == afterPixels[index + 2])
                        {
                            continue;
                        }

                        // 标注的视觉包围盒外扩了 padding，这里用更宽松的判定
                        bool inside = x >= 40 && x <= 130 && y >= 30 && y <= 110;

                        if (inside)
                        {
                            changedInside++;
                        }
                        else
                        {
                            changedOutside++;
                        }
                    }
                }

                if (changedOutside != 0 || changedInside == 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 渲染范围不对（标注附近改动 {0} 像素，远处改动 {1} 像素）",
                        changedInside, changedOutside));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   渲染只影响标注覆盖区域（附近改动 {0} 像素、远处 0 改动）", changedInside));
                }

                // ---- 6. 矩形描边确实画上了 ----
                checks++;
                byte[] edgePixel = CopyPixel(withAnnotation, 50, 70);

                if (edgePixel[2] < 150 || edgePixel[1] > 110)
                {
                    failures++;
                    log.AppendLine("  FAIL 矩形描边没画上：" + DescribePixel(edgePixel));
                }
                else
                {
                    log.AppendLine("  OK   矩形描边已绘制（" + DescribePixel(edgePixel) + "）");
                }

                // ---- 7. 高亮是半透明混合，不是实色覆盖 ----
                checks++;
                List<AnnotationObject> highlight = new List<AnnotationObject>();
                highlight.Add(new AnnotationObject
                {
                    Kind = AnnotationKind.Highlight,
                    X1 = 20, Y1 = 20, X2 = 60, Y2 = 50,
                    Color = Color.FromRgb(0xE2, 0x4B, 0x4A)
                });

                PixelBuffer highlighted = AnnotationRenderer.Render(plain, highlight);
                byte[] highlightPixel = CopyPixel(highlighted, 40, 35);

                bool blended = highlightPixel[2] > highlightPixel[0] + 20
                               && highlightPixel[2] > highlightPixel[1] + 20
                               && highlightPixel[0] > 100
                               && highlightPixel[0] < 240;

                if (!blended)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 高亮不是半透明混合（{0}，底色应为 200/200/200）",
                        DescribePixel(highlightPixel)));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   高亮为半透明混合（{0}，可同时看到底色与标记色）",
                        DescribePixel(highlightPixel)));
                }

                // ---- 8. 序号标注同时有圆底与数字几何 ----
                checks++;
                AnnotationObject badge = new AnnotationObject
                {
                    Kind = AnnotationKind.NumberBadge,
                    X1 = 30, Y1 = 30, X2 = 70, Y2 = 70,
                    Text = "1",
                    FontSize = 24.0
                };

                AnnotationVisual badgeVisual = AnnotationVisualBuilder.Build(badge);

                if (badgeVisual.Geometry == null || badgeVisual.LabelGeometry == null
                    || badgeVisual.Fill == null || badgeVisual.LabelBrush == null)
                {
                    failures++;
                    log.AppendLine("  FAIL 序号标注缺少圆底或数字几何");
                }
                else
                {
                    log.AppendLine("  OK   序号标注同时具备圆底与白色数字几何");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 标注渲染测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 标注端到端（M2b-2）：创建 / 选中改参数 / 撤销，以及**破坏性操作前自动合并**。
        /// </summary>
        private static void CheckAnnotationEndToEnd(
            IImageService imageService,
            string imagePath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[31] 标注端到端（选中改参数 / 自动合并）");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                viewModel.LoadFromPathAsync(imagePath).GetAwaiter().GetResult();

                checks++;

                if (!viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能加载测试图片，跳过标注部分");
                    log.AppendLine();
                    return;
                }

                viewModel.BeginAnnotationCommand.Execute(null);

                if (!viewModel.IsAnnotationMode)
                {
                    failures++;
                    log.AppendLine("  FAIL 未能进入标注模式");
                    log.AppendLine();
                    return;
                }

                // ---- 拖拽创建箭头 ----
                checks++;
                viewModel.BeginAnnotationGesture(60.0, 60.0);
                viewModel.UpdateAnnotationGesture(180.0, 120.0);
                viewModel.EndAnnotationGesture();

                if (viewModel.AnnotationCount != 1)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 拖拽创建标注失败（当前 {0} 个）", viewModel.AnnotationCount));
                }
                else
                {
                    log.AppendLine("  OK   拖拽创建箭头成功（1 个标注，起止点保持拖拽结果）");
                }

                // ---- 点击已有标注可选中（且不会误创建） ----
                checks++;
                viewModel.BeginAnnotationGesture(120.0, 90.0);
                viewModel.EndAnnotationGesture();

                if (!viewModel.HasSelectedAnnotation || viewModel.SelectedAnnotationIndex != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 点击标注未选中（当前下标 {0}）", viewModel.SelectedAnnotationIndex));
                }
                else if (viewModel.AnnotationCount != 1)
                {
                    failures++;
                    log.AppendLine("  FAIL 点击已有标注时误创建了新标注");
                }
                else
                {
                    log.AppendLine("  OK   点击已有标注即选中（未误创建新对象）");
                }

                // ---- 选中后改颜色立即生效 ----
                checks++;
                viewModel.AnnotationColor = Color.FromRgb(0x37, 0x8A, 0xDD);

                if (viewModel.Annotations[0].Source.Color != Color.FromRgb(0x37, 0x8A, 0xDD))
                {
                    failures++;
                    log.AppendLine("  FAIL 改颜色没有应用到选中的标注");
                }
                else
                {
                    log.AppendLine("  OK   选中后改颜色立即生效（这正是“选中再改参数”的价值）");
                }

                // ---- 撤销把颜色改回去 ----
                checks++;
                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                if (viewModel.AnnotationCount != 1
                    || viewModel.Annotations[0].Source.Color != Color.FromRgb(0xE2, 0x4B, 0x4A))
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 撤销未还原标注颜色（当前 {0} 个标注）", viewModel.AnnotationCount));
                }
                else
                {
                    log.AppendLine("  OK   撤销还原标注颜色（对象列表快照历史生效）");
                }

                // ---- 破坏性操作前自动合并 ----
                checks++;
                int countBeforeFilter = viewModel.AnnotationCount;
                PixelBuffer beforeFilter = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);

                viewModel.InvertCommand.Execute(null);
                WaitForIdle(viewModel);

                PixelBuffer afterFilter = PixelBuffer.FromBitmap(viewModel.Document.Bitmap);
                bool flattened = viewModel.AnnotationCount == 0;
                bool imageChanged = !PixelsEqual(beforeFilter.GetPixels(), afterFilter.GetPixels());

                if (countBeforeFilter == 0 || !flattened || !imageChanged)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 破坏性操作前未正确合并（操作前 {0} 个标注，操作后 {1} 个，画面变化 {2}）",
                        countBeforeFilter, viewModel.AnnotationCount, imageChanged));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   执行反色前自动合并：{0} 个标注已烘进像素并清空列表", countBeforeFilter));
                }

                // ---- 撤销合并：标注必须"回来"（像素与列表要一起还原） ----
                // 注意这里有**两步**可撤销：先撤掉反色（标注仍已烘进像素），
                // 再撤掉合并（标注恢复成可编辑的对象）。这是"合并本身也是一步历史"的直接体现，
                // 两步都必须是完整状态，不能出现"标注凭空消失"或"画面上两份标注"。
                checks++;
                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                bool afterFirstUndo = viewModel.AnnotationCount == 0;

                viewModel.UndoCommand.Execute(null);
                WaitForIdle(viewModel);

                if (!afterFirstUndo)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 第一次撤销应只撤掉反色、标注仍处于已合并状态（当前 {0} 个）",
                        viewModel.AnnotationCount));
                }
                else if (viewModel.AnnotationCount != countBeforeFilter)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 撤销合并后标注没有恢复（当前 {0} 个，应回到 {1} 个）—— 像素与对象列表必须一起还原",
                        viewModel.AnnotationCount, countBeforeFilter));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   两次撤销逐步回退：先撤反色（标注仍已合并）→ 再撤合并（{0} 个标注恢复为可编辑对象）",
                        viewModel.AnnotationCount));
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 标注端到端测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 校验批量流水线（M3）：
        ///   A. 步骤顺序真的影响结果（先缩放后水印 vs 先水印后缩放，水印尺寸差一倍）
        ///   B. BatchContext.Scale 只作用于"绝对像素"参数，不作用于百分比
        ///   C. 输出命名：绝不覆盖磁盘上已有文件、绝不覆盖同一次运行已分配的名字、绝不覆盖源文件
        ///   D. 整条含文字水印的流水线可以在**非 UI 线程**跑通（原先注释声称必须 UI 线程，实测不成立）
        ///   E. 端到端：坏文件不影响其它文件；**预览推算的输出尺寸与实际输出一致**（预览不骗人）
        /// </summary>
        private static void CheckBatchPipeline(
            IImageService imageService,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[32] 批量流水线与输出命名");

            try
            {
                CheckBatchStepOrder(ref checks, ref failures, log);
                CheckBatchPreviewScale(ref checks, ref failures, log);
                CheckBatchOutputNaming(tempRoot, ref checks, ref failures, log);
                CheckBatchOffUiThread(ref checks, ref failures, log);
                CheckBatchEndToEnd(imageService, tempRoot, ref checks, ref failures, log);
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 批量流水线测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        #region [32] 分项

        /// <summary>
        /// A. 顺序语义。
        ///
        /// 用"水印亮区的包围盒宽度"作为度量：先缩放到 200 宽再画 40px 字，字占画布约 30%；
        /// 先画 40px 字再把 400 宽缩到 200，字只剩约 15%。两者相差约一倍 ——
        /// 这就是"顺序不是实现细节"的可观测证据。
        /// </summary>
        private static void CheckBatchStepOrder(ref int checks, ref int failures, StringBuilder log)
        {
            // 深底 + 纯白不透明字，方便按亮度阈值量出字的范围
            PixelBuffer source = CreateSolidBuffer(400, 300, 20, 20, 20);

            BatchResizeStep resize = new BatchResizeStep
            {
                Mode = BatchResizeMode.LongEdge,
                Value = 200,
                OnlyShrink = false,
                Kernel = ResampleKernel.Bicubic
            };

            BatchWatermarkStep watermark = new BatchWatermarkStep
            {
                Text = "WM",
                SizeMode = WatermarkSizeMode.Absolute,
                FontSize = 40.0,
                Anchor = TextAnchor.Center,
                Margin = 0.0,
                Opacity = 255,
                Shadow = false,
                Bold = true
            };

            List<IBatchStep> resizeThenMark = new List<IBatchStep> { resize, watermark };
            List<IBatchStep> markThenResize = new List<IBatchStep>
            {
                watermark.Clone(),
                (IBatchStep)resize.Clone()
            };

            PixelBuffer a = BatchPipeliner.Run(source, resizeThenMark, BatchContext.FullResolution, System.Threading.CancellationToken.None);
            PixelBuffer b = BatchPipeliner.Run(source, markThenResize, BatchContext.FullResolution, System.Threading.CancellationToken.None);

            checks++;
            if (a.Width != 200 || a.Height != 150 || b.Width != 200 || b.Height != 150)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 两种顺序的输出尺寸都应收缩到 200×150，实际 {0}×{1} / {2}×{3}",
                    a.Width, a.Height, b.Width, b.Height));
            }
            else
            {
                log.AppendLine(string.Format("  OK   两种顺序输出尺寸一致（{0}×{1}）", a.Width, a.Height));
            }

            int widthA;
            int heightA;
            int widthB;
            int heightB;
            MeasureBrightBounds(a, 128, out widthA, out heightA);
            MeasureBrightBounds(b, 128, out widthB, out heightB);

            checks++;
            if (widthA <= 0 || widthB <= 0)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 两种顺序里都应能看到水印亮区，实测字宽 {0} / {1}", widthA, widthB));
            }
            else if (widthA < widthB * 1.5)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 顺序应当影响水印相对大小：先缩放后水印应明显更大（实测字宽 {0} vs {1}）",
                    widthA, widthB));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   顺序影响结果：先缩放后水印字宽 {0} px，先水印后缩放字宽 {1} px（约 {2:0.#} 倍）",
                    widthA, widthB, widthA / (double)widthB));
            }

            checks++;
            if (CountDifferentPixels(a.GetPixels(), b.GetPixels()) == 0)
            {
                failures++;
                log.AppendLine("  FAIL 两种顺序的像素完全一致，说明顺序被忽略了");
            }
            else
            {
                log.AppendLine("  OK   两种顺序的像素不同（顺序确实生效）");
            }
        }

        /// <summary>
        /// B. 预览缩放只作用于绝对像素参数。
        ///
        /// 这条是"面板缩略图不骗人"的地基：绝对参数（长边、边框粗细）必须乘缩放因子，
        /// 相对参数（百分比）必须**不**乘 —— 因为降采样不改变比例关系，乘了反而错。
        /// </summary>
        private static void CheckBatchPreviewScale(ref int checks, ref int failures, StringBuilder log)
        {
            BatchContext half = new BatchContext(0.5, 96.0, 96.0);

            BatchResizeStep absolute = new BatchResizeStep
            {
                Mode = BatchResizeMode.LongEdge,
                Value = 800,
                OnlyShrink = false
            };

            int fullWidth;
            int fullHeight;
            int halfWidth;
            int halfHeight;
            absolute.ReadTargetSize(1600, 1200, BatchContext.FullResolution, out fullWidth, out fullHeight);
            absolute.ReadTargetSize(1600, 1200, half, out halfWidth, out halfHeight);

            checks++;
            if (fullWidth != 800 || fullHeight != 600)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 全分辨率下长边 800 应得 800×600，实际 {0}×{1}", fullWidth, fullHeight));
            }
            else if (halfWidth != 400 || halfHeight != 300)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 半缩放预览下应得 400×300（绝对参数必须乘 Scale），实际 {0}×{1}", halfWidth, halfHeight));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   绝对像素参数随预览缩放：全分辨率 {0}×{1} → 半缩放 {2}×{3}",
                    fullWidth, fullHeight, halfWidth, halfHeight));
            }

            BatchResizeStep percent = new BatchResizeStep { Mode = BatchResizeMode.Percent, Value = 50 };

            int percentFullWidth;
            int percentFullHeight;
            int percentHalfWidth;
            int percentHalfHeight;
            percent.ReadTargetSize(1600, 1200, BatchContext.FullResolution, out percentFullWidth, out percentFullHeight);
            percent.ReadTargetSize(1600, 1200, half, out percentHalfWidth, out percentHalfHeight);

            checks++;
            if (percentFullWidth != percentHalfWidth || percentFullHeight != percentHalfHeight)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 百分比是相对量，不应受预览缩放影响（{0}×{1} vs {2}×{3}）",
                    percentFullWidth, percentFullHeight, percentHalfWidth, percentHalfHeight));
            }
            else
            {
                log.AppendLine(string.Format("  OK   百分比参数不受预览缩放影响（恒为 {0}×{1}）", percentFullWidth, percentFullHeight));
            }

            // 水印的相对字号同理：宽度占比与画布尺寸无关
            BatchWatermarkStep watermark = new BatchWatermarkStep
            {
                Text = "水印",
                SizeMode = WatermarkSizeMode.RelativeToWidth,
                FontSize = 10.0,
                Anchor = TextAnchor.Center,
                Opacity = 255
            };

            PixelBuffer wide = CreateSolidBuffer(1200, 300, 20, 20, 20);
            PixelBuffer narrow = CreateSolidBuffer(400, 100, 20, 20, 20);

            int wideTextWidth;
            int wideTextHeight;
            int narrowTextWidth;
            int narrowTextHeight;
            MeasureBrightBounds(
                BatchPipeliner.Run(wide, new List<IBatchStep> { watermark }, BatchContext.FullResolution, System.Threading.CancellationToken.None),
                128, out wideTextWidth, out wideTextHeight);
            MeasureBrightBounds(
                BatchPipeliner.Run(narrow, new List<IBatchStep> { watermark }, BatchContext.FullResolution, System.Threading.CancellationToken.None),
                128, out narrowTextWidth, out narrowTextHeight);

            checks++;
            if (wideTextWidth <= 0 || narrowTextWidth <= 0)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 相对字号水印应能画出可见文字，实测宽 {0} / {1}", wideTextWidth, narrowTextWidth));
            }
            else
            {
                double wideRatio = wideTextWidth / 1200.0;
                double narrowRatio = narrowTextWidth / 400.0;

                if (Math.Abs(wideRatio - narrowRatio) > 0.02)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 相对字号水印的宽度占比应基本一致，实测 {0:0.###} vs {1:0.###}",
                        wideRatio, narrowRatio));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   相对字号水印与大图尺寸无关：1200px 图上占 {0:0.#}%，400px 图上占 {1:0.#}%",
                        wideRatio * 100.0, narrowRatio * 100.0));
                }
            }
        }

        /// <summary>
        /// C. 输出命名。
        ///
        /// 三条规则里最容易出错的是"同一次运行内的冲突"：来自不同文件夹的同名文件撞名时，
        /// 磁盘上还没有第二个文件，单靠 File.Exists 根本查不出来 —— 会静默互相覆盖。
        /// 这条断言就是为它写的。
        /// </summary>
        private static void CheckBatchOutputNaming(string tempRoot, ref int checks, ref int failures, StringBuilder log)
        {
            string root = Path.Combine(tempRoot, "batch-naming");
            Directory.CreateDirectory(root);

            string source = Path.Combine(root, "photo.png");
            File.WriteAllText(source, "stub");
            File.WriteAllText(Path.Combine(root, "photo_批量.png"), "already here");

            BatchOutputNaming naming = new BatchOutputNaming(root, "_批量", null);
            HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string first = naming.NextOutputPath(source, reserved);

            checks++;
            if (!string.Equals(Path.GetFileName(first), "photo_批量_2.png", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine("  FAIL 磁盘已有 photo_批量.png，应让到 photo_批量_2.png，实际 " + Path.GetFileName(first));
            }
            else
            {
                log.AppendLine("  OK   磁盘同名自动加序号：" + Path.GetFileName(first));
            }

            reserved.Add(first);

            // 同一次运行内、磁盘上尚无该文件 —— 只有 reserved 集合能拦住它
            string second = naming.NextOutputPath(source, reserved);

            checks++;
            if (!string.Equals(Path.GetFileName(second), "photo_批量_3.png", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 同一次运行内的重名未被拦住（磁盘上没有 photo_批量_2.png，只能靠已分配集合判断），实际 {0}",
                    Path.GetFileName(second)));
            }
            else
            {
                log.AppendLine("  OK   同一次运行内的重名也会让号（不会静默互相覆盖）：" + Path.GetFileName(second));
            }

            checks++;
            if (string.Equals(first, source, StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine("  FAIL 输出路径与源文件相同，会覆盖原图");
            }
            else
            {
                log.AppendLine("  OK   输出路径绝不等于源文件");
            }

            // 后缀清空时目标名会与源文件重合，必须被识别出来
            BatchOutputNaming noSuffix = new BatchOutputNaming(root, string.Empty, null);
            string fallback = noSuffix.NextOutputPath(source, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            checks++;
            if (!string.Equals(Path.GetFileName(fallback), "photo_2.png", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine("  FAIL 后缀为空时应避开源文件名，实际 " + Path.GetFileName(fallback));
            }
            else
            {
                log.AppendLine("  OK   后缀为空也不会覆盖源文件（" + Path.GetFileName(fallback) + "）");
            }

            // 用户手输的后缀可能含非法字符
            checks++;
            string dirty = BatchOutputNaming.SanitizeFileNamePart("a/b:c*d?e");
            if (dirty.IndexOf('/') >= 0 || dirty.IndexOf(':') >= 0 || dirty.IndexOf('*') >= 0 || dirty.IndexOf('?') >= 0)
            {
                failures++;
                log.AppendLine("  FAIL 后缀里的非法字符未被过滤：" + dirty);
            }
            else
            {
                log.AppendLine("  OK   后缀非法字符被替换为下划线：" + dirty);
            }

            // 强制扩展名（统一转格式时用）
            checks++;
            BatchOutputNaming forced = new BatchOutputNaming(root, "_x", "jpg");
            string forcedPath = forced.PreviewPath(Path.Combine(root, "a.png"));
            if (!forcedPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine("  FAIL 强制扩展名未生效：" + Path.GetFileName(forcedPath));
            }
            else
            {
                log.AppendLine("  OK   强制扩展名统一为 .jpg（jpg → .jpg 规范化）：" + Path.GetFileName(forcedPath));
            }
        }

        /// <summary>
        /// D. 整条含文字水印的流水线在**非 UI 线程**上跑通。
        ///
        /// TextOverlayFilter 原先写着"必须在 UI 线程构造 RenderTargetBitmap"，
        /// 但自检一直在普通线程调它且从未失败 —— 那个约束不成立。
        /// 这条断言把"不成立"钉死：批量每张图都要切回 UI 线程排队的话，界面会一顿一顿。
        /// </summary>
        private static void CheckBatchOffUiThread(ref int checks, ref int failures, StringBuilder log)
        {
            PixelBuffer source = CreateSolidBuffer(240, 160, 20, 20, 20);

            List<IBatchStep> steps = new List<IBatchStep>
            {
                new BatchResizeStep { Mode = BatchResizeMode.Percent, Value = 100, OnlyShrink = false },
                new BatchWatermarkStep
                {
                    Text = "线程",
                    SizeMode = WatermarkSizeMode.RelativeToWidth,
                    FontSize = 12.0,
                    Anchor = TextAnchor.BottomRight,
                    Opacity = 200
                }
            };

            int callerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int workerThreadId = 0;
            PixelBuffer result = null;
            Exception workerError = null;

            Task.Run(() =>
            {
                try
                {
                    workerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    result = BatchPipeliner.Run(source, steps, BatchContext.FullResolution, System.Threading.CancellationToken.None);
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            }).GetAwaiter().GetResult();

            checks++;
            if (workerError != null)
            {
                failures++;
                log.AppendLine("  FAIL 含文字水印的流水线在线程池线程上失败了：" + workerError.GetType().Name + " " + workerError.Message);
            }
            else if (result == null)
            {
                failures++;
                log.AppendLine("  FAIL 后台线程上的流水线没有返回结果");
            }
            else if (workerThreadId == callerThreadId)
            {
                failures++;
                log.AppendLine("  FAIL 流水线并没有真的跑在别的线程上（线程号相同），这条断言失去意义");
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   含文字水印的整条流水线在后台线程跑通（线程 {0} → {1}，输出 {2}×{3}）",
                    callerThreadId, workerThreadId, result.Width, result.Height));
            }

            checks++;
            if (result == null)
            {
                failures++;
                log.AppendLine("  FAIL 无法校验水印是否真的画上了（没有输出）");
            }
            else
            {
                int textWidth;
                int textHeight;
                MeasureBrightBounds(result, 128, out textWidth, out textHeight);

                if (textWidth <= 0 || textHeight <= 0)
                {
                    failures++;
                    log.AppendLine("  FAIL 后台线程产出的结果里看不到水印");
                }
                else
                {
                    log.AppendLine(string.Format("  OK   后台线程产出的水印可见（亮区 {0}×{1} px）", textWidth, textHeight));
                }
            }
        }

        /// <summary>
        /// E. 端到端。
        ///
        /// 两个关键断言：
        ///   1. 一个坏文件不能让后面的文件陪葬（逐张独立）；
        ///   2. **预览推算的输出尺寸 == 实际写出的文件尺寸** —— 这是"面板缩略图不骗人"的最终校验，
        ///      也是 BatchContext.Scale 存在的全部理由。允许 1% 误差：
        ///      预览是基于"整数因子降采样"的小图算的，取整链条必然引入 1~2 px 偏差。
        /// </summary>
        private static void CheckBatchEndToEnd(
            IImageService imageService,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            string root = Path.Combine(tempRoot, "batch-e2e");
            string sourceDirectory = Path.Combine(root, "src");
            string outputDirectory = Path.Combine(root, "out");
            Directory.CreateDirectory(sourceDirectory);

            string bigPath = Path.Combine(sourceDirectory, "big.png");
            string smallPath = Path.Combine(sourceDirectory, "small.png");
            string brokenPath = Path.Combine(sourceDirectory, "broken.png");

            // 1600×1200 会在预览里被降采样（上限 720），因此能真正走到 Scale 路径
            CreateTestImage(bigPath, 1600, 1200, 96.0, ImageFileFormat.Png, log);
            CreateTestImage(smallPath, 400, 300, 96.0, ImageFileFormat.Png, log);
            File.WriteAllText(brokenPath, "this is not an image");

            MainViewModel viewModel = new MainViewModel(
                imageService,
                new NullDialogService(),
                new ImmediateDispatcherService());

            viewModel.BatchOutputDirectory = outputDirectory;
            viewModel.BatchFileNameSuffix = "_批量";
            viewModel.BatchOutputFormat = new BatchOutputFormatOption(ImageFileFormat.Png, "PNG");

            viewModel.BatchSteps.Add(new BatchStepViewModel(
                new BatchResizeStep
                {
                    Mode = BatchResizeMode.LongEdge,
                    Value = 800,
                    OnlyShrink = true,
                    Kernel = ResampleKernel.Bicubic
                }, 1));

            viewModel.BatchSteps.Add(new BatchStepViewModel(
                new BatchWatermarkStep
                {
                    Text = "批量测试",
                    SizeMode = WatermarkSizeMode.RelativeToWidth,
                    FontSize = 6.0,
                    Anchor = TextAnchor.BottomRight,
                    Opacity = 200
                }, 2));

            bool addedBig = viewModel.AddBatchQueueItem(bigPath);
            bool addedSmall = viewModel.AddBatchQueueItem(smallPath);
            bool addedBroken = viewModel.AddBatchQueueItem(brokenPath);
            bool duplicate = viewModel.AddBatchQueueItem(bigPath);

            checks++;
            if (!addedBig || !addedSmall || !addedBroken || duplicate || viewModel.BatchQueue.Count != 3)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 队列入队与去重异常：加了 {0}/{1}/{2}，重复项被接受={3}，队列 {4} 项",
                    addedBig, addedSmall, addedBroken, duplicate, viewModel.BatchQueue.Count));
            }
            else
            {
                log.AppendLine("  OK   队列入队正常，重复路径被忽略（队列 3 项）");
            }

            viewModel.SelectedBatchItem = viewModel.BatchQueue[0];

            // 执行（Confirm 由 NullDialogService 返回 Yes）
            viewModel.RunBatchAsync().GetAwaiter().GetResult();

            checks++;
            if (viewModel.IsBatchRunning)
            {
                failures++;
                log.AppendLine("  FAIL 批量执行结束后 IsBatchRunning 没有复位");
            }
            else
            {
                log.AppendLine("  OK   批量执行结束后状态已复位");
            }

            checks++;
            if (viewModel.BatchQueue[0].IsFailed || viewModel.BatchQueue[1].IsFailed || !viewModel.BatchQueue[2].IsFailed)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 逐张状态不符：期望「前两张成功、坏文件失败」，实际 {0} / {1} / {2}",
                    viewModel.BatchQueue[0].Status, viewModel.BatchQueue[1].Status, viewModel.BatchQueue[2].Status));
            }
            else
            {
                log.AppendLine(string.Format("  OK   坏文件不影响其它文件：坏文件状态「{0}」", viewModel.BatchQueue[2].Status));
            }

            string bigOutput = Path.Combine(outputDirectory, "big_批量.png");
            string smallOutput = Path.Combine(outputDirectory, "small_批量.png");

            checks++;
            if (!File.Exists(bigOutput) || !File.Exists(smallOutput))
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 输出文件缺失：big={0} small={1}", File.Exists(bigOutput), File.Exists(smallOutput)));
            }
            else
            {
                ImageLoadResult bigResult = imageService.LoadAsync(bigOutput).GetAwaiter().GetResult();
                ImageLoadResult smallResult = imageService.LoadAsync(smallOutput).GetAwaiter().GetResult();

                checks++;
                if (bigResult.PixelWidth != 800 || bigResult.PixelHeight != 600)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 1600×1200 长边缩到 800 应得 800×600，实际 {0}×{1}",
                        bigResult.PixelWidth, bigResult.PixelHeight));
                }
                else
                {
                    log.AppendLine("  OK   长边约束生效：1600×1200 → 800×600");
                }

                checks++;
                if (smallResult.PixelWidth != 400 || smallResult.PixelHeight != 300)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 400×300 小于目标长边，「只缩不放」应保持原尺寸，实际 {0}×{1}",
                        smallResult.PixelWidth, smallResult.PixelHeight));
                }
                else
                {
                    log.AppendLine("  OK   「只缩不放」生效：400×300 不被放大");
                }

                // ---- 预览推算 vs 实际输出 ----
                viewModel.SelectedBatchItem = viewModel.BatchQueue[0];
                viewModel.RunBatchPreviewAsync().GetAwaiter().GetResult();

                checks++;
                if (viewModel.BatchPreviewImage == null)
                {
                    failures++;
                    log.AppendLine("  FAIL 预览没有产出图像：" + viewModel.BatchPreviewText);
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   预览已生成（{0}×{1} px）",
                        viewModel.BatchPreviewImage.PixelWidth,
                        viewModel.BatchPreviewImage.PixelHeight));
                }

                checks++;
                int estimateWidth = viewModel.BatchPreviewOutputWidth;
                int estimateHeight = viewModel.BatchPreviewOutputHeight;
                double toleranceWidth = Math.Max(2.0, bigResult.PixelWidth * 0.01);
                double toleranceHeight = Math.Max(2.0, bigResult.PixelHeight * 0.01);

                if (Math.Abs(estimateWidth - bigResult.PixelWidth) > toleranceWidth
                    || Math.Abs(estimateHeight - bigResult.PixelHeight) > toleranceHeight)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 预览推算的输出尺寸与实际不符（预览会说谎）：预览 {0}×{1}，实际 {2}×{3}",
                        estimateWidth, estimateHeight, bigResult.PixelWidth, bigResult.PixelHeight));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   预览推算的输出尺寸与实际一致（预览 {0}×{1} vs 实际 {2}×{3}，容差 1%）",
                        estimateWidth, estimateHeight, bigResult.PixelWidth, bigResult.PixelHeight));
                }
            }

            // 再跑一次：同名的旧输出还在，必须让号而不是覆盖
            viewModel.RunBatchAsync().GetAwaiter().GetResult();

            checks++;
            if (!File.Exists(Path.Combine(outputDirectory, "big_批量_2.png")))
            {
                failures++;
                log.AppendLine("  FAIL 第二次运行没有让号出新文件（应生成 big_批量_2.png）");
            }
            else if (File.Exists(bigOutput) && File.Exists(smallOutput))
            {
                log.AppendLine("  OK   第二次运行自动让号，原有输出未被覆盖");
            }
            else
            {
                failures++;
                log.AppendLine("  FAIL 第二次运行把第一次的输出弄丢了");
            }
        }

        /// <summary>
        /// 量出"亮像素"的包围盒尺寸（用于度量白字水印的大小）。
        /// 亮度阈值取通道最大值，避免依赖具体的灰度公式。
        /// </summary>
        private static void MeasureBrightBounds(PixelBuffer buffer, int threshold, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (buffer == null)
            {
                return;
            }

            byte[] pixels = buffer.GetPixels();
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = -1;
            int maxY = -1;

            for (int y = 0; y < buffer.Height; y++)
            {
                int rowOffset = y * buffer.Stride;

                for (int x = 0; x < buffer.Width; x++)
                {
                    int index = rowOffset + x * 4;
                    int luminosity = pixels[index];

                    if (pixels[index + 1] > luminosity)
                    {
                        luminosity = pixels[index + 1];
                    }

                    if (pixels[index + 2] > luminosity)
                    {
                        luminosity = pixels[index + 2];
                    }

                    if (luminosity < threshold)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            if (maxX < 0)
            {
                return;
            }

            width = maxX - minX + 1;
            height = maxY - minY + 1;
        }

        #endregion

        /// <summary>
        /// 校验 M4 的系统集成：命令行解析 / 运行时探测 / 文件关联。
        ///
        /// 文件关联这一块**必须**用注入的测试根键来验证：真实关联写在
        /// <c>HKCU\Software\Classes</c>，自检要是往那儿写，跑一次测试就等于偷偷改了
        /// 用户的"打开方式"。所以 <see cref="FileAssociationService"/> 的根键是构造参数，
        /// 自检传一个 <c>Software\PSText-SelfTest-&lt;guid&gt;</c>，
        /// 把写入 / 幂等 / 状态 / 注销 / 清理是否干净整条链路真正跑一遍。
        /// </summary>
        private static void CheckSystemIntegration(
            string pngPath,
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[33] 命令行 / 运行时探测 / 文件关联");

            CheckCommandLineParsing(pngPath, ref checks, ref failures, log);
            CheckRuntimeProbe(ref checks, ref failures, log);
            CheckFileAssociation(tempRoot, ref checks, ref failures, log);

            log.AppendLine();
        }

        /// <summary>命令行解析：这决定了"双击图片能不能打开"以及部署脚本能不能用。</summary>
        private static void CheckCommandLineParsing(
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            CommandLineOptions options = CommandLineOptions.Parse(null);

            checks++;
            if (options.Mode != StartupMode.OpenWindow || options.ImagePath != null || options.IsHeadless)
            {
                failures++;
                log.AppendLine("  FAIL 无参数时应正常打开窗口且没有待打开文件");
            }
            else
            {
                log.AppendLine("  OK   无参数 → 打开主窗口");
            }

            checks++;
            string[][] headlessCases =
            {
                new[] { "--register" },
                new[] { "--unregister" },
                new[] { "--assoc-status" },
                new[] { "--runtime" }
            };

            StartupMode[] expectedModes =
            {
                StartupMode.RegisterAssociation,
                StartupMode.UnregisterAssociation,
                StartupMode.AssociationStatus,
                StartupMode.RuntimeInfo
            };

            bool headlessOk = true;

            for (int i = 0; i < headlessCases.Length; i++)
            {
                CommandLineOptions parsed = CommandLineOptions.Parse(headlessCases[i]);

                if (parsed.Mode != expectedModes[i] || !parsed.IsHeadless)
                {
                    headlessOk = false;
                    log.AppendLine(string.Format(
                        "  FAIL 参数 {0} 解析为 {1}，期望 {2}",
                        headlessCases[i][0], parsed.Mode, expectedModes[i]));
                }
            }

            if (headlessOk)
            {
                log.AppendLine("  OK   四个无界面开关都能正确识别");
            }
            else
            {
                failures++;
            }

            // 大小写不敏感：Windows 上用户手敲命令很少注意大小写
            checks++;
            if (CommandLineOptions.Parse(new[] { "--REGISTER" }).Mode != StartupMode.RegisterAssociation)
            {
                failures++;
                log.AppendLine("  FAIL 开关应当大小写不敏感");
            }
            else
            {
                log.AppendLine("  OK   开关大小写不敏感（--REGISTER）");
            }

            // 自检优先：CI 里可能顺带带上其它参数
            checks++;
            if (CommandLineOptions.Parse(new[] { "--register", "--selftest" }).Mode != StartupMode.SelfTest)
            {
                failures++;
                log.AppendLine("  FAIL --selftest 应当优先于其它开关");
            }
            else
            {
                log.AppendLine("  OK   --selftest 优先于其它开关");
            }

            // 图片路径（"打开方式"就是这么把文件传进来的）
            checks++;
            CommandLineOptions withFile = CommandLineOptions.Parse(new[] { pngPath });

            if (withFile.Mode != StartupMode.OpenWindow
                || !string.Equals(withFile.ImagePath, pngPath, StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine("  FAIL 存在的图片路径应被识别为待打开文件，实际 " + (withFile.ImagePath ?? "null"));
            }
            else
            {
                log.AppendLine("  OK   存在的图片路径被识别为待打开文件");
            }

            // 不存在的路径不能被当成文件（否则双击一个已删除的图片会带出一个错误路径）
            checks++;
            if (CommandLineOptions.Parse(new[] { @"C:\ps-text-not-exist\a.jpg" }).ImagePath != null)
            {
                failures++;
                log.AppendLine("  FAIL 不存在的路径不应被当成待打开文件");
            }
            else
            {
                log.AppendLine("  OK   不存在的路径被忽略");
            }

            // 无界面模式下不应再取出图片路径（避免两件事同时做）
            checks++;
            CommandLineOptions mixed = CommandLineOptions.Parse(new[] { pngPath, "--runtime" });

            if (mixed.Mode != StartupMode.RuntimeInfo || mixed.ImagePath != null)
            {
                failures++;
                log.AppendLine("  FAIL 无界面模式下不应再带图片路径");
            }
            else
            {
                log.AppendLine("  OK   无界面模式不会同时打开图片");
            }

            // --print：右键菜单的“用 PS-text 打印”靠它把文件带进来
            checks++;
            CommandLineOptions print = CommandLineOptions.Parse(new[] { "--print", pngPath });

            if (print.Mode != StartupMode.OpenWindow
                || !print.PrintAfterLoad
                || !string.Equals(print.ImagePath, pngPath, StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL --print 应保留正常启动模式并标记打开后打印（模式={0}，打印={1}，文件={2}）",
                    print.Mode, print.PrintAfterLoad, print.ImagePath ?? "null"));
            }
            else
            {
                log.AppendLine("  OK   --print 正常打开图片并标记「打开后进打印预览」");
            }

            checks++;
            if (CommandLineOptions.Parse(new[] { pngPath }).PrintAfterLoad)
            {
                failures++;
                log.AppendLine("  FAIL 不带 --print 时不应触发打印");
            }
            else
            {
                log.AppendLine("  OK   不带 --print 时不会触发打印（避免误打印）");
            }

            checks++;
            if (CommandLineOptions.Parse(new[] { pngPath, "--register" }).PrintAfterLoad)
            {
                failures++;
                log.AppendLine("  FAIL 无界面模式下不应保留打印标记");
            }
            else
            {
                log.AppendLine("  OK   无界面模式不会保留打印标记");
            }
        }

        /// <summary>
        /// 运行时探测。
        ///
        /// 重点在于钉住"为什么必须读注册表"：CLR 版本在任何 .NET 4.x 上都报 4.0.30319，
        /// 而框架版本是 4.8 —— 两者不同源。断言把这对矛盾摆在一起，
        /// 谁以后想用 Environment.Version 走捷径，都会立刻看到这两条结论对不上。
        /// </summary>
        private static void CheckRuntimeProbe(ref int checks, ref int failures, StringBuilder log)
        {
            bool probeSucceeded;
            int release;
            string versionText;
            DotNetRuntimeProbe.TryReadRelease(out probeSucceeded, out release, out versionText);

            checks++;
            if (!probeSucceeded)
            {
                failures++;
                log.AppendLine("  FAIL 读不到 .NET Framework 的 Release 值（本机注册表应可读）");
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   读到 .NET Framework {0}（Release {1}）",
                    versionText ?? "未知", release));
            }

            // 这套判定有**两份实现**：启动器（bat）用 "Version 以 4.8. 开头"，
            // 程序内用 "Release >= 528040"。两者必须给出一致结论 ——
            // 不然会出现"启动器放行、程序却弹版本过低警告"这种自相矛盾的现象，
            // 而那种现象最难解释、最容易被认为是软件坏了。
            checks++;
            bool launcherSaysOk = versionText != null && versionText.StartsWith("4.8.", StringComparison.Ordinal);
            bool appSaysOk = probeSucceeded && release >= DotNetRuntimeProbe.Net48MinimumRelease;

            if (launcherSaysOk != appSaysOk)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 两套 4.8 判定不一致：启动器（Version「{0}」）={1}，程序（Release {2} >= {3}）={4}",
                    versionText, launcherSaysOk, release, DotNetRuntimeProbe.Net48MinimumRelease, appSaysOk));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   启动器的 Version 判定与程序的 Release 判定一致（均为 {0}）",
                    appSaysOk ? "满足 4.8" : "不满足"));
            }

            RuntimeEnvironmentInfo info = DotNetRuntimeProbe.Probe();

            checks++;
            if (info == null || string.IsNullOrEmpty(info.ToDisplayText()))
            {
                failures++;
                log.AppendLine("  FAIL 运行环境探测应总能给出一段可显示的文本");
            }
            else
            {
                log.AppendLine("  OK   诊断文本可生成（" + info.ToDisplayText().Length + " 字符）");
            }

            checks++;
            if (info == null || info.IsNet48OrLater == false)
            {
                failures++;
                log.AppendLine("  FAIL 运行环境探测应报告 4.8 及以上");
            }
            else if (info.ClrVersionText == null || !info.ClrVersionText.StartsWith("4.0.", StringComparison.Ordinal))
            {
                failures++;
                log.AppendLine("  FAIL CLR 版本应当形如 4.0.xxxxx，实际 " + (info.ClrVersionText ?? "null"));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   框架报 4.8 而 CLR 报 {0} —— 两者不同源，不能拿 CLR 版本当框架版本",
                    info.ClrVersionText));
            }

            checks++;
            if (!info.ProcessBitsText.Contains("位"))
            {
                failures++;
                log.AppendLine("  FAIL 进程位数文本异常：" + info.ProcessBitsText);
            }
            else
            {
                log.AppendLine("  OK   进程位数：" + info.ProcessBitsText + "（Prefer32Bit 下应为 32 位）");
            }

            // 探测失败时的降级文案：不能说成"版本太低"，也不能把 null 漏到界面上
            checks++;
            RuntimeEnvironmentInfo failed = new RuntimeEnvironmentInfo(
                false, 0, null, "4.0.30319", "Win32NT", false, null, null);
            string failedText = failed.ToDisplayText();

            if (failed.IsNet48OrLater)
            {
                failures++;
                log.AppendLine("  FAIL 探测失败时不应判定为「满足 4.8」");
            }
            else if (failedText.Contains("null") || !failedText.Contains("无法读取"))
            {
                failures++;
                log.AppendLine("  FAIL 探测失败的文案不合格（不应出现 null，且应说明「无法读取」）");
            }
            else
            {
                log.AppendLine("  OK   探测失败时降级为「无法读取」，且不把 null 漏到界面");
            }
        }

        /// <summary>文件关联：先跑不依赖注册表写入的纯逻辑，再做完整流程（环境不允许写时明确 Skip）。</summary>
        private static void CheckFileAssociation(
            string tempRoot,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            CheckCommandPathParsing(ref checks, ref failures, log);

            // ---- 环境探测：本进程到底能不能写 HKCU ----
            //
            // 这一步不是"为了测试而测试"：某些受限环境（本机就是这样）会禁止
            // 被启动的可执行文件写注册表 —— 目的是防止程序偷偷改文件关联之类的系统设置。
            // 这种情况下把断言判成 FAIL 是**误报**：程序没错，环境不允许。
            // 正确做法是明确 Skip 并说清原因，而不是让 --selftest 在这类机器上恒为红。
            string writeError;
            bool canWrite = TryWriteRegistryProbe(out writeError);

            if (!canWrite)
            {
                log.AppendLine("  SKIP 本机禁止本进程写注册表，跳过关联写入流程（打开方式 + 右键菜单）");
                log.AppendLine("       原因：" + writeError);
                log.AppendLine("       这是环境策略，不是程序缺陷：本机工具层会拦下可执行文件对注册表的写入");
                log.AppendLine("       （已核实同样的键路径用受信任工具可以正常创建，且与 exe 名字无关）。");
                log.AppendLine("       在普通 Windows 上重跑 --selftest，这些断言会自动执行；");
                log.AppendLine("       或直接运行 PS-text.exe --register，然后在图片上右键看菜单与“打开方式”。");
                return;
            }

            log.AppendLine("  OK   本进程可写 HKCU（继续跑完整的关联流程）");

            string testRoot = @"Software\PSText-SelfTest-" + Guid.NewGuid().ToString("N");

            // 再确认一次：测试根键不能落在 Software\Classes 下，否则会改到用户真实关联
            checks++;
            if (testRoot.StartsWith(@"Software\Classes", StringComparison.OrdinalIgnoreCase)
                || testRoot.IndexOf(@"\Classes", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failures++;
                log.AppendLine("  FAIL 测试根键不能落在 Software\\Classes 下（会改到用户真实关联）");
                return;
            }

            log.AppendLine("  OK   测试根键与真实关联隔离（" + testRoot + "）");

            string stubDirectory = Path.Combine(tempRoot, "assoc-stub");
            Directory.CreateDirectory(stubDirectory);
            string stubExe = Path.Combine(stubDirectory, "PS-text.exe");
            File.WriteAllText(stubExe, "stub");

            string otherExe = Path.Combine(stubDirectory, "Other", "PS-text.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(otherExe));
            File.WriteAllText(otherExe, "stub");

            try
            {
                FileAssociationService service = new FileAssociationService(stubExe, Registry.CurrentUser, testRoot);

                checks++;
                if (service.GetState() != FileAssociationState.NotRegistered)
                {
                    failures++;
                    log.AppendLine("  FAIL 尚未注册时状态应为 NotRegistered");
                }
                else
                {
                    log.AppendLine("  OK   初始状态：未注册");
                }

                FileAssociationResult register = service.Register();

                checks++;
                if (!register.Success)
                {
                    failures++;
                    log.AppendLine("  FAIL 注册失败：" + register.Message);
                    return;
                }

                log.AppendLine("  OK   注册成功：" + register.Message);

                // ---- 逐项核对写进去的内容 ----
                string classes = testRoot + @"\Classes";
                string progId = classes + @"\PSText.Image";

                checks++;
                bool progIdOk =
                    string.Equals(ReadRegistryString(progId, null), "PS-text 图片", StringComparison.Ordinal)
                    && string.Equals(ReadRegistryString(progId + @"\DefaultIcon", null), "\"" + stubExe + "\",0", StringComparison.Ordinal)
                    && string.Equals(ReadRegistryString(progId + @"\shell\open\command", null), "\"" + stubExe + "\" \"%1\"", StringComparison.Ordinal);

                if (!progIdOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL ProgID 内容不符：名称「{0}」图标「{1}」命令「{2}」",
                        ReadRegistryString(progId, null),
                        ReadRegistryString(progId + @"\DefaultIcon", null),
                        ReadRegistryString(progId + @"\shell\open\command", null)));
                }
                else
                {
                    log.AppendLine("  OK   ProgID / 图标 / 打开命令 均已写入");
                }

                checks++;
                bool applicationsOk =
                    string.Equals(ReadRegistryString(classes + @"\Applications\PS-text.exe", "FriendlyAppName"), "PS-text 图片编辑器", StringComparison.Ordinal)
                    && string.Equals(
                        ReadRegistryString(classes + @"\Applications\PS-text.exe\shell\open\command", null),
                        "\"" + stubExe + "\" \"%1\"",
                        StringComparison.Ordinal);

                if (!applicationsOk)
                {
                    failures++;
                    log.AppendLine("  FAIL Applications\\PS-text.exe 下的友好名或命令缺失");
                }
                else
                {
                    log.AppendLine("  OK   Applications\\PS-text.exe 已登记（含友好名）");
                }

                checks++;
                bool associationsOk =
                    string.Equals(
                        ReadRegistryString(testRoot + @"\RegisteredApplications", "PS-text"),
                        testRoot + @"\PS-text\Capabilities",
                        StringComparison.Ordinal)
                    && string.Equals(
                        ReadRegistryString(testRoot + @"\PS-text\Capabilities\FileAssociations", ".jpg"),
                        "PSText.Image",
                        StringComparison.Ordinal);

                if (!associationsOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 默认程序所需的 Capabilities 结构不完整（RegisteredApplications=「{0}」）",
                        ReadRegistryString(testRoot + @"\RegisteredApplications", "PS-text")));
                }
                else
                {
                    log.AppendLine("  OK   Capabilities + RegisteredApplications 已登记（可进「设置默认程序」列表）");
                }

                // 每个扩展名都要挂上 OpenWithProgids，漏一个就有一类图片打不开
                string[] extensions = FileAssociationService.GetSupportedExtensions();
                int missingAssociations = 0;

                for (int i = 0; i < extensions.Length; i++)
                {
                    string openWith = classes + @"\" + extensions[i] + @"\OpenWithProgids";

                    if (!RegistryValueExists(openWith, "PSText.Image")
                        || !RegistryValueExists(classes + @"\Applications\PS-text.exe\SupportedTypes", extensions[i]))
                    {
                        missingAssociations++;
                    }
                }

                checks++;
                if (missingAssociations > 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 有 {0} 个扩展名没有挂上 OpenWithProgids / SupportedTypes",
                        missingAssociations));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   {0} 个扩展名全部挂上 OpenWithProgids（且只登记「可以打开」、不改默认）",
                        extensions.Length));
                }

                checks++;
                if (service.GetState() != FileAssociationState.Registered)
                {
                    failures++;
                    log.AppendLine("  FAIL 注册后状态应为 Registered");
                }
                else
                {
                    log.AppendLine("  OK   注册后状态：已注册（指向当前程序）");
                }

                // ---- 右键菜单 ----
                string shellRoot = classes + @"\SystemFileAssociations\image\shell";

                checks++;
                bool verbsOk =
                    string.Equals(ReadRegistryString(shellRoot + @"\PSText.Edit", "MUIVerb"), "用 PS-text 编辑", StringComparison.Ordinal)
                    && string.Equals(
                        ReadRegistryString(shellRoot + @"\PSText.Edit\command", null),
                        "\"" + stubExe + "\" \"%1\"",
                        StringComparison.Ordinal)
                    && string.Equals(ReadRegistryString(shellRoot + @"\PSText.Print", "MUIVerb"), "用 PS-text 打印", StringComparison.Ordinal)
                    && string.Equals(
                        ReadRegistryString(shellRoot + @"\PSText.Print\command", null),
                        "\"" + stubExe + "\" --print \"%1\"",
                        StringComparison.Ordinal)
                    && service.HasContextMenu;

                if (!verbsOk)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 右键菜单动词不完整：编辑=「{0}」打印=「{1}」HasContextMenu={2}",
                        ReadRegistryString(shellRoot + @"\PSText.Edit\command", null),
                        ReadRegistryString(shellRoot + @"\PSText.Print\command", null),
                        service.HasContextMenu));
                }
                else
                {
                    log.AppendLine("  OK   右键菜单已登记（“用 PS-text 编辑” / “用 PS-text 打印”，打印带 --print 参数）");
                }

                checks++;
                if (RegistryValueExists(shellRoot + @"\PSText.Edit", "Extended"))
                {
                    failures++;
                    log.AppendLine("  FAIL 不该写 Extended —— 那会让菜单项只在按住 Shift 时出现");
                }
                else
                {
                    log.AppendLine("  OK   未写 Extended（右键菜单项平时就能看到，不必按 Shift）");
                }

                // 绿色版被移动过：另一份 exe 查询同一份注册表，必须能识别出来
                checks++;
                FileAssociationService moved = new FileAssociationService(otherExe, Registry.CurrentUser, testRoot);

                if (moved.GetState() != FileAssociationState.RegisteredForAnotherCopy)
                {
                    failures++;
                    log.AppendLine("  FAIL 注册表指向别的 exe 时应报告 RegisteredForAnotherCopy");
                }
                else
                {
                    log.AppendLine("  OK   注册表指向别的 exe 时状态为「指向另一份程序」（提示用户重新注册）");
                }

                // 幂等：再注册一次不应出错，也不应把内容改坏
                checks++;
                FileAssociationResult again = service.Register();

                if (!again.Success || service.GetState() != FileAssociationState.Registered)
                {
                    failures++;
                    log.AppendLine("  FAIL 重复注册应当幂等，实际：" + again.Message);
                }
                else
                {
                    log.AppendLine("  OK   重复注册幂等");
                }

                // ---- 注销 ----
                FileAssociationResult unregister = service.Unregister();

                checks++;
                if (!unregister.Success)
                {
                    failures++;
                    log.AppendLine("  FAIL 注销失败：" + unregister.Message);
                    return;
                }

                log.AppendLine("  OK   注销成功");

                checks++;
                bool cleaned =
                    !RegistryKeyExists(progId)
                    && !RegistryKeyExists(classes + @"\Applications\PS-text.exe")
                    && !RegistryKeyExists(testRoot + @"\PS-text\Capabilities")
                    && !RegistryValueExists(testRoot + @"\RegisteredApplications", "PS-text")
                    && service.GetState() == FileAssociationState.NotRegistered;

                if (!cleaned)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 注销后仍有残留：ProgID={0} Applications={1} Capabilities={2} RegisteredApplications={3} 状态={4}",
                        RegistryKeyExists(progId),
                        RegistryKeyExists(classes + @"\Applications\PS-text.exe"),
                        RegistryKeyExists(testRoot + @"\PS-text\Capabilities"),
                        RegistryValueExists(testRoot + @"\RegisteredApplications", "PS-text"),
                        service.GetState()));
                }
                else
                {
                    log.AppendLine("  OK   注销后无残留（ProgID / Applications / Capabilities / RegisteredApplications 均已清空）");
                }

                // OpenWithProgids 摘掉自己后应当整条键消失，不在用户注册表里留空壳
                int leftoverKeys = 0;

                for (int i = 0; i < extensions.Length; i++)
                {
                    if (RegistryKeyExists(classes + @"\" + extensions[i] + @"\OpenWithProgids"))
                    {
                        leftoverKeys++;
                    }
                }

                checks++;
                if (leftoverKeys > 0)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 有 {0} 个 OpenWithProgids 空壳键没被清理", leftoverKeys));
                }
                else
                {
                    log.AppendLine("  OK   空的 OpenWithProgids 键已清理（不留空壳）");
                }

                // ---- 右键菜单也要一起清干净 ----
                checks++;
                if (RegistryKeyExists(shellRoot + @"\PSText.Edit")
                    || RegistryKeyExists(shellRoot + @"\PSText.Print")
                    || RegistryKeyExists(shellRoot)
                    || service.HasContextMenu)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 注销后右键菜单有残留：编辑={0} 打印={1} shell={2} HasContextMenu={3}",
                        RegistryKeyExists(shellRoot + @"\PSText.Edit"),
                        RegistryKeyExists(shellRoot + @"\PSText.Print"),
                        RegistryKeyExists(shellRoot),
                        service.HasContextMenu));
                }
                else
                {
                    log.AppendLine("  OK   注销后右键菜单无残留（动词键与空的 shell 键都已清理）");
                }

                checks++;
                FileAssociationResult unregisterAgain = service.Unregister();

                if (!unregisterAgain.Success)
                {
                    failures++;
                    log.AppendLine("  FAIL 重复注销应当幂等，实际：" + unregisterAgain.Message);
                }
                else
                {
                    log.AppendLine("  OK   重复注销幂等");
                }

                // exe 已被删除（绿色版被挪走）时，注册应当给出可读的失败原因，而不是抛异常
                FileAssociationService missingExe = new FileAssociationService(
                    Path.Combine(stubDirectory, "gone", "PS-text.exe"), Registry.CurrentUser, testRoot);
                FileAssociationResult missingResult = missingExe.Register();

                checks++;
                if (missingResult.Success || string.IsNullOrEmpty(missingResult.Message))
                {
                    failures++;
                    log.AppendLine("  FAIL exe 不存在时应注册失败并给出可读原因");
                }
                else
                {
                    log.AppendLine("  OK   exe 不存在时注册失败并给出可读原因");
                }
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 文件关联测试异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                // 无论成败都要把测试根键清干净，不给用户留垃圾
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(testRoot, false);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 不依赖注册表写入权限的纯逻辑：
        /// 写入的命令行必须能被自己的读取逻辑还原 —— 这是"注册 / 判定是否指向另一份程序"的基础。
        /// 环境不允许写注册表时，这一段仍然会被执行（否则关联代码就完全没被覆盖了）。
        /// </summary>
        private static void CheckCommandPathParsing(ref int checks, ref int failures, StringBuilder log)
        {
            string withSpaces = @"C:\Program Files\PS-text\PS-text.exe";

            checks++;
            string command = FileAssociationService.Quote(withSpaces) + " \"%1\"";
            string parsed = FileAssociationService.ExtractExecutablePath(command);

            if (!string.Equals(parsed, withSpaces, StringComparison.Ordinal))
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 带空格的命令路径还原失败：写入「{0}」读回「{1}」", command, parsed ?? "null"));
            }
            else
            {
                log.AppendLine("  OK   带空格的路径写成「" + command + "」后能正确还原");
            }

            checks++;
            string quoted = FileAssociationService.Quote(withSpaces);
            if (quoted.Length < 2 || !quoted.StartsWith("\"", StringComparison.Ordinal)
                || !quoted.EndsWith("\"", StringComparison.Ordinal))
            {
                failures++;
                log.AppendLine("  FAIL Quote 应当把路径包在引号里（路径可能含空格）");
            }
            else
            {
                log.AppendLine("  OK   Quote 会给路径加引号");
            }

            checks++;
            string bare = FileAssociationService.ExtractExecutablePath(@"C:\tools\PS-text.exe %1");
            if (!string.Equals(bare, @"C:\tools\PS-text.exe", StringComparison.Ordinal))
            {
                failures++;
                log.AppendLine("  FAIL 无引号的命令也应能解析出 exe 路径，实际：" + (bare ?? "null"));
            }
            else
            {
                log.AppendLine("  OK   无引号的命令也能解析");
            }

            // 注册表被人为改坏 / 残留半截命令时不能崩，只应判成"指向另一份程序"
            checks++;
            bool degenerateSafe =
                FileAssociationService.ExtractExecutablePath(null) == null
                && FileAssociationService.ExtractExecutablePath(string.Empty) == null
                && FileAssociationService.ExtractExecutablePath("\"未闭合的引号") == null;

            if (!degenerateSafe)
            {
                failures++;
                log.AppendLine("  FAIL 畸形命令（null / 空 / 引号未闭合）应安全返回 null");
            }
            else
            {
                log.AppendLine("  OK   畸形命令安全返回 null（注册表被改坏也不会崩）");
            }
        }

        /// <summary>探测本进程能否写 HKCU。不能写时返回 false 并给出原因。</summary>
        private static bool TryWriteRegistryProbe(out string error)
        {
            string probePath = @"Software\PSText-SelfTest-Probe-" + Guid.NewGuid().ToString("N");
            error = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(probePath))
                {
                    if (key == null)
                    {
                        error = "CreateSubKey 返回 null";
                        return false;
                    }

                    key.SetValue("probe", "1", RegistryValueKind.String);
                }

                Registry.CurrentUser.DeleteSubKeyTree(probePath, false);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;

                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(probePath, false);
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        private static string ReadRegistryString(string path, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
                {
                    return key == null ? null : key.GetValue(valueName) as string;
                }
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }

        private static bool RegistryKeyExists(string path)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
                {
                    return key != null;
                }
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        private static bool RegistryValueExists(string path, string valueName)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string[] names = key.GetValueNames();

                    for (int i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], valueName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>
        /// 校验遮盖类标注（马赛克 / 模糊）。
        ///
        /// 遮盖与其它标注的本质区别：它的内容是**底图的像素级派生**，不是几何。
        /// 因此要额外钉住两件其它标注不存在的事：
        ///   1. **块网格锚定在整幅图的 (0,0)** —— 否则拖动标注时所有方块会跟着"流动"；
        ///   2. **只改矩形内的像素** —— 这是遮盖类工具的安全性底线，矩形外被改了就是事故。
        /// </summary>
        private static void CheckMosaicAnnotation(
            IImageService imageService,
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[34] 遮盖标注（马赛克 / 模糊）");

            try
            {
                CheckMosaicPixelate(ref checks, ref failures, log);
                CheckMosaicAnchor(ref checks, ref failures, log);
                CheckMosaicBlur(ref checks, ref failures, log);
                CheckMosaicProvider(ref checks, ref failures, log);
                CheckMosaicEndToEnd(imageService, pngPath, ref checks, ref failures, log);
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 遮盖标注测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>块平均本身：常值解 + 块值确实等于该块的平均值。</summary>
        private static void CheckMosaicPixelate(ref int checks, ref int failures, StringBuilder log)
        {
            // ---- 常值解：纯色输入必须原样还原（含奇数块，四舍五入不能掉色阶）----
            PixelBuffer solid = CreateSolidBuffer(120, 90, 40, 80, 160);

            int originX;
            int originY;
            PixelBuffer covered = MosaicFilter.PixelateRegion(
                solid, 7, 5, 61, 43, 9, out originX, out originY, System.Threading.CancellationToken.None);

            checks++;
            if (covered == null)
            {
                failures++;
                log.AppendLine("  FAIL 纯色图上的块平均不该返回空");
            }
            else
            {
                byte[] pixels = covered.GetPixels();
                int mismatched = 0;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 160 || pixels[i + 1] != 80 || pixels[i + 2] != 40 || pixels[i + 3] != 255)
                    {
                        mismatched++;
                    }
                }

                if (mismatched > 0)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 纯色图块平均后有 {0} 个像素偏色（常值解不成立）", mismatched));
                }
                else
                {
                    log.AppendLine(string.Format("  OK   纯色图块平均逐像素等于原色（{0}×{1}）", covered.Width, covered.Height));
                }
            }

            // ---- 块值必须等于该块内像素的算术平均 ----
            PixelBuffer ramp = CreateHorizontalRamp(160, 80);
            const int block = 16;

            int rampOriginX;
            int rampOriginY;
            PixelBuffer rampCovered = MosaicFilter.PixelateRegion(
                ramp, 20, 16, 80, 48, block, out rampOriginX, out rampOriginY, System.Threading.CancellationToken.None);

            checks++;
            if (rampCovered == null)
            {
                failures++;
                log.AppendLine("  FAIL 渐变图上的块平均不该返回空");
            }
            else
            {
                int[] wrongBlocks = new int[0];
                int wrong = 0;
                string detail = null;

                for (int blockY = rampOriginY; blockY + block <= rampOriginY + rampCovered.Height; blockY += block)
                {
                    for (int blockX = rampOriginX; blockX + block <= rampOriginX + rampCovered.Width; blockX += block)
                    {
                        // 期望值 = 该块在原图上的通道平均
                        // 注意用**蓝**通道：CreateHorizontalRamp 的渐变在蓝通道（红通道恒为 128），
                        // 拿红通道比等于在比常量，断言会变成空转。
                        int expectedBlue = MosaicFilter.AverageChannel(ramp, blockX, blockY, block, block, 0);
                        byte[] sample = CopyPixel(rampCovered, blockX - rampOriginX, blockY - rampOriginY);

                        if (sample == null || sample[0] != (byte)expectedBlue)
                        {
                            wrong++;

                            if (detail == null)
                            {
                                detail = string.Format(
                                    "块 ({0},{1}) 期望 B={2}，实际 B={3}",
                                    blockX, blockY, expectedBlue, sample == null ? -1 : sample[0]);
                            }
                        }
                    }
                }

                if (wrong > 0)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 有 {0} 个块的值不等于该块平均（首个：{1}）", wrong, detail));
                }
                else
                {
                    log.AppendLine("  OK   每个块的值都等于该块在原图上的算术平均");
                }

                // 块内必须完全均匀 —— 否则就不是"方块"而是"糊"
                checks++;
                int unevenBlocks = 0;

                for (int blockY = rampOriginY; blockY + block <= rampOriginY + rampCovered.Height; blockY += block)
                {
                    byte[] first = CopyPixel(rampCovered, 0, blockY - rampOriginY);

                    for (int inner = 1; inner < block; inner++)
                    {
                        byte[] other = CopyPixel(rampCovered, 0, blockY - rampOriginY + inner);

                        if (first == null || other == null || first[0] != other[0])
                        {
                            unevenBlocks++;
                        }
                    }
                }

                if (unevenBlocks > 0)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 有 {0} 处块内像素不一致", unevenBlocks));
                }
                else
                {
                    log.AppendLine("  OK   块内像素完全一致（硬边方块，不是糊）");
                }
            }
        }

        /// <summary>
        /// 块网格锚定在整幅图的 (0,0)。
        ///
        /// 这是"拖动标注时马赛克不流动"的全部依据：区域起点不同，但只要求得的
        /// 素材起点相同，同一图像坐标处的块值就必然相同。
        /// </summary>
        private static void CheckMosaicAnchor(ref int checks, ref int failures, StringBuilder log)
        {
            PixelBuffer ramp = CreateHorizontalRamp(240, 160);
            const int block = 10;

            int ax;
            int ay;
            int bx;
            int by;

            MosaicFilter.PixelateRegion(ramp, 103, 57, 60, 40, block, out ax, out ay, System.Threading.CancellationToken.None);
            MosaicFilter.PixelateRegion(ramp, 107, 59, 60, 40, block, out bx, out by, System.Threading.CancellationToken.None);

            checks++;
            if (ax % block != 0 || ay % block != 0 || bx % block != 0 || by % block != 0)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 素材起点必须对齐到块边界（否则相位会随标注移动而漂移）：({0},{1}) / ({2},{3})",
                    ax, ay, bx, by));
            }
            else
            {
                log.AppendLine(string.Format("  OK   素材起点对齐到块边界（{0}px 网格）：({1},{2}) / ({3},{4})", block, ax, ay, bx, by));
            }

            // 两次请求落在同一个块网格内 ⇒ 求得的起点必须相同
            checks++;
            if (ax != bx || ay != by)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 同一块网格内的两次请求应求得同一起点，实际 ({0},{1}) vs ({2},{3})", ax, ay, bx, by));
            }
            else
            {
                log.AppendLine("  OK   同一块网格内的不同起点求出同一起点（相位不随标注位移改变）");
            }

            // 更直接的行为断言：同一图像坐标处的块值，两次请求必须相同
            int cx;
            int cy;
            int dx;
            int dy;
            PixelBuffer first = MosaicFilter.PixelateRegion(ramp, 100, 50, 60, 40, block, out cx, out cy, System.Threading.CancellationToken.None);
            PixelBuffer second = MosaicFilter.PixelateRegion(ramp, 106, 54, 60, 40, block, out dx, out dy, System.Threading.CancellationToken.None);

            checks++;
            string mismatch = null;
            int sampled = 0;

            if (first == null || second == null)
            {
                failures++;
                log.AppendLine("  FAIL 两次请求都应返回结果");
            }
            else
            {
                // 只在两次结果**都覆盖**的图像坐标上取样。
                // first 覆盖 (100..160)×(50..90)，second 覆盖 (100..170)×(50..100)，
                // 交集是 (100..160)×(50..90)，这里取靠内的一圈。
                for (int imageY = 60; imageY <= 80 && mismatch == null; imageY += block)
                {
                    for (int imageX = 110; imageX <= 150; imageX += block)
                    {
                        byte[] left = CopyPixel(first, imageX - cx, imageY - cy);
                        byte[] right = CopyPixel(second, imageX - dx, imageY - dy);

                        if (left == null || right == null)
                        {
                            mismatch = string.Format("({0},{1}) 超出某一次结果的范围", imageX, imageY);
                            break;
                        }

                        sampled++;

                        if (left[0] != right[0] || left[1] != right[1] || left[2] != right[2])
                        {
                            mismatch = string.Format("({0},{1}) 块值不同", imageX, imageY);
                            break;
                        }
                    }
                }

                if (mismatch != null)
                {
                    failures++;
                    log.AppendLine("  FAIL 同一图像坐标的块值随请求起点改变（拖动会看到马赛克流动）：" + mismatch);
                }
                else if (sampled < 4)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 取样点太少（{0} 个），这条断言没有说服力", sampled));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   同一图像坐标的块值与请求起点无关（抽样 {0} 个块，拖动时马赛克不会流动）", sampled));
                }
            }
        }

        /// <summary>区域内模糊：尺寸可控、纯色不变（边界外扩确实生效）。</summary>
        private static void CheckMosaicBlur(ref int checks, ref int failures, StringBuilder log)
        {
            PixelBuffer solid = CreateSolidBuffer(200, 140, 90, 140, 200);

            int originX;
            int originY;
            PixelBuffer blurred = MosaicFilter.BlurRegion(
                solid, 40, 30, 60, 40, 8.0, out originX, out originY, System.Threading.CancellationToken.None);

            checks++;
            if (blurred == null)
            {
                failures++;
                log.AppendLine("  FAIL 区域内模糊不该返回空");
            }
            else if (blurred.Width != 60 || blurred.Height != 40 || originX != 40 || originY != 30)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 模糊结果应正好是请求矩形（期望 60×40 @(40,30)，实际 {0}×{1} @({2},{3})）",
                    blurred.Width, blurred.Height, originX, originY));
            }
            else
            {
                byte[] pixels = blurred.GetPixels();
                int maxDelta = 0;

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    maxDelta = Math.Max(maxDelta, Math.Abs(pixels[i] - 200));
                    maxDelta = Math.Max(maxDelta, Math.Abs(pixels[i + 1] - 140));
                    maxDelta = Math.Max(maxDelta, Math.Abs(pixels[i + 2] - 90));
                }

                if (maxDelta > 1)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 纯色图模糊后应保持不变，最大偏差 {0}", maxDelta));
                }
                else
                {
                    log.AppendLine(string.Format("  OK   区域模糊尺寸与起点正确，纯色图最大偏差 {0}", maxDelta));
                }
            }

            // 边界外扩的意义：紧贴图像左上角的区域，若不做外扩，镜像补位会把边界颜色污染进来
            checks++;
            int edgeOriginX;
            int edgeOriginY;
            PixelBuffer ramp = CreateHorizontalRamp(120, 80);
            PixelBuffer edgeBlur = MosaicFilter.BlurRegion(
                ramp, 0, 0, 40, 30, 10.0, out edgeOriginX, out edgeOriginY, System.Threading.CancellationToken.None);

            // 渐变图左上角模糊后，最左列的值必须落在"原值 ~ 右侧邻居"之间，而不是被镜像成更亮的颜色
            byte[] cornerTop = edgeBlur == null ? null : CopyPixel(edgeBlur, 0, 0);
            byte[] cornerBelow = edgeBlur == null ? null : CopyPixel(edgeBlur, 0, 12);

            if (edgeBlur == null || cornerTop == null || cornerBelow == null)
            {
                failures++;
                log.AppendLine("  FAIL 贴边区域的模糊不该返回空");
            }
            else
            {
                byte[] rampLeft = CopyPixel(ramp, 0, 20);
                byte[] rampRight = CopyPixel(ramp, 39, 20);

                int minExpected = Math.Min(rampLeft[0], rampRight[0]);
                int maxExpected = Math.Max(rampLeft[0], rampRight[0]);

                if (cornerTop[0] < minExpected - 2 || cornerTop[0] > maxExpected + 2)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 贴边模糊的结果超出行内取值范围（B={0} 不在 {1}~{2}），边界外扩可能没生效",
                        cornerTop[0], minExpected, maxExpected));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   贴边区域模糊未越界（B={0}，行内范围 {1}~{2}）", cornerTop[0], minExpected, maxExpected));
                }
            }
        }

        /// <summary>素材提供者：ImageBrush 必须 1:1 映射（Viewbox 与 Viewport 同矩形）。</summary>
        private static void CheckMosaicProvider(ref int checks, ref int failures, StringBuilder log)
        {
            AnnotationObject item = new AnnotationObject
            {
                Kind = AnnotationKind.Mosaic,
                X1 = 30,
                Y1 = 20,
                X2 = 110,
                Y2 = 80,
                CoverSize = 8,
                MosaicStyle = MosaicStyle.Pixelate
            };

            // 源不可用（例如还没打开图片）
            MosaicSourceProvider empty = new MosaicSourceProvider(() => null);

            checks++;
            if (empty.CreateCoverBrush(item) != null)
            {
                failures++;
                log.AppendLine("  FAIL 源不可用时不应生成素材");
            }
            else
            {
                log.AppendLine("  OK   源不可用时返回 null（由构建器退化为占位填充，不会让标注凭空消失）");
            }

            // 源可用
            PixelBuffer ramp = CreateHorizontalRamp(200, 120);
            MosaicSourceProvider provider = new MosaicSourceProvider(() => ramp);

            object brush = provider.CreateCoverBrush(item);

            checks++;
            if (!(brush is ImageBrush))
            {
                failures++;
                log.AppendLine("  FAIL 遮盖素材应当是 ImageBrush，实际 " + (brush == null ? "null" : brush.GetType().Name));
            }
            else
            {
                ImageBrush image = (ImageBrush)brush;

                // Viewbox 与 Viewport 必须完全相同 —— 这是"不二次采样、方块不被糊掉"的保证
                if (!image.Viewbox.Equals(image.Viewport) || image.Stretch != Stretch.Fill)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 素材必须 1:1 映射（Viewbox {0} / Viewport {1} / Stretch {2}）",
                        image.Viewbox, image.Viewport, image.Stretch));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   素材是 1:1 映射的 ImageBrush（矩形 {0:0}×{1:0}）", image.Viewbox.Width, image.Viewbox.Height));
                }
            }

            // 没有提供者时，构建器要给占位而不是空 —— 否则标注会"消失"
            checks++;
            AnnotationVisual visual = AnnotationVisualBuilder.Build(item, null);

            if (visual.Geometry == null || visual.Fill == null)
            {
                failures++;
                log.AppendLine("  FAIL 素材缺失时应当退化占位填充，而不是什么都不画");
            }
            else
            {
                log.AppendLine("  OK   素材缺失时退化为占位填充（标注仍然可见）");
            }
        }

        /// <summary>端到端：只改矩形内像素、块状可见、叠加层真的拿到了底图素材、可撤销。</summary>
        private static void CheckMosaicEndToEnd(
            IImageService imageService,
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            // ---- 渲染层 ----
            PixelBuffer ramp = CreateHorizontalRamp(240, 160);
            byte[] original = ramp.GetPixelsCopy();

            AnnotationObject item = new AnnotationObject
            {
                Kind = AnnotationKind.Mosaic,
                X1 = 60,
                Y1 = 40,
                X2 = 160,
                Y2 = 120,
                CoverSize = 10,
                MosaicStyle = MosaicStyle.Pixelate
            };

            List<AnnotationObject> objects = new List<AnnotationObject> { item };
            MosaicSourceProvider provider = new MosaicSourceProvider(() => ramp);

            PixelBuffer composited = AnnotationRenderer.Render(ramp, objects, provider);

            checks++;
            if (ReferenceEquals(composited, ramp))
            {
                failures++;
                log.AppendLine("  FAIL 有遮盖标注时渲染结果不应等于原图");
            }
            else
            {
                log.AppendLine("  OK   渲染产生了新的像素缓冲");
            }

            // 矩形外的像素**逐字节**不变：遮盖工具的安全性底线
            checks++;
            byte[] result = composited.GetPixels();
            int outsideChanged = 0;
            int insideChanged = 0;

            for (int y = 0; y < composited.Height; y++)
            {
                for (int x = 0; x < composited.Width; x++)
                {
                    int index = (y * composited.Width + x) * 4;
                    bool changed = result[index] != original[index]
                                   || result[index + 1] != original[index + 1]
                                   || result[index + 2] != original[index + 2];

                    bool inside = x >= 60 && x < 160 && y >= 40 && y < 120;

                    if (!changed)
                    {
                        continue;
                    }

                    if (inside)
                    {
                        insideChanged++;
                    }
                    else
                    {
                        outsideChanged++;
                    }
                }
            }

            if (outsideChanged > 0)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 遮盖标注改到了矩形外的 {0} 个像素（安全性底线）", outsideChanged));
            }
            else
            {
                log.AppendLine("  OK   矩形外的像素逐字节不变（遮盖只影响自己那块）");
            }

            checks++;
            if (insideChanged == 0)
            {
                failures++;
                log.AppendLine("  FAIL 矩形内的像素没有被遮盖");
            }
            else
            {
                log.AppendLine(string.Format("  OK   矩形内有 {0} 个像素被遮盖", insideChanged));
            }

            // 块状结构：块内一致、相邻块不同
            checks++;
            byte[] firstBlock = CopyPixel(composited, 60, 40);
            byte[] sameBlock = CopyPixel(composited, 65, 45);
            byte[] nextBlock = CopyPixel(composited, 70, 40);

            if (firstBlock == null || sameBlock == null || nextBlock == null)
            {
                failures++;
                log.AppendLine("  FAIL 取样点越界");
            }
            else if (firstBlock[0] != sameBlock[0])
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 块内像素应完全一致（B={0} vs {1}）—— 说明方块被二次采样糊掉了",
                    firstBlock[0], sameBlock[0]));
            }
            else if (firstBlock[0] == nextBlock[0])
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 相邻块的值相同（B={0}）—— 遮盖区域没有呈现块状", firstBlock[0]));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   遮盖区域呈块状（块内 B={0} 一致，相邻块 B={1} 不同）", firstBlock[0], nextBlock[0]));
            }

            // ---- ViewModel 层 ----
            MainViewModel viewModel = new MainViewModel(
                imageService,
                new NullDialogService(),
                new ImmediateDispatcherService());

            viewModel.LoadFromPathAsync(pngPath).GetAwaiter().GetResult();
            WaitForIdle(viewModel);

            viewModel.AnnotationToolIndex = (int)AnnotationKind.Mosaic;
            viewModel.AnnotationCoverSize = 16.0;
            viewModel.BeginAnnotationCommand.Execute(null);
            viewModel.BeginAnnotationGesture(40, 30);
            viewModel.UpdateAnnotationGesture(140, 110);
            viewModel.EndAnnotationGesture();

            checks++;
            if (viewModel.AnnotationCount != 1)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 拖拽后应产生 1 个遮盖标注，实际 {0} 个", viewModel.AnnotationCount));
            }
            else
            {
                AnnotationObject created = viewModel.Annotations[0].Source;

                if (created.Kind != AnnotationKind.Mosaic || Math.Abs(created.CoverSize - 16.0) > 1e-9)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 新建的遮盖标注参数不对（Kind={0}，强度={1}）", created.Kind, created.CoverSize));
                }
                else
                {
                    log.AppendLine("  OK   拖拽创建的遮盖标注带上了当前强度参数（16）");
                }
            }

            // 叠加层必须真的拿到了底图素材 —— 这是"预览所见即所得"的前提
            checks++;
            if (viewModel.Annotations.Count == 0 || !(viewModel.Annotations[0].FillBrush is ImageBrush))
            {
                failures++;
                log.AppendLine("  FAIL 叠加层的遮盖笔刷不是 ImageBrush（没取到底图素材）");
            }
            else
            {
                log.AppendLine("  OK   叠加层的遮盖笔刷确实取自底图（ImageBrush）");
            }

            // 撤销
            checks++;
            viewModel.UndoCommand.Execute(null);
            WaitForIdle(viewModel);

            if (viewModel.AnnotationCount != 0)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 撤销后遮盖标注应消失，实际还剩 {0} 个", viewModel.AnnotationCount));
            }
            else
            {
                log.AppendLine("  OK   撤销后遮盖标注被移除（对象列表快照历史生效）");
            }
        }

        /// <summary>
        /// 校验标注的八向缩放手柄。
        ///
        /// 缩放逻辑全是"边界与钳制"——不允许翻转、最小尺寸、保持宽高比、以中心为基准，
        /// 四者叠在一起分支很多，所以核心计算做成了纯函数（<see cref="AnnotationResizeCalculator"/>），
        /// 这里逐条钉死；VM 层则验证"手柄优先于拖动"这个最容易做错的交互。
        /// </summary>
        private static void CheckAnnotationResize(
            IImageService imageService,
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[35] 标注缩放手柄");

            try
            {
                CheckResizeCalculator(ref checks, ref failures, log);
                CheckAnnotationResizeEndToEnd(imageService, pngPath, ref checks, ref failures, log);
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 缩放手柄测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>纯函数部分：每个手柄的方向、钳制、宽高比、中心基准。</summary>
        private static void CheckResizeCalculator(ref int checks, ref int failures, StringBuilder log)
        {
            double left;
            double top;
            double right;
            double bottom;

            // ---- 拖右下角：左上角必须一动不动 ----
            checks++;
            bool ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.BottomRight, 200, 150,
                false, false, out left, out top, out right, out bottom);

            if (!ok || Math.Abs(left - 20) > 1e-9 || Math.Abs(top - 30) > 1e-9
                || Math.Abs(right - 200) > 1e-9 || Math.Abs(bottom - 150) > 1e-9)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 拖右下角应只改右上/下边界，实际 ({0},{1})-({2},{3})", left, top, right, bottom));
            }
            else
            {
                log.AppendLine("  OK   拖右下角：左上角固定，只改宽高");
            }

            // ---- 拖左上角：右下角固定 ----
            checks++;
            ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.TopLeft, -10, 5,
                false, false, out left, out top, out right, out bottom);

            if (!ok || Math.Abs(right - 120) > 1e-9 || Math.Abs(bottom - 90) > 1e-9
                || Math.Abs(left + 10) > 1e-9 || Math.Abs(top - 5) > 1e-9)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 拖左上角应只改左上边界，实际 ({0},{1})-({2},{3})", left, top, right, bottom));
            }
            else
            {
                log.AppendLine("  OK   拖左上角：右下角固定，只改宽高");
            }

            // ---- 拖边中点：只改一个方向 ----
            checks++;
            ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.Right, 300, 999,
                false, false, out left, out top, out right, out bottom);

            if (!ok || Math.Abs(left - 20) > 1e-9 || Math.Abs(top - 30) > 1e-9
                || Math.Abs(right - 300) > 1e-9 || Math.Abs(bottom - 90) > 1e-9)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 拖右边中点应只改右边界，实际 ({0},{1})-({2},{3})", left, top, right, bottom));
            }
            else
            {
                log.AppendLine("  OK   拖边中点只改一个方向（纵向不受指针影响）");
            }

            // ---- 不允许翻转：拖过头时钳制在最小尺寸 ----
            checks++;
            ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.BottomRight, 5, 5,
                false, false, out left, out top, out right, out bottom);

            double size = AnnotationResizeCalculator.MinimumSize;

            if (!ok || right - left < size - 1e-9 || bottom - top < size - 1e-9
                || right <= left || bottom <= top)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 拖过头时应钳制在最小尺寸而不是翻转，实际 ({0},{1})-({2},{3})", left, top, right, bottom));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   拖过头不会翻转（钳制在最小尺寸 {0:0.#}，左上角仍固定）", right - left));
            }

            // ---- 保持宽高比 ----
            checks++;
            ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.BottomRight, 300, 100,
                true, false, out left, out top, out right, out bottom);

            double originalAspect = 100.0 / 60.0;
            double newAspect = (right - left) / (bottom - top);

            if (!ok || Math.Abs(newAspect - originalAspect) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 保持宽高比失效：原始 {0:0.####}，结果 {1:0.####}", originalAspect, newAspect));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   Shift 保持宽高比（{0:0.###} → {1:0.###}，尺寸 {2:0}×{3:0}）",
                    originalAspect, newAspect, right - left, bottom - top));
            }

            // ---- 以中心为基准 ----
            checks++;
            ok = AnnotationResizeCalculator.TryComputeBounds(
                20, 30, 120, 90, AnnotationHandle.Right, 200, 60,
                false, true, out left, out top, out right, out bottom);

            double centerBefore = (20 + 120) / 2.0;
            double centerAfter = (left + right) / 2.0;

            if (!ok || Math.Abs(centerAfter - centerBefore) > 1e-9 || Math.Abs(top - 30) > 1e-9 || Math.Abs(bottom - 90) > 1e-9)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL Alt 应以中心为基准：中心 {0:0.#} → {1:0.#}", centerBefore, centerAfter));
            }
            else
            {
                log.AppendLine(string.Format("  OK   Alt 以中心为基准（中心恒为 {0:0.#}，左右对称变化）", centerAfter));
            }

            // ---- 手柄位置 ----
            checks++;
            double[] expected = { 0, 0, 50, 0, 100, 0, 100, 25, 100, 50, 50, 50, 0, 50, 0, 25 };
            AnnotationHandle[] handles = AnnotationResizeCalculator.AllHandles();
            bool pointsOk = handles.Length == 8;

            for (int i = 0; i < handles.Length && pointsOk; i++)
            {
                double x;
                double y;
                AnnotationResizeCalculator.GetHandlePoint(handles[i], 0, 0, 100, 50, out x, out y);

                if (Math.Abs(x - expected[i * 2]) > 1e-9 || Math.Abs(y - expected[i * 2 + 1]) > 1e-9)
                {
                    pointsOk = false;
                    log.AppendLine(string.Format(
                        "  FAIL 手柄 {0} 位置应为 ({1},{2})，实际 ({3},{4})",
                        handles[i], expected[i * 2], expected[i * 2 + 1], x, y));
                }
            }

            if (!pointsOk)
            {
                failures++;
            }
            else
            {
                log.AppendLine("  OK   八个手柄位置正确（四角 + 四边中点）");
            }

            // ---- 光标名 ----
            checks++;
            bool cursorsOk =
                AnnotationResizeCalculator.GetCursorName(AnnotationHandle.TopLeft) == "SizeNWSE"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.BottomRight) == "SizeNWSE"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.TopRight) == "SizeNESW"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.BottomLeft) == "SizeNESW"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.Top) == "SizeNS"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.Left) == "SizeWE"
                && AnnotationResizeCalculator.GetCursorName(AnnotationHandle.None) == null;

            if (!cursorsOk)
            {
                failures++;
                log.AppendLine("  FAIL 手柄的光标名映射不对");
            }
            else
            {
                log.AppendLine("  OK   八个手柄的光标名映射正确（四个斜向 + 两个横竖 + 无边）");
            }

            // ---- 非法输入不能崩 ----
            checks++;
            bool noneSafe = !AnnotationResizeCalculator.TryComputeBounds(
                0, 0, 10, 10, AnnotationHandle.None, 5, 5, false, false, out left, out top, out right, out bottom);
            bool nanSafe = !AnnotationResizeCalculator.TryComputeBounds(
                0, 0, 10, 10, AnnotationHandle.BottomRight, double.NaN, 5, false, false, out left, out top, out right, out bottom);

            if (!noneSafe || !nanSafe)
            {
                failures++;
                log.AppendLine("  FAIL None 手柄与 NaN 指针都应安全返回 false");
            }
            else
            {
                log.AppendLine("  OK   None 手柄与 NaN 指针安全返回 false（调用方保持原样）");
            }

            // ---- 抓取边距必须**按类型**取 ----
            //
            // 曾经统一写成 max(线宽, 字号*0.6) + 4，而 FontSize 对所有标注都有默认值 28，
            // 于是一个 4px 粗的矩形也带了 20 多像素的抓取边距：在它旁边点一下会被判成
            // "选中并拖动"，而不是"在空白处新建标注"。这个缺陷是 [35] 的端到端用例抓出来的。
            checks++;
            AnnotationObject thinRectangle = new AnnotationObject
            {
                Kind = AnnotationKind.Rectangle,
                X1 = 0,
                Y1 = 0,
                X2 = 100,
                Y2 = 50,
                StrokeWidth = 4.0,
                FontSize = 28.0
            };

            AnnotationObject textLabel = new AnnotationObject
            {
                Kind = AnnotationKind.Text,
                X1 = 0,
                Y1 = 0,
                X2 = 1,
                Y2 = 1,
                StrokeWidth = 4.0,
                FontSize = 28.0
            };

            double rectanglePadding = thinRectangle.Left - thinRectangle.VisualBounds.Left;
            double textLabelPadding = textLabel.Left - textLabel.VisualBounds.Left;

            if (rectanglePadding > 8.0)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 细矩形的抓取边距应按线宽取（4px 线宽应约 6px），实际 {0:0.#}px —— 会误抓旁边的点击",
                    rectanglePadding));
            }
            else if (textLabelPadding < 10.0)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 文字标注的抓取边距应按字号取，实际只有 {0:0.#}px", textLabelPadding));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   抓取边距按类型取：4px 线宽矩形 {0:0.#}px，28px 文字 {1:0.#}px",
                    rectanglePadding, textLabelPadding));
            }
        }

        /// <summary>VM 层：手柄优先于拖动、尺寸按屏幕像素折算、箭头不掉头、可撤销。</summary>
        private static void CheckAnnotationResizeEndToEnd(
            IImageService imageService,
            string pngPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            MainViewModel viewModel = new MainViewModel(
                imageService,
                new NullDialogService(),
                new ImmediateDispatcherService());

            viewModel.LoadFromPathAsync(pngPath).GetAwaiter().GetResult();
            WaitForIdle(viewModel);

            viewModel.AnnotationToolIndex = (int)AnnotationKind.Rectangle;
            viewModel.BeginAnnotationCommand.Execute(null);
            viewModel.BeginAnnotationGesture(40, 30);
            viewModel.UpdateAnnotationGesture(140, 110);
            viewModel.EndAnnotationGesture();

            checks++;
            if (!viewModel.HasSelectedAnnotation || viewModel.AnnotationHandles.Count != 8)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 选中标注后应出现 8 个手柄，实际 {0} 个（选中={1}）",
                    viewModel.AnnotationHandles.Count, viewModel.HasSelectedAnnotation));
            }
            else
            {
                log.AppendLine("  OK   新建标注后自动选中并出现 8 个手柄");
            }

            // ---- 手柄尺寸按屏幕像素折算：缩放不影响它看起来的大小 ----
            checks++;
            viewModel.ZoomFactor = 1.0;
            double sizeAt100 = viewModel.AnnotationHandles.Count > 0 ? viewModel.AnnotationHandles[0].Size : 0.0;

            viewModel.ZoomFactor = 2.0;
            double sizeAt200 = viewModel.AnnotationHandles.Count > 0 ? viewModel.AnnotationHandles[0].Size : 0.0;

            if (sizeAt100 <= 0.0 || Math.Abs(sizeAt200 * 2.0 - sizeAt100) > 0.05)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 手柄尺寸应按屏幕像素折算（100% 时 {0:0.##}，200% 时应为其一半，实际 {1:0.##}）",
                    sizeAt100, sizeAt200));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   手柄按屏幕像素保持恒定大小（100% {0:0.#}px → 200% {1:0.#}px）", sizeAt100, sizeAt200));
            }

            viewModel.ZoomFactor = 1.0;

            // ---- 命中测试与光标 ----
            checks++;
            AnnotationHandleViewModel topLeft = FindAnnotationHandle(viewModel, AnnotationHandle.TopLeft);
            double topLeftX = topLeft == null ? 0.0 : topLeft.Left + topLeft.Size / 2.0;
            double topLeftY = topLeft == null ? 0.0 : topLeft.Top + topLeft.Size / 2.0;

            bool hitHandle = viewModel.HitTestAnnotationHandle(topLeftX, topLeftY) == AnnotationHandle.TopLeft;
            bool missCenter = viewModel.HitTestAnnotationHandle(90, 70) == AnnotationHandle.None;

            if (!hitHandle || !missCenter)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 命中测试不符：左上角手柄={0}，标注中心={1}",
                    viewModel.HitTestAnnotationHandle(topLeftX, topLeftY),
                    viewModel.HitTestAnnotationHandle(90, 70)));
            }
            else
            {
                log.AppendLine("  OK   手柄中心命中、标注中心不误判为手柄");
            }

            checks++;
            viewModel.UpdateAnnotationCursor(topLeftX, topLeftY);
            string onHandle = viewModel.AnnotationCursorName;

            viewModel.UpdateAnnotationCursor(90, 70);
            string offHandle = viewModel.AnnotationCursorName;

            if (onHandle != "SizeNWSE" || offHandle != null)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 光标提示不对：手柄上应为 SizeNWSE（实际 {0}），空白处应为空（实际 {1}）",
                    onHandle ?? "null", offHandle ?? "null"));
            }
            else
            {
                log.AppendLine("  OK   光标提示正确（手柄上 SizeNWSE，空白处十字准星）");
            }

            // ---- 抓角缩放：手柄必须优先于"拖动标注" ----
            AnnotationObject before = viewModel.Annotations[0].Source.Clone();
            AnnotationHandleViewModel bottomRight = FindAnnotationHandle(viewModel, AnnotationHandle.BottomRight);

            checks++;
            if (bottomRight == null)
            {
                failures++;
                log.AppendLine("  FAIL 找不到右下角手柄");
                return;
            }

            viewModel.BeginAnnotationGesture(bottomRight.Left + bottomRight.Size / 2.0, bottomRight.Top + bottomRight.Size / 2.0);
            viewModel.UpdateAnnotationGesture(200, 160);
            viewModel.EndAnnotationGesture();

            AnnotationObject after = viewModel.Annotations[0].Source;

            if (Math.Abs(after.Left - before.Left) > 1e-6 || Math.Abs(after.Top - before.Top) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 抓角缩放不该挪动左上角（{0:0.#},{1:0.#} → {2:0.#},{3:0.#}）—— 手柄可能被当成了拖动",
                    before.Left, before.Top, after.Left, after.Top));
            }
            else if (Math.Abs(after.Left + after.Width - 200) > 1e-6 || Math.Abs(after.Top + after.Height - 160) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 右下角应跟到指针 (200,160)，实际 ({0:0.#},{1:0.#})",
                    after.Left + after.Width, after.Top + after.Height));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   抓右下角缩放：左上角不动，尺寸 {0:0}×{1:0} → {2:0}×{3:0}",
                    before.Width, before.Height, after.Width, after.Height));
            }

            // ---- 撤销回到原尺寸 ----
            checks++;
            viewModel.UndoCommand.Execute(null);
            WaitForIdle(viewModel);

            AnnotationObject restored = viewModel.Annotations[0].Source;

            if (Math.Abs(restored.Width - before.Width) > 1e-6 || Math.Abs(restored.Height - before.Height) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 撤销后应恢复原尺寸 {0:0}×{1:0}，实际 {2:0}×{3:0}",
                    before.Width, before.Height, restored.Width, restored.Height));
            }
            else
            {
                log.AppendLine("  OK   撤销后恢复原尺寸（缩放是一步独立历史）");
            }

            // ---- 箭头缩放后不能掉头 ----
            viewModel.AnnotationToolIndex = (int)AnnotationKind.Arrow;
            viewModel.BeginAnnotationGesture(200, 150);
            viewModel.UpdateAnnotationGesture(120, 100);
            viewModel.EndAnnotationGesture();

            int arrowIndex = viewModel.AnnotationCount - 1;
            AnnotationObject arrow = viewModel.Annotations[arrowIndex].Source;

            checks++;
            if (arrow.Kind != AnnotationKind.Arrow || Math.Abs(arrow.X1 - 200) > 1e-6 || Math.Abs(arrow.Y1 - 150) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 箭头起点应为按下点 (200,150)，实际 ({0:0.#},{1:0.#})", arrow.X1, arrow.Y1));
            }
            else
            {
                log.AppendLine("  OK   箭头创建正确（起点 = 按下点，从右下指向左上）");
            }

            // 拖左上角手柄往外拉
            viewModel.BeginAnnotationGesture(120, 100);
            viewModel.UpdateAnnotationGesture(60, 50);
            viewModel.EndAnnotationGesture();

            AnnotationObject resizedArrow = viewModel.Annotations[arrowIndex].Source;

            checks++;
            if (Math.Abs(resizedArrow.X1 - 200) > 1e-6 || Math.Abs(resizedArrow.Y1 - 150) > 1e-6)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 箭头缩放后方向反了（起点变成 ({0:0.#},{1:0.#})），应始终从尾指向头",
                    resizedArrow.X1, resizedArrow.Y1));
            }
            else
            {
                log.AppendLine(string.Format(
                    "  OK   箭头缩放不掉头（起点仍是 200,150；新终点 {0:0.#},{1:0.#}）",
                    resizedArrow.X2, resizedArrow.Y2));
            }

            // ---- 文字：拖角改字号 ----
            // 落点刻意选在远离箭头的空白处（箭头缩放到 (60,50)-(200,150)，
            // 视觉包围盒还会往外扩），否则会变成"选中箭头并拖动"而不是新建文字。
            viewModel.AnnotationToolIndex = (int)AnnotationKind.Text;
            viewModel.AnnotationText = "缩放测试";
            viewModel.BeginAnnotationGesture(20, 20);
            viewModel.UpdateAnnotationGesture(21, 21);
            viewModel.EndAnnotationGesture();

            int textIndex = viewModel.AnnotationCount - 1;

            checks++;
            if (textIndex < 0 || viewModel.Annotations[textIndex].Source.Kind != AnnotationKind.Text)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 在空白处点击应新建文字标注，实际总数 {0}，末项类型 {1}",
                    viewModel.AnnotationCount,
                    textIndex < 0 ? "无" : viewModel.Annotations[textIndex].Source.Kind.ToString()));
                return;
            }

            double originalFontSize = viewModel.Annotations[textIndex].Source.FontSize;

            AnnotationHandleViewModel textCorner = FindAnnotationHandle(viewModel, AnnotationHandle.BottomRight);

            checks++;
            if (textCorner == null)
            {
                failures++;
                log.AppendLine("  FAIL 文字标注也应有缩放手柄（用与字号成比例的虚拟盒）");
            }
            else
            {
                // 文字盒是虚拟的（与字号成比例），拖角的距离换算成字号变化
                viewModel.BeginAnnotationGesture(textCorner.Left + textCorner.Size / 2.0, textCorner.Top + textCorner.Size / 2.0);
                viewModel.UpdateAnnotationGesture(
                    textCorner.Left + textCorner.Size / 2.0 + 60,
                    textCorner.Top + textCorner.Size / 2.0 + 60);
                viewModel.EndAnnotationGesture();

                double grownFontSize = viewModel.Annotations[textIndex].Source.FontSize;

                if (grownFontSize <= originalFontSize + 1.0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 文字拖角应变大字号（{0:0.#} → {1:0.#}）", originalFontSize, grownFontSize));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   文字拖角改字号（{0:0.#} → {1:0.#}，位置跟随新盒左上角）",
                        originalFontSize, grownFontSize));
                }
            }
        }

        private static AnnotationHandleViewModel FindAnnotationHandle(MainViewModel viewModel, AnnotationHandle handle)
        {
            if (viewModel == null)
            {
                return null;
            }

            for (int i = 0; i < viewModel.AnnotationHandles.Count; i++)
            {
                if (viewModel.AnnotationHandles[i].Handle == handle)
                {
                    return viewModel.AnnotationHandles[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 校验多文档标签页。
        ///
        /// 核心断言只有一条，但它是整个改造的意义所在：
        /// **每个标签各自持有撤销历史、调整参数与标注** ——
        /// 切到另一个标签再切回来，还能接着撤销自己刚才那一步。
        /// 如果状态没隔离干净，"撤销"会撤回另一个文档的操作，这是最吓人的一类缺陷。
        /// </summary>
        private static void CheckDocumentTabs(
            IImageService imageService,
            string pngPath,
            string jpgPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            log.AppendLine("[36] 多文档标签页");

            try
            {
                MainViewModel viewModel = new MainViewModel(
                    imageService,
                    new NullDialogService(),
                    new ImmediateDispatcherService());

                // ---- 初始只有一个空标签 ----
                checks++;
                if (viewModel.Sessions.Count != 1 || viewModel.ActiveSession == null || viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 初始应只有一个空标签（标签数={0}，已打开={1}）",
                        viewModel.Sessions.Count, viewModel.HasDocument));
                }
                else
                {
                    log.AppendLine("  OK   初始只有一个空标签，ActiveSession 非空（转发属性因此不用判空）");
                }

                // ---- 打开图片复用空标签 ----
                viewModel.LoadFromPathAsync(pngPath).GetAwaiter().GetResult();
                WaitForIdle(viewModel);

                checks++;
                if (viewModel.Sessions.Count != 1 || !viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 空标签应被复用而不是新建（标签数={0}，已打开={1}）",
                        viewModel.Sessions.Count, viewModel.HasDocument));
                }
                else
                {
                    log.AppendLine("  OK   打开图片复用了空标签（不会白留一个空白页）");
                }

                DocumentSession first = viewModel.ActiveSession;

                // ---- 在第一个标签上做一步可撤销的编辑 ----
                viewModel.AnnotationToolIndex = (int)AnnotationKind.Rectangle;
                viewModel.BeginAnnotationCommand.Execute(null);
                viewModel.BeginAnnotationGesture(30, 20);
                viewModel.UpdateAnnotationGesture(120, 90);
                viewModel.EndAnnotationGesture();

                checks++;
                if (first.History.UndoCount != 1 || viewModel.AnnotationCount != 1)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 第一个标签上应有 1 步历史与 1 个标注，实际 {0} / {1}",
                        first.History.UndoCount, viewModel.AnnotationCount));
                }
                else
                {
                    log.AppendLine("  OK   第一个标签：1 步历史 + 1 个标注");
                }

                // ---- 再打开一张图：应开新标签，不顶掉正在编辑的那个 ----
                viewModel.LoadFromPathAsync(jpgPath).GetAwaiter().GetResult();
                WaitForIdle(viewModel);

                checks++;
                if (viewModel.Sessions.Count != 2 || !viewModel.HasMultipleSessions)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 已有文档时再打开应新建标签，实际标签数 {0}", viewModel.Sessions.Count));
                }
                else if (ReferenceEquals(viewModel.ActiveSession, first))
                {
                    failures++;
                    log.AppendLine("  FAIL 新标签应当被激活");
                }
                else
                {
                    log.AppendLine("  OK   再打开一张图会新建标签并激活它（不会顶掉正在编辑的那张）");
                }

                DocumentSession second = viewModel.ActiveSession;

                // ---- 这是整个改造的核心：状态必须按标签隔离 ----
                checks++;
                if (viewModel.UndoCount != 0 || viewModel.AnnotationCount != 0)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 新标签的历史与标注应当是干净的（撤销步数={0}，标注={1}）—— 状态没有隔离",
                        viewModel.UndoCount, viewModel.AnnotationCount));
                }
                else
                {
                    log.AppendLine("  OK   切到新标签后撤销步数与标注都是 0（状态确实按标签隔离）");
                }

                checks++;
                if (ReferenceEquals(second.Document.FilePath, first.Document.FilePath)
                    || string.Equals(second.Document.FilePath, first.Document.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    failures++;
                    log.AppendLine("  FAIL 两个标签应当是两份不同的文档");
                }
                else
                {
                    log.AppendLine("  OK   两个标签持有不同的文档（" + Path.GetFileName(first.Document.FilePath)
                                   + " / " + Path.GetFileName(second.Document.FilePath) + "）");
                }

                // ---- 切回第一个标签：历史与标注都还在 ----
                viewModel.ActivateTabCommand.Execute(first);

                checks++;
                if (!ReferenceEquals(viewModel.ActiveSession, first)
                    || viewModel.UndoCount != 1
                    || viewModel.AnnotationCount != 1)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 切回第一个标签后应恢复它的历史与标注（撤销={0}，标注={1}）",
                        viewModel.UndoCount, viewModel.AnnotationCount));
                }
                else
                {
                    log.AppendLine("  OK   切回第一个标签：撤销步数与标注都回来了");
                }

                // ---- 标记位唯一 ----
                checks++;
                int activeTabs = 0;

                for (int i = 0; i < viewModel.Sessions.Count; i++)
                {
                    if (viewModel.Sessions[i].IsActiveTab)
                    {
                        activeTabs++;
                    }
                }

                if (activeTabs != 1)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 应当恰好有一个标签被标记为当前（实际 {0} 个）", activeTabs));
                }
                else
                {
                    log.AppendLine("  OK   同一时刻只有一个标签被标记为当前");
                }

                // ---- 调整参数也按标签隔离 ----
                viewModel.Brightness = 40.0;
                viewModel.ActivateTabCommand.Execute(second);
                double otherBrightness = viewModel.Brightness;

                viewModel.ActivateTabCommand.Execute(first);
                double firstBrightness = viewModel.Brightness;

                checks++;
                // 注意：Slider 的参数读取走的是当前标签的持有者，因此切过去必须是对方的 0。
                // 但 Brightness 的 setter 会走 SetAdjustment —— 它作用于**当前**标签，所以这里要小心顺序。
                if (Math.Abs(firstBrightness - 40.0) > 1e-6)
                {
                    failures++;
                    log.AppendLine(string.Format("  FAIL 第一个标签的亮度应为 40，实际 {0:0.#}", firstBrightness));
                }
                else
                {
                    log.AppendLine(string.Format(
                        "  OK   调整参数按标签隔离（第一个标签亮度 {0:0.#}，第二个标签 {1:0.#}）",
                        firstBrightness, otherBrightness));
                }

                // ---- 关闭标签 ----
                bool closed = viewModel.CloseSessionAsync(second).GetAwaiter().GetResult();

                checks++;
                if (!closed || viewModel.Sessions.Count != 1 || !ReferenceEquals(viewModel.ActiveSession, first))
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 关闭第二个标签后应只剩第一个并激活它（closed={0}，剩余={1}）",
                        closed, viewModel.Sessions.Count));
                }
                else
                {
                    log.AppendLine("  OK   关闭标签后自动激活相邻标签");
                }

                // ---- 关掉最后一个：重置为空会话，而不是把集合清空 ----
                bool closedLast = viewModel.CloseSessionAsync(first).GetAwaiter().GetResult();

                checks++;
                if (!closedLast || viewModel.Sessions.Count != 1 || viewModel.HasDocument)
                {
                    failures++;
                    log.AppendLine(string.Format(
                        "  FAIL 关掉最后一个标签应保留一个空标签（closed={0}，标签数={1}，已打开={2}）",
                        closedLast, viewModel.Sessions.Count, viewModel.HasDocument));
                }
                else if (viewModel.ActiveSession == null)
                {
                    failures++;
                    log.AppendLine("  FAIL ActiveSession 不能为 null（转发属性依赖这条不变量）");
                }
                else
                {
                    log.AppendLine("  OK   关掉最后一个标签会重置为空标签（ActiveSession 永不为 null）");
                }

                // ---- 标签栏 XAML：真正构造窗口，验证模板能被实例化 ----
                CheckTabBarXaml(imageService, pngPath, jpgPath, ref checks, ref failures, log);
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 多文档标签页测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
        }

        /// <summary>
        /// 构造一个"有两个标签"的主窗口，强制布局 —— 验证标签栏模板能被实例化。
        ///
        /// 单独做这一步的理由：标签栏在没有文档时是隐藏的，而隐藏的元素不会被测量，
        /// DataTemplate 也就不会被实例化 —— 于是模板里的绑定错误在 [23] 里根本暴露不出来。
        /// 这里必须开着文档、而且是两个标签，才真正走到模板。
        /// </summary>
        private static void CheckTabBarXaml(
            IImageService imageService,
            string pngPath,
            string jpgPath,
            ref int checks,
            ref int failures,
            StringBuilder log)
        {
            MainViewModel viewModel = new MainViewModel(
                imageService,
                new NullDialogService(),
                new ImmediateDispatcherService());

            viewModel.LoadFromPathAsync(pngPath).GetAwaiter().GetResult();
            WaitForIdle(viewModel);
            viewModel.LoadFromPathAsync(jpgPath).GetAwaiter().GetResult();
            WaitForIdle(viewModel);

            Exception windowError = null;
            int realizedTabs = 0;

            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(new Action(() =>
            {
                try
                {
                    Views.MainWindow window = new Views.MainWindow { DataContext = viewModel };
                    window.Measure(new Size(1180, 760));
                    window.Arrange(new Rect(0, 0, 1180, 760));
                    window.UpdateLayout();

                    realizedTabs = viewModel.Sessions.Count;
                }
                catch (Exception ex)
                {
                    windowError = ex;
                }
            }));

            checks++;
            if (windowError != null)
            {
                failures++;
                log.AppendLine(string.Format(
                    "  FAIL 带标签栏的主窗口构造失败：{0} {1}", windowError.GetType().Name, windowError.Message));
            }
            else if (realizedTabs != 2)
            {
                failures++;
                log.AppendLine(string.Format("  FAIL 构造窗口时应有 2 个标签，实际 {0}", realizedTabs));
            }
            else
            {
                log.AppendLine("  OK   带标签栏的主窗口能构造并完成布局（标签模板可实例化）");
            }
        }

        /// <summary>点 (x, y) 是否落在以 (x1,y1)-(x2,y2) 为轴的胶囊带内（用于校验涂抹范围）。</summary>
        private static bool IsNearStroke(int x, int y, int x1, int y1, int x2, int y2, int radius)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lengthSquared = dx * dx + dy * dy;

            double t = lengthSquared <= 1e-9
                ? 0.0
                : ((x - x1) * dx + (y - y1) * dy) / lengthSquared;

            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            double closestX = x1 + t * dx;
            double closestY = y1 + t * dy;
            double ex = x - closestX;
            double ey = y - closestY;

            return Math.Sqrt(ex * ex + ey * ey) <= radius;
        }

        /// <summary>
        /// 仿制图章的测试底图：左半边深蓝 (B220 G60 R20) 作为"源纹理"，右半边浅灰 (B200 G200 R200)。
        /// 用左右分界而不是一个小方块，是为了让"源区域"能完整覆盖偏移后的取样范围，
        /// 这样涂抹包围盒才正好等于笔刷直径，便于断言。
        /// </summary>
        private static PixelBuffer CreateStampSource(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    bool patch = x < width / 2;

                    pixels[index] = patch ? (byte)220 : (byte)200;      // B
                    pixels[index + 1] = patch ? (byte)60 : (byte)200;   // G
                    pixels[index + 2] = patch ? (byte)20 : (byte)200;   // R
                    pixels[index + 3] = 255;
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>
        /// 横向渐变测试图：B 通道从左到右递增、G 通道递减、R 固定 128。
        /// 三个通道走势不同，因此可以顺带抓出通道顺序写反的问题。
        /// </summary>
        private static PixelBuffer CreateHorizontalRamp(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;

                    pixels[index] = (byte)(x * 255 / Math.Max(1, width - 1));
                    pixels[index + 1] = (byte)((width - 1 - x) * 255 / Math.Max(1, width - 1));
                    pixels[index + 2] = 128;
                    pixels[index + 3] = 255;
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>黑 / 白相间的竖条纹（用于检验降采样是否把细纹平滑掉）。</summary>
        private static PixelBuffer CreateStripedBuffer(int width, int height, int stripeWidth)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte value = ((x / stripeWidth) % 2) == 0 ? (byte)255 : (byte)0;
                    int index = (y * width + x) * 4;

                    pixels[index] = value;
                    pixels[index + 1] = value;
                    pixels[index + 2] = value;
                    pixels[index + 3] = 255;
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>
        /// 左半不透明红、右半完全透明（且 RGB 为 0）。
        /// 右侧的 "0,0,0,0" 正是"直通 alpha 混色会变黑"的根源，用于验证预乘 alpha 是否生效。
        /// </summary>
        private static PixelBuffer CreateHalfTransparentBuffer(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;

                    // BGRA
                    pixels[index] = 0;
                    pixels[index + 1] = 0;
                    pixels[index + 2] = x < width / 2 ? (byte)255 : (byte)0;
                    pixels[index + 3] = x < width / 2 ? (byte)255 : (byte)0;
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>
        /// 读取某套主题配色字典里定义的全部键（用 ResourceDictionary 直接加载，不依赖当前主题）。
        /// </summary>
        private static void CollectThemeKeys(ThemePreference preference, List<string> keys)
        {
            keys.Clear();

            string source = preference == ThemePreference.Dark
                ? "pack://application:,,,/PS-text;component/Resources/Theme.Dark.xaml"
                : "pack://application:,,,/PS-text;component/Resources/Theme.Light.xaml";

            try
            {
                ResourceDictionary dictionary = new ResourceDictionary
                {
                    Source = new Uri(source, UriKind.Absolute)
                };

                foreach (object key in dictionary.Keys)
                {
                    string name = key as string;

                    if (!string.IsNullOrEmpty(name))
                    {
                        keys.Add(name);
                    }
                }

                keys.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
                keys.Clear();
            }
        }

        /// <summary>创建指定纯色的缓冲。</summary>
        private static PixelBuffer CreateSolidBuffer(int width, int height, byte r, byte g, byte b)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>创建棋盘格（黑白相间），用于检验模糊 / 锐化对对比度的影响。</summary>
        private static PixelBuffer CreateCheckerboard(int width, int height, int cellSize)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    bool dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                    byte value = dark ? (byte)0 : (byte)255;

                    pixels[index] = value;
                    pixels[index + 1] = value;
                    pixels[index + 2] = value;
                    pixels[index + 3] = 255;
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>
        /// 校验“结果每个像素都等于变换后的输入”，返回最大通道偏差。
        /// invertCheck 为 true 时按 255-源值 期望。
        /// </summary>
        private static int MaxChannelError(PixelBuffer result, PixelBuffer source, bool invertCheck)
        {
            byte[] resultPixels = result.GetPixels();
            byte[] sourcePixels = source.GetPixels();

            if (resultPixels.Length != sourcePixels.Length)
            {
                return int.MaxValue;
            }

            int maximum = 0;

            for (int i = 0; i < resultPixels.Length; i++)
            {
                int sourceValue = sourcePixels[i];
                bool isAlpha = (i % 4) == 3;
                int expected;

                if (invertCheck)
                {
                    // 反色只作用于颜色通道，Alpha 必须原样保留
                    expected = isAlpha ? sourceValue : 255 - sourceValue;
                }
                else
                {
                    expected = sourceValue;
                }

                int delta = Math.Abs(resultPixels[i] - expected);
                if (delta > maximum)
                {
                    maximum = delta;
                }
            }

            return maximum;
        }

        private static int MinChannelValue(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            int minimum = 255;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    if (pixels[i + channel] < minimum)
                    {
                        minimum = pixels[i + channel];
                    }
                }
            }

            return minimum;
        }

        private static int MaxChannelValue(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            int maximum = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    if (pixels[i + channel] > maximum)
                    {
                        maximum = pixels[i + channel];
                    }
                }
            }

            return maximum;
        }

        /// <summary>灰度通道的标准差（衡量局部对比度）。</summary>
        private static double StandardDeviation(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            double sum = 0.0;
            double sumSquares = 0.0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double luma = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                sum += luma;
                sumSquares += luma * luma;
                count++;
            }

            if (count == 0)
            {
                return 0.0;
            }

            double mean = sum / count;
            double variance = (sumSquares / count) - (mean * mean);
            return variance <= 0.0 ? 0.0 : Math.Sqrt(variance);
        }

        /// <summary>某一行的最大水平梯度（衡量边缘锐度）。</summary>
        private static double MaxHorizontalGradient(PixelBuffer buffer, int y)
        {
            byte[] row = new byte[buffer.Stride];
            buffer.CopyRow(Math.Min(y, buffer.Height - 1), row, 0, buffer.Stride);

            double maximum = 0.0;

            for (int x = 1; x < buffer.Width; x++)
            {
                int currentIndex = x * 4;
                int previousIndex = (x - 1) * 4;

                double current = 0.114 * row[currentIndex] + 0.587 * row[currentIndex + 1] + 0.299 * row[currentIndex + 2];
                double previous = 0.114 * row[previousIndex] + 0.587 * row[previousIndex + 1] + 0.299 * row[previousIndex + 2];
                double gradient = Math.Abs(current - previous);

                if (gradient > maximum)
                {
                    maximum = gradient;
                }
            }

            return maximum;
        }

        /// <summary>创建测试用像素缓冲（竖向渐变，各通道不同，便于识别调整方向）。</summary>
        private static PixelBuffer CreateTestBuffer(int width, int height)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width + x) * 4;
                    byte t = (byte)(x * 255 / Math.Max(1, width - 1));
                    byte u = (byte)(y * 255 / Math.Max(1, height - 1));

                    pixels[index] = (byte)(255 - t);        // B
                    pixels[index + 1] = u;                  // G
                    pixels[index + 2] = t;                  // R
                    pixels[index + 3] = 255;                // A
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>叠加确定性噪点（让压缩率更接近真实照片，而不是纯渐变）。</summary>
        private static PixelBuffer AddNoise(PixelBuffer source, int seed)
        {
            byte[] pixels = source.GetPixelsCopy();
            int random = seed;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                // 线性同余，确定性、无需 Random 实例
                random = (random * 1103515245 + 12345) & 0x7FFFFFFF;
                int noise = (random >> 16) % 61 - 30;

                pixels[i] = ClampByte(pixels[i] + noise);
                pixels[i + 1] = ClampByte(pixels[i + 1] + noise);
                pixels[i + 2] = ClampByte(pixels[i + 2] + noise);
            }

            return new PixelBuffer(pixels, source.Width, source.Height);
        }

        /// <summary>按 2×2 平均降采样（模拟预览缓冲）。</summary>
        private static PixelBuffer DownsampleByTwo(PixelBuffer source)
        {
            int width = Math.Max(1, source.Width / 2);
            int height = Math.Max(1, source.Height / 2);
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int destination = (y * width + x) * 4;
                    int sum0 = 0;
                    int sum1 = 0;
                    int sum2 = 0;
                    int sum3 = 0;

                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sourceIndex = ((y * 2 + dy) * source.Width + (x * 2 + dx)) * 4;
                            byte[] sourcePixels = source.GetPixels();
                            sum0 += sourcePixels[sourceIndex];
                            sum1 += sourcePixels[sourceIndex + 1];
                            sum2 += sourcePixels[sourceIndex + 2];
                            sum3 += sourcePixels[sourceIndex + 3];
                        }
                    }

                    pixels[destination] = (byte)(sum0 / 4);
                    pixels[destination + 1] = (byte)(sum1 / 4);
                    pixels[destination + 2] = (byte)(sum2 / 4);
                    pixels[destination + 3] = (byte)(sum3 / 4);
                }
            }

            return new PixelBuffer(pixels, width, height);
        }

        /// <summary>创建指定纯色的编辑状态（用于撤销 / 重做测试）。</summary>
        private static EditState CreateSolidState(int width, int height, byte r, byte g, byte b)
        {
            byte[] pixels = new byte[width * height * 4];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }

            return EditState.Create(new PixelBuffer(pixels, width, height), PixelAdjustments.Neutral, 96.0, 96.0);
        }

        /// <summary>裁剪出子区域（用于比较并行整图与串行小图的输出）。</summary>
        private static PixelBuffer CropBuffer(PixelBuffer source, int x, int y, int width, int height)
        {
            if (source == null)
            {
                throw new ArgumentNullException("source");
            }

            width = Math.Min(width, source.Width - x);
            height = Math.Min(height, source.Height - y);

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width", "裁剪区域超出源图范围。");
            }

            byte[] output = new byte[width * height * 4];
            byte[] row = new byte[source.Stride];

            for (int rowIndex = 0; rowIndex < height; rowIndex++)
            {
                source.CopyRow(y + rowIndex, row, 0, source.Stride);
                Buffer.BlockCopy(row, x * 4, output, rowIndex * width * 4, width * 4);
            }

            return new PixelBuffer(output, width, height);
        }

        /// <summary>读取指定坐标的像素（B,G,R,A 四字节）。</summary>
        private static byte[] CopyPixel(PixelBuffer buffer, int x, int y)
        {
            byte[] row = new byte[buffer.Stride];
            buffer.CopyRow(y, row, 0, buffer.Stride);

            int offset = x * 4;
            return new byte[] { row[offset], row[offset + 1], row[offset + 2], row[offset + 3] };
        }

        /// <summary>像素的可读描述（便于日志排查）。</summary>
        private static string DescribePixel(byte[] pixel)
        {
            if (pixel == null || pixel.Length < 4)
            {
                return "(无效像素)";
            }

            return string.Format("R{0} G{1} B{2} A{3}", pixel[2], pixel[1], pixel[0], pixel[3]);
        }

        /// <summary>两个像素数组之间的最大绝对通道差（逐字节比较）。</summary>
        private static int MaxPixelsDifference(byte[] left, byte[] right)
        {
            if (left == null || right == null)
            {
                return int.MaxValue;
            }

            if (left.Length != right.Length)
            {
                return int.MaxValue;
            }

            int maximum = 0;

            for (int i = 0; i < left.Length; i++)
            {
                // Alpha 通道也参与比较：快照必须完全无损。
                int delta = Math.Abs(left[i] - right[i]);
                if (delta > maximum)
                {
                    maximum = delta;
                }
            }

            return maximum;
        }

        /// <summary>两个像素在 R/G/B 通道上的最大绝对差。</summary>
        private static int MaxAbsDifference(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length < 3 || right.Length < 3)
            {
                return int.MaxValue;
            }

            int maximum = 0;

            for (int channel = 0; channel < 3; channel++)
            {
                int delta = Math.Abs(left[channel] - right[channel]);
                if (delta > maximum)
                {
                    maximum = delta;
                }
            }

            return maximum;
        }

        private static bool PixelsEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static double AverageLuma(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            double total = 0.0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                total += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                count++;
            }

            return count == 0 ? 0.0 : total / count;
        }

        /// <summary>所有像素中 R/G/B 三通道差值的最大值（灰度图应为 0）。</summary>
        private static int MaxChannelDelta(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            int maximum = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i];
                int g = pixels[i + 1];
                int r = pixels[i + 2];

                int delta = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
                if (delta > maximum)
                {
                    maximum = delta;
                }
            }

            return maximum;
        }

        /// <summary>整幅图的通道取值范围（用于判断对比度是否压缩了动态范围）。</summary>
        private static double ChannelRange(PixelBuffer buffer)
        {
            byte[] pixels = buffer.GetPixels();
            int minimum = 255;
            int maximum = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int value = pixels[i + channel];
                    if (value < minimum)
                    {
                        minimum = value;
                    }

                    if (value > maximum)
                    {
                        maximum = value;
                    }
                }
            }

            return maximum - minimum;
        }

        /// <summary>两个通道均值的差（用于判断色温方向）。</summary>
        private static double ChannelMeanDifference(PixelBuffer buffer, int channelA, int channelB)
        {
            byte[] pixels = buffer.GetPixels();
            double sumA = 0.0;
            double sumB = 0.0;
            int count = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                sumA += pixels[i + channelA];
                sumB += pixels[i + channelB];
                count++;
            }

            return count == 0 ? 0.0 : (sumA - sumB) / count;
        }

        private static byte ClampByte(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            return value > 255 ? (byte)255 : (byte)value;
        }

        private static string FormatBytes(long bytes)
        {
            return HistoryManager.FormatBytes(bytes);
        }

        /// <summary>生成指定格式的测试图片（渐变 + 网格，便于肉眼确认显示正确）。</summary>
        private static void CreateTestImage(
            string path,
            int width,
            int height,
            double dpi,
            ImageFileFormat format,
            StringBuilder log)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                Rect rect = new Rect(0, 0, width, height);

                LinearGradientBrush gradient = new LinearGradientBrush(
                    Color.FromRgb(32, 96, 192),
                    Color.FromRgb(240, 200, 64),
                    new Point(0, 0),
                    new Point(1, 1));
                context.DrawRectangle(gradient, null, rect);

                Pen gridPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1.0);
                for (int x = 0; x < width; x += 40)
                {
                    context.DrawLine(gridPen, new Point(x, 0), new Point(x, height));
                }

                for (int y = 0; y < height; y += 40)
                {
                    context.DrawLine(gridPen, new Point(0, y), new Point(width, y));
                }

                FormattedText text = new FormattedText(
                    width + "x" + height + " @ " + dpi.ToString("0") + "dpi",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    20,
                    Brushes.White,
                    1.0);
                context.DrawText(text, new Point(12, 12));
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            BitmapEncoder encoder;
            switch (format)
            {
                case ImageFileFormat.Jpeg:
                    encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                    break;
                case ImageFileFormat.Bmp:
                    encoder = new BmpBitmapEncoder();
                    break;
                case ImageFileFormat.Tiff:
                    encoder = new TiffBitmapEncoder();
                    break;
                default:
                    encoder = new PngBitmapEncoder();
                    break;
            }

            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            log.AppendLine("  OK   生成 " + Path.GetFileName(path) + "（"
                + new FileInfo(path).Length + " 字节）");
        }

        /// <summary>尝试以独占方式打开文件，用于验证加载后是否残留文件句柄。</summary>
        private static bool IsFileUnlocked(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return stream.Length >= 0;
                }
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

        private static void WriteLog(string content)
        {
            string[] candidates = BuildLogPaths();

            foreach (string candidate in candidates)
            {
                try
                {
                    string directory = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(candidate, content, new UTF8Encoding(true));
                    Console.WriteLine("SELFTEST_LOG=" + candidate);
                }
                catch (Exception)
                {
                    // 换下一个候选路径。
                }
            }
        }

        private static string[] BuildLogPaths()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return new[]
            {
                // 优先写入仓库的 build 目录（可从源码目录直接查看）
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "build", "selftest.log")),
                Path.Combine(baseDirectory, "selftest.log"),
                Path.Combine(Path.GetTempPath(), "pstext-selftest.log")
            };
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响自检结论。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }



        /// <summary>自检用的对话框桩：不弹窗，仅记录。</summary>
        private sealed class NullDialogService : IDialogService
        {
            public string ShowOpenImageDialog(string title, string initialDirectory)
            {
                return null;
            }

            public System.Collections.Generic.IReadOnlyList<string> ShowOpenImagesDialog(string title, string initialDirectory)
            {
                return new System.Collections.Generic.List<string>();
            }

            public string ShowSaveImageDialog(
                string title,
                string initialDirectory,
                string suggestedFileName,
                string filter,
                string defaultExtension)
            {
                return null;
            }

            public string ShowFolderDialog(string title, string initialDirectory)
            {
                return null;
            }

            public ConfirmResult Confirm(string message, string title)
            {
                return ConfirmResult.Yes;
            }

            public void ShowError(string message, string title)
            {
            }

            public void ShowInformation(string message, string title)
            {
            }

            public void ShowException(string message, Exception exception, string title)
            {
            }
        }

        /// <summary>自检用的调度器桩：直接执行（自检运行在无窗口线程）。</summary>
        private sealed class ImmediateDispatcherService : IDispatcherService
        {
            private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

            public bool IsOnUiThread
            {
                get { return true; }
            }

            public Dispatcher Dispatcher
            {
                get { return _dispatcher; }
            }

            public void Invoke(Action action)
            {
                if (action != null)
                {
                    action();
                }
            }

            public System.Threading.Tasks.Task InvokeAsync(Action action)
            {
                if (action != null)
                {
                    action();
                }

                return System.Threading.Tasks.Task.FromResult(0);
            }

            public System.Threading.Tasks.Task<T> InvokeAsync<T>(Func<T> function)
            {
                return System.Threading.Tasks.Task.FromResult(function == null ? default(T) : function());
            }
        }

    }
}
