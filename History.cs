using Microsoft.Data.Sqlite;

namespace Janela;

sealed record Activity(string App, string Category, string Project, string State);
sealed record Interval(long Id, long Start, long End, string App, string Category, string Project, string State);
sealed record Rule(long Id, string App, string Contains, string Category, string Project);
sealed record AppGroup(long Id, string Name, List<string> Apps);
sealed record WeeklyGoal(long Id, string Scope, string Target, string Mode, int Minutes, bool Notify, long NotifiedWeek);

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
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
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
            CREATE TABLE IF NOT EXISTS preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS weekly_goals(
                id INTEGER PRIMARY KEY, scope TEXT NOT NULL, target TEXT NOT NULL,
                mode TEXT NOT NULL, minutes INTEGER NOT NULL, notify INTEGER NOT NULL,
                notified_week INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS app_groups(
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL COLLATE NOCASE UNIQUE);
            CREATE TABLE IF NOT EXISTS app_group_members(
                app TEXT PRIMARY KEY, group_id INTEGER NOT NULL REFERENCES app_groups(id) ON DELETE CASCADE);
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

    public string TrayCategory
    {
        get
        {
            using var cmd = Command("SELECT value FROM preferences WHERE key='trayCategory'");
            return cmd.ExecuteScalar() as string ?? "";
        }
        set
        {
            using var cmd = Command("INSERT OR REPLACE INTO preferences VALUES('trayCategory',$value)", ("$value", value));
            cmd.ExecuteNonQuery();
        }
    }

    public List<WeeklyGoal> WeeklyGoals()
    {
        using var cmd = Command("SELECT id,scope,target,mode,minutes,notify,notified_week FROM weekly_goals ORDER BY id");
        using var rows = cmd.ExecuteReader();
        List<WeeklyGoal> goals = [];
        while (rows.Read()) goals.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
            rows.GetInt32(4), rows.GetBoolean(5), rows.GetInt64(6)));
        return goals;
    }

    public void SaveWeeklyGoal(long id, string scope, string target, string mode, int minutes, bool notify)
    {
        if (scope is not ("category" or "project") || target.Length == 0 ||
            mode is not ("minimum" or "maximum") || minutes is < 1 or > 10080)
            throw new ArgumentException("Informe um alvo e uma duração de 1 a 10.080 minutos.");
        using var cmd = Command(id == 0
            ? "INSERT INTO weekly_goals(scope,target,mode,minutes,notify) VALUES($scope,$target,$mode,$minutes,$notify)"
            : "UPDATE weekly_goals SET scope=$scope,target=$target,mode=$mode,minutes=$minutes,notify=$notify WHERE id=$id",
            ("$id", id), ("$scope", scope), ("$target", target), ("$mode", mode), ("$minutes", minutes), ("$notify", notify));
        cmd.ExecuteNonQuery();
    }

    public void DeleteWeeklyGoal(long id)
    {
        using var cmd = Command("DELETE FROM weekly_goals WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void MarkGoalNotified(long id, long week)
    {
        using var cmd = Command("UPDATE weekly_goals SET notified_week=$week WHERE id=$id", ("$id", id), ("$week", week));
        cmd.ExecuteNonQuery();
    }

    public long TrackedMilliseconds(long from, long until, string scope, string target)
    {
        using var cmd = Command("""
            SELECT coalesce(sum(min(end,$until)-max(start,$from)),0)
            FROM intervals WHERE end>$from AND start<$until AND state<>'idle' AND (
                $scope='' OR ($scope='category' AND category=$target)
                OR ($scope='project' AND project=$target AND state='active'))
            """, ("$from", from), ("$until", until), ("$scope", scope), ("$target", target));
        return Convert.ToInt64(cmd.ExecuteScalar());
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

    const string RetroactiveFilter = """
        app=$app AND state<>'idle' AND
        (($c<>'' AND category<>$c) OR ($p<>'' AND project<>$p))
        """;

    void ValidateRetroactive(string app, string fragment, string category, string project, SqliteTransaction? transaction)
    {
        if (fragment != "" || app == "" || (category == "" && project == ""))
            throw new ArgumentException("O histórico só pode ser atualizado por regras do aplicativo inteiro.");
        using var cmd = Command("SELECT 1 FROM rules WHERE app=$app AND fragment<>'' LIMIT 1", ("$app", app));
        cmd.Transaction = transaction;
        if (cmd.ExecuteScalar() != null)
            throw new ArgumentException("Este aplicativo tem regras por título. Como os títulos não são guardados, corrija as sessões na linha do tempo.");
    }

    public (long Count, long Milliseconds, long ThroughId) PreviewRule(string app, string fragment, string category, string project)
    {
        app = app.ToLowerInvariant();
        ValidateRetroactive(app, fragment, category, project, null);
        using var cmd = Command("SELECT count(*), coalesce(sum(end-start),0), coalesce(max(id),0) FROM intervals WHERE " +
            RetroactiveFilter, ("$app", app), ("$c", category), ("$p", project));
        using var row = cmd.ExecuteReader();
        row.Read();
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2));
    }

    public void SaveRule(long id, string app, string fragment, string category, string project, long? throughId = null)
    {
        if (app == "" || (category == "" && project == ""))
            throw new ArgumentException("Informe um aplicativo e uma categoria ou projeto.");
        app = app.ToLowerInvariant();
        using var transaction = db.BeginTransaction();
        if (throughId.HasValue) ValidateRetroactive(app, fragment, category, project, transaction);
        var sql = id > 0
            ? "UPDATE rules SET app=$app, fragment=$f, category=$c, project=$p WHERE id=$id"
            : """
              INSERT INTO rules(app,fragment,category,project) VALUES($app,$f,$c,$p)
              ON CONFLICT(app,fragment) DO UPDATE SET category=$c,project=$p
              """;
        using var cmd = Command(sql, ("$id", id), ("$app", app), ("$f", fragment),
            ("$c", category), ("$p", project));
        cmd.Transaction = transaction;
        cmd.ExecuteNonQuery();
        if (throughId is > 0)
        {
            using var update = Command("""
                UPDATE intervals SET category=CASE WHEN $c='' THEN category ELSE $c END,
                    project=CASE WHEN $p='' THEN project ELSE $p END
                WHERE id<=$id AND
                """ + " " + RetroactiveFilter,
                ("$id", throughId.Value), ("$app", app), ("$c", category), ("$p", project));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        Reload();
    }

    public void DeleteRule(long id)
    {
        using var cmd = Command("DELETE FROM rules WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
        Reload();
    }

    public IReadOnlyList<AppGroup> AppGroups()
    {
        using var cmd = Command("""
            SELECT g.id, g.name, m.app FROM app_groups g
            LEFT JOIN app_group_members m ON m.group_id=g.id ORDER BY g.name, m.app
            """);
        using var rows = cmd.ExecuteReader();
        var groups = new Dictionary<long, AppGroup>();
        while (rows.Read())
        {
            var id = rows.GetInt64(0);
            if (!groups.TryGetValue(id, out var group))
                groups[id] = group = new(id, rows.GetString(1), []);
            if (!rows.IsDBNull(2)) group.Apps.Add(rows.GetString(2));
        }
        return groups.Values.ToArray();
    }

    public List<string> KnownApps()
    {
        using var cmd = Command("""
            SELECT app FROM intervals WHERE app<>''
            UNION SELECT app FROM rules
            UNION SELECT app FROM app_group_members
            ORDER BY app
            """);
        using var rows = cmd.ExecuteReader();
        List<string> apps = [];
        while (rows.Read()) apps.Add(rows.GetString(0));
        return apps;
    }

    public void SaveAppGroup(long id, string name, string[] apps)
    {
        if (name.Length == 0 || apps.Length == 0)
            throw new ArgumentException("Informe um nome e selecione pelo menos um aplicativo.");
        using var transaction = db.BeginTransaction();
        using var save = Command(id == 0
            ? "INSERT INTO app_groups(name) VALUES($name)"
            : "UPDATE app_groups SET name=$name WHERE id=$id",
            ("$name", name), ("$id", id));
        save.Transaction = transaction;
        try
        {
            if (save.ExecuteNonQuery() != 1)
                throw new ArgumentException("Este grupo não existe mais.");
            var groupId = id;
            if (id == 0)
            {
                using var last = Command("SELECT last_insert_rowid()");
                last.Transaction = transaction;
                groupId = (long)last.ExecuteScalar()!;
            }
            using var clear = Command("DELETE FROM app_group_members WHERE group_id=$id", ("$id", groupId));
            clear.Transaction = transaction;
            clear.ExecuteNonQuery();
            using var add = Command("INSERT INTO app_group_members(app,group_id) VALUES($app,$id)",
                ("$app", ""), ("$id", groupId));
            add.Transaction = transaction;
            foreach (var app in apps)
            {
                add.Parameters["$app"].Value = app;
                add.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("O nome já está em uso ou um aplicativo pertence a outro grupo.");
        }
    }

    public void DeleteAppGroup(long id)
    {
        using var cmd = Command("DELETE FROM app_groups WHERE id=$id", ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void Correct(IReadOnlyList<long> ids, string category, string project)
    {
        using var transaction = db.BeginTransaction();
        using var cmd = Command("UPDATE intervals SET category=$c,project=$p WHERE id=$id AND state!='idle'",
            ("$id", 0L), ("$c", category), ("$p", project));
        cmd.Transaction = transaction;
        foreach (var id in ids)
        {
            cmd.Parameters["$id"].Value = id;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
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

    public long ActiveMilliseconds(long from, long until)
    {
        using var cmd = Command("""
            SELECT coalesce(sum(min(end,$until)-max(start,$from)),0)
            FROM intervals WHERE state<>'idle' AND end>$from AND start<$until
            """, ("$from", from), ("$until", until));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose() => db.Dispose();
}
