# Rebuilds bin\ from source. Only needed after changing code; Install.ps1 uses bin\ as is.
# Needs Visual Studio Build Tools (C++) for the plugin; the app uses the C# compiler built into Windows.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot; $bin = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ico = "$src\assets\vsxdeck.ico"   # made by make-icons.ps1
& $csc /nologo /target:winexe /platform:x64 /optimize /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll "/win32icon:$ico" "/resource:$ico,vsxdeck.ico" "/out:$bin\VSXDeck.exe" "$src\vsxdeck.cs"
if ($LASTEXITCODE) { throw 'VSXDeck.exe failed to build' }

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio Build Tools (C++) not found: needed for the plugin' }
$obj = Join-Path $env:TEMP 'vsxdeck-obj'; New-Item -ItemType Directory -Force $obj | Out-Null
cmd /c "`"$vs\Common7\Tools\VsDevCmd.bat`" -arch=x64 -no_logo >nul 2>&1 && cl /nologo /O2 /W3 /LD `"$src\vsxdeckholder.c`" user32.lib gdi32.lib /Fo:`"$obj\\`" /Fe:`"$bin\VSX Deck Holder.dll`""
if ($LASTEXITCODE) { throw 'VSX Deck Holder failed to build' }
Remove-Item "$bin\VSX Deck Holder.lib", "$bin\VSX Deck Holder.exp" -ErrorAction SilentlyContinue
Get-ChildItem $bin | Select-Object Name, Length
