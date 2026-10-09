@echo off
rem ============================================================
rem  PS-text 启动器
rem
rem  为什么不直接双击 PS-text.exe：
rem    本程序需要 .NET Framework 4.8，而 Windows 7 SP1 出厂并不带它。
rem    直接双击 exe 时，系统给的提示是英文的，也不会告诉你去哪装、该装哪个版本。
rem    这个启动器负责：检测 4.8 -> 缺了就给出中文说明与安装引导。
rem
rem  用法：
rem    启动 PS-text.bat              正常启动
rem    启动 PS-text.bat /check       只检测运行环境，不启动（排查问题时用）
rem    启动 PS-text.bat "D:\a.jpg"   启动并直接打开指定图片
rem ============================================================

setlocal
title PS-text
set "APP=%~dp0PS-text.exe"

set "CHECKONLY="
if /i "%~1"=="/check" set "CHECKONLY=1"

rem ---- 检测 .NET Framework 4.8 ----
rem 不要用 reg query 取 Release 去做数值比较：reg 把 REG_DWORD 打印成十六进制
rem （例如 0x82309），拿去跟 528040 比大小必然得出错误结论。
rem 这里改为匹配 Version 字符串（形如 4.8.09221），一条 findstr 足够，且与系统语言无关。
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Version 2>nul | findstr /c:"4.8." >nul
if errorlevel 1 goto :missing

if defined CHECKONLY (
    echo [OK] 已检测到 .NET Framework 4.8 或更高版本。
    echo [OK] 程序位置: %APP%
    if exist "%APP%" (echo [OK] 程序文件存在。) else (echo [FAIL] 找不到 PS-text.exe，请确认已完整解压。)
    exit /b 0
)

if not exist "%APP%" goto :noapp

start "" "%APP%" %*
exit /b 0

:noapp
echo.
echo   [错误] 找不到 PS-text.exe
echo.
echo   请先把压缩包 **完整解压** 到一个文件夹，再运行本启动器。
echo   直接双击压缩包里的文件运行会失败。
echo.
pause
exit /b 1

:missing
if defined CHECKONLY (
    echo [FAIL] 未检测到 .NET Framework 4.8 或更高版本。
    exit /b 1
)

echo.
echo   ============================================================
echo     PS-text 需要 .NET Framework 4.8，当前电脑上没有检测到。
echo   ============================================================
echo.
echo     安装方法（任选一种）：
echo.
echo     1) 联网安装（推荐）
echo        https://dotnet.microsoft.com/download/dotnet-framework/net48
echo        打开后下载 ".NET Framework 4.8 运行时"，双击安装，装完再运行本程序。
echo.
echo        注意：Windows 7 SP1 请选 4.8 运行时，
echo              4.8.1 及以上版本不支持 Windows 7。
echo.
echo     2) 离线安装
echo        把 ndp48-x86-x64-allos-enu.exe 放到本文件夹里，
echo        再双击本启动器，会自动调用它。
echo.

if exist "%~dp0ndp48-x86-x64-allos-enu.exe" goto :localinstaller
if exist "%~dp0ndp48-x86-x64-allos-chs.exe" goto :localinstaller

echo     即将为你打开下载页面...
echo.
pause
start "" "https://dotnet.microsoft.com/download/dotnet-framework/net48"
exit /b 1

:localinstaller
echo     检测到本文件夹里有离线安装包，正在启动安装程序...
echo.
for %%f in ("%~dp0ndp48-x86-x64-allos-enu.exe" "%~dp0ndp48-x86-x64-allos-chs.exe") do (
    if exist "%%~f" (
        start /wait "" "%%~f"
        goto :afterinstall
    )
)
exit /b 1

:afterinstall
echo.
echo     安装程序已结束。若安装成功，请重新运行本启动器。
echo.
pause
exit /b 0
