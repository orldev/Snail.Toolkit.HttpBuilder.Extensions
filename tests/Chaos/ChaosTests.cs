using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Contracts;
using Snail.Toolkit.HttpBuilder.Extensions.Tests.Extensions;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Chaos;

/// <summary>
/// The crash tests of the 2026-10-21 audit, kept as regression tests: racing sends, parallel calls, failing and
/// stalling servers, and the boundary values the library promises to survive.
/// </summary>
public class ChaosTests
{
    [JsonConverter(typeof(EnumMemberJsonConverter<Level>))]
    public enum Level : byte
    {
        [EnumMember(Value = "LOW")] Low = 1,
        [EnumMember(Value = "HIGH")] High = 2
    }

    [JsonConverter(typeof(EnumMemberJsonConverter<Huge>))]
    public enum Huge : ulong
    {
        Small = 1,
        Top = ulong.MaxValue
    }

    private static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout) =>
        new(handler) { BaseAddress = new Uri("https://www.example.com"), Timeout = timeout };

    /// <summary>
    /// Two racing sends of one builder: exactly one reaches the wire and the other is refused, because the guard is
    /// one atomic exchange.
    /// </summary>
    [Fact]
    public async Task SendAsync_RacingSendsOfOneBuilder_SendsExactlyOnce()
    {
        for (var round = 0; round < 200; round++)
        {
            var handler = new RecordingHandler();
            using var client = Client(handler, TimeSpan.FromSeconds(10));
            var builder = new HttpRequestBuilder(client, HttpMethod.Post, "thing").AsJson(new Request());

            using var start = new Barrier(2);
            var first = Task.Run(() => SendAfter(start, builder));
            var second = Task.Run(() => SendAfter(start, builder));

            var outcomes = await Task.WhenAll(first, second);

            Assert.Single(handler.Requests);
            Assert.Single(outcomes, outcome => outcome is InvalidOperationException);
        }
    }

    /// <summary>
    /// One client called from many threads at once: no header, query or body crosses into another call, because
    /// each verb makes a builder of its own.
    /// </summary>
    [Fact]
    public async Task TypedClient_ParallelCalls_KeepTheirOwnState()
    {
        var handler = new ConcurrentRecorder();
        using var client = Client(handler, TimeSpan.FromSeconds(10));
        var typed = new ProbeClient(client);

        await Parallel.ForAsync(0, 2000, async (call, token) => await typed.CallAsync(call, token));

        Assert.Equal(2000, handler.Mismatches + handler.Matches);
        Assert.Equal(0, handler.Mismatches);
    }

    /// <summary>
    /// A failure body that breaks mid-read: the status is the news, and the exception still carries it, with no
    /// body rather than a part of one.
    /// </summary>
    [Fact]
    public async Task SendCheckedAsync_FailureBodyBreaks_StillThrowsTheStatus()
    {
        using var release = new CancellationTokenSource();
        await release.CancelAsync();
        using var client = Client(
            new ScriptedHandler(HttpStatusCode.ServiceUnavailable, () => new StallingBody("partial", release.Token)),
            TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Null(failure.Body);
    }

    /// <summary>
    /// <c>SendStreamedAsync</c> promises that the client's timeout covers the whole request. A failure status whose
    /// body stalls is read outside the deadline the terminal keeps for a successful body, so it needs one of its own:
    /// without it a 300 ms client waited as long as the server held the connection.
    /// </summary>
    [Fact]
    public async Task SendStreamedAsync_FailureBodyStalls_FailsWithinTheClientTimeout()
    {
        using var release = new CancellationTokenSource();
        using var client = Client(
            new ScriptedHandler(HttpStatusCode.InternalServerError, () => new StallingBody("""{"error":""", release.Token)),
            TimeSpan.FromMilliseconds(300));

        var failure = await Record.ExceptionAsync(() => new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendStreamedAsync<Response>(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        await release.CancelAsync();

        var status = Assert.IsType<HttpBuilderException>(failure);
        Assert.Equal(HttpStatusCode.InternalServerError, status.StatusCode);
        Assert.Null(status.Body);
    }

    /// <summary>
    /// The same stall on a streaming terminal. A stream still being generated is the caller's to bound, but a
    /// failure is known once its headers arrive, and its body is bounded by the client's timeout as well as by size.
    /// </summary>
    [Fact]
    public async Task SendAsNdjsonAsync_FailureBodyStalls_FailsWithinTheClientTimeout()
    {
        using var release = new CancellationTokenSource();
        using var client = Client(
            new ScriptedHandler(HttpStatusCode.BadGateway, () => new StallingBody("upstream", release.Token)),
            TimeSpan.FromMilliseconds(300));

        var failure = await Record.ExceptionAsync(() => new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendAsNdjsonAsync<Response>()
            .ToListAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5)));

        await release.CancelAsync();

        Assert.Equal(HttpStatusCode.BadGateway, Assert.IsType<HttpBuilderException>(failure).StatusCode);
    }

    /// <summary>
    /// The caller's own cancellation still wins over the status while the failure body stalls: the token is the
    /// caller's decision, not a slow read.
    /// </summary>
    [Fact]
    public async Task SendCheckedAsync_CancelledWhileTheFailureBodyStalls_ThrowsTheCancellation()
    {
        using var release = new CancellationTokenSource();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var client = Client(
            new ScriptedHandler(HttpStatusCode.InternalServerError, () => new StallingBody("{", release.Token)),
            Timeout.InfiniteTimeSpan);

        var failure = await Record.ExceptionAsync(() => new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancel.Token)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        await release.CancelAsync();

        Assert.Equal(cancel.Token, Assert.IsAssignableFrom<OperationCanceledException>(failure).CancellationToken);
    }

    /// <summary>
    /// One NDJSON line of 16 MB arrives in the pipe's 4 KB reads. Searched from the start on every read, finding its
    /// end cost the square of its length. The parse of the same bytes is the yardstick, so a busy machine slows
    /// both: measured, the read took ×165 the parse searched from the start and ×10 searched once (the pipe's
    /// copies of a line the parser skims), and ×40 sits apart from both by a factor of four.
    /// </summary>
    [Fact]
    public async Task SendAsNdjsonAsync_OneLongLine_CostsAboutItsParse()
    {
        var line = Encoding.UTF8.GetBytes($$"""{"field":"{{new string('a', 16 * 1024 * 1024)}}"}""");
        using var client = Client(new ScriptedHandler(HttpStatusCode.OK, () => new MemoryStream(line)), TimeSpan.FromMinutes(5));

        await ReadLineAsync(client);
        JsonSerializer.Deserialize<Response>(line);

        var read = await Fastest(() => ReadLineAsync(client));
        var parsed = await Fastest(() => Task.FromResult(JsonSerializer.Deserialize<Response>(line)));
        var ratio = read.TotalMilliseconds / parsed.TotalMilliseconds;

        Assert.True(ratio < 40, $"Read in {read.TotalMilliseconds:F0} ms, parsed in {parsed.TotalMilliseconds:F0} ms: ×{ratio:F1}");
    }

    /// <summary>
    /// "A numeric wire value maps onto the defined member it names." Cast to the enum, a number was truncated to
    /// the underlying type, so 258 on a byte enum read as 2, <c>High</c> — a member it does not name.
    /// </summary>
    [Theory]
    [InlineData("258", default(Level))]
    [InlineData("-254", default(Level))]
    [InlineData("2", Level.High)]
    public void EnumMemberJsonConverter_NumberOnAByteEnum_ReadsTheMemberItNames(string json, Level expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<Level>(json));

    /// <summary>
    /// A <see cref="ulong"/> enum: a member within <see cref="long"/> reads by its number, and -1 does not alias the
    /// member at <see cref="ulong.MaxValue"/>, whose bits it shares.
    /// </summary>
    [Theory]
    [InlineData("1", Huge.Small)]
    [InlineData("-1", default(Huge))]
    public void EnumMemberJsonConverter_NumberOnAnUnsignedEnum_ReadsTheMemberItNames(string json, Huge expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<Huge>(json));

    /// <summary>
    /// The exception's message is what a log writes. It named the whole request URI, so an API key passed with
    /// <c>Query("key", …)</c> reached every log line; <see cref="HttpBuilderException.RequestUri"/> still has it all.
    /// </summary>
    [Fact]
    public async Task SendAsync_FailureOfARequestWithASecretInTheQuery_KeepsTheSecretOutOfTheMessage()
    {
        using var client = Client(new RecordingHandler(Canned.Text("denied", HttpStatusCode.Forbidden)), TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => new HttpRequestBuilder(client, HttpMethod.Get, "v1/places")
            .Query("key", "AIzaSECRET")
            .SendAsync<Response>());

        Assert.DoesNotContain("AIzaSECRET", failure.Message, StringComparison.Ordinal);
        Assert.Contains("GET https://www.example.com/v1/places responded 403", failure.Message, StringComparison.Ordinal);
        Assert.Equal("https://www.example.com/v1/places?key=AIzaSECRET", failure.RequestUri?.AbsoluteUri);
    }

    /// <summary>The exception is public and takes any URI: a relative one keeps its query out of the message too.</summary>
    [Fact]
    public void HttpBuilderException_RelativeUriWithAQuery_KeepsTheQueryOutOfTheMessage()
    {
        var failure = new HttpBuilderException(
            HttpMethod.Get, new Uri("v1/places?key=AIzaSECRET", UriKind.Relative), HttpStatusCode.Forbidden, null, null);

        Assert.Equal("GET v1/places responded 403 Forbidden.", failure.Message);
    }

    /// <summary>
    /// An invalid header name is refused at the call that gave it. Assembled at send time, it threw a
    /// <see cref="FormatException"/> when the request had a body and vanished when it had none: one mistake, two
    /// behaviours, neither where it was made.
    /// </summary>
    [Theory]
    [InlineData("Bad Name")]
    [InlineData("X-Line\r\nInjected")]
    [InlineData("Ünicode")]
    [InlineData("")]
    public void Header_InvalidName_IsRefusedAtTheCall(string name)
    {
        using var client = Client(new RecordingHandler(), TimeSpan.FromSeconds(10));
        var builder = new HttpRequestBuilder(client, HttpMethod.Get, "thing");

        Assert.ThrowsAny<ArgumentException>(() => builder.Header(name, "value"));
        Assert.ThrowsAny<ArgumentException>(() => builder.Headers([new KeyValuePair<string, string>(name, "value")]));
    }

    /// <summary>
    /// The failure body is cut at 4096 UTF-16 units, which can fall between the halves of a surrogate pair; the cut
    /// then steps back one unit, so the body ends on a whole character before its ellipsis.
    /// </summary>
    [Theory]
    [InlineData(4095, 4095)]
    [InlineData(4094, 4096)]
    [InlineData(4096, 4096)]
    public async Task SendAsync_FailureBodyCutNearASurrogatePair_EndsOnAWholeCharacter(int lead, int kept)
    {
        var body = $"{new string('a', lead)}\U0001F600{new string('b', 10)}";
        using var client = Client(new RecordingHandler(Canned.Text(body, HttpStatusCode.BadRequest)), TimeSpan.FromSeconds(10));

        var failure = await Assert.ThrowsAsync<HttpBuilderException>(() => new HttpRequestBuilder(client, HttpMethod.Get, "thing")
            .SendAsync<Response>());

        Assert.Equal($"{body[..kept]}…", failure.Body);
    }

    private static async Task<Response?> ReadLineAsync(HttpClient client)
    {
        var values = await new HttpRequestBuilder(client, HttpMethod.Get, "thing").SendAsNdjsonAsync<Response>().ToListAsync();

        return Assert.Single(values);
    }

    private static async Task<TimeSpan> Fastest(Func<Task<Response?>> work)
    {
        var fastest = TimeSpan.MaxValue;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var started = Stopwatch.GetTimestamp();
            await work();
            var elapsed = Stopwatch.GetElapsedTime(started);

            fastest = elapsed < fastest ? elapsed : fastest;
        }

        return fastest;
    }

    private static async Task<Exception?> SendAfter(Barrier start, IHttpRequestBuilder builder)
    {
        start.SignalAndWait();

        return await Record.ExceptionAsync(async () =>
        {
            using var response = await builder.SendAsync();
        });
    }

    private sealed class ProbeClient(HttpClient client) : TypedHttpClientBase(client)
    {
        public async Task CallAsync(int call, CancellationToken cancellationToken)
        {
            var builder = call % 2 == 0
                ? Post($"calls/{call}").AsJson(new Request { Field = $"{call}" })
                : Get($"calls/{call}");

            using var response = await builder
                .Header("X-Call", $"{call}")
                .Query("call", $"{call}")
                .SendAsync(cancellationToken);
        }
    }

    /// <summary>Checks, inside the send, that every part of a request names the same call.</summary>
    private sealed class ConcurrentRecorder : HttpMessageHandler
    {
        private int _matches;
        private int _mismatches;

        public int Matches => _matches;

        public int Mismatches => _mismatches;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = request.RequestUri!.Segments[^1];
            var header = string.Join(',', request.Headers.GetValues("X-Call"));
            var query = request.RequestUri.Query;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            var isOdd = int.Parse(call, CultureInfo.InvariantCulture) % 2 == 1;
            var isOwn = header == call
                && query == $"?call={call}"
                && (isOdd || body == $$"""{"field":"{{call}}"}""")
                && (request.Method == HttpMethod.Get) == (body is null);

            Interlocked.Increment(ref isOwn ? ref _matches : ref _mismatches);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
