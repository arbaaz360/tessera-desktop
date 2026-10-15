using Microsoft.AspNetCore.Http;
using Tessera.Api;

namespace Tessera.Tests;

/// <summary>
/// The rules about what a client may keep.
/// </summary>
/// <remarks>
/// These are cheap tests for an expensive failure. A cache turns a server's
/// momentary wrong answer into a client's durable one: once a browser has filed
/// a bad response away it stops asking, so fixing the server fixes nothing for
/// that browser. Every case below is a URL that has been, or could be, answered
/// wrongly for a moment.
/// </remarks>
public sealed class HttpCachingTests
{
    private static string? Policy(string path, int status = StatusCodes.Status200OK, bool declared = false) =>
        HttpCaching.DefaultFor(new PathString(path), status, declared);

    [Theory]
    [InlineData("/api/queues")]
    [InlineData("/api/system-config")]
    [InlineData("/api/users/me")]
    [InlineData("/API/Users/Me")]          // the path is not case sensitive to a browser
    [InlineData("/health/ready")]
    public void An_api_answer_is_never_stored(string path) =>
        Assert.Equal(HttpCaching.NoStore, Policy(path));

    [Fact]
    public void An_endpoint_that_declared_a_policy_keeps_it()
    {
        // A thumbnail is addressed by content id, so its URL cannot outlive its
        // bytes and it is genuinely immutable. Only the endpoint knows that, so
        // the default must not overwrite it.
        Assert.Null(Policy("/api/media/1234/thumb", declared: true));
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void A_failure_is_never_stored_whatever_the_path(int status)
    {
        // The exact shape of the bug this guards: a request under /assets, which
        // is otherwise cached for a year as immutable, that did NOT find a file.
        Assert.Equal(HttpCaching.NoStore, Policy("/assets/index-abc123.js", status));
        Assert.Equal(HttpCaching.NoStore, Policy("/jobs", status));
        Assert.Equal(HttpCaching.NoStore, Policy("/api/queues", status));
    }

    [Theory]
    [InlineData("/jobs")]
    [InlineData("/albums")]
    [InlineData("/assets/index-abc123.js")]
    public void A_file_that_was_found_is_left_to_the_file_handler(string path) =>
        Assert.Null(Policy(path));

    [Fact]
    public void A_hashed_asset_is_immutable_and_the_shell_is_not()
    {
        // The whole point of the split: the bundle's name changes when its bytes
        // do, so asking again is pure cost. index.html keeps one URL across every
        // build, so a client that stops asking is pinned to the first build it saw.
        Assert.Equal(HttpCaching.Immutable, HttpCaching.ForFile("/assets/index-abc123.js"));
        Assert.Equal(HttpCaching.Revalidate, HttpCaching.ForFile("/index.html"));
        Assert.Equal(HttpCaching.Revalidate, HttpCaching.ForFile("/jobs"));
    }

    [Fact]
    public void The_asset_prefix_is_matched_by_segment_not_by_text()
    {
        // "/assetsomething" starts with the same letters and is not the asset
        // folder. A prefix test done with StartsWith would say it is.
        Assert.Equal(HttpCaching.Revalidate, HttpCaching.ForFile("/assetsomething/x.js"));
    }
}
