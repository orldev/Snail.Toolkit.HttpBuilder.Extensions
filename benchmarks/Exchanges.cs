using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.HttpBuilder.Extensions.Benchmarks;

/// <summary>
/// Measures the per-request paths every client built on this library takes: a request assembled with a query and a
/// JSON body, a buffered typed response of 200 items and one of 6000 (some 600 KB, a large list endpoint), a
/// newline-delimited JSON stream read item by item, and a server-sent event stream read event by event.
/// </summary>
/// <remarks>
/// The handler answers from memory, so the numbers are the library's own work. Read the Allocated column: the times
/// move with the machine, the bytes do not.
/// </remarks>
[MemoryDiagnoser]
public class Exchanges
{
    private const int Count = 200;

    private const int LargeCount = 6000;

    private static readonly MediaTypeHeaderValue Json = new("application/json");

    private HttpClient _document = null!;
    private HttpClient _largeDocument = null!;
    private HttpClient _lines = null!;
    private HttpClient _events = null!;
    private HttpClient _empty = null!;
    private Item _order = null!;

    /// <summary>
    /// Prepares a client per answer: a JSON array, a large one, the same items as lines and as events, and an empty
    /// body; then checks that every read path gives back exactly the items it was sent.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        var items = Enumerable.Range(1, Count).Select(Item.Numbered).ToArray();
        var document = JsonSerializer.SerializeToUtf8Bytes(items, JsonSerializerOptions.Web);
        var lines = Encoding.UTF8.GetBytes(
            string.Join('\n', items.Select(item => JsonSerializer.Serialize(item, JsonSerializerOptions.Web))));

        var largeDocument = JsonSerializer.SerializeToUtf8Bytes(
            Enumerable.Range(1, LargeCount).Select(Item.Numbered).ToArray(), JsonSerializerOptions.Web);
        var events = Encoding.UTF8.GetBytes(string.Concat(items.Select(item =>
            $"event: item\nid: {item.Id}\ndata: {JsonSerializer.Serialize(item, JsonSerializerOptions.Web)}\n\n")));

        _document = Client(() => Answer(document));
        _largeDocument = Client(() => Answer(largeDocument));
        _lines = Client(() => Answer(lines));
        _events = Client(() => Answer(events));
        _empty = Client(() => new ByteArrayContent([]));
        _order = Item.Numbered(42);

        await VerifyAsync(items).ConfigureAwait(false);
    }

    /// <summary>Releases the clients.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _document.Dispose();
        _largeDocument.Dispose();
        _lines.Dispose();
        _events.Dispose();
        _empty.Dispose();
    }

    /// <summary>Sends a POST with two query parameters, a header and a JSON body.</summary>
    [Benchmark]
    public async Task<int> Request()
    {
        using var response = await new HttpRequestBuilder(_empty, HttpMethod.Post, "orders")
            .Query("tenant", "eu-1")
            .Query("page", "2")
            .Header("X-Request-Id", "bench")
            .AsJson(_order)
            .SendAsync()
            .ConfigureAwait(false);

        return (int)response.StatusCode;
    }

    /// <summary>Reads a buffered response of 200 items into an array.</summary>
    [Benchmark]
    public async Task<int> Document()
    {
        var items = await new HttpRequestBuilder(_document, HttpMethod.Get, "items")
            .SendAsync<Item[]>()
            .ConfigureAwait(false);

        return items?.Length ?? 0;
    }

    /// <summary>Reads a buffered response of 6000 items into an array.</summary>
    [Benchmark]
    public async Task<int> LargeDocument()
    {
        var items = await new HttpRequestBuilder(_largeDocument, HttpMethod.Get, "items")
            .SendAsync<Item[]>()
            .ConfigureAwait(false);

        return items?.Length ?? 0;
    }

    /// <summary>Streams 200 items, one per line.</summary>
    [Benchmark]
    public async Task<int> Lines()
    {
        var read = 0;

        await foreach (var _ in new HttpRequestBuilder(_lines, HttpMethod.Get, "items")
                           .SendAsNdjsonAsync<Item>()
                           .ConfigureAwait(false))
            read++;

        return read;
    }

    /// <summary>Streams 200 items as server-sent events, each with a name and an id.</summary>
    [Benchmark]
    public async Task<int> Events()
    {
        var read = 0;

        await foreach (var _ in new HttpRequestBuilder(_events, HttpMethod.Get, "items")
                           .SendAsSseEventsAsync<Item>()
                           .ConfigureAwait(false))
            read++;

        return read;
    }

    private async Task VerifyAsync(Item[] items)
    {
        var expected = JsonSerializer.Serialize(items, JsonSerializerOptions.Web);
        var largeExpected = JsonSerializer.Serialize(
            Enumerable.Range(1, LargeCount).Select(Item.Numbered).ToArray(), JsonSerializerOptions.Web);
        var document = await new HttpRequestBuilder(_document, HttpMethod.Get, "items").SendAsync<Item[]>().ConfigureAwait(false);
        var largeDocument = await new HttpRequestBuilder(_largeDocument, HttpMethod.Get, "items").SendAsync<Item[]>().ConfigureAwait(false);
        var lines = await new HttpRequestBuilder(_lines, HttpMethod.Get, "items").SendAsNdjsonAsync<Item>().ToArrayAsync().ConfigureAwait(false);
        var events = await new HttpRequestBuilder(_events, HttpMethod.Get, "items").SendAsSseEventsAsync<Item>().ToArrayAsync().ConfigureAwait(false);

        string[] read =
        [
            JsonSerializer.Serialize(document, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(lines, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(events.Select(received => received.Data), JsonSerializerOptions.Web)
        ];

        var isNamed = events.All(received => received is { Event: "item" } && received.Id == received.Data.Id.ToString(CultureInfo.InvariantCulture));

        if (Array.Exists(read, text => text != expected)
            || JsonSerializer.Serialize(largeDocument, JsonSerializerOptions.Web) != largeExpected
            || !isNamed)
        {
            throw new InvalidOperationException("A read path did not give back the items it was sent.");
        }
    }

    private static HttpClient Client(Func<HttpContent> answer) =>
        new(new CannedHandler(answer)) { BaseAddress = new Uri("http://localhost/") };

    private static ByteArrayContent Answer(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = Json;

        return content;
    }
}

/// <summary>An item of a catalogue, the shape a typical API answers with.</summary>
public sealed record Item(int Id, string Name, decimal Price, string[] Tags)
{
    /// <summary>The item with the given number.</summary>
    public static Item Numbered(int id) => new(id, $"Item number {id}", id * 1.25m, ["catalogue", "stock"]);
}
