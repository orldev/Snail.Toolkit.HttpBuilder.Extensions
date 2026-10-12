using System.Buffers;

namespace Snail.Toolkit.HttpBuilder.Extensions;

/// <summary>A buffered response body held in a pooled array, so parsing it costs no copy of its own.</summary>
/// <remarks>
/// The body of a <see cref="HttpCompletionOption.ResponseContentRead"/> response is already in memory;
/// <see cref="HttpContent.ReadAsByteArrayAsync()"/> would copy it into a new array the size of the body, which for a
/// large answer lands on the large object heap. Disposing returns the array to the pool.
/// </remarks>
internal readonly struct PooledBody : IDisposable
{
    private readonly byte[] _buffer;
    private readonly bool _isRented;

    private PooledBody(byte[] buffer, int length, bool isRented)
    {
        _buffer = buffer;
        Length = length;
        _isRented = isRented;
    }

    /// <summary>The number of bytes received.</summary>
    public int Length { get; }

    /// <summary>The bytes received.</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    /// <summary>Reads the whole body of a response.</summary>
    /// <remarks>
    /// Emptiness is the number of bytes received, as before. A seekable stream — what a buffered body gives — is
    /// copied into a pooled array of its exact length; any other stream is read to its end into a growing one.
    /// </remarks>
    public static async Task<PooledBody> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            if (!stream.CanSeek)
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);

                return new PooledBody(copy.GetBuffer(), (int)copy.Length, isRented: false);
            }

            var length = checked((int)(stream.Length - stream.Position));

            if (length == 0)
            {
                return new PooledBody([], 0, isRented: false);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            return new PooledBody(buffer, length, isRented: true);
        }
    }

    /// <summary>Returns the array to the pool when it came from there.</summary>
    public void Dispose()
    {
        if (_isRented)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
