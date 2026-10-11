using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using SupportDesk.Core;
namespace SupportDesk.Api;

public sealed class ConflictException : Exception;
public sealed class Store
{
    readonly string connection;
    readonly NpgsqlDataSource? postgres;
    public bool IsPostgres => postgres is not null;
    public Store(string path)
    {
        if (path.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
        {
            connection = path;
            postgres = NpgsqlDataSource.Create(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15 }.ToString();
        }
        using var c = Open();
        if (IsPostgres)
            Run(c, "CREATE TABLE IF NOT EXISTS answers(id TEXT PRIMARY KEY,body TEXT NOT NULL,version BIGINT NOT NULL); CREATE TABLE IF NOT EXISTS meta(id INTEGER PRIMARY KEY CHECK(id=1),revision BIGINT NOT NULL); INSERT INTO meta VALUES(1,0) ON CONFLICT(id) DO NOTHING; CREATE TABLE IF NOT EXISTS audit(id BIGSERIAL PRIMARY KEY,time TEXT NOT NULL,operation TEXT NOT NULL,answer_id TEXT NOT NULL,revision BIGINT NOT NULL); CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,access TEXT UNIQUE,refresh TEXT UNIQUE,access_until BIGINT,refresh_until BIGINT,device TEXT); CREATE TABLE IF NOT EXISTS vectors(id TEXT PRIMARY KEY,version BIGINT,model TEXT,vector TEXT); CREATE INDEX IF NOT EXISTS audit_revision ON audit(revision); CREATE INDEX IF NOT EXISTS sessions_expiry ON sessions(refresh_until);");
        else Run(c, "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS answers(id TEXT PRIMARY KEY,body TEXT NOT NULL,version INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS meta(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL); INSERT OR IGNORE INTO meta VALUES(1,0); CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,time TEXT NOT NULL,operation TEXT NOT NULL,answer_id TEXT NOT NULL,revision INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,access TEXT UNIQUE,refresh TEXT UNIQUE,access_until INTEGER,refresh_until INTEGER,device TEXT); CREATE TABLE IF NOT EXISTS vectors(id TEXT PRIMARY KEY,version INTEGER,model TEXT,vector TEXT);");
    }
    public DbConnection Open()
    {
        if (postgres is not null) return postgres.OpenConnection();
        var c = new SqliteConnection(connection);
        c.Open();
        return c;
    }
    public static void Run(DbConnection c, string sql, params (string, object?)[] values)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in values)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    static Answer Read(string body, long version)
    {
        var a = JsonSerializer.Deserialize<Answer>(body)!;
        a.Version = version;
        return a;
    }
    public Snapshot Snapshot()
    {
        using var c = Open();
        using var tx = BeginRead(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT revision FROM meta WHERE id=1";
        var revision = (long)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT body,version FROM answers ORDER BY id";
        using var r = cmd.ExecuteReader();
        var answers = new List<Answer>();
        while (r.Read())
            answers.Add(Read(r.GetString(0), r.GetInt64(1)));
        return new(revision, answers);
    }
    public Changes Since(long since)
    {
        using var c = Open();
        using var tx = BeginRead(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT revision FROM meta WHERE id=1";
        long rev = (long)cmd.ExecuteScalar()!;
        if (since == rev)
            return new(rev, false, [], []);
        cmd.CommandText = "SELECT operation,answer_id FROM audit WHERE revision>@since ORDER BY revision";
        cmd.Parameters.AddWithValue("@since", since);
        var ids = new HashSet<string>();
        bool reset = since < 0 || since > rev;
        using (var r = cmd.ExecuteReader())
        while (r.Read())
        {
            if (r.GetString(0) == "import")
                reset = true;
            else
                ids.Add(r.GetString(1));
        }
        if (reset || ids.Count == 0)
        {
            cmd.CommandText = "SELECT body,version FROM answers";
            using var r = cmd.ExecuteReader();
            var all = new List<Answer>();
            while (r.Read())
                all.Add(Read(r.GetString(0), r.GetInt64(1)));
            return new(rev, true, all, []);
        }
        var changed = new List<Answer>();
        var deleted = new List<string>();
        foreach (string id in ids)
        {
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT body,version FROM answers WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                changed.Add(Read(r.GetString(0), r.GetInt64(1)));
            else
                deleted.Add(id);
        }
        return new(rev, false, changed, deleted);
    }
    public void Change(string id, ChangeRequest req, bool delete)
    {
        if (id.Length > 128 || req.BaseVersion < 0)
            throw new ArgumentException("Invalid ID/version");
        if (!delete)
            Validate(req.Answer!, id);
        using var c = Open();
        using var tx = BeginWrite(c);
        if (IsPostgres) Run(c, "SELECT revision FROM meta WHERE id=1 FOR UPDATE");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT version,body FROM answers WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        long? value = null; Answer? previous = null;
        using (var reader = cmd.ExecuteReader()) if (reader.Read()) { value = reader.GetInt64(0); previous = Read(reader.GetString(1), value.Value); }
        long version = value ?? 0;
        // Older clients do not know Responses: preserve them during edits.
        if (!delete && previous?.Responses.Count > 0 && req.Answer!.Responses.Count == 0) { req.Answer.Responses = previous.Responses.Select(o => o with { }).ToList(); req.Answer.Responses[0].Body = req.Answer.Body; }
        if (!delete) Validate(req.Answer!, id);
        if (version != req.BaseVersion || delete && value is null)
            throw new ConflictException();
        if (delete)
            Run(c, "DELETE FROM answers WHERE id=@id", ("@id", id));
        else
            Run(c, "INSERT INTO answers VALUES(@id,@body,@v) ON CONFLICT(id) DO UPDATE SET body=excluded.body,version=excluded.version", ("@id", id), ("@body", JsonSerializer.Serialize(req.Answer)), ("@v", version + 1));
        Run(c, "UPDATE meta SET revision=revision+1 WHERE id=1; INSERT INTO audit(time,operation,answer_id,revision) SELECT @time,@op,@id,revision FROM meta WHERE id=1; DELETE FROM vectors WHERE id=@id", ("@time", DateTimeOffset.UtcNow.ToString("O")), ("@op", delete ? "delete" : version == 0 ? "create" : "update"), ("@id", id));
        tx.Commit();
    }
    public static void Validate(Answer a, string id)
    {
        if (a is null || a.Id != id || string.IsNullOrWhiteSpace(a.Title) || a.Title.Length > 500 || string.IsNullOrWhiteSpace(a.Category) || a.Category.Length > 500 || string.IsNullOrWhiteSpace(a.Body) || a.Body.Length > 200000 || (a.Notes?.Length ?? 0) > 200000 || (a.Source?.Length ?? 0) > 200000 || a.Platform is not ("Все" or "Windows" or "macOS" or "Linux") || a.Kind is not ("reply" or "internal"))
            throw new ArgumentException("Invalid answer");
        a.Responses ??= [];
        if (a.Responses.Any(o => o is null) || a.Responses.Count > 100 || a.Responses.Select(o => o.Id).Distinct().Count() != a.Responses.Count || a.Responses.Any(o => string.IsNullOrWhiteSpace(o.Id) || o.Id.Length > 128 || string.IsNullOrWhiteSpace(o.Body) || o.Body.Length > 200000 || (o.Title?.Length ?? 0) > 500 || (o.When?.Length ?? 0) > 2000 || (o.Source?.Length ?? 0) > 2000 || o.Step < 0 || o.Step > 99) || a.Responses.Sum(o => (long)o.Body.Length) > 1000000) throw new ArgumentException("Invalid response options");
        foreach (var o in a.Responses) { o.Title = string.IsNullOrWhiteSpace(o.Title) ? "Вариант ответа" : o.Title; o.When ??= ""; o.Source ??= ""; }
        if (a.Responses.Count > 0) a.Body = a.Responses[0].Body;
        a.Notes ??= "";
        a.Source ??= "";
    }
    public DbTransaction BeginWrite(DbConnection connection) => connection is SqliteConnection sqlite
        ? sqlite.BeginTransaction(deferred: false) : connection.BeginTransaction();
    DbTransaction BeginRead(DbConnection connection) => IsPostgres
        ? connection.BeginTransaction(IsolationLevel.RepeatableRead) : connection.BeginTransaction();
}

public static class DatabaseParameters
{
    public static void AddWithValue(this DbParameterCollection parameters, string name, object value)
    {
        DbParameter parameter = parameters is NpgsqlParameterCollection ? new NpgsqlParameter() : new SqliteParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameters.Add(parameter);
    }
}

