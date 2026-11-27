# Qt 版构建脚本：定位 Qt 套件 -> cmake -G Ninja 配置 -> 编译 -> windeployqt 收集运行库。
# 版本号单一真源仍是仓库根 VERSION（CMakeLists.txt 读出后生成 version.h / app.rc），
# 构建成功后同步 README 版本徽标。可选 -Tag：为该版本创建本地 git 附注标签。
param(
    [string]$QtDir,
    [string]$BuildType = 'Release',
    [switch]$Clean,
    [switch]$Tag
)
$ErrorActionPreference = 'Stop'

function Test-LaunchedByDoubleClick {
    try {
        $ppid = [int](Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$PID").ParentProcessId
        for ($i = 0; $i -lt 2 -and $ppid -gt 0; $i++) {
            $p = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$ppid" -ErrorAction SilentlyContinue
            if (-not $p) { break }
            if ($p.Name -eq 'explorer.exe') { return $true }
            $ppid = [int]$p.ParentProcessId
        }
        return $false
    } catch { return $false }
}

function Wait-IfDoubleClick {
    $interactive = -not ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected)
    if ($interactive -and (Test-LaunchedByDoubleClick)) {
        Write-Host ''
        Write-Host '（双击启动）构建流程已结束，按任意键关闭窗口...'
        try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Read-Host | Out-Null }
    }
}

# 按 PE 导入表递归把套件里的依赖 DLL 拷到产物目录
function Copy-MingwDeps {
    param([string]$Dir, [string]$KitBin)
    $objdump = Join-Path $KitBin 'objdump.exe'
    if (-not (Test-Path $objdump)) { return }
    $queue = New-Object System.Collections.Queue
    $queue.Enqueue('HwWidgetQt.exe')
    Get-ChildItem -Path $Dir -Filter '*.dll' | ForEach-Object { $queue.Enqueue($_.Name) }
    $seen = @{}
    while ($queue.Count -gt 0) {
        $name = [string]$queue.Dequeue()
        $key = $name.ToLower()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $path = Join-Path $Dir $name
        if (-not (Test-Path $path)) { continue }
        $deps = & $objdump -p $path 2>$null | Select-String 'DLL Name:' |
                ForEach-Object { ($_.Line -replace '.*DLL Name:\s*', '').Trim() }
        foreach ($d in $deps) {
            $dk = $d.ToLower()
            if ($seen.ContainsKey($dk)) { continue }
            if (Test-Path (Join-Path $Dir $d)) { $queue.Enqueue($d); continue }
            $src = Join-Path $KitBin $d
            if (Test-Path $src) { Copy-Item $src $Dir -Force; $queue.Enqueue($d) }
        }
    }
}

# 依次尝试：-QtDir 参数 -> $env:QTDIR -> C:\Qt\6.*\mingw_64 -> C:\Qt\6.*\msvc*_64 -> MSYS2 mingw64
function Resolve-QtKit {
    param([string]$Explicit)
    $cands = @()
    if ($Explicit) { $cands += $Explicit }
    if ($env:QTDIR) { $cands += $env:QTDIR }
    foreach ($pat in @('C:\Qt\6.*\mingw_64', 'C:\Qt\6.*\msvc2019_64', 'C:\Qt\6.*\msvc2022_64',
                       'D:\Qt\6.*\mingw_64', 'D:\Qt\6.*\msvc2022_64')) {
        $cands += (Get-ChildItem -Path $pat -Directory -ErrorAction SilentlyContinue |
                   Sort-Object Name -Descending | Select-Object -ExpandProperty FullName)
    }
    if (Test-Path 'E:\tools\msys64\mingw64\bin\qmake6.exe') { $cands += 'E:\tools\msys64\mingw64' }
    foreach ($c in $cands) {
        if (-not $c) { continue }
        if ((Test-Path (Join-Path $c 'bin\qmake6.exe')) -or (Test-Path (Join-Path $c 'bin\qmake.exe'))) { return $c }
    }
    return $null
}

try {
    $root  = Split-Path -Parent $PSScriptRoot           # qt -> 仓库根
    $srcQ  = $PSScriptRoot                              # 源码根（含 CMakeLists.txt）
    $build = Join-Path $srcQ 'build'

    $kit = Resolve-QtKit -Explicit $QtDir
    if (-not $kit) {
        throw "未找到 Qt 6 套件。请用 -QtDir <路径> 指定（例如 C:\Qt\6.8.1\mingw_64），或设置 `$env:QTDIR。"
    }
    $kitBin = Join-Path $kit 'bin'
    Write-Host "Qt 套件: $kit"

    # ---- 版本号 ----
    $verFile = Join-Path $root 'VERSION'
    $ver = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '0.0.0' }

    # ---- 工具链路径：优先用套件自带（MSYS2 布局），否则回退 PATH ----
    $msys = Split-Path -Parent (Split-Path -Parent $kit)     # E:\tools\msys64
    $env:Path = "$kitBin;" + $env:Path
    if (Test-Path (Join-Path $msys 'usr\bin\cmake.exe')) {
        $env:Path = (Join-Path $msys 'usr\bin') + ';' + $env:Path
    }
    $cmake = (Get-Command cmake -ErrorAction SilentlyContinue)
    $ninja = (Get-Command ninja -ErrorAction SilentlyContinue)
    if (-not $cmake) { throw "未找到 cmake。请安装 CMake 并加入 PATH，或用 Qt Creator 自带版本。" }
    if (-not $ninja) {
        $n = Join-Path $kitBin 'ninja.exe'
        if (Test-Path $n) { $ninja = $n } else { throw "未找到 ninja。请安装 Ninja 并加入 PATH。" }
    }
    if ($kit -like '*msys64*' -and (Test-Path (Join-Path $msys 'usr\bin\g++.exe'))) {
        $env:Path = (Join-Path $kitBin) + ';' + $env:Path
    }
    $ninjaExe = if ($ninja -is [string]) { $ninja } else { $ninja.Source }
    Write-Host "cmake: $($cmake.Source)"
    Write-Host "ninja: $ninjaExe"

    if ($Clean -and (Test-Path $build)) {
        Remove-Item -Recurse -Force $build
        Write-Host "已清理 $build"
    }
    New-Item -ItemType Directory -Force -Path $build | Out-Null

    # ---- 配置 ----
    $cfgArgs = @('-S', $srcQ, '-B', $build, "-DCMAKE_BUILD_TYPE=$BuildType", "-DCMAKE_PREFIX_PATH=$kit")
    if ($ninjaExe) { $cfgArgs += @('-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$ninjaExe") }
    $gxx = Join-Path $kitBin 'g++.exe'
    if (Test-Path $gxx) { $cfgArgs += "-DCMAKE_CXX_COMPILER=$gxx" }
    & $cmake.Source @cfgArgs
    if ($LASTEXITCODE -ne 0) { throw "cmake 配置失败: $LASTEXITCODE" }

    # ---- 编译 ----
    & $cmake.Source --build $build --config $BuildType
    if ($LASTEXITCODE -ne 0) { throw "编译失败: $LASTEXITCODE" }

    $exe = Join-Path $build 'HwWidgetQt.exe'
    if (-not (Test-Path $exe)) { throw "未找到产物 $exe" }

    # ---- 收集运行库（windeployqt + 递归补齐依赖 DLL）----
    $wdq = Join-Path $kitBin 'windeployqt.exe'
    if (Test-Path $wdq) {
        # windeployqt 会向 stderr 写“缺少 dxcompiler.dll”之类的提示，
        # 统一收进 stdout 并在局部放宽 ErrorActionPreference，避免被当成致命错误
        $ea2 = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { (& $wdq --release --no-translations --no-system-d3d-compiler --no-opengl-sw $exe 2>&1) | Out-Host }
        finally { $ErrorActionPreference = $ea2 }
        if ($LASTEXITCODE -ne 0) { Write-Host "windeployqt 返回 $LASTEXITCODE（可忽略）" }
    } else {
        Write-Host "未找到 windeployqt，跳过运行库收集（需自行保证 Qt DLL 可用）"
    }
    # MSYS2 的 Qt 依赖 gcc 运行时与 ICU/zlib/zstd 等 mingw64 DLL，windeployqt 不会拷，
    # 这里按 PE 导入表递归补齐（套件里找不到的就是系统 DLL，直接跳过）
    Copy-MingwDeps -Dir $build -KitBin $kitBin

    # ---- 同步 README 版本徽标与“当前版本”文字 ----
    $readme = Join-Path $root 'README.md'
    if (Test-Path $readme) {
        $r = Get-Content $readme -Raw -Encoding UTF8
        $r = $r -replace 'https://img\.shields\.io/badge/version-[^-]+-', "https://img.shields.io/badge/version-$ver-"
        $r = $r -replace '(?<=当前版本：`)[^`]+(?=`)', $ver
        [IO.File]::WriteAllText($readme, $r, (New-Object Text.UTF8Encoding($false)))
    }

    # ---- 可选打标签 ----
    if ($Tag) {
        Push-Location $root
        $ea = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $null = & git rev-parse -q --verify "refs/tags/v$ver" 2>$null
            if ($LASTEXITCODE -ne 0) {
                & git -c user.name="huawei-widget" -c user.email="huawei-widget@local" tag -a "v$ver" -m "release $ver" 2>$null
                if ($LASTEXITCODE -eq 0) { Write-Host "已创建 git 标签 v$ver（本地，未 push）" }
                else { Write-Host "打标签失败 (git exit $LASTEXITCODE)" }
            } else {
                Write-Host "git 标签 v$ver 已存在，跳过"
            }
        } finally { $ErrorActionPreference = $ea; Pop-Location }
    }

    Write-Host "OK (v$ver) -> $exe"
    $script:buildOk = $true
}
catch {
    Write-Host "构建失败: $_"
    $script:buildOk = $false
}
finally {
    Wait-IfDoubleClick
}
# 构建过程中的原生工具（cmake/windeployqt）会往 stderr 写过提示，
# 显式给出退出码，避免调用方（含 CI、双击）误判为失败
if ($script:buildOk) { exit 0 } else { exit 1 }