using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>
/// Reads a JSON response body as it arrives, never holding all of it, under the deadline the client's
/// <see cref="HttpClient.Timeout"/> sets for the whole request.
/// </summary>
/// <remarks>
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> hands the body over once the headers arrive, and the
/// client's timeout stops there. The time it leaves is given to the body read, and running out of it, a cancelled
/// token and a broken connection each surface as the client reports them when it reads the body itself.
/// </remarks>
internal static class StreamedBody
{
    /// <summary>What <see cref="HttpClient"/> says when its timeout elapses.</summary>
    private const string TimeoutMessage =
        "The request was canceled due to the configured HttpClient.Timeout of {0} seconds elapsing.";

    /// <summary>What <see cref="HttpClient"/> says when the connection fails while it reads a body.</summary>
    private const string CopyErrorMessage = "Error while copying content to a stream.";

    /// <summary>Sends the request and parses its body as it arrives.</summary>
    /// <returns>The body, or <c>default</c> when the response carries none.</returns>
    /// <remarks>
    /// A builder this library did not make gives no client to take a timeout from: its response is read whole
    /// first, as <c>SendAsync</c> reads it, so whatever timeout its send applies still covers the body.
    /// </remarks>
    public static async Task<TValue?> ReadAsync<TValue, TShape>(
        IHttpRequestBuilder builder,
        TShape shape,
        Func<PipeReader, TShape, CancellationToken, ValueTask<TValue?>> parse,
        CancellationToken cancellationToken)
    {
        if (builder is not HttpRequestBuilder own)
        {
            using HttpResponseMessage buffered = await builder
                .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            return await ParseAsync(buffered.Content, shape, parse, cancellationToken).ConfigureAwait(false);
        }

        TimeSpan timeout = own.ClientTimeout;
        long started = Stopwatch.GetTimestamp();

        using HttpResponseMessage response = await builder
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        using CancellationTokenSource? deadline = timeout == Timeout.InfiniteTimeSpan
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);

        deadline?.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);

        try
        {
            return await ParseAsync(response.Content, shape, parse, deadline?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (deadline is not null && !cancellationToken.IsCancellationRequested)
        {
            string message = string.Format(CultureInfo.CurrentCulture, TimeoutMessage, timeout.TotalSeconds);

            throw new TaskCanceledException(
                message, new TimeoutException(exception.Message, exception), exception.CancellationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested && !IsTheCallersCancellation(exception, cancellationToken))
        {
            throw new TaskCanceledException(exception.Message, exception, cancellationToken);
        }
    }

    /// <summary>
    /// Whether a cancellation already reads as <see cref="HttpClient"/> reports the caller's: a
    /// <see cref="TaskCanceledException"/> carrying the caller's token.
    /// </summary>
    /// <remarks>
    /// Which reader notices a cancellation first is a race — the stream's own wait throws a
    /// <see cref="TaskCanceledException"/>, a <c>PipeReader</c> above it a plain <see cref="OperationCanceledException"/>
    /// with the same token — so the type alone, or the token alone, does not say it.
    /// </remarks>
    internal static bool IsTheCallersCancellation(OperationCanceledException exception, CancellationToken cancellationToken) =>
        exception is TaskCanceledException && exception.CancellationToken == cancellationToken;

    /// <summary>Parses a body from its first byte, or answers <c>default</c> when there is none.</summary>
    /// <remarks>Emptiness is the bytes received, never <c>Content-Length</c>, as for a buffered body.</remarks>
    private static async Task<TValue?> ParseAsync<TValue, TShape>(
        HttpContent content,
        TShape shape,
        Func<PipeReader, TShape, CancellationToken, ValueTask<TValue?>> parse,
        CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));

            try
            {
                ReadResult first = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                if (first.Buffer.IsEmpty && first.IsCompleted)
                {
                    return default;
                }

                reader.AdvanceTo(first.Buffer.Start);

                return await parse(reader, shape, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                throw new HttpRequestException(CopyErrorMessage, exception);
            }
            finally
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
