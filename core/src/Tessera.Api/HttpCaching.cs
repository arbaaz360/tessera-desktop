using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Tessera.Api;

/// <summary>
/// What a client is allowed to remember, decided once for the whole service.
/// </summary>
/// <remarks>
/// <para>
/// A response with no <c>Cache-Control</c> is not an uncached response. HTTP
/// lets a client invent a lifetime for it from <c>Last-Modified</c> - about a
/// tenth of the file's age - and then serve it again without asking. Every
/// response here used to be in that state, which is how a browser came to hold
/// HTML under an API URL: while <c>/api/queues</c> was unimplemented the app
/// shell answered it with a 200 and the shell's validators, so one browser
/// stored the document, judged it fresh for hours, and replayed it locally.
/// The jobs page then parsed a document as a list and died in a component that
/// had nothing wrong with it, in a browser that had stopped speaking to the
/// server. Another browser on the same machine was fine, because its cache was
/// empty.
/// </para>
/// <para>
/// The lesson is not "remember to set a header". It is that a cache turns a
/// server's momentary wrong answer into a client's durable one, and that a
/// service whose failures are storable cannot be fixed by fixing the service.
/// So the policy is a property of the service rather than of whoever wrote the
/// last endpoint, and it has three classes.
/// </para>
/// </remarks>
public static class HttpCaching
{
    /// <summary>Where the build puts files whose names carry a content hash.</summary>
    public const string HashedAssets = "/assets";

    /// <summary>
    /// A year, and no need to ask. Safe only where the URL cannot outlive the
    /// bytes - a content hash in the name, or a content-addressed id in the path.
    /// </summary>
    public const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>Keep a copy, but ask every time before using it.</summary>
    public const string Revalidate = "no-cache";

    /// <summary>Do not keep this at all.</summary>
    public const string NoStore = "no-store, no-cache, max-age=0";

    /// <summary>
    /// The default policy for a response, or <c>null</c> to leave it alone.
    /// </summary>
    /// <param name="path">The path that was requested.</param>
    /// <param name="statusCode">The status about to be sent.</param>
    /// <param name="alreadyDeclared">
    /// Whether the endpoint set its own <c>Cache-Control</c>. It wins: a
    /// thumbnail keyed by content id really is immutable, and only the endpoint
    /// knows that.
    /// </param>
    /// <remarks>
    /// Two things must not be stored, and the second is the one that is easy to
    /// miss. Anything dynamic, obviously - it answers a per-account question and
    /// changes constantly. But also anything that FAILED, whatever its path: a
    /// failure is a statement about one moment, and a client that files one away
    /// keeps being told about a problem that has since been fixed, without ever
    /// making the request that would correct it.
    /// </remarks>
    public static string? DefaultFor(PathString path, int statusCode, bool alreadyDeclared)
    {
        if (alreadyDeclared)
        {
            return null;
        }

        return statusCode >= StatusCodes.Status400BadRequest || IsDynamic(path)
            ? NoStore
            : null;
    }

    /// <summary>The policy for a file the static handler actually found on disk.</summary>
    /// <remarks>
    /// The app shell is the one file whose URL does not change when its contents
    /// do, so a client that stops asking for it is pinned to whichever build it
    /// saw first. Revalidating costs a 304.
    /// </remarks>
    public static string ForFile(PathString path) =>
        path.StartsWithSegments(HashedAssets, StringComparison.Ordinal) ? Immutable : Revalidate;

    /// <summary>
    /// Declare the policy for everything the app generates. Register before the
    /// rest of the pipeline, so it covers error pages and unmatched routes as
    /// well as endpoints.
    /// </summary>
    public static IApplicationBuilder UseCachePolicy(this IApplicationBuilder app) =>
        app.Use(static async (ctx, next) =>
        {
            // OnStarting rather than a plain assignment: the decision has to land
            // after the endpoint has run, or an endpoint that deliberately chose
            // its own policy would be overwritten by the default.
            ctx.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;

                var policy = DefaultFor(
                    response.HttpContext.Request.Path,
                    response.StatusCode,
                    response.Headers.ContainsKey(HeaderNames.CacheControl));

                if (policy is not null)
                {
                    response.Headers.CacheControl = policy;

                    // A stored validator is how a client re-acquires a body it
                    // was told not to keep.
                    response.Headers.Remove(HeaderNames.ETag);
                    response.Headers.Remove(HeaderNames.LastModified);
                }

                return Task.CompletedTask;
            }, ctx.Response);

            await next();
        });

    /// <summary>The policy for a file served by the static handler.</summary>
    public static void ForStaticFile(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers.CacheControl = ForFile(ctx.Context.Request.Path);

    /// <summary>The policy for the app shell, wherever it is served from.</summary>
    /// <remarks>
    /// Kept apart from <see cref="ForStaticFile"/> deliberately. The SPA fallback
    /// serves index.html under whatever URL was asked for, so deciding from the
    /// request path would let a request under <c>/assets</c> mark a document
    /// immutable for a year.
    /// </remarks>
    public static void ForAppShell(StaticFileResponseContext ctx) =>
        ctx.Context.Response.Headers.CacheControl = Revalidate;

    private static bool IsDynamic(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
