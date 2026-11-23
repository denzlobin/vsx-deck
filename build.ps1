# Rebuilds bin\ from source. Only needed after changing code; Install.ps1 uses bin\ as is.
# Needs Visual Studio Build Tools (C++) for the plugin; the app uses the C# compiler built into Windows.
#   -Holder   rebuild VSX Deck Holder even if vsxdeckholder.c hasn't changed (a new DLL means an admin reinstall)
param([switch]$Holder)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot; $bin = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

# The kit version lives in one place, the Stream Deck manifest; the app gets it stamped in here.
$version = (Get-Content -LiteralPath "$src\com.vsxdeck.sdPlugin\manifest.json" -Raw | ConvertFrom-Json).Version
$stamp = Join-Path $env:TEMP 'vsxdeck-version.cs'
Set-Content -LiteralPath $stamp -Encoding ASCII -Value @"
[assembly: System.Reflection.AssemblyVersion("$version")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
"@

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ico = "$src\assets\vsxdeck.ico"   # made by make-icons.ps1
& $csc /nologo /target:winexe /platform:x64 /optimize /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll "/win32icon:$ico" "/resource:$ico,vsxdeck.ico" "/out:$bin\VSXDeck.exe" "$src\vsxdeck.cs" $stamp
if ($LASTEXITCODE) { throw 'VSXDeck.exe failed to build' }
Write-Host "VSXDeck.exe $version"

$dll = "$bin\VSX Deck Holder.dll"
if (-not $Holder -and (Test-Path -LiteralPath $dll) -and (Get-Item -LiteralPath $dll).LastWriteTime -gt (Get-Item -LiteralPath "$src\vsxdeckholder.c").LastWriteTime) {
    Write-Host 'VSX Deck Holder.dll is newer than vsxdeckholder.c: kept (-Holder to rebuild it anyway)'
} else {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio Build Tools (C++) not found: needed for the plugin' }
    $obj = Join-Path $env:TEMP 'vsxdeck-obj'; New-Item -ItemType Directory -Force $obj | Out-Null
    cmd /c "`"$vs\Common7\Tools\VsDevCmd.bat`" -arch=x64 -no_logo >nul 2>&1 && cl /nologo /O2 /W3 /LD `"$src\vsxdeckholder.c`" user32.lib gdi32.lib /Fo:`"$obj\\`" /Fe:`"$dll`""
    if ($LASTEXITCODE) { throw 'VSX Deck Holder failed to build' }
    Remove-Item "$bin\VSX Deck Holder.lib", "$bin\VSX Deck Holder.exp" -ErrorAction SilentlyContinue
}
Get-ChildItem $bin | Select-Object Name, Length
