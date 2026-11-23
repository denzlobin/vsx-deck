<#
  VSX Deck: one-step install. Re-running is safe (it updates in place).
    powershell -ExecutionPolicy Bypass -File .\Install.ps1 [-Mode full|daw] [-Output "<device name>"] [-Vsx <VSX.dll or its folder>] [-VstDir <folder>] [-DryRun]

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
$kitVersion = (Get-Content -LiteralPath "$kit\com.vsxdeck.sdPlugin\manifest.json" -Raw | ConvertFrom-Json).Version   # the one version number
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
# VSX.dll in these folders or up to two levels below them (installers often add a vendor folder).
$vstFolders = "$env:ProgramFiles\VstPlugins", "$env:ProgramFiles\Steinberg\VstPlugins", "$env:CommonProgramFiles\VST2",
              "$env:CommonProgramFiles\Steinberg\VST2", "$env:ProgramFiles\VST2"
function FindVsx($folders) {
    foreach ($d in $folders) {
        if (-not (Test-Path -LiteralPath $d -PathType Container)) { continue }
        $hit = Get-ChildItem -LiteralPath $d -Filter 'VSX.dll' -File -Recurse -Depth 2 -ErrorAction SilentlyContinue |
               Where-Object { Is64BitDll $_.FullName } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
}
# A path the user gave: VSX.dll itself, or a folder to search.
function VsxAt($path) {
    $path = $path.Trim().Trim('"')
    if (Test-Path -LiteralPath $path -PathType Leaf) { if (Is64BitDll $path) { return $path } else { return $null } }
    FindVsx @($path)
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

$installVsx = 'Install Slate VSX first (Steven Slate Audio Center, with the VST2 format), then run this again.'
if ($Vsx) { $given = $Vsx; $Vsx = VsxAt $given; if (-not $Vsx) { throw "No 64-bit VSX.dll (VST2) at -Vsx $given" } }
else {
    $Vsx = FindVsx $vstFolders
    # Not in the usual places: VSX may be in a VST folder of the user's own, so ask before giving up.
    while (-not $Vsx) {
        Write-Host 'VSX.dll (64-bit VST2) was not found in the usual VST folders.'
        if ($DryRun) { throw "$installVsx (Or pass -Vsx with VSX's folder.)" }
        $where = (Read-Host 'If VSX is installed, type its VST2 folder (or the path to VSX.dll). Press Enter if it is not installed').Trim().Trim('"')
        if (-not $where) { throw $installVsx }
        $Vsx = VsxAt $where
        if (-not $Vsx) { Write-Warning "No 64-bit VSX.dll in $where (searched two folders deep)." }
    }
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

# Files from a downloaded zip carry Windows' "came from the internet" mark, and copies keep it: SmartScreen would then
# stop the unsigned VSXDeck.exe ("Windows protected your PC").
Get-ChildItem -LiteralPath $kit -Recurse -File | Unblock-File

if ($full) {
    Step 'VB-Cable (virtual default output)'
    if (HasCable) { Write-Host 'Installed.' }
    else {
        if ((Read-Host 'VB-Cable is not installed. Download it from vb-audio.com and install now? (admin prompt) [Y/n]') -match '^[nN]') { throw 'VB-Cable is required: https://vb-audio.com/Cable/' }
        $page = 'https://vb-audio.com/Cable/'
        $dl = Join-Path $env:TEMP 'vbcable'; Remove-Item -LiteralPath $dl -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $dl | Out-Null
        # The pack's file name changes with each VB-Cable release: if this one is gone, the user takes it from the page.
        try {
            Invoke-WebRequest 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip' -OutFile "$dl\pack.zip" -UseBasicParsing
            Expand-Archive "$dl\pack.zip" "$dl\x" -Force
        } catch {
            Start-Process $page
            throw "Couldn't download VB-Cable ($($_.Exception.Message)). Install it from the page that just opened ($page), restart if it asks, then run this again."
        }
        $sig = Get-AuthenticodeSignature "$dl\x\VBCABLE_Setup_x64.exe"
        if ($sig.Status -ne 'Valid') { throw 'VB-Cable installer signature is not valid; not running it.' }
        $signer = $sig.SignerCertificate.Subject
        # VB-Audio signs as its owner, e.g. "CN=BUREL VINCENT Entrepreneur individuel, O=..." (Pack46): accept a
        # common name with both names in either order, or VB-Audio; anything else is the user's call.
        $cn = if ($signer -match '(?:^|,\s*)CN=([^,]+)') { $Matches[1] } else { '' }
        $known = $cn -match 'VB-Audio' -or ($cn -match '\bBurel\b' -and $cn -match '\bVincent\b')
        if (-not $known -and (Read-Host "The VB-Cable installer is signed by: $signer`nRun it anyway? [y/N]") -notmatch '^[yY]') {
            throw "Not running it. Install VB-Cable from $page, then run this again."
        }
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
    elseif (-not (Test-Path $outFile) -or -not (Get-Content $outFile -Raw)) {
        $outs = @([AudioDevices]::Names(0) | Where-Object { $_ -notlike '*VB-Audio*' })
        if ($outs.Count -eq 0) { Write-Warning 'No playback device found (interface off?). Choose the output later in VSX Deck Settings.' }
        else {
            Write-Host 'Where should VSX Deck play (your headphone interface)?'
            for ($i = 0; $i -lt $outs.Count; $i++) { Write-Host ("  {0}) {1}" -f ($i + 1), $outs[$i]) }
            $n = 0
            do { $a = (Read-Host 'Number [1]').Trim(); if (-not $a) { $n = 1 } elseif (-not [int]::TryParse($a, [ref]$n)) { $n = 0 } } until ($n -ge 1 -and $n -le $outs.Count)
            Set-Content $outFile ($outs[$n - 1] -replace ' \(.*\)$', '') -NoNewline   # match by name without the "(driver)" suffix
        }
    }
    if (Test-Path $outFile) { Write-Host "Output: $(Get-Content $outFile)" }
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
                    Plugin = [ordered]@{ Name = 'VSX Deck'; UUID = 'com.vsxdeck'; Version = $kitVersion }
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
    $mine = $mans | Where-Object { try { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Name -eq 'VSX Deck' } catch { $false } } | Select-Object -First 1
    $utf8 = New-Object Text.UTF8Encoding $false
    $pageDir = $null
    if ($mine) {   # an update must not undo the user's own key layout
        Write-Host 'Kept your "VSX Deck" profile as it is. (Delete it in the Stream Deck app and run this again for the default layout.)'
    } elseif ($mans) {
        # Which deck: the one the existing profiles belong to; with several decks, ask.
        $devices = @($mans | ForEach-Object { try { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Device } catch { } } |
                     Where-Object { $_ } | Group-Object { "$($_.Model) $($_.UUID)" } | ForEach-Object { $_.Group[0] })
        $device = $devices[0]
        if ($devices.Count -gt 1) {
            Write-Host 'Several Stream Decks found. Which one gets the VSX Deck profile?'
            for ($i = 0; $i -lt $devices.Count; $i++) { Write-Host ("  {0}) model {1}, id {2}" -f ($i + 1), $devices[$i].Model, $devices[$i].UUID) }
            $n = 0
            do { $a = (Read-Host 'Number [1]').Trim(); if (-not $a) { $n = 1 } elseif (-not [int]::TryParse($a, [ref]$n)) { $n = 0 } } until ($n -ge 1 -and $n -le $devices.Count)
            $device = $devices[$n - 1]
        }
        Write-Host 'The layout is made for a 15-key deck (5 x 3); on other sizes, move the keys in the Stream Deck app.'
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
$to = if (Test-Path $outFile) { Get-Content $outFile } else { 'the output you choose in its Settings' }
Write-Host @"

Last step: if Windows doesn't play into VB-Cable yet, VSX Deck's window says so. Press "Play through VSX" there
(or set "CABLE Input (VB-Audio Virtual Cable)" as the default output in Sound settings).
VSX Deck then plays everything through VSX to: $to
"@
