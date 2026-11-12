# 构建 WinLaunch（.NET Framework 4.8 + 内置 csc，零安装依赖）
# 用法：
#   powershell -ExecutionPolicy Bypass -File build.ps1                      # 编译 + 部署
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy            # 只编译
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.1       # 指定版本号
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Release             # 编译 + 部署 + 出发布包
#
# 默认部署到 %LOCALAPPDATA%\Programs\WinLaunch，发布包默认写到 %USERPROFILE%\WinLaunch-release。
# 两者都可用 -DeployTo / -ReleaseDir 覆盖。
param(
    [string]$DeployTo = (Join-Path $env:LOCALAPPDATA 'Programs\WinLaunch'),
    [switch]$NoDeploy,
    [string]$Version = '1.0.0',
    [switch]$Release,
    [string]$ReleaseDir = (Join-Path $env:USERPROFILE 'WinLaunch-release')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $fw 'WPF'

if (-not (Test-Path $csc)) { throw "找不到 C# 编译器：$csc" }
if (-not (Test-Path (Join-Path $wpf 'PresentationFramework.dll'))) { throw "找不到 WPF 程序集：$wpf" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号格式应为 x.y.z，例如 1.0.0（当前：$Version）" }

$ico = Join-Path $root 'assets\app.ico'
if (-not (Test-Path $ico)) {
    Write-Output '生成图标…'
    & (Join-Path $root 'tools\make-icon.ps1')
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$buildDir = Join-Path $env:TEMP 'WinLaunch.build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$buildExe = Join-Path $buildDir 'WinLaunch.exe'

# 版本号只在这一处生成：AssemblyInfo.cs 里不写版本，避免两处不一致
$versionCs = Join-Path $buildDir 'WinLaunchVersion.g.cs'
$versionSrc = @"
using System.Reflection;
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
Set-Content -LiteralPath $versionCs -Value $versionSrc -Encoding UTF8

$src = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })
$src += $versionCs

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-',
    "/out:$buildExe",
    "/win32manifest:$root\src\app.manifest",
    "/win32icon:$ico",
    "/r:$fw\System.dll",
    "/r:$fw\System.Core.dll",
    "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.Windows.Forms.dll",
    "/r:$fw\System.Xml.dll",
    "/r:$fw\System.Runtime.Serialization.dll",
    "/r:$wpf\PresentationCore.dll",
    "/r:$wpf\PresentationFramework.dll",
    "/r:$wpf\WindowsBase.dll",
    "/r:$fw\System.Xaml.dll"
) + $src

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$fileVersion = (Get-Item $buildExe).VersionInfo.FileVersion
Write-Output ("编译成功：" + $buildExe + "  (" + [math]::Round((Get-Item $buildExe).Length / 1KB) + " KB, v" + $fileVersion + ")")

if (-not $NoDeploy) {
    $deployDir = [System.IO.Path]::GetFullPath($DeployTo)
    if ($deployDir.TrimEnd('\').StartsWith($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output '警告：部署目录位于源码目录内，不建议从这里直接运行或分发。'
    }
    New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
    $deployExe = Join-Path $deployDir 'WinLaunch.exe'
    $runDeploy = Join-Path $deployDir 'run.cmd'

    try {
        Copy-Item -LiteralPath $buildExe -Destination $deployExe -Force

        # 部署目录的启动脚本（相对自身）
        $runContent = "@echo off`r`nstart `"`" `"%~dp0WinLaunch.exe`"`r`n"
        Set-Content -LiteralPath $runDeploy -Value $runContent -Encoding ASCII

        # dist 只保留兼容入口：重定向到部署目录，不放 exe
        $legacyContent = "@echo off`r`nrem Legacy entry: redirect to the deployed copy.`r`nstart `"`" `"$deployExe`"`r`n"
        Set-Content -LiteralPath (Join-Path $dist 'run.cmd') -Value $legacyContent -Encoding ASCII
        Remove-Item -LiteralPath (Join-Path $dist 'WinLaunch.exe') -Force -ErrorAction SilentlyContinue

        Write-Output ("已部署到：" + $deployExe + "（运行 " + $runDeploy + "）")
    }
    catch {
        Write-Output ("部署失败（如果 WinLaunch 正在运行，请先从托盘退出再构建）：" + $_.Exception.Message)
        Write-Output ("本次编译产物仍在：" + $buildExe)
    }
}

if (-not $Release) { return }

# ---------------- 发布包 ----------------

$outDir = Join-Path $ReleaseDir ("WinLaunch-" + $Version)
Write-Output ("生成发布包：" + $outDir)
if (Test-Path $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$relExe = Join-Path $outDir 'WinLaunch.exe'
Copy-Item -LiteralPath $buildExe -Destination $relExe -Force

foreach ($doc in @('使用说明.md', '发布说明.md')) {
    $p = Join-Path $root ("docs\" + $doc)
    if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $outDir $doc) -Force }
}

# 绿色版压缩包
$zip = Join-Path $outDir ("WinLaunch-" + $Version + "-portable.zip")
$zipItems = @($relExe)
$docZip = Join-Path $outDir '使用说明.md'
if (Test-Path $docZip) { $zipItems += $docZip }
Compress-Archive -LiteralPath $zipItems -DestinationPath $zip -Force
Write-Output ("绿色版压缩包：" + $zip)

# 安装包（需要 Inno Setup 6 的 ISCC.exe）
$iscc = $null
foreach ($cand in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))) {
    if ($cand -and (Test-Path $cand)) { $iscc = $cand; break }
}
$iss = Join-Path $root 'installer\WinLaunch.iss'
if ($iscc -and (Test-Path $iss)) {
    & $iscc "/DMyAppVersion=$Version" "/DMyAppExe=$relExe" "/DMyOutputDir=$outDir" "/O$outDir" $iss
    if ($LASTEXITCODE -ne 0) {
        Write-Output ("安装包编译失败，ISCC 退出码 " + $LASTEXITCODE)
        exit 1
    }
    else { Write-Output ("安装包：" + (Join-Path $outDir ("WinLaunch-" + $Version + "-setup.exe"))) }
}
else {
    Write-Output '跳过安装包：未找到 Inno Setup 6（ISCC.exe）或 installer\WinLaunch.iss'
}

# 校验和
$sums = Join-Path $outDir 'SHA256SUMS.txt'
$lines = @()
foreach ($f in Get-ChildItem $outDir -File | Where-Object { $_.Extension -in @('.exe', '.zip') } | Sort-Object Name) {
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += ($h + '  ' + $f.Name)
}
Set-Content -LiteralPath $sums -Value ($lines -join "`r`n") -Encoding ASCII
Write-Output ("校验和：" + $sums)
$lines | ForEach-Object { Write-Output ("  " + $_) }

# 签名状态提示（不阻止构建）
foreach ($f in Get-ChildItem $outDir -File | Where-Object { $_.Extension -eq '.exe' }) {
    $sig = Get-AuthenticodeSignature -LiteralPath $f.FullName
    Write-Output ("签名状态：" + $f.Name + " → " + $sig.Status)
}
