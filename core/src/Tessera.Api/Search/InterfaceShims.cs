using SkiaSharp;
using Tessera.Desktop.Search;

namespace Tessera.Api.Search;

/// <summary>
/// Two methods the coordinator is adding to the frozen interfaces
/// (<c>IImageEmbedder.EmbedImages</c>, <c>IVectorIndex.TryGet</c>), reached
/// through extension methods until they land.
/// </summary>
/// <remarks>
/// The call sites are written exactly as they will read against the
/// interface: <c>embedder.EmbedImages(list)</c>, <c>index.TryGet(id, out v)</c>.
/// An instance method wins over an extension method, so the moment the
/// interface gains them the calls bind there and this file is dead code to
/// delete. Until then the fakes answer directly and anything else degrades
/// to the one-at-a-time call or to "not held".
/// </remarks>
internal static class InterfaceShims
{
    public static float[][] EmbedImages(this IImageEmbedder embedder, IReadOnlyList<SKBitmap> bitmaps)
    {
        if (embedder is FakeImageEmbedder fake)
        {
            return fake.EmbedImages(bitmaps);
        }

        var vectors = new float[bitmaps.Count][];
        for (var i = 0; i < bitmaps.Count; i++)
        {
            vectors[i] = embedder.EmbedImage(bitmaps[i]);
        }

        return vectors;
    }

    public static bool TryGet(this IVectorIndex index, long id, out float[] vector)
    {
        if (index is InMemoryVectorIndex memory)
        {
            return memory.TryGet(id, out vector);
        }

        vector = [];
        return false;
    }
}
