using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Playnite.SDK;

namespace GlazeWMPlaynite
{
    /// <summary>
    /// Sets which monitor is the PRIMARY display.
    ///
    /// This is the only thing that reliably decides where a game opens. A game
    /// that pins its own window pins it to (0,0), and (0,0) is by definition the
    /// primary display — which is why Dishonored kept appearing on the ultrawide
    /// no matter where it was moved to afterwards.
    ///
    /// It is also the entirety of what Display Helper does (its DLL imports
    /// CDS_SET_PRIMARY / setAsPrimaryDevice and touches no windows). Doing it here
    /// removes that dependency and, more usefully, lets the user pick a screen by
    /// NAME — "acer" — instead of Display Helper's \\.\DISPLAY1/2/3, which are GDI
    /// names that mean nothing to a human and get renumbered by driver resets.
    ///
    /// The awkward part is that Windows has no "make this primary" call. Primary is
    /// defined as the display at the origin, so the whole desktop has to be
    /// translated: offset every monitor by the negative of the target's position,
    /// then commit. Get that wrong and monitors overlap or the desktop tears.
    /// </summary>
    internal static class PrimaryDisplay
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int CDS_UPDATEREGISTRY = 0x01;
        private const int CDS_NORESET = 0x10000000;
        private const int CDS_SET_PRIMARY = 0x10;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int DM_POSITION = 0x00000020;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTL { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public POINTL dmPosition;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags,
                       dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType,
                       dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint num, ref DISPLAY_DEVICE d, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

        private class Screen
        {
            public string Device;
            public DEVMODE Mode;
            public bool IsPrimary;
        }

        private static List<Screen> Attached()
        {
            var list = new List<Screen>();
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(dm);
                if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) continue;

                list.Add(new Screen
                {
                    Device = dd.DeviceName,
                    Mode = dm,
                    IsPrimary = dm.dmPosition.x == 0 && dm.dmPosition.y == 0,
                });
            }
            return list;
        }

        /// <summary>The GDI device name of whichever monitor is currently primary.</summary>
        public static string Current()
        {
            foreach (var s in Attached())
            {
                if (s.IsPrimary) return s.Device;
            }
            return null;
        }

        /// <summary>
        /// Find a monitor's GDI device name by matching its resolution.
        ///
        /// Resolution is the only property shared between what GlazeWM reports and
        /// what the GDI layer exposes here without a great deal more plumbing, and
        /// it is sufficient on a desk where no two monitors match. Where two do,
        /// the first is taken and that is logged.
        /// </summary>
        public static string DeviceForResolution(int width, int height)
        {
            var hits = new List<string>();
            foreach (var s in Attached())
            {
                if (s.Mode.dmPelsWidth == width && s.Mode.dmPelsHeight == height)
                {
                    hits.Add(s.Device);
                }
            }
            if (hits.Count == 0) return null;
            if (hits.Count > 1)
            {
                Logger.Warn($"GlazeWM: {hits.Count} monitors are {width}x{height}; using {hits[0]}.");
            }
            return hits[0];
        }

        /// <summary>
        /// Make <paramref name="device"/> the primary display.
        ///
        /// Windows defines the primary as the monitor at (0,0), so this translates
        /// the whole desktop: every monitor is offset by the negative of the
        /// target's current position, which puts the target at the origin and
        /// leaves every relative position unchanged.
        ///
        /// Changes are staged with CDS_NORESET and committed in one call at the
        /// end, so the desktop rearranges once rather than flickering per monitor.
        /// </summary>
        public static bool Set(string device)
        {
            if (string.IsNullOrEmpty(device)) return false;

            var screens = Attached();
            Screen target = null;
            foreach (var s in screens)
            {
                if (string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase)) target = s;
            }
            if (target == null)
            {
                Logger.Warn($"GlazeWM: {device} is not attached; cannot make it primary.");
                return false;
            }
            if (target.IsPrimary)
            {
                Logger.Info($"GlazeWM: {device} is already primary.");
                return true;
            }

            int dx = -target.Mode.dmPosition.x;
            int dy = -target.Mode.dmPosition.y;

            foreach (var s in screens)
            {
                var dm = s.Mode;
                dm.dmPosition.x += dx;
                dm.dmPosition.y += dy;
                dm.dmFields = DM_POSITION;

                int flags = CDS_UPDATEREGISTRY | CDS_NORESET;
                if (ReferenceEquals(s, target)) flags |= CDS_SET_PRIMARY;

                int rc = ChangeDisplaySettingsEx(s.Device, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
                if (rc != DISP_CHANGE_SUCCESSFUL)
                {
                    Logger.Warn($"GlazeWM: staging {s.Device} failed ({rc}).");
                }
            }

            // Commit everything at once.
            int commit = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (commit != DISP_CHANGE_SUCCESSFUL)
            {
                Logger.Error($"GlazeWM: applying the display change failed ({commit}).");
                return false;
            }

            Logger.Info($"GlazeWM: {device} is now the primary display.");
            return true;
        }
    }
}
