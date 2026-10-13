// MagicFnSetup: one-time driver setup for MagicFn. Puts one of two drivers on the Magic Keyboard (cable only):
//  - MagicFnDriver (folder driver\package next to this exe): our own UMDF driver, the only one with the Lock button.
//    Trusts its self-signed certificate on this machine, then installs the package.
//  - Apple's KeyMagic2 from Boot Camp (folder bootcamp-magic2-original): force-installs it, adds the keyboard to
//    the driver's table and restarts the device.
// The two must never be stacked: KeyMagic2 bugchecks under MagicFnDriver. Installing either one replaces the other.
//   MagicFnSetup             ask which driver, then install (asks for administrator rights)
//   MagicFnSetup magicfndriver    install MagicFnDriver
//   MagicFnSetup apple       install Apple's KeyMagic2
//   MagicFnSetup check       dry run for Apple's driver: show the device and the INF entry, change nothing
// Add "lg" to an install to turn on MagicFn's monitor input switch; without it MagicFn only mirrors brightness.
// The switch is proven on LG monitors only, so every install without the flag turns it off again.
// Build: csc /nologo /codepage:65001 /platform:x64 /out:MagicFnSetup.exe MagicFnSetup.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

static class MagicFnSetup
{
    const string Pid = "0321";                                   // Magic Keyboard USB-C; see README for other models

    // First table byte is the keyboard type: 2 = external, corded. Type 1 (built-in) would drop the media-key collection.
    class Transport { public string Name, Enumerator, HardwareId, InfPrefix, TableName; public byte[] TableEntry; }
    static readonly Transport[] Transports =
    {
        new Transport { Name = "cable", Enumerator = "USB", HardwareId = @"USB\VID_05AC&PID_" + Pid + "&MI_01", InfPrefix = @"USB\VID_05AC",
                        TableName = "VID_05AC&PID_" + Pid, TableEntry = new byte[] { 2, 0, 0, 1, 1 } },
    };

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVINSTALL_PARAMS
    {
        public uint cbSize, Flags, FlagsEx;
        public IntPtr hwndParent, InstallMsgHandler, InstallMsgHandlerContext, FileQueue, ClassInstallReserved;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DRVINFO_DATA
    {
        public uint cbSize, DriverType;
        public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description, MfgName, ProviderName;
        public long DriverDate;
        public ulong DriverVersion;
    }

    const string S = "setupapi.dll";
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport(S, SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA dev);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA dev, uint prop, out uint type, byte[] buf, uint size, out uint required);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA dev, StringBuilder id, uint size, out uint required);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DEVINSTALL_PARAMS p);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceInstallParams(IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DEVINSTALL_PARAMS p);
    [DllImport(S, SetLastError = true)] static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA dev, uint type);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiEnumDriverInfo(IntPtr set, ref SP_DEVINFO_DATA dev, uint type, uint index, ref SP_DRVINFO_DATA drv);
    [DllImport(S, CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDriverInfoDetail(IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DRVINFO_DATA drv, IntPtr detail, uint size, out uint required);
    [DllImport(S)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", SetLastError = true)] static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA dev, ref SP_DRVINFO_DATA drv, uint flags, out bool needReboot);

    const uint DIGCF_PRESENT = 2, DIGCF_ALLCLASSES = 4, SPDRP_HARDWAREID = 1, SPDRP_LOWERFILTERS = 0x12, SPDIT_CLASSDRIVER = 1;
    const uint DI_ENUMSINGLEINF = 0x00010000, DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000800; // the INF hides its models (ExcludeFromSelect)
    const int DetailHardwareIdOffset = 1576, DetailSize = 1584;                        // SP_DRVINFO_DETAIL_DATA_W on x64

    static int Main(string[] args)
    {
        bool lg = Array.Exists(args, a => a.Equals("lg", StringComparison.OrdinalIgnoreCase));
        string arg = Array.Find(args, a => !a.Equals("lg", StringComparison.OrdinalIgnoreCase));
        if (arg != null) arg = arg.ToLowerInvariant();
        bool check = arg == "check";
        int code;
        try { code = Run(check, check ? "apple" : arg, lg); }
        catch (Exception e) { Console.WriteLine("\nERROR: " + e.Message); code = 1; }
        if (!check) { Console.WriteLine("\nPress Enter to close this window."); Console.ReadLine(); }
        return code;
    }

    static int Run(bool check, string driver, bool lg)
    {
        string exe = Process.GetCurrentProcess().MainModule.FileName, dir = Path.GetDirectoryName(exe);
        if (driver != "magicfndriver" && driver != "apple")
        {
            Console.WriteLine("Which driver to install?");
            Console.WriteLine("  1 - MagicFnDriver: our own driver, the Lock button works");
            Console.WriteLine("  2 - Apple KeyMagic2 from Boot Camp");
            Console.Write("Choice [1]: ");
            driver = (Console.ReadLine() ?? "").Trim() == "2" ? "apple" : "magicfndriver";
        }
        bool own = driver == "magicfndriver";
        string inf = Path.Combine(dir, own ? @"driver\package\MagicFnDriver.inf" : @"bootcamp-magic2-original\Keymagic2.inf");
        if (!File.Exists(inf)) throw new FileNotFoundException("Driver not found: " + inf);

        if (!check && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("Administrator rights are needed, restarting...");
            Process.Start(new ProcessStartInfo(exe, driver + (lg ? " lg" : "")) { Verb = "runas" }); // throws if the UAC prompt is declined
            Environment.Exit(0);
        }

        if (!check)
        {
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\MagicFn")) k.SetValue("InputSwitch", lg ? 1 : 0, RegistryValueKind.DWord);
            Console.WriteLine(lg ? "MagicFn: monitor input switch is on (LG)." : "MagicFn: monitor brightness only. The LG flag turns on the input switch.");
        }

        if (own) { RemoveInstalledMagicFnDriver(); TrustCertificate(Path.Combine(dir, @"driver\package\MagicFn.cer")); }

        int found = 0;
        foreach (var t in Transports) if (Setup(t, inf, check, own)) found++;
        if (found == 0) throw new Exception("Keyboard not found. Connect it with a cable and run again.");
        if (check) Console.WriteLine("\nCheck passed, nothing was changed.");
        else Console.WriteLine(RestartMagicFn(dir) ? "\nDone. MagicFn is running." : "\nDone. Now start MagicFn.exe.");
        return 0;
    }

    // Restarted so that it picks up the new driver and the LG flag. Started through Explorer, which hands it the user's
    // ordinary token: a direct start from this elevated process would leave MagicFn running as administrator.
    static bool RestartMagicFn(string dir)
    {
        string app = Path.Combine(dir, "MagicFn.exe");
        if (!File.Exists(app)) return false;
        foreach (var running in Process.GetProcessesByName("MagicFn")) { running.Kill(); running.WaitForExit(5000); }
        Process.Start("explorer.exe", "\"" + app + "\"");
        return true;
    }

    // Earlier builds are removed first, so that Windows never keeps one as the driver to roll back to.
    static void RemoveInstalledMagicFnDriver()
    {
        var list = Process.Start(new ProcessStartInfo("pnputil.exe", "/enum-drivers") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
        string output = list.StandardOutput.ReadToEnd();
        list.WaitForExit();
        foreach (string block in System.Text.RegularExpressions.Regex.Split(output, @"\r?\n\s*\r?\n"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(block, @"magicfndriver\.inf|magickbd\.inf", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;   // magickbd: the driver's earlier name
            var name = System.Text.RegularExpressions.Regex.Match(block, @"oem\d+\.inf");
            if (!name.Success) continue;
            var delete = Process.Start(new ProcessStartInfo("pnputil.exe", "/delete-driver " + name.Value + " /uninstall /force") { UseShellExecute = false, CreateNoWindow = true });
            delete.WaitForExit();
            Console.WriteLine("Removed the previous MagicFnDriver build: {0}", name.Value);
        }
    }

    // Windows installs a self-signed driver package only when its certificate is both a trusted root and a trusted
    // publisher. Whoever holds the certificate's private key can then sign drivers this machine accepts.
    static void TrustCertificate(string path)
    {
        var certificate = new X509Certificate2(path);
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            store.Close();
        }
        Console.WriteLine("Driver certificate is now trusted: {0}", certificate.Subject);
    }

    // Returns false when the keyboard is not connected this way.
    static bool Setup(Transport t, string inf, bool check, bool own)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, t.Enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == new IntPtr(-1)) throw Win32("SetupDiGetClassDevs");
        try
        {
            SP_DEVINFO_DATA dev;
            if (!FindDevice(set, t.HardwareId, out dev)) { Console.WriteLine("[{0}] keyboard is not connected, skipping.", t.Name); return false; }
            var idBuf = new StringBuilder(512); uint req;
            SetupDiGetDeviceInstanceId(set, ref dev, idBuf, 512, out req);
            string instanceId = idBuf.ToString();
            Console.WriteLine("[{0}] keyboard: {1}", t.Name, instanceId);

            // MagicFnDriver is always reinstalled, so that a newer build replaces an older one.
            bool installed = !own && string.Join("|", Property(set, ref dev, SPDRP_LOWERFILTERS)).IndexOf("KeyMagic2", StringComparison.OrdinalIgnoreCase) >= 0;
            if (installed) Console.WriteLine("[{0}] Apple driver is already installed.", t.Name);
            if (!installed || check) // the dry run exercises driver selection even when there is nothing to install
            {
                string hwid;
                SP_DRVINFO_DATA drv = SelectDriver(set, ref dev, inf, t.InfPrefix, out hwid);
                Console.WriteLine("[{0}] driver: {1} ({2}), INF entry {3}", t.Name, drv.Description, drv.ProviderName, hwid);
                if (!check)
                {
                    bool reboot;
                    if (!DiInstallDevice(IntPtr.Zero, set, ref dev, ref drv, 0, out reboot)) throw Win32("Driver install (DiInstallDevice)");
                    Console.WriteLine("[{0}] driver installed.{1}", t.Name, reboot ? " Windows asks for a restart." : "");
                }
            }
            if (check || own) return true;   // MagicFnDriver keeps no table, and installing it restarts the device

            using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\KeyMagic2\KeyboardTable"))
                k.SetValue(t.TableName, t.TableEntry, RegistryValueKind.Binary);
            Console.WriteLine("[{0}] keyboard added to the driver table.", t.Name);

            // The driver reads its table when the device starts.
            var p = Process.Start(new ProcessStartInfo("pnputil.exe", "/restart-device \"" + instanceId + "\"") { UseShellExecute = false, CreateNoWindow = true });
            p.WaitForExit();
            Console.WriteLine(p.ExitCode == 0 ? "[{0}] keyboard restarted." : "[{0}] could not restart the keyboard: reconnect it.", t.Name);
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static bool FindDevice(IntPtr set, string hardwareId, out SP_DEVINFO_DATA dev)
    {
        dev = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
        for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref dev); i++)
            foreach (string id in Property(set, ref dev, SPDRP_HARDWAREID))
                if (string.Equals(id, hardwareId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string[] Property(IntPtr set, ref SP_DEVINFO_DATA dev, uint prop)
    {
        var buf = new byte[4096]; uint type, req;
        if (!SetupDiGetDeviceRegistryProperty(set, ref dev, prop, out type, buf, (uint)buf.Length, out req)) return new string[0];
        return Encoding.Unicode.GetString(buf, 0, (int)req).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Builds the driver list from Apple's INF alone and picks a model entry for this connection type:
    // the USB and Bluetooth entries use different install sections.
    static SP_DRVINFO_DATA SelectDriver(IntPtr set, ref SP_DEVINFO_DATA dev, string inf, string infPrefix, out string hwid)
    {
        var p = new SP_DEVINSTALL_PARAMS { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINSTALL_PARAMS)) };
        if (!SetupDiGetDeviceInstallParams(set, ref dev, ref p)) throw Win32("SetupDiGetDeviceInstallParams");
        p.Flags |= DI_ENUMSINGLEINF; p.FlagsEx |= DI_FLAGSEX_ALLOWEXCLUDEDDRVS; p.DriverPath = inf;
        if (!SetupDiSetDeviceInstallParams(set, ref dev, ref p)) throw Win32("SetupDiSetDeviceInstallParams");
        if (!SetupDiBuildDriverInfoList(set, ref dev, SPDIT_CLASSDRIVER)) throw Win32("SetupDiBuildDriverInfoList");

        IntPtr detail = Marshal.AllocHGlobal(8192);
        try
        {
            var drv = new SP_DRVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DRVINFO_DATA)) };
            for (uint i = 0; SetupDiEnumDriverInfo(set, ref dev, SPDIT_CLASSDRIVER, i, ref drv); i++)
            {
                Marshal.WriteInt32(detail, DetailSize); uint req;
                // ERROR_INSUFFICIENT_BUFFER (122) still fills the fixed part and the first hardware id.
                if (!SetupDiGetDriverInfoDetail(set, ref dev, ref drv, detail, 8192, out req) && Marshal.GetLastWin32Error() != 122) continue;
                hwid = Marshal.PtrToStringUni(detail + DetailHardwareIdOffset);
                if (hwid.StartsWith(infPrefix, StringComparison.OrdinalIgnoreCase)) return drv;
            }
        }
        finally { Marshal.FreeHGlobal(detail); }
        throw new Exception("The driver INF has no entry for " + infPrefix);
    }

    static Exception Win32(string what)
    {
        int e = Marshal.GetLastWin32Error();
        return new Exception(string.Format("{0}: error 0x{1:X8} ({2})", what, e, new System.ComponentModel.Win32Exception(e).Message));
    }
}
