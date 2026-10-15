using Tessera.Core.Domain;
using Tessera.Data.Repositories;

namespace Tessera.Api.Auth;

/// <summary>
/// Who is calling. Resolved once per request and stashed on the context.
/// </summary>
/// <remarks>
/// Four credential forms are accepted, deliberately: an <c>x-api-key</c>
/// header for services, a bearer session token for the web app's API calls, a
/// <c>sessionKey</c> query value for image/video URLs that cannot send headers,
/// and the session cookie used by ordinary browser requests. The explicit
/// forms take precedence over the cookie so switching accounts cannot leave
/// API data and media authenticated as the account that logged in last.
/// </remarks>
public sealed class CurrentUser
{
    public Account? Account { get; set; }
    public bool IsAuthenticated => Account is not null;

    public long AccountIdOrThrow => Account?.AccountId
        ?? throw new InvalidOperationException("No authenticated account on this request.");
}

public sealed class AuthMiddleware(RequestDelegate next)
{
    public const string CookieName = "tessera_session";

    public async Task InvokeAsync(HttpContext ctx, AccountRepository accounts, CurrentUser current)
    {
        var ct = ctx.RequestAborted;

        if (ctx.Request.Headers.TryGetValue("x-api-key", out var key) &&
            !string.IsNullOrWhiteSpace(key))
        {
            current.Account = await accounts.ByApiKeyAsync(key.ToString(), ct);
        }
        else if (TryBearerToken(ctx.Request.Headers.Authorization, out var bearer))
        {
            current.Account = await accounts.BySessionTokenAsync(bearer, ct);
        }
        else if (ctx.Request.Query.TryGetValue("sessionKey", out var sessionKey) &&
                 !string.IsNullOrWhiteSpace(sessionKey))
        {
            current.Account = await accounts.BySessionTokenAsync(sessionKey.ToString(), ct);
        }
        else if (ctx.Request.Cookies.TryGetValue(CookieName, out var token) &&
                 !string.IsNullOrWhiteSpace(token))
        {
            current.Account = await accounts.BySessionTokenAsync(token, ct);
        }

        await next(ctx);
    }

    private static bool TryBearerToken(string? authorization, out string token)
    {
        const string prefix = "Bearer ";
        if (authorization is not null &&
            authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            token = authorization[prefix.Length..].Trim();
            return token.Length > 0;
        }

        token = "";
        return false;
    }
}

public static class AuthExtensions
{
    /// <summary>
    /// Refuse anonymous callers.
    /// </summary>
    /// <remarks>
    /// Applied per endpoint group rather than globally-with-exceptions. An
    /// allow-list of public routes is the shape where someone adds a route and
    /// forgets it is public; requiring the filter makes the default "closed".
    /// </remarks>
    public static RouteHandlerBuilder RequireUser(this RouteHandlerBuilder b) =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            return current.IsAuthenticated
                ? await next(ctx)
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                                  title: "Sign in required");
        });

    public static RouteGroupBuilder RequireUser(this RouteGroupBuilder b) =>
        (RouteGroupBuilder)b.AddEndpointFilter(async (ctx, next) =>
        {
            var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            return current.IsAuthenticated
                ? await next(ctx)
                : Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
                                  title: "Sign in required");
        });

    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder b) =>
        (RouteGroupBuilder)b.AddEndpointFilter(async (ctx, next) =>
        {
            var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            return current.Account is { IsAdmin: true }
                ? await next(ctx)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                                  title: "Administrator only");
        });
}
