using Tessera.Core;
using Tessera.Core.Domain;
using Tessera.Data.Repositories;

namespace Tessera.Api.Import;

/// <summary>Why the Tessera folder cannot be used where it is set.</summary>
public sealed class TesseraFolderException(string message, bool badRequest = false) : Exception(message)
{
    /// <summary>The request was malformed (not a path), not merely refused.</summary>
    public bool BadRequest { get; } = badRequest;
}

public sealed class TesseraFolderOptions
{
    /// <summary>
    /// Where the folder goes when nobody has chosen: <c>TESSERA_FOLDER</c>,
    /// else <c>Pictures/Tessera</c> in the user's own Pictures folder
    /// (<c>%USERPROFILE%\Pictures</c>, or wherever Windows has moved it;
    /// <c>~/Pictures</c> on macOS).
    /// </summary>
    public string DefaultPath { get; set; } = DefaultFor(null);

    public static string DefaultFor(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return PathRules.Canonical(Path.GetFullPath(overridePath));
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures))
        {
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        return PathRules.Canonical(Path.Combine(pictures, "Tessera"));
    }
}

/// <summary>
/// The Tessera folder: the one place the app writes. Pictures dropped or
/// picked from outside every library folder are copied here, filed by album.
/// </summary>
/// <remarks>
/// <para>
/// Made at first use, not at install: an app that has only ever indexed
/// folders in place never creates it. It is a library root like any other,
/// marked app-owned (<c>scan_root.app_owned</c>), so what is copied in is
/// scanned, watched and shown by the same machinery. Its folders are not
/// albums - the import files copies into albums itself.
/// </para>
/// <para>
/// It may sit inside a folder the person indexes (their whole Pictures
/// folder, say); that root then leaves it to its own root, so nothing is
/// indexed twice. Moving it in Settings changes where the next copy goes;
/// what was copied before stays where it is, still in the library.
/// </para>
/// </remarks>
public sealed class TesseraFolder(
    SettingsRepository settings,
    LibraryRepository library,
    Microsoft.Extensions.Options.IOptions<TesseraFolderOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Where the folder is, or will be at first use.</summary>
    public async Task<string> PathAsync(CancellationToken ct) =>
        await settings.ImportFolderAsync(ct) ?? options.Value.DefaultPath;

    /// <summary>The folder's root if it has been made; null before first use.</summary>
    public async Task<ScanRoot?> RootIfMadeAsync(CancellationToken ct)
    {
        var path = await PathAsync(ct);
        return (await library.AllRootsAsync(ct))
            .FirstOrDefault(r => string.Equals(r.AbsPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The folder, made and registered if this is its first use.
    /// </summary>
    /// <exception cref="TesseraFolderException">It would hold a library folder, or sit inside the app's own.</exception>
    public async Task<ScanRoot> EnsureAsync(long accountId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = await PathAsync(ct);
            var existing = (await library.AllRootsAsync(ct))
                .FirstOrDefault(r => string.Equals(r.AbsPath, path, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var (conflict, other) = await library.ConflictAsync(path, ct, appFolder: true);
                if (conflict != RootConflict.None)
                {
                    throw new TesseraFolderException(conflict == RootConflict.ContainsARoot
                        ? $"The Tessera folder {path} would hold the library folder {other}. Choose another place for it in Settings."
                        : $"The Tessera folder {path} is inside {other}. Choose another place for it in Settings.");
                }

                Directory.CreateDirectory(path);
                await library.AddRootAsync(accountId, path, "Tessera", appOwned: true, ct);
            }
            else
            {
                Directory.CreateDirectory(path);
                if (!existing.AppOwned)
                {
                    await library.MarkAppOwnedAsync(existing.RootId, ct);
                }
            }

            // Decided once: a later change of where Pictures is does not move it.
            await settings.SetImportFolderAsync(path, ct);

            return (await library.AllRootsAsync(ct))
                .First(r => string.Equals(r.AbsPath, path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Choose where the next copies go. Nothing already copied moves: the old
    /// folder stays in the library, as it is.
    /// </summary>
    /// <exception cref="TesseraFolderException">Not an absolute path, a library folder already, or one that would hold one.</exception>
    public async Task<string> MoveAsync(string rawPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath.Trim()))
        {
            throw new TesseraFolderException("The Tessera folder must be an absolute path.", badRequest: true);
        }

        var path = PathRules.Canonical(Path.GetFullPath(rawPath.Trim()));
        await _gate.WaitAsync(ct);
        try
        {
            var roots = await library.AllRootsAsync(ct);
            var same = roots.FirstOrDefault(r => string.Equals(r.AbsPath, path, StringComparison.OrdinalIgnoreCase));
            if (same is { AppOwned: false })
            {
                throw new TesseraFolderException($"{path} is a library folder already; the Tessera folder must be a folder of its own.");
            }

            if (same is null)
            {
                var (conflict, other) = await library.ConflictAsync(path, ct, appFolder: true);
                if (conflict != RootConflict.None)
                {
                    throw new TesseraFolderException(conflict == RootConflict.ContainsARoot
                        ? $"{path} holds the library folder {other}; choose a folder that holds none."
                        : $"{path} is inside the earlier Tessera folder {other}; choose another place.");
                }
            }

            await settings.SetImportFolderAsync(same?.AbsPath ?? path, ct);
            return same?.AbsPath ?? path;
        }
        finally
        {
            _gate.Release();
        }
    }
}
