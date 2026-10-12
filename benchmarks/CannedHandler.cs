using System.Net;

namespace Snail.Toolkit.HttpBuilder.Extensions.Benchmarks;

/// <summary>
/// Answers every request with a fresh copy of canned content, reading the request body first as a server would.
/// </summary>
/// <remarks>No socket is opened: what is measured is the library on both sides of the handler.</remarks>
internal sealed class CannedHandler(Func<HttpContent> answer) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is { } body)
            await body.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = answer(), RequestMessage = request };
    }
}
