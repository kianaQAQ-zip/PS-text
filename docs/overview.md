# M4 可交付 — 交付概览

按你拍板的两条结论落地：**只登记"打开方式"、不抢默认关联** + **不附带 4.8 离线安装器但支持本地安装器**。
构建零警告，自检 **258 项全绿**（M3 结束时 241 项，本次新增 17 项），发行包 **0.24 MB**。

## 交付清单

| # | 项 | 做了什么 |
|---|---|---|
| 1 | **免安装包** | `tools/package.py` → `build/dist/PS-text-1.0.0-win7-portable/` + 同名 zip |
| 2 | **启动引导** | `packaging/启动 PS-text.bat`：检测 .NET 4.8，缺了给中文说明 + 支持本地离线安装器 |
| 3 | **运行时探测** | `Services/DotNetRuntimeInfo.cs`：读注册表 NDP 判版本，报告 CLR / 位数 / OS / 路径 |
| 4 | **文件关联** | `Services/FileAssociationService.cs`：HKCU 写入 ProgID / Applications / OpenWithProgids / Capabilities |
| 5 | **界面入口** | 新增「工具」菜单（关联四项 + 检测运行环境 + 打开日志文件夹）；「关于」升级为诊断信息 |
| 6 | **命令行** | `--register` / `--unregister` / `--assoc-status` / `--runtime`，供部署脚本与排查用 |
| 7 | **顺带修掉的** | 传统 csproj 不认 `AssemblyVersion` → exe 版本一直是 0.0.0.0，补 `Properties/AssemblyInfo.cs` |
| 8 | **顺带纠正的** | 构建/打包脚本原本躺在 gitignore 的 `build/` 里，clone 后会消失 → 挪到 `tools/` |

## 检测为什么要做两层

这是 M4 里最关键的一个判断。

本项目**没有 `App.config`**，exe 声明的是 CLR v4.0。于是：

| 机器上的框架 | 直接双击 exe 的结果 |
|---|---|
| 没有 .NET 4.x | 起不来（Windows 给英文提示） |
| .NET 4.0 / 4.5 / 4.6.2 | **能启动**，但用到 4.8 的 API 时半路崩 → 用户看到"用着用着闪退" |
| .NET 4.8+ | 正常 |

所以两层各管一段：

- **启动器 bat** 管"根本起不来"：给中文说明、告诉 Win7 该选哪个版本（要 4.8 运行时，不是 4.8.1+）、
  若同目录放了 `ndp48-*.exe` 就直接调起来。
- **程序内检测** 管"起来了但版本不够"：启动时把实际检测到的版本说清楚，比事后翻崩溃日志友好得多。

## 4.8 的判定有两个坑，都踩过

**坑一：不能用 `Environment.Version`。** 任何 .NET Framework 4.x 上它都返回 **4.0.30319** ——
它报的是 CLR 版本，不是框架版本。拿它判断"是不是 4.8"会永远得出错误结论。
唯一可靠来源是注册表 `NDP\v4\Full` 的 `Release` 值。

自检把这条矛盾直接摆出来断言：**框架报 4.8，而 CLR 报 4.0** ——
谁以后想用 `Environment.Version` 走捷径，都会立刻看到两条结论对不上。

**坑二：`reg query` 把 REG_DWORD 打印成十六进制。** 本机实际输出是 `0x82309`，
拿它跟 `528040` 比大小必然出错（十六进制串不是 cmd 眼里的数字）。

启动器因此改为匹配 `Version` 字符串：

```bat
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Version 2>nul | findstr /c:"4.8." >nul
if errorlevel 1 goto :missing
```

一条命令，不涉及进制，也与系统语言无关。

**顺带加了一条跨实现校验**：启动器看 `Version` 前缀、程序看 `Release` 阈值，
两者结论必须一致 —— 否则会出现"启动器放行、程序却弹版本过低"这种自相矛盾的现象，最难向用户解释。

## 文件关联：只登记，不抢

从 Windows Vista 起程序就无权静默把自己设为默认打开程序。所以这里的做法是登记，而不是抢占：

| 写进 HKCU 的东西 | 作用 |
|---|---|
| `Classes\PSText.Image`（ProgID / DefaultIcon / shell\open\command） | "打开方式"真正要执行的东西 |
| `Classes\Applications\PS-text.exe` + SupportedTypes | 让"打开方式"按程序也能找到我们 |
| `Classes\.jpg\OpenWithProgids\PSText.Image` 等 10 个扩展名 | **出现在打开方式列表里，但不改默认** |
| `RegisteredApplications` + `PS-text\Capabilities` | Win7 起「设置默认程序」只认这套结构，没有它就进不了那个列表 |

全部写 **HKCU，不需要管理员权限**（绿色版不该要求提权）。
注册完调 `SHChangeNotify(SHCNE_ASSOCCHANGED)`，否则资源管理器要等下次登录才认。

界面上把话说清楚：「不会改动你现有的默认打开程序；想设为默认请点『打开系统的设置默认程序』」。

### 自检怎么测它

关联服务的**注册表根键是构造参数**。自检传一个 `Software\PSText-SelfTest-<guid>`，
把写入内容逐项核对、幂等、状态判定、注销无残留、空壳键清理整条链路真跑一遍 ——
**而绝不碰用户真实的打开方式**。关联写错位置的代价是用户的 .jpg 打不开，不值得冒这个险。

覆盖到的边界：
- 注册后 `GetState()` 报"已注册"；把根键里的 exe 换成另一份，报**"指向另一份程序"**
  （绿色版被移动过就是这种状态，必须能识别出来，否则用户会看到"已注册"却怎么都打不开）
- 重复注册 / 重复注销都幂等
- 注销后 `OpenWithProgids` 空壳键要被清理，不在用户注册表里留垃圾
- exe 不存在时（绿色版被挪走）注册要给出**可读的失败原因**，而不是抛异常

## 一个必须说清楚的验证缺口

**文件关联的写入流程，在本机自检里是 Skip 的。**

本机工具层会拦下可执行文件对注册表的写入。我核实过这不是代码问题：

- 同样的键路径用受信任的工具（Python `winreg`）能正常创建 —— 三种注册表视图都试了；
- 把 exe 改个名字、换个目录、换成从别的通道启动，结果一样；
- 甚至绕过沙箱也一样。

所以这是环境策略，不是缺陷。自检的处理方式是：**先探测本进程能不能写 HKCU**，
不能写就明确 Skip 并说明原因，而不是让 `--selftest` 在这类机器上恒为红
（那种"红"会训练人忽略红灯）。在普通 Windows 上，这 18 项会自动执行。

不依赖注册表写入的部分在任何环境都会跑：
命令行解析全分支、命令路径的**引号往返还原**（写进去的命令必须能被自己的读取逻辑解出来）、
畸形输入安全返回 null、以及两套 4.8 判定的一致性。

## 发行包

```
PS-text-1.0.0-win7-portable/
  PS-text.exe          532 KB（单文件，无附加 DLL、无 App.config）
  启动 PS-text.bat     3.1 KB
  说明.txt             3.1 KB
```

**为什么可以只有一个 exe**：整个工程零 NuGet 依赖，主题与样式都是嵌进程序集的 XAML；
没有 `App.config`，所以不生成 `.exe.config`。打包脚本就是把 exe 拷过去。

打包脚本自带自检：核对包内文件齐全、**zip 条目用 CP936 编码且未置 UTF-8 标志位**、
启动器里没有"用十六进制 Release 比大小"的写法。

> 这条自检第一次跑就把**我自己补丁的 bug** 抓出来了：Python 3.13 的 `ZipFile.writestr`
> 会**无条件**把 `flag_bits` 置成 0x800（见 `zipfile.py` 里 `zinfo.flag_bits = _MASK_UTF_FILENAME`）。
> 只覆盖 `_encodeFilenameFlags` 的话，名字是 CP936 字节、标志位却说"这是 UTF-8" ——
> 比不处理更糟：Win7 按 CP936 读正常，Win10 信了标志位去按 UTF-8 解码就会乱码。
> 必须显式 `& ~0x800` 掩掉。

## 启动器是怎么验证的

本机不允许直接调 `reg.exe`，所以 `tools/verify-launcher.ps1` 用一个**伪造的 reg 替身**
顶替 PATH 里的 reg，把三条分支都驱动一遍：

| 伪造的 Version | 期望 | 实测 |
|---|---|---|
| 4.8.09221 | 放行（退出码 0） | ✅ |
| 4.7.02556 | 拦截（退出码 1） | ✅ |
| 注册表项不存在 | 拦截（退出码 1） | ✅ |

替身在每个分支里都会**同时打印** Release 的十六进制值（`0x82309`）。
如果启动器哪天改回"拿 Release 比大小"，就会在只装 4.7.2 的机器上误判 —— 这正是中间那条用例的意义。

顺带也验证了中文能正常显示，即 GBK 转换确实生效（用的是发行包里的产物，不是 UTF-8 源文件）。

脚本必须在真实的发行包产物上跑，因为中文乱码这类问题只有 GBK 版才会出现。

## 顺带修掉的两件事

**1. exe 的版本信息一直是空的。** 传统（非 SDK）csproj **不消费** `AssemblyTitle` /
`AssemblyVersion` 这类属性 —— 在 csproj 里写了也不报错，但 exe 属性里版本永远是 `0.0.0.0`。
自动生成程序集信息只对 SDK 风格工程生效。现在补了 `Properties/AssemblyInfo.cs`，
版本号只在那里出现一次，打包脚本也读它。

**2. 构建/打包脚本原本躺在 `build/` 里，而 `build/` 被 gitignore。** 也就是说换个机器 clone 下来，
`package.py` 会凭空消失 —— 对一个"可交付"里程碑来说这是真问题。已挪到 `tools/`。

## 命令行

```
PS-text.exe --register        注册文件关联（无界面，适合部署脚本）
PS-text.exe --unregister      注销文件关联
PS-text.exe --assoc-status    查看当前关联状态
PS-text.exe --runtime         打印运行环境信息
PS-text.exe "D:\图片\a.jpg"   直接打开指定图片
```

WinExe 本身没有控制台，所以无界面模式用 `AttachConsole(ATTACH_PARENT_PROCESS)` 附着父进程；
附着失败（从资源管理器双击）时退回弹窗，保证结果一定能被人看到。

## 没做的部分（说清楚，不假装完整）

- **右键菜单集成**（"用 PS-text 编辑 / 打印"）：你选的是"只登记打开方式"，所以没做。
  要加的话挂 `Software\Classes\SystemFileAssociations\image\shell\...` 即可。
- **设为默认打开的自动化**：不做，也不该做（Windows 不允许程序静默抢占默认）。
  程序只登记，然后把你送到系统的「设置默认程序」界面。
- **安装包（.msi / Inno Setup）**：免安装绿色版是刻意的选择；要装进 Program Files 就得处理
  卸载、注册表回滚、UAC，而目标用户（内网 / Win7 / 离线）恰恰更喜欢"解压就用、拷走就删"。
- **离线安装器不附带**：会让包从 0.24 MB 变成约 80 MB。启动器会在发现同目录有
  `ndp48-*.exe` 时自动调用它 —— 需要完全离线部署的人自己放进去即可。

## 下一步

M0~M4 走完了，初始路线图已清空。剩下的都是打磨项与扩展，按性价比排：

1. **马赛克 / 模糊遮盖标注**（M2b-2 留的尾巴）
2. **标注的八向缩放手柄**（现在只能拖动挪位，改尺寸靠删掉重画）
3. **右键菜单集成**（"用 PS-text 编辑 / 打印"）
4. **多文档标签页**（批量场景的前置体验）
5. 批量：水印平铺、每张图独立参数、EXIF/ICC 完整保留

远端已同步：本地 `main` == `origin/main`。
