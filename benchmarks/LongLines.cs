using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.HttpBuilder.Extensions.Benchmarks;

/// <summary>
/// Measures one newline-delimited JSON line of some 4 MB — 50 000 items in one array, the shape of a batched
/// embeddings answer — read through the NDJSON terminal.
/// </summary>
/// <remarks>
/// The body arrives in the pipe's 4 KB reads, so the cost of finding the line's end shows here and not in
/// <see cref="Exchanges.Lines"/>, whose lines are short. Searched from the start on every read, this line took
/// some 25 times its parse.
/// </remarks>
[MemoryDiagnoser]
public class LongLines
{
    private const int Count = 50_000;

    private HttpClient _line = null!;

    /// <summary>Prepares the client answering with the one long line, and checks that it reads back whole.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var items = Enumerable.Range(1, Count).Select(Item.Numbered).ToArray();
        var line = JsonSerializer.SerializeToUtf8Bytes(items, ExchangeJson.Default.ItemArray);

        _line = new HttpClient(new CannedHandler(() => new ByteArrayContent(line))) { BaseAddress = new Uri("http://localhost/") };

        var read = await Line().ConfigureAwait(false);

        if (read != Count)
        {
            throw new InvalidOperationException($"The line read back {read} items of {Count}.");
        }
    }

    /// <summary>Releases the client.</summary>
    [GlobalCleanup]
    public void Cleanup() => _line.Dispose();

    /// <summary>Reads the one line of 50 000 items with source-generated metadata.</summary>
    [Benchmark]
    public async Task<int> Line()
    {
        var read = 0;

        await foreach (var batch in new HttpRequestBuilder(_line, HttpMethod.Get, "embeddings")
                           .SendAsNdjsonAsync(ExchangeJson.Default.ItemArray, CancellationToken.None)
                           .ConfigureAwait(false))
        {
            read += batch.Length;
        }

        return read;
    }
}
