// VSX Deck: system audio through Slate VSX, with VSX's own window (meters, editing) in a tray app.
//   Windows default output = VB-Cable -> capture "CABLE Output" -> VSX Deck Holder (wraps VSX) -> your interface
// The Stream Deck switches rooms / ON-OFF / Bypass through files the plugin watches (see vsxdeckholder.c).
// Usage: VSXDeck.exe [--tray]   (closing the window hides it to the tray; Exit from the tray menu)
// Build: csc /target:winexe /platform:x64 /optimize /unsafe /r:System.Windows.Forms.dll /r:System.Drawing.dll vsxdeck.cs
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Cfg
{
    public const string CaptureName = "CABLE Output";      // VB-Cable recording side (what Windows plays into)
    public const int Block = 256;                          // plugin block size (frames)
    // App, plugin copy, output.txt and the deck state all live in one per-user folder.
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VSX Deck");
    public static readonly string Plugin = Path.Combine(AppDir, "VSX Deck Holder.dll");
    public static string Data(string name) { return Path.Combine(AppDir, name); }
    // Output device: part of its name, from output.txt (written by the installer). Default: Volt 276.
    public static string RenderName
    {
        get
        {
            try { var s = File.ReadAllText(Path.Combine(AppDir, "output.txt")).Trim(); if (s.Length > 0) return s; } catch (IOException) { }
            return "MONITOR L/R";
        }
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
    readonly float rate;
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
    public static IAudioClient Open(int flow, string nameContains, out string fullName)
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
            string name = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "";
            if (name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            fullName = name;
            Guid iid = typeof(IAudioClient).GUID; object o; d.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
            return (IAudioClient)o;
        }
        throw new Exception((flow == 0 ? "playback" : "recording") + " device containing '" + nameContains + "' not found");
    }
}

// ---------------------------------------------------------------- audio engine
sealed class Engine : IDisposable
{
    readonly Vst vst; readonly IAudioClient cap, ren; readonly IAudioCaptureClient capC; readonly IAudioRenderClient renC;
    readonly int rate, capCh, renCh; readonly uint renSize;
    readonly Thread thread; volatile bool run = true;
    public readonly string Info;
    public string Error;

    public Engine()
    {
        string capName, renName;
        cap = Wasapi.Open(1, Cfg.CaptureName, out capName);
        ren = Wasapi.Open(0, Cfg.RenderName, out renName);
        IntPtr cf, rf; cap.GetMixFormat(out cf); ren.GetMixFormat(out rf);
        rate = Marshal.ReadInt32(cf, 4); capCh = Marshal.ReadInt16(cf, 2); renCh = Marshal.ReadInt16(rf, 2);
        int renRate = Marshal.ReadInt32(rf, 4);
        // ponytail: no resampler; both devices must share a rate (set VB-Cable to the interface's rate in Sound settings).
        if (renRate != rate) throw new Exception(string.Format("sample rates differ: {0} {1} Hz vs {2} {3} Hz", capName, rate, renName, renRate));
        Check(cap.Initialize(0, 0, 200000, 0, cf, IntPtr.Zero), "capture init");   // 20 ms buffers, mix format = float32
        Check(ren.Initialize(0, 0, 300000, 0, rf, IntPtr.Zero), "render init");
        ren.GetBufferSize(out renSize);
        Guid ic = typeof(IAudioCaptureClient).GUID, ir = typeof(IAudioRenderClient).GUID; object o;
        cap.GetService(ref ic, out o); capC = (IAudioCaptureClient)o;
        ren.GetService(ref ir, out o); renC = (IAudioRenderClient)o;
        vst = new Vst(Cfg.Plugin, rate);   // on the UI thread: VSX's window and state calls belong to it
        Info = string.Format("{0}  →  VSX  →  {1}   ({2} Hz)", capName, renName, rate);
        thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "audio" };
        thread.Start();
    }
    static void Check(int hr, string what) { if (hr != 0) throw new Exception(what + " failed 0x" + hr.ToString("X8")); }

    void Loop()
    {
        try
        {
            const int B = Cfg.Block;
            var fifo = new float[rate * 2 * 2]; int fifoN = 0;             // interleaved stereo, up to 1 s
            IntPtr inL = Marshal.AllocHGlobal(B * 4), inR = Marshal.AllocHGlobal(B * 4), outL = Marshal.AllocHGlobal(B * 4), outR = Marshal.AllocHGlobal(B * 4);
            IntPtr ins = Marshal.AllocHGlobal(16), outs = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(ins, 0, inL); Marshal.WriteIntPtr(ins, 8, inR); Marshal.WriteIntPtr(outs, 0, outL); Marshal.WriteIntPtr(outs, 8, outR);
            var l = new float[B]; var r = new float[B];
            cap.Start(); ren.Start();
            int target = rate / 100 * 2;                                       // keep ~20 ms queued at the output
            while (run)
            {
                Thread.Sleep(2);
                uint pk; capC.GetNextPacketSize(out pk);
                while (pk > 0)
                {
                    IntPtr data; uint fr, fl; long p1, p2;
                    capC.GetBuffer(out data, out fr, out fl, out p1, out p2);
                    int need = (int)fr * 2;
                    if (fifoN + need > fifo.Length) fifoN = 0;                 // ponytail: overflow (clock drift) -> drop backlog
                    if ((fl & 2) != 0) Array.Clear(fifo, fifoN, need);         // SILENT flag
                    else for (int i = 0; i < fr; i++)
                    {
                        fifo[fifoN + i * 2] = ReadF(data, i * capCh);
                        fifo[fifoN + i * 2 + 1] = ReadF(data, i * capCh + (capCh > 1 ? 1 : 0));
                    }
                    fifoN += need;
                    capC.ReleaseBuffer(fr);
                    capC.GetNextPacketSize(out pk);
                }
                uint pad; ren.GetCurrentPadding(out pad);
                while (fifoN >= B * 2 && pad + B <= renSize && pad < target + B)
                {
                    for (int i = 0; i < B; i++) { l[i] = fifo[i * 2]; r[i] = fifo[i * 2 + 1]; }
                    Buffer.BlockCopy(fifo, B * 2 * 4, fifo, 0, (fifoN - B * 2) * 4); fifoN -= B * 2;
                    Marshal.Copy(l, 0, inL, B); Marshal.Copy(r, 0, inR, B);
                    vst.Process(ins, outs, B);
                    Marshal.Copy(outL, l, 0, B); Marshal.Copy(outR, r, 0, B);
                    IntPtr buf; renC.GetBuffer((uint)B, out buf);
                    for (int i = 0; i < B; i++) for (int c = 0; c < renCh; c++) WriteF(buf, i * renCh + c, c == 0 ? l[i] : c == 1 ? r[i] : 0f);
                    renC.ReleaseBuffer((uint)B, 0);
                    pad += B;
                }
                if (fifoN > rate / 5 * 2) fifoN = 0;                           // ponytail: >200 ms backlog (drift) -> resync
            }
            ren.Stop(); cap.Stop();
        }
        catch (Exception ex) { Error = ex.Message; }
    }
    static unsafe float ReadF(IntPtr p, int i) { return ((float*)p)[i]; }
    static unsafe void WriteF(IntPtr p, int i, float v) { ((float*)p)[i] = v; }

    public Vst Plugin { get { return vst; } }
    public void Dispose() { run = false; thread.Join(1000); vst.Dispose(); }
}

// ---------------------------------------------------------------- window, tray, deck key pictures
sealed class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, int flags);

    readonly Engine engine; readonly Panel editor = new Panel(); readonly NotifyIcon tray = new NotifyIcon();
    readonly Label info = new Label { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    readonly System.Windows.Forms.Timer idle = new System.Windows.Forms.Timer { Interval = 30 };
    bool exiting;
    DateTime seenStatus; bool watching;
    int captureAt = Environment.TickCount + 2500;   // once after start, when VSX has drawn

    public MainForm(bool startHidden)
    {
        Text = "VSX Deck"; FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        Icon = SystemIcons.Application;
        editor.Dock = DockStyle.Fill;
        Controls.Add(editor); Controls.Add(info);
        engine = new Engine();
        info.Text = engine.Info;
        var size = engine.Plugin.EditorSize();
        ClientSize = new Size(size.Width, size.Height + info.Height);
        engine.Plugin.SizeWindow = (w, h) => BeginInvoke((Action)(() => ClientSize = new Size(w, h + info.Height)));
        HandleCreated += (s, e) => engine.Plugin.EditorOpen(editor.Handle);
        idle.Tick += (s, e) =>
        {
            engine.Plugin.EditorIdle();
            if (engine.Error != null) { info.Text = "Audio stopped: " + engine.Error; info.ForeColor = Color.Firebrick; }
            WatchStatus();
            CaptureSlotPictureWhenDue();
        };
        idle.Start();

        tray.Icon = SystemIcons.Application; tray.Text = "VSX Deck"; tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Show VSX", null, (s, e) => ShowWindow());
        tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { exiting = true; Close(); });
        tray.DoubleClick += (s, e) => ShowWindow();
        if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Load += (s, e) => Hide(); }
    }
    void ShowWindow() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } // keep running in the tray
        idle.Stop(); engine.Plugin.EditorClose(); engine.Dispose(); tray.Visible = false;
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
    // 144x144 key: room picture, slot number, room name on a dark band; active = green frame and band.
    static void SaveKey(Bitmap shot, Rectangle src, int slot, string room, bool active, string path)
    {
        var green = Color.FromArgb(31, 143, 78);
        using (var key = new Bitmap(144, 144))
        using (var g = Graphics.FromImage(key))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(shot, new Rectangle(0, 0, 144, 144), src, GraphicsUnit.Pixel);
            if (!active) using (var dim = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, 144, 144);
            using (var band = new SolidBrush(active ? Color.FromArgb(225, green) : Color.FromArgb(190, 0, 0, 0))) g.FillRectangle(band, 0, 98, 144, 46);
            using (var badge = new SolidBrush(active ? green : Color.FromArgb(200, 0, 0, 0))) g.FillEllipse(badge, 6, 6, 40, 40);
            using (var num = new Font("Segoe UI", slot < 10 ? 20 : 15, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(slot.ToString(), num, Brushes.White, new RectangleF(6, 6, 40, 40), sf);
            using (var name = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                g.DrawString(room, name, Brushes.White, new RectangleF(4, 99, 136, 44), sf);
            if (active) using (var pen = new Pen(green, 8)) g.DrawRectangle(pen, 4, 4, 136, 136);
            string tmp = path + ".tmp";
            key.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            File.Copy(tmp, path, true); File.Delete(tmp);
        }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (new Mutex(true, "VSXDeck.single", out created))
        {
            if (!created) return; // already running
            Application.EnableVisualStyles();
            try { Application.Run(new MainForm(Array.IndexOf(args, "--tray") >= 0)); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "VSX Deck", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
