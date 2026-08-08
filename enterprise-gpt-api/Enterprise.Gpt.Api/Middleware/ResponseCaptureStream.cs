using System.Text;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// A write-only pass-through over the real response stream that keeps a bounded copy of what was written.
/// </summary>
/// <remarks>
/// <para>
/// Pass-through, not buffering: every write and every flush reaches the inner stream immediately, and the
/// copy is taken afterwards. That is what keeps <c>POST api/conversations/{id}/stream</c> intact — the
/// controller writes one model fragment and flushes, over and over, and a wrapper that held bytes back
/// until completion would turn a live stream into a single delivery at the end.
/// </para>
/// <para>
/// Eligibility is settled on the first write rather than at construction, because a response's content
/// type is not known until the handler sets it. A <c>text/event-stream</c> response therefore stops being
/// captured the moment it identifies itself, and the wrapper degrades to a plain forwarder for the rest
/// of its life.
/// </para>
/// </remarks>
/// <param name="inner">The response stream being wrapped. Not owned, and never disposed here.</param>
/// <param name="response">The response whose content type decides eligibility.</param>
/// <param name="options">Supplies the byte cap and the content-type allow-list.</param>
internal sealed class ResponseCaptureStream(
    Stream inner,
    HttpResponse response,
    BodyLoggingOptions options) : Stream
{
    /// <summary>Starting capture capacity, doubled towards the cap as a response grows.</summary>
    private const int InitialCapacity = 4096;

    private readonly Stream _inner = inner;
    private readonly HttpResponse _response = response;
    private readonly BodyLoggingOptions _options = options;

    private byte[]? _captureBuffer;
    private int _capturedBytes;
    private bool _eligibilityDecided;
    private bool _capturing;

    /// <summary>
    /// Whether the response outgrew the byte cap.
    /// </summary>
    internal bool Truncated { get; private set; }

    /// <summary>
    /// The content type observed when eligibility was decided, or <see langword="null"/> if nothing was
    /// ever written.
    /// </summary>
    internal string? CapturedContentType { get; private set; }

    /// <summary>
    /// Returns what was captured, or <see langword="null"/> when nothing was.
    /// </summary>
    /// <returns>The captured text, decoded as UTF-8.</returns>
    /// <remarks>
    /// The cap is a byte count, so a truncated capture can end mid-sequence and decode to a replacement
    /// character. Cutting on a character boundary instead would mean decoding incrementally on the write
    /// path, which is real work on every response to tidy up the last glyph of a payload that is already
    /// explicitly marked as truncated.
    /// </remarks>
    internal string? GetCapturedText() =>
        _capturedBytes > 0 ? Encoding.UTF8.GetString(_captureBuffer!, 0, _capturedBytes) : null;

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

    /// <summary>
    /// Releases nothing: the wrapped stream belongs to the server, and disposing it here would close the
    /// response out from under the host.
    /// </summary>
    /// <param name="disposing">Unused.</param>
    protected override void Dispose(bool disposing)
    {
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

    /// <summary>
    /// Grows the capture buffer towards the cap, so a small response does not pay for a large cap.
    /// </summary>
    /// <remarks>
    /// Allocating <see cref="BodyLoggingOptions.MaxBodyBytes"/> up front would put an allocation on the
    /// large object heap, per response, for any cap above 85 KB — a configuration the validated ceiling
    /// permits.
    /// </remarks>
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
}
