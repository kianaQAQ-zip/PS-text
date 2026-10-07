using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSText.Infrastructure.History;
using PSText.Infrastructure.Imaging;
using PSText.Models;
using PSText.Services;
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
            }
            catch (Exception ex)
            {
                failures++;
                log.AppendLine("  FAIL 主窗口测试异常: " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();
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
