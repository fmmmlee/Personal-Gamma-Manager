using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Gamma_Manager
{
    internal class Display
    {
        #region Classes
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        public class DisplayInfo
        {
            public int numDisplay;
            public string displayName;
            public string displayLink;
            public bool isExternal;
            public IntPtr PhysicalHandle;
            public float rGamma = 1.0f;
            public float gGamma = 1.0f;
            public float bGamma = 1.0f; 
            public float rContrast = 1.0f; 
            public float gContrast = 1.0f; 
            public float bContrast = 1.0f;
            public float rBright = 0.0f;
            public float gBright = 0.0f;
            public float bBright = 0.0f;
            public int monitorContrast;
        }
        #endregion

        #region DllImport
        [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", EntryPoint = "GetMonitorBrightness")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorBrightness(IntPtr handle, ref uint minimumBrightness, ref uint currentBrightness, ref uint maxBrightness);

        [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitor")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitors")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        #endregion

        public static List<DisplayInfo> QueryDisplayDevices()
        {
            List<DisplayInfo> monitors = new List<DisplayInfo>();

            WinApi.MonitorEnumDelegate MonitorEnumProc = new WinApi.MonitorEnumDelegate(
            (IntPtr hMonitor, IntPtr hdcMonitor, ref WinApi.RECT lprcMonitor, IntPtr dwData) =>
            {
                WinApi.MONITORINFOEX monitorInfo = new WinApi.MONITORINFOEX() { Size = Marshal.SizeOf(typeof(WinApi.MONITORINFOEX)) };

                DisplayInfo monitor = new DisplayInfo();
                if (WinApi.GetMonitorInfo(hMonitor, ref monitorInfo))
                {
                    WinApi.DISPLAY_DEVICE device = new WinApi.DISPLAY_DEVICE();
                    device.Initialize();
                    if (WinApi.EnumDisplayDevices(monitorInfo.DeviceName.ToLPTStr(), 0, ref device, 0))
                    {
                        monitor.displayLink = monitorInfo.DeviceName;

                    }
                    string DName = device.DeviceID;
                    DName = DName.Substring(DName.IndexOf("\\") + 1);
                    DName = DName.Substring(0, DName.IndexOf("\\"));
                    monitor.displayName = DName;

                    /*Console.WriteLine("Left: " + lprcMonitor.Left);
                    Console.WriteLine("Right: " + lprcMonitor.Right);
                    Console.WriteLine("Top: " + lprcMonitor.Top);
                    Console.WriteLine("Bottom: " + lprcMonitor.Bottom);*/

                }
                for (int i = 0; i < monitors.Count; i++)
                {

                }

                uint physicalMonitorsCount = 0;

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref physicalMonitorsCount))
                {
                    // Cannot get monitor count
                    return true;
                }

                var physicalMonitors = new PHYSICAL_MONITOR[physicalMonitorsCount];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, physicalMonitorsCount, physicalMonitors))
                {
                    // Cannot get physical monitor handle
                    return true;
                }

                foreach (PHYSICAL_MONITOR physicalMonitor in physicalMonitors)
                {

                    uint minValue = 0, currentValue = 0, maxValue = 0;
                    if (!GetMonitorBrightness(physicalMonitor.hPhysicalMonitor, ref minValue, ref currentValue, ref maxValue))
                    {
                        monitor.isExternal = false;
                        monitor.PhysicalHandle = (IntPtr)(-1);
                        DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                        continue;
                    }
                    else
                    {
                        monitor.isExternal = true;
                    }

                    monitor.PhysicalHandle = physicalMonitor.hPhysicalMonitor;

                }
                if (monitor.isExternal)
                {
                    monitor.monitorContrast = ExternalMonitor.GetContrast(monitor.PhysicalHandle);
                }
                else
                {
                    monitor.monitorContrast = -1;
                }
                monitors.Add(monitor);
                return true;
            });

            WinApi.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, MonitorEnumProc, IntPtr.Zero);
            return monitors;
        }

        /*public static void DisposeMonitors(IEnumerable<DisplayInfo> monitors)
        {
            if (monitors?.Any() == true)
            {
                PHYSICAL_MONITOR[] monitorArray = monitors.Select(m => new PHYSICAL_MONITOR { hPhysicalMonitor = m.PhysicalHandle }).ToArray();
                DestroyPhysicalMonitors((uint)monitorArray.Length, monitorArray);
            }
        }*/

        /// <summary>
        /// Returns true if Windows HDR (Advanced Color) is currently enabled on the display
        /// identified by the GDI device name (e.g. "\\.\DISPLAY1").
        /// Uses QueryDisplayConfig + DisplayConfigGetDeviceInfo — no COM, no UWP required.
        /// </summary>
        public static bool IsHdrEnabled(string displayLink)
        {
            try
            {
                uint numPaths, numModes;
                if (WinApi.GetDisplayConfigBufferSizes(WinApi.QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0)
                    return false;

                var paths = new WinApi.DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new WinApi.DISPLAYCONFIG_MODE_INFO[numModes];

                if (WinApi.QueryDisplayConfig(WinApi.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                    return false;

                for (int i = 0; i < numPaths; i++)
                {
                    // Get the GDI source name (e.g. "\\.\DISPLAY1") for this path
                    var sourceName = new WinApi.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sourceName.header.type = WinApi.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sourceName.header.size = (uint)Marshal.SizeOf(sourceName);
                    sourceName.header.adapterId = paths[i].sourceInfo.adapterId;
                    sourceName.header.id = paths[i].sourceInfo.id;

                    if (WinApi.DisplayConfigGetSourceDeviceName(ref sourceName) != 0)
                        continue;

                    string gdiName = sourceName.viewGdiDeviceName != null
                        ? sourceName.viewGdiDeviceName.TrimEnd('\0')
                        : string.Empty;

                    if (!string.Equals(gdiName, displayLink, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Matched — query advanced color (HDR) state for this target.
                    // Prefer the 24H2+ query: its activeColorMode distinguishes true HDR
                    // from SDR-with-ACM, where the legacy advancedColorEnabled bit reads
                    // true for both and would mask HDR<->SDR transitions.
                    var colorInfo2 = new WinApi.DISPLAYCONFIG_ADVANCED_COLOR_INFO_2();
                    colorInfo2.header.type = WinApi.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
                    colorInfo2.header.size = (uint)Marshal.SizeOf(colorInfo2);
                    colorInfo2.header.adapterId = paths[i].targetInfo.adapterId;
                    colorInfo2.header.id = paths[i].targetInfo.id;

                    if (WinApi.DisplayConfigGetAdvancedColorInfo2(ref colorInfo2) == 0)
                        return colorInfo2.activeColorMode == WinApi.DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;

                    // Pre-24H2 fallback
                    var colorInfo = new WinApi.DISPLAYCONFIG_ADVANCED_COLOR_INFO();
                    colorInfo.header.type = WinApi.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                    colorInfo.header.size = (uint)Marshal.SizeOf(colorInfo);
                    colorInfo.header.adapterId = paths[i].targetInfo.adapterId;
                    colorInfo.header.id = paths[i].targetInfo.id;

                    if (WinApi.DisplayConfigGetAdvancedColorInfo(ref colorInfo) != 0)
                        continue;

                    // Bit 1 = advancedColorEnabled (HDR on)
                    return (colorInfo.value & 0x2) != 0;
                }
            }
            catch
            {
                // Silently ignore — HDR detection is best-effort
            }
            return false;
        }

    }
}
