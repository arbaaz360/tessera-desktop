using Dapper;
using Tessera.Core.Domain;

namespace Tessera.Data.Repositories;

/// <summary>
/// The one built-in account. Shrunk from logins, sessions and API keys to a
/// single read, so that every <c>account_id</c> query in the repositories
/// stays exactly as it was (CONTRACT §2).
/// </summary>
public sealed class AccountRepository(Db db)
{
    public async Task<Account?> GetAsync(long accountId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            """
            SELECT account_id AS AccountId, display_name AS DisplayName, created_at AS CreatedAt
              FROM account WHERE account_id = @AccountId
            """,
            new { AccountId = accountId },
            cancellationToken: ct));
    }
}
