# Builds dist\CrestSetup-<version>.exe: publishes Crest self-contained (its own .NET runtime),
# adds Uninstall.exe, zips it all, and embeds the zip in a small Setup.exe compiled with the
# .NET Framework compiler that ships with Windows.
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH

$root    = Split-Path $PSScriptRoot -Parent
$src     = Join-Path $root 'src\Crest'
$dist    = Join-Path $root 'dist'
$app     = Join-Path $dist 'app'
$work    = Join-Path $dist 'build'
# One version for everything: read it from Crest.csproj and refuse to build if Setup.cs or Uninstall.cs disagree.
$version = ([xml](Get-Content (Join-Path $src 'Crest.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not (Select-String -Quiet -SimpleMatch "Version = `"$version`"" "$PSScriptRoot\Setup.cs")) { throw "Setup.cs Info.Version is not $version" }
if (-not (Select-String -Quiet -SimpleMatch "`"$version.0`"" "$PSScriptRoot\Uninstall.cs")) { throw "Uninstall.cs AssemblyVersion is not $version.0" }
$csc     = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fx      = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf     = "$fx\WPF"
$icon    = Join-Path $src 'Assets\Crest.ico'

if (-not $SkipPublish) {
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    dotnet publish $src -nologo -v q -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $app
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
}
Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item -Force

New-Item -ItemType Directory -Force $work | Out-Null

& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$app\Uninstall.exe" "/win32icon:$icon" "/win32manifest:$PSScriptRoot\app.manifest" `
    /r:System.Windows.Forms.dll "$PSScriptRoot\Uninstall.cs"
if ($LASTEXITCODE) { throw 'Uninstall.exe failed to compile' }

$zip = Join-Path $work 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

$out = Join-Path $dist "CrestSetup-$version.exe"
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$out" "/win32icon:$icon" "/win32manifest:$PSScriptRoot\app.manifest" `
    "/resource:$zip,payload.zip" "/resource:$icon,Crest.ico" `
    "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" "/r:$fx\System.Xaml.dll" `
    "/r:$fx\System.IO.Compression.dll" "/r:$fx\System.IO.Compression.FileSystem.dll" "/r:System.dll" `
    "$PSScriptRoot\Setup.cs"
if ($LASTEXITCODE) { throw 'Setup.exe failed to compile' }

Remove-Item $work -Recurse -Force
"{0}  {1:N1} MB" -f $out, ((Get-Item $out).Length / 1MB)
