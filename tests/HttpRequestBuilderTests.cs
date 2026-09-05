using System.Net;
using System.Text.Json;
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
    /// The options must apply to the body no matter which order they and the body were
    /// given in, because the body is serialized at send time, not at configuration time.
    /// </summary>
    [Fact]
    public async Task JsonOptionsSetBeforeTheBody_ApplyToTheBody()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };
        var pascalCase = new JsonSerializerOptions();

        using var response = await new HttpRequestBuilder(client, HttpMethod.Post, "thing")
            .WithJsonOptions(pascalCase)
            .AsJson(new Plain { Field = "value" })
            .SendAsync();

        Assert.Contains("\"Field\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task JsonOptionsGivenWithTheBody_ApplyToTheBody()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };
        var pascalCase = new JsonSerializerOptions();

        using var response = await new HttpRequestBuilder(client, HttpMethod.Post, "thing")
            .AsJson(new Plain { Field = "value" }, pascalCase)
            .SendAsync();

        Assert.Contains("\"Field\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task JsonBody_DefaultsToWebOptions()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };

        using var response = await new HttpRequestBuilder(client, HttpMethod.Post, "thing")
            .AsJson(new Plain { Field = "value" })
            .SendAsync();

        Assert.Contains("\"field\"", handler.Bodies[0]);
    }

    /// <summary>
    /// A typed client configured for HTTP/2 in AddHttpClient used to be silently
    /// downgraded, because the builder stamped a hardcoded 1.1 onto every message.
    /// </summary>
    [Fact]
    public async Task Version_DefaultsToTheClientConfiguration()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.example.com"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        using var response = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsync();

        Assert.Equal(HttpVersion.Version20, handler.Single.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.Single.VersionPolicy);
    }

    /// <summary>
    /// The failure body is read capped at the stream, so an oversized error response
    /// cannot exhaust memory; the exception carries the first 4096 characters and an
    /// ellipsis.
    /// </summary>
    [Fact]
    public async Task OversizedErrorBody_IsCappedInTheException()
    {
        var (client, _) = Arrange(
            "https://www.example.com",
            Canned.Text(new string('x', 10_000), HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<HttpBuilderException>(() => client.Get("thing"));

        Assert.NotNull(exception.Body);
        Assert.Equal(4097, exception.Body!.Length);
        Assert.EndsWith("…", exception.Body);
    }

    /// <summary>
    /// A chunked response has no Content-Length, so emptiness is judged by the bytes
    /// actually received rather than by the header.
    /// </summary>
    [Fact]
    public async Task EmptyChunkedBody_ReadsAsDefault()
    {
        var handler = new UnsizedEmptyHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };

        Assert.Null(await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsync<Response>());
    }

    /// <summary>
    /// The send disposes the request content, so a second send on the same builder would
    /// reuse what is gone; it fails loudly instead.
    /// </summary>
    [Fact]
    public async Task SecondSendOnTheSameBuilder_Throws()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };
        var builder = new HttpRequestBuilder(client, HttpMethod.Get, "thing");

        using var response = await builder.SendAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.SendAsync());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SecondEnumerationOfAStream_Throws()
    {
        var handler = new RecordingHandler(Canned.Text("""{"field":"one"}""", HttpStatusCode.OK));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.example.com") };
        var stream = new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendAsNdjsonAsync<Response>();

        await stream.ToListAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.ToListAsync().AsTask());
        Assert.Single(handler.Requests);
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

    private sealed class Plain
    {
        public string? Field { get; set; }
    }

    private sealed class UnsizedEmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnsizedEmptyContent()
            });
    }

    private sealed class UnsizedEmptyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
