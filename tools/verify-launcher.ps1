<#
.SYNOPSIS
    验证「启动 PS-text.bat」的运行环境判定分支。

.DESCRIPTION
    为什么需要这个脚本：启动器里最关键的一行是 .NET Framework 4.8 的检测，
    而它偏偏是最容易写错、又最难发现写错的地方 —— 写错的表现只是
    "本该提示装 4.8 的用户，双击后什么也没发生"，只有在没有 4.8 的机器上才会暴露。

    本机不允许直接调 reg.exe（被工具层黑名单拦截），所以这里用**伪造的 reg 替身**
    顶替 PATH 里的 reg，把启动器的三条分支都驱动一遍：
        · Version = 4.8.09221   -> 应当放行（退出码 0）
        · Version = 4.7.02556   -> 应当拦截（退出码 1）
        · 注册表项不存在         -> 应当拦截（退出码 1）

    替身在每个分支里都会**同时打印** Release 的十六进制值（0x82309 这种）。
    如果启动器哪天改回"拿 Release 比大小"，就会在只装 4.7.2 的机器上误判 ——
    这正是中间那条用例存在的意义。

    必须在真实的发行包产物上跑（而不是 packaging/ 里的 UTF-8 源文件），
    因为中文乱码这类问题只有 GBK 版才会出现。

    结果同时写进 $LogPath，便于在没有控制台回显的环境里取结果。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\verify-launcher.ps1
#>
[CmdletBinding()]
param(
    [string]$DistRoot = (Join-Path $PSScriptRoot '..\build\dist'),
    [string]$Harness  = (Join-Path $PSScriptRoot '..\build\tmp\launcher-test'),
    [string]$LogPath  = (Join-Path $PSScriptRoot '..\build\tmp\launcher-verify.log')
)

$ErrorActionPreference = 'Stop'

# 用 Write-Output 而不是 Write-Host：Write-Host 走信息流，2>&1 抓不到，
# 脚本被别的程序调用时会"跑了但没输出"。
$script:lines = New-Object System.Collections.ArrayList
$script:failures = 0

function Emit($text) {
    Write-Output $text
    [void]$script:lines.Add([string]$text)
}

function Step($text) { Emit $text }
function Pass($text) { Emit ("  [OK]   " + $text) }
function Fail($text) { Emit ("  [FAIL] " + $text); $script:failures++ }

function Save-Log {
    $directory = Split-Path -Parent $LogPath
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($LogPath, ($script:lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
}

# ---------- 1. 找到发行包 ----------
$package = Get-ChildItem -Path $DistRoot -Directory -Filter 'PS-text-*-win7-portable' |
           Sort-Object Name -Descending | Select-Object -First 1

if (-not $package) {
    Step '找不到发行包目录，请先运行 tools/package.py'
    Save-Log
    exit 1
}

Step ('发行包：' + $package.FullName)

$launcher = Join-Path $package.FullName '启动 PS-text.bat'
$exe      = Join-Path $package.FullName 'PS-text.exe'

if (-not (Test-Path $launcher)) { Fail '发行包里没有启动器'; Save-Log; exit 1 }
if (-not (Test-Path $exe))      { Fail '发行包里没有 PS-text.exe'; Save-Log; exit 1 }

# ---------- 2. 搭测试场（ASCII 名字，避开命令行传中文名的编码问题）----------
# 用 .NET 的 Directory.Delete 而不是 Remove-Item -Recurse：
# 某些受限环境会把 Remove-Item 换成"先丢进回收站"的包装，递归删除会失败。
if (Test-Path $Harness) { [System.IO.Directory]::Delete($Harness, $true) }
New-Item -ItemType Directory -Path (Join-Path $Harness 'fakebin') -Force | Out-Null

Copy-Item $launcher (Join-Path $Harness 'launcher.bat') -Force
Copy-Item $exe      (Join-Path $Harness 'PS-text.exe') -Force

$fakeReg = @'
@echo off
if /i "%FAKE_REG_MODE%"=="missing" (
    echo 错误: 系统找不到指定的注册表项或值。
    exit /b 1
)
echo HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full
echo     Release    REG_DWORD    0x%FAKE_REG_RELEASE%
echo     Version    REG_SZ    %FAKE_REG_VERSION%
exit /b 0
'@

$fakeRegPath = Join-Path $Harness 'fakebin\reg.bat'
[System.IO.File]::WriteAllText($fakeRegPath, $fakeReg, [System.Text.Encoding]::GetEncoding(936))

# ---------- 3. 逐条驱动分支 ----------
$env:Path = (Join-Path $Harness 'fakebin') + ';' + $env:Path

$cases = @(
    @{ Name = '装了 4.8';       Mode = 'ok';      Version = '4.8.09221'; Release = '82309';  ExpectExit = 0; ExpectText = '[OK]' },
    @{ Name = '只有 4.7.2';     Mode = 'ok';      Version = '4.7.02556'; Release = '461808'; ExpectExit = 1; ExpectText = '[FAIL]' },
    @{ Name = '没有该注册表项'; Mode = 'missing'; Version = '';          Release = '';       ExpectExit = 1; ExpectText = '[FAIL]' }
)

foreach ($case in $cases) {
    $env:FAKE_REG_MODE    = $case.Mode
    $env:FAKE_REG_VERSION = $case.Version
    $env:FAKE_REG_RELEASE = $case.Release

    $shown = $case.Version
    if ([string]::IsNullOrEmpty($shown)) { $shown = '(无)' }

    Step ('--- ' + $case.Name + '（伪造 Version=' + $shown + '）---')

    $output = (& (Join-Path $Harness 'launcher.bat') /check 2>&1 | Out-String).Trim()
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne $case.ExpectExit) {
        Fail ('退出码应为 ' + $case.ExpectExit + '，实际 ' + $exitCode + '；输出：' + $output)
        continue
    }

    if ($output -notlike ('*' + $case.ExpectText + '*')) {
        Fail ('输出里应包含 ' + $case.ExpectText + '，实际：' + $output)
        continue
    }

    Pass ('退出码 ' + $exitCode + '，输出 ' + $output)

    if ($output -match '[\u4e00-\u9fff]') {
        Pass '中文显示正常（GBK 转换生效）'
    } else {
        Fail '输出里没有中文，可能 GBK 转换失败'
    }
}

# ---------- 4. 启动器源码静态检查 ----------
Step '--- 启动器源码静态检查 ---'
$text = [System.IO.File]::ReadAllText($launcher, [System.Text.Encoding]::GetEncoding(936))

if ($text -match 'findstr /c:"4\.8\."') { Pass '用 Version 字符串匹配判定 4.8' }
else { Fail '没有找到 findstr /c:"4.8." 判定' }

if ($text -match '(GEQ|LSS|GTR|LEQ)\s+528040') { Fail '用了 reg 的 Release 做数值比较（十六进制陷阱）' }
else { Pass '没有用 Release 做数值比较' }

if ($text -match 'reg query') { Pass '确实做了运行时检测' }
else { Fail '没有 reg query' }

# ---------- 5. 收尾 ----------
Step ''
if ($script:failures -eq 0) {
    Step '启动器验证通过。'
    Save-Log
    exit 0
}

Step ('启动器验证失败：' + $script:failures + ' 项')
Save-Log
exit 1
