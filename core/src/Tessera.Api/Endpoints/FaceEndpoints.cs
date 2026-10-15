using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tessera.Api.Auth;
using Tessera.Core;
using Tessera.Data.Repositories;

namespace Tessera.Api.Endpoints;

/// <summary>
/// Find a face across the library.
/// </summary>
/// <remarks>
/// <para>
/// FaceSearch owns every face and answers "which faces look like these" by
/// hash. Tessera does what it does for every upstream: hands over the bytes,
/// takes hashes back, keeps only what the caller may see, and speaks the ids
/// the web app renders. The previous reverse-face-search sidecar read Immich's
/// database and linked to Immich pages; it retires with Immich.
/// </para>
/// <para>
/// The picture searched can be one the person uploads, or one already in the
/// library - the second is the first with Tessera fetching the bytes itself.
/// </para>
/// </remarks>
public static class FaceEndpoints
{
    /// <summary>Matches per query face worth sending to a browser.</summary>
    private const int MaxMatches = 60;

    public static void MapFaces(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/faces").WithTags("faces").RequireUser();

        g.MapPost("/search", async (
            HttpRequest request, CurrentUser me, ViewableContentRepository content,
            IHttpClientFactory http, CancellationToken ct) =>
        {
            if (!request.HasFormContentType || request.Form.Files.Count == 0)
            {
                return Results.Problem(statusCode: 400, title: "Send one image as multipart/form-data");
            }

            var file = request.Form.Files[0];
            await using var stream = file.OpenReadStream();
            return await SearchAsync(stream, file.FileName, file.ContentType, me, content, http, ct);
        });

        app.MapPost("/api/assets/{id}/similar-faces", async (
            string id, CurrentUser me, ViewableContentRepository content,
            IHttpClientFactory http, CancellationToken ct) =>
        {
            if (await content.ResolveIdAsync(id, ct) is not { } contentId ||
                await content.GetAsync(me.AccountIdOrThrow, contentId, ct) is not { } row ||
                row.AbsolutePath is null || !File.Exists(row.AbsolutePath))
            {
                return Results.NotFound();
            }

            await using var stream = File.OpenRead(row.AbsolutePath);
            return await SearchAsync(stream, Path.GetFileName(row.AbsolutePath), row.Mime, me, content, http, ct);
        }).WithTags("faces").RequireUser();
    }

    private static async Task<IResult> SearchAsync(
        Stream image, string fileName, string? mime,
        CurrentUser me, ViewableContentRepository content, IHttpClientFactory http, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var part = new StreamContent(image);
        part.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
        form.Add(part, "image", fileName);

        HttpResponseMessage res;
        try
        {
            res = await http.CreateClient("facesearch").PostAsync("/api/search", form, ct);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "FaceSearch is not reachable");
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                                       title: $"FaceSearch answered {(int)res.StatusCode}");
            }

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(ct);

            // Every match's hash, resolved once; a hash the caller cannot see
            // simply does not come back.
            var hashes = new List<byte[]>();
            foreach (var face in body.GetProperty("faces").EnumerateArray())
            {
                foreach (var m in face.GetProperty("matches").EnumerateArray())
                {
                    if (m.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String &&
                        Hashing.TryParseSha256(h.GetString(), out var sha))
                    {
                        hashes.Add(sha);
                    }
                }
            }

            var visible = await content.ResolveVisibleHashesAsync(me.AccountIdOrThrow, hashes, ct);

            var faces = body.GetProperty("faces").EnumerateArray().Select(face => new
            {
                box = face.GetProperty("bbox"),
                score = face.TryGetProperty("score", out var sc) ? sc.GetDouble() : 0,
                identity = face.TryGetProperty("identity", out var idn) ? idn : default,
                matches = face.GetProperty("matches").EnumerateArray()
                    .Where(m => m.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String
                                && visible.TryGetValue(h.GetString()!.ToLowerInvariant(), out _))
                    .Take(MaxMatches)
                    .Select(m => new
                    {
                        assetId = ImmichId.From(visible[m.GetProperty("sha256").GetString()!.ToLowerInvariant()]),
                        distance = m.GetProperty("d").GetDouble(),
                        band = m.GetProperty("band").GetString(),
                        album = m.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.String ? al.GetString() : null,
                        person = m.TryGetProperty("person", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null,
                    }),
            });

            return Results.Ok(new
            {
                imageWidth = body.GetProperty("imageWidth").GetInt32(),
                imageHeight = body.GetProperty("imageHeight").GetInt32(),
                bands = body.GetProperty("bands"),
                faces,
            });
        }
    }
}
