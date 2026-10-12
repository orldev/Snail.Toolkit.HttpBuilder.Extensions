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

    /// <remarks>Blank lines and JSON nulls are stream noise, not data.</remarks>
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
        Assert.Contains("model not found", exception.Body, StringComparison.Ordinal);
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
        Assert.Contains("overloaded", exception.Body, StringComparison.Ordinal);
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
    public async Task SseEvents_CarryEventTypeAndLastId()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            id: 1
            event: delta
            data: {"field":"one"}

            data: {"field":"two"}

            """,
            HttpStatusCode.OK));

        var events = await client.StreamSseEvents("api/chat").ToListAsync();

        Assert.Equal(["delta", null], events.Select(e => e.Event));
        Assert.Equal(["1", "1"], events.Select(e => e.Id));
        Assert.Equal(["one", "two"], events.Select(e => e.Data.Field));
    }

    /// <summary>
    /// The wire format's corners: a field with no colon has an empty value, the space after
    /// the colon is optional and only one is removed, and a value may hold more colons.
    /// </summary>
    [Fact]
    public async Task SseEvents_FieldsWithoutColonOrSpace_ReadAsTheSpecificationSays()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            event:tick:tock
            id
            data:{"field":
            data
            data:  "x"}

            """,
            HttpStatusCode.OK));

        var received = Assert.Single(await client.StreamSseEvents("api/chat").ToListAsync());

        Assert.Equal("tick:tock", received.Event);
        Assert.Equal(string.Empty, received.Id);
        Assert.Equal("x", received.Data.Field);
    }

    [Fact]
    public async Task Streaming_CancellationStopsTheStream()
    {
        var (client, _) = Arrange(Canned.Text(
            """
            {"field":"one"}
            {"field":"two"}
            {"field":"three"}
            """,
            HttpStatusCode.OK));

        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.Stream("api/chat", new Request(), cancellation.Token))
            {
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task Sse_FailureThrowsWithBody()
    {
        var (client, _) = Arrange(Canned.Text("quota exceeded", HttpStatusCode.TooManyRequests));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() =>
            client.StreamSse("api/chat", new Request()).ToListAsync().AsTask());

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("quota exceeded", exception.Body, StringComparison.Ordinal);
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
