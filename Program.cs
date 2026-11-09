using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Web.WebView2.WinForms;
using Windows.Media;
using Windows.Media.Control;

namespace Janela;

static class Program
{
    internal static readonly string UserId = WindowsIdentity.GetCurrent().User!.Value;
    internal static readonly string PipeName = $"Janela.Video.{UserId}";
    internal static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Janela");

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Any(arg => arg.StartsWith("chrome-extension://", StringComparison.Ordinal)))
        {
            var app = Path.GetFileName(Environment.ProcessPath) switch
            {
                "Janela.chrome.exe" => (byte)1,
                "Janela.edge.exe" => (byte)2,
                _ => (byte)0
            };
            if (app != 0) MediaBridge.Forward(app).GetAwaiter().GetResult();
            return;
        }

        using var instance = new Mutex(true, "Local\\Janela.Tracker", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(DataPath);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        using var store = new History(Path.Combine(DataPath, "history.sqlite"));
        using var window = new Dashboard(store);
        if (args.Contains("--background")) window.Shown += (_, _) => window.Hide();
        Application.Run(window);
    }
}

sealed class Dashboard : Form
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly History history;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly CancellationTokenSource stop = new();
    readonly MediaBridge browser = new();
    readonly MediaProbe media = new();
    readonly Tracker tracker;
    bool exiting, locked, suspended, paused;
    bool closingSession;
    int seconds;
    string? pendingInstaller;

    public Dashboard(History history)
    {
        this.history = history;
        tracker = new Tracker(history.Record);
        seconds = history.IdleSeconds;
        paused = history.Paused;
        Text = "Janela";
        Width = 1150;
        Height = 790;
        MinimumSize = new Size(520, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        Controls.Add(web);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => Open());
        menu.Items.Add("Pausar / retomar", null, (_, _) => Toggle());
        menu.Items.Add("Sair", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = Icon, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        UpdateTray();
        Load += async (_, _) => await LoadPanel();
        Shown += async (_, _) => await CheckUpdates(stop.Token);
        SystemEvents.SessionSwitch += SessionSwitch;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionEnding += SessionEnding;
        _ = browser.Listen(stop.Token);
        _ = media.Initialize();
        timer.Tick += (_, _) => Sample();
        timer.Start();
    }

    void Open() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void UpdateTray() => tray.Text = paused ? "Janela · Pausado" : "Janela · Monitorando";
    void Toggle()
    {
        history.Paused = !paused;
        paused = !paused;
        Reset();
        UpdateTray();
    }

    void Reset() { tracker.End(DateTimeOffset.UtcNow); browser.Clear(); }

    void Sample()
    {
        if (pendingInstaller != null && !ContainsFocus && !locked && !suspended && !closingSession)
        {
            var installer = pendingInstaller;
            pendingInstaller = null;
            try
            {
                Updater.Launch(installer);
                exiting = true;
                Close();
                return;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        if (paused || locked || suspended || closingSession) return;
        var now = DateTimeOffset.UtcNow;
        var idle = Native.Idle();
        var window = Native.Foreground();
        if (idle is null || window is null) { tracker.End(now); return; }
        var rule = history.Classify(window.App, window.Title);
        bool video = window.App is "chrome" or "msedge"
            ? browser.Playing(window.App, window.Handle)
            : media.Playing(window);
        var remaining = TimeSpan.FromSeconds(seconds) - idle.Value;
        var state = video ? "video" : remaining <= TimeSpan.Zero ? "idle" : "active";
        var key = state == "idle" ? new Activity("", "", "", "idle") :
            new Activity(window.App, rule.Category, rule.Project, state);
        tracker.Observe(now, key, remaining);
    }

    async Task CheckUpdates(CancellationToken token)
    {
        if (!File.Exists(Updater.InstalledMarker)) return;
        try
        {
            // Give an installer that just restarted the app time to exit.
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (pendingInstaller == null)
                    {
                        Updater.CleanOldDownloads();
                        pendingInstaller = await Updater.Check(token);
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested) { Debug.WriteLine(ex); }
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    void SessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) OnUi(() => { locked = true; Reset(); });
        if (e.Reason == SessionSwitchReason.SessionUnlock) OnUi(() => { locked = false; Reset(); });
    }

    void PowerChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnUi(() => { suspended = true; Reset(); });
        if (e.Mode == PowerModes.Resume) OnUi(() => { suspended = false; Reset(); });
    }

    void SessionEnding(object? sender, SessionEndingEventArgs e) =>
        OnUi(() => { closingSession = true; Reset(); });

    void OnUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
    }

    async Task LoadPanel()
    {
        try
        {
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Program.DataPath, "WebView"));
            if (IsDisposed) return;
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("janela.local", Path.Combine(AppContext.BaseDirectory, "ui"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != "https://janela.local/index.html";
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != "https://janela.local/index.html") return;
                try { HandleMessage(e.WebMessageAsJson); }
                catch (Exception ex)
                {
                    string? action = null;
                    try
                    {
                        using var failed = JsonDocument.Parse(e.WebMessageAsJson);
                        action = failed.RootElement.GetProperty("action").GetString();
                    }
                    catch (Exception) { }
                    Send(new { action, error = "Não foi possível concluir a operação.",
                        detail = ex is ArgumentException ? ex.Message : "" });
                }
            };
            core.Navigate("https://janela.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir o painel. Verifique o runtime do WebView2.\n{ex.Message}", "Janela");
        }
    }

    void HandleMessage(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var data = doc.RootElement;
        var action = data.GetProperty("action").GetString();
        switch (action)
        {
            case "toggle": Toggle(); break;
            case "settings":
                var limit = data.GetProperty("seconds").GetInt32();
                if (limit is < 5 or > 7200) throw new ArgumentException("Use de 5 a 7200 segundos.");
                history.IdleSeconds = seconds = limit;
                Reset();
                break;
            case "saveRule":
                history.SaveRule(ReadId(data), Field(data, "app"), Field(data, "contains"),
                    Field(data, "category"), Field(data, "project"));
                break;
            case "deleteRule": history.DeleteRule(ReadId(data)); break;
            case "correct":
                history.Correct(ReadId(data), Field(data, "category"), Field(data, "project"));
                break;
            case "refresh": break;
            default: return;
        }

        bool week = data.TryGetProperty("period", out var p) && p.GetString() == "week";
        var today = DateTime.Today;
        var day = week ? today.AddDays(-(((int)today.DayOfWeek + 6) % 7)) : today;
        var from = new DateTimeOffset(day).ToUnixTimeMilliseconds();
        var until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Send(new
        {
            action, period = week ? "week" : "day", paused, seconds, rows = history.Read(from, until),
            current = tracker.Current(from, until),
            rules = history.Rules
        });
    }

    static long ReadId(JsonElement data) => data.GetProperty("id").GetInt64();
    static string Field(JsonElement data, string name)
    {
        var value = data.GetProperty(name).GetString()?.Trim() ?? "";
        if (value.Length > 160) throw new ArgumentException("Use até 160 caracteres por campo.");
        return value;
    }

    void Send<T>(T payload) => web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Json));

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        timer.Stop();
        Reset();
        stop.Cancel();
        SystemEvents.SessionSwitch -= SessionSwitch;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionEnding -= SessionEnding;
        tray.Dispose();
        timer.Dispose();
        stop.Dispose();
        base.OnFormClosing(e);
    }
}

sealed record WindowInfo(nint Handle, string App, string Title, string AppId);

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInput { public uint Size; public uint Tick; }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetApplicationUserModelId(nint process, ref uint length, System.Text.StringBuilder appId);

    public static TimeSpan? Idle()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - input.Tick))
            : null;
    }

    public static WindowInfo? Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var title = new System.Text.StringBuilder(1024);
            GetWindowText(hwnd, title, title.Capacity);
            var appId = new System.Text.StringBuilder(256);
            uint length = (uint)appId.Capacity;
            try { GetApplicationUserModelId(process.Handle, ref length, appId); }
            catch (System.ComponentModel.Win32Exception) { }
            return new(hwnd, process.ProcessName.ToLowerInvariant(), title.ToString(), appId.ToString());
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}

sealed class MediaProbe
{
    GlobalSystemMediaTransportControlsSessionManager? manager;
    public async Task Initialize()
    {
        try { manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception) { }
    }

    public bool Playing(WindowInfo window)
    {
        try
        {
            return manager?.GetSessions().Any(session =>
            {
                var source = session.SourceAppUserModelId;
                if (!string.Equals(source, window.AppId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(source, window.App + ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrEmpty(source)) return false;
                var info = session.GetPlaybackInfo();
                return info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    info.PlaybackType == MediaPlaybackType.Video;
            }) == true;
        }
        catch (Exception) { return false; }
    }
}
