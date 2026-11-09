using Microsoft.Data.Sqlite;

namespace Janela;

sealed record Activity(string App, string Category, string Project, string State);
sealed record Interval(long Id, long Start, long End, string App, string Category, string Project, string State);
sealed record Rule(long Id, string App, string Contains, string Category, string Project);

sealed class Tracker(Action<Activity, long, long> record)
{
    Activity? current;
    long start, last, tick;
    public void Observe(DateTimeOffset now, Activity next, TimeSpan idleRemaining)
    {
        long at = now.ToUnixTimeMilliseconds();
        long clock = Environment.TickCount64;
        if (current != null && (at < last || clock - tick > 3000 || Math.Abs((at - last) - (clock - tick)) > 2000))
            Close(last);

        if (current != next)
        {
            long boundary = at;
            if (current?.State == "active" && next.State == "idle")
                boundary = Math.Clamp(at + (long)idleRemaining.TotalMilliseconds, last, at);
            Close(boundary);
            current = next;
            start = boundary;
        }
        else if (at - start >= 30000)
        {
            record(next, start, at);
            start = at;
        }
        last = at;
        tick = clock;
    }

    public void End(DateTimeOffset now)
    {
        long at = now.ToUnixTimeMilliseconds();
        Close(at >= last && at - last <= 1500 ? at : last);
    }

    void Close(long end)
    {
        if (current != null && end > start) record(current, start, end);
        current = null;
    }

    public Interval? Current(long from, long until) => current == null || last <= from || last <= start
        ? null : new(0, Math.Max(start, from), Math.Min(last, until), current.App, current.Category, current.Project, current.State);
}

sealed class History : IDisposable
{
    readonly SqliteConnection db;
    readonly List<Rule> rules = [];
    public IReadOnlyList<Rule> Rules => rules;

    public History(string path)
    {
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        using var setup = Command("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS intervals(
                id INTEGER PRIMARY KEY, start INTEGER NOT NULL, end INTEGER NOT NULL,
                app TEXT NOT NULL, category TEXT NOT NULL, project TEXT NOT NULL, state TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS intervals_end ON intervals(end);
            CREATE TABLE IF NOT EXISTS rules(
                id INTEGER PRIMARY KEY, app TEXT NOT NULL, fragment TEXT NOT NULL COLLATE NOCASE,
                category TEXT NOT NULL, project TEXT NOT NULL, UNIQUE(app, fragment));
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """);
        setup.ExecuteNonQuery();
        Reload();
    }

    SqliteCommand Command(string sql, params (string Name, object Value)[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return command;
    }

    public int IdleSeconds
    {
        get => ReadSetting("idle", 180);
        set => WriteSetting("idle", value);
    }

    public bool Paused
    {
        get => ReadSetting("paused", 0) != 0;
        set => WriteSetting("paused", value ? 1 : 0);
    }

    int ReadSetting(string key, int fallback)
    {
        using var cmd = Command("SELECT value FROM settings WHERE key=$key", ("$key", key));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? fallback);
    }

    void WriteSetting(string key, int value)
    {
        using var cmd = Command("INSERT OR REPLACE INTO settings VALUES($key, $value)", ("$key", key), ("$value", value));
        cmd.ExecuteNonQuery();
    }

    void Reload()
    {
        rules.Clear();
        using var cmd = Command("SELECT * FROM rules ORDER BY length(fragment) DESC, id DESC");
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) rules.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2),
            rows.GetString(3), rows.GetString(4)));
    }

    public (string Category, string Project) Classify(string app, string title)
    {
        string? category = null, project = null;
        foreach (var rule in rules)
        {
            if (rule.App != app || !title.Contains(rule.Contains, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.Category.Length > 0) category ??= rule.Category;
            if (rule.Project.Length > 0) project ??= rule.Project;
            if (category != null && project != null) break;
        }
        return (category ?? "", project ?? "");
    }

    public void SaveRule(long id, string app, string fragment, string category, string project)
    {
        if (app == "" || (category == "" && project == ""))
            throw new ArgumentException("Informe um aplicativo e uma categoria ou projeto.");
        var sql = id > 0
            ? "UPDATE rules SET app=$app, fragment=$f, category=$c, project=$p WHERE id=$id"
            : """
              INSERT INTO rules(app,fragment,category,project) VALUES($app,$f,$c,$p)
              ON CONFLICT(app,fragment) DO UPDATE SET category=$c,project=$p
              """;
        using var cmd = Command(sql, ("$id", id), ("$app", app.ToLowerInvariant()), ("$f", fragment),
            ("$c", category), ("$p", project));
        cmd.ExecuteNonQuery();
        Reload();
    }

    public void DeleteRule(long id)
    {
        using var cmd = Command("DELETE FROM rules WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
        Reload();
    }

    public void Correct(long id, string category, string project)
    {
        using var cmd = Command("UPDATE intervals SET category=$c,project=$p WHERE id=$id AND state!='idle'",
            ("$id", id), ("$c", category), ("$p", project));
        cmd.ExecuteNonQuery();
    }

    public void Record(Activity activity, long start, long end)
    {
        using var cmd = Command("INSERT INTO intervals(start,end,app,category,project,state) VALUES($s,$e,$a,$c,$p,$t)",
            ("$s", start), ("$e", end), ("$a", activity.App), ("$c", activity.Category),
            ("$p", activity.Project), ("$t", activity.State));
        cmd.ExecuteNonQuery();
    }

    public List<Interval> Read(long from, long until)
    {
        using var cmd = Command("""
            SELECT id, max(start,$from), min(end,$until), app, category, project, state
            FROM intervals WHERE end>$from AND start<$until ORDER BY start
            """, ("$from", from), ("$until", until));
        using var rows = cmd.ExecuteReader();
        List<Interval> result = [];
        while (rows.Read())
            result.Add(new(rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetString(3),
                rows.GetString(4), rows.GetString(5), rows.GetString(6)));
        return result;
    }

    public void Dispose() => db.Dispose();
}
