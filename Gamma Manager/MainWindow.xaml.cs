using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Gamma_Manager
{
    public partial class MainWindow : Window
    {
        // ---------------------------------------------------------------------
        // State (mirrors the original WinForms version)
        // ---------------------------------------------------------------------
        private readonly CultureInfo customCulture = new CultureInfo("ru-RU");
        private readonly IniFile iniFile;
        private readonly List<Display.DisplayInfo> displays;
        private int numDisplay;
        private Display.DisplayInfo currDisplay;
        private readonly Dictionary<string, bool> lastHdrStates = new Dictionary<string, bool>();

        // True while we update controls programmatically — suppresses change handlers.
        private bool disableChangeFunc = false;

        // Colour channel selection
        private bool allColors = true;
        private bool redColor, greenColor, blueColor;

        // Window lifetime
        private bool reallyClosing = false;
        private readonly bool startMinimized;

        // Tray
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private System.Windows.Forms.ContextMenuStrip trayMenu;
        private readonly List<System.Windows.Forms.ToolStripComboBox> toolMonitors =
            new List<System.Windows.Forms.ToolStripComboBox>();

        private const string StartupRegName = "GammaManager";
        private const string AutoSwitchSection = "AutoSwitch";

        // ---------------------------------------------------------------------
        // Constructor
        // ---------------------------------------------------------------------
        public MainWindow(bool startMinimized = false)
        {
            InitializeComponent();
            this.startMinimized = startMinimized;

            iniFile = new IniFile("GammaManager.ini");

            displays = Display.QueryDisplayDevices();
            displays.Reverse();
            for (int i = 0; i < displays.Count; i++)
            {
                displays[i].numDisplay = i;
                ComboMonitors.Items.Add((i + 1) + ") " + displays[i].displayName);
            }
            numDisplay = 0;
            currDisplay = displays[numDisplay];

            disableChangeFunc = true;
            ComboMonitors.SelectedIndex = numDisplay;
            disableChangeFunc = false;

            RestoreLastState();

            disableChangeFunc = true;
            ChkRunOnStartup.IsChecked = IsRunOnStartupEnabled();
            disableChangeFunc = false;

            foreach (var d in displays)
                lastHdrStates[d.displayLink] = Display.IsHdrEnabled(d.displayLink);

            FillInfo(currDisplay);
            InitPresets();
            InitAutoSwitchProfiles();
            LoadAutoSwitchSettings();
            ApplyCurrentHdrProfiles();

            InitTrayIcon();
            RebuildTrayMenu();

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Apply DWM attributes before first render to avoid a flash of unstyled chrome.
            SourceInitialized += (s, e) => ApplyDwmEffects();
        }

        // ---------------------------------------------------------------------
        // Window lifecycle
        // ---------------------------------------------------------------------
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            CenterOnScreen();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!reallyClosing)
            {
                e.Cancel = true;
                SaveLastState();
                Hide();
                return;
            }
            SaveLastState();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            notifyIcon?.Dispose();
        }

        private void CenterOnScreen()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width  - ActualWidth)  / 2;
            Top  = wa.Top  + (wa.Height - ActualHeight) / 2;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---------------------------------------------------------------------
        // DWM acrylic / dark title bar / rounded corners
        // ---------------------------------------------------------------------
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMSBT_TRANSIENTWINDOW = 3;
        private const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        private void ApplyDwmEffects()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;

                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int backdrop = DWMSBT_TRANSIENTWINDOW;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch { }
        }

        // ---------------------------------------------------------------------
        // Tray icon
        // ---------------------------------------------------------------------
        private void InitTrayIcon()
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Gamma Manager",
                Visible = true
            };
            try
            {
                var streamInfo = Application.GetResourceStream(
                    new Uri("pack://application:,,,/TrayIcon.ico"));
                if (streamInfo != null)
                    notifyIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
            }
            catch { }
            notifyIcon.DoubleClick += (s, e) => ShowFromTray();

            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            notifyIcon.ContextMenuStrip = trayMenu;
        }

        private void RebuildTrayMenu()
        {
            trayMenu.Items.Clear();
            toolMonitors.Clear();

            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings");
            settingsItem.Click += (s, e) => ShowFromTray();
            trayMenu.Items.Add(settingsItem);
            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            for (int i = 0; i < displays.Count; i++)
            {
                var combo = new System.Windows.Forms.ToolStripComboBox(displays[i].displayName)
                {
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
                };
                combo.Items.Add(displays[i].displayName + ":");
                combo.Text = displays[i].displayName + ":";
                combo.SelectedIndexChanged += OnTrayMonitorIndexChanged;

                string[] presets = iniFile.GetSections();
                if (presets != null)
                {
                    foreach (string preset in presets)
                    {
                        if (preset == AutoSwitchSection) continue;
                        if (iniFile.Read("monitor", preset).Equals(displays[i].displayName))
                            combo.Items.Add(preset);
                    }
                }
                toolMonitors.Add(combo);
                trayMenu.Items.Add(combo);
            }

            trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => { reallyClosing = true; Close(); };
            trayMenu.Items.Add(exitItem);
        }

        private void ShowFromTray()
        {
            // Restore from minimised first so Show() actually paints the window
            // and the foreground-stealing call below succeeds.
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            CenterOnScreen();
            Activate();
            Focus();
            // Tray double-click counts as foreground-eligible user input, so this
            // succeeds even when WPF Activate() alone doesn't bring us to the front.
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            FillInfo(currDisplay);
        }

        private void OnTrayMonitorIndexChanged(object sender, EventArgs e)
        {
            if (disableChangeFunc) return;
            var combo = (System.Windows.Forms.ToolStripComboBox)sender;
            string monitor = combo.ToString().Substring(0, combo.ToString().IndexOf(":"));

            int targetIdx = 0;
            disableChangeFunc = true;
            for (int i = 0; i < displays.Count; i++)
            {
                if (monitor.Equals(displays[i].displayName))
                    targetIdx = i;
                else
                    toolMonitors[i].SelectedIndex = 0;
            }
            disableChangeFunc = false;

            var target = toolMonitors[targetIdx];
            if (target.SelectedIndex != 0)
            {
                Dispatcher.Invoke(() =>
                {
                    numDisplay = targetIdx;
                    currDisplay = displays[targetIdx];
                    ComboMonitors.SelectedIndex = targetIdx;
                    ApplyProfileToDisplay(target.Text, currDisplay);
                });
            }
        }

        // ---------------------------------------------------------------------
        // Title-bar window controls
        // ---------------------------------------------------------------------
        private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close(); // hides to tray

        // ---------------------------------------------------------------------
        // Slider value handlers
        // ---------------------------------------------------------------------
        private void OnSliderGammaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (disableChangeFunc || currDisplay == null) return;
            ComboPresets.Text = string.Empty;
            float v = (float)e.NewValue / 100f;
            TextGamma.Text = v.ToString("0.00", CultureInfo.InvariantCulture);

            if (allColors) { currDisplay.rGamma = currDisplay.gGamma = currDisplay.bGamma = v; }
            else if (redColor)   currDisplay.rGamma = v;
            else if (greenColor) currDisplay.gGamma = v;
            else if (blueColor)  currDisplay.bGamma = v;
            ApplyGammaRamp();
        }

        private void OnSliderBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (disableChangeFunc || currDisplay == null) return;
            ComboPresets.Text = string.Empty;
            float v = (float)e.NewValue / 100f;
            TextBrightness.Text = v.ToString("0.00", CultureInfo.InvariantCulture);

            if (allColors) { currDisplay.rBright = currDisplay.gBright = currDisplay.bBright = v; }
            else if (redColor)   currDisplay.rBright = v;
            else if (greenColor) currDisplay.gBright = v;
            else if (blueColor)  currDisplay.bBright = v;
            ApplyGammaRamp();
        }

        private void OnSliderContrastChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (disableChangeFunc || currDisplay == null) return;
            ComboPresets.Text = string.Empty;
            float v = (float)e.NewValue / 100f;
            TextContrast.Text = v.ToString("0.00", CultureInfo.InvariantCulture);

            if (allColors) { currDisplay.rContrast = currDisplay.gContrast = currDisplay.bContrast = v; }
            else if (redColor)   currDisplay.rContrast = v;
            else if (greenColor) currDisplay.gContrast = v;
            else if (blueColor)  currDisplay.bContrast = v;
            ApplyGammaRamp();
        }

        private void OnSliderMonitorContrastChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (disableChangeFunc || currDisplay == null) return;
            ComboPresets.Text = string.Empty;
            int v = (int)e.NewValue;
            TextMonitorContrast.Text = v.ToString(CultureInfo.InvariantCulture);
            currDisplay.monitorContrast = v;
            if (currDisplay.isExternal)
                ExternalMonitor.SetContrast(currDisplay.PhysicalHandle, (uint)v);
        }

        private void ApplyGammaRamp()
        {
            Gamma.SetGammaRamp(currDisplay.displayLink,
                Gamma.CreateGammaRamp(
                    currDisplay.rGamma, currDisplay.gGamma, currDisplay.bGamma,
                    currDisplay.rContrast, currDisplay.gContrast, currDisplay.bContrast,
                    currDisplay.rBright, currDisplay.gBright, currDisplay.bBright));
        }

        // ---------------------------------------------------------------------
        // Editable text-box → slider commit
        // ---------------------------------------------------------------------
        private bool TryParseFloatFlexible(string s, out float val)
        {
            s = (s ?? "").Trim();
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return true;
            if (float.TryParse(s, NumberStyles.Float, customCulture, out val)) return true;
            return false;
        }

        private void CommitFloat(TextBox box, Slider slider)
        {
            if (disableChangeFunc) return;
            if (TryParseFloatFlexible(box.Text, out float val))
            {
                double target = Math.Round(val * 100);
                slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, target));
            }
            box.Text = ((float)slider.Value / 100f).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private void CommitInt(TextBox box, Slider slider)
        {
            if (disableChangeFunc) return;
            if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, val));
            box.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
        }

        private void OnTextGammaCommit(object sender, RoutedEventArgs e) => CommitFloat(TextGamma, SliderGamma);
        private void OnTextBrightnessCommit(object sender, RoutedEventArgs e) => CommitFloat(TextBrightness, SliderBrightness);
        private void OnTextContrastCommit(object sender, RoutedEventArgs e) => CommitFloat(TextContrast, SliderContrast);
        private void OnTextMonitorContrastCommit(object sender, RoutedEventArgs e) => CommitInt(TextMonitorContrast, SliderMonitorContrast);

        private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            var box = (TextBox)sender;
            Slider slider =
                box == TextGamma ? SliderGamma :
                box == TextBrightness ? SliderBrightness :
                box == TextContrast ? SliderContrast :
                box == TextMonitorContrast ? SliderMonitorContrast : null;
            bool floatMode = slider != SliderMonitorContrast;
            if (slider == null) return;

            if (e.Key == Key.Enter)
            {
                if (floatMode) CommitFloat(box, slider); else CommitInt(box, slider);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = floatMode
                    ? ((float)slider.Value / 100f).ToString("0.00", CultureInfo.InvariantCulture)
                    : ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------------
        // Colour channel selection
        // ---------------------------------------------------------------------
        private void ClearColors()
        {
            BtnAllColors.Style = (System.Windows.Style)FindResource("PillButton");
            BtnRed.Style       = (System.Windows.Style)FindResource("PillButton");
            BtnGreen.Style     = (System.Windows.Style)FindResource("PillButton");
            BtnBlue.Style      = (System.Windows.Style)FindResource("PillButton");
            allColors = redColor = greenColor = blueColor = false;
        }

        private void SelectChannel(System.Windows.Controls.Button activeBtn)
        {
            ClearColors();
            activeBtn.Style = (System.Windows.Style)FindResource("PillButtonActive");
        }

        private void OnAllColorsClick(object sender, RoutedEventArgs e)
        {
            disableChangeFunc = true;
            SelectChannel(BtnAllColors);
            allColors = true;
            float g = (currDisplay.rGamma + currDisplay.gGamma + currDisplay.bGamma) / 3f;
            float c = (currDisplay.rContrast + currDisplay.gContrast + currDisplay.bContrast) / 3f;
            float b = (currDisplay.rBright + currDisplay.gBright + currDisplay.bBright) / 3f;
            TextGamma.Text     = g.ToString("0.00", CultureInfo.InvariantCulture);
            TextContrast.Text  = c.ToString("0.00", CultureInfo.InvariantCulture);
            TextBrightness.Text= b.ToString("0.00", CultureInfo.InvariantCulture);
            SliderGamma.Value      = (int)(g * 100);
            SliderContrast.Value   = (int)(c * 100);
            SliderBrightness.Value = (int)(b * 100);
            disableChangeFunc = false;
        }

        private void OnRedClick(object sender, RoutedEventArgs e)   => SetChannel(BtnRed,   c => (c.rGamma, c.rContrast, c.rBright), v => { redColor = true; });
        private void OnGreenClick(object sender, RoutedEventArgs e) => SetChannel(BtnGreen, c => (c.gGamma, c.gContrast, c.gBright), v => { greenColor = true; });
        private void OnBlueClick(object sender, RoutedEventArgs e)  => SetChannel(BtnBlue,  c => (c.bGamma, c.bContrast, c.bBright), v => { blueColor = true; });

        private void SetChannel(System.Windows.Controls.Button btn,
            Func<Display.DisplayInfo, (float g, float c, float b)> selector,
            Action<bool> setFlag)
        {
            disableChangeFunc = true;
            SelectChannel(btn);
            setFlag(true);
            var (g, c, b) = selector(currDisplay);
            TextGamma.Text     = g.ToString("0.00", CultureInfo.InvariantCulture);
            TextContrast.Text  = c.ToString("0.00", CultureInfo.InvariantCulture);
            TextBrightness.Text= b.ToString("0.00", CultureInfo.InvariantCulture);
            SliderGamma.Value      = (int)(g * 100);
            SliderContrast.Value   = (int)(c * 100);
            SliderBrightness.Value = (int)(b * 100);
            disableChangeFunc = false;
        }

        // ---------------------------------------------------------------------
        // Preset / monitor / unlock-contrast handlers
        // ---------------------------------------------------------------------
        private void OnUnlockContrastChanged(object sender, RoutedEventArgs e)
        {
            SliderContrast.Maximum = ChkUnlockContrast.IsChecked == true ? 10000 : 300;
        }

        private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (disableChangeFunc || ComboMonitors.SelectedItem == null) return;
            string item = ComboMonitors.SelectedItem.ToString();
            int idx = int.Parse(item.Substring(0, item.IndexOf(")")), CultureInfo.InvariantCulture) - 1;
            numDisplay = idx;
            currDisplay = displays[idx];
            FillInfo(currDisplay);
            InitPresets();
            InitAutoSwitchProfiles();
            LoadAutoSwitchSettings();
        }

        private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (disableChangeFunc || ComboPresets.SelectedItem == null) return;
            ApplyProfileToDisplay(ComboPresets.SelectedItem.ToString(), currDisplay);
            RebuildTrayMenu();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string typed = ComboPresets.Text;
            if (string.IsNullOrWhiteSpace(typed)) return;
            string section = currDisplay.displayName + ": " + typed;

            iniFile.Write("monitor", currDisplay.displayName, section);
            iniFile.Write("rGamma",    currDisplay.rGamma.ToString(customCulture),    section);
            iniFile.Write("gGamma",    currDisplay.gGamma.ToString(customCulture),    section);
            iniFile.Write("bGamma",    currDisplay.bGamma.ToString(customCulture),    section);
            iniFile.Write("rContrast", currDisplay.rContrast.ToString(customCulture), section);
            iniFile.Write("gContrast", currDisplay.gContrast.ToString(customCulture), section);
            iniFile.Write("bContrast", currDisplay.bContrast.ToString(customCulture), section);
            iniFile.Write("rBright",   currDisplay.rBright.ToString(customCulture),   section);
            iniFile.Write("gBright",   currDisplay.gBright.ToString(customCulture),   section);
            iniFile.Write("bBright",   currDisplay.bBright.ToString(customCulture),   section);
            iniFile.Write("monitorContrast", currDisplay.monitorContrast.ToString(customCulture), section);

            InitPresets();
            ComboPresets.Text = section;
            InitAutoSwitchProfiles();
            LoadAutoSwitchSettings();
            RebuildTrayMenu();
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ComboPresets.Text)) return;
            iniFile.DeleteSection(ComboPresets.Text);
            InitPresets();
            InitAutoSwitchProfiles();
            LoadAutoSwitchSettings();
            RebuildTrayMenu();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            disableChangeFunc = true;
            ComboPresets.Text = string.Empty;
            ClearColors();
            BtnAllColors.Style = (System.Windows.Style)FindResource("PillButtonActive");
            allColors = true;

            currDisplay.rGamma = currDisplay.gGamma = currDisplay.bGamma = 1;
            currDisplay.rContrast = currDisplay.gContrast = currDisplay.bContrast = 1;
            currDisplay.rBright = currDisplay.gBright = currDisplay.bBright = 0;

            SliderGamma.Value      = 100;
            SliderContrast.Value   = 100;
            SliderBrightness.Value = 0;

            if (currDisplay.isExternal) SliderMonitorContrast.Value = 50;

            Gamma.SetGammaRamp(currDisplay.displayLink, Gamma.CreateGammaRamp(1, 1, 1, 1, 1, 1, 0, 0, 0));
            FillInfo(currDisplay);
            InitPresets();
            RebuildTrayMenu();
            disableChangeFunc = false;
        }

        private void OnHideClick(object sender, RoutedEventArgs e)
        {
            SaveLastState();
            Hide();
        }

        // ---------------------------------------------------------------------
        // Preset list / FillInfo / state restore
        // ---------------------------------------------------------------------
        private void InitPresets()
        {
            disableChangeFunc = true;
            ComboPresets.Items.Clear();
            ComboPresets.Text = string.Empty;
            string[] sections = iniFile.GetSections();
            if (sections != null)
            {
                foreach (string s in sections)
                {
                    if (s == AutoSwitchSection) continue;
                    if (iniFile.Read("monitor", s).Equals(currDisplay.displayName))
                        ComboPresets.Items.Add(s);
                }
            }
            disableChangeFunc = false;
        }

        private void FillInfo(Display.DisplayInfo d)
        {
            disableChangeFunc = true;
            float g = (d.rGamma + d.gGamma + d.bGamma) / 3f;
            float c = (d.rContrast + d.gContrast + d.bContrast) / 3f;
            float b = (d.rBright + d.gBright + d.bBright) / 3f;

            TextGamma.Text      = g.ToString("0.00", CultureInfo.InvariantCulture);
            TextContrast.Text   = c.ToString("0.00", CultureInfo.InvariantCulture);
            TextBrightness.Text = b.ToString("0.00", CultureInfo.InvariantCulture);

            SliderGamma.Value      = (int)(g * 100);
            SliderContrast.Value   = (int)(c * 100);
            SliderBrightness.Value = (int)(b * 100);

            bool ext = d.isExternal;
            LabelMonitorContrast.Visibility   = ext ? Visibility.Visible : Visibility.Collapsed;
            SliderMonitorContrast.Visibility  = ext ? Visibility.Visible : Visibility.Collapsed;
            TextMonitorContrast.Visibility    = ext ? Visibility.Visible : Visibility.Collapsed;
            if (ext)
            {
                int mc = ExternalMonitor.GetContrast(d.PhysicalHandle);
                TextMonitorContrast.Text = mc.ToString(CultureInfo.InvariantCulture);
                SliderMonitorContrast.Value = mc;
            }
            disableChangeFunc = false;
        }

        private void SaveLastState()
        {
            foreach (var d in displays)
            {
                string section = "LastState_" + d.displayName;
                iniFile.Write("rGamma",    d.rGamma.ToString(customCulture),    section);
                iniFile.Write("gGamma",    d.gGamma.ToString(customCulture),    section);
                iniFile.Write("bGamma",    d.bGamma.ToString(customCulture),    section);
                iniFile.Write("rContrast", d.rContrast.ToString(customCulture), section);
                iniFile.Write("gContrast", d.gContrast.ToString(customCulture), section);
                iniFile.Write("bContrast", d.bContrast.ToString(customCulture), section);
                iniFile.Write("rBright",   d.rBright.ToString(customCulture),   section);
                iniFile.Write("gBright",   d.gBright.ToString(customCulture),   section);
                iniFile.Write("bBright",   d.bBright.ToString(customCulture),   section);
                iniFile.Write("monitorContrast", d.monitorContrast.ToString(), section);
            }
        }

        private void RestoreLastState()
        {
            foreach (var d in displays)
            {
                string section = "LastState_" + d.displayName;
                if (!iniFile.KeyExists("rGamma", section)) continue;

                try
                {
                    d.rGamma    = float.Parse(iniFile.Read("rGamma",    section), customCulture);
                    d.gGamma    = float.Parse(iniFile.Read("gGamma",    section), customCulture);
                    d.bGamma    = float.Parse(iniFile.Read("bGamma",    section), customCulture);
                    d.rContrast = float.Parse(iniFile.Read("rContrast", section), customCulture);
                    d.gContrast = float.Parse(iniFile.Read("gContrast", section), customCulture);
                    d.bContrast = float.Parse(iniFile.Read("bContrast", section), customCulture);
                    d.rBright   = float.Parse(iniFile.Read("rBright",   section), customCulture);
                    d.gBright   = float.Parse(iniFile.Read("gBright",   section), customCulture);
                    d.bBright   = float.Parse(iniFile.Read("bBright",   section), customCulture);
                    if (!int.TryParse(iniFile.Read("monitorContrast", section), out d.monitorContrast))
                        d.monitorContrast = -1;

                    Gamma.SetGammaRamp(d.displayLink,
                        Gamma.CreateGammaRamp(
                            d.rGamma,    d.gGamma,    d.bGamma,
                            d.rContrast, d.gContrast, d.bContrast,
                            d.rBright,   d.gBright,   d.bBright));
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------------
        // Run-on-startup registry helpers
        // ---------------------------------------------------------------------
        private bool IsRunOnStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                return key?.GetValue(StartupRegName) != null;
            }
        }

        private void SetRunOnStartup(bool enable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (enable)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    key.SetValue(StartupRegName, $"\"{exe}\" /startup");
                }
                else key.DeleteValue(StartupRegName, throwOnMissingValue: false);
            }
        }

        private void OnRunOnStartupChanged(object sender, RoutedEventArgs e)
        {
            if (!disableChangeFunc) SetRunOnStartup(ChkRunOnStartup.IsChecked == true);
        }

        // ---------------------------------------------------------------------
        // HDR auto-switch
        // ---------------------------------------------------------------------
        private void InitAutoSwitchProfiles()
        {
            disableChangeFunc = true;
            ComboSdrProfile.Items.Clear();
            ComboHdrProfile.Items.Clear();
            ComboSdrProfile.Items.Add("(none)");
            ComboHdrProfile.Items.Add("(none)");

            string[] sections = iniFile.GetSections();
            if (sections != null)
            {
                foreach (string s in sections)
                {
                    if (s == AutoSwitchSection) continue;
                    if (iniFile.Read("monitor", s).Equals(currDisplay.displayName))
                    {
                        ComboSdrProfile.Items.Add(s);
                        ComboHdrProfile.Items.Add(s);
                    }
                }
            }
            disableChangeFunc = false;
        }

        private void LoadAutoSwitchSettings()
        {
            disableChangeFunc = true;
            ChkAutoSwitch.IsChecked = iniFile.Read("enabled", AutoSwitchSection) == "1";

            string sdrKey = currDisplay.displayName + "_SDR";
            string hdrKey = currDisplay.displayName + "_HDR";
            string sdr = iniFile.Read(sdrKey, AutoSwitchSection);
            string hdr = iniFile.Read(hdrKey, AutoSwitchSection);

            ComboSdrProfile.SelectedItem = string.IsNullOrEmpty(sdr) ? "(none)" : sdr;
            ComboHdrProfile.SelectedItem = string.IsNullOrEmpty(hdr) ? "(none)" : hdr;
            if (ComboSdrProfile.SelectedIndex < 0) ComboSdrProfile.SelectedIndex = 0;
            if (ComboHdrProfile.SelectedIndex < 0) ComboHdrProfile.SelectedIndex = 0;
            disableChangeFunc = false;
        }

        private void OnAutoSwitchChanged(object sender, RoutedEventArgs e)
        {
            if (!disableChangeFunc)
                iniFile.Write("enabled", ChkAutoSwitch.IsChecked == true ? "1" : "0", AutoSwitchSection);
        }

        private void OnSdrProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (disableChangeFunc) return;
            string sel = ComboSdrProfile.SelectedItem?.ToString() ?? "(none)";
            iniFile.Write(currDisplay.displayName + "_SDR",
                sel == "(none)" ? "" : sel, AutoSwitchSection);
        }

        private void OnHdrProfileChanged(object sender, SelectionChangedEventArgs e)
        {
            if (disableChangeFunc) return;
            string sel = ComboHdrProfile.SelectedItem?.ToString() ?? "(none)";
            iniFile.Write(currDisplay.displayName + "_HDR",
                sel == "(none)" ? "" : sel, AutoSwitchSection);
        }

        private void ApplyProfileToDisplay(string presetName, Display.DisplayInfo display)
        {
            if (string.IsNullOrEmpty(presetName) || presetName == "(none)") return;
            if (!iniFile.KeyExists("rGamma", presetName)) return;

            try
            {
                display.rGamma    = float.Parse(iniFile.Read("rGamma",    presetName), customCulture);
                display.gGamma    = float.Parse(iniFile.Read("gGamma",    presetName), customCulture);
                display.bGamma    = float.Parse(iniFile.Read("bGamma",    presetName), customCulture);
                display.rContrast = float.Parse(iniFile.Read("rContrast", presetName), customCulture);
                display.gContrast = float.Parse(iniFile.Read("gContrast", presetName), customCulture);
                display.bContrast = float.Parse(iniFile.Read("bContrast", presetName), customCulture);
                display.rBright   = float.Parse(iniFile.Read("rBright",   presetName), customCulture);
                display.gBright   = float.Parse(iniFile.Read("gBright",   presetName), customCulture);
                display.bBright   = float.Parse(iniFile.Read("bBright",   presetName), customCulture);
                if (!int.TryParse(iniFile.Read("monitorContrast", presetName), out display.monitorContrast))
                    display.monitorContrast = -1;

                Gamma.SetGammaRamp(display.displayLink,
                    Gamma.CreateGammaRamp(
                        display.rGamma,    display.gGamma,    display.bGamma,
                        display.rContrast, display.gContrast, display.bContrast,
                        display.rBright,   display.gBright,   display.bBright));

                if (display.isExternal && display.monitorContrast >= 0)
                    ExternalMonitor.SetContrast(display.PhysicalHandle, (uint)display.monitorContrast);

                if (display.displayLink == currDisplay.displayLink)
                {
                    disableChangeFunc = true;
                    ComboPresets.Text = presetName;
                    FillInfo(display);
                    ClearColors();
                    BtnAllColors.Style = (System.Windows.Style)FindResource("PillButtonActive");
                    allColors = true;
                    disableChangeFunc = false;
                }
            }
            catch { }
        }

        private void ApplyCurrentHdrProfiles()
        {
            if (ChkAutoSwitch.IsChecked != true) return;
            foreach (var d in displays)
            {
                bool isHdr = Display.IsHdrEnabled(d.displayLink);
                lastHdrStates[d.displayLink] = isHdr;
                string key = d.displayName + (isHdr ? "_HDR" : "_SDR");
                ApplyProfileToDisplay(iniFile.Read(key, AutoSwitchSection), d);
            }
        }

        private void CheckAndApplyHdrProfiles()
        {
            if (ChkAutoSwitch.IsChecked != true) return;
            foreach (var d in displays)
            {
                bool isHdrNow = Display.IsHdrEnabled(d.displayLink);
                if (!lastHdrStates.TryGetValue(d.displayLink, out bool wasHdr))
                {
                    lastHdrStates[d.displayLink] = isHdrNow;
                    continue;
                }
                if (isHdrNow == wasHdr) continue;
                lastHdrStates[d.displayLink] = isHdrNow;
                string key = d.displayName + (isHdrNow ? "_HDR" : "_SDR");
                ApplyProfileToDisplay(iniFile.Read(key, AutoSwitchSection), d);
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) CheckAndApplyHdrProfiles();
            else Dispatcher.Invoke(CheckAndApplyHdrProfiles);
        }
    }
}
