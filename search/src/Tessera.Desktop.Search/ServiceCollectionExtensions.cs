using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Tessera.Desktop.Search;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IImageEmbedder"/> (SigLIP2 on ONNX Runtime) and
    /// <see cref="IVectorIndex"/> (brute-force int8 cosine) as singletons.
    /// The model is not loaded here; the host awaits
    /// <see cref="IImageEmbedder.EnsureReadyAsync"/> when it wants it.
    /// </summary>
    public static IServiceCollection AddTesseraSearch(this IServiceCollection services, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ModelsDir))
            throw new ArgumentException("SearchOptions.ModelsDir is required.", nameof(options));

        ModelManifest manifest = ModelManifest.Embedded();
        if (!string.Equals(manifest.ModelId, options.ModelId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"This build ships only '{manifest.ModelId}'; SearchOptions.ModelId is '{options.ModelId}'.", nameof(options));

        services.TryAddSingleton(options);
        services.TryAddSingleton(manifest);
        services.TryAddSingleton(sp => new ModelStore(
            sp.GetRequiredService<SearchOptions>(), null, sp.GetService<ILogger<ModelStore>>()));
        services.TryAddSingleton(sp => new SiglipEmbedder(
            sp.GetRequiredService<SearchOptions>(), sp.GetRequiredService<ModelStore>(),
            sp.GetService<ILogger<SiglipEmbedder>>(), sp.GetRequiredService<ModelManifest>()));
        services.TryAddSingleton<IImageEmbedder>(sp => sp.GetRequiredService<SiglipEmbedder>());
        services.TryAddSingleton(sp => new BruteForceVectorIndex(sp.GetRequiredService<ModelManifest>().Dimensions));
        services.TryAddSingleton<IVectorIndex>(sp => sp.GetRequiredService<BruteForceVectorIndex>());
        return services;
    }
}
