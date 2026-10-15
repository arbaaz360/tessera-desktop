using Tessera.Data.Repositories;

namespace Tessera.Api.Licence;

/// <summary>What <c>GET /api/licence</c> answers, and the Library page shows.</summary>
public sealed record LicenceStatus(string State, int Cap, long Indexed, bool CapReached);

/// <summary>
/// The free-tier rule, in one place (CONTRACT §4).
/// </summary>
/// <remarks>
/// While the state is <c>free</c> and the library holds more than
/// <see cref="Cap"/> distinct contents across live locations, the scanner
/// still records every file, but no thumbnail and no vector is produced for
/// content beyond the cap. "Beyond" is decided by indexing order: the first
/// <see cref="Cap"/> contents by id are within it, so what a person saw
/// working keeps working and nothing flickers out when the 2,001st file
/// arrives. Setting the state to <c>licensed</c> releases the queue.
/// </remarks>
public sealed class FreeTier(SettingsRepository settings, LibraryRepository library)
{
    public const int Cap = 2000;

    public async Task<LicenceStatus> StatusAsync(CancellationToken ct)
    {
        var state = await settings.LicenceStateAsync(ct);
        var indexed = await library.IndexedCountAsync(ct);
        return new LicenceStatus(state, Cap, indexed, state == LicenceStates.Free && indexed > Cap);
    }

    /// <summary>
    /// The highest content id that may get a thumbnail or a vector right now,
    /// or null when nothing is held back.
    /// </summary>
    public async Task<long?> MaxContentIdAsync(CancellationToken ct)
    {
        if (await settings.LicenceStateAsync(ct) == LicenceStates.Licensed)
        {
            return null;
        }

        return await library.CapBoundaryAsync(Cap, ct);
    }
}
