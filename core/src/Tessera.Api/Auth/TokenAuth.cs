using System.Security.Cryptography;
using System.Text;
using Tessera.Api.Boot;
using Tessera.Core.Domain;

namespace Tessera.Api.Auth;

/// <summary>
/// Who is calling. Resolved once per request and stashed on the context.
/// </summary>
/// <remarks>
/// There is one caller: the web app, holding the per-launch token the shell
/// put in both processes' environments (CONTRACT §2). It acts as the one
/// built-in account, so every repository keeps its <c>accountId</c> and
/// nothing below this line knows accounts went away.
/// </remarks>
public sealed class CurrentUser
{
    public Account? Account { get; set; }
    public bool IsAuthenticated => Account is not null;

    public long AccountIdOrThrow => Account?.AccountId
        ?? throw new InvalidOperationException("No authenticated account on this request.");
}

/// <summary>
/// The token check for everything under <c>/api</c>. <c>/health/*</c> is open.
/// </summary>
/// <remarks>
/// Two spellings, because <c>&lt;img&gt;</c> and <c>&lt;video&gt;</c> cannot
/// send a header: <c>Authorization: Bearer</c>, or <c>?sessionKey=</c>.
/// Compared in constant time; a wrong or missing token is a 401 with a
/// <c>detail</c>, never a redirect.
/// </remarks>
public sealed class TokenAuthMiddleware(RequestDelegate next, CoreEnvironment env)
{
    private readonly byte[] _token = Encoding.UTF8.GetBytes(env.Token);

    public async Task InvokeAsync(HttpContext ctx, CurrentUser current)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        var presented = BearerToken(ctx.Request.Headers.Authorization)
            ?? (ctx.Request.Query.TryGetValue("sessionKey", out var key) ? key.ToString() : null);

        if (string.IsNullOrEmpty(presented) || !Matches(presented))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                detail = string.IsNullOrEmpty(presented)
                    ? "No token. Send Authorization: Bearer <TESSERA_TOKEN> or ?sessionKey=<TESSERA_TOKEN>."
                    : "Wrong token.",
            });
            return;
        }

        current.Account = Account.BuiltIn;
        await next(ctx);
    }

    private bool Matches(string presented)
    {
        var given = Encoding.UTF8.GetBytes(presented.Trim());
        return given.Length == _token.Length && CryptographicOperations.FixedTimeEquals(given, _token);
    }

    private static string? BearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is not null && authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[prefix.Length..].Trim();
            return token.Length > 0 ? token : null;
        }

        return null;
    }
}

public static class AuthExtensions
{
    /// <summary>
    /// Refuse anonymous callers. The middleware already does for every /api
    /// path; this keeps each group closed by declaration as well, so a route
    /// mapped outside /api by mistake is still not open.
    /// </summary>
    public static RouteGroupBuilder RequireUser(this RouteGroupBuilder b) =>
        (RouteGroupBuilder)b.AddEndpointFilter(async (ctx, next) =>
        {
            var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            return current.IsAuthenticated
                ? await next(ctx)
                : Api.Fail(StatusCodes.Status401Unauthorized, "Token required");
        });

    public static RouteHandlerBuilder RequireUser(this RouteHandlerBuilder b) =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            return current.IsAuthenticated
                ? await next(ctx)
                : Api.Fail(StatusCodes.Status401Unauthorized, "Token required");
        });
}
