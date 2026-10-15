using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Tessera.Data;

/// <summary>
/// Hands out connections to the one SQLite file, <c>library.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// WAL mode, so readers never wait for the one writer and the grid keeps
/// scrolling while a scan records files. <c>synchronous=NORMAL</c> under WAL
/// is durable against a process crash and loses at most the last
/// transactions on power loss, which for an index of files that are still on
/// disk is the right trade. Microsoft.Data.Sqlite pools connections, so
/// opening one per operation is a dictionary lookup, not a file open.
/// </para>
/// <para>
/// ONE WRITER, HELD IN-PROCESS. SQLite allows one writer at a time, and
/// Microsoft.Data.Sqlite waits for a busy database by sleeping 150 ms and
/// trying again - so four scanner threads recording files at once did not
/// queue, they each lost most of a second per write and a 60-file rescan
/// took five seconds. Every write opens through <see cref="OpenWriteAsync"/>,
/// which takes a semaphore for the life of the connection; the database
/// never sees a second writer, readers go through <see cref="OpenAsync"/>
/// and never wait (WAL). Measured: the same rescan in under half a second.
/// </para>
/// <para>
/// Every timestamp is an INTEGER of Unix milliseconds, UTC. Dapper converts
/// <see cref="DateTimeOffset"/> both ways through one type handler, and SQL
/// that needs "now" uses <see cref="NowMs"/>, so there is one clock and one
/// unit in the whole database.
/// </para>
/// </remarks>
public sealed class Db : IDisposable
{
    /// <summary>"Now" in SQL, as the same Unix milliseconds the application writes.</summary>
    public const string NowMs = "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writer = new(1, 1);

    static Db()
    {
        // Dapper maps snake_case columns onto PascalCase properties without an
        // alias on every column.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Dapper consults its built-in type map before any handler, so the
        // built-in DateTimeOffset entry must go or parameters would be sent as
        // ISO text into INTEGER columns.
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
        SqlMapper.AddTypeHandler(new UnixMillisecondsHandler());
    }

    public Db(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            // Also the busy timeout: how long a writer waits for another writer.
            DefaultTimeout = 30,
        }.ToString();

        // Persistent in the file; set once. synchronous is NOT persistent -
        // it is per connection, and the default (FULL) syncs the WAL on every
        // commit: measured at 20-30 ms a write on NTFS, 28 files/s for a scan.
        // So it is set on every connection below.
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL");
    }

    /// <summary>
    /// Per connection, not per file: foreign keys are off by default in
    /// SQLite and the schema's ON DELETE CASCADE rules depend on them;
    /// synchronous=NORMAL under WAL commits without an fsync and loses at
    /// most the last transactions on power loss, which for an index of
    /// files that are still on disk is the right trade.
    /// </summary>
    private const string PerConnection = "PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";

    public string Path { get; }

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(PerConnection);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A connection that may write. Only one exists at a time; disposing it
    /// lets the next writer in. Never open a second one while holding one.
    /// </summary>
    public async ValueTask<SqliteConnection> OpenWriteAsync(CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct);
        var conn = new WriteConnection(_connectionString, _writer);
        try
        {
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(PerConnection);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();   // releases the gate
            throw;
        }
    }

    /// <summary>The one write connection: holds the writer gate until it is disposed.</summary>
    private sealed class WriteConnection(string connectionString, SemaphoreSlim gate) : SqliteConnection(connectionString)
    {
        private int _released;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    public static long ToMs(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    public static long? ToMs(DateTimeOffset? t) => t is { } v ? v.ToUnixTimeMilliseconds() : null;
    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public static DateTimeOffset? FromMs(long? ms) => ms is { } v ? FromMs(v) : null;

    /// <summary>Drop the pooled connections, so the file can be deleted (tests) or the process can exit cleanly.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        _writer.Dispose();
    }
}

/// <summary>DateTimeOffset on the way in and out: Unix milliseconds, UTC.</summary>
internal sealed class UnixMillisecondsHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = value.ToUnixTimeMilliseconds();
    }

    public override DateTimeOffset Parse(object value) => value switch
    {
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
        int ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
        double ms => DateTimeOffset.FromUnixTimeMilliseconds((long)ms),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        _ => throw new DataException($"Cannot read a timestamp from {value.GetType().Name}"),
    };
}
