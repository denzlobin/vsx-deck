<#
  VSX Deck: one-step install. Re-running is safe (it updates in place).
    powershell -ExecutionPolicy Bypass -File .\Install.ps1 [-Mode full|daw] [-Output "MONITOR L/R"] [-Vsx <path to VSX.dll>] [-VstDir <folder>] [-DryRun]

  Needs: Slate VSX (VST2, 64-bit) and, for the keys, the Elgato Stream Deck app.
  Modes (asked if not given):
    full  System-wide: all Windows audio goes through VSX, plus the DAW plugin.
            - VB-Cable (asks first; admin prompt)         Windows -> VB-Cable -> VSX Deck -> your interface
            - VSX Deck app                                %LOCALAPPDATA%\VSX Deck (per user; starts at login, in the tray)
            - VSX Deck Holder for DAWs (asks)             -VstDir, default: the folder VSX.dll is in
    daw   DAW only: VSX Deck Holder for your DAW and the deck keys. No VB-Cable, no app, Windows audio untouched.
  Both: Stream Deck plugin + "VSX Deck" profile, deck state in %LOCALAPPDATA%\VSX Deck.
  -DryRun checks everything and prints what it would do, without changing anything.
#>
param([ValidateSet('full', 'daw')][string]$Mode, [string]$Output, [string]$Vsx, [string]$VstDir, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'VSX Deck'
$holder = 'VSX Deck Holder.dll'
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Q($s) { "'" + ($s -replace "'", "''") + "'" }   # quote a path for a PowerShell command line
# Runs a command elevated (UAC prompt); $false if the prompt was declined.
function Admin($command) {
    try { Start-Process powershell -ArgumentList '-NoProfile', '-Command', $command -Verb RunAs -Wait -WindowStyle Hidden; $true }
    catch { Write-Warning 'Administrator prompt declined.'; $false }
}

Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumeratorI { [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollectionI c); }
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollectionI { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDeviceI d); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceI { int Activate(); [PreserveSig] int OpenPropertyStore(int access, out IPropertyStoreI ps); }
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStoreI { int GetCount(out int n); int GetAt(int i, out PKeyI k); [PreserveSig] int GetValue(ref PKeyI k, out PVarI v); }
[StructLayout(LayoutKind.Sequential)] public struct PKeyI { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Sequential)] public struct PVarI { public short vt; short a, b, c; public IntPtr p; IntPtr p2; }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorI { }
public static class AudioDevices {
    public static string[] Names(int flow) {   // 0 = playback, 1 = recording; active devices' friendly names
        var list = new List<string>();
        var en = (IMMDeviceEnumeratorI)new MMDeviceEnumeratorI(); IMMDeviceCollectionI col; en.EnumAudioEndpoints(flow, 1, out col);
        int n; col.GetCount(out n);
        var key = new PKeyI { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        for (int i = 0; i < n; i++) { IMMDeviceI d; col.Item(i, out d); IPropertyStoreI ps; d.OpenPropertyStore(0, out ps); PVarI v; ps.GetValue(ref key, out v); if (v.vt == 31) list.Add(Marshal.PtrToStringUni(v.p)); }
        return list.ToArray();
    }
}
'@
function HasCable { [bool]([AudioDevices]::Names(1) | Where-Object { $_ -like '*CABLE Output*' }) }

# A 64-bit DLL (PE machine 0x8664)? VSX Deck is 64-bit and can only load 64-bit VSX.
function Is64BitDll($path) {
    if (-not $path -or [IO.Path]::GetExtension($path) -ne '.dll' -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try {
        $b = [IO.File]::ReadAllBytes($path); if ($b.Length -lt 0x40) { return $false }
        $pe = [BitConverter]::ToInt32($b, 0x3C)
        return $pe -gt 0 -and $pe + 6 -le $b.Length -and [BitConverter]::ToUInt16($b, $pe + 4) -eq 0x8664
    } catch { return $false }
}
function FindVsx {
    foreach ($d in "$env:ProgramFiles\VstPlugins", "$env:ProgramFiles\Steinberg\VstPlugins", "$env:CommonProgramFiles\VST2",
                   "$env:CommonProgramFiles\Steinberg\VST2") {
        $p = Join-Path $d 'VSX.dll'; if (Is64BitDll $p) { return $p }
    }
}
function Ask($question) { if ($DryRun) { throw "A dry run can't ask '$question': pass -Mode / -Vsx / -VstDir instead." }; (Read-Host $question).Trim().Trim('"') }

Step 'Checking prerequisites'
if (-not (Test-Path -LiteralPath "$kit\bin\$holder")) { throw 'bin\ is missing: run build.ps1 first (or copy the whole folder).' }
if (-not $Mode) {
    Write-Host 'What to install?'
    Write-Host '  1) System-wide: all Windows audio through VSX (VB-Cable + VSX Deck app), plus the DAW plugin'
    Write-Host '  2) DAW only: VSX Deck Holder for your DAW + the deck keys; Windows audio untouched'
    $Mode = if ((Ask 'Number [1]') -eq '2') { 'daw' } else { 'full' }
}
$full = $Mode -eq 'full'
if ($full -and -not (Test-Path -LiteralPath "$kit\bin\VSXDeck.exe")) { throw 'bin\VSXDeck.exe is missing: run build.ps1 first.' }

if (-not $Vsx) { $Vsx = FindVsx }
while (-not (Is64BitDll $Vsx)) {
    if ($Vsx) { Write-Warning "Not a 64-bit VST2 plugin: $Vsx" } else { Write-Host 'VSX.dll (VST2) was not found in the usual VST folders.' }
    $Vsx = Ask 'Full path to VSX.dll (empty = cancel)'
    if (-not $Vsx) { throw 'Install Slate VSX (VST2, 64-bit) via Steven Slate Audio Center, then run this again.' }
}
$Vsx = [IO.Path]::GetFullPath($Vsx)
Write-Host "VSX: $Vsx"

# Where the DAW plugin goes: a folder your DAW scans. Default = beside VSX.dll, which the DAW already scans.
if (-not $VstDir) { $VstDir = Split-Path $Vsx }
try { $VstDir = [IO.Path]::GetFullPath($VstDir.Trim().Trim('"')) } catch { throw "Not a valid folder path: $VstDir" }
if (-not [IO.Path]::IsPathRooted($VstDir) -or $VstDir -match '^\\\\') { throw "Use a local folder with a drive letter for -VstDir: $VstDir" }
if (Test-Path -LiteralPath $VstDir -PathType Leaf) { throw "-VstDir is a file, not a folder: $VstDir" }
foreach ($own in $app, $kit) { if (($VstDir.TrimEnd('\') + '\').StartsWith($own.TrimEnd('\') + '\', 'OrdinalIgnoreCase')) { throw "-VstDir can't be inside $own (pick the folder your DAW scans for VST2 plugins)." } }
$vst = Join-Path $VstDir $holder
Write-Host "DAW plugin folder: $VstDir$(if (-not (Test-Path -LiteralPath $VstDir)) { ' (will be created)' })"

$sdExe = Join-Path $env:ProgramFiles 'Elgato\StreamDeck\StreamDeck.exe'
if (-not (Test-Path $sdExe)) { Write-Warning 'Stream Deck app not found: deck keys will be skipped.' }

if ($DryRun) {
    Write-Host "`nDry run, nothing changed. Would install ($Mode):"
    if ($full) { Write-Host "  VB-Cable: $(if (HasCable) { 'installed' } else { 'download and install (asks first)' })"; Write-Host "  VSX Deck app -> $app" }
    Write-Host "  VSX Deck Holder -> $vst"
    if (Test-Path $sdExe) { Write-Host '  Stream Deck plugin + "VSX Deck" profile' }
    return
}

if ($full) {
    Step 'VB-Cable (virtual default output)'
    if (HasCable) { Write-Host 'Installed.' }
    else {
        if ((Read-Host 'VB-Cable is not installed. Download it from vb-audio.com and install now? (admin prompt) [Y/n]') -match '^[nN]') { throw 'VB-Cable is required: https://vb-audio.com/Cable/' }
        $dl = Join-Path $env:TEMP 'vbcable'; New-Item -ItemType Directory -Force $dl | Out-Null
        Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip' -OutFile "$dl\pack.zip" -UseBasicParsing
        Expand-Archive "$dl\pack.zip" "$dl\x" -Force
        if ((Get-AuthenticodeSignature "$dl\x\VBCABLE_Setup_x64.exe").Status -ne 'Valid') { throw 'VB-Cable installer signature is not valid; not running it.' }
        Start-Process "$dl\x\VBCABLE_Setup_x64.exe" -ArgumentList '-i', '-h' -WorkingDirectory "$dl\x" -Verb RunAs -Wait
        Start-Sleep 3
        if (-not (HasCable)) { throw 'VB-Cable did not appear; a restart may be needed. Run this again afterwards.' }
    }
}

Step "Deck state in $app"
New-Item -ItemType Directory -Force $app | Out-Null
[IO.File]::WriteAllText((Join-Path $app 'vsx.txt'), $Vsx, (New-Object Text.UTF8Encoding $false))   # the Holder loads VSX from here
Write-Host 'Done.'

if ($full) {
    Step "VSX Deck app in $app"
    Get-Process VSXDeck -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
    Copy-Item "$kit\bin\*" $app -Force
    $outFile = Join-Path $app 'output.txt'
    if ($Output) { Set-Content $outFile $Output -NoNewline }
    elseif (-not (Test-Path $outFile)) {
        $outs = @([AudioDevices]::Names(0) | Where-Object { $_ -notlike '*VB-Audio*' })
        Write-Host 'Where should VSX Deck play (your headphone interface)?'
        for ($i = 0; $i -lt $outs.Count; $i++) { Write-Host ("  {0}) {1}" -f ($i + 1), $outs[$i]) }
        $pick = Read-Host 'Number [1]'
        $name = $outs[$(if ($pick) { [int]$pick - 1 } else { 0 })]
        Set-Content $outFile ($name -replace ' \(.*\)$', '') -NoNewline   # match by name without the "(driver)" suffix
    }
    Write-Host "Output: $(Get-Content $outFile)"
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'VSX Deck.lnk'))
    $lnk.TargetPath = "$app\VSXDeck.exe"; $lnk.Arguments = '--tray'; $lnk.Save()
    Write-Host 'Starts at login (in the tray).'
}

Step "VSX Deck Holder for DAWs in $VstDir"
$same = { (Test-Path -LiteralPath $vst) -and (Get-FileHash -LiteralPath $vst).Hash -eq (Get-FileHash -LiteralPath "$kit\bin\$holder").Hash }
if (& $same) { Write-Host 'Up to date.' }
elseif (-not $full -or (Read-Host "Install VSX Deck Holder (VST2) to $VstDir? [Y/n]") -notmatch '^[nN]') {
    # Plain copy if the folder is yours; Program Files and the like need an administrator prompt.
    try { New-Item -ItemType Directory -Force -Path $VstDir | Out-Null; Copy-Item -LiteralPath "$kit\bin\$holder" -Destination $vst -Force }
    catch {
        Admin "New-Item -ItemType Directory -Force -Path $(Q $VstDir) | Out-Null; Copy-Item -LiteralPath $(Q "$kit\bin\$holder") -Destination $(Q $vst) -Force" | Out-Null
    }
    if (& $same) { Write-Host "Installed. In your DAW, rescan VST plugins (Reaper: Preferences > Plug-ins > VST > Re-scan), then put VSX Deck Holder where VSX was (Reaper: View > Monitoring FX)." }
    elseif (Test-Path -LiteralPath $vst) { Write-Warning "Could not replace $vst. Close your DAW (it may have the plugin loaded) and run this again." }
    else { Write-Warning "VSX Deck Holder was not installed to $VstDir." }
}
if (Test-Path -LiteralPath $vst) { [IO.File]::WriteAllText((Join-Path $app 'holder.txt'), $vst, (New-Object Text.UTF8Encoding $false)) }   # for Uninstall.ps1
if (-not $full -and (Test-Path -LiteralPath "$app\VSXDeck.exe")) { Write-Warning 'The system-wide part (VSX Deck app) is still installed. To remove it: Uninstall.ps1 -Part app' }

if (Test-Path $sdExe) {
    Step 'Stream Deck plugin and "VSX Deck" profile'
    Get-Process StreamDeck -ErrorAction SilentlyContinue | Stop-Process; Start-Sleep 2
    $pl = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.vsxdeck.sdPlugin'
    New-Item -ItemType Directory -Force $pl | Out-Null
    Copy-Item "$kit\com.vsxdeck.sdPlugin\*" $pl -Force

    $action = { param($uuid, $name, $settings)
        [ordered]@{ ActionID = [guid]::NewGuid().ToString(); LinkedTitle = $false; Name = $name
                    Plugin = [ordered]@{ Name = 'VSX Deck'; UUID = 'com.vsxdeck'; Version = '0.2.10' }
                    Resources = $null; Settings = $settings; State = 0; States = @([ordered]@{ ShowTitle = $false }); UUID = $uuid } }
    $keys = [ordered]@{}   # "column,row" on a 5x3 deck
    for ($n = 1; $n -le 10; $n++) { $keys["$(($n - 1) % 5),$([Math]::Floor(($n - 1) / 5))"] = & $action 'com.vsxdeck.slot' 'VSX Preset' ([ordered]@{ slot = $n }) }
    $keys['0,2'] = & $action 'com.vsxdeck.bypass' 'VSX Bypass' ([ordered]@{})
    if ($full) { $keys['1,2'] = & $action 'com.vsxdeck.app' 'VSX Deck App' ([ordered]@{}) }
    $keys['3,2'] = & $action 'com.vsxdeck.save' 'VSX Save' ([ordered]@{})
    $keys['4,2'] = & $action 'com.vsxdeck.onoff' 'VSX On/Off' ([ordered]@{})
    $page = [ordered]@{ Controllers = @([ordered]@{ Actions = $keys; Type = 'Keypad' }); Icon = ''; Name = '' }

    $root = Join-Path $env:APPDATA 'Elgato\StreamDeck\ProfilesV3'
    $mans = @(Get-ChildItem $root -Filter manifest.json -Recurse -Depth 1 -ErrorAction SilentlyContinue)
    $mine = $mans | Where-Object { (Get-Content $_.FullName -Raw) -match '"Name":"VSX Deck"' } | Select-Object -First 1
    $utf8 = New-Object Text.UTF8Encoding $false
    $pageDir = $null
    if ($mine) {
        $pageDir = Join-Path $mine.DirectoryName ('Profiles\' + (Get-Content $mine.FullName -Raw | ConvertFrom-Json).Pages.Current.ToUpper())
    } elseif ($mans) {
        $device = (Get-Content $mans[0].FullName -Raw | ConvertFrom-Json).Device
        $profId = [guid]::NewGuid().ToString().ToUpper(); $pageId = [guid]::NewGuid().ToString().ToLower()
        $pageDir = Join-Path $root "$profId.sdProfile\Profiles\$($pageId.ToUpper())"
        New-Item -ItemType Directory -Force $pageDir | Out-Null
        $prof = [ordered]@{ AppIdentifier = '*'; Device = $device; Name = 'VSX Deck'; Pages = [ordered]@{ Current = $pageId; Default = $pageId; Pages = @($pageId) }; Version = '3.0' }
        [IO.File]::WriteAllText((Join-Path $root "$profId.sdProfile\manifest.json"), ($prof | ConvertTo-Json -Depth 10 -Compress), $utf8)
    } else { Write-Warning 'Open the Stream Deck app once (so it creates a profile), then run this again for the deck keys.' }
    if ($pageDir) {
        New-Item -ItemType Directory -Force (Join-Path $pageDir 'Images') | Out-Null
        [IO.File]::WriteAllText((Join-Path $pageDir 'manifest.json'), ($page | ConvertTo-Json -Depth 10 -Compress), $utf8)
        Write-Host "Rows 1-2: presets 1-10. Bottom: Bypass$(if ($full) { ', App' }) (left), Save, VSX ON/OFF (right)."
    }
    Start-Process $sdExe
}

if (-not $full) {
    Write-Host "`nDone. The deck keys control VSX Deck Holder in your DAW; ON/OFF and Bypass show a dashed outline while the DAW isn't running."
    return
}
Step 'Starting VSX Deck'
Start-Process "$app\VSXDeck.exe"
Write-Host @"

Last step (Windows won't let a script do it): set Windows' default OUTPUT to "CABLE Input (VB-Audio Virtual Cable)".
Opening Sound settings now. VSX Deck then plays everything through VSX to: $(Get-Content $outFile)
"@
Start-Process 'ms-settings:sound'
