using System.Security.Cryptography;
using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

public sealed class AccountRepository(Db db)
{
    public async Task<Account?> ByEmailAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            """
            SELECT account_id AS AccountId, email AS Email, display_name AS DisplayName,
                   is_admin AS IsAdmin, disabled_at AS DisabledAt, kind AS Kind
              FROM account WHERE email = @Email
            """,
            new { Email = email },
            cancellationToken: ct));
    }

    public async Task<(long AccountId, string Hash)?> CredentialsAsync(string email, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(long, string)?>(new CommandDefinition(
            "SELECT account_id, password_hash FROM account WHERE email = @Email AND disabled_at IS NULL",
            new { Email = email },
            cancellationToken: ct));
        return row;
    }

    public async Task<long> CreateAsync(
        string email, string displayName, string passwordHash, bool isAdmin, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO account (email, display_name, password_hash, is_admin)
            VALUES (@Email, @DisplayName, @Hash, @IsAdmin)
            RETURNING account_id
            """,
            new { Email = email, DisplayName = displayName, Hash = passwordHash, IsAdmin = isAdmin },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Account>(new CommandDefinition(
            """
            SELECT account_id AS AccountId, email AS Email, display_name AS DisplayName,
                   is_admin AS IsAdmin, disabled_at AS DisabledAt, kind AS Kind
              FROM account ORDER BY created_at
            """,
            cancellationToken: ct))).ToList();
    }

    // ------------------------------------------------------------- sessions

    /// <summary>
    /// Start a session and return the token to hand the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is returned once and never stored - only its SHA-256 goes to
    /// the database. A dump of this table is therefore not a set of working
    /// logins, which is not true of any scheme that stores the token itself.
    /// </para>
    /// <para>
    /// Expired sessions are swept in the same statement. A sweep that runs on
    /// a timer is a job that has to exist, be scheduled and be noticed when it
    /// stops; one that runs on every login cannot be forgotten, and bounds the
    /// table at "logins since the last login" without anything else existing.
    /// </para>
    /// </remarks>
    public async Task<string> StartSessionAsync(
        long accountId, string userAgent, TimeSpan lifetime, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            WITH swept AS (DELETE FROM session WHERE expires_at < now())
            INSERT INTO session (account_id, token_hash, user_agent, expires_at)
            VALUES (@AccountId, @Hash, @UserAgent, now() + @Lifetime)
            """,
            new
            {
                AccountId = accountId,
                Hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)),
                UserAgent = userAgent,
                Lifetime = lifetime,
            },
            cancellationToken: ct));

        return token;
    }

    public async Task<Account?> BySessionTokenAsync(string token, CancellationToken ct)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

        await using var conn = await db.OpenAsync(ct);
        var account = await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            """
            SELECT a.account_id AS AccountId, a.email AS Email, a.display_name AS DisplayName,
                   a.is_admin AS IsAdmin, a.disabled_at AS DisabledAt, a.kind AS Kind
              FROM session s JOIN account a ON a.account_id = s.account_id
             WHERE s.token_hash = @Hash AND s.expires_at > now() AND a.disabled_at IS NULL
            """,
            new { Hash = hash },
            cancellationToken: ct));

        if (account is not null)
        {
            // Fire-and-forget would lose the update on a crash; it is one cheap
            // write and the session table is small.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE session SET last_seen = now() WHERE token_hash = @Hash",
                new { Hash = hash },
                cancellationToken: ct));
        }

        return account;
    }

    public async Task EndSessionAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM session WHERE token_hash = @Hash",
            new { Hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)) },
            cancellationToken: ct));
    }

    public async Task<Account?> ByApiKeyAsync(string key, CancellationToken ct)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            """
            SELECT a.account_id AS AccountId, a.email AS Email, a.display_name AS DisplayName,
                   a.is_admin AS IsAdmin, a.disabled_at AS DisabledAt, a.kind AS Kind
              FROM api_key k JOIN account a ON a.account_id = k.account_id
             WHERE k.key_hash = @Hash AND k.revoked_at IS NULL AND a.disabled_at IS NULL
            """,
            new { Hash = hash },
            cancellationToken: ct));
    }

    /// <summary>
    /// Mint an API key for an account. Returns the secret exactly once.
    /// </summary>
    /// <remarks>
    /// Unlike the system this replaces, an admin can mint a key for another
    /// account. There, keys were self-service only, so wiring up a service
    /// account meant knowing its password - which is a worse answer.
    /// </remarks>
    public async Task<string> CreateApiKeyAsync(long accountId, string name, CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO api_key (account_id, name, key_hash) VALUES (@AccountId, @Name, @Hash)",
            new
            {
                AccountId = accountId,
                Name = name,
                Hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)),
            },
            cancellationToken: ct));

        return key;
    }

}
