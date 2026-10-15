using System.Reflection;
using Dapper;

namespace Tessera.Data.Migrations;

/// <summary>
/// Brings the database up to the schema this build expects, at startup.
/// </summary>
/// <remarks>
/// <para>
/// Migrations are embedded in the assembly rather than read from disk, so the
/// code and the schema it expects travel together and cannot be deployed
/// apart. They run in filename order, each in its own transaction, and each is
/// recorded. A migration is never edited once shipped - it is superseded by
/// the next file.
/// </para>
/// <para>
/// One process owns one library.db (core.pid), so there is no cross-process
/// lock here; the transaction is IMMEDIATE so a concurrent reader inside the
/// process waits rather than seeing half a schema.
/// </para>
/// </remarks>
public sealed class SchemaMigrator(Db db)
{
    private const string Prefix = "Tessera.Data.Migrations.";

    /// <summary>Apply anything this build has that the database does not.</summary>
    /// <returns>The versions applied, in order. Empty when already current.</returns>
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct)
    {
        var pending = new List<string>();

        await using var conn = await db.OpenWriteAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS schema_migration (
                version    TEXT PRIMARY KEY,
                applied_at INTEGER NOT NULL DEFAULT (CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER))
            )
            """,
            cancellationToken: ct));

        var applied = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT version FROM schema_migration", cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);

        foreach (var (version, sql) in Embedded())
        {
            if (applied.Contains(version))
            {
                continue;
            }

            // One transaction per migration: a failure leaves the database on
            // the last version that fully succeeded, never half of one.
            await using var tx = conn.BeginTransaction(deferred: false);
            await conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO schema_migration (version) VALUES (@Version)",
                new { Version = version }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);

            pending.Add(version);
        }

        return pending;
    }

    /// <summary>Every embedded migration, ordered by filename.</summary>
    private static IEnumerable<(string Version, string Sql)> Embedded()
    {
        var asm = Assembly.GetExecutingAssembly();

        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)
                                 && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name[Prefix.Length..^4], reader.ReadToEnd());
        }
    }
}
