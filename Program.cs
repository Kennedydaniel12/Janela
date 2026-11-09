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

        using var openPanel = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Janela.OpenPanel");
        using var instance = new Mutex(true, "Local\\Janela.Tracker", out var first);
        if (!first)
        {
            if (!args.Contains("--background")) openPanel.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(DataPath);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        using var store = new History(Path.Combine(DataPath, "history.sqlite"));
        using var window = new Dashboard(store, openPanel);
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
    readonly bool updatesEnabled = File.Exists(Updater.InstalledMarker);
    const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupName = "Pulso";
    UpdatePackage? pendingInstaller;
    bool checkingUpdate, checkedUpdate;
    string? updateError;
    string trayCategory;
    long nextTrayRefresh;

    public Dashboard(History history, EventWaitHandle openPanel)
    {
        this.history = history;
        tracker = new Tracker(history.Record);
        seconds = history.IdleSeconds;
        paused = history.Paused;
        trayCategory = history.TrayCategory;
        Text = "Pulso";
        Width = 1150;
        Height = 790;
        MinimumSize = new Size(520, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
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
        timer.Tick += (_, _) =>
        {
            if (openPanel.WaitOne(0)) Open();
            Sample();
        };
        timer.Start();
    }

    void Open() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    static long UnixTime(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();
    static string ShortDuration(long milliseconds)
    {
        var minutes = milliseconds / 60000;
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    long TrackedTime(long from, long until, string scope, string target)
    {
        var duration = history.TrackedMilliseconds(from, until, scope, target);
        var live = tracker.Current(from, until);
        if (live != null && live.State != "idle" && (scope == "" ||
            (scope == "category" && live.Category == target) ||
            (scope == "project" && live.Project == target && live.State == "active")))
            duration += live.End - live.Start;
        return duration;
    }

    void UpdateTray()
    {
        var scope = trayCategory.Length == 0 ? "" : "category";
        var label = scope == "" ? "Uso" : trayCategory[..Math.Min(trayCategory.Length, 24)];
        var total = TrackedTime(UnixTime(DateTime.Today), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), scope, trayCategory);
        var status = paused ? "Pausado" : locked || suspended ? "Em espera" : "Coleta ativa";
        tray.Text = $"{label} hoje: {ShortDuration(total)} · {status}";
    }

    void RefreshTrayAndGoals()
    {
        if (Environment.TickCount64 < nextTrayRefresh) return;
        nextTrayRefresh = Environment.TickCount64 + 15000;
        UpdateTray();
        if (locked || suspended || closingSession) return;
        var week = UnixTime(WeekStart(DateTime.Today));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var goal in history.WeeklyGoals())
        {
            if (!goal.Notify || goal.NotifiedWeek == week ||
                TrackedTime(week, now, goal.Scope, goal.Target) < goal.Minutes * 60000L) continue;
            var title = goal.Mode == "minimum" ? "Meta semanal atingida" : "Limite semanal atingido";
            tray.ShowBalloonTip(5000, title, $"{goal.Target} chegou a {ShortDuration(goal.Minutes * 60000L)} nesta semana.", ToolTipIcon.Info);
            history.MarkGoalNotified(goal.Id, week);
            break;
        }
    }
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
        if (!ContainsFocus && updateError == null && InstallUpdate()) return;
        RefreshTrayAndGoals();
        if (paused || locked || suspended || closingSession) return;
        var now = DateTimeOffset.UtcNow;
        var idle = Native.Idle();
        var window = Native.Foreground(captureIcon: true);
        if (idle is null || window is null) { tracker.End(now); return; }
        var rule = history.Classify(window.App, window.Title);
        bool video = window.App is "chrome" or "msedge" or "brave"
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
        if (!updatesEnabled) return;
        try
        {
            // Give an installer that just restarted the app time to exit.
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            while (!token.IsCancellationRequested)
            {
                await CheckForUpdate(token);
                await Task.Delay(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    async Task CheckForUpdate(CancellationToken token)
    {
        if (!updatesEnabled || checkingUpdate || pendingInstaller != null) return;
        checkingUpdate = true;
        updateError = null;
        try
        {
            Updater.CleanOldDownloads();
            pendingInstaller = await Updater.Check(token);
            checkedUpdate = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            updateError = "Não foi possível verificar atualizações. Tente novamente.";
        }
        finally { checkingUpdate = false; }
    }

    bool InstallUpdate()
    {
        if (pendingInstaller == null || locked || suspended || closingSession) return false;
        try { Updater.Launch(pendingInstaller); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            updateError = "Não foi possível iniciar a instalação. Tente novamente.";
            return false;
        }

        exiting = true;
        Close();
        return true;
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
            Directory.CreateDirectory(AppIcons.Folder);
            core.SetVirtualHostNameToFolderMapping(AppIcons.Host, AppIcons.Folder,
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
            MessageBox.Show(this, $"Não foi possível abrir o painel. Verifique o runtime do WebView2.\n{ex.Message}", "Pulso");
        }
    }

    void HandleMessage(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var data = doc.RootElement;
        var action = data.GetProperty("action").GetString();
        switch (action)
        {
            case "weeklyReport":
                SendWeeklyReport();
                return;
            case "saveGoal":
                history.SaveWeeklyGoal(ReadId(data), Field(data, "scope"), Field(data, "target"),
                    Field(data, "mode"), data.GetProperty("minutes").GetInt32(), data.GetProperty("notify").GetBoolean());
                SendWeeklyReport(action);
                return;
            case "deleteGoal":
                history.DeleteWeeklyGoal(ReadId(data));
                SendWeeklyReport(action);
                return;
            case "trayCategory":
                history.TrayCategory = trayCategory = Field(data, "category");
                UpdateTray();
                break;
            case "startup":
                if (!updatesEnabled) throw new ArgumentException("Instale o Pulso para habilitar o início com o Windows.");
                using (var key = Registry.CurrentUser.CreateSubKey(StartupKey))
                {
                    if (data.GetProperty("enabled").GetBoolean())
                        key.SetValue(StartupName, $"\"{Application.ExecutablePath}\" --background");
                    else key.DeleteValue(StartupName, false);
                }
                break;
            case "previewRule":
                var preview = history.PreviewRule(
                    Field(data, "app"), Field(data, "contains"), Field(data, "category"), Field(data, "project"));
                Send(new { action, preview.Count, preview.Milliseconds, preview.ThroughId });
                return;
            case "openApps": Send(new { action, apps = Native.OpenApps() }); return;
            case "groupApps":
                var known = Native.OpenApps().Select(app => app.App).Concat(history.KnownApps())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(app => app, StringComparer.CurrentCultureIgnoreCase);
                Send(new { action, apps = known.Select(app => new { app, icon = AppIcons.Find(app) }) });
                return;
            case "saveGroup":
                var members = data.GetProperty("apps").EnumerateArray()
                    .Select(item => (item.GetString() ?? "").Trim().ToLowerInvariant())
                    .Distinct().ToArray();
                if (members.Any(app => app.Length is < 1 or > 160))
                    throw new ArgumentException("Um dos nomes de aplicativo é inválido.");
                history.SaveAppGroup(ReadId(data), Field(data, "name"), members);
                break;
            case "deleteGroup": history.DeleteAppGroup(ReadId(data)); break;
            case "checkUpdate": _ = CheckForUpdate(stop.Token); break;
            case "installUpdate": if (InstallUpdate()) return; break;
            case "toggle": Toggle(); break;
            case "settings":
                var limit = data.GetProperty("seconds").GetInt32();
                if (limit is < 5 or > 7200) throw new ArgumentException("Use de 5 a 7200 segundos.");
                history.IdleSeconds = seconds = limit;
                Reset();
                break;
            case "saveRule":
                history.SaveRule(ReadId(data), Field(data, "app"), Field(data, "contains"),
                    Field(data, "category"), Field(data, "project"),
                    data.TryGetProperty("throughId", out var throughId) ? throughId.GetInt64() : null);
                break;
            case "deleteRule": history.DeleteRule(ReadId(data)); break;
            case "correct":
                var ids = data.GetProperty("ids").EnumerateArray().Select(id => id.GetInt64()).ToArray();
                history.Correct(ids, Field(data, "category"), Field(data, "project"));
                break;
            case "refresh": break;
            default: return;
        }

        var period = data.TryGetProperty("period", out var p) ? p.GetString() : "day";
        var today = DateTime.Today;
        var start = period switch
        {
            "week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            "yesterday" => today.AddDays(-1),
            "last7" => today.AddDays(-6),
            "custom" => ReadDate(data, "start"),
            _ => today
        };
        var end = period switch
        {
            "yesterday" => today,
            "custom" => ReadDate(data, "end").AddDays(1),
            _ => today.AddDays(1)
        };
        if (start >= end || start > today || end > today.AddDays(1))
            throw new ArgumentException("Escolha um período válido, até hoje.");
        var from = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        var until = Math.Min(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new DateTimeOffset(end).ToUnixTimeMilliseconds());
        var offsetDays = period switch
        {
            "week" or "last7" => 7,
            "custom" => (end - start).Days,
            _ => 1
        };
        var previousStart = new DateTimeOffset(start.AddDays(-offsetDays)).ToUnixTimeMilliseconds();
        var previousEnd = new DateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(until)
            .LocalDateTime.AddDays(-offsetDays)).ToUnixTimeMilliseconds();
        var rows = history.Read(from, until);
        var current = tracker.Current(from, until);
        var groups = history.AppGroups();
        var icons = rows.Select(row => row.App).Append(current?.App ?? "")
            .Concat(history.Rules.Select(rule => rule.App)).Concat(groups.SelectMany(group => group.Apps)).Distinct()
            .Select(app => (App: app, Icon: AppIcons.Find(app)))
            .Where(entry => entry.Icon != null)
            .ToDictionary(entry => entry.App, entry => entry.Icon);
        Send(new
        {
            action, period, rangeKey = Field(data, "rangeKey"), from, until, paused, seconds, rows, current, icons,
            previousActive = history.ActiveMilliseconds(previousStart, previousEnd),
            startup = new { enabled = Registry.GetValue(@"HKEY_CURRENT_USER\" + StartupKey, StartupName, null) is string,
                available = updatesEnabled },
            rules = history.Rules, groups,
            trayCategory,
            update = new
            {
                version = Updater.InstalledVersion.ToString(3),
                available = pendingInstaller?.Version.ToString(3),
                enabled = updatesEnabled, checking = checkingUpdate, checkedUpdate, error = updateError
            }
        });
    }

    void SendWeeklyReport(string action = "weeklyReport")
    {
        var start = WeekStart(DateTime.Today);
        var now = DateTime.Now;
        var from = UnixTime(start);
        var until = UnixTime(now);
        var goals = history.WeeklyGoals().Select(goal => new
        {
            goal.Id, goal.Scope, goal.Target, goal.Mode, goal.Minutes, goal.Notify,
            progress = TrackedTime(from, until, goal.Scope, goal.Target)
        });
        Send(new
        {
            action, from, until, paused, rows = history.Read(from, until), current = tracker.Current(from, until),
            previous = history.Read(UnixTime(start.AddDays(-7)), UnixTime(now.AddDays(-7))),
            goals
        });
    }

    static long ReadId(JsonElement data) => data.GetProperty("id").GetInt64();
    static DateTime ReadDate(JsonElement data, string name)
    {
        if (!DateOnly.TryParseExact(Field(data, name), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ||
            date.Year < 2000 || date > DateOnly.FromDateTime(DateTime.Today))
            throw new ArgumentException("Informe datas válidas para o período.");
        return date.ToDateTime(TimeOnly.MinValue);
    }
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
    const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    struct LastInput { public uint Size; public uint Tick; }

    delegate bool EnumWindow(nint hwnd, nint data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
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

    public static WindowInfo? Foreground(bool captureIcon = false) =>
        ReadWindow(GetForegroundWindow(), includeAppId: true, captureIcon);

    public static IReadOnlyList<OpenApp> OpenApps()
    {
        var apps = new Dictionary<string, OpenApp>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) ||
                (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
                return true;

            var window = ReadWindow(hwnd, includeAppId: false, captureIcon: true);
            if (window != null && !string.IsNullOrWhiteSpace(window.Title))
                apps.TryAdd(window.App, new OpenApp(window.App, window.Title, AppIcons.Find(window.App)));
            return true;
        }, 0);
        return apps.Values.OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public sealed record OpenApp(string App, string Title, string? Icon);

    static WindowInfo? ReadWindow(nint hwnd, bool includeAppId, bool captureIcon)
    {
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
            if (includeAppId)
            {
                try { GetApplicationUserModelId(process.Handle, ref length, appId); }
                catch (System.ComponentModel.Win32Exception) { }
            }
            var app = process.ProcessName.ToLowerInvariant();
            if (captureIcon) AppIcons.Capture(process, app);
            return new(hwnd, app, title.ToString(), appId.ToString());
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
