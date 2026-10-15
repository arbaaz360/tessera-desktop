using Tessera.Api.Auth;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Email, string Password);
    public sealed record UserResponse(long Id, string Email, string Name, bool IsAdmin);

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("auth");

        g.MapPost("/login", async (
            LoginRequest body, AccountRepository accounts, HttpContext ctx, CancellationToken ct) =>
        {
            var creds = await accounts.CredentialsAsync(body.Email, ct);

            // Verify even when the account is unknown, against a dummy hash, so
            // a missing account and a wrong password take the same time. Without
            // it, response timing enumerates valid emails.
            var hash = creds?.Hash ?? DummyHash;
            var ok = Verify(body.Password, hash) && creds is not null;

            if (!ok)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                                       title: "Wrong email or password");
            }

            var token = await accounts.StartSessionAsync(
                creds!.Value.AccountId,
                ctx.Request.Headers.UserAgent.ToString(),
                TimeSpan.FromDays(30),
                ct);

            ctx.Response.Cookies.Append(AuthMiddleware.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                // Secure is not forced: this is served over plain http on the
                // LAN behind an HTTPS ingress. Setting it here would silently
                // drop the cookie for anyone reaching it directly by port.
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(30),
                Path = "/",
            });

            var account = await accounts.ByEmailAsync(body.Email, ct);

            // A superset: Tessera's own fields plus the ones Immich's
            // LoginResponseDto promises, so the existing web app can log in
            // without a second endpoint that would drift from this one.
            // accessToken is the session token the cookie already carries -
            // the app only needs it to authenticate <img> URLs, which cannot
            // send a header.
            return Results.Ok(new
            {
                id = account!.AccountId,
                email = account.Email,
                name = account.DisplayName,
                isAdmin = account.IsAdmin,
                userId = Tessera.Core.ImmichId.Account(account.AccountId),
                userEmail = account.Email,
                accessToken = token,
                profileImagePath = "",
                shouldChangePassword = false,
                isOnboarded = true,
            });
        });

        g.MapPost("/logout", async (AccountRepository accounts, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Request.Cookies.TryGetValue(AuthMiddleware.CookieName, out var token) &&
                !string.IsNullOrWhiteSpace(token))
            {
                await accounts.EndSessionAsync(token, ct);
            }

            ctx.Response.Cookies.Delete(AuthMiddleware.CookieName);
            return Results.NoContent();
        });

        g.MapGet("/me", (CurrentUser current) =>
            current.Account is { } a
                ? Results.Ok(new UserResponse(a.AccountId, a.Email, a.DisplayName, a.IsAdmin))
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in required"));
    }

    /// <summary>
    /// A real bcrypt hash of a value nobody knows, used to burn the same CPU on
    /// a failed lookup as on a real one.
    /// </summary>
    private static readonly string DummyHash =
        BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: 10);

    /// <summary>
    /// Verify against the hash format carried over from Immich.
    /// </summary>
    /// <remarks>
    /// Immich stores bcrypt ($2b$10$...). Copying those hashes verbatim during
    /// the migration means every existing password keeps working and no
    /// password is ever reset - which also means nobody, including whoever runs
    /// the migration, has to know one.
    /// </remarks>
    private static bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
