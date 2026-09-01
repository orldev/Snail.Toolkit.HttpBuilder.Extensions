using System.Net;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.HttpEndpoints;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// Covers the behaviour the builder is relied on for beyond assembling a single
/// message: that requests are independent, and that URIs and failures are handled.
/// </summary>
public class HttpRequestBuilderTests
{
    private static (SampleExample Client, RecordingHandler Handler) Arrange(
        string? baseAddress = "https://www.example.com",
        params Canned[] responses)
    {
        var handler = new RecordingHandler(responses);
        var client = new HttpClient(handler)
        {
            BaseAddress = baseAddress is null ? null : new Uri(baseAddress)
        };

        return (new SampleExample(client), handler);
    }

    /// <summary>
    /// The reason the builder was reshaped: state used to live on the client, so a
    /// body or header set by one call was still attached to the next one.
    /// </summary>
    [Fact]
    public async Task SecondCall_InheritsNothingFromTheFirst()
    {
        var (client, handler) = Arrange();

        await client.Post("first", new Request { Field = "sent" }, header: "one");
        await client.Get("second");

        Assert.Equal(2, handler.Requests.Count);

        var second = handler.Requests[1];

        Assert.Equal(HttpMethod.Get, second.Method);
        Assert.Equal("https://www.example.com/second", second.RequestUri?.ToString());
        Assert.Null(second.Content);
        Assert.Null(handler.Bodies[1]);
        Assert.False(second.Headers.Contains("X-Sample"));
    }

    [Fact]
    public async Task Query_IsAppendedAndEscaped()
    {
        var (client, handler) = Arrange();

        await client.Search("search", new Dictionary<string, string?>
        {
            ["q"] = "a b&c",
            ["page"] = "2"
        });

        // AbsoluteUri, not ToString(): the latter unescapes for display, so it would
        // hide whether the space was actually escaped on the wire.
        Assert.Equal(
            "https://www.example.com/search?q=a%20b%26c&page=2",
            handler.Single.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Query_SkipsNullValues()
    {
        var (client, handler) = Arrange();

        await client.Search("search", new Dictionary<string, string?>
        {
            ["kept"] = "",
            ["skipped"] = null
        });

        Assert.Equal("https://www.example.com/search?kept=", handler.Single.RequestUri?.ToString());
    }

    /// <summary>
    /// A base address with a path used to lose it, because the path was assigned to a
    /// <see cref="UriBuilder"/> rather than appended.
    /// </summary>
    [Fact]
    public async Task RelativePath_KeepsThePathOnTheBaseAddress()
    {
        var (client, handler) = Arrange("https://www.example.com/crm/v8/");

        await client.Get("Keynex");

        Assert.Equal("https://www.example.com/crm/v8/Keynex", handler.Single.RequestUri?.ToString());
    }

    [Fact]
    public async Task RootRelativePath_ReplacesThePathOnTheBaseAddress()
    {
        var (client, handler) = Arrange("https://www.example.com/crm/v8/");

        await client.Get("/ping");

        Assert.Equal("https://www.example.com/ping", handler.Single.RequestUri?.ToString());
    }

    [Fact]
    public async Task AbsoluteUrl_IsSentAsGiven()
    {
        var (client, handler) = Arrange("https://www.example.com/crm/");

        await client.Get("https://other.example.org/v2/thing");

        Assert.Equal("https://other.example.org/v2/thing", handler.Single.RequestUri?.ToString());
    }

    /// <summary>
    /// Google's APIs use paths like <c>v1/places:autocomplete</c>. Relative-URI parsing
    /// can read the segment before the colon as a scheme, so the path is joined by hand.
    /// </summary>
    [Fact]
    public async Task ColonInPath_IsNotMistakenForAScheme()
    {
        var (client, handler) = Arrange("https://places.googleapis.com");

        await client.Get("v1/places:autocomplete");

        Assert.Equal(
            "https://places.googleapis.com/v1/places:autocomplete",
            handler.Single.RequestUri?.ToString());
    }

    [Fact]
    public async Task RelativePathWithoutBaseAddress_Throws()
    {
        var (client, _) = Arrange(baseAddress: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Get("relative"));

        Assert.Contains("BaseAddress", exception.Message);
    }

    /// <summary>
    /// A failure response used to be deserialized as if it had succeeded, turning a
    /// clear "400, here is why" into an opaque JSON error.
    /// </summary>
    [Fact]
    public async Task FailureStatus_ThrowsCarryingStatusAndBody()
    {
        var (client, _) = Arrange(
            "https://www.example.com",
            Canned.Text("{\"error\":\"key invalid\"}", HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() => client.Get("thing"));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("{\"error\":\"key invalid\"}", exception.Body);
        Assert.Equal(HttpMethod.Get, exception.Method);
        Assert.Equal("https://www.example.com/thing", exception.RequestUri?.ToString());
        Assert.Contains("key invalid", exception.Message);
    }

    [Fact]
    public async Task FailureStatus_IsCatchableAsHttpRequestException()
    {
        var (client, _) = Arrange(
            "https://www.example.com",
            Canned.Text("nope", HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpBuilderException>(async () =>
        {
            try
            {
                await client.Get("thing");
            }
            catch (HttpRequestException)
            {
                throw;
            }
        });
    }

    [Fact]
    public async Task NoContentResponse_ReturnsDefaultRatherThanThrowing()
    {
        var (client, _) = Arrange("https://www.example.com", Canned.Empty());

        Assert.Null(await client.Get("thing"));
    }

    [Fact]
    public async Task RawSend_DoesNotThrowOnFailureStatus()
    {
        var (client, _) = Arrange(
            "https://www.example.com",
            Canned.Text("nope", HttpStatusCode.NotFound));

        using var response = await client.Run(HttpMethod.Get, "thing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Content headers are rejected by the request header collection, so they are
    /// routed to the body rather than throwing.
    /// </summary>
    [Fact]
    public async Task ContentTypeHeader_IsAppliedToTheBody()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };

        await new ContentTypeClient(client).Send();

        Assert.Equal(
            "application/vnd.api+json",
            handler.Single.Content?.Headers.ContentType?.MediaType);
    }

    private sealed class ContentTypeClient(HttpClient client) : TypedHttpClientBase(client)
    {
        public Task<Response?> Send() =>
            Post("thing")
                .AsJson(new Request { Field = "x" })
                .Header("Content-Type", "application/vnd.api+json")
                .SendAsync<Response>();
    }
}
