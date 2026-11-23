<#
  VSX Deck: uninstall.
    powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1 [-Part all|app] [-Presets keep|delete] [-DryRun]

  -Part all   everything: app, VSX Deck Holder (DAW plugin), Stream Deck plugin + "VSX Deck" profile, deck state
  -Part app   only the system-wide part (VSX Deck app, start at login); keeps the DAW plugin and the deck keys
  -Presets    keep (default) leaves your slotN.vsx presets and cleared\ in %LOCALAPPDATA%\VSX Deck
  -DryRun     prints what it would remove, changes nothing
  VB-Cable is left installed (other apps may use it); remove it in Settings > Apps if you don't need it.
#>
param([ValidateSet('all', 'app')][string]$Part, [ValidateSet('keep', 'delete')][string]$Presets, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:LOCALAPPDATA 'VSX Deck'
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Q($s) { "'" + ($s -replace "'", "''") + "'" }
function Ask($question) { if ($DryRun) { throw "A dry run can't ask '$question': pass -Part / -Presets instead." }; (Read-Host $question).Trim() }
# Deletes a file or folder; elevated (UAC prompt) if it is somewhere only an administrator may change.
function Remove($path) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    if ($DryRun) { Write-Host "  would remove $path"; return }
    try { Remove-Item -LiteralPath $path -Recurse -Force }
    catch {
        try { Start-Process powershell -ArgumentList '-NoProfile', '-Command', "Remove-Item -LiteralPath $(Q $path) -Recurse -Force" -Verb RunAs -Wait -WindowStyle Hidden }
        catch { Write-Warning 'Administrator prompt declined.' }
    }
    if (Test-Path -LiteralPath $path) { Write-Warning "Could not remove $path (in use? close your DAW / Stream Deck app and run this again)." }
    else { Write-Host "  removed $path" }
}

if (-not $Part) {
    Write-Host 'What to remove?'
    Write-Host '  1) Everything'
    Write-Host '  2) Only the system-wide part (VSX Deck app); keep the DAW plugin and the deck keys'
    $Part = if ((Ask 'Number [1]') -eq '2') { 'app' } else { 'all' }
}
if ($Part -eq 'all' -and -not $Presets) {
    $Presets = if ((Ask 'Delete your saved VSX presets too? [y/N]') -match '^[yY]') { 'delete' } else { 'keep' }
}
if ($DryRun) { Write-Host "Dry run, nothing changed. Would remove ($Part$(if ($Part -eq 'all') { ", presets: $Presets" })):" }

Step 'VSX Deck app'
if (-not $DryRun) { Get-Process VSXDeck -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1 }
Remove (Join-Path ([Environment]::GetFolderPath('Startup')) 'VSX Deck.lnk')
foreach ($f in 'VSXDeck.exe', 'VSX Deck Holder.dll', 'output.txt', 'settings.ini', 'vsxdeck.log') { Remove (Join-Path $app $f) }
if (Test-Path -LiteralPath $app) { Get-ChildItem -LiteralPath $app -Filter 'slot*.png' | ForEach-Object { Remove $_.FullName } }   # key pictures

if ($Part -eq 'all') {
    Step 'VSX Deck Holder (DAW plugin)'
    $holders = @()
    foreach ($t in 'holder.txt', 'vsx.txt') {   # where the installer put it; else beside VSX
        $p = Join-Path $app $t
        if (Test-Path -LiteralPath $p) { $v = (Get-Content -LiteralPath $p -Raw).Trim(); $holders += $(if ($t -eq 'vsx.txt') { Join-Path (Split-Path $v) 'VSX Deck Holder.dll' } else { $v }) }
    }
    $holders += Join-Path $env:ProgramFiles 'VstPlugins\VSX Deck Holder.dll'
    $holders | Where-Object { [IO.Path]::GetFileName($_) -eq 'VSX Deck Holder.dll' } | Select-Object -Unique | ForEach-Object { Remove $_ }
    Write-Host '  In your DAW, remove VSX Deck Holder from the tracks / Monitoring FX it was on.'

    Step 'Stream Deck plugin and "VSX Deck" profile'
    $sd = Get-Process StreamDeck -ErrorAction SilentlyContinue | Select-Object -First 1
    $sdExe = if ($sd) { $sd.Path }
    if ($sd -and -not $DryRun) { $sd | Stop-Process; Start-Sleep 2 }   # it would write the profile back
    Remove (Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.vsxdeck.sdPlugin')
    Get-ChildItem (Join-Path $env:APPDATA 'Elgato\StreamDeck\ProfilesV3') -Filter manifest.json -Recurse -Depth 1 -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -like '*.sdProfile' -and $(try { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Name -eq 'VSX Deck' } catch { $false }) } |
        ForEach-Object { Remove $_.DirectoryName }
    if ($sdExe -and -not $DryRun) { Start-Process $sdExe }

    Step "Deck state in $app"
    if ($Presets -eq 'delete') { Remove $app }
    elseif (Test-Path -LiteralPath $app) {
        Get-ChildItem -LiteralPath $app | Where-Object { $_.Name -notmatch '^slot\d+\.vsx$' -and $_.Name -ne 'cleared' } | ForEach-Object { Remove $_.FullName }
        Write-Host "  kept your presets in $app"
    }
} else {
    Write-Host '  The deck keys and VSX Deck Holder stay. Remove the "VSX Deck App" key from your deck: it has nothing to open now.'
}

Write-Host "`nIf Windows still plays to ""CABLE Input"", set your default output back to your interface (Sound settings)."
Write-Host 'VB-Cable stays installed; remove "VB-Audio Virtual Cable" in Settings > Apps if nothing else uses it.'
