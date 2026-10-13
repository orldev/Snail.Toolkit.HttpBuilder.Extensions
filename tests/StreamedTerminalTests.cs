using System.Net;
using System.Text;
using Moq;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests;

/// <summary>
/// The streamed terminal reads the body as it arrives, and must answer as the buffered one does: the same values,
/// and the same exceptions when the client's timeout elapses, the token is cancelled or the connection breaks while
/// the body is read.
/// </summary>
public class StreamedTerminalTests
{
    private static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout) =>
        new(handler) { BaseAddress = new Uri("https://www.example.com"), Timeout = timeout };

    private static HttpRequestBuilder Get(HttpClient client) => new(client, HttpMethod.Get, "thing");

    [Theory]
    [InlineData("""{"field":"one"}""")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData(" ")]
    [InlineData("""{"field":""")]
    [InlineData("""{"field":"one"} trailing""")]
    public async Task SendStreamedAsync_AnyBody_AnswersAsSendAsync(string body)
    {
        using var client = Client(new RecordingHandler(Canned.Text(body, HttpStatusCode.OK)), TimeSpan.FromSeconds(10));

        var buffered = await Outcome(() => Get(client).SendAsync<Response>());
        var streamed = await Outcome(() => Get(client).SendStreamedAsync<Response>(CancellationToken.None));
        var typed = await Outcome(() => Get(client).SendStreamedAsync(TestJson.Default.Response, CancellationToken.None));

        Assert.Equal(buffered, streamed);
        Assert.Equal(buffered, typed);
    }

    [Fact]
    public async Task SendStreamedAsync_FailureStatus_ThrowsWithTheBody()
    {
        using var client = Client(new RecordingHandler(Canned.Text("nope", HttpStatusCode.BadRequest)), TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => Get(client).SendStreamedAsync<Response>(CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal("nope", failure.Body);
    }

    [Fact]
    public async Task SendStreamedAsync_TimeoutWhileTheBodyStalls_ThrowsWhatSendAsyncThrows()
    {
        using var client = Client(new StreamingHandler(TimeSpan.Zero, () => ScriptedBody.Stalling()), TimeSpan.FromMilliseconds(200));

        var buffered = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendAsync<Response>());
        var streamed = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendStreamedAsync<Response>(CancellationToken.None));
        var typed = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendStreamedAsync(TestJson.Default.Response, CancellationToken.None));

        Assert.IsType<TaskCanceledException>(buffered);
        Assert.IsType<TimeoutException>(buffered.InnerException);
        Assert.Equal(Describe(buffered), Describe(streamed));
        Assert.Equal(Describe(buffered), Describe(typed));
    }

    /// <summary>
    /// The timeout bounds the whole request: the time the headers took is not given to the body again. Headers after
    /// 300 ms and the rest of the body 450 ms later miss a 600 ms timeout, which a body read granted the full
    /// timeout would meet.
    /// </summary>
    [Fact]
    public async Task SendStreamedAsync_HeadersAndBodyTogetherOverTheTimeout_TimesOut()
    {
        using var client = Client(
            new StreamingHandler(TimeSpan.FromMilliseconds(300), () => ScriptedBody.Delayed(TimeSpan.FromMilliseconds(450))),
            TimeSpan.FromMilliseconds(600));

        var buffered = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendAsync<Response>());
        var streamed = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendStreamedAsync<Response>(CancellationToken.None));

        Assert.Equal(Describe(buffered), Describe(streamed));
    }

    [Fact]
    public async Task SendStreamedAsync_SlowBodyWithinTheTimeout_ReadsIt()
    {
        using var client = Client(
            new StreamingHandler(TimeSpan.Zero, () => ScriptedBody.Delayed(TimeSpan.FromMilliseconds(50))),
            TimeSpan.FromSeconds(10));

        var streamed = await Get(client).SendStreamedAsync<Response>(CancellationToken.None);

        Assert.Equal("one", streamed?.Field);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendStreamedAsync_CancelledWhileTheBodyStalls_ThrowsWhatSendAsyncThrows(bool hasClientTimeout)
    {
        using var client = Client(
            new StreamingHandler(TimeSpan.Zero, () => ScriptedBody.Stalling()),
            hasClientTimeout ? TimeSpan.FromSeconds(10) : Timeout.InfiniteTimeSpan);

        using var bufferedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var streamedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var buffered = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(client).SendAsync<Response>(bufferedCancel.Token));
        var streamed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(client).SendStreamedAsync<Response>(streamedCancel.Token));

        Assert.Equal(bufferedCancel.Token, buffered.CancellationToken);
        Assert.Equal(streamedCancel.Token, streamed.CancellationToken);
        Assert.Equal(buffered.GetType(), streamed.GetType());
    }

    [Fact]
    public async Task SendStreamedAsync_ConnectionBreaksInTheBody_ThrowsWhatSendAsyncThrows()
    {
        using var client = Client(new StreamingHandler(TimeSpan.Zero, () => ScriptedBody.Breaking()), TimeSpan.FromSeconds(10));

        var buffered = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendAsync<Response>());
        var streamed = await Assert.ThrowsAnyAsync<Exception>(() => Get(client).SendStreamedAsync<Response>(CancellationToken.None));

        Assert.IsType<HttpRequestException>(buffered);
        Assert.Equal(Describe(buffered), Describe(streamed));
    }

    /// <summary>
    /// A builder the library did not make gives no client timeout to hold the body to, so its response is asked for
    /// whole, and whatever timeout its send applies covers the body.
    /// </summary>
    [Fact]
    public async Task SendStreamedAsync_ForeignBuilder_AsksForTheWholeResponse()
    {
        var builder = new Mock<IHttpRequestBuilder>();
        builder
            .Setup(foreign => foreign.SendCheckedAsync(It.IsAny<HttpCompletionOption>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"field":"one"}""") });

        var streamed = await builder.Object.SendStreamedAsync(TestJson.Default.Response, CancellationToken.None);

        Assert.Equal("one", streamed?.Field);
        builder.Verify(foreign => foreign.SendCheckedAsync(HttpCompletionOption.ResponseContentRead, CancellationToken.None));
    }

    [Fact]
    public async Task SendStreamedAsync_NullTypeInfo_Throws()
    {
        using var client = Client(new RecordingHandler(), TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<ArgumentNullException>(() => Get(client).SendStreamedAsync<Response>(typeInfo: null!, CancellationToken.None));
    }

    private static async Task<string> Outcome(Func<Task<Response?>> read)
    {
        try
        {
            var value = await read();

            return value is null ? "null" : $"value {value.Field}";
        }
        catch (Exception exception)
        {
            return exception.GetType().FullName!;
        }
    }

    /// <summary>
    /// The exception a caller sees and the cause it names: their types and the outer message. What lies under the
    /// cause is the reading machinery's own, and is not compared.
    /// </summary>
    private static string Describe(Exception exception) =>
        $"{exception.GetType().FullName}: {exception.Message} | {exception.InnerException?.GetType().FullName}";

    /// <summary>Answers after a delay with a body the test scripts.</summary>
    private sealed class StreamingHandler(TimeSpan headersAfter, Func<Stream> body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(headersAfter, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) };
        }
    }

    /// <summary>
    /// A body that sends its first byte at once and then stalls, waits, or breaks, as a slow or failing server
    /// would.
    /// </summary>
    private sealed class ScriptedBody : Stream
    {
        private readonly byte[] _body = Encoding.UTF8.GetBytes("""{"field":"one"}""");
        private readonly Func<CancellationToken, Task> _beforeTheRest;
        private int _position;

        private ScriptedBody(Func<CancellationToken, Task> beforeTheRest) => _beforeTheRest = beforeTheRest;

        public static ScriptedBody Stalling() => new(token => Task.Delay(Timeout.Infinite, token));

        public static ScriptedBody Delayed(TimeSpan delay) => new(token => Task.Delay(delay, token));

        public static ScriptedBody Breaking() => new(_ => Task.FromException(new IOException("The connection was reset.")));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == 1)
            {
                await _beforeTheRest(cancellationToken);
            }

            var count = _position == 0 ? 1 : Math.Min(buffer.Length, _body.Length - _position);

            _body.AsMemory(_position, count).CopyTo(buffer);
            _position += count;

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
