using System.Net;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.HttpEndpoints;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// Covers the streaming terminals: that chunks come out parsed and in order, that
/// noise between them is skipped, and that a failure still carries the body.
/// </summary>
public class HttpStreamingTests
{
    private static (SampleExample Client, RecordingHandler Handler) Arrange(params Canned[] responses)
    {
        var handler = new RecordingHandler(responses);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.example.com")
        };

        return (new SampleExample(client), handler);
    }

    [Fact]
    public async Task Ndjson_YieldsEachLineInOrder()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            {"field":"one"}

            {"field":"two"}
            null
            {"field":"three"}
            """,
            HttpStatusCode.OK));

        var chunks = await client.Stream("api/chat", new Request()).ToListAsync();

        // Blank lines and JSON nulls are stream noise, not data.
        Assert.Equal(["one", "two", "three"], chunks.Select(c => c.Field));
    }

    [Fact]
    public async Task Ndjson_FailureThrowsWithBody()
    {
        var (client, _) = Arrange(Canned.Text(
            """{"error":"model not found"}""", HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() =>
            client.Stream("api/chat", new Request()).ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("model not found", exception.Body);
    }

    [Fact]
    public async Task JsonStream_YieldsArrayElements()
    {
        var (client, _) = Arrange(Canned.Text(
            """[{"field":"one"},null,{"field":"two"}]""", HttpStatusCode.OK));

        var chunks = await client.StreamArray("api/items").ToListAsync();

        Assert.Equal(["one", "two"], chunks.Select(c => c.Field));
    }

    [Fact]
    public async Task JsonStream_FailureThrowsWithBody()
    {
        var (client, _) = Arrange(Canned.Text("overloaded", HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() =>
            client.StreamArray("api/items").ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("overloaded", exception.Body);
    }

    [Fact]
    public async Task Sse_YieldsTheDataOfEachEventInOrder()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            : keep-alive

            event: message
            data: {"field":"one"}

            data: {"field":
            data: "two"}

            """,
            HttpStatusCode.OK));

        var chunks = await client.StreamSse("api/chat", new Request()).ToListAsync();

        Assert.Equal(["one", "two"], chunks.Select(c => c.Field));
    }

    [Fact]
    public async Task Sse_DoneSentinelEndsTheStream()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            data: {"field":"one"}

            data: [DONE]

            data: {"field":"never"}

            """,
            HttpStatusCode.OK));

        var chunks = await client.StreamSse("api/chat", new Request()).ToListAsync();

        Assert.Equal(["one"], chunks.Select(c => c.Field));
    }

    [Fact]
    public async Task Sse_LastEventNeedsNoTrailingSeparator()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            data: {"field":"one"}

            data: {"field":"two"}
            """,
            HttpStatusCode.OK));

        var chunks = await client.StreamSse("api/chat", new Request()).ToListAsync();

        Assert.Equal(["one", "two"], chunks.Select(c => c.Field));
    }

    [Fact]
    public async Task Sse_FailureThrowsWithBody()
    {
        var (client, _) = Arrange(Canned.Text("quota exceeded", HttpStatusCode.TooManyRequests));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() =>
            client.StreamSse("api/chat", new Request()).ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("quota exceeded", exception.Body);
    }

    [Fact]
    public async Task Ndjson_SecondCallInheritsNothingFromTheFirst()
    {
        var (client, handler) = Arrange(
            Canned.Text("""{"field":"one"}""", HttpStatusCode.OK),
            Canned.Text("""[{"field":"two"}]""", HttpStatusCode.OK));

        await client.Stream("first", new Request { Field = "sent" }).ToListAsync();
        await client.StreamArray("second").ToListAsync();

        var second = handler.Requests[1];

        Assert.Equal(HttpMethod.Get, second.Method);
        Assert.Equal("https://www.example.com/second", second.RequestUri?.ToString());
        Assert.Null(handler.Bodies[1]);
    }
}
