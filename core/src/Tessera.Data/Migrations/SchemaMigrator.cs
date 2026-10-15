using System.Reflection;
using Dapper;
using Npgsql;

namespace Tessera.Data.Migrations;

/// <summary>
/// Brings the database up to the schema this build expects, at startup.
/// </summary>
/// <remarks>
/// <para>
/// Until now there was no way to change the schema at all: <c>db/01-schema.sql</c>
/// runs only when Postgres initialises an empty data directory, so any change
/// after the first start had to be typed into a live database by hand. That is
/// a change with no record, no ordering and no way to tell whether a given
/// database has it - which is how two environments quietly stop being the same
/// schema.
/// </para>
/// <para>
/// Migrations are embedded in the assembly rather than read from a mount, so
/// the code and the schema it expects travel together and cannot be deployed
/// apart. They run in filename order, each in its own transaction, and each is
/// recorded. A migration is never edited once applied - it is superseded.
/// </para>
/// <para>
/// The whole run holds a Postgres ADVISORY LOCK, so several API instances
/// starting at once queue rather than race. The lock is held by the session and
/// released even if the process is killed, which a lock table would not be.
/// </para>
/// </remarks>
public sealed class SchemaMigrator(Db db)
{
    /// <summary>Arbitrary, fixed, and ours: the key for the migration lock.</summary>
    private const long LockKey = 8_147_203_591_004_211L;

    private const string Prefix = "Tessera.Data.Migrations.";

    /// <summary>Apply anything this build has that the database does not.</summary>
    /// <returns>The versions applied, in order. Empty when already current.</returns>
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct)
    {
        var pending = new List<string>();

        await using var conn = await db.OpenAsync(ct);

        // Serialise across instances BEFORE reading what is applied: two
        // starting at once must not both decide the same migration is pending.
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@Key)", new { Key = LockKey }, cancellationToken: ct));

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS schema_migration (
                    version    text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
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

                // One transaction per migration: a failure leaves the database
                // on the last version that fully succeeded, never half of one.
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 600, cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migration (version) VALUES (@Version)",
                    new { Version = version }, transaction: tx, cancellationToken: ct));
                await tx.CommitAsync(ct);

                pending.Add(version);
            }
        }
        finally
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@Key)", new { Key = LockKey }, cancellationToken: ct));
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
