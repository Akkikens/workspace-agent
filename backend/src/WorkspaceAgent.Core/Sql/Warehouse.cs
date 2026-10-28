using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace WorkspaceAgent.Sql;

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>
/// The demo warehouse: a SQLite file seeded deterministically (same rows on every machine), and the
/// executor for governed queries. Governed statements run on a read-only connection with a SQLite
/// authorizer that denies every action except SELECT, reads of granted (table, column) pairs and
/// allow-listed functions. That check runs inside SQLite at prepare time, independently of the parser.
/// </summary>
public sealed class Warehouse
{
    private readonly string _connectionString;

    public Warehouse(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString();
    }

    public string Path { get; }

    public static Warehouse CreateSeeded(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var tx = conn.BeginTransaction();
            Seed(conn);
            tx.Commit();
        }
        return new Warehouse(path);
    }

    public QueryResult ExecuteGoverned(GovernedQuery q, IReadOnlySet<string> functions)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var denied = new List<string>();
        var grants = q.ReadGrants;
        // Keep the delegate alive for the lifetime of the connection.
        delegate_authorizer auth = (_, action, p0, p1, db, _) =>
        {
            string a = p0.utf8_to_string() ?? "", b = p1.utf8_to_string() ?? "", d = db.utf8_to_string() ?? "";
            switch (action)
            {
                case raw.SQLITE_SELECT:
                    return raw.SQLITE_OK;
                case raw.SQLITE_READ:
                    var table = a.ToLowerInvariant();
                    var col = b.ToLowerInvariant();
                    if (d is "main" or "" && (grants.Contains((table, col)) || (col == "" && grants.Any(g => g.Table == table))))
                        return raw.SQLITE_OK;
                    denied.Add($"read {a}.{b}");
                    return raw.SQLITE_DENY;
                case raw.SQLITE_FUNCTION:
                    if (functions.Contains(b)) return raw.SQLITE_OK;
                    denied.Add($"function {b}");
                    return raw.SQLITE_DENY;
                default:
                    denied.Add($"action {action}");
                    return raw.SQLITE_DENY;
            }
        };
        raw.sqlite3_set_authorizer(conn.Handle, auth, null);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = q.Sql;
            foreach (var (k, v) in q.Parameters) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return Read(cmd, (int)q.Limit);
        }
        catch (SqliteException e) when (denied.Count > 0)
        {
            throw new AuthorizerDeniedException(denied, e);
        }
        finally
        {
            raw.sqlite3_set_authorizer(conn.Handle, (delegate_authorizer?)null, null);
            GC.KeepAlive(auth);
        }
    }

    /// <summary>Ungoverned read-only execution, used only for eval gold queries and tests.</summary>
    public QueryResult ExecuteRaw(string sql, int maxRows = 10_000)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Read(cmd, maxRows);
    }

    private static QueryResult Read(SqliteCommand cmd, int maxRows)
    {
        using var r = cmd.ExecuteReader();
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var rows = new List<object?[]>();
        while (rows.Count < maxRows && r.Read())
        {
            var row = new object?[r.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return new QueryResult(cols, rows);
    }

    // ---------------------------------------------------------------- seed data

    public static readonly string[] BusinessUnits = ["Modem", "Camera", "Automotive", "Compute"];

    private static readonly (string Name, string Bu, string Status, double Budget)[] Programs =
    [
        ("5G Modem Gen 5", "Modem", "active", 42.0),
        ("NTN Satellite Link", "Modem", "at_risk", 18.5),
        ("RF Front-End Gen 4", "Modem", "completed", 26.0),
        ("ISP Pipeline v7", "Camera", "active", 21.0),
        ("Low-Light Video", "Camera", "at_risk", 9.5),
        ("Depth Sensing", "Camera", "completed", 12.0),
        ("ADAS Perception L3", "Automotive", "active", 55.0),
        ("Digital Cockpit 5", "Automotive", "active", 31.0),
        ("Telematics 5G", "Automotive", "at_risk", 14.0),
        ("Laptop SoC Bring-up", "Compute", "active", 64.0),
        ("NPU Compiler", "Compute", "active", 11.0),
        ("Arm64 Driver Stack", "Compute", "completed", 8.0),
    ];

    private static readonly Dictionary<string, string[]> Components = new()
    {
        ["Modem"] = ["baseband", "protocol-stack", "rf-calibration", "power"],
        ["Camera"] = ["isp", "sensor-driver", "autofocus", "video-encoder"],
        ["Automotive"] = ["perception", "hmi", "can-bus", "ota-update"],
        ["Compute"] = ["kernel", "gpu-driver", "npu-runtime", "firmware"],
    };

    private static readonly string[] Titles = ["Staff Engineer", "Senior Engineer", "Engineer", "Program Manager", "Test Engineer", "Engineering Manager"];
    private static readonly string[] FirstNames = ["Ana", "Ben", "Chloe", "Dev", "Elena", "Farid", "Grace", "Hiro", "Ines", "Jon", "Kavya", "Liam"];
    private static readonly string[] LastNames = ["Ito", "Khan", "Lopez", "Meyer", "Novak", "Okafor"];

    private sealed class Lcg(ulong seed)
    {
        private ulong _s = seed;
        public int Next(int n) { _s = _s * 6364136223846793005UL + 1442695040888963407UL; return (int)((_s >> 33) % (ulong)n); }
    }

    private static string Day(int offset) => new DateOnly(2026, 1, 1).AddDays(offset).ToString("yyyy-MM-dd");

    private static void Seed(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE programs (program_id INTEGER PRIMARY KEY, name TEXT NOT NULL, business_unit TEXT NOT NULL,
              status TEXT NOT NULL, start_date TEXT NOT NULL, target_date TEXT NOT NULL, budget_musd REAL NOT NULL);
            CREATE TABLE milestones (milestone_id INTEGER PRIMARY KEY, program_id INTEGER NOT NULL REFERENCES programs,
              name TEXT NOT NULL, due_date TEXT NOT NULL, completed_date TEXT, status TEXT NOT NULL);
            CREATE TABLE defects (defect_id INTEGER PRIMARY KEY, program_id INTEGER NOT NULL REFERENCES programs,
              severity TEXT NOT NULL, status TEXT NOT NULL, component TEXT NOT NULL, opened_at TEXT NOT NULL, closed_at TEXT);
            CREATE TABLE builds (build_id INTEGER PRIMARY KEY, program_id INTEGER NOT NULL REFERENCES programs,
              branch TEXT NOT NULL, status TEXT NOT NULL, duration_min INTEGER NOT NULL, started_at TEXT NOT NULL);
            CREATE TABLE employees (employee_id INTEGER PRIMARY KEY, name TEXT NOT NULL, business_unit TEXT NOT NULL,
              title TEXT NOT NULL, salary_usd INTEGER NOT NULL);
            """);

        var rng = new Lcg(20261028);
        int milestoneId = 1, defectId = 1, buildId = 1;
        string[] milestoneNames = ["Spec freeze", "Code complete", "Validation", "Launch readiness"];

        for (int p = 0; p < Programs.Length; p++)
        {
            var (name, bu, status, budget) = Programs[p];
            int pid = p + 1;
            int start = -300 + 20 * p;
            int target = start + 420 + rng.Next(120);
            Exec(conn, "INSERT INTO programs VALUES ($1,$2,$3,$4,$5,$6,$7)", pid, name, bu, status, Day(start), Day(target), budget);

            for (int m = 0; m < milestoneNames.Length; m++)
            {
                int due = start + 90 * (m + 1) + rng.Next(30);
                bool done = status == "completed" || due < 200 && rng.Next(4) != 0;
                string mStatus = done ? "done" : due < 300 ? "late" : "open";
                object? completed = done ? Day(due + rng.Next(25) - 10) : null;
                Exec(conn, "INSERT INTO milestones VALUES ($1,$2,$3,$4,$5,$6)", milestoneId++, pid, milestoneNames[m], Day(due), completed, mStatus);
            }

            int defects = 10 + pid * 7 % 13;
            for (int d = 0; d < defects; d++)
            {
                string sev = rng.Next(10) switch { 0 => "S1", < 3 => "S2", < 7 => "S3", _ => "S4" };
                int opened = rng.Next(280);
                string dStatus = status == "completed" ? "closed" : rng.Next(10) switch { < 4 => "open", < 6 => "in_progress", _ => "closed" };
                object? closed = dStatus == "closed" ? Day(opened + 1 + rng.Next(40)) : null;
                var comps = Components[bu];
                Exec(conn, "INSERT INTO defects VALUES ($1,$2,$3,$4,$5,$6,$7)", defectId++, pid, sev, dStatus, comps[rng.Next(comps.Length)], Day(opened), closed);
            }

            int builds = 8 + rng.Next(8);
            for (int b = 0; b < builds; b++)
            {
                string branch = rng.Next(4) == 0 ? "release" : "main";
                string bStatus = rng.Next(100) < (status == "at_risk" ? 35 : 15) ? "failed" : "passed";
                Exec(conn, "INSERT INTO builds VALUES ($1,$2,$3,$4,$5,$6)", buildId++, pid, branch, bStatus, 20 + rng.Next(70), Day(150 + rng.Next(130)));
            }
        }

        int empId = 1;
        foreach (var bu in BusinessUnits)
        {
            for (int i = 0; i < 6; i++)
            {
                var name = $"{FirstNames[rng.Next(FirstNames.Length)]} {LastNames[rng.Next(LastNames.Length)]}";
                var title = Titles[i];
                int salary = 120_000 + 5_000 * rng.Next(20);
                Exec(conn, "INSERT INTO employees VALUES ($1,$2,$3,$4,$5)", empId++, name, bu, title, salary);
            }
        }
    }

    private static void Exec(SqliteConnection conn, string sql, params object?[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

public sealed class AuthorizerDeniedException(IReadOnlyList<string> denied, Exception inner)
    : Exception($"SQLite authorizer denied: {string.Join(", ", denied.Distinct())}", inner)
{
    public IReadOnlyList<string> Denied { get; } = denied;
}
