using System.Net;
using System.Text;
using System.Text.Json;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// The NDJSON terminal reads what a <see cref="StreamReader"/> reading lines read, whatever the bytes and
/// however they arrive: the reader, given the same bytes in the same reads, is the oracle every body is
/// compared with, delivered whole and a few bytes at a time.
/// </summary>
public class NdjsonLinesTests
{
    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];

    public static TheoryData<string, byte[]> Bodies() => new()
    {
        { "empty", [] },
        { "lf", Text("{\"field\":\"one\"}\n{\"field\":\"two\"}\n") },
        { "crlf", Text("{\"field\":\"one\"}\r\n{\"field\":\"two\"}\r\n") },
        { "cr", Text("{\"field\":\"one\"}\r{\"field\":\"two\"}\r") },
        { "no final line end", Text("{\"field\":\"one\"}\n{\"field\":\"two\"}") },
        { "blank and white space lines", Text("\n \t\n{\"field\":\"one\"}\n\v\f\n\u00A0\u2028 \n\u001C\n{\"field\":\"two\"}") },
        { "nulls", Text("null\n{\"field\":\"one\"}\n null \n") },
        { "multi-byte", Text("{\"field\":\"Zürich — 日本 🚀\"}\n{\"field\":\"\\u00fc\"}\n") },
        { "utf-8 mark", [.. Utf8Mark, .. Text("{\"field\":\"one\"}\n")] },
        { "utf-8 mark mid-stream", Text("{\"field\":\"one\"}\n\uFEFF{\"field\":\"two\"}\n") },
        { "invalid utf-8 in a value", [.. Text("{\"field\":\"a"), 0xC0, 0xFF, .. Text("b\"}\n{\"field\":\"two\"}\n")] },
        { "invalid utf-8 cut at a line end", [.. Text("{\"field\":\"a\"}"), 0xE2, .. Text("\n{\"field\":\"two\"}\n")] },
        { "utf-16 little-endian mark", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("{\"field\":\"one\"}\n")] },
        { "utf-16 big-endian mark", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("{\"field\":\"one\"}\n")] },
        { "part of a utf-8 mark", [0xEF, 0xBB] },
        { "not quite a utf-8 mark", [0xEF, 0xBB, .. Text("{\"field\":\"one\"}\n")] },
        { "a long line", Text($"{{\"field\":\"{new string('x', 20_000)}\"}}\n{{\"field\":\"two\"}}\n") },
        { "malformed after two", Text("{\"field\":\"one\"}\n{\"field\":\"two\"}\n{\"field\":\n{\"field\":\"never\"}\n") },
        { "numbers where a string is due", Text("{\"field\":\"one\"}\n{\"field\":2}\n") }
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task SendAsNdjsonAsync_AnyBody_ReadsAsAStreamReaderDid(string body, byte[] bytes)
    {
        foreach (var chunk in new[] { int.MaxValue, 1, 3, 7 })
        {
            var expected = await ReadAsBeforeAsync(bytes, chunk);
            var reflected = await ReadAsync(bytes, chunk, builder => builder.SendAsNdjsonAsync<Response>());
            var generated = await ReadAsync(bytes, chunk, builder => builder.SendAsNdjsonAsync(TestJson.Default.Response));

            Assert.True(expected == reflected, $"{body}, {chunk} bytes a read: {reflected} instead of {expected}");
            Assert.True(expected == generated, $"{body}, {chunk} bytes a read, typed: {generated} instead of {expected}");
        }
    }

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<string> ReadAsync(
        byte[] bytes, int chunk, Func<HttpRequestBuilder, IAsyncEnumerable<Response>> read)
    {
        using var client = new HttpClient(new Trickling(bytes, chunk)) { BaseAddress = new Uri("https://www.example.com") };
        var fields = new List<string?>();

        try
        {
            await foreach (var value in read(new HttpRequestBuilder(client, HttpMethod.Get, "lines")))
            {
                fields.Add(value.Field);
            }
        }
        catch (Exception failure)
        {
            return $"{string.Join('|', fields)} then {failure.GetType().Name}: {failure.Message}";
        }

        return string.Join('|', fields);
    }

    private static async Task<string> ReadAsBeforeAsync(byte[] bytes, int chunk)
    {
        using var reader = new StreamReader(new Chunks(bytes, chunk), Encoding.UTF8);
        var fields = new List<string?>();

        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (JsonSerializer.Deserialize<Response>(line, JsonSerializerOptions.Web) is { } value)
                {
                    fields.Add(value.Field);
                }
            }
        }
        catch (Exception failure)
        {
            return $"{string.Join('|', fields)} then {failure.GetType().Name}: {failure.Message}";
        }

        return string.Join('|', fields);
    }

    /// <summary>Answers with the body, handed out at most <c>chunk</c> bytes a read, as a slow socket would.</summary>
    private sealed class Trickling(byte[] body, int chunk) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new Chunks(body, chunk)),
                RequestMessage = request
            });
    }

    private sealed class Chunks(byte[] body, int chunk) : MemoryStream(body)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, chunk));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, chunk), cancellationToken);
    }
}
