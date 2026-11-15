// Battery Float: a small half circle hanging from the top centre of the screen, under the camera.
// Hover it and it pops open into a rounded panel with a ring for every battery device connected to
// the PC and every external USB drive (space used). Move the mouse away and it closes.
//
// Battery sources:
//  1. Windows' own Bluetooth battery value (the one Settings > Bluetooth & devices shows), read from the
//     device tree with CfgMgr32. Covers most Bluetooth mice, keyboards and headsets.
//  2. Apple AirPods / Beats: Windows does not get their battery, but they broadcast it in a Bluetooth LE
//     "proximity pairing" advert (Apple company id 0x004C, type 0x07, 10% steps). A broadcast is only used
//     when its model id matches AirPods paired with this PC and the signal is strong, so other people's
//     AirPods are ignored.
// Drives: removable drives plus fixed drives whose storage bus is USB / SD (IOCTL_STORAGE_QUERY_PROPERTY).
//
// Built with the C# 5 compiler that ships with Windows (tools\build.cmd): no $"", no ?.
//
// batteryfloat.exe            run (single instance)
// batteryfloat.exe --dump F   write a diagnostic report to file F (12 s of listening) and exit
// batteryfloat.exe --pinned   stay open (for screenshots / testing)
// batteryfloat.exe --demo D   render the showcase GIF frames (made-up devices) into folder D

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace BatteryFloat
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    static class Native
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint len, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, [Out] byte[] buf, ref uint size, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLongW(IntPtr h, int index, int value);
    }

    // ---------------------------------------------------------------- device tree (CfgMgr32)

    class BtDevice { public string Id, Mac, Name; public bool Ble; public bool? Connected; public Guid Container; }
    class BatteryNode { public string Id, Name; public int Battery; public bool? Connected; public Guid Container; }
    class AppleDev { public string Mac, Name; public int Pid; public bool Connected; public Guid Container; }

    class Snapshot
    {
        public List<BtDevice> Bt = new List<BtDevice>();
        public List<BatteryNode> Batteries = new List<BatteryNode>();
        public List<AppleDev> Apple = new List<AppleDev>();
        public DateTime At;
    }

    static class Pnp
    {
        const uint FILTER_ENUMERATOR = 0x1, FILTER_PRESENT = 0x100;
        const uint T_BYTE = 0x03, T_BOOL = 0x11, T_STRING = 0x12;

        static DEVPROPKEY KBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static DEVPROPKEY KConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        static DEVPROPKEY KFriendly = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static DEVPROPKEY KDesc = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        static DEVPROPKEY KContainer = new DEVPROPKEY("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);

        // BTHENUM\{service}_VID&0001004C_PID&201F\7&1A2B3C4D&0&001122AABBCC_C00000000
        static readonly Regex AppleSvc = new Regex(@"^BTHENUM\\\{[0-9A-F-]+\}(?:_HCIBYPASS)?_VID&0001004C_PID&([0-9A-F]{4})\\[^\\]*?([0-9A-F]{12})_C[0-9A-F]+$", RegexOptions.IgnoreCase);
        // BTHENUM\DEV_001122AABBCC\...   BTHLE\DEV_D0E1...\...
        static readonly Regex DevNode = new Regex(@"^(BTHENUM|BTHLE)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase);

        static string[] Ids(string filter, uint flags)
        {
            for (int tries = 0; tries < 4; tries++)
            {
                uint len;
                if (Native.CM_Get_Device_ID_List_SizeW(out len, filter, flags) != 0 || len == 0) return new string[0];
                char[] buf = new char[len];
                int cr = Native.CM_Get_Device_ID_ListW(filter, buf, len, flags);
                if (cr == 0) return new string(buf).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (cr != 0x1A) return new string[0]; // 0x1A = CR_BUFFER_SMALL: list grew, retry
            }
            return new string[0];
        }

        static uint Locate(string id)
        {
            uint inst;
            if (Native.CM_Locate_DevNodeW(out inst, id, 0) == 0) return inst;
            if (Native.CM_Locate_DevNodeW(out inst, id, 1) == 0) return inst; // phantom (paired, not present)
            return 0;
        }

        static byte[] Raw(uint inst, ref DEVPROPKEY key, out uint type)
        {
            uint size = 0;
            Native.CM_Get_DevNode_PropertyW(inst, ref key, out type, null, ref size, 0);
            if (size == 0) return null;
            byte[] buf = new byte[size];
            if (Native.CM_Get_DevNode_PropertyW(inst, ref key, out type, buf, ref size, 0) != 0) return null;
            return buf;
        }

        static string Str(uint inst, ref DEVPROPKEY key)
        {
            uint t; byte[] b = Raw(inst, ref key, out t);
            if (b == null || t != T_STRING) return null;
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        static bool? Bool(uint inst, ref DEVPROPKEY key)
        {
            uint t; byte[] b = Raw(inst, ref key, out t);
            if (b == null || b.Length < 1 || t != T_BOOL) return null;
            return b[0] != 0;
        }

        static int Byte(uint inst, ref DEVPROPKEY key)
        {
            uint t; byte[] b = Raw(inst, ref key, out t);
            if (b == null || b.Length < 1 || t != T_BYTE) return -1;
            return b[0];
        }

        static Guid Container(uint inst)
        {
            uint t; byte[] b = Raw(inst, ref KContainer, out t);
            if (b == null || b.Length != 16) return Guid.Empty;
            return new Guid(b);
        }

        static string Name(uint inst)
        {
            string n = Str(inst, ref KFriendly);
            if (string.IsNullOrEmpty(n)) n = Str(inst, ref KDesc);
            return n ?? "";
        }

        public static string Clean(string n)
        {
            if (string.IsNullOrEmpty(n)) return "Bluetooth device";
            string[] suffixes = { " Hands-Free AG", " Hands-Free", " Avrcp Transport", " Stereo" };
            foreach (string suf in suffixes)
                if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - suf.Length);
            return n;
        }

        public static Snapshot Scan()
        {
            var s = new Snapshot();
            s.At = DateTime.Now;
            var byContainer = new Dictionary<Guid, BtDevice>();

            // 1. Bluetooth device nodes (classic + LE): name, connected, container
            foreach (string en in new string[] { "BTHENUM", "BTHLE" })
            {
                foreach (string id in Ids(en, FILTER_ENUMERATOR))
                {
                    Match m = DevNode.Match(id);
                    if (!m.Success) continue;
                    uint inst = Locate(id);
                    if (inst == 0) continue;
                    var d = new BtDevice();
                    d.Id = id;
                    d.Mac = m.Groups[2].Value.ToUpperInvariant();
                    d.Ble = en == "BTHLE";
                    d.Name = Clean(Name(inst));
                    d.Connected = Bool(inst, ref KConnected);
                    d.Container = Container(inst);
                    s.Bt.Add(d);
                    if (d.Container != Guid.Empty && !byContainer.ContainsKey(d.Container)) byContainer[d.Container] = d;
                }
            }

            // 2. Paired Apple audio devices: model id (PID) from the service node, joined by MAC
            var seenMac = new HashSet<string>();
            foreach (string id in Ids("BTHENUM", FILTER_ENUMERATOR))
            {
                Match m = AppleSvc.Match(id);
                if (!m.Success) continue;
                string mac = m.Groups[2].Value.ToUpperInvariant();
                if (!seenMac.Add(mac)) continue;
                var a = new AppleDev();
                a.Mac = mac;
                a.Pid = Convert.ToInt32(m.Groups[1].Value, 16);
                BtDevice d = s.Bt.Find(x => !x.Ble && x.Mac == mac);
                a.Name = d != null ? d.Name : Models.Name(a.Pid);
                a.Connected = d != null && d.Connected == true;
                a.Container = d != null ? d.Container : Guid.Empty;
                s.Apple.Add(a);
            }

            // 3. Every present device node that carries Windows' Bluetooth battery value
            string[] all = Ids(null, FILTER_PRESENT);
            if (all.Length == 0) all = Ids(null, 0);
            var seenContainer = new HashSet<Guid>();
            foreach (string id in all)
            {
                uint inst = Locate(id);
                if (inst == 0) continue;
                int b = Byte(inst, ref KBattery);
                if (b < 0 || b > 100) continue;
                var n = new BatteryNode();
                n.Id = id;
                n.Battery = b;
                n.Container = Container(inst);
                if (n.Container != Guid.Empty && !seenContainer.Add(n.Container)) continue;
                BtDevice owner;
                if (n.Container != Guid.Empty && byContainer.TryGetValue(n.Container, out owner))
                {
                    n.Name = owner.Name;
                    n.Connected = owner.Connected;
                }
                else
                {
                    n.Name = Clean(Name(inst));
                    n.Connected = Bool(inst, ref KConnected);
                }
                s.Batteries.Add(n);
            }
            return s;
        }
    }

    // ---------------------------------------------------------------- external drives

    class DriveItem { public string Letter, Label; public long Total, Free; public int Bus; public string Type; }

    static class Drives
    {
        // STORAGE_BUS_TYPE: 7 = USB, 0x0C = SD, 0x0D = MMC
        static int BusType(string letter)
        {
            IntPtr h = Native.CreateFileW(@"\\.\" + letter, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return -1;
            try
            {
                byte[] query = new byte[12];       // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
                byte[] outBuf = new byte[1024];    // STORAGE_DEVICE_DESCRIPTOR, BusType at offset 28
                int ret;
                if (!Native.DeviceIoControl(h, 0x2D1400, query, query.Length, outBuf, outBuf.Length, out ret, IntPtr.Zero) || ret < 32) return -1;
                return BitConverter.ToInt32(outBuf, 28);
            }
            finally { Native.CloseHandle(h); }
        }

        public static List<DriveItem> Scan() { return Scan(null); }

        public static List<DriveItem> Scan(List<string> log)
        {
            var list = new List<DriveItem>();
            string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    string letter = d.Name.Substring(0, 2).ToUpperInvariant();
                    if (d.DriveType != DriveType.Removable && d.DriveType != DriveType.Fixed)
                    {
                        if (log != null) log.Add(letter + " " + d.DriveType + " -> excluded (not a disk)");
                        continue;
                    }
                    int bus = BusType(letter);
                    bool external = d.DriveType == DriveType.Removable || bus == 7 || bus == 0x0C || bus == 0x0D;
                    if (log != null)
                        log.Add(string.Format("{0} {1} ready={2} bus=0x{3:X} -> {4}", letter, d.DriveType, d.IsReady, bus,
                            letter == sys ? "excluded (system drive)" : (!d.IsReady ? "excluded (not ready)" : (external ? "INCLUDED" : "excluded (internal/virtual bus)"))));
                    if (letter == sys || !d.IsReady || !external) continue;
                    var it = new DriveItem();
                    it.Letter = letter;
                    it.Label = d.VolumeLabel;
                    it.Total = d.TotalSize;
                    it.Free = d.TotalFreeSpace;
                    it.Bus = bus;
                    it.Type = d.DriveType.ToString();
                    list.Add(it);
                }
                catch { }
            }
            list.Sort(delegate (DriveItem a, DriveItem b) { return string.CompareOrdinal(a.Letter, b.Letter); });
            return list;
        }

        public static string Size(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1000) return (gb / 1024).ToString("0.0") + " TB";
            if (gb >= 10) return gb.ToString("0") + " GB";
            if (gb >= 1) return gb.ToString("0.0") + " GB";
            return (bytes / 1048576.0).ToString("0") + " MB";
        }
    }

    // ---------------------------------------------------------------- Apple proximity-pairing adverts

    static class Models
    {
        // Apple Bluetooth product ids (the advert carries them little-endian: 1F 20 = 0x201F)
        static readonly Dictionary<int, string> Names = new Dictionary<int, string> {
            { 0x2002, "AirPods" }, { 0x200F, "AirPods 2" }, { 0x2013, "AirPods 3" },
            { 0x2019, "AirPods 4" }, { 0x201B, "AirPods 4 (ANC)" },
            { 0x200E, "AirPods Pro" }, { 0x2014, "AirPods Pro 2" }, { 0x2024, "AirPods Pro 2 (USB-C)" }, { 0x2027, "AirPods Pro 3" },
            { 0x200A, "AirPods Max" }, { 0x201F, "AirPods Max (USB-C)" },
            { 0x2003, "Powerbeats3" }, { 0x2005, "BeatsX" }, { 0x2006, "Beats Solo3" }, { 0x2009, "Beats Studio3" },
            { 0x200B, "Powerbeats Pro" }, { 0x200C, "Beats Solo Pro" }, { 0x2010, "Beats Flex" },
            { 0x2011, "Beats Studio Buds" }, { 0x2012, "Beats Fit Pro" }, { 0x2016, "Beats Studio Buds+" }, { 0x2017, "Beats Studio Pro" },
        };
        // one battery (over-ear / neckband): no left/right/case
        static readonly HashSet<int> Single = new HashSet<int> { 0x200A, 0x201F, 0x2003, 0x2005, 0x2006, 0x2009, 0x200C, 0x2010, 0x2017 };

        public static string Name(int pid)
        {
            string n;
            return Names.TryGetValue(pid, out n) ? n : "Apple audio 0x" + pid.ToString("X4");
        }
        public static bool IsSingle(int pid) { return Single.Contains(pid); }
    }

    class AppleReading
    {
        public int Pid, Rssi, Left, Right, Case, ChargeBits, Status;
        public bool Flip;
        public DateTime At;
        public string Hex;

        // Layout as decoded by OpenPods / AirPodsDesktop:
        // [0]=07 [1]=19 [2]=01 [3..4]=model [5]=status (bit5 clear = left/right swapped)
        // [6]=two bud nibbles 0..10 (x10%), 15 = unknown  [7]=charging nibble | case nibble
        public static AppleReading Parse(byte[] b, int rssi)
        {
            if (b == null || b.Length != 27 || b[0] != 0x07 || b[1] != 0x19 || b[2] != 0x01) return null;
            var r = new AppleReading();
            r.Pid = b[3] | (b[4] << 8);
            r.Status = b[5];
            r.Flip = (b[5] & 0x20) == 0;
            int hi = b[6] >> 4, lo = b[6] & 0xF;
            r.Left = r.Flip ? hi : lo;
            r.Right = r.Flip ? lo : hi;
            r.ChargeBits = b[7] >> 4;
            r.Case = b[7] & 0xF;
            r.Rssi = rssi;
            r.At = DateTime.Now;
            r.Hex = BitConverter.ToString(b, 0, 9);
            return r;
        }

        public bool ChargingLeft { get { return (ChargeBits & (Flip ? 0x2 : 0x1)) != 0; } }
        public bool ChargingRight { get { return (ChargeBits & (Flip ? 0x1 : 0x2)) != 0; } }
        public bool ChargingCase { get { return (ChargeBits & 0x4) != 0; } }
        public static bool Valid(int v) { return v >= 0 && v <= 10; }
        public AppleReading Clone() { return (AppleReading)MemberwiseClone(); }

        public string Describe()
        {
            return string.Format("model=0x{0:X4} ({1}) L={2} R={3} case={4} chargeBits={5} status=0x{6:X2} rssi={7}",
                Pid, Models.Name(Pid), Valid(Left) ? (Left * 10) + "%" : "n/a", Valid(Right) ? (Right * 10) + "%" : "n/a",
                Valid(Case) ? (Case * 10) + "%" : "n/a", Convert.ToString(ChargeBits, 2).PadLeft(4, '0'), Status, Rssi);
        }
    }

    class AppleScanner
    {
        public const int RssiConnected = -75;  // our own, connected AirPods: close by

        readonly object gate = new object();
        readonly object wlock = new object();
        readonly Dictionary<int, AppleReading> readings = new Dictionary<int, AppleReading>();
        volatile Snapshot snap;
        BluetoothLEAdvertisementWatcher w;
        public volatile string LastError;
        public Action<AppleReading, bool, string> Trace;

        public void SetSnapshot(Snapshot s) { snap = s; }

        public void EnsureRunning(bool want)
        {
            lock (wlock)
            {
                try
                {
                    if (!want)
                    {
                        if (w != null && w.Status == BluetoothLEAdvertisementWatcherStatus.Started) w.Stop();
                        return;
                    }
                    if (w != null && w.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                    {
                        w.Received -= OnReceived;
                        w = null;
                    }
                    if (w == null)
                    {
                        w = new BluetoothLEAdvertisementWatcher();
                        w.ScanningMode = BluetoothLEScanningMode.Passive;
                        w.Received += OnReceived;
                    }
                    if (w.Status != BluetoothLEAdvertisementWatcherStatus.Started) w.Start();
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    w = null;
                }
            }
        }

        public string Status
        {
            get { lock (wlock) { return w == null ? "none" : w.Status.ToString(); } }
        }

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
        {
            try
            {
                foreach (var m in e.Advertisement.ManufacturerData)
                {
                    if (m.CompanyId != 0x004C || m.Data.Length != 27) continue;
                    byte[] b = new byte[m.Data.Length];
                    DataReader.FromBuffer(m.Data).ReadBytes(b);
                    AppleReading r = AppleReading.Parse(b, e.RawSignalStrengthInDBm);
                    if (r != null) Offer(r);
                }
            }
            catch { }
        }

        void Offer(AppleReading r)
        {
            Snapshot s = snap;
            bool paired = false, connected = false;
            if (s != null)
                foreach (AppleDev a in s.Apple)
                    if (a.Pid == r.Pid) { paired = true; if (a.Connected) connected = true; }

            string why;
            if (!paired) why = "ignored: no paired device of this model";
            else if (!connected) why = "ignored: not connected to this PC";
            else if (r.Rssi < RssiConnected) why = "ignored: signal too weak (someone else's?)";
            else
            {
                lock (gate)
                {
                    AppleReading cur;
                    bool have = readings.TryGetValue(r.Pid, out cur);
                    if (have && cur.Hex == r.Hex)
                    {
                        cur.At = r.At;                       // same payload: refresh
                        cur.Rssi = Math.Max(cur.Rssi, r.Rssi);
                        why = "accepted (same)";
                    }
                    else if (have && (r.At - cur.At).TotalSeconds < 15 && r.Rssi < cur.Rssi - 8)
                        why = "ignored: weaker than the current reading";
                    else
                    {
                        readings[r.Pid] = r;
                        why = "accepted";
                    }
                }
            }
            var t = Trace;
            if (t != null) t(r, connected, why);
        }

        public Dictionary<int, AppleReading> Copy()
        {
            lock (gate)
            {
                var d = new Dictionary<int, AppleReading>();
                foreach (var kv in readings) d[kv.Key] = kv.Value.Clone();
                return d;
            }
        }
    }

    // ---------------------------------------------------------------- settings, autostart

    class Settings
    {
        public bool Autostart = true, HideFullscreen = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryFloat"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "autostart") s.Autostart = v != "0";
                    else if (k == "hidefullscreen") s.HideFullscreen = v != "0";
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, string.Format("autostart={0}\r\nhidefullscreen={1}\r\n", Autostart ? 1 : 0, HideFullscreen ? 1 : 0));
            }
            catch { }
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BatteryFloat";

        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    // ---------------------------------------------------------------- the island

    class Item
    {
        public string Key, Name, Value, Detail, Glyph;
        public int Pct = -1;
        public bool Charging, Stale, Drive;
        public string Sig() { return Key + "|" + Name + "|" + Value + "|" + Detail + "|" + Pct + "|" + Charging + "|" + Stale; }
    }

    class Island : Form
    {
        const string G_HEADPHONE = "\uE7F6", G_MOUSE = "\uE962", G_KEYBOARD = "\uE765", G_PAD = "\uE7FC",
                     G_SPEAKER = "\uE7F5", G_BT = "\uE702", G_BOLT = "\uE945", G_USB = "\uE88E";

        // logical sizes (x s = physical px)
        const float CollW = 64, CollH = 18, SegW = 224, SegH = 56, PadX = 10, PadTop = 4, PadBottom = 6, Radius = 18;
        const int MaxItems = 4;

        static readonly Color cBg = Color.FromArgb(255, 14, 14, 16);
        static readonly Color cText = Color.FromArgb(242, 242, 245);
        static readonly Color cName = Color.FromArgb(178, 178, 186);
        static readonly Color cDetail = Color.FromArgb(128, 128, 136);
        static readonly Color cGlyph = Color.FromArgb(215, 215, 222);
        static readonly Color cDim = Color.FromArgb(105, 105, 112);
        static readonly Color cTrack = Color.FromArgb(52, 52, 58);
        static readonly Color cGreen = Color.FromArgb(52, 199, 89);
        static readonly Color cAmber = Color.FromArgb(255, 176, 32);
        static readonly Color cRed = Color.FromArgb(255, 69, 58);
        static readonly Color cBlue = Color.FromArgb(64, 156, 255);

        readonly Settings cfg;
        readonly bool pinned;
        readonly AppleScanner apple = new AppleScanner();
        volatile Snapshot snap;
        volatile List<DriveItem> drives = new List<DriveItem>();
        int scanning;
        DateTime lastScan = DateTime.MinValue;
        float s = 1f;
        Font fValue, fName, fDetail, fGlyph, fBolt, fEmpty;
        StringFormat sfCenter, sfName;
        float nameH, itemH;

        List<Item> items = new List<Item>();
        string itemsSig = null;
        int lowest = -1;
        float We, He, Re;          // expanded size (physical px)

        bool open;
        float prog;                 // 0 = half circle, 1 = open
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastMs;
        long hoverSince = -1, leaveSince = -1;
        bool fullscreen;
        ContextMenuStrip menu;
        System.Windows.Forms.Timer anim, poll, data, scanTimer, devChange;

        public Island(Settings cfg, bool pinned)
        {
            this.cfg = cfg;
            this.pinned = pinned;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Battery Float";
            Size = new Size(1, 1);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiX / 96f;
            MakeFonts();
            BuildMenu();
            RefreshItems(true);
            if (pinned) { open = true; prog = 1f; }

            anim = new System.Windows.Forms.Timer();
            anim.Interval = 15;
            anim.Tick += delegate { OnAnim(); };

            poll = new System.Windows.Forms.Timer();
            poll.Interval = 50;
            poll.Tick += delegate { OnPoll(); };
            poll.Start();

            data = new System.Windows.Forms.Timer();
            data.Interval = 2000;
            data.Tick += delegate { OnData(); };
            data.Start();

            scanTimer = new System.Windows.Forms.Timer();
            scanTimer.Interval = 30000;
            scanTimer.Tick += delegate { StartScan(); };
            scanTimer.Start();

            devChange = new System.Windows.Forms.Timer();
            devChange.Interval = 1500;
            devChange.Tick += delegate { devChange.Stop(); StartScan(); };

            StartScan();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_LAYERED (per-pixel alpha) | WS_EX_TOOLWINDOW (no Alt-Tab) | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SetClickThrough(!open);
            Render();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        // ---------------- fonts + layout

        void MakeFonts()
        {
            fValue = new Font("Segoe UI Semibold", 13.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fName = new Font("Segoe UI", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fDetail = new Font("Segoe UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fEmpty = new Font("Segoe UI", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            string ico = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            fGlyph = new Font(ico, 18f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            fBolt = new Font(ico, 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);

            sfCenter = new StringFormat(StringFormatFlags.NoWrap);
            sfCenter.Alignment = StringAlignment.Center;
            sfCenter.LineAlignment = StringAlignment.Center;
            sfCenter.Trimming = StringTrimming.EllipsisCharacter;

            sfName = new StringFormat(StringFormatFlags.LineLimit);
            sfName.Alignment = StringAlignment.Center;
            sfName.LineAlignment = StringAlignment.Near;
            sfName.Trimming = StringTrimming.EllipsisWord;

            nameH = (float)Math.Ceiling(fName.GetHeight() * 2) + 1;
        }

        static bool FontExists(string n)
        {
            using (var f = new Font(n, 10f)) return string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase);
        }

        void Relayout()
        {
            int n = Math.Max(1, items.Count);
            We = PadX * s * 2 + n * SegW * s;
            He = PadTop * s + SegH * s + PadBottom * s;
            Re = Radius * s;
        }

        // ---------------- data

        void StartScan()
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;
            lastScan = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Snapshot sn = null;
                try { sn = Pnp.Scan(); } catch { }
                if (sn != null)
                {
                    snap = sn;
                    apple.SetSnapshot(sn);
                    apple.EnsureRunning(sn.Apple.Count > 0); // on an MTA pool thread, as WinRT likes
                }
                try { drives = Drives.Scan(); } catch { }
                Interlocked.Exchange(ref scanning, 0);
                try { BeginInvoke(new MethodInvoker(delegate { RefreshItems(false); })); } catch { }
            });
        }

        static string GlyphFor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere") || n.Contains("trackball")) return G_MOUSE;
            if (n.Contains("keyboard") || n.Contains("keys")) return G_KEYBOARD;
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("joy-con")) return G_PAD;
            if (n.Contains("speaker") || n.Contains("soundcore") || n.Contains("boom")) return G_SPEAKER;
            if (n.Contains("airpods") || n.Contains("buds") || n.Contains("head") || n.Contains("beats") || n.Contains("wh-") || n.Contains("wf-") ||
                n.Contains("jbl") || n.Contains("bose") || n.Contains("audio") || n.Contains("pods") || n.Contains("earphone")) return G_HEADPHONE;
            return G_BT;
        }

        // GDI+ has no emoji fallback: drop emoji so "My AirPods Max 🎧" does not show a box
        static string Display(string n)
        {
            if (n == null) return "";
            var sb = new StringBuilder();
            foreach (char c in n)
            {
                if (char.IsSurrogate(c) || c == '\uFE0F' || c == '\u200D' || (c >= '\u2600' && c <= '\u27BF')) continue;
                sb.Append(c);
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        static string Ago(double sec)
        {
            if (sec < 60) return "<1 min ago";
            if (sec < 3600) return ((int)(sec / 60)) + " min ago";
            return ((int)(sec / 3600)) + " h ago";
        }

        static string N(int v) { return AppleReading.Valid(v) ? (v * 10).ToString() : "–"; }

        Item AppleItem(string name, string key, int pid, AppleReading r, BatteryNode win, DateTime now)
        {
            var it = new Item();
            it.Key = "apple:" + key;
            it.Name = Display(name);
            it.Glyph = G_HEADPHONE;
            if (r == null)
            {
                if (win != null) { it.Pct = win.Battery; it.Value = win.Battery + "%"; return it; }
                it.Value = "—";
                it.Detail = "waiting for signal";
                return it;
            }
            double age = (now - r.At).TotalSeconds;
            it.Stale = age > 120;
            if (Models.IsSingle(pid))
            {
                int v = Math.Max(AppleReading.Valid(r.Left) ? r.Left : -1, AppleReading.Valid(r.Right) ? r.Right : -1);
                it.Pct = v >= 0 ? v * 10 : -1;
                it.Charging = r.ChargingLeft || r.ChargingRight;
            }
            else
            {
                int l = AppleReading.Valid(r.Left) ? r.Left : 99, rr = AppleReading.Valid(r.Right) ? r.Right : 99;
                int lo = Math.Min(l, rr);
                it.Pct = lo <= 10 ? lo * 10 : (AppleReading.Valid(r.Case) ? r.Case * 10 : -1);
                it.Charging = lo <= 10 && (l == lo ? r.ChargingLeft : r.ChargingRight);
                it.Detail = "L " + N(r.Left) + "  R " + N(r.Right) + "  Case " + N(r.Case);
            }
            it.Value = it.Pct >= 0 ? it.Pct + "%" : "—";
            if (it.Stale) { it.Charging = false; it.Detail = "seen " + Ago(age); }
            return it;
        }

        List<Item> BuildItems()
        {
            var list = new List<Item>();
            Snapshot sn = snap;
            if (sn != null)
            {
                Dictionary<int, AppleReading> readings = apple.Copy();
                DateTime now = DateTime.Now;
                var usedPid = new HashSet<int>();
                var covered = new HashSet<Guid>();

                // AirPods / Beats connected to this PC
                foreach (AppleDev a in sn.Apple)
                {
                    if (!a.Connected) continue;
                    AppleReading r = null;
                    if (usedPid.Add(a.Pid)) readings.TryGetValue(a.Pid, out r);
                    BatteryNode win = null;
                    foreach (BatteryNode b in sn.Batteries) if (a.Container != Guid.Empty && b.Container == a.Container) win = b;
                    if (a.Container != Guid.Empty) covered.Add(a.Container);
                    list.Add(AppleItem(a.Name, a.Mac, a.Pid, r, win, now));
                }

                // Windows-reported batteries (mice, keyboards, headsets...)
                var winItems = new List<Item>();
                foreach (BatteryNode b in sn.Batteries)
                {
                    if (b.Connected == false) continue;
                    if (b.Container != Guid.Empty && covered.Contains(b.Container)) continue;
                    var it = new Item();
                    it.Key = "win:" + (b.Container != Guid.Empty ? b.Container.ToString() : b.Id);
                    it.Name = Display(b.Name);
                    it.Pct = b.Battery;
                    it.Value = b.Battery + "%";
                    it.Glyph = GlyphFor(b.Name);
                    winItems.Add(it);
                }
                winItems.Sort(delegate (Item x, Item y) { return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase); });
                list.AddRange(winItems);
            }

            foreach (DriveItem d in drives)
            {
                var it = new Item();
                it.Key = "drive:" + d.Letter;
                it.Drive = true;
                it.Glyph = G_USB;
                string label = string.IsNullOrEmpty(d.Label) ? "USB drive" : Display(d.Label);
                it.Name = label + " (" + d.Letter + ")";
                it.Pct = d.Total > 0 ? (int)Math.Round(d.Free * 100.0 / d.Total) : -1;   // bar = free space
                it.Value = Drives.Size(d.Free);
                it.Detail = "free of " + Drives.Size(d.Total);
                list.Add(it);
            }
            return list;
        }

        // at most 4: batteries first, but keep room for up to 2 drives when drives are connected
        static List<Item> Cap(List<Item> all)
        {
            var bats = new List<Item>(); var drv = new List<Item>();
            foreach (Item it in all) (it.Drive ? drv : bats).Add(it);
            int nd = Math.Min(drv.Count, Math.Max(2, MaxItems - bats.Count));
            int nb = Math.Min(bats.Count, MaxItems - nd);
            nd = Math.Min(drv.Count, MaxItems - nb);
            var res = bats.GetRange(0, nb);
            res.AddRange(drv.GetRange(0, nd));
            return res;
        }

        void RefreshItems(bool force)
        {
            List<Item> list = Cap(BuildItems());
            var sb = new StringBuilder();
            int low = -1;
            foreach (Item it in list)
            {
                sb.Append(it.Sig()).Append('\n');
                if (!it.Drive && !it.Stale && it.Pct >= 0 && (low < 0 || it.Pct < low)) low = it.Pct;
            }
            string sig = sb.ToString();
            if (!force && sig == itemsSig && low == lowest) return;
            items = list;
            itemsSig = sig;
            lowest = low;
            Relayout();
            UpdateVisibility();
            Render();
        }

        void OnData()
        {
            int st;
            bool fs = false;
            if (Native.SHQueryUserNotificationState(out st) == 0) fs = st == 2 || st == 3 || st == 4; // busy / D3D fullscreen / presentation
            if (fs != fullscreen) { fullscreen = fs; UpdateVisibility(); }
            if (Visible)
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200); // stay topmost
            RefreshItems(false);
        }

        void UpdateVisibility()
        {
            // only there while a battery device or an external drive is connected
            bool want = items.Count > 0 && !(cfg.HideFullscreen && fullscreen);
            if (pinned) want = true;
            if (want && !Visible) { open = pinned; prog = pinned ? 1f : 0f; Show(); SetClickThrough(!open); Render(); }
            else if (!want && Visible) { open = false; prog = 0f; Hide(); }
        }

        // ---------------- hover + animation

        bool Inside(POINT c, float w, float h, float margin)
        {
            Rectangle scr = Screen.PrimaryScreen.Bounds;
            float cx = scr.Left + scr.Width / 2f;
            return c.X >= cx - w / 2f - margin && c.X <= cx + w / 2f + margin && c.Y >= scr.Top && c.Y <= scr.Top + h + margin;
        }

        void OnPoll()
        {
            if (!Visible || pinned) return;
            POINT c;
            if (!Native.GetCursorPos(out c)) return;
            long now = clock.ElapsedMilliseconds;
            if (!open)
            {
                // a held button means a drag (Snap Layouts bar, tab or text drag): never pop open over it
                bool dragging = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
                if (!dragging && Inside(c, CollW * s, CollH * s, 6 * s))
                {
                    if (hoverSince < 0) hoverSince = now;
                    else if (now - hoverSince >= 120) SetOpen(true);
                }
                else hoverSince = -1;
            }
            else
            {
                if (Inside(c, We, He, 14 * s) || menu.Visible) leaveSince = -1;
                else if (leaveSince < 0) leaveSince = now;
                else if (now - leaveSince >= 180) SetOpen(false);
            }
        }

        // closed: clicks fall through to whatever is under the half circle (hover is polled, not hit-tested)
        void SetClickThrough(bool on)
        {
            if (!IsHandleCreated) return;
            int ex = Native.GetWindowLongW(Handle, -20); // GWL_EXSTYLE
            int want = on ? (ex | 0x20) : (ex & ~0x20);  // WS_EX_TRANSPARENT
            if (want != ex) Native.SetWindowLongW(Handle, -20, want);
        }

        void SetOpen(bool o)
        {
            open = o;
            hoverSince = -1;
            leaveSince = -1;
            SetClickThrough(!o);
            if (o)
            {
                RefreshItems(false);
                if ((DateTime.Now - lastScan).TotalSeconds > 5) StartScan();
            }
            lastMs = clock.ElapsedMilliseconds;
            anim.Start();
        }

        void OnAnim()
        {
            long now = clock.ElapsedMilliseconds;
            float dt = now - lastMs;
            lastMs = now;
            float dur = open ? 220f : 160f;
            if (open) prog = Math.Min(1f, prog + dt / dur);
            else prog = Math.Max(0f, prog - dt / dur);
            Render();
            if ((open && prog >= 1f) || (!open && prog <= 0f)) anim.Stop();
        }

        float Eased()
        {
            // smooth ease-in-out, no overshoot, both ways
            float p = prog;
            return p < 0.5f ? 4f * p * p * p : 1f - (float)Math.Pow(-2f * p + 2f, 3) / 2f;
        }

        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static Color A(Color c, float a) { return Color.FromArgb((int)Math.Round(c.A * Clamp01(a)), c.R, c.G, c.B); }

        static Color LevelColor(int pct)
        {
            if (pct < 0) return cDim;
            if (pct < 30) return cRed;
            if (pct < 50) return cAmber;
            return cGreen;
        }

        static Color DriveColor(int freePct)
        {
            if (freePct < 0) return cDim;
            if (freePct < 30) return cRed;
            if (freePct < 50) return cAmber;
            return cBlue;
        }

        // ---------------- painting (per-pixel alpha via UpdateLayeredWindow)

        static GraphicsPath Shape(float w, float h, float r)
        {
            var p = new GraphicsPath();
            r = Math.Min(r, Math.Min(h, w / 2f));
            if (r < 0.5f) { p.AddRectangle(new RectangleF(0, 0, w, h)); return p; }
            p.AddLine(0, 0, w, 0);
            if (h - r > 0) p.AddLine(w, 0, w, h - r);
            p.AddArc(w - 2 * r, h - 2 * r, 2 * r, 2 * r, 0, 90);
            if (w - 2 * r > 0) p.AddLine(w - r, h, r, h);
            p.AddArc(0, h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Render()
        {
            if (!IsHandleCreated || !Visible) return;
            using (Bitmap bmp = RenderBitmap())
            {
                Rectangle scr = Screen.PrimaryScreen.Bounds;
                Push(bmp, scr.Left + (scr.Width - bmp.Width) / 2, scr.Top);
            }
        }

        Bitmap RenderBitmap()
        {
            float e = Eased();
            float cw = CollW * s, ch = CollH * s;
            float w = Math.Max(cw, Lerp(cw, We, e)), h = Math.Max(ch, Lerp(ch, He, e));
            float rad = Lerp(ch, Re, Clamp01(e));
            int bw = (int)Math.Ceiling(w), bh = (int)Math.Ceiling(h);
            var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath shape = Shape(bw, bh, rad))
                    {
                        using (var b = new SolidBrush(cBg)) g.FillPath(b, shape);
                        float ca = Clamp01(1f - e / 0.25f);
                        if (ca > 0) DrawCollapsed(g, bw, ca);
                        float ia = Clamp01((e - 0.7f) / 0.3f);   // content fades in once the bar is nearly full size
                        if (ia > 0)
                        {
                            g.SetClip(shape);
                            DrawContent(g, (bw - We) / 2f, ia);
                            g.ResetClip();
                        }
                    }
                }
            }
            return bmp;
        }

        void Push(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(memDc, hBmp);
                var size = new SIZE(bmp.Width, bmp.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(x, y);
                var blend = new BLENDFUNCTION();
                blend.BlendOp = 0;              // AC_SRC_OVER
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;          // AC_SRC_ALPHA
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        void DrawCollapsed(Graphics g, int bw, float alpha)
        {
            // a short level bar inside the tab: the lowest battery (or the fullest drive when only drives are connected)
            float bwid = 30 * s, bh = 4 * s;
            float x = (bw - bwid) / 2f, y = (CollH * s - bh) / 2f - 1 * s;
            int pct = lowest;
            bool drive = false;
            if (pct < 0)
                foreach (Item it in items) if (it.Drive && it.Pct >= 0 && (pct < 0 || it.Pct < pct)) { pct = it.Pct; drive = true; }
            FillBar(g, A(cTrack, alpha), x, y, bwid, bh);
            if (pct > 0) FillBar(g, A(drive ? DriveColor(pct) : LevelColor(pct), alpha), x, y, Math.Max(bh, bwid * pct / 100f), bh);
        }

        static void FillBar(Graphics g, Color c, float x, float y, float w, float h)
        {
            if (w <= 0 || h <= 0) return;
            float r = Math.Min(h, w);
            using (var path = new GraphicsPath())
            {
                path.AddArc(x, y, r, h, 90, 180);
                path.AddArc(x + w - r, y, r, h, 270, 180);
                path.CloseFigure();
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }
        }

        void DrawContent(Graphics g, float ox, float alpha)
        {
            float sw = SegW * s;
            for (int i = 0; i < items.Count; i++)
            {
                float x = ox + PadX * s + i * sw, y = PadTop * s;
                if (i > 0)
                    using (var p = new Pen(A(cTrack, alpha), Math.Max(1f, s)))
                        g.DrawLine(p, x, y + 12 * s, x, y + SegH * s - 12 * s);
                DrawItem(g, items[i], x, y, alpha);
            }
        }

        void DrawItem(Graphics g, Item it, float x, float y, float a)
        {
            float sw = SegW * s, sh = SegH * s, pad = 12 * s, textX = x + 44 * s;
            Color lvl = it.Stale ? cDim : (it.Drive ? DriveColor(it.Pct) : LevelColor(it.Pct));

            using (var gb = new SolidBrush(A(it.Stale ? cDim : cGlyph, a)))
                g.DrawString(it.Glyph, fGlyph, gb, new RectangleF(x + pad, y, 26 * s, sh), sfCenter);

            // value on the right, with a bolt when charging
            string val = it.Value ?? "";
            SizeF vs = g.MeasureString(val, fValue);
            float vw = Math.Min(vs.Width, 80 * s);
            float right = x + sw - pad;
            var nearRight = new StringFormat(sfCenter); nearRight.Alignment = StringAlignment.Far;
            using (var vb = new SolidBrush(A(it.Stale ? cDim : cText, a)))
                g.DrawString(val, fValue, vb, new RectangleF(right - 80 * s, y + 7 * s, 80 * s, 20 * s), nearRight);
            float nameRight = right - vw - 6 * s;
            if (it.Charging)
            {
                using (var cb = new SolidBrush(A(lvl, a)))
                    g.DrawString(G_BOLT, fBolt, cb, new RectangleF(nameRight - 14 * s, y + 7 * s, 14 * s, 20 * s), sfCenter);
                nameRight -= 16 * s;
            }

            var nameFmt = new StringFormat(sfCenter); nameFmt.Alignment = StringAlignment.Near;
            using (var nb = new SolidBrush(A(it.Stale ? cDim : cName, a)))
                g.DrawString(it.Name ?? "", fName, nb, new RectangleF(textX, y + 7 * s, Math.Max(10, nameRight - textX), 20 * s), nameFmt);

            float barW = right - textX, bh = 4 * s, by = y + 31 * s;
            FillBar(g, A(cTrack, a), textX, by, barW, bh);
            if (it.Pct > 0) FillBar(g, A(lvl, a), textX, by, Math.Max(bh, barW * Math.Min(100, it.Pct) / 100f), bh);

            if (!string.IsNullOrEmpty(it.Detail))
                using (var db = new SolidBrush(A(cDetail, a)))
                    g.DrawString(it.Detail, fDetail, db, new RectangleF(textX, y + 37 * s, barW, 15 * s), nameFmt);
        }

        // ---------------- demo: renders showcase frames with the real drawing code and made-up devices

        Island(float scale)
        {
            s = scale;
            MakeFonts();
            BuildMenu();
        }

        static Item Bat(string name, string glyph, int pct, string detail, bool charging)
        {
            var it = new Item();
            it.Key = name; it.Name = name; it.Glyph = glyph; it.Pct = pct; it.Value = pct + "%";
            it.Detail = detail; it.Charging = charging;
            return it;
        }

        static Item Drv(string name, double freeGb, double totalGb)
        {
            var it = new Item();
            it.Key = name; it.Name = name; it.Glyph = G_USB; it.Drive = true;
            long free = (long)(freeGb * 1073741824.0), total = (long)(totalGb * 1073741824.0);
            it.Pct = (int)Math.Round(free * 100.0 / total);
            it.Value = Drives.Size(free);
            it.Detail = "free of " + Drives.Size(total);
            return it;
        }

        void SetDemoItems(List<Item> list)
        {
            items = list;
            lowest = -1;
            foreach (Item it in list) if (!it.Drive && it.Pct >= 0 && (lowest < 0 || it.Pct < lowest)) lowest = it.Pct;
            Relayout();
        }

        public static int Demo(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, "f*.png")) File.Delete(old);
            var isl = new Island(1.25f);
            float s = isl.s;

            var scenes = new List<KeyValuePair<string, List<Item>>>();
            scenes.Add(new KeyValuePair<string, List<Item>>(
                "Batteries:  green 50%+   ·   amber under 50%   ·   red under 30%   ·   bolt = charging",
                new List<Item> {
                    Bat("AirPods Pro", G_HEADPHONE, 70, "L 70  R 80  Case 40", false),
                    Bat("MX Master 3S", G_MOUSE, 45, null, false),
                    Bat("K380 Keyboard", G_KEYBOARD, 22, null, false),
                    Bat("WH-1000XM5", G_HEADPHONE, 88, null, true) }));
            scenes.Add(new KeyValuePair<string, List<Item>>(
                "External USB drives:  bar = free space   ·   blue 50%+ free   ·   amber under 50%   ·   red under 30%",
                new List<Item> {
                    Drv("SanDisk Ultra (E:)", 41, 58),
                    Drv("Samsung T7 (F:)", 380, 931),
                    Drv("Backup HDD (G:)", 120, 1863) }));
            scenes.Add(new KeyValuePair<string, List<Item>>(
                "Both at once, up to 4 connections   ·   hidden when nothing is connected",
                new List<Item> {
                    Bat("AirPods Max", G_HEADPHONE, 50, null, false),
                    Bat("Xbox Controller", G_PAD, 12, null, false),
                    Drv("SanDisk Ultra (E:)", 41, 58),
                    Drv("Samsung T7 (F:)", 380, 931) }));

            int W = (int)(1120 * s), H = (int)(150 * s), bezel = (int)(26 * s);
            var list = new StringBuilder();
            int n = 0;
            Action<string, bool, int> frame = null;
            string caption = "";
            frame = delegate (string unused, bool cursorOn, int ms)
            {
                using (var canvas = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (var wall = new LinearGradientBrush(new Rectangle(0, bezel, W, H - bezel), Color.FromArgb(38, 52, 92), Color.FromArgb(70, 40, 88), 20f))
                        g.FillRectangle(wall, 0, bezel, W, H - bezel);
                    using (var bz = new SolidBrush(Color.FromArgb(10, 10, 11))) g.FillRectangle(bz, 0, 0, W, bezel);
                    float cam = 9 * s, cx = W / 2f, cy = bezel / 2f;
                    using (var c1 = new SolidBrush(Color.FromArgb(34, 34, 40))) g.FillEllipse(c1, cx - cam / 2, cy - cam / 2, cam, cam);
                    using (var c2 = new SolidBrush(Color.FromArgb(70, 80, 120))) g.FillEllipse(c2, cx - cam / 5, cy - cam / 5, cam / 2.5f, cam / 2.5f);

                    using (Bitmap w = isl.RenderBitmap())
                        g.DrawImage(w, (W - w.Width) / 2, bezel);

                    if (cursorOn) DrawCursor(g, cx + 8 * s, isl.prog >= 1f ? bezel + isl.He + 3 * s : bezel + 8 * s, s);

                    using (var cb = new SolidBrush(Color.FromArgb(225, 230, 240)))
                    using (var f = new Font("Segoe UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        var sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(caption, f, cb, new RectangleF(0, H - 34 * s, W, 30 * s), sf);
                    }
                    string name = "f" + n.ToString("000") + ".png";
                    canvas.Save(Path.Combine(dir, name), ImageFormat.Png);
                    list.AppendLine(name + " " + ms);
                    n++;
                }
            };

            foreach (var sc in scenes)
            {
                caption = sc.Key;
                isl.SetDemoItems(sc.Value);
                isl.open = false; isl.prog = 0f;
                frame(null, false, 1100);                       // closed tab, cursor away
                frame(null, true, 350);                         // hover
                isl.open = true;
                for (int i = 1; i <= 9; i++) { isl.prog = i / 9f; frame(null, true, 28); }
                frame(null, true, 3200);                        // open, read it
                isl.open = false;
                for (int i = 6; i >= 0; i--) { isl.prog = i / 7f; frame(null, false, 28); }
            }
            File.WriteAllText(Path.Combine(dir, "frames.txt"), list.ToString());
            return 0;
        }

        static void DrawCursor(Graphics g, float x, float y, float s)
        {
            float k = 1.15f * s;
            PointF[] p = {
                new PointF(x, y), new PointF(x, y + 17 * k), new PointF(x + 4 * k, y + 13 * k), new PointF(x + 7 * k, y + 20 * k),
                new PointF(x + 9.5f * k, y + 19 * k), new PointF(x + 6.5f * k, y + 12 * k), new PointF(x + 12 * k, y + 12 * k) };
            using (var fill = new SolidBrush(Color.White)) g.FillPolygon(fill, p);
            using (var pen = new Pen(Color.Black, 1.2f * s)) { pen.LineJoin = LineJoin.Round; g.DrawPolygon(pen, p); }
        }

        // ---------------- menu (right-click), device changes

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            var miAuto = new ToolStripMenuItem("Start with Windows", null, delegate (object o, EventArgs e)
            {
                cfg.Autostart = !cfg.Autostart;
                cfg.Save();
                try { AutoStart.Set(cfg.Autostart); } catch { }
            });
            var miFull = new ToolStripMenuItem("Hide during fullscreen apps", null, delegate { cfg.HideFullscreen = !cfg.HideFullscreen; cfg.Save(); UpdateVisibility(); });
            var miRefresh = new ToolStripMenuItem("Refresh", null, delegate { StartScan(); });
            var miExit = new ToolStripMenuItem("Exit", null, delegate { ExitApp(); });
            menu.Items.AddRange(new ToolStripItem[] { miAuto, miFull, miRefresh, new ToolStripSeparator(), miExit });
            menu.Opening += delegate { miAuto.Checked = cfg.Autostart; miFull.Checked = cfg.HideFullscreen; };
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                Native.SetForegroundWindow(Handle);
                menu.Show(Cursor.Position);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0219 && devChange != null) { devChange.Stop(); devChange.Start(); } // WM_DEVICECHANGE: drive or device added/removed
            base.WndProc(ref m);
        }

        void ExitApp()
        {
            try { apple.EnsureRunning(false); } catch { }
            Application.Exit();
        }
    }

    // ---------------------------------------------------------------- diagnostics

    static class Dump
    {
        public static int Run(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Battery Float diagnostic  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  on " + Environment.MachineName);
            Snapshot s = Pnp.Scan();
            sb.AppendLine();
            sb.AppendLine("Bluetooth devices (" + s.Bt.Count + "):");
            foreach (BtDevice d in s.Bt)
                sb.AppendLine(string.Format("  {0,-3} {1}  connected={2}  container={3}  {4}", d.Ble ? "LE" : "BR", d.Mac,
                    d.Connected.HasValue ? d.Connected.Value.ToString() : "?", d.Container, d.Name));
            sb.AppendLine();
            sb.AppendLine("Devices with a Windows battery value (" + s.Batteries.Count + "):");
            foreach (BatteryNode b in s.Batteries)
                sb.AppendLine(string.Format("  {0}%  connected={1}  {2}  [{3}]", b.Battery, b.Connected.HasValue ? b.Connected.Value.ToString() : "?", b.Name, b.Id));
            sb.AppendLine();
            sb.AppendLine("Paired Apple audio devices (" + s.Apple.Count + "):");
            foreach (AppleDev a in s.Apple)
                sb.AppendLine(string.Format("  {0}  pid=0x{1:X4} ({2})  connected={3}  {4}", a.Mac, a.Pid, Models.Name(a.Pid), a.Connected, a.Name));

            sb.AppendLine();
            sb.AppendLine("Drives (bus: 7 USB, 0xC SD, 0xD MMC, 0x11 NVMe, 0xB SATA, 0xE/0xF virtual):");
            var dlog = new List<string>();
            List<DriveItem> drives = Drives.Scan(dlog);
            foreach (string l in dlog) sb.AppendLine("  " + l);
            foreach (DriveItem d in drives)
                sb.AppendLine(string.Format("  shown: {0} {1}  {2} free of {3}", d.Letter, d.Label, Drives.Size(d.Free), Drives.Size(d.Total)));

            var scanner = new AppleScanner();
            scanner.SetSnapshot(s);
            var lines = new List<string>();
            var seen = new HashSet<string>();
            scanner.Trace = delegate (AppleReading r, bool connected, string why)
            {
                lock (lines)
                {
                    string k = r.Hex + why;
                    if (!seen.Add(k) || lines.Count > 60) return;
                    lines.Add(string.Format("  {0:HH:mm:ss} {1}  [{2}...]  -> {3}", r.At, r.Describe(), r.Hex, why));
                }
            };
            var t = new Thread(delegate () { scanner.EnsureRunning(true); });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Thread.Sleep(12000);
            string status = scanner.Status;
            scanner.EnsureRunning(false);

            sb.AppendLine();
            sb.AppendLine("Apple battery broadcasts in 12 s (watcher " + status + (scanner.LastError != null ? ", error " + scanner.LastError : "") + "):");
            lock (lines) foreach (string l in lines) sb.AppendLine(l);
            if (lines.Count == 0) sb.AppendLine("  (none)");
            Dictionary<int, AppleReading> final = scanner.Copy();
            sb.AppendLine();
            sb.AppendLine("Readings kept:");
            foreach (var kv in final) sb.AppendLine("  " + kv.Value.Describe());
            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return 0;
            }
            catch { return 1; }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--dump")
                return Dump.Run(args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "batteryfloat-dump.txt"));
            if (args.Length >= 2 && args[0] == "--demo") return Island.Demo(args[1]);
            bool pinned = args.Length >= 1 && args[0] == "--pinned";

            bool created;
            using (var single = new Mutex(true, @"Local\BatteryFloat.Single", out created))
            {
                if (!created) return 0;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Settings cfg = Settings.Load();
                cfg.Save();
                try { AutoStart.Set(cfg.Autostart); } catch { }   // re-register every start (keeps the path right)
                var island = new Island(cfg, pinned);
                island.Show();
                Application.Run(new ApplicationContext());
                GC.KeepAlive(island);
                GC.KeepAlive(single);
            }
            return 0;
        }
    }
}
