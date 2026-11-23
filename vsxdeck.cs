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
[assembly: System.Reflection.AssemblyVersion("0.3.3")]                 // kit version: keep in step with manifest.json and Install.ps1
[assembly: System.Reflection.AssemblyInformationalVersion("0.3.3")]   // "Product version" as shown (file version stays 4-part)

static class Cfg
{
    public const string CaptureName = "CABLE Output";      // VB-Cable recording side (what Windows plays into)
    public const int Block = 256;                          // plugin block size (frames)
    // App, plugin copy, output.txt and the deck state all live in one per-user folder.
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSX Deck");
    public static readonly string Plugin = Path.Combine(AppDir, "VSX Deck Holder.dll");
    public static string Data(string name) { return Path.Combine(AppDir, name); }
    // Output device: (part of) its name, from output.txt (installer / Settings). "" = not chosen yet.
    public static string RenderName
    {
        get
        {
            try { return File.ReadAllText(Path.Combine(AppDir, "output.txt")).Trim(); } catch (IOException) { return ""; }
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
    // Cable and output both move audio 10 ms at a time: a cushion under ~25 ms can run dry.
    public static int LatencyMs { get { switch (Get("latency", "normal")) { case "low": return 20; case "safe": return 50; default: return 30; } } }
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
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection c);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice d);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDevice d); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore ps);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}
// Undocumented but unchanged since Windows 7; Sound settings' "Default Format" goes through it.
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr fmt);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
    // unused, but they hold their places in the interface before SetDefaultEndpoint
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
}
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
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
        // Best match first: the exact name (Settings saves it), then the name before its "(driver)" suffix (the
        // installer saves that), then any name containing it; so "Speakers" doesn't pick "Speakers 2 (USB)".
        var all = Devices(flow).ToList();
        var t = all.FirstOrDefault(d => d.Item1.Equals(nameContains, StringComparison.OrdinalIgnoreCase))
             ?? all.FirstOrDefault(d => d.Item1.StartsWith(nameContains + " (", StringComparison.OrdinalIgnoreCase))
             ?? all.FirstOrDefault(d => d.Item1.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
        if (t != null)
        {
            fullName = t.Item1;
            Guid iid = typeof(IAudioClient).GUID; object o; t.Item2.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
            return (IAudioClient)o;
        }
        throw new Exception((flow == 0 ? "playback" : "recording") + " device containing '" + nameContains + "' not found");
    }

    // Windows' default output (what apps play to), or "" if there is none.
    public static string DefaultOutput()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            IMMDevice d; if (en.GetDefaultAudioEndpoint(0, 1 /*eMultimedia*/, out d) != 0 || d == null) return "";
            IPropertyStore ps; if (d.OpenPropertyStore(0, out ps) != 0) return "";
            var key = new PropKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            PropVariant v; ps.GetValue(ref key, out v);
            return v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "";
        }
        finally { Marshal.ReleaseComObject(en); }
    }
    // Makes CABLE Input Windows' default output, as Sound settings' "Set as default" does (console and multimedia;
    // the communications default is left alone).
    public static void PlayIntoCable()
    {
        var dev = Devices(0).FirstOrDefault(t => t.Item1.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);
        if (dev == null) throw new Exception("VB-Cable (CABLE Input) not found");
        string id; dev.Item2.GetId(out id);
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try { foreach (int role in new[] { 0, 1 }) { int hr = pc.SetDefaultEndpoint(id, role); if (hr != 0) throw new Exception("setting the default output failed 0x" + hr.ToString("X8")); } }
        finally { Marshal.ReleaseComObject(pc); }
    }

    // Puts both VB-Cable devices at this sample rate, as Sound settings > device > Advanced > Default Format would.
    // True if anything changed.
    public static bool MatchCable(int rate)
    {
        bool changed = false;
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var side in new[] { Tuple.Create(0, "CABLE Input"), Tuple.Create(1, Cfg.CaptureName) })
            {
                var dev = Devices(side.Item1).FirstOrDefault(t => t.Item1.IndexOf(side.Item2, StringComparison.OrdinalIgnoreCase) >= 0);
                if (dev == null) continue;
                string id; IntPtr mix, fmt;
                if (dev.Item2.GetId(out id) != 0 || pc.GetMixFormat(id, out mix) != 0) continue;
                bool same = Marshal.ReadInt32(mix, 4) == rate;
                if (!same && pc.GetDeviceFormat(id, 0, out fmt) == 0)
                {
                    IntPtr newFmt = WithRate(fmt, rate), newMix = WithRate(mix, rate);
                    int hr = pc.SetDeviceFormat(id, newFmt, newMix);
                    Marshal.FreeCoTaskMem(newFmt); Marshal.FreeCoTaskMem(newMix); Marshal.FreeCoTaskMem(fmt);
                    if (hr != 0)
                    {
                        Marshal.FreeCoTaskMem(mix);
                        throw new Exception(string.Format("couldn't switch {0} to {1:0.#} kHz (0x{2:X8}): set CABLE Input and CABLE Output to {1:0.#} kHz in Sound settings",
                            dev.Item1, rate / 1000.0, hr));
                    }
                    changed = true;
                }
                Marshal.FreeCoTaskMem(mix);
            }
        }
        finally { Marshal.ReleaseComObject(pc); }
        return changed;
    }
    static IntPtr WithRate(IntPtr fmt, int rate)   // copy of a WAVEFORMATEX(TENSIBLE) at another sample rate
    {
        int size = 18 + Marshal.ReadInt16(fmt, 16);
        var b = new byte[size]; Marshal.Copy(fmt, b, 0, size);
        BitConverter.GetBytes(rate).CopyTo(b, 4);                                   // nSamplesPerSec
        BitConverter.GetBytes(rate * BitConverter.ToInt16(b, 12)).CopyTo(b, 8);     // nAvgBytesPerSec = rate * nBlockAlign
        IntPtr p = Marshal.AllocCoTaskMem(size); Marshal.Copy(b, 0, p, size); return p;
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
        string capName, renName; IntPtr cf = IntPtr.Zero, rf = IntPtr.Zero;
        try
        {
            // Never guess: "" would match the first device, which can be VB-Cable itself (audio in a loop).
            if (Cfg.RenderName.Length == 0) throw new Exception("no output chosen: pick one in Settings");
            ren = Wasapi.Open(0, Cfg.RenderName, out renName);
            if (renName.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception(renName + " can't be the output: pick your interface in Settings");
            ren.GetMixFormat(out rf);
            int renRate = Marshal.ReadInt32(rf, 4);
            // No resampler: VB-Cable runs at the output's rate. Switching it (fresh install, or a DAW changed the
            // interface's rate) restarts its streams, so connect on the next try.
            if (Wasapi.MatchCable(renRate)) throw new Exception(string.Format("VB-Cable switched to {0:0.#} kHz to match {1}", renRate / 1000.0, renName));
            cap = Wasapi.Open(1, Cfg.CaptureName, out capName);
            cap.GetMixFormat(out cf);
            rate = Marshal.ReadInt32(cf, 4); capCh = Marshal.ReadInt16(cf, 2); renCh = Marshal.ReadInt16(rf, 2);
            if (renRate != rate)
                throw new Exception(string.Format("{0} runs at {1:0.#} kHz, {2} at {3:0.#} kHz: set CABLE Input and CABLE Output to {3:0.#} kHz in Sound settings",
                    capName, rate / 1000.0, renName, renRate / 1000.0));
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
        catch
        {
            // While waiting for a device this runs every 2 s: don't leave audio clients behind.
            foreach (object c in new object[] { capC, renC, cap, ren }) if (c != null) Marshal.ReleaseComObject(c);
            throw;
        }
        finally { if (cf != IntPtr.Zero) Marshal.FreeCoTaskMem(cf); if (rf != IntPtr.Zero) Marshal.FreeCoTaskMem(rf); }
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
            // Jitter buffer: play only once a full latency's worth is in hand (queued at the output + waiting), and
            // after running dry collect it again. Without this the cushion was whatever happened to be waiting at
            // start (often 10 ms = one output period) and every late cable packet cracked.
            bool playing = false, primed = false; uint lastPad = 0;
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
                    primed = false;
                    Events.Enqueue(string.Format("{0}: loop away {1:0.0} ms, VSX took {2:0.0} ms, {3:0.0} ms was queued, {4:0.0} ms waiting",
                        fifoN >= B * 2 ? "late" : "cable gap", Ms(gap), Ms(lastProc), lastPad * 1000.0 / rate, fifoN / 2 * 1000.0 / rate));
                }
                lastProc = 0;
                if (!primed && pad + fifoN / 2 >= target) primed = true;
                while (primed && fifoN >= B * 2 && pad + B <= renSize && pad < target + B)
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
    // Layout numbers are written for 100 % scaling; S() converts them to pixels at the screen's scaling.
    public static readonly float Dpi = DpiNow();
    static float DpiNow() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; }
    public static int S(int px) { return (int)Math.Round(px * Dpi / 96f); }
    public static int U(int pixels) { return (int)Math.Round(pixels * 96f / Dpi); }   // pixels -> 100 % units
    public static readonly Color Charcoal = Color.FromArgb(0x17, 0x18, 0x1c), Muted = Color.FromArgb(0xa7, 0xa8, 0xae),
        Key = Color.FromArgb(0x2b, 0x2c, 0x31), Light = Color.FromArgb(0xec, 0xea, 0xe6), Warn = Color.FromArgb(0xff, 0x8a, 0x7a);
    readonly Panel header = new Panel { Dock = DockStyle.Top, Height = S(34), BackColor = Charcoal };
    readonly Label info = new Label
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(12), 0, 0, 0),
        ForeColor = Muted, Font = new Font("Segoe UI", 9f), AutoEllipsis = true
    };
    string infoText = ""; int shownGlitches; bool infoError, systemwide; int systemwideAt = Environment.TickCount;
    string defaultOut; int defaultAt = Environment.TickCount;   // Windows' default output, checked every 2 s
    readonly Button playHere = new Button
    {
        Text = "Play through VSX", Dock = DockStyle.Fill, Visible = false, FlatStyle = FlatStyle.Flat, TabStop = false, Cursor = Cursors.Hand,
        ForeColor = Color.FromArgb(0xec, 0xea, 0xe6), BackColor = Color.FromArgb(0x1f, 0x20, 0x24), Font = new Font("Segoe UI", 8.25f)
    };
    int retryAt = -1;   // audio failed (device off/unplugged): reconnect when due
    Form settings, aboutBox;
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
        // Settings, About, Support at the right, as in S-1 Utility: small bordered buttons, glyphs from Segoe MDL2.
        var tools = new Panel { Dock = DockStyle.Right, Width = S(3 * 32 + 2 * 4 + 8), BackColor = Charcoal };
        Tool(tools, 0, "\uE713", "Settings", OpenSettings);
        Tool(tools, 1, "\uE946", "About VSX Deck", OpenAbout);
        Tool(tools, 2, "\uEB51", "Support development (Ko-fi)", () => AboutForm.Open(AboutForm.SupportUrl));
        playHere.FlatAppearance.BorderColor = Color.FromArgb(0x3d, 0x40, 0x49);
        playHere.FlatAppearance.MouseOverBackColor = Key; playHere.FlatAppearance.MouseDownBackColor = Charcoal;
        playHere.Click += (s, e) =>
        {
            try { Wasapi.PlayIntoCable(); defaultAt = Environment.TickCount; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message + "\n\nSet CABLE Input as the default output in Sound settings.", "VSX Deck"); }
        };
        new ToolTip().SetToolTip(playHere, "Make VB-Cable (CABLE Input) Windows' default output, so system audio goes through VSX");
        var playBox = new Panel { Dock = DockStyle.Right, Width = S(140), Padding = new Padding(0, S(4), S(4), S(4)) };
        playBox.Controls.Add(playHere);
        header.Controls.Add(info); header.Controls.Add(playBox); header.Controls.Add(tools);   // last added docks rightmost
        Controls.Add(editor); Controls.Add(header);
        // VSX first, audio second: with the output device off or unplugged the window still opens, says
        // "Waiting for audio" and connects when the device appears (the idle timer retries every 2 s).
        vst = new Vst(Cfg.Plugin, 48000);
        RestartAudio();
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
            CheckSystemwide();
            CheckDefaultOutput();
            WatchStatus();
            CaptureSlotPictureWhenDue();
        };
        idle.Start();

        tray.Icon = AppIcon(SystemInformation.SmallIconSize); tray.Text = "VSX Deck"; tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show VSX", null, (s, e) => ShowWindow());
        tray.ContextMenuStrip.Items.Add("Settings…", null, (s, e) => OpenSettings());
        tray.ContextMenuStrip.Items.Add("About…", null, (s, e) => OpenAbout());
        tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { exiting = true; Close(); });
        tray.DoubleClick += (s, e) => ShowWindow();
        if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Load += (s, e) => Hide(); }
        HandleCreated += (s, e) => ListenForShow();   // BeginInvoke needs the window handle
    }
    static void Tool(Panel bar, int i, string glyph, string tip, Action click)   // 32 x 26, 4 apart, centred in the header
    {
        var b = new Button
        {
            Font = new Font("Segoe MDL2 Assets", 9f), FlatStyle = FlatStyle.Flat, TabStop = false, Cursor = Cursors.Hand,
            ForeColor = Color.FromArgb(0xcc, 0xcc, 0xd2), BackColor = Color.FromArgb(0x1f, 0x20, 0x24),
            Bounds = new Rectangle(S(i * 36), S(4), S(32), S(26))
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x3d, 0x40, 0x49);
        b.FlatAppearance.MouseOverBackColor = Key; b.FlatAppearance.MouseDownBackColor = Charcoal;
        // Drawn as a shape centred on its ink: as button text the icon font's line metrics push glyphs off centre.
        b.Paint += (s, e) =>
        {
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            using (var brush = new SolidBrush(b.ForeColor))
            {
                path.AddString(glyph, b.Font.FontFamily, (int)FontStyle.Regular, b.Font.SizeInPoints * Dpi / 72f, Point.Empty, StringFormat.GenericTypographic);
                RectangleF r = path.GetBounds();
                using (var m = new System.Drawing.Drawing2D.Matrix())
                {
                    m.Translate((float)Math.Round((b.Width - r.Width) / 2f - r.X), (float)Math.Round((b.Height - r.Height) / 2f - r.Y));
                    path.Transform(m);
                }
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        };
        b.Click += (s, e) => click();
        new ToolTip().SetToolTip(b, tip);
        bar.Controls.Add(b);
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
    void SetInfo(string text, bool error) { infoText = text; infoError = error; UpdateHeader(); }
    void UpdateHeader()   // route or error, dropouts, and a VSX Systemwide warning
    {
        string t = shownGlitches > 0 ? string.Format("{0}   ·   dropouts {1}", infoText, shownGlitches) : infoText;
        bool notCable = !string.IsNullOrEmpty(defaultOut) && defaultOut.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) < 0;
        if (notCable) t = "Windows plays to " + defaultOut + ", not VB-Cable: VSX Deck hears nothing.   ·   " + t;
        if (systemwide) t = "VSX Systemwide is running too: system audio may go through VSX twice. Quit one of them.   ·   " + t;
        info.Text = t; info.ForeColor = infoError || systemwide || notCable ? Warn : Muted;
        playHere.Visible = notCable && !systemwide;   // with Systemwide running, switching would fight it
    }
    void ShowGlitches()   // crackle check: dropouts since the audio connection started
    {
        if (engine == null) return;
        string ev; while (engine.Events.TryDequeue(out ev)) Program.Log(ev);
        if (engine.Glitches == shownGlitches) return;
        shownGlitches = engine.Glitches;
        UpdateHeader();
    }
    // Every 2 s: is Windows still playing into VB-Cable? It changes when the user picks another output, or when
    // VSX Systemwide quits and restores the output it found. No automatic switch back: that may be on purpose.
    void CheckDefaultOutput()
    {
        if (Environment.TickCount - defaultAt < 0) return;
        defaultAt = Environment.TickCount + 2000;
        string now; try { now = Wasapi.DefaultOutput(); } catch (Exception) { return; }
        if (now != defaultOut) { defaultOut = now; UpdateHeader(); }
    }
    void CheckSystemwide()   // every 10 s: VSX Systemwide started after VSX Deck gets the same warning as at start
    {
        if (Environment.TickCount - systemwideAt < 0) return;
        systemwideAt = Environment.TickCount + 10000;
        bool on = System.Diagnostics.Process.GetProcessesByName("VSXSystemwide").Length > 0;
        if (on != systemwide) { systemwide = on; UpdateHeader(); }
    }
    void OpenSettings() { OpenOnce(ref settings, () => new SettingsForm(this)); }
    void OpenAbout() { OpenOnce(ref aboutBox, () => new AboutForm(Icon)); }
    static void OpenOnce(ref Form f, Func<Form> make)   // one at a time; a second request brings it forward
    {
        if (f != null) { f.Activate(); return; }
        using (f = make()) f.ShowDialog();
        f = null;
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
    // Dark title bar and the app's palette, shared by the dialogs.
    internal static void ToDpi(Form f) { f.Scale(new SizeF(MainForm.Dpi / 96f, MainForm.Dpi / 96f)); }
    internal static void Dark(Form f)   // also: lay out in 100 % units, ToDpi() at the end scales it
    {
        f.AutoScaleMode = AutoScaleMode.None;
        f.BackColor = MainForm.Charcoal; f.ForeColor = MainForm.Light; f.Font = new Font("Segoe UI", 9.75f);
        f.HandleCreated += (s, e) => { int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); };
    }
    const int W = 420;
    int y = 16;

    public SettingsForm(MainForm main)
    {
        Text = "VSX Deck Settings"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; Icon = main.Icon;
        Dark(this);

        Heading("Audio");
        string[] outputs = Wasapi.Names(0).Where(n => n.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) < 0).ToArray();
        var output = Combo(outputs);
        output.SelectedIndex = Array.FindIndex(outputs, n => n.IndexOf(Cfg.RenderName, StringComparison.OrdinalIgnoreCase) >= 0);
        output.SelectedIndexChanged += (s, e) => { Cfg.RenderName = (string)output.SelectedItem; main.RestartAudio(); };
        Row("Output", output);
        var latency = Combo(new[] { "Low (20 ms)", "Normal (30 ms)", "Safe (50 ms)" });
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
        Buttons(Btn("Open folder", (s, e) => System.Diagnostics.Process.Start("explorer.exe", "\"" + Cfg.AppDir + "\"")));

        y += 12;
        var close = Btn("Close", (s, e) => Close());
        close.Location = new Point(W - 16 - MainForm.U(close.PreferredSize.Width), y);
        Controls.Add(close); CancelButton = close;
        ClientSize = new Size(W, y + MainForm.U(close.Height) + 16);
        SettingsForm.ToDpi(this);
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
        c.Location = new Point(96, y); c.Width = W - 96 - 16; Controls.Add(c); y += MainForm.U(c.Height) + 8;
    }
    void Note(string text)
    {
        Controls.Add(new Label { Text = text, Location = new Point(16, y), Size = new Size(W - 32, 18), ForeColor = MainForm.Muted, Font = new Font("Segoe UI", 8.25f) });
        y += 22;
    }
    void Buttons(params Button[] buttons)
    {
        int x = 16;
        foreach (var b in buttons) { b.Location = new Point(x, y); Controls.Add(b); x += MainForm.U(b.PreferredSize.Width) + 8; }
        y += MainForm.U(buttons[0].Height) + 8;
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
    internal static Button Btn(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(8, 0, 8, 0), FlatStyle = FlatStyle.Flat, BackColor = MainForm.Key, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x3d, 0x40, 0x49); b.Click += click; return b;
    }

    // ---- actions
    static void DeletePictures() { foreach (var f in Directory.GetFiles(Cfg.AppDir, "slot*.png")) File.Delete(f); }
}

// ---------------------------------------------------------------- About / Support
sealed class AboutForm : Form
{
    const string RepoUrl = "https://github.com/denzlobin/vsx-deck";
    public const string SupportUrl = "https://ko-fi.com/dzl0";
    const int W = 400;
    int y = 28;

    public AboutForm(Icon icon)
    {
        Text = "About VSX Deck"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; Icon = icon;
        SettingsForm.Dark(this);

        Line("V S X   D E C K", new Font("Segoe UI", 18f, FontStyle.Bold), MainForm.Light, 4);
        Line("v" + ((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(GetType().Assembly,
            typeof(System.Reflection.AssemblyInformationalVersionAttribute))).InformationalVersion,new Font("Consolas", 9f), MainForm.Muted, 16);
        Controls.Add(new Label { BackColor = MainForm.Key, Bounds = new Rectangle((W - 120) / 2, y, 120, 1) }); y += 17;
        Line("by Denis Zlobin", Font, MainForm.Muted, 2);
        var repo = new LinkLabel
        {
            Text = "github.com/denzlobin/vsx-deck", Font = new Font("Consolas", 9f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, y, W, 20), LinkColor = Color.FromArgb(0x35, 0xd0, 0x7f), ActiveLinkColor = MainForm.Light, LinkBehavior = LinkBehavior.HoverUnderline
        };
        repo.LinkClicked += (s, e) => Open(RepoUrl);
        Controls.Add(repo); y += 34;
        Line("Licensed under MIT", new Font("Segoe UI", 8.25f), MainForm.Muted, 8);
        Line("Not affiliated with or endorsed by Slate Digital or Steven Slate Audio.\nVSX is their product; you need your own licensed copy.\n" +
             "VSX is used only through the standard VST2 plugin interface;\nno VSX code was decompiled or modified. Don't run with VSX Systemwide.",
            new Font("Segoe UI", 8.25f), Color.FromArgb(0x77, 0x78, 0x7e), 20, 66);

        KeyPreview = true; KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };   // no buttons: Esc or the title bar closes it
        ClientSize = new Size(W, y + 8);   // the last line's gap + 8 = the 28 at the top
        SettingsForm.ToDpi(this);
    }
    void Line(string text, Font font, Color color, int gap, int height = 0)   // one centred line (or two with \n)
    {
        int h = height > 0 ? height : (int)Math.Ceiling(font.GetHeight(96f)) + 2;   // 100 % units
        Controls.Add(new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, y, W, h) });
        y += h + gap;
    }
    public static void Open(string url) { try { System.Diagnostics.Process.Start(url); } catch (Exception) { } }
}

static class Program
{
    [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread]
    static void Main(string[] args)
    {
        // Sharp text at 125 % / 150 % display scaling: draw at the real DPI instead of being stretched by Windows.
        // ponytail: system-aware, not per-monitor: sharp at the main monitor's scaling; on a monitor with another scaling
        // Windows stretches the window (soft, but laid out right). Per-monitor would need re-layout on every DPI change.
        try { SetProcessDpiAwarenessContext((IntPtr)(-2)); }   // DPI_AWARENESS_CONTEXT_SYSTEM_AWARE (Windows 10 1703+)
        catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
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
            // A surprise in a timer or click handler (a locked file, a COM hiccup) is logged and the app carries on;
            // .NET's own dialog would offer "Quit" and take system audio with it.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("error: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("fatal: " + e.ExceptionObject);
            Application.EnableVisualStyles();
            if (System.Diagnostics.Process.GetProcessesByName("VSXSystemwide").Length > 0 &&
                MessageBox.Show("VSX Systemwide is running. With both, system audio may go through VSX twice.\n\nQuit VSX Systemwide first (its tray icon), or start VSX Deck anyway?",
                    "VSX Deck", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            // Audio devices that aren't there yet are handled by the window ("Waiting for audio"); this only
            // catches VSX itself failing to load.
            try { Application.Run(new MainForm(tray)); }
            catch (Exception ex)
            {
                Log(string.Format("start failed: {0} (plugin file present: {1})", ex.Message, File.Exists(Cfg.Plugin)));
                MessageBox.Show(ex.Message, "VSX Deck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);   // -1 = any
    public static void Log(string line)
    {
        try
        {
            string path = Path.Combine(Cfg.AppDir, "vsxdeck.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 1 << 20)   // past 1 MB: keep the newer half
            {
                string all = File.ReadAllText(path);
                int cut = all.IndexOf('\n', all.Length / 2);
                File.WriteAllText(path, cut < 0 ? "" : all.Substring(cut + 1));
            }
            File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + line + Environment.NewLine);
        }
        catch (Exception) { }   // logging must never be what fails
    }
}
