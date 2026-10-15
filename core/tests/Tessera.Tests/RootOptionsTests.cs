using Tessera.Api;

namespace Tessera.Tests;

/// <summary>
/// One folder, three spellings: the container's, the host's, another
/// container's. Tessera speaks the host's to everyone else, and these pin the
/// translation - including the prefix trap that would hand "/reddit-staging"
/// to a root called "/reddit".
/// </summary>
public sealed class RootOptionsTests
{
    private static readonly RootOptions Roots = new()
    {
        HostPaths = @"/library=X:/Immich/uploads/library;/reddit=X:\Pics\n\reddit;/mnt/e=E:/",
    };

    [Theory]
    [InlineData("/library/96e7/pratika_sood", "X:/Immich/uploads/library/96e7/pratika_sood")]
    [InlineData("/library", "X:/Immich/uploads/library")]
    [InlineData("/reddit/DesiVixens", "X:/Pics/n/reddit/DesiVixens")]   // backslashes in the registry are fine
    [InlineData("/mnt/e/Photos", "E:/Photos")]
    public void Translates_a_container_path_to_the_host_spelling(string container, string host) =>
        Assert.Equal(host, Roots.ToHostPath(container));

    [Theory]
    [InlineData("/reddit-staging/x")]   // a sibling that merely starts the same
    [InlineData("/thumbs/ab")]          // a root nobody declared
    public void Refuses_a_path_outside_every_declared_root(string container) =>
        Assert.Null(Roots.ToHostPath(container));
}
