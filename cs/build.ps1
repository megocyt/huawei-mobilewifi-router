# 用 Windows 自带的 .NET Framework csc 编译，产物 HwWidget.exe 免安装、零外部依赖
# 版本号单一真源为仓库根 VERSION：据此生成 AssemblyInfo.cs 注入 EXE，并自动同步 README 版本徽标。
# 可选 -Tag：编译成功后为该版本创建 git 附注标签 v<版本>（仅本地，不 push）。
param([switch]$Tag)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot                          # cs -> 仓库根
$build = Join-Path $PSScriptRoot 'build'                         # 编译产物目录
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$src = Join-Path $PSScriptRoot 'src'
$exe = Join-Path $build 'HwWidget.exe'
$ico = Join-Path $build 'HwWidget.ico'

# ---- 读取版本号 ----
$verFile = Join-Path $root 'VERSION'
$ver = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '0.0.0' }
$parts = $ver.Split('.')
while ($parts.Count -lt 4) { $parts = $parts + @('0') }
$asmVer = ($parts[0..3] -join '.')      # AssemblyVersion/FileVersion 需四段数字

# ---- 生成 AssemblyInfo.cs（派生产物，不入库）----
$asmInfo = Join-Path $build 'AssemblyInfo.cs'
$a = @(
'using System.Reflection;',
'',
'// 由 build.ps1 依据仓库根 VERSION 自动生成，请勿手改、勿入库。',
'[assembly: AssemblyTitle("HwWidget")]',
'[assembly: AssemblyProduct("HUAWEI WiFi Widget")]',
('[assembly: AssemblyVersion("' + $asmVer + '")]'),
('[assembly: AssemblyFileVersion("' + $ver + '")]'),
('[assembly: AssemblyInformationalVersion("' + $ver + '")]')
)
Set-Content -Path $asmInfo -Value $a -Encoding UTF8

$iconArgs = @()
if (Test-Path $ico) {
    # /win32icon 将图标写入 EXE 壳（资源管理器/任务栏可见）；/resource 同名内嵌供运行时 NotifyIcon/Form.Icon 读取
    $iconArgs += "/win32icon:$ico"
    $iconArgs += "/resource:$ico,HwWidget.ico"
}
& $csc /nologo /optimize+ /target:winexe /platform:anycpu `
    "/out:$exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    @iconArgs `
    $asmInfo `
    (Join-Path $src 'Device.cs') (Join-Path $src 'Config.cs') (Join-Path $src 'Lang.cs') (Join-Path $src 'WidgetForm.cs') `
    (Join-Path $src 'SettingsForm.cs') (Join-Path $src 'Win32Controls.cs') (Join-Path $src 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "编译失败: $LASTEXITCODE" }

# ---- 同步 README 版本徽标与“当前版本”文字（仅替换标记位置，不动其它内容）----
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
    $ErrorActionPreference = 'Continue'   # git 向 stderr 输出时 Stop 会抛 NativeCommandError，局部放宽
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
