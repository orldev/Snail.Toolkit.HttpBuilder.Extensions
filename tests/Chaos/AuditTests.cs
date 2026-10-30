using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Chaos;

/// <summary>
/// The crash tests of the 2026-10-30 audit, kept as regression tests: header values and query parameters refused at
/// the call, secrets kept out of messages and the shared pool, parallel stalled failures, the longest timeout.
/// </summary>
public class AuditTests
{
    private static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout, string baseAddress = "https://www.example.com") =>
        new(handler) { BaseAddress = new Uri(baseAddress), Timeout = timeout };

    /// <summary>
    /// The message leaves the query out so that a key stays out of logs; built with <c>GetLeftPart(UriPartial.Path)</c>
    /// it kept the user information, and a base address with <c>user:password@</c> wrote the password into every
    /// log line of every failure.
    /// </summary>
    [Fact]
    public async Task SendAsync_FailureThroughABaseAddressWithCredentials_KeepsThemOutOfTheMessage()
    {
        using var client = Client(
            new RecordingHandler(Canned.Text("denied", HttpStatusCode.Forbidden)),
            TimeSpan.FromSeconds(10),
            "https://robot:hunter2@www.example.com/api/");

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => new HttpRequestBuilder(client, HttpMethod.Get, "things")
            .SendAsync<Response>());

        Assert.DoesNotContain("hunter2", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A header value goes to <c>TryAddWithoutValidation</c>, and <c>"fine\r\nX-Injected: yes"</c> once reached the
    /// socket as two headers. A line break must never put a header of its own on the wire: the request is read back
    /// from a real socket, which a recording handler cannot show.
    /// </summary>
    [Fact]
    public async Task Header_ValueWithALineBreak_NeverInjectsAHeaderOnTheWire()
    {
        await using var server = await LoopbackServer.StartAsync();
        using var client = Client(new SocketsHttpHandler(), TimeSpan.FromSeconds(10), server.Address);

        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await new HttpRequestBuilder(client, HttpMethod.Get, "things")
                .Header("X-Note", "fine\r\nX-Injected: yes")
                .SendAsync();
        });
        var request = await server.RequestAsync();

        Assert.DoesNotContain("\r\nX-Injected: yes\r\n", request, StringComparison.Ordinal);
        Assert.True(failure is not null || request.Length > 0, "Neither a failure nor a request.");
    }

    /// <summary>
    /// A value with a line break is refused at the call that gave it, as a name outside the field-name characters
    /// is, rather than travelling to the handler.
    /// </summary>
    [Fact]
    public void Header_ValueWithALineBreak_IsRefusedAtTheCall()
    {
        using var client = Client(new RecordingHandler(), TimeSpan.FromSeconds(10));
        var builder = new HttpRequestBuilder(client, HttpMethod.Get, "things");

        Assert.ThrowsAny<ArgumentException>(() => builder.Header("X-Note", "fine\r\nX-Injected: yes"));
    }

    /// <summary>
    /// The query was appended after the whole path, so a path with a fragment carried the query inside the fragment,
    /// which never leaves the client: the parameter was dropped without a word.
    /// </summary>
    [Fact]
    public async Task Query_PathWithAFragment_StillSendsTheQuery()
    {
        var handler = new RecordingHandler();
        using var client = Client(handler, TimeSpan.FromSeconds(10));

        using var response = await new HttpRequestBuilder(client, HttpMethod.Get, "docs#intro").Query("page", "2").SendAsync();

        Assert.Equal("?page=2", handler.Single.RequestUri!.Query);
    }

    /// <summary>
    /// A query parameter is escaped only when the message is assembled: a null name failed at the send, and half a
    /// surrogate pair was sent as U+FFFD. Both are refused at the call that gave them. The half surrogate is built
    /// here: an inline value of a theory is serialized on its way to the test and arrives as three replacement
    /// characters.
    /// </summary>
    [Theory]
    [InlineData("half surrogate")]
    [InlineData("null name")]
    public async Task Query_ParameterThatCannotBeSent_IsRefusedAtTheCall(string parameter)
    {
        var name = parameter == "null name" ? null : "name";
        var value = parameter == "half surrogate" ? $"{(char)0xD800}" : "value";
        var handler = new RecordingHandler();
        using var client = Client(handler, TimeSpan.FromSeconds(10));
        var builder = new HttpRequestBuilder(client, HttpMethod.Get, "things");

        var atTheCall = Record.Exception(() => builder.Query(name!, value));
        var atTheSend = atTheCall is null ? await Record.ExceptionAsync(async () => { using var sent = await builder.SendAsync(); }) : null;

        var sentQuery = handler.Requests.Count > 0 ? handler.Single.RequestUri!.Query : "nothing sent";
        Assert.True(atTheCall is ArgumentException, $"At the call: {atTheCall?.GetType().Name ?? "none"}; at the send: {atTheSend?.GetType().Name ?? "none"}; sent: {sentQuery}");
    }

    /// <summary>
    /// A hundred failing streamed calls at once, each with a body that stalls: every one is bounded by its own
    /// deadline and answers the status within the client's timeout, none waits for another.
    /// </summary>
    [Fact]
    public async Task SendStreamedAsync_HundredParallelStalledFailures_AllAnswerWithinTheTimeout()
    {
        using var release = new CancellationTokenSource();
        using var client = Client(
            new ScriptedHandler(HttpStatusCode.InternalServerError, () => new StallingBody("{", release.Token)),
            TimeSpan.FromMilliseconds(500));

        var started = Stopwatch.GetTimestamp();
        var calls = Enumerable.Range(0, 100).Select(_ => Record.ExceptionAsync(() => new HttpRequestBuilder(client, HttpMethod.Get, "things")
            .SendStreamedAsync<Response>(CancellationToken.None)).AsTask());
        var failures = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        var elapsed = Stopwatch.GetElapsedTime(started);

        await release.CancelAsync();

        Assert.All(failures, failure => Assert.IsType<HttpBuilderException>(failure));
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"{elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// The longest timeout a client accepts, <see cref="int.MaxValue"/> milliseconds: what is left of it must still
    /// be a delay a token source takes, and the failure body is read.
    /// </summary>
    [Fact]
    public async Task SendCheckedAsync_LongestClientTimeout_ReadsTheFailureBody()
    {
        using var client = Client(
            new RecordingHandler(Canned.Text("nope", HttpStatusCode.BadRequest)),
            TimeSpan.FromMilliseconds(int.MaxValue));

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => new HttpRequestBuilder(client, HttpMethod.Get, "things")
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead));

        Assert.Equal("nope", failure.Body);
    }

    /// <summary>
    /// The buffered body is parsed from an array of the shared pool. Returned without clearing, the bytes of a secret
    /// — a Vault answer — stayed readable to the next code in the process renting an array of that size.
    /// </summary>
    [Fact]
    public async Task SendAsync_ResponseCarryingASecret_LeavesNoCopyInTheSharedPool()
    {
        var secret = $"{{\"field\":\"s3cr3t-{new string('x', 3000)}\"}}";
        using var client = Client(new RecordingHandler(Canned.Text(secret, HttpStatusCode.OK)), TimeSpan.FromSeconds(10));

        var read = await new HttpRequestBuilder(client, HttpMethod.Get, "secret").SendAsync<Response>();

        var rented = ArrayPool<byte>.Shared.Rent(secret.Length);
        var residue = Encoding.UTF8.GetString(rented, 0, secret.Length);
        ArrayPool<byte>.Shared.Return(rented);

        Assert.NotNull(read);
        Assert.DoesNotContain("s3cr3t-", residue, StringComparison.Ordinal);
    }

    /// <summary>A one-shot HTTP/1.1 server on loopback that records the raw request and answers <c>200 {}</c>.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _request;

        private LoopbackServer(TcpListener listener)
        {
            _listener = listener;
            _request = ServeOnceAsync(listener);
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public static Task<LoopbackServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            return Task.FromResult(new LoopbackServer(listener));
        }

        public async Task<string> RequestAsync()
        {
            var finished = await Task.WhenAny(_request, Task.Delay(TimeSpan.FromSeconds(1)));

            return finished == _request ? await _request : string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();

            try
            {
                await _request;
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
            }
        }

        private static async Task<string> ServeOnceAsync(TcpListener listener)
        {
            using var connection = await listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];

            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(buffer);

                if (count == 0)
                {
                    break;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }

            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray());

            return received.ToString();
        }
    }
}
