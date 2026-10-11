using System.Text;
using Enterprise.Gpt.Api.Options;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// A write-only pass-through over the real response stream that keeps a bounded copy of what was written.
/// </summary>
/// <remarks>
/// Pass-through, not buffering, or <c>POST api/conversations/{id}/stream</c> would arrive as one delivery at
/// the end. Eligibility is settled on the first write, once the handler has set the content type.
/// </remarks>
internal sealed class ResponseCaptureStream(
    Stream inner,
    HttpResponse response,
    BodyLoggingOptions options) : Stream
{
    private const int InitialCapacity = 4096;

    private readonly Stream _inner = inner;
    private readonly HttpResponse _response = response;
    private readonly BodyLoggingOptions _options = options;

    private byte[]? _captureBuffer;
    private int _capturedBytes;
    private bool _eligibilityDecided;
    private bool _capturing;

    internal bool Truncated { get; private set; }

    /// <summary>
    /// The content type at the first write, or <see langword="null"/> if nothing was written.
    /// </summary>
    internal string? CapturedContentType { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <remarks>
    /// The cap counts bytes, so a truncated capture can end in a replacement character; cutting on a character
    /// boundary would cost incremental decoding on every write.
    /// </remarks>
    internal string? GetCapturedText() =>
        _capturedBytes > 0 ? Encoding.UTF8.GetString(_captureBuffer!, 0, _capturedBytes) : null;

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // Forwarded before the copy is taken so a write that fails never appears in the log as delivered.
        _inner.Write(buffer);
        Capture(buffer);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Leaves the wrapped stream open: it belongs to the server.</summary>
    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    #region Private methods

    private void Capture(ReadOnlySpan<byte> data)
    {
        if (!_eligibilityDecided)
        {
            DecideEligibility();
        }

        if (!_capturing || data.IsEmpty)
        {
            return;
        }

        Grow(data.Length);
        var remaining = _captureBuffer!.Length - _capturedBytes;

        // Capture stays armed once the buffer is full rather than switching itself off, so a later write
        // arriving after the cap is still what sets the truncation flag.
        if (data.Length > remaining)
        {
            data[..remaining].CopyTo(_captureBuffer.AsSpan(_capturedBytes));
            _capturedBytes = _captureBuffer.Length;
            Truncated = true;
            return;
        }

        data.CopyTo(_captureBuffer.AsSpan(_capturedBytes));
        _capturedBytes += data.Length;
    }

    private void DecideEligibility()
    {
        _eligibilityDecided = true;
        CapturedContentType = _response.ContentType;
        _capturing = BodyCapturePolicy.IsAllowedContentType(CapturedContentType, _options.AllowedContentTypes);

        if (_capturing)
        {
            _captureBuffer = new byte[Math.Min(_options.MaxBodyBytes, InitialCapacity)];
        }
    }

    // Grown towards the cap: allocating MaxBodyBytes up front would hit the large object heap on every
    // response for any cap above 85 KB.
    private void Grow(int incoming)
    {
        var required = _capturedBytes + incoming;

        if (required <= _captureBuffer!.Length || _captureBuffer.Length >= _options.MaxBodyBytes)
        {
            return;
        }

        var capacity = Math.Min(_options.MaxBodyBytes, Math.Max(required, _captureBuffer.Length * 2));
        Array.Resize(ref _captureBuffer, capacity);
    }

    #endregion
}
