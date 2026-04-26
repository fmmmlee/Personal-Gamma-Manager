using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HdrEventTest
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private readonly TextBox log;
        private readonly Label statusLabel;
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly Dictionary<string, bool> lastHdrState = new Dictionary<string, bool>();
        private int eventCount_DisplaySettingsChanged = 0;
        private int eventCount_WmDisplayChange = 0;
        private int eventCount_PollDetected = 0;
        private DateTime startTime;

        private const int WM_DISPLAYCHANGE = 0x007E;

        public MainForm()
        {
            Text = "HDR Event Reliability Tester";
            Width = 900;
            Height = 600;
            StartPosition = FormStartPosition.CenterScreen;

            statusLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 80,
                Font = new Font("Consolas", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10),
                BackColor = Color.FromArgb(240, 240, 240),
            };

            var buttonPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
            var clearBtn = new Button { Text = "Clear log", Left = 10, Top = 8, Width = 100 };
            clearBtn.Click += (s, e) => { log.Clear(); ResetCounters(); };
            var pollBtn = new Button { Text = "Force poll now", Left = 120, Top = 8, Width = 120 };
            pollBtn.Click += (s, e) => PollNow(true);
            buttonPanel.Controls.Add(clearBtn);
            buttonPanel.Controls.Add(pollBtn);

            log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                ReadOnly = true,
                WordWrap = false,
            };

            Controls.Add(log);
            Controls.Add(buttonPanel);
            Controls.Add(statusLabel);

            startTime = DateTime.Now;

            // Event hook 1: managed wrapper
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Seed baseline HDR state
            PollNow(true);
            UpdateStatus();

            // Poll every 500ms for ground truth
            pollTimer = new System.Windows.Forms.Timer { Interval = 500 };
            pollTimer.Tick += (s, e) => PollNow(false);
            pollTimer.Start();

            Append("=== Session started at " + startTime.ToString("HH:mm:ss.fff") + " ===");
            Append("Toggle HDR via Settings, hotkey, or your scripts. Watch which hooks fire.");
            Append("Poll runs every 500ms — it's the ground truth baseline.");
            Append("");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                eventCount_WmDisplayChange++;
                Append($"[{Stamp()}] >>> WM_DISPLAYCHANGE fired (bpp={m.WParam.ToInt32()}, res={m.LParam.ToInt64() & 0xFFFF}x{(m.LParam.ToInt64() >> 16) & 0xFFFF})");
                UpdateStatus();
            }
            base.WndProc(ref m);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            eventCount_DisplaySettingsChanged++;
            Append($"[{Stamp()}] >>> SystemEvents.DisplaySettingsChanged fired");
            UpdateStatus();
        }

        private void PollNow(bool initial)
        {
            try
            {
                var states = QueryAllHdrStates();
                foreach (var kv in states)
                {
                    bool prev;
                    bool had = lastHdrState.TryGetValue(kv.Key, out prev);
                    if (!had)
                    {
                        lastHdrState[kv.Key] = kv.Value;
                        Append($"[{Stamp()}]     Baseline: {kv.Key} HDR={(kv.Value ? "ON" : "off")}");
                    }
                    else if (prev != kv.Value)
                    {
                        lastHdrState[kv.Key] = kv.Value;
                        eventCount_PollDetected++;
                        Append($"[{Stamp()}] ### POLL detected HDR change on {kv.Key}: {(prev ? "ON" : "off")} -> {(kv.Value ? "ON" : "off")}");
                        UpdateStatus();
                    }
                    else if (initial)
                    {
                        // forced poll, no change
                    }
                }
                if (initial)
                {
                    Append($"[{Stamp()}]     (manual poll, no change detected)");
                }
            }
            catch (Exception ex)
            {
                Append($"[{Stamp()}] !!! Poll error: {ex.Message}");
            }
        }

        private void ResetCounters()
        {
            eventCount_DisplaySettingsChanged = 0;
            eventCount_WmDisplayChange = 0;
            eventCount_PollDetected = 0;
            startTime = DateTime.Now;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var hdrSummary = "";
            foreach (var kv in lastHdrState)
                hdrSummary += $"{kv.Key}={(kv.Value ? "HDR" : "sdr")}  ";

            statusLabel.Text =
                $"Running: {(DateTime.Now - startTime).TotalSeconds:F0}s     Current: {hdrSummary}\r\n" +
                $"DisplaySettingsChanged:   {eventCount_DisplaySettingsChanged,4}\r\n" +
                $"WM_DISPLAYCHANGE:         {eventCount_WmDisplayChange,4}\r\n" +
                $"Poll-detected HDR flips:  {eventCount_PollDetected,4}  <-- ground truth";
        }

        private string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

        private void Append(string s)
        {
            if (log.InvokeRequired) { log.BeginInvoke(new Action<string>(Append), s); return; }
            log.AppendText(s + "\r\n");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            pollTimer?.Stop();
            pollTimer?.Dispose();
            base.OnFormClosed(e);
        }

        // ===== QueryDisplayConfig + advanced color info P/Invoke =====

        private static Dictionary<string, bool> QueryAllHdrStates()
        {
            var result = new Dictionary<string, bool>();

            int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
            if (rc != 0) return result;

            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc != 0) return result;

            for (int i = 0; i < numPaths; i++)
            {
                var path = paths[i];

                // Get source device name (e.g. \\.\DISPLAY1)
                var srcName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                srcName.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                srcName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                srcName.header.adapterId = path.sourceInfo.adapterId;
                srcName.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref srcName) != 0) continue;

                // Get advanced color info for the target
                var acInfo = new DISPLAYCONFIG_ADVANCED_COLOR_INFO();
                acInfo.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
                acInfo.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADVANCED_COLOR_INFO>();
                acInfo.header.adapterId = path.targetInfo.adapterId;
                acInfo.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref acInfo) != 0) continue;

                bool hdrOn = (acInfo.value & 0x2) != 0; // bit1 = advancedColorEnabled
                result[srcName.viewGdiDeviceName] = hdrOn;
            }

            return result;
        }

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADVANCED_COLOR_INFO requestPacket);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx;
            public uint outputTechnology; public uint rotation; public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // 48-byte union blob; we don't inspect it but need the size right
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            // 48 bytes of union follow (padded by Size=64 hint; sizeof(header)=16, union=48)
        }

        private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
        {
            DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1,
            DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0 advancedColorSupported, bit1 advancedColorEnabled, bit2 wideColorEnforced, bit3 advancedColorForceDisabled
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }
    }
}
