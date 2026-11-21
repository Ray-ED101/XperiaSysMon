using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace XperiaSysMon
{
    public partial class MainWindow : Window
    {
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter? _diskCounter;
        private List<PerformanceCounter> _coreCounters = new();
        private List<ProgressBar> _coreBars = new();

        // Swipe navigation state
        private Point _swipeStartPoint;
        private bool _isSwiping;
        private int _currentPage = 0; // 0 = System Monitor, 1 = Command Deck

        // Primary monitor cursor lock state
        private RECT _primaryMonitorBounds;
        private bool _hasPrimaryBounds = false;
        private bool _isCursorLocked = true;

        private static readonly Color CpuCoolColor = (Color)ColorConverter.ConvertFromString("#38BDF8");
        private static readonly Color GpuCoolColor = (Color)ColorConverter.ConvertFromString("#C084FC");
        private static readonly Color WarmColor = (Color)ColorConverter.ConvertFromString("#F59E0B");
        private static readonly Color HotColor = (Color)ColorConverter.ConvertFromString("#EF4444");

        // Win32 Virtual Key injection for media/volume/hotkey shortcuts
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_SHIFT = 0x10;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12; // Alt key
        private const byte VK_LWIN = 0x5B;
        private const byte VK_SNAPSHOT = 0x2C;
        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // Win32 Cursor Clipping APIs
        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        // Win32 Primary Monitor Focus Helpers
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Core Temp Shared Memory Structure
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CORE_TEMP_SHARED_DATA
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public uint[] uiLoad;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
            public uint[] uiTjMax;
            public uint uiCoreCnt;
            public uint uiCPUCnt;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public float[] fTemp;
            public float fVID;
            public float fCPUSpeed;
            public float fFSBSpeed;
            public float fMultipier;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)]
            public byte[] sCPUName;
            public byte ucFahrenheit;
            public byte ucDeltaToTjMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public MainWindow()
        {
            InitializeComponent();
            LoadDeckShortcuts();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();

            try
            {
                _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
                _diskCounter.NextValue();
            }
            catch { }

            InitCoreBars();
            UpdateArcGeometry(CpuArcPath, 0);
            UpdateArcGeometry(GpuArcPath, 0);

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (s, e) => await UpdateMetricsAsync();
            timer.Start();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

            RECT? secondaryWorkArea = null;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                MONITORINFO mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    if ((mi.dwFlags & 1) != 0)
                    {
                        // Primary monitor (Display 1)
                        _primaryMonitorBounds = mi.rcMonitor;
                        _hasPrimaryBounds = true;
                    }
                    else
                    {
                        // Secondary monitor (Xperia VDD)
                        secondaryWorkArea = mi.rcWork;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (_hasPrimaryBounds && _isCursorLocked)
            {
                ClipCursor(ref _primaryMonitorBounds);
            }

            if (secondaryWorkArea.HasValue)
            {
                var rect = secondaryWorkArea.Value;
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

                WindowState = WindowState.Normal;
                Left = rect.Left * dpiX;
                Top = rect.Top * dpiY;
                WindowState = WindowState.Maximized;
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // Release cursor lock when app exits
            ClipCursor(IntPtr.Zero);
        }

        private void ToggleCursorLock()
        {
            _isCursorLocked = !_isCursorLocked;
            if (_isCursorLocked && _hasPrimaryBounds)
            {
                ClipCursor(ref _primaryMonitorBounds);
            }
            else
            {
                ClipCursor(IntPtr.Zero);
            }
        }

        private void FocusPrimaryMonitor()
        {
            if (!_hasPrimaryBounds) return;

            int centerX = (_primaryMonitorBounds.Left + _primaryMonitorBounds.Right) / 2;
            int centerY = (_primaryMonitorBounds.Top + _primaryMonitorBounds.Bottom) / 2;

            IntPtr hwnd = WindowFromPoint(new POINT { X = centerX, Y = centerY });
            if (hwnd != IntPtr.Zero)
            {
                IntPtr rootHwnd = GetAncestor(hwnd, 2); // GA_ROOT = 2
                if (rootHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(rootHwnd);
                    Thread.Sleep(60);
                }
            }
        }

        // ==================== SWIPE & TAP NAVIGATION LOGIC ====================
        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _swipeStartPoint = e.GetPosition(this);
            _isSwiping = true;
        }

        private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSwiping) return;
            _isSwiping = false;

            Point endPoint = e.GetPosition(this);
            double deltaX = endPoint.X - _swipeStartPoint.X;
            double deltaY = Math.Abs(endPoint.Y - _swipeStartPoint.Y);

            // Detect horizontal swipe (> 75px travel, mostly horizontal)
            if (Math.Abs(deltaX) > 75 && Math.Abs(deltaX) > deltaY)
            {
                if (deltaX < 0 && _currentPage == 0)
                {
                    SwitchPage(1); // Swipe Left -> Command Deck
                    e.Handled = true;
                }
                else if (deltaX > 0 && _currentPage == 1)
                {
                    SwitchPage(0); // Swipe Right -> System Monitor
                    e.Handled = true;
                }
            }
        }

        private void SwitchPage(int pageIndex)
        {
            _currentPage = pageIndex;
            double monitorTargetX = pageIndex == 0 ? 0 : -1260;
            double deckTargetX = pageIndex == 0 ? 1260 : 0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(260);

            MonitorTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                To = monitorTargetX,
                Duration = duration,
                EasingFunction = ease
            });

            DeckTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                To = deckTargetX,
                Duration = duration,
                EasingFunction = ease
            });

            DotPage0.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pageIndex == 0 ? "#38BDF8" : "#2E2E38"));
            DotPage1.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pageIndex == 1 ? "#38BDF8" : "#2E2E38"));
        }

        private void SwitchIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            SwitchPage(_currentPage == 0 ? 1 : 0);
            e.Handled = true;
        }

        // ==================== JSON DECK SHORTCUTS & CUSTOM ICONS ====================
        public class DeckShortcutConfig
        {
            public string Icon { get; set; } = "App";
            public string Title { get; set; } = "SHORTCUT";
            public string Subtitle { get; set; } = "LAUNCH APP";
            public string Color { get; set; } = "#38BDF8";
            public string ActionType { get; set; } = "App"; // "App", "Key", "Hotkey", "Record", "Screenshot", or "CursorLock"
            public string Target { get; set; } = "";
            public string Arguments { get; set; } = "";
        }

        public class DeckTileViewModel
        {
            public ImageSource? IconImage { get; set; }
            public Visibility ImageVisibility { get; set; } = Visibility.Collapsed;
            public string IconGlyph { get; set; } = "\uE71D";
            public Visibility GlyphVisibility { get; set; } = Visibility.Visible;
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public Brush AccentBrush { get; set; } = Brushes.DeepSkyBlue;
            public string ActionType { get; set; } = "App";
            public string Target { get; set; } = "";
            public string Arguments { get; set; } = "";
        }

        private static string ResolveIconGlyph(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return "\uE71D";
            if (iconName.Length == 1) return iconName;

            return iconName.Trim().ToLowerInvariant() switch
            {
                "gamepad" or "steam" or "game" => "\uE7FC",
                "play" or "playpause" or "media" => "\uE768",
                "next" or "nexttrack" or "skip" => "\uE893",
                "prev" or "prevtrack" => "\uE892",
                "mute" or "volume" or "sound" => "\uE74F",
                "volup" => "\uE995",
                "voldown" => "\uE993",
                "taskmgr" or "system" or "performance" => "\uE9D9",
                "camera" or "screenshot" => "\uE722",
                "record" or "video" => "\uE714",
                "music" or "spotify" => "\uE8D6",
                "browser" or "web" or "chrome" => "\uE774",
                "chat" or "discord" => "\uE8BD",
                "folder" or "files" => "\uE8B7",
                "terminal" or "cmd" => "\uE756",
                "settings" => "\uE713",
                "power" => "\uE7E8",
                "lock" or "cursorlock" or "mouse" => "\uE72E",
                _ => "\uE71D"
            };
        }

        private ImageSource? TryLoadCustomImage(string iconSetting, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(iconSetting)) return null;

            string fullPath = System.IO.Path.IsPathRooted(iconSetting)
                ? iconSetting
                : System.IO.Path.Combine(baseDir, iconSetting);

            if (!System.IO.File.Exists(fullPath)) return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void LoadDeckShortcuts()
        {
            string exeDir = AppContext.BaseDirectory;
            string configPath = System.IO.Path.Combine(exeDir, "shortcuts.json");

            List<DeckShortcutConfig> configs;

            if (!System.IO.File.Exists(configPath))
            {
                configs = new List<DeckShortcutConfig>
                {
                    new() { Icon = "Gamepad", Title = "STEAM DECK", Subtitle = "BIG PICTURE", Color = "#38BDF8", ActionType = "App", Target = "steam://open/bigpicture" },
                    new() { Icon = "PlayPause", Title = "PLAY / PAUSE", Subtitle = "MEDIA CONTROL", Color = "#10B981", ActionType = "Key", Target = "PlayPause" },
                    new() { Icon = "CursorLock", Title = "CURSOR LOCK", Subtitle = "TOGGLE MONITOR", Color = "#F59E0B", ActionType = "CursorLock", Target = "" },
                    new() { Icon = "Mute", Title = "MUTE AUDIO", Subtitle = "TOGGLE SOUND", Color = "#F59E0B", ActionType = "Key", Target = "Mute" },
                    new() { Icon = "TaskMgr", Title = "TASK MGR", Subtitle = "PROCESSES", Color = "#C084FC", ActionType = "App", Target = "taskmgr.exe" },
                    new() { Icon = "Record", Title = "RECORD", Subtitle = "WIN + ALT + R", Color = "#EF4444", ActionType = "Record", Target = "Win+Alt+R" }
                };

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                System.IO.File.WriteAllText(configPath, JsonSerializer.Serialize(configs, jsonOptions));
            }
            else
            {
                try
                {
                    string json = System.IO.File.ReadAllText(configPath);
                    configs = JsonSerializer.Deserialize<List<DeckShortcutConfig>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<DeckShortcutConfig>();
                }
                catch
                {
                    configs = new List<DeckShortcutConfig>();
                }
            }

            var items = new List<DeckTileViewModel>();
            foreach (var c in configs)
            {
                Brush brush;
                try
                {
                    brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Color));
                }
                catch
                {
                    brush = new SolidColorBrush(CpuCoolColor);
                }

                ImageSource? customImg = TryLoadCustomImage(c.Icon, exeDir);
                bool hasCustomImg = customImg != null;

                items.Add(new DeckTileViewModel
                {
                    IconImage = customImg,
                    ImageVisibility = hasCustomImg ? Visibility.Visible : Visibility.Collapsed,
                    IconGlyph = hasCustomImg ? "" : ResolveIconGlyph(c.Icon),
                    GlyphVisibility = hasCustomImg ? Visibility.Collapsed : Visibility.Visible,
                    Title = c.Title.ToUpper(),
                    Subtitle = c.Subtitle.ToUpper(),
                    AccentBrush = brush,
                    ActionType = c.ActionType,
                    Target = c.Target,
                    Arguments = c.Arguments
                });
            }

            DeckItemsControl.ItemsSource = items;
        }

        private void SendKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void SendMultiKeyCombo(string combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;

            var parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var vKeys = new List<byte>();

            foreach (var part in parts)
            {
                byte vk = part.ToLowerInvariant() switch
                {
                    "win" or "lwin" => VK_LWIN,
                    "ctrl" or "control" => VK_CONTROL,
                    "alt" => VK_MENU,
                    "shift" => VK_SHIFT,
                    "esc" or "escape" => 0x1B,
                    "tab" => 0x09,
                    "enter" or "return" => 0x0D,
                    "space" => 0x20,
                    "prtsc" or "printscreen" or "snapshot" => VK_SNAPSHOT,
                    "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
                    "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
                    "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
                    { Length: 1 } s when char.IsLetterOrDigit(s[0]) => (byte)char.ToUpperInvariant(s[0]),
                    _ => 0
                };

                if (vk != 0) vKeys.Add(vk);
            }

            if (vKeys.Count == 0) return;

            foreach (byte vk in vKeys)
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
            }

            Thread.Sleep(30);

            for (int i = vKeys.Count - 1; i >= 0; i--)
            {
                keybd_event(vKeys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }

        private void DeckTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not DeckTileViewModel item) return;

            switch (item.ActionType.Trim().ToLowerInvariant())
            {
                case "cursorlock":
                    ToggleCursorLock();
                    break;

                case "key":
                    switch (item.Target.Trim().ToLowerInvariant())
                    {
                        case "playpause": SendKey(VK_MEDIA_PLAY_PAUSE); break;
                        case "nexttrack": SendKey(VK_MEDIA_NEXT_TRACK); break;
                        case "prevtrack": SendKey(0xB1); break;
                        case "mute": SendKey(VK_VOLUME_MUTE); break;
                        case "volup": SendKey(0xAF); break;
                        case "voldown": SendKey(0xAE); break;
                    }
                    break;

                case "hotkey":
                    FocusPrimaryMonitor();
                    SendMultiKeyCombo(item.Target);
                    break;

                case "record":
                    FocusPrimaryMonitor();
                    string recordCombo = string.IsNullOrWhiteSpace(item.Target) ? "Win+Alt+R" : item.Target;
                    SendMultiKeyCombo(recordCombo);
                    break;

                case "screenshot":
                    SendMultiKeyCombo("Win+PrintScreen");
                    break;

                case "app":
                default:
                    if (!string.IsNullOrWhiteSpace(item.Target))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = item.Target,
                                Arguments = item.Arguments ?? "",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                    break;
            }
        }

        // ==================== TELEMETRY & GAUGE LOGIC ====================
        private void InitCoreBars()
        {
            int coreCount = Environment.ProcessorCount;
            var initialBrush = new SolidColorBrush(CpuCoolColor);

            for (int i = 0; i < coreCount; i++)
            {
                var pc = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
                pc.NextValue();
                _coreCounters.Add(pc);

                var pb = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, i == coreCount - 1 ? 0 : 4, 0),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E24")),
                    Foreground = initialBrush,
                    BorderThickness = new Thickness(0),
                    Orientation = Orientation.Vertical
                };
                _coreBars.Add(pb);
                CoreBarsGrid.Children.Add(pb);
            }
        }

        private async Task UpdateMetricsAsync()
        {
            // Re-assert primary monitor cursor lock (Windows clears ClipCursor on Alt+Tab / Win key / UAC)
            if (_isCursorLocked && _hasPrimaryBounds)
            {
                ClipCursor(ref _primaryMonitorBounds);
            }

            float cpu = _cpuCounter.NextValue();
            CpuText.Text = $"{Math.Round(cpu)}%";
            UpdateArcGeometry(CpuArcPath, cpu);

            for (int i = 0; i < _coreCounters.Count; i++)
            {
                _coreBars[i].Value = _coreCounters[i].NextValue();
            }

            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
                double availGb = memStatus.ullAvailPhys / (1024.0 * 1024 * 1024);
                double usedGb = totalGb - availGb;

                RamText.Text = $"{memStatus.dwMemoryLoad}%";
                RamBar.Value = memStatus.dwMemoryLoad;
                RamDetailText.Text = $"{usedGb:F1} / {totalGb:F1} GB";
                RamAvailText.Text = $"{availGb:F1} GB AVAILABLE";
            }

            var now = DateTime.Now;
            if (_diskCounter != null)
            {
                float disk = Math.Min(100, _diskCounter.NextValue());
                DiskText.Text = $"{Math.Round(disk)}%";
                DiskBar.Value = disk;
                DiskStatusText.Text = disk > 65 ? "HEAVY I/O" : (disk > 10 ? "ACTIVE" : "IDLE");
            }
            ClockText.Text = now.ToString("hh:mm tt");
            DateText.Text = now.ToString("dddd, MMM dd").ToUpper();

            int cpuTemp = GetCoreTempCelsius();
            CpuTempText.Text = cpuTemp > 0 ? $"{cpuTemp}°C" : "N/A";
            ApplyCpuThermalColor(cpuTemp);

            var gpu = await Task.Run(() => GetNvidiaStats());
            GpuText.Text = $"{gpu.load}%";
            UpdateArcGeometry(GpuArcPath, gpu.load);
            GpuTempText.Text = $"{gpu.temp}°C";
            ApplyGpuThermalColor(gpu.temp);

            if (gpu.vramTotal > 0)
            {
                VramText.Text = $"VRAM {gpu.vramUsed} / {gpu.vramTotal} MB";
                VramBar.Value = (gpu.vramUsed * 100.0) / gpu.vramTotal;
            }
            if (gpu.clockMhz > 0)
            {
                GpuClockText.Text = $"{gpu.clockMhz} MHz";
            }
        }

        private int GetCoreTempCelsius()
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting("CoreTempMappingObject", MemoryMappedFileRights.Read);
                using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(CORE_TEMP_SHARED_DATA)), MemoryMappedFileAccess.Read);

                int size = Marshal.SizeOf(typeof(CORE_TEMP_SHARED_DATA));
                byte[] buffer = new byte[size];
                accessor.ReadArray(0, buffer, 0, size);

                GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var data = Marshal.PtrToStructure<CORE_TEMP_SHARED_DATA>(handle.AddrOfPinnedObject());
                    int count = (int)Math.Min(data.uiCoreCnt, 256);
                    if (count > 0 && data.fTemp != null)
                    {
                        float maxTemp = 0;
                        for (int i = 0; i < count; i++)
                        {
                            if (data.fTemp[i] > maxTemp) maxTemp = data.fTemp[i];
                        }
                        if (data.ucFahrenheit != 0)
                        {
                            maxTemp = (maxTemp - 32f) * 5f / 9f;
                        }
                        return (int)Math.Round(maxTemp);
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
            catch { }
            return 0;
        }

        private Color GetThermalColor(int tempCelsius, Color coolFallback)
        {
            if (tempCelsius >= 80) return HotColor;
            if (tempCelsius >= 65) return WarmColor;
            return coolFallback;
        }

        private void ApplyCpuThermalColor(int temp)
        {
            Color activeColor = GetThermalColor(temp, CpuCoolColor);
            var brush = new SolidColorBrush(activeColor);

            CpuArcPath.Stroke = brush;
            CpuTempText.Foreground = brush;
            if (CpuArcPath.Effect is DropShadowEffect shadow)
            {
                shadow.Color = activeColor;
            }
            foreach (var bar in _coreBars)
            {
                bar.Foreground = brush;
            }
        }

        private void ApplyGpuThermalColor(int temp)
        {
            Color activeColor = GetThermalColor(temp, GpuCoolColor);
            var brush = new SolidColorBrush(activeColor);

            GpuArcPath.Stroke = brush;
            GpuTempText.Foreground = brush;
            VramBar.Foreground = brush;
            if (GpuArcPath.Effect is DropShadowEffect shadow)
            {
                shadow.Color = activeColor;
            }
        }

        private void UpdateArcGeometry(Path arcPath, double percentage)
        {
            double clamped = Math.Max(0.5, Math.Min(100.0, percentage));
            double startAngle = 135.0;
            double sweepAngle = (clamped / 100.0) * 270.0;
            double endAngle = startAngle + sweepAngle;

            double cx = 105, cy = 105, radius = 92;
            Point startPt = PolarToCartesian(cx, cy, radius, startAngle);
            Point endPt = PolarToCartesian(cx, cy, radius, endAngle);

            bool isLargeArc = sweepAngle > 180.0;
            var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = endPt,
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            arcPath.Data = geo;
        }

        private Point PolarToCartesian(double cx, double cy, double r, double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private (int load, int temp, int vramUsed, int vramTotal, int clockMhz) GetNvidiaStats()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total,clocks.gr --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (0, 0, 0, 0, 0);
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();

                var p = output.Split(',');
                if (p.Length >= 5)
                {
                    int.TryParse(p[0].Trim(), out int load);
                    int.TryParse(p[1].Trim(), out int temp);
                    int.TryParse(p[2].Trim(), out int vUsed);
                    int.TryParse(p[3].Trim(), out int vTotal);
                    int.TryParse(p[4].Trim(), out int clock);
                    return (load, temp, vUsed, vTotal, clock);
                }
            }
            catch { }
            return (0, 0, 0, 0, 0);
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            WindowState = WindowState.Maximized == WindowState ? WindowState.Normal : WindowState.Maximized;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Right && _currentPage == 0) SwitchPage(1);
            else if (e.Key == Key.Left && _currentPage == 1) SwitchPage(0);
        }
    }
}