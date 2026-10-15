using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Tessera.Core;
using Tessera.Data;
using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>What a search asks for, after the query string has been read.</summary>
/// <param name="Text">The words, or null for "no text" (metadata mode only).</param>
/// <param name="AlbumId">Only members of this album.</param>
/// <param name="MimePrefix">"image/" or "video/", or null for both.</param>
/// <param name="Limit">Rows wanted.</param>
/// <param name="Offset">Rows to skip first (the web app pages by number).</param>
public sealed record SearchQuery(
    string? Text, long? AlbumId, string? MimePrefix,
    DateTimeOffset? TakenAfter, DateTimeOffset? TakenBefore,
    int Limit, int Offset);

public sealed record SearchHit(long ContentId, float Score);

/// <summary>
/// Search, answered locally: words to a vector, cosine over the index, then
/// the filters against SQLite.
/// </summary>
/// <remarks>
/// <para>
/// The index knows ids, not what is live or in which album. So the filters
/// are applied after ranking, in two passes: over-fetch and keep what SQLite
/// admits, which is enough for an unfiltered or lightly filtered search; and
/// when that comes up short, fetch the whole admitted set once and let the
/// index skip everything else, which is exact for a narrow filter.
/// </para>
/// <para>
/// Metadata mode is a plain query: file names and the same filters, newest
/// first, with no vector involved.
/// </para>
/// </remarks>
public sealed class SearchService(Db db, IImageEmbedder embedder, IVectorIndex index, IOptions<StorageOptions> storage)
{
    public Task<IReadOnlyList<SearchHit>> TextAsync(long accountId, SearchQuery q, CancellationToken ct)
    {
        var vector = embedder.EmbedText(q.Text ?? throw new ArgumentException("A text search needs text.", nameof(q)));
        return RankAsync(accountId, vector, q, excludeId: null, ct);
    }

    /// <summary>"More like this": the picture's own vector as the query. Null when it has no thumbnail to embed from.</summary>
    public async Task<IReadOnlyList<SearchHit>?> SimilarAsync(long accountId, long contentId, SearchQuery q, CancellationToken ct)
    {
        byte[]? sha;
        await using (var conn = await db.OpenAsync(ct))
        {
            sha = await conn.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
                "SELECT sha256 FROM content WHERE content_id = @Id", new { Id = contentId }, cancellationToken: ct));
        }

        if (sha is null)
        {
            return null;
        }

        var path = Hashing.DerivedPath(storage.Value.ThumbnailRoot, sha, "thumb", "webp");
        if (!File.Exists(path))
        {
            return null;
        }

        using var bitmap = SkiaFiles.Decode(path);
        if (bitmap is null)
        {
            return null;
        }

        var vector = embedder.EmbedImage(bitmap);
        return await RankAsync(accountId, vector, q, excludeId: contentId, ct);
    }

    public async Task<IReadOnlyList<SearchHit>> MetadataAsync(long accountId, SearchQuery q, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var ids = await conn.QueryAsync<long>(new CommandDefinition(
            $"""
            SELECT t.content_id
              FROM timeline_entry t
              JOIN content c ON c.content_id = t.content_id
             WHERE {Filters}
               AND (@Name IS NULL OR EXISTS (
                       SELECT 1 FROM location l
                        WHERE l.content_id = t.content_id AND l.gone_at IS NULL
                          AND l.rel_path LIKE '%' || @Name || '%'))
             ORDER BY t.taken_at DESC, t.content_id DESC
             LIMIT @Take OFFSET @Skip
            """,
            new
            {
                Account = accountId, Album = q.AlbumId, Mime = q.MimePrefix,
                After = Db.ToMs(q.TakenAfter), Before = Db.ToMs(q.TakenBefore),
                Name = string.IsNullOrWhiteSpace(q.Text) ? null : q.Text.Trim(),
                Take = q.Limit, Skip = q.Offset,
            },
            cancellationToken: ct));

        return ids.Select(id => new SearchHit(id, 1f)).ToList();
    }

    private async Task<IReadOnlyList<SearchHit>> RankAsync(
        long accountId, float[] vector, SearchQuery q, long? excludeId, CancellationToken ct)
    {
        var want = q.Offset + q.Limit;

        // Pass 1: over-fetch, keep what the filters admit.
        var hits = index.Search(vector, Math.Max(want * 3, 300), id => id != excludeId);
        var admitted = await AdmittedAsync(accountId, hits.Select(h => h.Id).ToList(), q, ct);
        var kept = hits.Where(h => admitted.Contains(h.Id)).ToList();

        if (kept.Count < want && hits.Count < index.Count)
        {
            // Pass 2: the filters bite; rank only what they admit.
            var allowed = await AdmittedAsync(accountId, null, q, ct);
            kept = [.. index.Search(vector, want, id => id != excludeId && allowed.Contains(id))];
        }

        return kept.Skip(q.Offset).Take(q.Limit).Select(h => new SearchHit(h.Id, h.Score)).ToList();
    }

    /// <summary>Of these ids (or of everything, when null), the ones that are live and pass the filters.</summary>
    private async Task<HashSet<long>> AdmittedAsync(long accountId, IReadOnlyList<long>? ids, SearchQuery q, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<long>(new CommandDefinition(
            $"""
            SELECT t.content_id
              FROM timeline_entry t
              JOIN content c ON c.content_id = t.content_id
             WHERE {Filters}
               AND (@Ids IS NULL OR t.content_id IN (SELECT value FROM json_each(@Ids)))
            """,
            new
            {
                Account = accountId, Album = q.AlbumId, Mime = q.MimePrefix,
                After = Db.ToMs(q.TakenAfter), Before = Db.ToMs(q.TakenBefore),
                Ids = ids is null ? null : JsonSerializer.Serialize(ids),
            },
            cancellationToken: ct));
        return rows.ToHashSet();
    }

    private const string Filters = """
        t.account_id = @Account
           AND (@Album IS NULL OR EXISTS (
                SELECT 1 FROM album_item ai JOIN album al ON al.album_id = ai.album_id
                 WHERE ai.content_id = t.content_id AND ai.album_id = @Album AND al.account_id = @Account))
           AND (@Mime IS NULL OR c.mime LIKE @Mime || '%')
           AND (@After IS NULL OR t.taken_at >= @After)
           AND (@Before IS NULL OR t.taken_at <= @Before)
        """;
}
