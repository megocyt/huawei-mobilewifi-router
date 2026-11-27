# 从源码构建静态 Qt（只编 qtbase），供 qt\build.ps1 -Static 链接成单文件 exe。
# 裁剪：-no-icu（省掉 38MB ICU）、-no-opengl（只留 D3D11 RHI）、-no-dbus、-no-pkg-config，
# 只保留 Core/Gui/Widgets/Network 需要的特性。产物安装到 E:\tools\qt6-static。
param(
    [string]$SrcDir = 'E:\tools\qt6-src',
    [string]$Prefix = 'E:\tools\qt6-static',
    [string]$Msys   = 'E:\tools\msys64',
    [int]$Jobs = 0,
    [switch]$Clean,
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'

$ver = '6.12.0'
$tarball = Join-Path $SrcDir "qtbase-everywhere-src-$ver.tar.xz"
$srcRoot = Join-Path $SrcDir "qtbase-everywhere-src-$ver"
$mingwBin = Join-Path $Msys 'mingw64\bin'
# msys 自带的 tar 不穿 Windows 盘符路径，优先用系统 bsdtar（libarchive，支持 xz）
$tar = Join-Path $env:SystemRoot 'system32\tar.exe'
if (-not (Test-Path $tar)) { $tar = Join-Path $Msys 'usr\bin\tar.exe' }

if (-not (Test-Path $tarball)) {
    throw "缺少源码包 $tarball（下载地址见 README 或 https://download.qt.io/official_releases/qt/6.12/6.12.0/submodules/）"
}
if (-not (Test-Path (Join-Path $mingwBin 'g++.exe'))) { throw "未找到 mingw64 工具链: $mingwBin" }
if (-not (Test-Path $tar)) { throw "未找到 tar: $tar" }

if (-not (Test-Path $srcRoot)) {
    Write-Host "解压 $tarball"
    & $tar -xf $tarball -C $SrcDir
    if ($LASTEXITCODE -ne 0) { throw '解压失败' }
}

$bld = Join-Path $srcRoot 'build-static'
if ($Clean -and (Test-Path $bld)) {
    Remove-Item -Recurse -Force $bld
    Write-Host "已清理 $bld"
}
New-Item -ItemType Directory -Force -Path $bld | Out-Null

$env:Path = "$mingwBin;" + $env:Path
$cmake = Join-Path $mingwBin 'cmake.exe'
if (-not (Test-Path $cmake)) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }

# configure/build 的原生工具会往 stderr 写 CMake 警告，Stop 模式会把它当致命错误中断脚本，
# 这里整体放宽为 Continue，退出码仍由 $LASTEXITCODE 判断
#
# 全部第三方依赖都走 Qt 自带副本（-qt-zlib / no-feature-system-*），否则 MSYS2 的
# mingw64 前缀会被 CMake 命中，exe 又会拖出 libz-1/libpng16/libpcre2-16/libb2-1 等 DLL。
# brotli 与 textmarkdownreader 直接关掉：程序不用 br 压缩，也不用 markdown，
# 顺带避开系统 md4c.h 与 Qt 自带 md4c 常量不一致导致的 static_assert 编译失败。
$qtFlags = @(
    '-static', '-static-runtime', '-release',
    '-no-icu', '-no-opengl', '-no-dbus', '-no-pkg-config',
    '-qt-zlib', '-no-zstd',
    '-no-feature-brotli',
    '-no-feature-system-png', '-no-feature-system-pcre2',
    '-no-feature-system-libb2', '-no-feature-textmarkdownreader'
)
$okFile = Join-Path $bld '.configured'
$stamp = "static2 prefix=$Prefix flags=$($qtFlags -join ' ')"
$configured = (Test-Path $okFile) -and ((Get-Content $okFile -Raw).Trim() -eq $stamp)
if (-not $configured) {
    Write-Host "配置静态 Qt -> $Prefix"
    $cfgArgs = @((Join-Path $srcRoot 'configure.bat'), '-prefix', "`"$Prefix`"") + $qtFlags +
               @('-nomake', 'examples', '-nomake', 'tests')
    $ea = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location $bld
    try {
        & cmd /c ($cfgArgs -join ' ') 2>&1 | Out-Host
        $code = $LASTEXITCODE
    } finally { Pop-Location; $ErrorActionPreference = $ea }
    if ($code -ne 0) { throw "configure 失败: $code" }
    Set-Content -Path $okFile -Value $stamp
}

$joint = if ($Jobs -gt 0) { $Jobs } else { [Environment]::ProcessorCount }
Write-Host "编译静态 Qt（$joint 线程，约 20~35 分钟）"
$ea = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & $cmake --build $bld --parallel $joint 2>&1 | Out-Host; $code = $LASTEXITCODE }
finally { $ErrorActionPreference = $ea }
if ($code -ne 0) { throw "编译失败: $code" }

if (-not $NoInstall) {
    Write-Host "安装到 $Prefix"
    $ea = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $cmake --install $bld 2>&1 | Out-Host; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $ea }
    if ($code -ne 0) { throw "安装失败: $code" }
    Write-Host "OK -> $Prefix"
}