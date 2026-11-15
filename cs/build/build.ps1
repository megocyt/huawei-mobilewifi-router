# 用 Windows 自带的 .NET Framework csc 编译，产物 HwWidget.exe 免安装、零外部依赖
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$src = Join-Path $PSScriptRoot '..\src'
$exe = Join-Path $PSScriptRoot 'HwWidget.exe'
$ico = Join-Path $PSScriptRoot 'HwWidget.ico'
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
    (Join-Path $src 'Device.cs') (Join-Path $src 'Config.cs') (Join-Path $src 'Lang.cs') (Join-Path $src 'WidgetForm.cs') `
    (Join-Path $src 'SettingsForm.cs') (Join-Path $src 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw "编译失败: $LASTEXITCODE" }
Write-Host "OK -> $exe"
