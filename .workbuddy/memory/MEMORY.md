# PS-text 项目长期备忘

## 项目定位（已拍板 · 2026-10-07）
- **路线 B｜办公图片处理**：裁剪、加字/水印、标注、打印、批量。
- 一句话定位：**能在 Windows 7 干净跑的、零依赖的、中文批量图片处理工具**。
- ⚠️ 对外文案**不要**再写"像美图秀秀 / PS"——会拉高预期、暴露短板。

## 明确排除（不再讨论）
美颜/磨皮/祛痘/瘦脸、人脸检测与关键点模型、OpenCVSharp 等 CV 依赖、贴纸、一键风格滤镜库(LUT)。

## 核心功能优先级
- **P0**：批量处理、非破坏性标注与水印、图像尺寸缩放、未保存提示、崩溃日志落盘、应用图标、部署包。
- **P1**：任意角度旋转、多文档标签页、EXIF 保留。
- **P2**：最近文件缩略图、窗口/面板状态记忆、i18n。

## 技术栈与约束（既有，勿擅改）
- WPF + .NET Framework 4.8，`AnyCPU + Prefer32Bit=true`（32 位运行）。
- **零第三方 NuGet**——引入任何依赖前必须先向用户确认。
- 构建产物在 `build/bin`（`Directory.Build.props` 定制），`.csproj` 为传统显式包含。
- 自检在 `SelfTest.cs`（4,384 行 / 142 断言），`PSText.exe --selftest` 运行。
- MVVM：VM 只依赖 `IImageService / IDialogService / IDispatcherService / IPrintService`，**不得引用 WPF 控件类型**。
- 滤镜一律写成纯函数：`BitmapSource → WriteableBitmap`，并保持"串行=并行"逐像素一致。

## 已记录的坑
- `WmpBitmapEncoder` 是 **JPEG XR（HD Photo）**，不是 WebP；代码注释/属性名 `IsWebP`/README 均为错误表述，待修正为 `IsJpegXr`。
- 调整预览用降采样 + 防抖提交；`_lastRenderedAdjustments` 与 `_committedAdjustments` 必须分开维护（曾因此丢历史步骤）。

## 工作流约定
- 需求决策走 grill 流程：AI 质询 → 用户拍板 → 按 P0/P1/P2 实施。
- 每阶段交付后跑 `--selftest` 再进下一阶段。
- 用户 GitHub 账号 `kianaQAQ-zip`，提交身份 `kiana / 1440667466@qq.com`；红线：不得提交真实密钥。当前仓库尚无 `.git`，M0 阶段补。
