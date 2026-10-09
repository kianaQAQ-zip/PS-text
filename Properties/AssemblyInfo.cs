using System.Reflection;
using System.Runtime.InteropServices;

// ============================================================================
//  程序集信息
//
//  为什么必须存在这个文件：
//    本项目是**传统（非 SDK）csproj**。MSBuild 自动生成程序集信息
//    （GenerateAssemblyInfo）只对 SDK 风格的工程生效，传统工程里的
//    <AssemblyTitle> / <AssemblyVersion> 等属性**完全不起作用** ——
//    编译能过、也不报错，但 exe 的属性里版本一直是 0.0.0.0。
//    因此这里显式声明，并把版本号作为**单一来源**：
//    发行包命名（build/package.py）也读这里。
//
//  改版本号时只改下面这几行，不要另外在 csproj 里写一遍。
// ============================================================================

[assembly: AssemblyTitle("PS-text 图片编辑器")]
[assembly: AssemblyProduct("PS-text")]
[assembly: AssemblyCompany("PS-text")]
[assembly: AssemblyCopyright("Copyright © 2025")]
[assembly: AssemblyDescription("轻量办公图片处理工具：批量、水印、标注、修补、打印（Windows 7 兼容）")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

// 本程序不向 COM 暴露任何类型，保持 false 可以减少注册表与打包环节的干扰。
[assembly: ComVisible(false)]
