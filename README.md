# VSX Deck

Control **Slate VSX** headphone correction from an **Elgato Stream Deck**, for all system audio and in your DAW:

- **VSX ON/OFF**: green = on (with the room name), grey = off. One key, showing what the live VSX actually does.
- **Room slots 1–10**: switch rooms instantly; the active slot is green and shows the room's picture.
- **Save**: store what you dialed in the VSX window into the selected slot.
- **Bypass**: VSX's own level-matched bypass, for A/B.
- **VSX Deck** tray app: VSX's own window with live meters, following the deck.

```
apps ─► Windows default output "CABLE Input" (VB-Cable) ─► VSX Deck [VSX Deck Holder ─► VSX] ─► your interface
DAW  ─► Monitoring FX: VSX Deck Holder ─► VSX
deck ─► %LOCALAPPDATA%\VSX Deck\ ─► every VSX Deck Holder applies it to its running VSX (no reload, no volume burst)
```

## Install

1. Install **Slate VSX** (its VST2, `C:\Program Files\VstPlugins\VSX.dll`, is used) and the **Elgato Stream Deck** app.
2. Run, from this folder:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```
   It offers to install **VB-Cable** (admin prompt) and asks which output to play to. It then installs the
   **VSX Deck** app (starts at login, in the tray), offers **VSX Deck Holder** for your DAW (admin prompt; close
   the DAW first when updating it), and adds the Stream Deck plugin with a **VSX Deck** profile. Re-running is safe.
3. Set Windows' default **output** to **CABLE Input (VB-Audio Virtual Cable)**. The installer opens Sound settings.
4. DAW (optional): rescan VST plugins and put **VSX Deck Holder** where VSX was, e.g. Reaper's
   View → Monitoring FX. Monitoring over ASIO bypasses VB-Cable, so DAW audio is never processed twice.

Profile layout (5×3 deck): rows 1–2 = slots 1–10; bottom row = Bypass (left), Save, VSX ON/OFF (right).

## Use

- **Switch rooms** with the slot keys. Everything running VSX Deck Holder follows: the VSX Deck app and your DAW.
- **New preset**: press an empty slot (blue outline, "save here"; audio stays as is), dial the room in a VSX
  window, press **Save**. The key flashes ✓ (⚠ if no VSX window is open).
  **Update one**: select it, tweak, **Save**. If two VSX windows are open (app and DAW), close the one you aren't
  tweaking; the last to save wins.
- **Clear a preset**: hold its slot key for 1.5 s. Recoverable: it moves to `%LOCALAPPDATA%\VSX Deck\cleared\`
  (copy it back as `slotN.vsx`). Slot keys act on release, so a short press still selects.
- **ON** returns to the last slot used (slot 1 if that one is empty). OFF is a 30 ms fade to dry; VSX keeps running.
- **Bypass** stays on across room switches and is never saved into a slot.
- **Amber dashed outline** on ON/OFF or Bypass = not confirmed by a running VSX (the VSX Deck app isn't running).
- **Tray icon**: Show VSX / Exit. Closing the window only hides it.
- Latency of the system-audio path: roughly 20–40 ms. Fine for music and video, noticeable in games.

### Room pictures on slot keys

When the VSX Deck window is visible and settles on a slot, it saves a small thumbnail of the room picture VSX is
showing (`slotN.png`, `slotN-active.png` in `%LOCALAPPDATA%\VSX Deck`). They are made on your PC from **your own
licensed VSX, for your own deck**. The artwork is Slate's: this repository contains none, and don't share the
generated files.

## How it works

| File | What |
|---|---|
| `vsxdeckholder.c` | **VSX Deck Holder**: VST2 plugin wrapping VSX. Keeps one instance alive and applies the deck state to it (`effSetChunk`); OFF = fade to dry; bypass = flag in VSX's state; answers Save; reports `status.txt`. Its editor reopens VSX's window behind a snapshot after each change (VSX only reads its state when the window opens). |
| `vsxdeck.cs` | **VSX Deck** app: VB-Cable → VSX Deck Holder → output (WASAPI), VSX window, tray, key pictures. |
| `com.vsxdeck.sdPlugin\` | Stream Deck keys (Node 20, no packages). `node plugin.js` runs its self-check. |
| `Install.ps1`, `build.ps1` | Setup; rebuild `bin\` (needs Visual Studio C++ Build Tools for the plugin). |
| `bin\` | Ready-built `VSXDeck.exe` and `VSX Deck Holder.dll`. |

Everything lives in one per-user folder, `%LOCALAPPDATA%\VSX Deck`: the app, `output.txt`, and the deck state
`slotN.vsx` (VSX state), `active.vsx` (empty = OFF), `lastslot.txt`, `bypass.txt`, `save.req`, `status.txt`.
Only your account can write it, so no other user can feed settings to your VSX.

## Remove

Tray → Exit. Remove VSX Deck Holder from your DAW and delete `C:\Program Files\VstPlugins\VSX Deck Holder.dll`.
Delete `%LOCALAPPDATA%\VSX Deck` and `shell:startup\VSX Deck.lnk`; remove the VSX Deck profile and plugin in the
Stream Deck app. Uninstall VB-Cable (Apps) and set the default output back to your interface.
