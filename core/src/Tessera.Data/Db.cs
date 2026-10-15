using Microsoft.Extensions.Options;
using Npgsql;

namespace Tessera.Data;

public sealed class TesseraDbOptions
{
    public const string Section = "ConnectionStrings";
    public string Tessera { get; set; } = "";
}

/// <summary>
/// Hands out connections from one pooled data source.
/// </summary>
/// <remarks>
/// A single <see cref="NpgsqlDataSource"/> for the process: it owns the pool,
/// prepares statements across connections, and is the only thing that should
/// ever parse the connection string. Creating connections ad hoc from a string
/// is how a service ends up with several pools and a confusing connection
/// ceiling.
/// </remarks>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public Db(IOptions<TesseraDbOptions> options)
    {
        var cs = options.Value.Tessera;
        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Tessera is not configured.");
        }

        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.EnableDynamicJson();
        _source = builder.Build();
    }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct = default) =>
        _source.OpenConnectionAsync(ct);

    /// <summary>For binary COPY, which is the only sane way to load millions of rows.</summary>
    public NpgsqlDataSource Source => _source;

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
