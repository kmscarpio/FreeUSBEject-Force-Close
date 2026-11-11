// FreeUSBEject - a small, free tray utility to safely eject USB / external drives.
// Original implementation. Build with build.bat (uses csc.exe that ships with Windows).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace FreeUSBEject
{
    static class Log
    {
        public static void Write(string msg)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FreeUSBEject.log");
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        public const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;
        public const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        public const uint IOCTL_STORAGE_CHECK_VERIFY = 0x002D4800;
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

        [StructLayout(LayoutKind.Sequential)]
        public struct PREVENT_MEDIA_REMOVAL { [MarshalAs(UnmanagedType.U1)] public bool PreventMediaRemoval; }

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_DEVICE_NUMBER { public uint DeviceType; public int DeviceNumber; public int PartitionNumber; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref PREVENT_MEDIA_REMOVAL inBuf,
            uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref STORAGE_PROPERTY_QUERY inBuf,
            uint inSize, byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            out STORAGE_DEVICE_NUMBER outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteVolumeMountPointW(string mountPoint);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType,
            StringBuilder vetoName, int nameLen, uint flags);
    }

    class DriveEntry
    {
        public char Letter;
        public string Label;
        public long Size;
        public int DiskNumber;
    }

    static class Ejector
    {
        const int BusType1394 = 4, BusTypeUsb = 7;

        static SafeFileHandle OpenVolume(char letter, uint access)
        {
            return Native.CreateFile(@"\\.\" + letter + ":", access,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        // Returns true if the volume sits on a USB or FireWire bus (this covers external hard drives).
        static bool IsExternalBus(char letter)
        {
            using (SafeFileHandle h = OpenVolume(letter, 0))
            {
                if (h.IsInvalid) return false;
                var q = new Native.STORAGE_PROPERTY_QUERY(); // StorageDeviceProperty, StandardQuery
                byte[] buf = new byte[1024];
                uint ret;
                if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, ref q,
                    (uint)Marshal.SizeOf(q), buf, (uint)buf.Length, out ret, IntPtr.Zero))
                    return false;
                int bus = BitConverter.ToInt32(buf, 28); // STORAGE_DEVICE_DESCRIPTOR.BusType
                return bus == BusTypeUsb || bus == BusType1394;
            }
        }

        static int GetDiskNumber(char letter)
        {
            using (SafeFileHandle h = OpenVolume(letter, 0))
            {
                if (h.IsInvalid) return -1;
                Native.STORAGE_DEVICE_NUMBER n;
                uint ret;
                if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0,
                    out n, (uint)Marshal.SizeOf(typeof(Native.STORAGE_DEVICE_NUMBER)), out ret, IntPtr.Zero))
                    return -1;
                return n.DeviceNumber;
            }
        }

        // Drives that were just ejected stay hidden until they are really gone / plugged in again.
        static readonly HashSet<char> Ejected = new HashSet<char>();

        static bool HasMedia(char letter)
        {
            using (SafeFileHandle h = OpenVolume(letter, 0))
            {
                if (h.IsInvalid) return false;
                uint ret;
                return Native.DeviceIoControl(h, Native.IOCTL_STORAGE_CHECK_VERIFY, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
            }
        }

        // Forget ejected drives whose letter disappeared or whose media is gone (so they can show up again when re-plugged).
        public static void PruneEjected()
        {
            lock (Ejected)
            {
                if (Ejected.Count == 0) return;
                var present = new HashSet<char>();
                foreach (DriveInfo d in DriveInfo.GetDrives()) present.Add(char.ToUpperInvariant(d.Name[0]));
                foreach (char l in new List<char>(Ejected))
                    if (!present.Contains(l) || !HasMedia(l)) Ejected.Remove(l);
            }
        }

        public static List<DriveEntry> ListExternal()
        {
            try { PruneEjected(); } catch (Exception ex) { Log.Write("Prune: " + ex.Message); }
            var list = new List<DriveEntry>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Removable && d.DriveType != DriveType.Fixed) continue;
                    char letter = d.Name[0];
                    lock (Ejected) { if (Ejected.Contains(char.ToUpperInvariant(letter))) continue; }
                    bool ready = false;
                    try { ready = d.IsReady; } catch { }
                    if (d.DriveType == DriveType.Removable && !ready) continue; // empty slot / no media
                    if (d.DriveType == DriveType.Fixed && !IsExternalBus(letter)) continue;
                    var e = new DriveEntry();
                    e.Letter = letter;
                    e.DiskNumber = GetDiskNumber(letter);
                    try { if (ready) { e.Label = d.VolumeLabel; e.Size = d.TotalSize; } } catch { }
                    list.Add(e);
                }
                catch (Exception ex) { Log.Write("ListExternal " + d.Name + ": " + ex.Message); }
            }
            return list;
        }

        static string FindPnpId(int diskNumber)
        {
            try
            {
                string target = @"\\.\PHYSICALDRIVE" + diskNumber;
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, PNPDeviceID FROM Win32_DiskDrive"))
                    foreach (ManagementObject mo in s.Get())
                        if (string.Equals((string)mo["DeviceID"], target, StringComparison.OrdinalIgnoreCase))
                            return (string)mo["PNPDeviceID"];
            }
            catch (Exception ex) { Log.Write("FindPnpId: " + ex.Message); }
            return null;
        }

        // Closes (then kills) programs whose .exe is located on one of the given drives.
        static void CloseProgramsOn(List<char> letters)
        {
            try
            {
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                var pids = new List<int>();
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process"))
                    foreach (ManagementObject mo in s.Get())
                    {
                        string exe = mo["ExecutablePath"] as string;
                        if (string.IsNullOrEmpty(exe) || exe.Length < 3) continue;
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        if (pid == self) continue;
                        foreach (char l in letters)
                            if (char.ToUpperInvariant(exe[0]) == char.ToUpperInvariant(l) && exe[1] == ':')
                            { pids.Add(pid); break; }
                    }
                foreach (int pid in pids)
                {
                    try
                    {
                        var p = System.Diagnostics.Process.GetProcessById(pid);
                        Log.Write("Closing " + p.ProcessName + " (" + pid + ")");
                        p.CloseMainWindow();
                        if (!p.WaitForExit(2000)) p.Kill();
                    }
                    catch (Exception ex) { Log.Write("Close " + pid + ": " + ex.Message); }
                }
                if (pids.Count > 0) Thread.Sleep(500);
            }
            catch (Exception ex) { Log.Write("CloseProgramsOn: " + ex.Message); }
        }

        // Ejects and, on success, hides the drive(s) from the list right away.
        public static string Eject(DriveEntry drive, bool force)
        {
            var letters = new List<char>();
            foreach (DriveEntry e in ListExternal())
                if (e.Letter == drive.Letter || (drive.DiskNumber >= 0 && e.DiskNumber == drive.DiskNumber))
                    letters.Add(char.ToUpperInvariant(e.Letter));
            if (!letters.Contains(char.ToUpperInvariant(drive.Letter))) letters.Add(char.ToUpperInvariant(drive.Letter));
            string err = EjectCore(drive, force);
            if (err == null)
            {
                lock (Ejected) { foreach (char l in letters) Ejected.Add(l); }
                foreach (char l in letters) RemoveFromExplorer(l);
            }
            return err;
        }

        // After a successful eject: free the drive letter and tell Explorer to drop the entry.
        static void RemoveFromExplorer(char letter)
        {
            string root = letter + @":\";
            try
            {
                if (!Native.DeleteVolumeMountPointW(root))
                    Log.Write("DeleteVolumeMountPoint " + root + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            catch (Exception ex) { Log.Write("DeleteVolumeMountPoint: " + ex.Message); }
            try
            {
                const uint PATHW_FLUSH = 0x0005 | 0x1000;
                Native.SHChangeNotify(0x00000040, PATHW_FLUSH, root, IntPtr.Zero); // SHCNE_MEDIAREMOVED
                Native.SHChangeNotify(0x00000080, PATHW_FLUSH, root, IntPtr.Zero); // SHCNE_DRIVEREMOVED
            }
            catch (Exception ex) { Log.Write("SHChangeNotify: " + ex.Message); }
        }

        // Returns null on success, otherwise an error message.
        // force = close programs running from the drive and dismount even if files are open.
        static string EjectCore(DriveEntry drive, bool force)
        {
            // All volumes (drive letters) that live on the same physical disk must be released.
            var all = ListExternal();
            var targets = new List<char>();
            foreach (DriveEntry e in all)
                if (e.Letter == drive.Letter || (drive.DiskNumber >= 0 && e.DiskNumber == drive.DiskNumber))
                    targets.Add(e.Letter);

            if (force) CloseProgramsOn(targets);

            var handles = new List<SafeFileHandle>();
            try
            {
                foreach (char letter in targets)
                {
                    SafeFileHandle h = OpenVolume(letter, Native.GENERIC_READ | Native.GENERIC_WRITE);
                    if (h.IsInvalid)
                        return "Cannot open drive " + letter + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    handles.Add(h);

                    uint ret;
                    bool locked = false;
                    for (int i = 0; i < (force ? 2 : 5) && !locked; i++)
                    {
                        locked = Native.DeviceIoControl(h, Native.FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                        if (!locked) Thread.Sleep(300);
                    }
                    if (!locked && force) Log.Write("Lock failed on " + letter + ": forcing dismount");
                    if (!locked && !force)
                        return "Drive " + letter + ": is in use by a program. Close any open files or windows on it and try again.";
                    if (!Native.DeviceIoControl(h, Native.FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero))
                        return "Could not dismount " + letter + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }

                // Allow removal, then CLOSE our handles: Windows vetoes the eject while we still hold the volume open.
                // (The volumes stay dismounted after the handles are closed.)
                foreach (SafeFileHandle h in handles)
                {
                    uint ret;
                    var allow = new Native.PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = false };
                    Native.DeviceIoControl(h, Native.IOCTL_STORAGE_MEDIA_REMOVAL, ref allow,
                        (uint)Marshal.SizeOf(allow), IntPtr.Zero, 0, out ret, IntPtr.Zero);
                    h.Dispose();
                }
                handles.Clear();

                // Preferred: ask Plug and Play to eject the device (same as "Safely Remove Hardware").
                string pnp = drive.DiskNumber >= 0 ? FindPnpId(drive.DiskNumber) : null;
                if (pnp != null)
                {
                    uint inst;
                    if (Native.CM_Locate_DevNodeW(out inst, pnp, 0) == 0)
                    {
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            uint cur = inst;
                            for (int depth = 0; depth < 3; depth++)
                            {
                                int veto; var name = new StringBuilder(260);
                                int cr = Native.CM_Request_Device_EjectW(cur, out veto, name, name.Capacity, 0);
                                Log.Write("CM eject attempt " + attempt + " depth " + depth + " cr=" + cr + " veto=" + veto + " " + name);
                                if (cr == 0) { Log.Write("Ejected via Plug and Play"); return null; }
                                uint parent;
                                if (Native.CM_Get_Parent(out parent, cur, 0) != 0) break;
                                cur = parent;
                            }
                            Thread.Sleep(500);
                        }
                    }
                }

                // Fallback: classic media eject (flash drives / card readers).
                foreach (char letter in targets)
                {
                    using (SafeFileHandle h = OpenVolume(letter, Native.GENERIC_READ | Native.GENERIC_WRITE))
                    {
                        if (h.IsInvalid) continue;
                        uint ret;
                        if (Native.DeviceIoControl(h, Native.IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero))
                        {
                            Log.Write("Ejected via media eject (fallback)");
                            return null;
                        }
                    }
                }
                return "Windows refused to eject this device. It may still be in use. Details are in FreeUSBEject.log.";
            }
            finally
            {
                foreach (SafeFileHandle h in handles) h.Dispose();
            }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "FreeUSBEject";

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool enable)
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();

        public TrayApp()
        {
            // Uses the icon embedded in the .exe (from FreeUSBEject.ico at build time); falls back to a default.
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            if (tray.Icon == null) tray.Icon = SystemIcons.Application;
            tray.Text = "FreeUSBEject";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                try { if (e.Button == MouseButtons.Left) ShowMenu(); }
                catch (Exception ex) { Log.Write("MouseClick: " + ex); }
            };
            menu.Opening += delegate { Rebuild(); };
            Rebuild();
            tray.Visible = true;
            // Keep the list current: forgets ejected drives once they are gone.
            refresh.Interval = 2000;
            refresh.Tick += delegate { try { Ejector.PruneEjected(); } catch (Exception ex) { Log.Write("Refresh: " + ex.Message); } };
            refresh.Start();
        }

        // Opens the menu the same way a right-click does, so it closes when you click elsewhere or press Esc.
        void ShowMenu()
        {
            try
            {
                var m = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m != null) { m.Invoke(tray, null); return; }
            }
            catch (Exception ex) { Log.Write("ShowContextMenu: " + ex.Message); }
            // Fallback: give the menu focus first so outside clicks dismiss it.
            Native.SetForegroundWindow(menu.Handle);
            menu.Show(Cursor.Position);
        }

        void Rebuild()
        {
            try
            {
                menu.Items.Clear();
                List<DriveEntry> drives = Ejector.ListExternal();
                if (drives.Count == 0)
                    menu.Items.Add(new ToolStripMenuItem("No USB / external drives found") { Enabled = false });
                foreach (DriveEntry d in drives)
                {
                    DriveEntry drive = d;
                    string text = "Eject " + d.Letter + ":";
                    if (!string.IsNullOrEmpty(d.Label)) text += "  " + d.Label;
                    if (d.Size > 0) text += "  (" + FormatSize(d.Size) + ")";
                    var item = new ToolStripMenuItem(text);
                    item.Click += delegate { DoEject(drive, false); };
                    menu.Items.Add(item);
                    var force = new ToolStripMenuItem("Force eject " + d.Letter + ":  (close programs using it)");
                    force.Click += delegate { DoEject(drive, true); };
                    menu.Items.Add(force);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Rebuild: " + ex);
                menu.Items.Clear();
                menu.Items.Add(new ToolStripMenuItem("Error listing drives (see log)") { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());
            var startup = new ToolStripMenuItem("Start with Windows") { Checked = Startup.IsEnabled(), CheckOnClick = false };
            startup.Click += delegate
            {
                try { Startup.Set(!Startup.IsEnabled()); }
                catch (Exception ex) { Log.Write("Startup: " + ex.Message); tray.ShowBalloonTip(4000, "FreeUSBEject", "Could not change startup setting.", ToolTipIcon.Error); }
            };
            menu.Items.Add(startup);
            menu.Items.Add("Exit", null, delegate { tray.Visible = false; tray.Dispose(); Application.Exit(); });
        }

        static string FormatSize(long b)
        {
            double gb = b / (1024.0 * 1024 * 1024);
            return gb >= 1024 ? (gb / 1024).ToString("0.#") + " TB" : gb.ToString("0.#") + " GB";
        }

        void DoEject(DriveEntry d, bool force)
        {
            try
            {
                string err = Ejector.Eject(d, force);
                if (err == null)
                    tray.ShowBalloonTip(3000, "FreeUSBEject", "Drive " + d.Letter + ": can now be safely removed.", ToolTipIcon.Info);
                else
                    tray.ShowBalloonTip(5000, "FreeUSBEject", err, ToolTipIcon.Error);
            }
            catch (Exception ex)
            {
                Log.Write("DoEject: " + ex);
                tray.ShowBalloonTip(5000, "FreeUSBEject", "Unexpected error: " + ex.Message, ToolTipIcon.Error);
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "FreeUSBEject_SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                // Never let an unexpected error close the app: log it and keep running.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log.Write("ThreadException: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Log.Write("Unhandled: " + e.ExceptionObject); };
                Application.Run(new TrayApp());
            }
        }
    }
}
