<#
  VSX Deck: one-step install. Re-running is safe (it updates in place).
    powershell -ExecutionPolicy Bypass -File .\Install.ps1 [-Output "MONITOR L/R"]

  Needs: Slate VSX (VST2: C:\Program Files\VstPlugins\VSX.dll) and the Elgato Stream Deck app.
  Sets up:
    - VB-Cable (asks first; admin prompt)             Windows -> VB-Cable -> VSX Deck -> your interface
    - VSX Deck app, VSX Deck Holder, deck state       %LOCALAPPDATA%\VSX Deck (per user; starts at login, in the tray)
    - VSX Deck Holder VST2 for DAWs (asks; admin)     C:\Program Files\VstPlugins\VSX Deck Holder.dll
    - Stream Deck plugin + "VSX Deck" profile         presets 1-10, Bypass, Save, VSX ON/OFF
#>
param([string]$Output)
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$app = Join-Path $env:LOCALAPPDATA 'VSX Deck'
$vsx = Join-Path $env:ProgramFiles 'VstPlugins\VSX.dll'
$holder = 'VSX Deck Holder.dll'
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Admin($command) { Start-Process powershell -ArgumentList '-NoProfile', '-Command', $command -Verb RunAs -Wait -WindowStyle Hidden }

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

Step 'Checking prerequisites'
if (-not (Test-Path $vsx)) { throw "Slate VSX (VST2) not found at $vsx. Install VSX via Steven Slate Audio Center, then run this again." }
if (-not (Test-Path "$kit\bin\VSXDeck.exe")) { throw 'bin\ is missing: run build.ps1 first (or copy the whole folder).' }
$sdExe = Join-Path $env:ProgramFiles 'Elgato\StreamDeck\StreamDeck.exe'
if (-not (Test-Path $sdExe)) { Write-Warning 'Stream Deck app not found: deck keys will be skipped.' }

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

Step "VSX Deck app in $app"
Get-Process VSXDeck -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
New-Item -ItemType Directory -Force $app | Out-Null
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

Step 'VSX Deck Holder for DAWs (Reaper)'
$vst = Join-Path $env:ProgramFiles "VstPlugins\$holder"
if ((Test-Path $vst) -and (Get-FileHash $vst).Hash -eq (Get-FileHash "$kit\bin\$holder").Hash) { Write-Host 'Up to date.' }
elseif ((Read-Host "Install VSX Deck Holder (VST2) to $vst? (admin prompt) [Y/n]") -notmatch '^[nN]') {
    Admin "Copy-Item '$kit\bin\$holder' '$vst' -Force"
    if (Test-Path $vst) { Write-Host 'Installed. In Reaper: Preferences > Plug-ins > VST > Re-scan, then add "VSX Deck Holder" to View > Monitoring FX.' }
}

if (Test-Path $sdExe) {
    Step 'Stream Deck plugin and "VSX Deck" profile'
    Get-Process StreamDeck -ErrorAction SilentlyContinue | Stop-Process; Start-Sleep 2
    $pl = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.vsxdeck.sdPlugin'
    New-Item -ItemType Directory -Force $pl | Out-Null
    Copy-Item "$kit\com.vsxdeck.sdPlugin\*" $pl -Force

    $action = { param($uuid, $name, $settings)
        [ordered]@{ ActionID = [guid]::NewGuid().ToString(); LinkedTitle = $false; Name = $name
                    Plugin = [ordered]@{ Name = 'VSX Deck'; UUID = 'com.vsxdeck'; Version = '2.1.0.0' }
                    Resources = $null; Settings = $settings; State = 0; States = @([ordered]@{ ShowTitle = $false }); UUID = $uuid } }
    $keys = [ordered]@{}   # "column,row" on a 5x3 deck
    for ($n = 1; $n -le 10; $n++) { $keys["$(($n - 1) % 5),$([Math]::Floor(($n - 1) / 5))"] = & $action 'com.vsxdeck.slot' 'VSX Preset' ([ordered]@{ slot = $n }) }
    $keys['0,2'] = & $action 'com.vsxdeck.bypass' 'VSX Bypass' ([ordered]@{})
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
        Write-Host 'Rows 1-2: presets 1-10. Bottom: Bypass (left), Save, VSX ON/OFF (right).'
    }
    Start-Process $sdExe
}

Step 'Starting VSX Deck'
Start-Process "$app\VSXDeck.exe"
Write-Host @"

Last step (Windows won't let a script do it): set Windows' default OUTPUT to "CABLE Input (VB-Audio Virtual Cable)".
Opening Sound settings now. VSX Deck then plays everything through VSX to: $(Get-Content $outFile)
"@
Start-Process 'ms-settings:sound'
