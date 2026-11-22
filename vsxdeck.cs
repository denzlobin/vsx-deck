// VSX Deck: system audio through Slate VSX, with VSX's own window (meters, editing) in a tray app.
//   Windows default output = VB-Cable -> capture "CABLE Output" -> VSX Deck Holder (wraps VSX) -> your interface
// The Stream Deck switches rooms / ON-OFF / Bypass through files the plugin watches (see vsxdeckholder.c).
// Usage: VSXDeck.exe [--tray]   (closing the window hides it to the tray; Exit from the tray menu)
// Build: build.ps1
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Shown by Windows wherever it names the app (e.g. the microphone indicator, Task Manager, file properties).
[assembly: System.Reflection.AssemblyTitle("VSX Deck")]
[assembly: System.Reflection.AssemblyProduct("VSX Deck")]
[assembly: System.Reflection.AssemblyDescription("Slate VSX for system audio, controlled from the Stream Deck")]
[assembly: System.Reflection.AssemblyVersion("0.2.3")]                 // kit version: keep in step with manifest.json and Install.ps1
[assembly: System.Reflection.AssemblyInformationalVersion("0.2.3")]   // "Product version" as shown (file version stays 4-part)

static class Cfg
{
    public const string CaptureName = "CABLE Output";      // VB-Cable recording side (what Windows plays into)
    public const int Block = 256;                          // plugin block size (frames)
    // App, plugin copy, output.txt and the deck state all live in one per-user folder.
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSX Deck");
    public static readonly string Plugin = Path.Combine(AppDir, "VSX Deck Holder.dll");
    public static string Data(string name) { return Path.Combine(AppDir, name); }
    // Output device: (part of) its name, from output.txt (installer / Settings). Default: Volt 276.
    public static string RenderName
    {
        get
        {
            try { var s = File.ReadAllText(Path.Combine(AppDir, "output.txt")).Trim(); if (s.Length > 0) return s; } catch (IOException) { }
            return "MONITOR L/R";
        }
        set { File.WriteAllText(Path.Combine(AppDir, "output.txt"), value); }
    }

    // settings.ini: plain key=value lines (latency, pictures).
    static string Ini { get { return Path.Combine(AppDir, "settings.ini"); } }
    public static string Get(string key, string fallback)
    {
        try { foreach (var l in File.ReadAllLines(Ini)) if (l.StartsWith(key + "=")) return l.Substring(key.Length + 1).Trim(); }
        catch (IOException) { }
        return fallback;
    }
    public static void Set(string key, string value)
    {
        var lines = File.Exists(Ini) ? File.ReadAllLines(Ini).Where(l => !l.StartsWith(key + "=")).ToList() : new System.Collections.Generic.List<string>();
        lines.Add(key + "=" + value);
        File.WriteAllLines(Ini, lines);
    }
    public static readonly string[] LatencyNames = { "low", "normal", "safe" };
    public static int LatencyMs { get { switch (Get("latency", "normal")) { case "low": return 10; case "safe": return 40; default: return 20; } } }
    public static bool Pictures { get { return Get("pictures", "1") != "0"; } }

    // Start at login = a shortcut in the user's Startup folder (the installer makes the same one).
    public static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "VSX Deck.lnk"); } }
    public static void SetStartAtLogin(bool on)
    {
        if (!on) { File.Delete(StartupLink); return; }
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupLink });
        var t = lnk.GetType();
        t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
        t.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "--tray" });
        t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
    }
}

// ---------------------------------------------------------------- VST2 plugin (minimal host side)
sealed class Vst : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr HostCb(IntPtr e, int op, int idx, IntPtr v, IntPtr p, float o);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Dispatch(IntPtr e, int op, int idx, IntPtr v, IntPtr p, float o);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ProcessRep(IntPtr e, IntPtr inputs, IntPtr outputs, int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PluginMain(HostCb cb);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string p);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);

    readonly IntPtr eff; readonly Dispatch dispatch; readonly ProcessRep process;
    readonly HostCb keep;          // keeps the callback alive while the plugin holds it
    float rate;
    public Action<int, int> SizeWindow;

    public Vst(string path, float rate)
    {
        this.rate = rate;
        IntPtr lib = LoadLibraryW(path);
        if (lib == IntPtr.Zero) throw new Exception("cannot load " + path + " (error " + Marshal.GetLastWin32Error() + ")");
        IntPtr main = GetProcAddress(lib, "VSTPluginMain");
        if (main == IntPtr.Zero) throw new Exception("no VSTPluginMain in " + path);
        keep = Host;
        eff = ((PluginMain)Marshal.GetDelegateForFunctionPointer(main, typeof(PluginMain)))(keep);
        if (eff == IntPtr.Zero || Marshal.ReadInt32(eff) != 0x56737450) throw new Exception("plugin did not start (is VSX installed and licensed?)");
        dispatch = (Dispatch)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(eff, 8), typeof(Dispatch));
        process = (ProcessRep)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(eff, 120), typeof(ProcessRep));
        Call(0);                                     // effOpen
        Call(10, 0, IntPtr.Zero, IntPtr.Zero, rate); // effSetSampleRate
        Call(11, 0, (IntPtr)Cfg.Block);              // effSetBlockSize
        Call(12, 0, (IntPtr)1);                      // effMainsChanged on
        Call(71);                                    // effStartProcess
    }
    IntPtr Call(int op, int idx = 0, IntPtr v = default(IntPtr), IntPtr p = default(IntPtr), float o = 0) { return dispatch(eff, op, idx, v, p, o); }

    IntPtr Host(IntPtr e, int op, int idx, IntPtr v, IntPtr p, float o)
    {
        switch (op)
        {
            case 1: return (IntPtr)2400;                                                 // audioMasterVersion
            case 15: if (SizeWindow != null) SizeWindow(idx, (int)v); return (IntPtr)1;  // audioMasterSizeWindow
            case 16: return (IntPtr)(long)rate;                                          // audioMasterGetSampleRate
            case 17: return (IntPtr)Cfg.Block;                                           // audioMasterGetBlockSize
            case 32: case 33:                                                            // vendor / product string
                if (p != IntPtr.Zero) { var b = System.Text.Encoding.ASCII.GetBytes("VSX Deck\0"); Marshal.Copy(b, 0, p, b.Length); }
                return (IntPtr)1;
            default: return IntPtr.Zero;
        }
    }

    public void Process(IntPtr inputs, IntPtr outputs, int n) { process(eff, inputs, outputs, n); }
    public void SetRate(float newRate)   // new output device: same VSX instance, new sample rate
    {
        if (newRate == rate) return;
        rate = newRate;
        Call(72); Call(12, 0, IntPtr.Zero);          // stop process, mains off
        Call(10, 0, IntPtr.Zero, IntPtr.Zero, rate);
        Call(12, 0, (IntPtr)1); Call(71);            // mains on, start process
    }
    public Size EditorSize()
    {
        IntPtr pp = Marshal.AllocHGlobal(8);
        try
        {
            Call(13, 0, IntPtr.Zero, pp);                // effEditGetRect -> ERect* (short top, left, bottom, right)
            IntPtr r = Marshal.ReadIntPtr(pp);
            if (r == IntPtr.Zero) return new Size(800, 500);
            return new Size(Marshal.ReadInt16(r, 6) - Marshal.ReadInt16(r, 2), Marshal.ReadInt16(r, 4) - Marshal.ReadInt16(r, 0));
        }
        finally { Marshal.FreeHGlobal(pp); }
    }
    public void EditorOpen(IntPtr hwnd) { Call(14, 0, IntPtr.Zero, hwnd); }
    public void EditorIdle() { Call(19); }
    public void EditorClose() { Call(15); }
    public void Dispose() { Call(72); Call(12, 0, IntPtr.Zero); Call(1); } // stop, mains off, close
}

// ---------------------------------------------------------------- WASAPI (shared mode, polled)
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c); }
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDevice d); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore ps);
}
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore { int GetCount(out int n); int GetAt(int i, out PropKey k); [PreserveSig] int GetValue(ref PropKey k, out PropVariant v); }
[StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public int pid; }
[StructLayout(LayoutKind.Sequential)] struct PropVariant { public short vt; short r1, r2, r3; public IntPtr p; IntPtr p2; }
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int share, int flags, long dur, long period, IntPtr fmt, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint n); [PreserveSig] int GetStreamLatency(out long l); [PreserveSig] int GetCurrentPadding(out uint p);
    [PreserveSig] int IsFormatSupported(int s, IntPtr f, IntPtr c); [PreserveSig] int GetMixFormat(out IntPtr f); [PreserveSig] int GetDevicePeriod(out long d, out long m);
    [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr h);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
}
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long pos, out long qpc);
    [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint n);
}
[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient { [PreserveSig] int GetBuffer(uint frames, out IntPtr data); [PreserveSig] int ReleaseBuffer(uint frames, uint flags); }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

static class Wasapi
{
    // Active devices of a flow (0 = playback, 1 = recording) with their friendly names.
    static System.Collections.Generic.IEnumerable<Tuple<string, IMMDevice>> Devices(int flow)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection col; en.EnumAudioEndpoints(flow, 1 /*ACTIVE*/, out col);
        int n; col.GetCount(out n);
        var key = new PropKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
        for (int i = 0; i < n; i++)
        {
            IMMDevice d; col.Item(i, out d);
            IPropertyStore ps; d.OpenPropertyStore(0, out ps);
            PropVariant v; ps.GetValue(ref key, out v);
            yield return Tuple.Create(v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "", d);
        }
    }
    public static string[] Names(int flow) { return Devices(flow).Select(t => t.Item1).ToArray(); }
    public static IAudioClient Open(int flow, string nameContains, out string fullName)
    {
        foreach (var t in Devices(flow))
        {
            if (t.Item1.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            fullName = t.Item1;
            Guid iid = typeof(IAudioClient).GUID; object o; t.Item2.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
            return (IAudioClient)o;
        }
        throw new Exception((flow == 0 ? "playback" : "recording") + " device containing '" + nameContains + "' not found");
    }
}

// ---------------------------------------------------------------- audio engine
// Audio connection: capture -> VSX -> output. Restarted for new settings; the VSX instance outlives it.
sealed class Engine : IDisposable
{
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int index);
    readonly Vst vst; readonly IAudioClient cap, ren; readonly IAudioCaptureClient capC; readonly IAudioRenderClient renC;
    readonly int rate, capCh, renCh, target; readonly uint renSize;
    readonly Thread thread; volatile bool run = true;
    public readonly string Info;
    public string Error;
    // Glitch counters, shown in the header: output ran dry with audio waiting (late) or with none (cable gap),
    // VB-Cable reported a discontinuity, or a backlog was trimmed (resync).
    public int Late, CableGap, Discontinuity, Resync;
    public int Glitches { get { return Late + CableGap + Discontinuity + Resync; } }
    // One line per dropout with timings, written to vsxdeck.log by the window (the audio thread never touches files).
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new System.Collections.Concurrent.ConcurrentQueue<string>();

    public Engine(Func<int, Vst> vstFor)
    {
        string capName, renName;
        cap = Wasapi.Open(1, Cfg.CaptureName, out capName);
        ren = Wasapi.Open(0, Cfg.RenderName, out renName);
        IntPtr cf, rf; cap.GetMixFormat(out cf); ren.GetMixFormat(out rf);
        rate = Marshal.ReadInt32(cf, 4); capCh = Marshal.ReadInt16(cf, 2); renCh = Marshal.ReadInt16(rf, 2);
        int renRate = Marshal.ReadInt32(rf, 4);
        // ponytail: no resampler; both devices must share a rate (set VB-Cable to the interface's rate in Sound settings).
        if (renRate != rate) throw new Exception(string.Format("sample rates differ: {0} {1} Hz vs {2} {3} Hz", capName, rate, renName, renRate));
        int ms = Cfg.LatencyMs;
        target = rate * ms / 1000;                                                   // audio kept queued at the output
        Check(cap.Initialize(0, 0, 200000, 0, cf, IntPtr.Zero), "capture init");     // 20 ms, mix format = float32
        Check(ren.Initialize(0, 0, (2 * ms + 10) * 10000L, 0, rf, IntPtr.Zero), "render init");
        ren.GetBufferSize(out renSize);
        Guid ic = typeof(IAudioCaptureClient).GUID, ir = typeof(IAudioRenderClient).GUID; object o;
        cap.GetService(ref ic, out o); capC = (IAudioCaptureClient)o;
        ren.GetService(ref ir, out o); renC = (IAudioRenderClient)o;
        vst = vstFor(rate);   // on the UI thread: VSX's window and state calls belong to it
        Info = string.Format("{0}   →   VSX   →   {1}   ·   {2:0.#} kHz   ·   {3} ms", capName, renName, rate / 1000.0, ms);
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "audio" };
        thread.Start();
    }
    static void Check(int hr, string what) { if (hr != 0) throw new Exception(what + " failed 0x" + hr.ToString("X8")); }
    // In the loop: any failure (e.g. 0x88890004, device unplugged or switched off) stops it; the window reconnects.
    static void Ok(int hr, string what) { if (hr < 0) throw new Exception(what + " failed 0x" + hr.ToString("X8")); }
    static double Ms(long ticks) { return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

    void Loop()
    {
        try
        {
            int task = 0; AvSetMmThreadCharacteristics("Pro Audio", ref task);   // Windows' audio scheduling class
            const int B = Cfg.Block;
            var fifo = new float[rate * 2 * 2]; int fifoN = 0;             // interleaved stereo, up to 1 s
            IntPtr inL = Marshal.AllocHGlobal(B * 4), inR = Marshal.AllocHGlobal(B * 4), outL = Marshal.AllocHGlobal(B * 4), outR = Marshal.AllocHGlobal(B * 4);
            IntPtr ins = Marshal.AllocHGlobal(16), outs = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(ins, 0, inL); Marshal.WriteIntPtr(ins, 8, inR); Marshal.WriteIntPtr(outs, 0, outL); Marshal.WriteIntPtr(outs, 8, outR);
            var l = new float[B]; var r = new float[B];
            cap.Start(); ren.Start();
            bool playing = false; uint lastPad = 0;
            long prev = System.Diagnostics.Stopwatch.GetTimestamp(), lastProc = 0;
            while (run)
            {
                Thread.Sleep(1);                                               // ~1 ms with timeBeginPeriod(1), see Program
                long now = System.Diagnostics.Stopwatch.GetTimestamp(), gap = now - prev; prev = now;
                uint pk; Ok(capC.GetNextPacketSize(out pk), "capture");
                while (pk > 0)
                {
                    IntPtr data; uint fr, fl; long p1, p2;
                    Ok(capC.GetBuffer(out data, out fr, out fl, out p1, out p2), "capture");
                    if ((fl & 1) != 0 && playing) Discontinuity++;             // DATA_DISCONTINUITY
                    int need = (int)fr * 2;
                    if (fifoN + need > fifo.Length) { fifoN = 0; Resync++; }   // ponytail: overflow -> drop backlog
                    if ((fl & 2) != 0) Array.Clear(fifo, fifoN, need);         // SILENT flag
                    else for (int i = 0; i < fr; i++)
                    {
                        fifo[fifoN + i * 2] = ReadF(data, i * capCh);
                        fifo[fifoN + i * 2 + 1] = ReadF(data, i * capCh + (capCh > 1 ? 1 : 0));
                    }
                    fifoN += need;
                    Ok(capC.ReleaseBuffer(fr), "capture");
                    Ok(capC.GetNextPacketSize(out pk), "capture");
                }
                uint pad; Ok(ren.GetCurrentPadding(out pad), "output");
                if (playing && pad == 0 && lastPad > 0)
                {
                    if (fifoN >= B * 2) Late++; else CableGap++;
                    Events.Enqueue(string.Format("{0}: loop away {1:0.0} ms, VSX took {2:0.0} ms, {3:0.0} ms was queued, {4:0.0} ms waiting",
                        fifoN >= B * 2 ? "late" : "cable gap", Ms(gap), Ms(lastProc), lastPad * 1000.0 / rate, fifoN / 2 * 1000.0 / rate));
                }
                lastProc = 0;
                while (fifoN >= B * 2 && pad + B <= renSize && pad < target + B)
                {
                    for (int i = 0; i < B; i++) { l[i] = fifo[i * 2]; r[i] = fifo[i * 2 + 1]; }
                    Buffer.BlockCopy(fifo, B * 2 * 4, fifo, 0, (fifoN - B * 2) * 4); fifoN -= B * 2;
                    Marshal.Copy(l, 0, inL, B); Marshal.Copy(r, 0, inR, B);
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    vst.Process(ins, outs, B);
                    lastProc += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    Marshal.Copy(outL, l, 0, B); Marshal.Copy(outR, r, 0, B);
                    IntPtr buf; Ok(renC.GetBuffer((uint)B, out buf), "output");
                    for (int i = 0; i < B; i++) for (int c = 0; c < renCh; c++) WriteF(buf, i * renCh + c, c == 0 ? l[i] : c == 1 ? r[i] : 0f);
                    Ok(renC.ReleaseBuffer((uint)B, 0), "output");
                    pad += B; playing = true;
                }
                lastPad = pad;
                if (fifoN > rate / 5 * 2)                                      // >200 ms backlog (clock drift): keep the newest
                {
                    int keep = target * 2;
                    Buffer.BlockCopy(fifo, (fifoN - keep) * 4, fifo, 0, keep * 4); fifoN = keep; Resync++;
                }
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }
    static unsafe float ReadF(IntPtr p, int i) { return ((float*)p)[i]; }
    static unsafe void WriteF(IntPtr p, int i, float v) { ((float*)p)[i] = v; }

    public void Dispose()
    {
        run = false; thread.Join(1000);
        try { cap.Stop(); ren.Stop(); } catch (Exception) { }   // the device may be gone
        Marshal.ReleaseComObject(cap); Marshal.ReleaseComObject(ren);
    }
}

// ---------------------------------------------------------------- window, tray, deck key pictures
sealed class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, int flags);

    Vst vst; Engine engine; readonly Panel editor = new Panel(); readonly NotifyIcon tray = new NotifyIcon();
    // Header strip in the app's charcoal palette: the audio route, muted, and a Settings button.
    public static readonly Color Charcoal = Color.FromArgb(0x17, 0x18, 0x1c), Muted = Color.FromArgb(0xa7, 0xa8, 0xae),
        Key = Color.FromArgb(0x2b, 0x2c, 0x31), Light = Color.FromArgb(0xec, 0xea, 0xe6), Warn = Color.FromArgb(0xff, 0x8a, 0x7a);
    readonly Panel header = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Charcoal };
    readonly Label info = new Label
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
        ForeColor = Muted, Font = new Font("Segoe UI", 9f), AutoEllipsis = true
    };
    readonly Button gear = new Button
    {
        Dock = DockStyle.Right, Width = 40, Text = "", Font = new Font("Segoe MDL2 Assets", 11f),
        FlatStyle = FlatStyle.Flat, ForeColor = Muted, Cursor = Cursors.Hand, TabStop = false
    };
    string infoText = ""; int shownGlitches;
    int retryAt = -1;   // audio failed (device off/unplugged): reconnect when due
    SettingsForm settings;
    readonly System.Windows.Forms.Timer idle = new System.Windows.Forms.Timer { Interval = 30 };
    bool exiting;
    DateTime seenStatus; bool watching;
    int captureAt = Environment.TickCount + 2500;   // once after start, when VSX has drawn

    public MainForm(bool startHidden)
    {
        Text = "VSX Deck"; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        Icon = AppIcon(SystemInformation.IconSize);
        Font = new Font("Segoe UI", 9f); BackColor = Charcoal;
        HandleCreated += (s, e) => { int on = 1; DwmSetWindowAttribute(Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref on, 4); };
        editor.Dock = DockStyle.Fill;
        gear.FlatAppearance.BorderSize = 0; gear.FlatAppearance.MouseOverBackColor = Key;
        gear.Click += (s, e) => OpenSettings();
        new ToolTip().SetToolTip(gear, "Settings");
        header.Controls.Add(info); header.Controls.Add(gear);
        Controls.Add(editor); Controls.Add(header);
        engine = new Engine(VstFor);
        SetInfo(engine.Info, false);
        var size = vst.EditorSize();
        ClientSize = new Size(size.Width, size.Height + header.Height);
        vst.SizeWindow = (w, h) => BeginInvoke((Action)(() => ClientSize = new Size(w, h + header.Height)));
        HandleCreated += (s, e) => vst.EditorOpen(editor.Handle);
        idle.Tick += (s, e) =>
        {
            vst.EditorIdle();
            if (engine != null && engine.Error != null)
            {
                string why = engine.Error;
                Program.Log("audio stopped: " + why);
                engine.Dispose(); engine = null; retryAt = Environment.TickCount + 2000;
                SetInfo("Audio stopped (" + why + "), reconnecting…", true);
            }
            if (engine == null && retryAt != -1 && Environment.TickCount - retryAt >= 0) RestartAudio();
            ShowGlitches();
            WatchStatus();
            CaptureSlotPictureWhenDue();
        };
        idle.Start();

        tray.Icon = AppIcon(SystemInformation.SmallIconSize); tray.Text = "VSX Deck"; tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show VSX", null, (s, e) => ShowWindow());
        tray.ContextMenuStrip.Items.Add("Settings…", null, (s, e) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { exiting = true; Close(); });
        tray.DoubleClick += (s, e) => ShowWindow();
        if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Load += (s, e) => Hide(); }
        HandleCreated += (s, e) => ListenForShow();   // BeginInvoke needs the window handle
    }
    // One VSX instance for the app's lifetime: a new audio connection only re-rates it (no reload, no burst).
    Vst VstFor(int rate) { if (vst == null) vst = new Vst(Cfg.Plugin, rate); else vst.SetRate(rate); return vst; }
    public void RestartAudio()
    {
        if (engine != null) engine.Dispose();
        engine = null; shownGlitches = -1; retryAt = -1;
        try { engine = new Engine(VstFor); SetInfo(engine.Info, false); Program.Log("audio: " + engine.Info); }
        catch (Exception ex) { retryAt = Environment.TickCount + 2000; SetInfo("Waiting for audio: " + ex.Message, true); }
    }
    void SetInfo(string text, bool error) { infoText = text; info.Text = text; info.ForeColor = error ? Warn : Muted; }
    void ShowGlitches()   // crackle check: dropouts since the audio connection started
    {
        if (engine == null) return;
        string ev; while (engine.Events.TryDequeue(out ev)) Program.Log(ev);
        if (engine.Glitches == shownGlitches) return;
        shownGlitches = engine.Glitches;
        info.Text = shownGlitches == 0 ? infoText : string.Format("{0}   ·   dropouts {1}", infoText, shownGlitches);
    }
    void OpenSettings()
    {
        if (settings != null) { settings.Activate(); return; }
        using (settings = new SettingsForm(this)) settings.ShowDialog();
        settings = null;
    }
    void ShowWindow()
    {
        ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal;
        // Windows only lets the foreground app hand out focus: borrow its input queue for a moment.
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringToFront(); SetForegroundWindow(Handle); Activate();
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    // Another launch (deck key, Start menu) signals this event to bring the window up.
    public const string ShowEvent = "VSXDeck.show";
    EventWaitHandle showEvent;
    void ListenForShow()
    {
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        ThreadPool.RegisterWaitForSingleObject(showEvent, (s, timedOut) => BeginInvoke((Action)ShowWindow), null, -1, false);
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    static Icon AppIcon(Size size)   // vsxdeck.ico is embedded by build.ps1; Windows picks the frame for the size
    {
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("vsxdeck.ico"))
            return s != null ? new Icon(s, size) : SystemIcons.Application;
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } // keep running in the tray
        idle.Stop(); vst.EditorClose(); if (engine != null) engine.Dispose(); vst.Dispose(); tray.Visible = false;
        base.OnFormClosing(e);
    }

    // The plugin writes status.txt after applying a change; its window has settled well within 1.5 s.
    void WatchStatus()
    {
        var fi = new FileInfo(Cfg.Data("status.txt"));
        if (!fi.Exists) return;
        if (watching && fi.LastWriteTimeUtc == seenStatus) return;
        if (watching) captureAt = Environment.TickCount + 1500;
        watching = true; seenStatus = fi.LastWriteTimeUtc;
    }

    // Deck key pictures: a square crop of the room picture VSX shows for the active slot, saved as slotN.png /
    // slotN-active.png. Made on this PC from the user's own VSX for their own deck; never part of the kit.
    void CaptureSlotPictureWhenDue()
    {
        if (captureAt == -1 || Environment.TickCount - captureAt < 0) return;
        captureAt = -1;
        if (!Cfg.Pictures) return;
        try
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            string status = File.ReadAllText(Cfg.Data("status.txt"));
            if (!status.StartsWith("on") || status.Contains("bypass 1")) return;          // bypass dims the picture
            int slot; if (!int.TryParse(File.ReadAllText(Cfg.Data("lastslot.txt")).Trim(), out slot)) return;
            string preset = Cfg.Data("slot" + slot + ".vsx"), png = Cfg.Data("slot" + slot + ".png");
            if (!File.Exists(preset) || !File.ReadAllBytes(Cfg.Data("active.vsx")).SequenceEqual(File.ReadAllBytes(preset))) return;
            if (File.Exists(png) && File.GetLastWriteTimeUtc(png) > File.GetLastWriteTimeUtc(preset)) return;   // up to date
            string room = status.Split('\n')[0].Substring(2).Trim();
            using (var shot = new Bitmap(Math.Max(1, editor.Width), Math.Max(1, editor.Height)))
            {
                using (var g = Graphics.FromImage(shot)) { IntPtr dc = g.GetHdc(); PrintWindow(editor.Handle, dc, 2 /*PW_RENDERFULLCONTENT*/); g.ReleaseHdc(dc); }
                int pictureH = (int)(shot.Height * 0.72);                 // room picture, above VSX's control strip
                int side = Math.Min(pictureH, shot.Width);
                var src = new Rectangle((shot.Width - side) / 2, (pictureH - side) / 2, side, side);
                SaveKey(shot, src, slot, room, false, png);
                SaveKey(shot, src, slot, room, true, Cfg.Data("slot" + slot + "-active.png"));
            }
        }
        catch (IOException) { }
    }
    // 144x144 key: room picture, preset number badge, room name on a dark band; active = green frame and badge.
    static void SaveKey(Bitmap shot, Rectangle src, int slot, string room, bool active, string path)
    {
        Color green = Color.FromArgb(0x35, 0xd0, 0x7f), charcoal = Color.FromArgb(0x17, 0x18, 0x1c);
        using (var key = new Bitmap(144, 144))
        using (var g = Graphics.FromImage(key))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(shot, new Rectangle(0, 0, 144, 144), src, GraphicsUnit.Pixel);
            if (!active) using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, 144, 144);
            using (var band = new SolidBrush(Color.FromArgb(230, charcoal))) g.FillRectangle(band, 0, 98, 144, 46);
            var badgeRect = new RectangleF(8, 8, slot < 10 ? 36 : 44, 30);
            using (var badgePath = RoundRect(badgeRect, 8))
            using (var badge = new SolidBrush(active ? green : Color.FromArgb(220, charcoal))) g.FillPath(badge, badgePath);
            using (var num = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var ink = new SolidBrush(active ? charcoal : Color.White))
                g.DrawString(slot.ToString(), num, ink, badgeRect, sf);
            using (var name = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                g.DrawString(room, name, Brushes.White, new RectangleF(4, 99, 136, 44), sf);
            if (active) using (var pen = new Pen(green, 8)) g.DrawRectangle(pen, 4, 4, 136, 136);
            string tmp = path + ".tmp";
            key.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            File.Copy(tmp, path, true); File.Delete(tmp);
        }
    }
    static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

// ---------------------------------------------------------------- settings
sealed class SettingsForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    static readonly System.Text.RegularExpressions.Regex PresetName = new System.Text.RegularExpressions.Regex(@"^slot\d+\.vsx$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    const int W = 420;
    int y = 16;

    public SettingsForm(MainForm main)
    {
        Text = "VSX Deck Settings"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; Icon = main.Icon;
        BackColor = MainForm.Charcoal; ForeColor = MainForm.Light; Font = new Font("Segoe UI", 9.75f);
        HandleCreated += (s, e) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        Heading("Audio");
        string[] outputs = Wasapi.Names(0).Where(n => n.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
        var output = Combo(outputs);
        output.SelectedIndex = Array.FindIndex(outputs, n => n.IndexOf(Cfg.RenderName, StringComparison.OrdinalIgnoreCase) >= 0);
        output.SelectedIndexChanged += (s, e) => { Cfg.RenderName = (string)output.SelectedItem; main.RestartAudio(); };
        Row("Output", output);
        var latency = Combo(new[] { "Low (10 ms)", "Normal (20 ms)", "Safe (40 ms)" });
        latency.SelectedIndex = Math.Max(0, Array.IndexOf(Cfg.LatencyNames, Cfg.Get("latency", "normal")));
        latency.SelectedIndexChanged += (s, e) => { Cfg.Set("latency", Cfg.LatencyNames[latency.SelectedIndex]); main.RestartAudio(); };
        Row("Latency", latency);
        Note("More latency = more cushion against crackles and dropouts.");

        Heading("General");
        var login = Check("Start VSX Deck at login (in the tray)", File.Exists(Cfg.StartupLink));
        login.CheckedChanged += (s, e) => Cfg.SetStartAtLogin(login.Checked);

        Heading("Stream Deck keys");
        var pictures = Check("Room pictures on preset keys", Cfg.Pictures);
        pictures.CheckedChanged += (s, e) => { Cfg.Set("pictures", pictures.Checked ? "1" : "0"); if (!pictures.Checked) DeletePictures(); };
        Buttons(Btn("Redraw pictures", (s, e) => DeletePictures()));
        Note("Pictures are taken again as you select presets with the VSX window open.");

        Heading("Presets");
        Buttons(Btn("Back up…", (s, e) => Backup()), Btn("Restore…", (s, e) => Restore()),
                Btn("Open folder", (s, e) => System.Diagnostics.Process.Start("explorer.exe", "\"" + Cfg.AppDir + "\"")));

        y += 12;
        var close = Btn("Close", (s, e) => Close());
        close.Location = new Point(W - 16 - close.PreferredSize.Width, y);
        Controls.Add(close); CancelButton = close;
        ClientSize = new Size(W, y + close.Height + 16);
    }

    // ---- layout: one column, top to bottom
    void Heading(string text)
    {
        if (y > 16) y += 10;
        Controls.Add(new Label { Text = text.ToUpperInvariant(), Location = new Point(16, y), AutoSize = true, ForeColor = MainForm.Muted, Font = new Font("Segoe UI Semibold", 8.25f) });
        y += 24;
    }
    void Row(string label, Control c)
    {
        Controls.Add(new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true });
        c.Location = new Point(96, y); c.Width = W - 96 - 16; Controls.Add(c); y += c.Height + 8;
    }
    void Note(string text)
    {
        Controls.Add(new Label { Text = text, Location = new Point(16, y), Size = new Size(W - 32, 18), ForeColor = MainForm.Muted, Font = new Font("Segoe UI", 8.25f) });
        y += 22;
    }
    void Buttons(params Button[] buttons)
    {
        int x = 16;
        foreach (var b in buttons) { b.Location = new Point(x, y); Controls.Add(b); x += b.PreferredSize.Width + 8; }
        y += buttons[0].Height + 8;
    }
    static ComboBox Combo(string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = MainForm.Key, ForeColor = MainForm.Light };
        c.Items.AddRange(items); return c;
    }
    CheckBox Check(string text, bool on)
    {
        var c = new CheckBox { Text = text, Checked = on, Location = new Point(16, y), AutoSize = true };
        Controls.Add(c); y += 28; return c;
    }
    static Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(8, 0, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Key, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x3d, 0x40, 0x49); b.Click += click; return b;
    }

    // ---- actions
    static void DeletePictures() { foreach (var f in Directory.GetFiles(Cfg.AppDir, "slot*.png")) File.Delete(f); }

    void Backup()
    {
        var presets = Directory.GetFiles(Cfg.AppDir, "slot*.vsx").Where(f => PresetName.IsMatch(Path.GetFileName(f))).ToArray();
        if (presets.Length == 0) { MessageBox.Show(this, "No presets saved yet.", Text); return; }
        using (var d = new SaveFileDialog { Filter = "VSX Deck presets (*.zip)|*.zip", FileName = "VSX Deck presets " + DateTime.Now.ToString("yyyy-MM-dd") + ".zip" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            using (var fs = new FileStream(d.FileName, FileMode.Create))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var f in presets)
                {
                    var bytes = File.ReadAllBytes(f);
                    using (var e = zip.CreateEntry(Path.GetFileName(f)).Open()) e.Write(bytes, 0, bytes.Length);
                }
            MessageBox.Show(this, presets.Length + " presets backed up.", Text);
        }
    }

    // Takes only slotN.vsx entries that look like VSX state (1 MB cap, "VC2!" header). Presets it replaces move to
    // cleared\ like a held key does, so a restore can be undone.
    void Restore()
    {
        var found = new System.Collections.Generic.Dictionary<string, byte[]>();
        using (var d = new OpenFileDialog { Filter = "VSX Deck presets (*.zip)|*.zip" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using (var fs = File.OpenRead(d.FileName))
                using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
                    foreach (var e in zip.Entries)
                    {
                        if (!PresetName.IsMatch(e.FullName) || e.Length < 8 || e.Length > 1 << 20) continue;
                        var bytes = new byte[e.Length]; int n = 0;   // never read past the declared size
                        using (var st = e.Open()) for (int k; n < bytes.Length && (k = st.Read(bytes, n, bytes.Length - n)) > 0; ) n += k;
                        if (n == bytes.Length &&System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "VC2!") found[e.FullName.ToLowerInvariant()] = bytes;
                    }
            }
            catch (InvalidDataException) { }
        }
        if (found.Count == 0) { MessageBox.Show(this, "No VSX Deck presets in that file.", Text); return; }
        int replaced = found.Keys.Count(n => File.Exists(Cfg.Data(n)));
        string ask = found.Count + " presets to restore." + (replaced > 0 ? "\n" + replaced + " current presets with the same numbers move to the cleared folder." : "");
        if (MessageBox.Show(this, ask, Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        string cleared = Cfg.Data("cleared"), stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(cleared);
        foreach (var kv in found)
        {
            string path = Cfg.Data(kv.Key), slot = Path.GetFileNameWithoutExtension(kv.Key);
            if (File.Exists(path)) File.Move(path, Path.Combine(cleared, slot + "-" + stamp + ".vsx"));
            File.WriteAllBytes(path + ".tmp", kv.Value); File.Move(path + ".tmp", path);
            File.Delete(Cfg.Data(slot + ".png")); File.Delete(Cfg.Data(slot + "-active.png"));
        }
        MessageBox.Show(this, found.Count + " presets restored. Press a preset key to load one.", Text);
    }
}

static class Program
{
    [STAThread]
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    static void Main(string[] args)
    {
        timeBeginPeriod(1);   // 1 ms sleeps for the audio loop (Windows 11 default is ~15.6 ms: too coarse, crackles)
        bool created, tray = Array.IndexOf(args, "--tray") >= 0;
        using (new Mutex(true, "VSXDeck.single", out created))
        {
            if (!created)   // already running: a normal launch (deck key, Start menu) brings its window up
            {
                EventWaitHandle show;
                if (!tray && EventWaitHandle.TryOpenExisting(MainForm.ShowEvent, out show)) { AllowSetForegroundWindow(-1); show.Set(); show.Dispose(); }
                return;
            }
            Application.EnableVisualStyles();
            if (System.Diagnostics.Process.GetProcessesByName("VSXSystemwide").Length > 0 &&
                MessageBox.Show("VSX Systemwide is running. With both, system audio may go through VSX twice.\n\nQuit VSX Systemwide first (its tray icon), or start VSX Deck anyway?",
                    "VSX Deck", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            // Started at login, devices or files may not be ready yet: retry quietly for ~30 s before complaining.
            for (int attempt = 1; ; attempt++)
            {
                try { Application.Run(new MainForm(tray)); return; }
                catch (Exception ex)
                {
                    Log(string.Format("attempt {0}: {1} (plugin file present: {2})", attempt, ex.Message, File.Exists(Cfg.Plugin)));
                    if (!tray || attempt >= 6) { MessageBox.Show(ex.Message, "VSX Deck", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    Thread.Sleep(5000);
                }
            }
        }
    }
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);   // -1 = any
    public static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Cfg.AppDir, "vsxdeck.log"), DateTime.Now.ToString("s") + "  " + line + Environment.NewLine); }
        catch (IOException) { }
    }
}
