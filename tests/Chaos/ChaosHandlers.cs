using System.Net;
using System.Text;

namespace Snail.Toolkit.HttpBuilder.Extensions.Tests.Chaos;

/// <summary>Answers every request with one status and a body the test scripts.</summary>
internal sealed class ScriptedHandler(HttpStatusCode status, Func<Stream> body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body()) };

        return Task.FromResult(response);
    }
}

/// <summary>
/// A body that sends a few bytes and then stalls until the test releases it, as a server that hangs mid-answer
/// does. Released, it fails the read the way a connection reset would, so no read outlives its test.
/// </summary>
internal sealed class StallingBody(string start, CancellationToken release) : Stream
{
    private readonly byte[] _start = Encoding.UTF8.GetBytes(start);
    private bool _isStarted;

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
        if (!_isStarted)
        {
            _isStarted = true;
            _start.CopyTo(buffer);

            return _start.Length;
        }

        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, release);

        try
        {
            await Task.Delay(Timeout.Infinite, either.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The test released a body that never finished.");
        }

        return 0;
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
