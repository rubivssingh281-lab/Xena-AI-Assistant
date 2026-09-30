using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Xena.Desktop;

public partial class MainWindow : Window
{
    private enum Link { Connecting, Online, Offline }

    private readonly MainSettings _settings = MainSettings.Load();
    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _pingTimer;
    private readonly DateTime _sessionStart = DateTime.Now;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _haveSysTimes;
    private double _cpu, _mem;
    private int _pulseCount;
    private PythonBridge? _bridge;
    private Link _link = Link.Connecting;
    private int _restarts;
    private bool _closing;
    private AudioMonitor? _audio;
    private double _coreLevel;
    private int _frame;
    private const int BandCount = 8;
    private readonly float[] _bandBuf = new float[BandCount];
    private readonly double[] _bandSmooth = new double[BandCount];
    private bool _listening;
    private readonly System.Net.Http.HttpClient _camHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _camCts;
    private readonly List<CancellationTokenSource> _running = new();   // replies being written right now
    private int _historyIndex = -1;
    private string _draft = "";
    private string _filter = "all";
    private bool _followLog = true;
    private CodeAgentWindow? _codeAgent;
    private readonly System.Collections.ObjectModel.ObservableCollection<object> _entries = new();   // every card, whatever the filter

    public MainWindow()
    {
        InitializeComponent();
        ConversationLog.ItemsSource = _entries;
        _settings.Window?.ApplyTo(this);
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "\u2750" : "\u25A1";

        HostText.Text = "HOST: " + Environment.MachineName.ToUpperInvariant();
        ArsenalCount.Text = $"{ArsenalGrid.Children.Count + 1} READY";
        ApplyVoiceUi();
        SetFilter("all");
        AddWelcome();

        // Voice-reactive core: capture the mic and drive the visuals each frame.
        _audio = new AudioMonitor(BandCount);
        CompositionTarget.Rendering += OnRendering;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _pulseTimer.Tick += (_, _) => UpdatePulse();
        _pulseTimer.Start();

        _pingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pingTimer.Tick += async (_, _) => await PingAsync();

        Loaded += async (_, _) =>
        {
            UpdatePulse();
            CommandInput.Focus();
            await ConnectBridgeAsync(restart: false);
        };
    }

    // ---------------------------------------------------------------------
    // Window chrome (borderless frame): drag, caption buttons, correct maximize
    // ---------------------------------------------------------------------
    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref info);
            var work = info.rcWork;
            var mon = info.rcMonitor;
            mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
            mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
            mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
            mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // ---------------------------------------------------------------------
    // Backend connection: start, watch (ping), restart automatically if it dies
    // ---------------------------------------------------------------------
    private async Task ConnectBridgeAsync(bool restart)
    {
        SetLink(Link.Connecting);
        BackendText.Text = "PYTHON // STARTING";
        try
        {
            var bridge = await Task.Run(() => new PythonBridge());
            if (_closing) { bridge.Dispose(); return; }
            bridge.Disconnected += () => Dispatcher.BeginInvoke(() => OnBridgeLost(bridge));
            _bridge = bridge;
            BackendText.Text = $"PYTHON // PID {bridge.ProcessId}";
            SetLink(Link.Online);
            if (!_settings.VoiceOn)
                _ = bridge.SendAsync("set_voice", new Dictionary<string, string> { ["on"] = "false" });
            AddSystem(restart ? "Backend restarted. Neural link re-established." : "Neural link established. Xena is ready.");
            _pingTimer.Start();
            await PingAsync();
            if (!restart) await SendActionAsync("system_info");
            await StartCameraMonitorAsync();
        }
        catch (Exception ex)
        {
            SetLink(Link.Offline);
            BackendText.Text = "PYTHON // NOT RUNNING";
            AddError("Couldn't start Xena's backend: " + ex.Message +
                     "  Click OFFLINE at the top right to try again.");
        }
    }

    private void OnBridgeLost(PythonBridge bridge)
    {
        if (_closing || _bridge != bridge) return;
        _bridge = null;
        _pingTimer.Stop();
        _camCts?.Cancel();
        _camCts = null;
        ApplyCameraOffline();
        var why = bridge.LastErrors.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        bridge.Dispose();
        SetLink(Link.Offline);
        BackendText.Text = "PYTHON // STOPPED";
        if (_restarts < 3)
        {
            _restarts++;
            AddError("Xena's backend stopped unexpectedly" + (why.Length > 0 ? $" ({why})" : "") + ". Restarting it…");
            _ = ConnectBridgeAsync(restart: true);
        }
        else
        {
            AddError("Xena's backend keeps stopping" + (why.Length > 0 ? $" ({why})" : "") +
                     ". Click OFFLINE at the top right to start it again.");
        }
    }

    private async Task PingAsync()
    {
        var bridge = _bridge;
        if (bridge == null) return;
        try
        {
            var ms = await bridge.PingAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (_bridge == bridge) LinkText.Text = $"  NEURAL LINK ESTABLISHED  |  LATENCY: {ms:0} ms";
        }
        catch (TimeoutException)
        {
            if (_bridge == bridge) LinkText.Text = "  NEURAL LINK BUSY  |  BACKEND NOT RESPONDING";
        }
        catch { /* a lost connection is reported by OnBridgeLost */ }
    }

    private void SetLink(Link link)
    {
        _link = link;
        switch (link)
        {
            case Link.Connecting:
                LinkDot.Foreground = Br("OrangeBrush");
                LinkText.Text = "  ESTABLISHING NEURAL LINK…";
                ClusterText.Text = "SYNAPSE CLUSTER STARTING";
                break;
            case Link.Online:
                LinkDot.Foreground = Br("GreenBrush");
                LinkText.Text = "  NEURAL LINK ESTABLISHED";
                ClusterText.Text = "SYNAPSE CLUSTER ONLINE";
                break;
            case Link.Offline:
                LinkDot.Foreground = Br("RedBrush");
                LinkText.Text = "  NEURAL LINK LOST";
                ClusterText.Text = "SYNAPSE CLUSTER OFFLINE";
                break;
        }
        UpdateStatePill();
        UpdateHealth();
    }

    private void UpdateStatePill()
    {
        var (text, brush, back, tip) = _link switch
        {
            Link.Offline => ("\u25CF OFFLINE", Br("RedBrush"), Hex("#2A0E0E"), "Xena's backend isn't running. Click to start it."),
            Link.Connecting => ("\u25CF STARTING", Br("OrangeBrush"), Hex("#331A10"), "Xena is starting up."),
            _ when _running.Count > 0 => ("\u25CF THINKING", Br("OrangeBrush"), Hex("#331A10"), "Xena is writing a reply. Press Esc to stop."),
            _ when _listening => ("\u25CF LISTENING", Br("CyanBrush"), Hex("#102535"), "Xena is listening to your microphone."),
            _ => ("\u25CF READY", Br("GreenBrush"), Hex("#0A2015"), "Xena is ready."),
        };
        StateText.Text = text;
        StateText.Foreground = brush;
        StatePill.BorderBrush = brush;
        StatePill.Background = back;
        StatePill.ToolTip = tip;
        StatePill.Cursor = _link == Link.Offline ? Cursors.Hand : null;
    }

    // MouseDown (not Up): the header behind it starts a window drag on mouse down.
    private void StatePill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_link != Link.Offline) return;
        e.Handled = true;
        _restarts = 0;
        _ = ConnectBridgeAsync(restart: true);
    }

    // ---------------------------------------------------------------------
    // Live telemetry
    // ---------------------------------------------------------------------
    private void UpdatePulse()
    {
        var elapsed = DateTime.Now - _sessionStart;
        SessionText.Text = $"SESSION: {elapsed:hh\\:mm\\:ss}";

        // Real CPU load (system-wide) via GetSystemTimes deltas.
        double cpu = ReadCpuLoad();
        if (cpu >= 0)
        {
            _cpu = cpu;
            CpuText.Text = $"{cpu:0.0}%";
            CpuBar.Value = cpu;

            // Thermal estimate derived from CPU load (a real temp sensor needs
            // WMI/admin and is often unavailable; this tracks load plausibly).
            double temp = 42 + cpu * 0.42;
            ThermalText.Text = $"{temp:0.0} °C";
        }

        // Real physical memory usage.
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            _mem = mem.dwMemoryLoad;
            MemText.Text = $"{_mem:0.0}%";
            MemBar.Value = _mem;
        }

        if (_pulseCount++ % 12 == 0) UpdateDiskAndPower();   // every ~10 s
        UpdateHealth();
    }

    private void UpdateDiskAndPower()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var used = 100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize;
            DiskLabel.Text = $"Subroutine Storage ({root.TrimEnd('\\')})";
            DiskText.Text = $"{used:0.0}%";
            DiskBar.Value = used;
            DiskBar.ToolTip = $"{drive.TotalFreeSpace / 1e9:0.0} GB free of {drive.TotalSize / 1e9:0} GB";
        }
        catch { DiskText.Text = "—"; }

        if (GetSystemPowerStatus(out var power))
        {
            if ((power.BatteryFlag & 128) != 0 || power.BatteryLifePercent > 100)
                PowerText.Text = "AC";
            else
                PowerText.Text = $"{power.BatteryLifePercent}%" + (power.ACLineStatus == 1 ? " \u26A1" : "");
        }
    }

    private void UpdateHealth()
    {
        var (text, brush, back) = _link == Link.Offline
            ? ("OFFLINE", Br("RedBrush"), Hex("#2A0E0E"))
            : _cpu > 90 || _mem > 92
                ? ("HIGH LOAD", Br("OrangeBrush"), Hex("#331A10"))
                : ("HEALTHY", Br("GreenBrush"), Hex("#0A2015"));
        HealthText.Text = text;
        HealthText.Foreground = brush;
        HealthPill.BorderBrush = brush;
        HealthPill.Background = back;
    }

    private double ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return -1;
        long i = ToLong(idle), k = ToLong(kernel), u = ToLong(user);
        if (!_haveSysTimes)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u; _haveSysTimes = true;
            return -1; // need a baseline first
        }
        long di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        long total = dk + du; // kernel time already includes idle
        if (total <= 0) return -1;
        return Math.Clamp((double)(total - di) / total * 100.0, 0, 100);
    }

    private static long ToLong(FT ft) => ((long)ft.High << 32) | ft.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FT idleTime, out FT kernelTime, out FT userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FT { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
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

    // ---------------------------------------------------------------------
    // Per-frame (60Hz) voice-reactive rendering
    // ---------------------------------------------------------------------
    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalSeconds;
        bool live = _audio is { IsRunning: true };
        double target = live ? _audio!.Level : 0.0;

        // Fast attack, slow decay for a natural "pulse to the voice" feel.
        double rate = target > _coreLevel ? 0.35 : 0.08;
        _coreLevel += (target - _coreLevel) * rate;
        if (_coreLevel < 0.0005) _coreLevel = 0;

        double idleBreath = 0.06 * (0.5 + 0.5 * Math.Sin(t * 1.6));
        double scale = 1.0 + idleBreath + _coreLevel * 0.6;

        SetScale(CoreDot, scale);
        SetScale(CoreHot, scale);
        CoreHot.Opacity = Math.Clamp(_coreLevel * 0.9, 0, 0.9);
        CoreGlow.BlurRadius = 14 + idleBreath * 60 + _coreLevel * 46;

        SetScale(VoiceHalo, 1.0 + _coreLevel * 2.6);
        VoiceHalo.Opacity = Math.Clamp(_coreLevel * 0.7, 0, 0.7);

        RingInner.Opacity = 0.55 + _coreLevel * 0.45;
        RingMid.Opacity = 0.40 + _coreLevel * 0.50;

        UpdateSpectrum(live, t);

        if (_frame++ % 8 == 0)   // readable numbers, a few times a second
        {
            var pct = Math.Clamp(_coreLevel * 100, 0, 100);
            MicText.Text = live ? $"MIC {pct:0}%" : "MIC OFF";
            ResonanceText.Text = live ? $"CORE RESONANCE {pct:0}%" : "CORE RESONANCE STANDBY";
        }
    }

    private static void SetScale(UIElement element, double s)
    {
        if (element.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = s;
            st.ScaleY = s;
        }
    }

    private void UpdateSpectrum(bool live, double t)
    {
        int n = SpectrumBars.Children.Count;
        if (live && _audio != null)
        {
            _audio.CopyBands(_bandBuf);
            for (int i = 0; i < n && i < _bandBuf.Length; i++)
            {
                double tgt = _bandBuf[i];
                _bandSmooth[i] += (tgt - _bandSmooth[i]) * (tgt > _bandSmooth[i] ? 0.5 : 0.12);
                if (SpectrumBars.Children[i] is System.Windows.Shapes.Rectangle bar)
                    bar.Height = 2 + _bandSmooth[i] * 13;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                double h = 3 + 5 * (0.5 + 0.5 * Math.Sin(t * 3 + i * 0.7));
                if (SpectrumBars.Children[i] is System.Windows.Shapes.Rectangle bar)
                    bar.Height = h;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Conversation stream
    // ---------------------------------------------------------------------
    private Brush Br(string key) => (Brush)FindResource(key);

    private static Brush Hex(string hex) =>
        (Brush)(new BrushConverter().ConvertFromString(hex) ?? Brushes.Gray);

    private void Add(object entry)
    {
        _entries.Add(entry);
        if (_entries.Count > 400)
        {
            _entries.RemoveAt(0);
        }
        UpdateMessageCount();
    }

    private string Now() => DateTime.Now.ToString("HH:mm:ss");

    private void AddSystem(string body) => Add(new AgentEntry
    {
        Glyph = "\uE946", // Info
        Title = "SYSTEM",
        Timestamp = Now(),
        Body = body,
        Accent = Br("CyanBrush"),
        AvatarBg = Hex("#0C1E2A"),
        Kind = EntryKind.System,
    });

    private void AddError(string body) => Add(new AgentEntry
    {
        Glyph = "\uE783", // Error
        Title = "SYSTEM ERROR",
        Timestamp = Now(),
        Body = body,
        Accent = Br("RedBrush"),
        AvatarBg = Hex("#2A0E0E"),
        Kind = EntryKind.Error,
    });

    private AgentEntry AddXena(string body)
    {
        var entry = new AgentEntry
        {
            Glyph = "\uE99A", // Robot
            Title = "XENA CORE BROADCAST",
            Timestamp = Now(),
            Body = body,
            Accent = Br("OrangeBrush"),
            AvatarBg = Hex("#2A1608"),
        };
        Add(entry);
        return entry;
    }

    private void AddWelcome()
    {
        var cyan = Br("CyanBrush");
        var back = Hex("#102535");
        Add(new AgentEntry
        {
            Glyph = "\uE99A",
            Title = "XENA CORE BROADCAST",
            Timestamp = Now(),
            Body = "Hello. Type below and press Enter, or press ENGAGE LISTENING to talk.\n" +
                   "Try \"open chrome\", \"what's the time\", \"take a screenshot\", \"search for today's news\", " +
                   "or ask me anything. For code, open the CODE AGENT.",
            Accent = Br("OrangeBrush"),
            AvatarBg = Hex("#2A1608"),
            Pills =
            {
                new Pill { Text = "CTRL+K  TYPE", Brush = cyan, Background = back },
                new Pill { Text = "ESC  STOP", Brush = cyan, Background = back },
                new Pill { Text = "CTRL+E  CODE AGENT", Brush = cyan, Background = back },
                new Pill { Text = "CTRL+M  VOICE", Brush = cyan, Background = back },
            },
        });
    }

    // Replies are shown as plain text: drop markdown emphasis/code markers the
    // model may still emit (done on the whole text, so split "**" tokens are safe).
    private static string CleanDisplay(string text) =>
        text.Replace("**", "").Replace("__", "").Replace("`", "").TrimStart();

    private void AddUser(string body)
    {
        Add(new UserDirectiveEntry
        {
            Title = "USER DIRECTIVE",
            Timestamp = Now(),
            Body = body,
        });
        ScrollToLatest();   // your own message always brings you to the bottom
    }

    private void UpdateMessageCount()
    {
        var count = _entries
            .Count(e => e is UserDirectiveEntry || e is AgentEntry { Kind: EntryKind.Xena });
        MessageCountText.Text = count == 1 ? "1 MESSAGE" : $"{count} MESSAGES";
    }

    // Auto-follow new text only while you're at the bottom; if you scrolled up to
    // read, stay put and offer a "new messages" button instead.
    private void LogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            _followLog = LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 48;
            if (_followLog) JumpButton.Visibility = Visibility.Collapsed;
        }
        else if (_followLog)
        {
            LogScroll.ScrollToEnd();
        }
        else if (e.ExtentHeightChange > 0)
        {
            JumpButton.Visibility = Visibility.Visible;
        }
    }

    // The selectable message boxes would otherwise swallow the mouse wheel.
    private void LogScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        LogScroll.ScrollToVerticalOffset(LogScroll.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private void JumpButton_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    private void ScrollToLatest()
    {
        _followLog = true;
        JumpButton.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filter }) SetFilter(filter);
    }

    private void SetFilter(string filter)
    {
        _filter = filter;
        ConversationLog.Items.Filter = filter switch
        {
            "chat" => e => e is UserDirectiveEntry || e is AgentEntry { Kind: EntryKind.Xena },
            "system" => e => e is AgentEntry { Kind: EntryKind.System or EntryKind.Error } || e is ProtocolEntry || e is ToolEntry,
            _ => null,
        };
        foreach (var tab in new[] { FilterAll, FilterChat, FilterSystem })
        {
            var selected = (string)tab.Tag == filter;
            tab.Foreground = selected ? Br("OrangeBrush") : Br("MutedBrush");
            tab.BorderBrush = selected ? Br("OrangeBrush") : Br("LineBrush");
            tab.Background = selected ? Hex("#331A10") : Br("PanelAltBrush");
            tab.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
        }
        ScrollToLatest();
    }

    private static string EntryText(object entry) => entry switch
    {
        UserDirectiveEntry u => $"[{u.Timestamp}] YOU: {u.Body}",
        AgentEntry a => $"[{a.Timestamp}] {(a.Kind == EntryKind.Xena ? "XENA" : a.Title)}: {a.Body}",
        ProtocolEntry p => p.Header,
        ToolEntry t => t.Title,
        _ => "",
    };

    private string ConversationText() =>
        string.Join(Environment.NewLine + Environment.NewLine,
                    _entries.Select(EntryText).Where(s => s.Length > 0));

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Parent is ContextMenu { PlacementTarget: TextBox box })
            Clipboard.SetText(box.Text);
    }

    private void CopyConversation_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(ConversationText());

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save conversation",
            FileName = $"Xena conversation {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text file (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ConversationText(), new UTF8Encoding(false));
            AddSystem("Conversation saved to " + dialog.FileName);
        }
        catch (Exception ex)
        {
            AddError("Couldn't save the conversation: " + ex.Message);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearConversation();

    private void ClearConversation()
    {
        _entries.Clear();
        UpdateMessageCount();
        AddSystem("Conversation cleared.");
    }

    // ---------------------------------------------------------------------
    // Interaction
    // ---------------------------------------------------------------------
    private async void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        _listening = !_listening;
        ListenLabel.Text = _listening ? "STOP LISTENING" : "ENGAGE LISTENING";
        ListenButton.Background = _listening ? new SolidColorBrush(Color.FromRgb(190, 42, 42)) : Br("OrangeBrush");
        UpdateStatePill();

        if (_listening)
        {
            _audio?.Start();
            if (_audio is { IsRunning: false })
                AddError("Microphone unavailable: " + (_audio.LastError ?? "no capture device found."));
            else
                AddSystem("Listening — speak to Xena. Press STOP LISTENING when you're done.");
        }
        else
        {
            _audio?.Stop();
            AddSystem("Listening stopped.");
        }

        await SendActionAsync("toggle_listening", announce: false);
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e) => ToggleVoice();

    private void ToggleVoice()
    {
        _settings.VoiceOn = !_settings.VoiceOn;
        _settings.Save();
        ApplyVoiceUi();
        _ = _bridge?.SendAsync("set_voice", new Dictionary<string, string> { ["on"] = _settings.VoiceOn ? "true" : "false" });
    }

    private void ApplyVoiceUi()
    {
        var on = _settings.VoiceOn;
        VoiceGlyph.Text = on ? "\uE767" : "\uE74F";
        VoiceLabel.Text = on ? " VOICE ON" : " VOICE OFF";
        var brush = on ? Br("CyanBrush") : Br("MutedBrush");
        VoiceGlyph.Foreground = brush;
        VoiceLabel.Foreground = brush;
    }

    private void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running.Count > 0 && CommandInput.Text.Trim().Length == 0) StopReplies();
        else ExecuteCommand();
    }

    private void CommandInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ExecuteCommand();
                e.Handled = true;
                break;
            case Key.Up when _settings.History.Count > 0:
                if (_historyIndex == -1) { _draft = CommandInput.Text; _historyIndex = _settings.History.Count; }
                _historyIndex = Math.Max(0, _historyIndex - 1);
                ShowInput(_settings.History[_historyIndex]);
                e.Handled = true;
                break;
            case Key.Down when _historyIndex != -1:
                _historyIndex++;
                if (_historyIndex >= _settings.History.Count) { _historyIndex = -1; ShowInput(_draft); }
                else ShowInput(_settings.History[_historyIndex]);
                e.Handled = true;
                break;
            case Key.Escape:
                if (CommandInput.Text.Length > 0) { CommandInput.Clear(); _historyIndex = -1; }
                else StopReplies();
                e.Handled = true;
                break;
        }
    }

    private void ShowInput(string text)
    {
        CommandInput.Text = text;
        CommandInput.CaretIndex = text.Length;
    }

    private void CommandInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(CommandInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateExecuteButton();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.K) { CommandInput.Focus(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { ClearConversation(); e.Handled = true; }
        else if (ctrl && e.Key == Key.E) { OpenCodeAgent(); e.Handled = true; }
        else if (ctrl && e.Key == Key.M) { ToggleVoice(); e.Handled = true; }
        else if (e.Key == Key.Escape && !CommandInput.IsKeyboardFocused) { StopReplies(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private void RememberCommand(string command)
    {
        _historyIndex = -1;
        _settings.History.Remove(command);
        _settings.History.Add(command);
        if (_settings.History.Count > 100) _settings.History.RemoveAt(0);
        _settings.Save();
    }

    /// <summary>Stop every reply being written, and stop talking.</summary>
    private void StopReplies()
    {
        foreach (var cts in _running.ToList()) cts.Cancel();
        _ = _bridge?.SendAsync("stop_speaking");
    }

    private void UpdateExecuteButton()
    {
        var stop = _running.Count > 0 && CommandInput.Text.Trim().Length == 0;
        ExecuteLabel.Text = stop ? "STOP \u25A0" : "EXECUTE \u26A1";
        ExecuteButton.Background = stop ? new SolidColorBrush(Color.FromRgb(190, 42, 42)) : Br("OrangeBrush");
        ExecuteButton.ToolTip = stop ? "Stop Xena's reply (Esc)" : "Send (Enter)";
    }

    private async void ExecuteCommand()
    {
        var command = CommandInput.Text.Trim();
        if (command.Length == 0) return;
        RememberCommand(command);
        AddUser(command);
        CommandInput.Clear();

        var bridge = _bridge;
        if (bridge == null)
        {
            AddError(_link == Link.Connecting
                ? "Xena is still starting up. Give it a few seconds and send that again (press ↑ to bring it back)."
                : "Xena's backend isn't running. Click OFFLINE at the top right to start it.");
            return;
        }

        var cts = new CancellationTokenSource();
        var waiting = _running.Count > 0;   // another reply is still being written: this one queues
        _running.Add(cts);
        UpdateStatePill();
        UpdateExecuteButton();

        // Xena's reply streams into a single card, word by word, as the model writes it.
        AgentEntry? live = null;
        var raw = new System.Text.StringBuilder();
        var streaming = false;
        var finished = false;

        // If nothing has arrived after a moment (model loading/thinking), say so.
        _ = Task.Delay(450).ContinueWith(_ =>
        {
            if (!finished && live == null)
                live = AddXena(waiting ? "Waiting for the previous reply to finish…" : "Thinking…");
        }, TaskScheduler.FromCurrentSynchronizationContext());

        try
        {
            var result = await bridge.SendAsync(
                "command",
                new Dictionary<string, string> { ["text"] = command },
                partial =>
                {
                    if (cts.IsCancellationRequested) return;
                    // {"status": "..."}: what she's doing right now (e.g. searching the
                    // web). It replaces the card text; the answer then streams in fresh.
                    if (partial.ValueKind == JsonValueKind.Object)
                    {
                        if (partial.TryGetProperty("status", out var st) && st.GetString() is { Length: > 0 } status)
                        {
                            live ??= AddXena("");
                            raw.Clear();
                            live.Body = status;
                        }
                        return;
                    }
                    if (partial.ValueKind != JsonValueKind.String) return;
                    live ??= AddXena("");
                    streaming = true;
                    raw.Append(partial.GetString());
                    live.Body = CleanDisplay(raw.ToString());
                },
                cts.Token);

            finished = true;
            if (cts.IsCancellationRequested)
            {
                var soFar = streaming ? CleanDisplay(raw.ToString()).TrimEnd() : "";
                if (live != null && soFar.Length > 0) live.Body = soFar + "  [stopped]";
                else
                {
                    if (live != null) _entries.Remove(live);
                    AddSystem("Stopped.");
                }
            }
            else if (live != null) live.Body = CleanDisplay(result);   // the complete, final text
            else AddXena(CleanDisplay(result));
        }
        catch (OperationCanceledException)
        {
            finished = true;
            if (live != null) _entries.Remove(live);
            AddSystem("Stopped before Xena started on it.");
        }
        catch (Exception exception)
        {
            finished = true;
            if (live != null && !streaming) _entries.Remove(live);
            AddError(exception.Message);
        }
        finally
        {
            _running.Remove(cts);
            UpdateStatePill();
            UpdateExecuteButton();
            UpdateMessageCount();
        }
    }

    private async void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || string.IsNullOrEmpty(action)) return;
        if (action.StartsWith("prefill:"))
        {
            // Actions that need a name or a value: start the sentence, you finish it.
            ShowInput(action["prefill:".Length..]);
            CommandInput.Focus();
            return;
        }
        await SendActionAsync(action);
    }

    private async void CameraButton_Click(object sender, RoutedEventArgs e)
    {
        // The label follows the camera's real state (see ApplyCameraStatus).
        CameraButton.IsEnabled = false;
        try { await SendActionAsync("toggle_camera"); }
        finally { CameraButton.IsEnabled = true; }
    }

    // ---------------------------------------------------------------------
    // Live camera preview. The backend serves annotated frames + recognition
    // status on a small local HTTP server that is separate from the command
    // channel, so the video keeps running while Xena is busy with a reply.
    // ---------------------------------------------------------------------
    private async Task StartCameraMonitorAsync()
    {
        if (_bridge == null || _camCts != null) return;
        int port;
        try { port = int.Parse(await _bridge.SendAsync("camera_port")); }
        catch (Exception ex)
        {
            AddError("Camera preview unavailable: " + ex.Message);
            return;
        }
        _camCts = new CancellationTokenSource();
        var token = _camCts.Token;
        _ = Task.Run(() => CameraMonitorLoopAsync($"http://127.0.0.1:{port}", token));
    }

    private async Task CameraMonitorLoopAsync(string baseUrl, CancellationToken token)
    {
        var lastStatus = DateTime.MinValue;
        var running = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if ((DateTime.UtcNow - lastStatus).TotalMilliseconds >= 400)
                {
                    lastStatus = DateTime.UtcNow;
                    var json = await _camHttp.GetStringAsync(baseUrl + "/status", token);
                    using var doc = JsonDocument.Parse(json);
                    var status = doc.RootElement.Clone();
                    running = status.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
                    await Dispatcher.InvokeAsync(() => ApplyCameraStatus(status, running));
                }
                if (running)
                {
                    using var resp = await _camHttp.GetAsync(baseUrl + "/frame.jpg", token);
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync(token);
                        var frame = new BitmapImage();
                        frame.BeginInit();
                        frame.CacheOption = BitmapCacheOption.OnLoad;
                        frame.StreamSource = new MemoryStream(bytes);
                        frame.EndInit();
                        frame.Freeze();   // decoded off the UI thread, safe to hand over
                        await Dispatcher.InvokeAsync(() => CameraImage.Source = frame);
                    }
                    await Task.Delay(66, token);   // ~15 fps preview
                }
                else
                {
                    await Task.Delay(400, token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void ApplyCameraOffline()
    {
        CameraImage.Visibility = Visibility.Collapsed;
        CameraIdle.Visibility = Visibility.Visible;
        CameraLabel.Text = "\U0001F4F7 START CAMERA";
        CameraImage.Source = null;
        CamFpsText.Text = "CAM_01 // OFFLINE";
        CamLockText.Text = "STANDBY";
        FaceConfText.Text = "—";
        FacesText.Text = "—";
        SensingText.Text = "CAMERA OFFLINE";
        SensingText.Foreground = Br("MutedBrush");
    }

    private void ApplyCameraStatus(JsonElement status, bool running)
    {
        if (status.TryGetProperty("people_known", out var known) && known.ValueKind == JsonValueKind.Number)
            KnownText.Text = known.GetInt32().ToString();
        if (!running)
        {
            ApplyCameraOffline();
            return;
        }
        CameraImage.Visibility = Visibility.Visible;
        CameraIdle.Visibility = Visibility.Collapsed;
        CameraLabel.Text = "⏹ STOP CAMERA";
        SensingText.Text = "SPECTRAL SENSING ACTIVE";
        SensingText.Foreground = Br("CyanBrush");

        var fps = status.TryGetProperty("fps", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : 0;
        CamFpsText.Text = $"CAM_01 // {fps:0} FPS";
        FacesText.Text = status.TryGetProperty("faces", out var faces) && faces.ValueKind == JsonValueKind.Array
            ? faces.GetArrayLength().ToString()
            : "0";
        if (status.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.True)
        {
            // Camera privacy key / shutter is on: the camera only sends a placeholder image.
            FaceConfText.Text = "CAMERA BLOCKED";
            CamLockText.Text = "PRIVACY MODE";
            FacesText.Text = "—";
            return;
        }
        if (status.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            switch (p.GetProperty("state").GetString())
            {
                case "known":
                    var name = p.GetProperty("name").GetString() ?? "";
                    FaceConfText.Text = $"{name} {p.GetProperty("confidence").GetDouble() * 100:0}%";
                    CamLockText.Text = "LOCK ACQUIRED";
                    break;
                case "unknown":
                    FaceConfText.Text = "UNKNOWN";
                    CamLockText.Text = "UNKNOWN FACE";
                    break;
                default:
                    FaceConfText.Text = "IDENTIFYING…";
                    CamLockText.Text = "SCANNING";
                    break;
            }
        }
        else
        {
            FaceConfText.Text = "NO FACE";
            CamLockText.Text = "SCANNING";
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e) => await SendActionAsync("analyze_face");

    // CALIBRATE = reload the face database (re-reads known_faces, e.g. after adding photos by hand).
    private async void CalibrateButton_Click(object sender, RoutedEventArgs e) =>
        await SendActionAsync("rebuild_faces");

    private void CodeAgentButton_Click(object sender, RoutedEventArgs e) => OpenCodeAgent();

    /// <summary>Opens the Code Agent, or brings the one that's already open to the front.</summary>
    private void OpenCodeAgent()
    {
        try
        {
            if (_codeAgent != null)
            {
                if (_codeAgent.WindowState == WindowState.Minimized) _codeAgent.WindowState = WindowState.Normal;
                _codeAgent.Activate();
                return;
            }
            _codeAgent = new CodeAgentWindow(() => _bridge);
            _codeAgent.Closed += (_, _) => _codeAgent = null;
            _codeAgent.Show();
            _codeAgent.Activate();
            AddSystem("Code Agent opened. Every edit Xena suggests needs your approval.");
        }
        catch (Exception ex)
        {
            _codeAgent = null;
            AddError("Code Agent failed to open: " + ex.Message);
        }
    }

    private async Task SendActionAsync(string action, Dictionary<string, string>? args = null, bool announce = true)
    {
        if (_bridge == null)
        {
            AddError(_link == Link.Connecting
                ? "Xena is still starting up. Try again in a few seconds."
                : "Xena's backend isn't running. Click OFFLINE at the top right to start it.");
            return;
        }
        try
        {
            var result = await _bridge.SendAsync(action, args);
            // Skip the generic "Action completed." echo when the caller already
            // reported the outcome itself (e.g. the listen toggle).
            if (announce || result != "Action completed.")
                AddXena(result);
        }
        catch (Exception exception)
        {
            AddError(exception.Message);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Unsaved code in the Code Agent: ask first, and allow cancelling the exit.
        if (_codeAgent != null && !_codeAgent.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        _closing = true;
        _settings.Window = WindowBounds.Capture(this);
        _settings.Save();
        _codeAgent?.Close();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _pingTimer.Stop();
        _pulseTimer.Stop();
        _audio?.Dispose();
        _camCts?.Cancel();
        _bridge?.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
