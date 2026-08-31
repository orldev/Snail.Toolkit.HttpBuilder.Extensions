using System.Net;
using System.Text.Json;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

/// <summary>A response to replay, described rather than pre-built.</summary>
/// <param name="StatusCode">Status to return.</param>
/// <param name="Body">Body text, or null for a response with no content at all.</param>
public sealed record Canned(HttpStatusCode StatusCode, string? Body)
{
    /// <summary>A JSON response.</summary>
    /// <param name="body">Object to serialize as the body.</param>
    /// <param name="statusCode">Status to return.</param>
    public static Canned Json(object? body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode, JsonSerializer.Serialize(body));

    /// <summary>A plain-text response, for error bodies.</summary>
    /// <param name="body">Body text.</param>
    /// <param name="statusCode">Status to return.</param>
    public static Canned Text(string body, HttpStatusCode statusCode) => new(statusCode, body);

    /// <summary>A response with an empty body.</summary>
    /// <param name="statusCode">Status to return.</param>
    public static Canned Empty(HttpStatusCode statusCode = HttpStatusCode.NoContent) =>
        new(statusCode, string.Empty);

    /// <summary>Builds a fresh message. One per send — the builder disposes what it reads.</summary>
    internal HttpResponseMessage ToMessage() => new(StatusCode)
    {
        Content = Body is null ? new StringContent(string.Empty) : new StringContent(Body)
    };
}

/// <summary>
/// Captures what the builder actually put on the wire and replays canned responses.
/// </summary>
/// <remarks>
/// Bodies are read as the request passes through, because the content is disposed with
/// the request message and cannot be read afterwards.
/// </remarks>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Canned[] _responses;
    private int _sent;

    /// <summary>Initializes a handler that replays the given responses in order.</summary>
    /// <param name="responses">
    /// Responses to return. The last one repeats once they run out, so a test that
    /// only cares about the request can pass none at all.
    /// </param>
    public RecordingHandler(params Canned[] responses) =>
        _responses = responses.Length > 0 ? responses : [Canned.Json(new { })];

    /// <summary>Gets the requests that were sent, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Gets the request bodies, in order. Null where a request had no body.</summary>
    public List<string?> Bodies { get; } = [];

    /// <summary>Gets the single request that was sent, failing if there was not exactly one.</summary>
    public HttpRequestMessage Single => Assert.Single(Requests);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken));

        var canned = _responses[Math.Min(_sent++, _responses.Length - 1)];

        return canned.ToMessage();
    }
}
