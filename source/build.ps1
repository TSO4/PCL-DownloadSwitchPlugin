# SPDX-License-Identifier: MIT
# 构建需要 .NET Framework 4.8、MSVC C++ 工具和 Windows/.NET Framework SDK。
# 使用者运行编译好的插件不需要安装 Visual Studio 或额外 NuGet 依赖。
param([string]$MsvcRoot = '', [string]$WindowsSdkVersion = '')
$ErrorActionPreference = 'Stop'
$taskBuildDir = $PSScriptRoot
$taskOutDir = Join-Path $taskBuildDir 'build'
New-Item -ItemType Directory -Path $taskOutDir -Force | Out-Null
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
$taskReferences = @(
    '/r:System.Core.dll',
    '/r:System.Xml.dll',
    '/r:System.Xml.Linq.dll',
    '/r:System.Xaml.dll',
    ('/r:' + (Join-Path $taskFramework 'WPF\WindowsBase.dll')),
    ('/r:' + (Join-Path $taskFramework 'WPF\PresentationCore.dll')),
    ('/r:' + (Join-Path $taskFramework 'WPF\PresentationFramework.dll'))
)
$taskSources = @('Plugin.cs', 'DetailsWindow.cs', 'HashView.cs', 'DownloadActivity.cs', 'DiagnosticsLog.cs', 'DiagnosticsView.cs', 'PluginSettings.cs', 'SettingsView.cs', 'FallbackBridge.cs', 'FallbackState.cs')
$taskResources = @('Plugin.cs', 'DetailsWindow.cs', 'HashView.cs', 'DownloadActivity.cs', 'DiagnosticsLog.cs', 'DiagnosticsView.cs', 'PluginSettings.cs', 'SettingsView.cs', 'Launcher.cs', 'Compatibility.cs', 'AutoWatcher.cs', 'AutoProgress.cs', 'FallbackBridge.cs', 'FallbackState.cs', 'FallbackMain.cs', 'FallbackWindow.cs', 'InstallModel.cs', 'DownloadSources.cs','InstallerNetwork.cs', 'InstallerCatalog.cs', 'InstallerJava.cs', 'InstallerService.cs', 'NativeBridge.cpp', 'build.ps1', 'LICENSE') | ForEach-Object {
    '/resource:' + (Join-Path $taskBuildDir $_) + ',PclDownloadSwitch.Source.' + $_
}
$taskCecil = Join-Path $taskBuildDir 'vendor\Mono.Cecil.dll'
if (!(Test-Path -LiteralPath $taskCecil)) { throw '缺少 vendor\Mono.Cecil.dll，请保留完整 source 文件夹。' }
$taskResources += '/resource:' + (Join-Path $taskBuildDir 'vendor\Mono.Cecil.LICENSE.txt') + ',PclDownloadSwitch.Source.Mono.Cecil.LICENSE.txt'
& $taskCompiler /nologo /target:library /optimize+ /platform:anycpu @taskReferences @taskResources ("/out:" + (Join-Path $taskOutDir 'PCL.DownloadSwitch.dll')) ($taskSources | ForEach-Object { Join-Path $taskBuildDir $_ })
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }
$taskEntrySources = @('Launcher.cs', 'Compatibility.cs', 'AutoWatcher.cs', 'AutoProgress.cs', 'PluginSettings.cs', 'DiagnosticsLog.cs') | ForEach-Object { Join-Path $taskBuildDir $_ }
& $taskCompiler /nologo /target:winexe /optimize+ /platform:x64 /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Xml.Linq.dll ("/r:" + $taskCecil) ("/out:" + (Join-Path $taskOutDir 'PCL下载开关.exe')) @taskEntrySources
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
Copy-Item -LiteralPath $taskCecil -Destination (Join-Path $taskOutDir 'Mono.Cecil.dll') -Force
$taskBackupSources = @('FallbackMain.cs','FallbackWindow.cs','FallbackState.cs','InstallModel.cs','DownloadSources.cs','InstallerNetwork.cs','InstallerCatalog.cs','InstallerJava.cs','InstallerService.cs','PluginSettings.cs','DiagnosticsLog.cs') | ForEach-Object { Join-Path $taskBuildDir $_ }
& $taskCompiler /nologo /target:winexe /optimize+ /platform:x64 @taskReferences /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ("/out:" + (Join-Path $taskOutDir 'PCL备用下载.exe')) @taskBackupSources
if ($LASTEXITCODE -ne 0) { throw 'Backup installer compilation failed.' }

# 可显式传入 MSVC 根目录；默认从常见 Visual Studio 安装路径查找 x64 编译器。
if (!$MsvcRoot) {
    $taskVcRoots = @(
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio')
    )
    $taskFound = foreach ($taskRoot in $taskVcRoots) {
        Get-ChildItem -Path (Join-Path $taskRoot '*\*\VC\Tools\MSVC\*') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\Hostx64\x64\cl.exe') }
    }
    $MsvcRoot = ($taskFound | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
}
if (!$MsvcRoot) { throw '未找到 MSVC。可传入 -MsvcRoot 指定 VC\Tools\MSVC\<版本>。' }
$taskSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
if (!$WindowsSdkVersion) {
    $WindowsSdkVersion = (Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Lib') -Directory |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
}
$taskNetSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\NETFXSDK\4.8'
$taskIncludes = @(
    (Join-Path $MsvcRoot 'include'),
    (Join-Path $taskSdk "Include\$WindowsSdkVersion\ucrt"),
    (Join-Path $taskSdk "Include\$WindowsSdkVersion\shared"),
    (Join-Path $taskSdk "Include\$WindowsSdkVersion\um"),
    (Join-Path $taskNetSdk 'Include\um')
) | ForEach-Object { '/I' + $_ }
$taskLibraries = @(
    (Join-Path $MsvcRoot 'lib\x64'),
    (Join-Path $taskSdk "Lib\$WindowsSdkVersion\ucrt\x64"),
    (Join-Path $taskSdk "Lib\$WindowsSdkVersion\um\x64"),
    (Join-Path $taskNetSdk 'Lib\um\x64')
) | ForEach-Object { '/LIBPATH:' + $_ }
& (Join-Path $MsvcRoot 'bin\Hostx64\x64\cl.exe') /nologo /LD /MT /O2 /W4 /utf-8 @taskIncludes `
    (Join-Path $taskBuildDir 'NativeBridge.cpp') ("/Fo" + (Join-Path $taskOutDir 'NativeBridge.obj')) `
    /link @taskLibraries ("/OUT:" + (Join-Path $taskOutDir 'PCL.DownloadSwitch.Native.dll')) `
    ("/IMPLIB:" + (Join-Path $taskOutDir 'NativeBridge.lib')) kernel32.lib mscoree.lib
if ($LASTEXITCODE -ne 0) { throw 'Native bridge compilation failed.' }
Get-Item -LiteralPath (Join-Path $taskOutDir 'PCL.DownloadSwitch.dll'), (Join-Path $taskOutDir 'PCL下载开关.exe'), (Join-Path $taskOutDir 'PCL.DownloadSwitch.Native.dll'), (Join-Path $taskOutDir 'PCL备用下载.exe'), (Join-Path $taskOutDir 'Mono.Cecil.dll') |
    Select-Object Name,Length
