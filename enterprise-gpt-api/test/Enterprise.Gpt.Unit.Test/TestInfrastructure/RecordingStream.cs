namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// A write-only stream that remembers what was written and how it was chunked.
/// </summary>
/// <remarks>
/// Chunk sizes prove a response wrapper forwarded each fragment as it arrived rather than buffering to
/// the end, which is what SSE depends on.
/// </remarks>
public sealed class RecordingStream : Stream
{
    private readonly MemoryStream _content = new();

    public List<int> WriteSizes { get; } = [];

    public int FlushCount { get; private set; }

    public byte[] ToArray() => _content.ToArray();

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _content.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _content.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => FlushCount++;

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        FlushCount++;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteSizes.Add(buffer.Length);
        _content.Write(buffer);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}
