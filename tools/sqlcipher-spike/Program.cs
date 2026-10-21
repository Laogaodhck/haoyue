// 5.4 数据级静态加密（阶段性）步骤①：SQLCipher 兼容性验证 spike（可复跑）。
// 执行卡红线：SQLCipher + WAL + busy_timeout 兼容验证不过关 → 整体退级，不做。
// 实测结论见同目录 README.md（2026-10-21，全项过关）。
using Microsoft.Data.Sqlite;

// 关键点 0：provider 切换必须在任何连接打开前显式执行——bundle_e_sqlite3
//（Microsoft.Data.Sqlite 的传递依赖）与 bundle_e_sqlcipher 的 battery 初始化
// 顺序未定义，显式 SetProvider 是唯一可靠做法。
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

var dbPath = Path.Combine(Path.GetTempPath(), "haoyue-sqlcipher-spike.db");
File.Delete(dbPath);
var password = "spike-passphrase-甲乙丙丁-0123456789";

SqliteConnection Open(string password)
{
    var connection = new SqliteConnection($"Data Source={dbPath};Password={password}");
    connection.Open();
    return connection;
}

void Exec(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

string Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar()?.ToString() ?? "(null)";
}

string Truncate(string text) => text.Length > 120 ? text[..120] + "…" : text;

Console.WriteLine($"== 1. cipher_version（确认真的是 SQLCipher 在跑） ==");
using (var connection = Open(password))
{
    Console.WriteLine("cipher_version = " + Scalar(connection, "PRAGMA cipher_version;"));
    Console.WriteLine("kdf_iter = " + Scalar(connection, "PRAGMA kdf_iter;"));
}

Console.WriteLine($"\n== 2. 加密库内 FTS5 + trigram（SQLCipher 构建是否带 FTS5） ==");
using (var connection = Open(password))
{
    Exec(connection, "CREATE VIRTUAL TABLE fts USING fts5(title, tokenize='trigram');");
    Exec(connection, "INSERT INTO fts (title) VALUES ('知识库检索两层化执行方案'), ('quantization advisor');");
    var hits = Scalar(connection, "SELECT count(*) FROM fts WHERE fts MATCH '\"两层化\"';");
    Console.WriteLine($"FTS5 trigram CJK 命中：{hits}（期望 1）");
    if (hits != "1") { Console.WriteLine("VERDICT: FAIL（FTS5 不可用）"); return 1; }
}

Console.WriteLine($"\n== 3. WAL + busy_timeout 兼容 ==");
using (var connection = Open(password))
{
    Console.WriteLine("journal_mode = " + Scalar(connection, "PRAGMA journal_mode=WAL;"));
    Exec(connection, "PRAGMA busy_timeout=5000;");
    Console.WriteLine("busy_timeout = " + Scalar(connection, "PRAGMA busy_timeout;"));
    Exec(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
    Exec(connection, "INSERT INTO t (v) VALUES ('hello-中文');");
}

Console.WriteLine($"\n== 4. 正确口令重开 + 数据往返 ==");
string roundtrip;
using (var connection = Open(password))
{
    roundtrip = Scalar(connection, "SELECT v FROM t ORDER BY id DESC LIMIT 1;");
}
Console.WriteLine($"往返读取：{roundtrip}（期望 hello-中文）");

Console.WriteLine($"\n== 5. 无口令打开加密库 → 必须拒绝 ==");
var rejected = false;
try
{
    using var connection = new SqliteConnection($"Data Source={dbPath}");
    connection.Open();
    _ = Scalar(connection, "SELECT count(*) FROM sqlite_master;");
}
catch (Exception ex)
{
    rejected = true;
    Console.WriteLine($"无口令打开被拒：{ex.GetType().Name}: {Truncate(ex.Message)}");
}
Console.WriteLine(rejected ? "无口令访问已拒绝 ✓" : "VERDICT: FAIL（无口令可读）");

Console.WriteLine($"\n== 6. 明文库与加密库互不干扰（生产共存路径：超集语义） ==");
var plainPath = Path.Combine(Path.GetTempPath(), "haoyue-sqlcipher-spike-plain.db");
File.Delete(plainPath);
using (var plain = new SqliteConnection($"Data Source={plainPath}"))
{
    plain.Open();
    Exec(plain, "CREATE TABLE p (v TEXT);");
    Exec(plain, "INSERT INTO p VALUES ('plain-ok');");
    Console.WriteLine("明文库（同一进程、同一 provider）读写正常：" + Scalar(plain, "SELECT v FROM p;"));
}

Console.WriteLine($"\n== 7. 冷启动计时（20 次 open+首查，小库） ==");
var timings = new List<double>();
for (var index = 0; index < 20; index++)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using (var connection = Open(password))
    {
        _ = Scalar(connection, "SELECT count(*) FROM sqlite_master;");
    }
    stopwatch.Stop();
    timings.Add(stopwatch.Elapsed.TotalMilliseconds);
}
timings.Sort();
Console.WriteLine($"首次 {timings[0]:F1} ms，中位 {timings[10]:F1} ms，p95 {timings[18]:F1} ms（验收 <2000ms）");

Console.WriteLine($"\n== 8. 重量级对照：39MB 级加密库 vs 明文库（全表聚合，量化加密开销） ==");
var heavyPassword = "heavy-pass-甲乙丙丁";
foreach (var (path, secret, label) in new[]
         {
             (Path.Combine(Path.GetTempPath(), "haoyue-sqlcipher-heavy.db"), (string?)heavyPassword, "加密"),
             (Path.Combine(Path.GetTempPath(), "haoyue-sqlcipher-heavy-plain.db"), (string?)null, "明文"),
         })
{
    File.Delete(path);
    using (var connection = OpenHeavy(path, secret))
    {
        Exec(connection, "CREATE TABLE big (id INTEGER PRIMARY KEY, payload TEXT);");
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO big (payload) VALUES ($p)";
        var parameter = insert.Parameters.Add("$p", SqliteType.Text);
        var chunk = string.Concat(Enumerable.Repeat("数据块甲乙丙丁戊己庚辛", 64));
        for (var index = 0; index < 10000; index++)
        {
            parameter.Value = chunk + index;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    var scans = new List<double>();
    for (var index = 0; index < 10; index++)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using (var connection = OpenHeavy(path, secret))
        {
            _ = Scalar(connection, "SELECT count(*), sum(length(payload)) FROM big;");
        }
        stopwatch.Stop();
        scans.Add(stopwatch.Elapsed.TotalMilliseconds);
    }
    scans.Sort();
    Console.WriteLine($"{label}库（{new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB）：中位 {scans[5]:F0} ms，最大 {scans[^1]:F0} ms");
}

var passed = roundtrip == "hello-中文" && rejected;
Console.WriteLine($"\nVERDICT: {(passed ? "PASS —— 步骤①过关（详见 tools/sqlcipher-spike/README.md 与 ADR）" : "FAIL —— 按执行卡整体退级")}");
return passed ? 0 : 1;

SqliteConnection OpenHeavy(string path, string? password)
{
    var connection = new SqliteConnection(
        $"Data Source={path}" + (password is null ? "" : $";Password={password}"));
    connection.Open();
    return connection;
}
