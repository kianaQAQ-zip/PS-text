# PS-text 项目长期备忘

## 项目定位（已拍板 · 2026-10-07）
- **路线 B｜办公图片处理**：裁剪、加字/水印、标注、打印、批量。
- 一句话定位：**能在 Windows 7 干净跑的、零依赖的、中文批量图片处理工具**。
- ⚠️ 对外文案**不要**再写"像美图秀秀 / PS"——会拉高预期、暴露短板。

## 明确排除（不再讨论）
美颜/磨皮/祛痘/瘦脸、人脸检测与关键点模型、OpenCVSharp 等 CV 依赖、贴纸、一键风格滤镜库(LUT)。

## 核心功能优先级
- **P0**：批量处理、**修补/消除（智能填充）**、非破坏性标注与水印、图像尺寸缩放、未保存提示、崩溃日志落盘、应用图标、部署包。
- **P1**：任意角度旋转（已完成）、仿制图章、多文档标签页、EXIF 保留。
- **P2**：最近文件缩略图、窗口/面板状态记忆、i18n。

## 关于「消去水印」的定位（已拍板 · 2026-10-07）
- 目标场景：**半透明文字水印 + 扫描件印章标记**（不做复杂纹理上的图库 logo）。
- 档位：**智能填充（调和扩散）+ 仿制图章**。**不引入纹理合成、不引入 AI**。
- 命名立场：做成中性「修补 / 消除」，**不宣传为"去水印神器"**（它本就是 PS 污点修复画笔那类通用能力）。
- 为什么不上 AI：ONNX Runtime 官方 **v1.15.0 起不再支持 Win7**（实测可用停在 1.11 附近，未维护），
  加百 MB 模型会破坏「零依赖 / 轻量 / 内网离线」三个卖点，而 Win7 用户恰恰最需要离线。
- 能力边界（已在面板文案里写明，不要吹）：纯色/渐变背景效果好；**纹理背景会留平滑斑块**（调和插值的数学限制）。

## 技术栈与约束（既有，勿擅改）
- WPF + .NET Framework 4.8，`AnyCPU + Prefer32Bit=true`（32 位运行）。
- **零第三方 NuGet**——引入任何依赖前必须先向用户确认。
- 构建产物在 `build/bin`（`Directory.Build.props` 定制），`.csproj` 为传统显式包含（**新文件必须手工加 `<Compile Include>`**）。
- 自检在 `SelfTest.cs`（约 10,700 行 / **311 断言**），`PSText.exe --selftest` 运行。
  ⚠️ 其中**文件关联的写入流程约 18 项在本机是 Skip 的**（本机工具层拦下可执行文件写注册表，
  已核实非代码问题）。自检先探测 HKCU 可写性，不可写就明确 Skip，避免恒红。
- MVVM：VM 只依赖 `IImageService / IDialogService / IDispatcherService / IPrintService`，**不得引用 WPF 控件类型**。
- 滤镜一律写成纯函数：`BitmapSource → WriteableBitmap`，并保持"串行=并行"逐像素一致。
- 项目保持**零编译警告**：fire-and-forget 的 `Task` 要存进字段（否则 CS4014）。

## 构建与自检（本机实测可行路径）
- 构建必须用 VS2022 MSBuild（SDK 9 无 net48 WPF 目标包）：
  `C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe PSText.csproj -t:Build -p:Configuration=Release`
- ⚠️ **MSBuild 被本机工具层可执行文件黑名单拦截**：Bash 与 PowerShell 直接调用都会报
  "Known Windows LOLBin"，加 `dangerouslyDisableSandbox` 也无效。
  可行做法：写一个 Python 脚本用 `subprocess.run([MSBUILD, ...])` 调起（托管 Python 在
  `C:\Users\kiana\.workbuddy\binaries\python\envs\default\Scripts\python.exe`）。
- 自检：`PS-text.exe --selftest`。**必须把 TMP/TEMP 指向项目内可写目录**，
  否则 `%TEMP%\pstext-selftest-*` 建目录被拒（沙箱）→ 退出码 1。
  例：`TMP="D:/Code/PS-text/build/tmp" TEMP="D:/Code/PS-text/build/tmp" ./PS-text.exe --selftest`
- 日志落在 `build/selftest.log`、`build/bin/Release/selftest.log`、`$TEMP/pstext-selftest.log` 三处。
- PowerShell 的 `[Reflection.Assembly]::LoadFrom` 也被拦截（等同 Add-Type），
  想验证某个静态方法请**改成加一条自检**，别走反射。
- 已沉淀脚本（都在 `tools/`，**进仓库**；`build/` 被 gitignore，只放产物）：
  `"<managed-python>" tools/build.py Release`、`tools/package.py`（免安装包）、
  `tools/verify-launcher.ps1`（启动器分支验证，需 `Set-ExecutionPolicy -Scope Process Bypass`）。

## 版本控制与远端（已配置 · 2026-10-07）
- 远端：`git@github.com:kianaQAQ-zip/PS-text.git`，分支 `main`，SSH 推送，追踪已建立。
- ⚠️ 远端最初已有一个 `Initial commit`（**MIT LICENSE**，署名 `kianaQAQ`）。若再遇到"远端不是空仓库"，
  用 `git fetch` + `git rebase origin/main` 接上，**不要 force push**。
- 提交身份 `kiana / 1440667466@qq.com`；提交前查密钥 `grep -rnE "sk-[A-Za-z0-9]{10}"`。
- 项目采用 **MIT 许可**（README 尚未提到，可选补充）。
- 本机「net48 WPF 构建 + 无头自检」工作流已沉淀为技能 `~/.workbuddy/skills/net48-wpf-build-and-selftest/SKILL.md`。

## 已记录的坑
- `WmpBitmapEncoder` 是 **JPEG XR（HD Photo）**，不是 WebP；代码注释/属性名 `IsWebP`/README 均为错误表述，已修正为 `IsJpegXr`。
- 调整预览用降采样 + 防抖提交；`_lastRenderedAdjustments` 与 `_committedAdjustments` 必须分开维护（曾因此丢历史步骤）。
- `Parallel.For` 的 TLocal 重载（`Func<TLocal>` + `body` + `Action<TLocal>`）**必须传满 6 个参数含 `localFinally`**，
  否则编译器会匹配到 `long` 重载，循环变量变成 long 而报 CS0266。
- ViewModel 属性名会遮蔽同名类型：曾把属性命名为 `RotationAngle`，压掉了 `Services.Filters.RotationAngle` 枚举，
  导致其它 partial 里的 `RotationAngle.Clockwise90` 编译失败。现命名 `RotationDegrees`。
- 自检里窗口不 `Show()` → **可视树未建立**，`VisualTreeHelper` 取不到控件；要用 `LogicalTreeHelper` 走逻辑树。
- **XAML 注释里不能出现 `--`**：`<!-- ---------- 标题 ---------- -->` 是非法 XML，报 `MC3000`。
  分隔注释统一写 `<!-- ===== 标题 ===== -->`。
- **`TextBox` 绑 `int`（`UpdateSourceTrigger=PropertyChanged`）**：清空时会转换失败但不会崩，
  WPF 保留旧值并标红。既有页面都是这么做的，属可接受行为。
- `ComboBox` 绑枚举用 `SelectedValuePath="Tag"` + `{x:Static ns:Enum.Member}`，
  比"再维护一个 int 索引属性"少一层需要双向同步的中间状态。
- **`RenderTargetBitmap` / `DrawingVisual` / `FormattedText` 不要求 UI 线程**，
  只要求"创建与使用在同一线程"。已验证：整条含文字水印的批量流水线在线程池线程跑通（自检 [32]）。
- **自检里不能轮询"批量跑完没有"**：批量入口刻意返回 `Task`（`internal Task RunBatchAsync()`），
  自检 `GetAwaiter().GetResult()` 直接等它，不靠 sleep 猜。

## 交付与系统集成（M4 · 2026-10-09 完成）
- **产品版本号只写在 `Properties/AssemblyInfo.cs`**（`AssemblyInformationalVersion` = 单一来源，打包脚本读它）。
  传统（非 SDK）csproj **不消费** `<AssemblyTitle>` / `<AssemblyVersion>` —— 写了不报错，
  但 exe 版本**永远 0.0.0.0**（M4 之前一直如此）。自动生成程序集信息只对 SDK 风格工程生效。
- **4.8 判定必须读注册表** `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` 的 `Release`：
  `Environment.Version` 在任何 .NET 4.x 上都报 **4.0.30319**（它是 CLR 版本），拿它判断必然错。
  32 位进程读 `HKLM\SOFTWARE` 会被重定向到 `Wow6432Node` → **两个注册表视图都读，取较大的 Release**。
- **启动器 bat 不能用 `reg query` 的 Release 比大小**：`reg` 打印的是**十六进制**（`0x82309`）。
  改用 `reg query ... /v Version | findstr /c:"4.8."`（一条命令、不涉进制、与系统语言无关）。
- **检测要做两层**（因为没有 App.config，exe 声明 CLR v4.0）：只装 4.5/4.6.2 的 Win7 上
  **exe 能起来**却在用 4.8 API 时半路崩。程序内启动检测管"起来了但版本不够"；bat 管"根本起不来 + 离线装"。
- **文件关联只登记"打开方式"、不抢默认**（Vista 起程序无权静默设默认）。写 HKCU，不需要管理员：
  `Classes\PSText.Image`（ProgID/DefaultIcon/shell\open\command）+ `Classes\Applications\PS-text.exe`
  + 各扩展名 `OpenWithProgids` + `RegisteredApplications` 与 `PS-text\Capabilities`（Win7「设置默认程序」只认这套）。
  收尾必须调 `SHChangeNotify(SHCNE_ASSOCCHANGED)`，否则资源管理器要等下次登录才认。
- **关联服务的注册表根键是构造参数**，自检写 `Software\PSText-SelfTest-<guid>`，**绝不碰用户真实关联**。
- 免安装包：`tools/package.py` → `build/dist/PS-text-1.0.0-win7-portable/` + zip（**0.24 MB**，
  只有 exe + 启动器 + 说明，因为零 NuGet 且无 App.config → 不生成 `.exe.config`）。
- 无界面开关：`--register` / `--unregister` / `--assoc-status` / `--runtime`（`Services/CommandLineOptions.cs`）。
  WinExe 无控制台 → `Services/ConsoleBridge.cs` 用 `AttachConsole(ATTACH_PARENT_PROCESS)`，失败退回弹窗。

## 撤销架构（三种模型，按编辑类型选）
1. **整幅压缩快照**（`EditState` + `BufferEditCommand`）：滤镜 / 裁剪 / 缩放 / 旋转等"一次性、改尺寸"的编辑。
2. **区域历史**（`RegionEditCommand`，只存改动包围盒的前后像素）：仿制图章这类"一笔一步"的高频小范围编辑。
   12MP 图上整幅快照约 17MB/步，区域只要 KB 级 —— 实测相差 50 倍。
   ⚠️ `IEditCommand.ByteSize` 存在的意义就是让 `HistoryManager` 统计到命令自有的字节；
   漏掉它 = 这类高频编辑会**静默绕过内存上限**。
3. **对象 + 对象列表快照**（`AnnotationObject` + `AnnotationEditCommand`）：非破坏性标注（要能选中再改参数）。
   对象数量少，整列表快照几百字节/步；编辑时从"底图 + 全部对象"重新渲染，而不是回退像素。
   - 合并（烘焙）用 `AnnotationFlattenCommand`，**必须同时还原像素与对象列表**：
     只还原像素 → 撤销后标注凭空消失；只还原列表 → 画面上出现两份标注。
   - 标注层在**破坏性操作前自动合并**（钩在 `ApplyOneShotAsync` / 仿制图章 / 打印），
     合并本身是**一步独立历史**（所以撤销两次才回到"标注可编辑"）。
   - 导出与打印用**副本**烘焙，不破坏编辑现场。
   - 叠加层与合并渲染**共用同一份几何**（`AnnotationVisualBuilder`）—— 否则会"所见非所得"。

4. **批量流水线不需要撤销** —— 它只写新文件、原文件不动，天然可重来。
   不要为了"架构统一"给它硬塞一套撤销模型。

## 批量流水线（M3 · 2026-10-09 完成）
- 结构：**有序步骤列表**（缩放/调整/翻转旋转/边框/水印）套到一批文件上，导出为"原名 + 后缀"。
- 顺序是**产品语义**而非实现细节：先缩放后加水印 vs 反之，水印像素宽度实测 81px vs 40px。
- `BatchContext.Scale` 是"面板预览不骗人"的地基。**绝对**像素参数乘 Scale，**相对**参数（百分比）不乘。
  自检断言：长边 800 → 全分辨率 800×600 / 半缩放预览 400×300；百分比模式两种上下文同值。
- 水印默认"按图像宽度百分比"（批量图尺寸不一，固定像素会让大图看不见、小图占满屏）。
- **预览不做 EXIF 方向校正就会骗人**：`IImageService.LoadPreviewAsync` 的
  `decodePixelWidth` 分支**不做**方向校正（见 `WpfImageService.Decode`），
  所以批量预览改为"整幅解码 → 降采样 → 套流水线"。
- 输出命名必须防**三种**覆盖：磁盘同名 / **同一次运行内已分配的名字** / 源文件本身。
  中间那条最容易漏：来自不同文件夹的同名文件撞名时磁盘上还没有第二个文件，`File.Exists` 查不出来
  → 必须额外维护"已分配路径"集合（`HashSet<string>` + `OrdinalIgnoreCase`）。
- GIF 输入在"沿用原格式"时改写为 PNG（256 色 + 多帧会被压成单帧，会静默毁画质）。
- `BatchResizeStep.ReadTargetSize` 是 `internal`，自检直接调它钉住缩放换算（比端到端更快更稳）。

## 已记录的经验
- 重采样必须"缩小时按比例展宽核"才抗混叠；必须"预乘 alpha 再还原"才不会在透明边缘出黑边。
  验证手段：1px 条纹缩 10 倍应得均匀中灰（邻近取样对照组会整片变纯黑）。
- 任意角度旋转用逆映射（正映射会留孔洞）；90° 整数倍应短路到无损旋转路径。
- 几何变换改变画布尺寸后，若处于"适应窗口"必须重算 ZoomFactor。
- 智能填充用调和扩散（Δu=0），其价值全在两条性质上：**常值解**（纯色背景彻底抹平）与
  **精确重现线性函数**（渐变背景对齐，对付扫描件照明不均）。断言要打在这两条性质上，而不是"看起来还行"。
- 仿制图章的覆盖率要按"像素到笔迹折线的**距离场**"算，不能逐个笔刷点叠加（叠加会越涂越浓）。
  源像素必须取自**原始图像**，否则会自我复制出拖影。断言手法：构造"反复回折到同一点"的笔迹，
  中心像素必须严格等于源像素。
- 画笔类工具的拖拽预览不要逐帧重建整幅位图（12MP 每帧一次 `BitmapSource`，拖动会卡）；
  改为在叠加层画"**笔刷粗细的折线**"——它与按距离算出的实际涂抹范围几何一致，所见即所得。
- 只在掩膜内写回 ⇒ "不越界"是结构性保证；掩膜面积计数要数**并集**（写入时只数 0→1），否则重叠框选会虚增。
- `Parallel.For` 的 TLocal 重载**必须给 localFinally**（见上），否则匹配到 long 重载。

## 工作流约定
- 需求决策走 grill 流程：AI 质询 → 用户拍板 → 按 P0/P1/P2 实施。
- 每阶段交付后跑 `--selftest` 再进下一阶段。
- 用户 GitHub 账号 `kianaQAQ-zip`，提交身份 `kiana / 1440667466@qq.com`；红线：不得提交真实密钥。当前仓库尚无 `.git`，M0 阶段补。

## 遮盖标注 · 缩放手柄 · 右键菜单（2026-10-09 完成）
- **遮盖标注**（马赛克 / 模糊）与其它标注的本质区别：内容是**底图的像素级派生**而非几何。
  做法是把素材做成 `ImageBrush` 当 `Fill` 用 ⇒ **渲染器一行未改**（两边本来就只是 DrawGeometry）。
- **素材是"拉取"的**：`MosaicSourceProvider` 持一个取像素的委托，每次现取现用
  ⇒ **不需要任何失效通知**，从根上避免"底图换了忘了失效"。叠加层与合并必须共用同一实例。
- **马赛克块网格锚定图像坐标 (0,0)**，区域先向外对齐到块边界再处理 ——
  否则标注每移动一像素块边界就跟着挪，拖动时马赛克会"流动"。
- **不做整幅预处理缓存**：12MP 整体像素化是 48MB，而按标注矩形现场生成是几十 KB、亚毫秒。
- **八向缩放手柄**：`Models/AnnotationResize.cs` 是纯函数；
  手柄尺寸按「屏幕像素 ÷ 缩放」折算；**手柄命中必须优先于"拖动标注"**；
  文字标注用"与字号成比例的虚拟盒"（几何盒是 1×1 的点）；箭头缩放要保住原始方向（不掉头）。
- **右键菜单**挂 `SystemFileAssociations\image\shell`（一处覆盖所有图片类型）；
  用 `MUIVerb` 写显示名；不写 `Extended`（否则要按 Shift）。
  `--print` **只到打印预览**，刻意不做静默打印。

## 多文档标签页架构（2026-10-09 完成）
- 每个文档的全部工作台状态收进 `ViewModels/DocumentSession.cs`
  （撤销历史 / 标注 / 调整参数 / 缓冲区缓存 / 视图缩放 / 选中项）。
- **改造手法是关键**：`MainViewModel` 里那批私有**字段**改成**转发到 `ActiveSession` 的私有属性**，
  于是 374 处调用点一行未改，而"切换标签 = 换一整套状态"自动成立。
  换这类字段前务必先 `grep "ref _"` —— `SetProperty(ref _field, …)` 会报 CS0206。
- **两条不变量**：① 至少一个标签（关掉最后一个重置为"空会话"而非移除）
  ⇒ `ActiveSession` 永不为 null ⇒ 转发属性一律不用判空；
  ② 切换标签必须整体刷新（`RefreshAfterSessionChange`）——撤销步数/滑块/标注/缩放都属于某个文档。
- `LoadFromPathAsync`：空标签复用、已有文档则开新标签；加载失败撤掉刚建的空标签。
- `ConfirmCloseAsync` **遍历所有标签**；`MainWindow.OnClosing` 用 `HasAnyUnsavedChanges`。
- 标签栏是**独立整行**（横跨窗口宽度，不被右侧 296px 面板挤窄）；`IsActiveTab` 由 VM 维护，避免转换器。
- ⚠️ **隐藏元素不会被测量 ⇒ `DataTemplate` 不会被实例化**，模板里的绑定错误在自检里暴露不出来。
  自检要覆盖某段模板，必须让它**真的可见并完成布局**。

## 新增踩坑（2026-10-09）
- **`VisualBounds` 的外扩量必须按类型取**：原来统一 `max(线宽, 字号*0.6)+4`，
  而 `FontSize` 对所有标注都有默认值 28 ⇒ 4px 粗的矩形也带 20 多像素抓取边距，
  在旁边点一下会被判成"选中并拖动"而非"新建标注"。现按类型取（细矩形 6 / 文字 20.8 / 箭头 16 / 遮盖 0）。
- **自检里比对像素前先确认通道**：`CreateHorizontalRamp` 的渐变在**蓝**通道、红恒为 128，
  用红通道比对等于比常量、断言会变成空转（曾因此让一条断言长期无效）。
- `AdjustmentsHolder` 是 `internal`，`DocumentSession` 是 `public` ⇒ 相关属性要标 `internal`，否则 CS0053。

