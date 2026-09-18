using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public class CorsairFull
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE = 3, OPEN_EXISTING = 3;
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle h, ref ATT a);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, ref IntPtr p);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll", SetLastError = true)] static extern int HidP_GetCaps(IntPtr p, ref CAPS c);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] r, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] r, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] r, int len);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref DID did);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref DID did, IntPtr buf, uint sz, ref uint req, IntPtr d);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string n, uint a, uint sh, IntPtr sec, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, int n, out int written, IntPtr ov);

    [StructLayout(LayoutKind.Sequential)] struct ATT { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }
    [StructLayout(LayoutKind.Sequential)] struct DID { public int Size; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct CAPS { public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved; }

    static Guid HidGuid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
    static HashSet<string> seenReports = new HashSet<string>();

    public static void Main()
    {
        // Enumere aussi bien les appareils presents que tous les appareils de la classe HID.
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
        for (uint i = 0; ; i++)
        {
            var did = new DID { Size = Marshal.SizeOf(typeof(DID)) };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref did)) break;
            uint req = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
            if (req == 0) continue;
            IntPtr detail = Marshal.AllocHGlobal((int)req);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, detail, req, ref req, IntPtr.Zero)) continue;
                string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                if (path != null && path.StartsWith("\\\\?\\")) path = "\\\\.\\" + path.Substring(4);
                using (var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    var a = new ATT { Size = Marshal.SizeOf(typeof(ATT)) };
                    if (!HidD_GetAttributes(h, ref a)) continue;
                    if (a.Vid != 0x1B1C) continue;
                    var colId = path.Substring(path.LastIndexOf('#') + 1);
                    Console.WriteLine("[ENUM] " + colId);

                    var product = new byte[256];
                    HidD_GetProductString(h, product, product.Length);
                    var productStr = System.Text.Encoding.Unicode.GetString(product).TrimEnd('\0');

                    int inLen = 0, outLen = 0, featLen = 0, usagePage = 0, usage = 0;
                    var pp = IntPtr.Zero;
                    if (HidD_GetPreparsedData(h, ref pp) && pp != IntPtr.Zero)
                    {
                        var caps = new CAPS();
                        if (HidP_GetCaps(pp, ref caps) == 0x110000)
                        {
                            inLen = caps.InputReportByteLength; outLen = caps.OutputReportByteLength; featLen = caps.FeatureReportByteLength;
                            usagePage = caps.UsagePage; usage = caps.Usage;
                        }
                        HidD_FreePreparsedData(pp);
                    }
                    var col = path.Substring(path.LastIndexOf('#') + 1);
                    Console.WriteLine("=== " + col + " pid=" + a.Pid.ToString("X4") + " up=0x" + usagePage.ToString("X4") + " usage=0x" + usage.ToString("X4") + " in=" + inLen + " out=" + outLen + " feat=" + featLen);
                    Console.WriteLine(String.Concat("    product=", productStr));

                    // Feature reports
                    if (featLen >= 1)
                        for (byte fid = 0; fid < 0x14; fid++)
                        {
                            var fb = new byte[Math.Max(featLen, 64)];
                            fb[0] = fid;
                            if (HidD_GetFeature(h, fb, fb.Length))
                                Console.WriteLine("    FEATURE 0x" + fid.ToString("X2") + " = " + Hex(fb, 12));
                        }

                    // Output report attempts (Corsair protocol IDs)
                    if (outLen >= 2)
                    {
                        foreach (var pair in new byte[][] { new byte[] { 0xC9, 0x64 }, new byte[] { 0x64, 0x00 }, new byte[] { 0x01, 0x00 } })
                        {
                            var rq = new byte[outLen];
                            rq[0] = pair[0]; rq[1] = pair[1];
                            bool ok = HidD_SetOutputReport(h, rq, rq.Length);
                            Console.WriteLine(String.Concat("    SETOUT ", pair[0].ToString("X2"), " ", pair[1].ToString("X2"), " => ", ok, " err=", Marshal.GetLastWin32Error()));
                        }
                        // WriteFile path
                        var wq = new byte[outLen];
                        wq[0] = 0xC9; wq[1] = 0x64;
                        int wr;
                        bool wok = WriteFile(h, wq, wq.Length, out wr, IntPtr.Zero);
                        Console.WriteLine("    WRITEFILE 0xC9 0x64 => " + wok + " wrote=" + wr + " err=" + Marshal.GetLastWin32Error());
                    }

                    // Écoute des rapports d'entrée pendant 3 s (dédupliqués).
                    if (inLen >= 1)
                    {
                        var buf = new byte[Math.Max(inLen, 128)];
                        var sw = Environment.TickCount;
                        while (Environment.TickCount - sw < 3000)
                        {
                            int read;
                            if (ReadFile(h, buf, buf.Length, out read, IntPtr.Zero) && read > 0)
                            {
                                var key = Hex(buf, read);
                                if (seenReports.Add(key))
                                    Console.WriteLine(String.Concat("    INPUT len=", read, " ", key));
                            }
                        }
                    }
                }
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        SetupDiDestroyDeviceInfoList(set);
        Console.WriteLine("DONE");
    }

    static string Hex(byte[] b, int n)
    {
        var s = new StringBuilder();
        for (int k = 0; k < Math.Min(n, b.Length); k++) s.Append(b[k].ToString("X2")).Append(' ');
        return "[" + s.ToString().TrimEnd() + "]";
    }
}