// MagicFn: tray companion for an Apple Magic Keyboard running the MagicFnDriver driver (folder driver) on Windows.
//  - The driver reports Fn+F1 / Fn+F2 as standard HID brightness keys. Windows handles them itself
//    (flyout, step, key repeat) but cannot reach an external monitor, so this app mirrors the Windows
//    brightness level to the monitor over DDC/CI.
//  - Fn+Esc (the driver turns it into Pause) and the Lock button can lock the PC or put it to sleep.
//  - The Lock button can instead switch the monitor to another input over DDC/CI.
//  - F-row default mode and autostart in a settings window.
// Build: csc /nologo /codepage:65001 /target:winexe /out:MagicFn.exe MagicFn.cs
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

static class Settings
{
    const string AppKey = @"Software\MagicFn", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "MagicFn";

    public static bool MediaByDefault; // false: F1-F12 are function keys and Fn gives media; true: the reverse
    public static int FnEsc;           // 0 nothing, 1 lock, 2 sleep, 3 Task Manager
    public static int LockKey = 1;     // same values, for the Lock button
    public static bool LockKeySwitchesInput; // the Lock button switches the monitor input before its own action
    public static bool InputSwitchAvailable; // machine-wide, set by "MagicFnSetup lg": the input switch is proven on LG monitors only
    public static bool ExternalMonitor = true; // mirror the Windows brightness level to DDC/CI monitors (not needed on a laptop panel)
    public static int InputSource;     // index into Monitors.Inputs that the Lock button switches to; 0 = none

    public static void Load()
    {
        using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\MagicFn"))
            InputSwitchAvailable = k != null && (int)k.GetValue("InputSwitch", 0) != 0;
        using (var k = Registry.CurrentUser.OpenSubKey(AppKey))
        {
            if (k == null) return;
            MediaByDefault = (int)k.GetValue("MediaByDefault", 0) != 0;
            ExternalMonitor = (int)k.GetValue("ExternalMonitor", 1) != 0;
            FnEsc = (int)k.GetValue("FnEsc", 0);
            LockKey = (int)k.GetValue("LockKey", 1);
            LockKeySwitchesInput = (int)k.GetValue("LockKeySwitchesInput", 0) != 0;
            InputSource = Math.Max(0, Math.Min(Monitors.Inputs.Length - 1, (int)k.GetValue("InputSource", 0)));
        }
    }

    public static void Save()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
        {
            k.SetValue("MediaByDefault", MediaByDefault ? 1 : 0);
            k.SetValue("ExternalMonitor", ExternalMonitor ? 1 : 0);
            k.SetValue("FnEsc", FnEsc);
            k.SetValue("LockKey", LockKey);
            k.SetValue("LockKeySwitchesInput", LockKeySwitchesInput ? 1 : 0);
            k.SetValue("InputSource", InputSource);
        }
    }

    public static bool Autostart
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\""); else k.DeleteValue(RunName, false);
        }
    }
}

// MagicFnDriver adds a vendor-defined HID collection (page FF00, usage 0x10, report 0x54) to the keyboard, open to
// non-admin users: its input report is one byte of key bits (Lock button, F3-F6 of the media layer), its feature
// report is the F-row mode. Those keys come this way and not as keystrokes because Windows kept the driver's F16
// and Win+Tab pressed after their release.
static class Driver
{
    const byte ReportId = 0x54;
    const ushort UsagePage = 0xFF00, Usage = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3, RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
    }

    [DllImport("user32.dll")] static extern int GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetRawInputDeviceInfo(IntPtr h, uint cmd, System.Text.StringBuilder data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] report, int length);

    public static bool Connected;
    public const int Lock = 0x01, F3 = 0x02, F4 = 0x04, F5 = 0x08, F6 = 0x10;
    public static Action<int> KeysPressed = delegate { };   // bits of the keys that just went down; raised on the listener thread
    static Thread listener;

    static string FindPath()
    {
        uint n = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(null, ref n, size);
        var list = new RAWINPUTDEVICELIST[n];
        GetRawInputDeviceList(list, ref n, size);
        foreach (var d in list)
        {
            uint len = 512; var name = new System.Text.StringBuilder((int)len);
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, name, ref len) <= 0) continue;
            string path = name.ToString();
            if (path.IndexOf("VID_05AC", StringComparison.OrdinalIgnoreCase) < 0) continue;
            using (var h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                IntPtr pp; HIDP_CAPS caps;
                if (h.IsInvalid || !HidD_GetPreparsedData(h, out pp)) continue;
                HidP_GetCaps(pp, out caps); HidD_FreePreparsedData(pp);
                if (caps.UsagePage == UsagePage && caps.Usage == Usage) return path;
            }
        }
        return null;
    }

    // The driver forgets the F-row mode on replug, sleep or restart, so it is re-sent whenever devices change.
    public static void ApplySettings()
    {
        string path = FindPath();
        Connected = false;
        if (path == null) return;
        using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            Connected = !h.IsInvalid && HidD_SetFeature(h, new byte[] { ReportId, (byte)(Settings.MediaByDefault ? 1 : 0) }, 2);

        // The listener reads on a handle of its own: a blocking read would hold up SetFeature on a shared one.
        if (Connected && (listener == null || !listener.IsAlive))
        {
            listener = new Thread(() => Listen(path)) { IsBackground = true };
            listener.Start();
        }
    }

    // Ends when the keyboard goes away; the next ApplySettings starts it again.
    static void Listen(string path)
    {
        try
        {
            using (var h = CreateFile(path, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                using (var stream = new FileStream(h, FileAccess.Read, 2, false))
                {
                    var report = new byte[2];
                    int down = 0;
                    while (stream.Read(report, 0, 2) == 2)
                    {
                        if (report[0] != ReportId) continue;
                        int pressed = report[1] & ~down;
                        down = report[1];
                        if (pressed != 0) KeysPressed(pressed);
                    }
                }
            }
        }
        catch (IOException) { }
    }
}

// Do Not Disturb has neither a shortcut nor a documented API; this is the shell's own interface behind the bell button.
// shortcut: undocumented, proven on Windows 11 23H2 only; if a Windows update drops it the key silently does nothing.
static class DoNotDisturb
{
    const string Off = "Microsoft.QuietHoursProfile.Unrestricted", On = "Microsoft.QuietHoursProfile.PriorityOnly";

    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IQuietHoursSettings
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetUserSelectedProfile();
        void SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }

    public static void Toggle()
    {
        try
        {
            var settings = (IQuietHoursSettings)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("f53321fa-34f8-4b7f-b9a3-361877cb94cf"), true));
            try { settings.SetUserSelectedProfile(settings.GetUserSelectedProfile() == Off ? On : Off); }
            finally { Marshal.ReleaseComObject(settings); }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
    }
}

// External-monitor brightness over DDC/CI, as a percentage of each monitor's own range.
static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
    // shortcut: the four inputs of the one tested monitor; read the list from the capabilities string (VCP 60) if another monitor needs more.
    public static readonly string[] Inputs = { "None", "DisplayPort 1", "DisplayPort 2", "HDMI 1", "HDMI 2" };
    static readonly uint[] inputCodes = { 0, 0x0F, 0x10, 0x11, 0x12 };   // MCCS, VCP 60
    static readonly byte[] lgInputCodes = { 0, 0xD0, 0xD1, 0x90, 0x91 }; // LG, VCP F4

    static readonly AutoResetEvent wake = new AutoResetEvent(false);
    static int pending = -1, applied = -1;

    static void ForEach(Action<IntPtr> action)
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, hdc, rect, data) =>
        {
            uint count;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, mons)) return true;
            foreach (var m in mons) action(m.Handle);
            DestroyPhysicalMonitors(count, mons);
            return true;
        }, IntPtr.Zero);
    }

    // Sets every DDC/CI monitor to `percent` (percent < 0: change nothing). Returns the first monitor's level before the call, or -1.
    static int Visit(int percent)
    {
        int before = -1;
        ForEach(h =>
        {
            uint min, cur, max;
            if (!GetMonitorBrightness(h, out min, out cur, out max) || max <= min) return; // no DDC/CI
            if (before < 0) before = (int)Math.Round((cur - min) * 100.0 / (max - min));
            if (percent >= 0) SetMonitorBrightness(h, min + (uint)Math.Round((max - min) * percent / 100.0));
        });
        return before;
    }

    // One way only: once the monitor shows another input it ignores every DDC/CI command from this PC (tested on LG 27GR75Q).
    // shortcut: switches every monitor at once; pick one by description if a second DDC/CI monitor appears.
    public static void SwitchInput(int index)
    {
        ForEach(h => SetVCPFeature(h, 0x60, inputCodes[index]));
        try { Nvidia.LgSwitchInput(lgInputCodes[index]); } catch (DllNotFoundException) { } // no NVIDIA driver
    }

    public static int ReadPercent() { return applied = Visit(-1); }

    // DDC/CI writes take tens of milliseconds; while a key is held Windows changes the level faster than that,
    // so writes run on their own thread and only the latest level is sent.
    public static void SetPercentAsync(int percent)
    {
        Interlocked.Exchange(ref pending, percent);
        wake.Set();
    }

    public static void Worker()
    {
        while (true)
        {
            wake.WaitOne();
            int p = Interlocked.Exchange(ref pending, -1);
            if (p < 0 || p == applied) continue;
            Visit(p);
            applied = p;
        }
    }
}

// LG monitors ignore VCP 60 and switch inputs on VCP F4 sent from DDC source address 0x50 instead of the standard 0x51.
// Windows' own DDC/CI API cannot change that address, so the message goes out as raw I2C through the NVIDIA driver.
// shortcut: NVIDIA only, AMD and Intel need their own raw I2C call (ADL, IGCL).
static class Nvidia
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr Query(uint id);

    [StructLayout(LayoutKind.Sequential)]
    struct NV_I2C_INFO // version 3
    {
        public uint Version, DisplayMask; public byte IsDdcPort, DevAddress;
        public IntPtr RegAddress; public uint RegAddrSize; public IntPtr Data; public uint Size, Speed, SpeedKhz;
        public byte PortId; public uint IsPortIdSet;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumDisplayFn(int index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OutputIdFn(IntPtr display, out uint outputId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpusFn(IntPtr display, [Out] IntPtr[] gpus, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int I2cWriteFn(IntPtr gpu, ref NV_I2C_INFO info);

    static T Fn<T>(uint id) { return (T)(object)Marshal.GetDelegateForFunctionPointer(Query(id), typeof(T)); }

    public static void LgSwitchInput(byte value)
    {
        if (Fn<InitFn>(0x0150E828)() != 0) return;
        var enumDisplay = Fn<EnumDisplayFn>(0x9ABDD40D); var outputId = Fn<OutputIdFn>(0xD995937E);
        var gpusOf = Fn<GpusFn>(0x34EF9506); var write = Fn<I2cWriteFn>(0xE812EB07);

        // DDC/CI "set VCP feature": length, opcode, feature, value high, value low, checksum over both addresses and the message
        byte[] msg = { 0x84, 0x03, 0xF4, 0x00, value, 0 };
        byte sum = 0x6E ^ 0x50; for (int k = 0; k < 5; k++) sum ^= msg[k]; msg[5] = sum;
        IntPtr reg = Marshal.AllocHGlobal(1), data = Marshal.AllocHGlobal(msg.Length);
        Marshal.WriteByte(reg, 0x50); Marshal.Copy(msg, 0, data, msg.Length);

        IntPtr display;
        for (int i = 0; enumDisplay(i, out display) == 0; i++)
        {
            uint id, count; var gpus = new IntPtr[64];
            if (outputId(display, out id) != 0 || gpusOf(display, gpus, out count) != 0 || count == 0) continue;
            var info = new NV_I2C_INFO { DisplayMask = id, IsDdcPort = 1, DevAddress = 0x6E, RegAddress = reg, RegAddrSize = 1, Data = data, Size = (uint)msg.Length, Speed = 0xFFFF };
            info.Version = (uint)Marshal.SizeOf(typeof(NV_I2C_INFO)) | (3u << 16);
            write(gpus[0], ref info);
        }
        Marshal.FreeHGlobal(reg); Marshal.FreeHGlobal(data);
    }
}

// The Windows brightness level lives in the active power scheme ("display brightness", 0-100 %).
static class WindowsBrightness
{
    public static Guid Setting = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
    static Guid subVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");

    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, IntPtr scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, IntPtr scheme);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    public static void Set(int percent)
    {
        IntPtr scheme;
        if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0) return;
        PowerWriteACValueIndex(IntPtr.Zero, scheme, ref subVideo, ref Setting, (uint)percent);
        PowerWriteDCValueIndex(IntPtr.Zero, scheme, ref subVideo, ref Setting, (uint)percent);
        PowerSetActiveScheme(IntPtr.Zero, scheme);
        LocalFree(scheme);
    }
}

// Hidden top-level window: receives brightness-level, device-change and resume notifications.
class Watcher : NativeWindow
{
    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, uint flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const int WM_POWERBROADCAST = 0x218, WM_DEVICECHANGE = 0x219, WM_HOTKEY = 0x312, WM_DRIVER_KEYS = 0x8001 /* WM_APP + 1 */;
    const uint VK_PAUSE = 0x13;
    const byte VK_LWIN = 0x5B, VK_TAB = 0x09;
    public static Watcher Instance;
    const int PBT_APMRESUMEAUTOMATIC = 0x12, PBT_POWERSETTINGCHANGE = 0x8013, DBT_DEVNODES_CHANGED = 7;

    // Device changes arrive in bursts while a keyboard enumerates; talk to the driver once things settle.
    readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 1500 };

    // Sleeping on key-down lets the key-up wake the PC straight away, so wait for the keys to be released.
    readonly System.Windows.Forms.Timer sleepDelay = new System.Windows.Forms.Timer { Interval = 1000 };

    public Watcher()
    {
        Instance = this;
        CreateHandle(new CreateParams());
        sleepDelay.Tick += (s, e) => { sleepDelay.Stop(); Application.SetSuspendState(PowerState.Suspend, false, false); };
        UpdateHotkey();
        settle.Tick += (s, e) => { settle.Stop(); Driver.ApplySettings(); };
        IntPtr handle = Handle;
        Driver.KeysPressed = keys => PostMessage(handle, WM_DRIVER_KEYS, (IntPtr)keys, IntPtr.Zero);
        Driver.ApplySettings();

        SyncFromMonitor();
        RegisterPowerSettingNotification(Handle, ref WindowsBrightness.Setting, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
    }

    // Fn+Esc reaches Windows as Pause; the key is only taken over while an action is assigned.
    public void UpdateHotkey()
    {
        UnregisterHotKey(Handle, 1);
        // No MOD_NOREPEAT: Pause never sends a key-up, so Windows would treat every press after the first as a repeat.
        if (Settings.FnEsc != 0) RegisterHotKey(Handle, 1, 0, VK_PAUSE);
    }

    // Makes the Windows flyout start from the monitor's real level instead of a stale one.
    public static void SyncFromMonitor()
    {
        if (!Settings.ExternalMonitor) return;
        int level = Monitors.ReadPercent();
        if (level >= 0) WindowsBrightness.Set(level);
    }

    void Act(int action)
    {
        if (action == 1) LockWorkStation();
        else if (action == 2) sleepDelay.Start();
        else if (action == 3) System.Diagnostics.Process.Start("taskmgr.exe");
    }

    static void WinShortcut(byte key)
    {
        const uint KEYUP = 2;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && Settings.ExternalMonitor)
        {
            // POWERBROADCAST_SETTING: GUID (16 bytes), DataLength (4), Data
            var setting = (Guid)Marshal.PtrToStructure(m.LParam, typeof(Guid));
            if (setting == WindowsBrightness.Setting && Marshal.ReadInt32(m.LParam, 16) == 4)
                Monitors.SetPercentAsync(Math.Max(0, Math.Min(100, Marshal.ReadInt32(m.LParam, 20))));
        }
        else if (m.Msg == WM_HOTKEY) Act(Settings.FnEsc);
        else if (m.Msg == WM_DRIVER_KEYS)
        {
            int keys = (int)m.WParam;
            if ((keys & Driver.Lock) != 0)
            {
                if (Settings.InputSwitchAvailable && Settings.LockKeySwitchesInput && Settings.InputSource != 0) Monitors.SwitchInput(Settings.InputSource);
                Act(Settings.LockKey);
            }
            // The Windows counterparts of the icons on the keycaps.
            if ((keys & Driver.F3) != 0) WinShortcut(VK_TAB);     // Mission Control -> Task View
            if ((keys & Driver.F4) != 0) WinShortcut((byte)'S');  // Spotlight -> search
            if ((keys & Driver.F5) != 0) WinShortcut((byte)'H');  // Dictation -> voice typing
            if ((keys & Driver.F6) != 0) DoNotDisturb.Toggle();
        }
        else if ((m.Msg == WM_DEVICECHANGE && (int)m.WParam == DBT_DEVNODES_CHANGED) ||
                 (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_APMRESUMEAUTOMATIC))
        {
            settle.Stop(); settle.Start();
        }
        base.WndProc(ref m);
    }
}

class SettingsForm : Form
{
    readonly CheckBox autostart = new CheckBox { Text = "Start at Windows sign-in", AutoSize = true };
    readonly CheckBox external = new CheckBox { Text = "Control external monitor brightness", AutoSize = true };
    readonly ComboBox fnEsc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly ComboBox lockKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly CheckBox lockSwitchesInput = new CheckBox { Text = "Lock button switches monitor input", AutoSize = true };
    readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly ComboBox input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };

    public SettingsForm()
    {
        Text = "MagicFn settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        Controls.Add(t);

        autostart.Checked = Settings.Autostart;
        t.Controls.Add(autostart); t.SetColumnSpan(autostart, 2);

        external.Checked = Settings.ExternalMonitor;
        t.Controls.Add(external); t.SetColumnSpan(external, 2);

        // one row: the checkbox and the input it switches to
        lockSwitchesInput.Checked = Settings.LockKeySwitchesInput;
        lockSwitchesInput.Anchor = AnchorStyles.Left;
        lockSwitchesInput.Margin = new Padding(3, 6, 12, 6);
        input.Items.AddRange(Monitors.Inputs);
        input.SelectedIndex = Settings.InputSource;
        if (Settings.InputSwitchAvailable) { t.Controls.Add(lockSwitchesInput); t.Controls.Add(input); }

        mode.Items.AddRange(new object[] { "Standard F1–F12, media with Fn", "Brightness, media and volume, F1–F12 with Fn" });
        mode.SelectedIndex = Settings.MediaByDefault ? 1 : 0;
        t.Controls.Add(new Label { Text = "Top row without Fn:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) });
        t.Controls.Add(mode);

        fnEsc.Items.AddRange(new object[] { "Nothing (Pause key)", "Lock the computer", "Sleep", "Task Manager" });
        fnEsc.SelectedIndex = Math.Max(0, Math.Min(3, Settings.FnEsc));
        t.Controls.Add(new Label { Text = "Fn + Esc:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) });
        t.Controls.Add(fnEsc);

        lockKey.Items.AddRange(new object[] { "Nothing", "Lock the computer", "Sleep" });
        lockKey.SelectedIndex = Math.Max(0, Math.Min(2, Settings.LockKey));
        t.Controls.Add(new Label { Text = "Lock button:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) });
        t.Controls.Add(lockKey);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var apply = new Button { Text = "Apply", AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        t.Controls.Add(buttons); t.SetColumnSpan(buttons, 2);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (s, e) => Apply();
        apply.Click += (s, e) => Apply();
    }

    void Apply()
    {
        Settings.MediaByDefault = mode.SelectedIndex == 1;
        bool turnedOn = external.Checked && !Settings.ExternalMonitor;
        Settings.ExternalMonitor = external.Checked;
        if (turnedOn) Watcher.SyncFromMonitor();
        Settings.FnEsc = fnEsc.SelectedIndex;
        Settings.LockKey = lockKey.SelectedIndex;
        Settings.LockKeySwitchesInput = lockSwitchesInput.Checked;
        Settings.InputSource = input.SelectedIndex;
        Watcher.Instance.UpdateHotkey();
        Settings.Save();
        Settings.Autostart = autostart.Checked;
        Driver.ApplySettings();
    }
}

class Tray : ApplicationContext
{
    readonly NotifyIcon icon = new NotifyIcon { Text = "MagicFn", Visible = true };
    readonly Watcher watcher = new Watcher();
    SettingsForm form;

    public Tray()
    {
        icon.Icon = DrawIcon();
        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem { Enabled = false };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
        menu.Items.Add("Exit", null, (s, e) => { icon.Visible = false; ExitThread(); });
        menu.Opening += (s, e) => status.Text = Driver.Connected ? "Keyboard connected" : "Keyboard or driver not found";
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (s, e) => ShowSettings();
    }

    void ShowSettings()
    {
        if (form != null) { form.Activate(); return; }
        using (form = new SettingsForm()) form.ShowDialog();
        form = null;
    }

    static Icon DrawIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);
            using (var b = new SolidBrush(Color.FromArgb(40, 40, 44))) g.FillEllipse(b, 1, 1, 30, 30);
            g.DrawString("fn", font, Brushes.White, new RectangleF(0, -1, 32, 32), fmt);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "MagicFn.SingleInstance", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            Settings.Load();
            new Thread(Monitors.Worker) { IsBackground = true }.Start();
            Application.Run(new Tray());
        }
    }
}
