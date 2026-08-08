using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Substitutes <see cref="ResponseCaptureStream"/> for the response stream while leaving every other
/// response-body operation with the server's own feature.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than using the framework's <c>StreamResponseBodyFeature</c>, whose
/// <c>CompleteAsync</c> completes only its own pipe writer and never tells the prior feature the response
/// is finished — so a handler calling <see cref="HttpResponse.CompleteAsync"/> while the wrapper was
/// installed would not reach Kestrel.
/// </para>
/// <para>
/// <see cref="SendFileAsync"/> deliberately bypasses the capture stream and goes straight to the server,
/// which preserves the zero-copy <c>sendfile</c> path. The cost is that a file response is not captured,
/// which is no loss: file payloads are binary and would fail the content-type allow-list anyway.
/// </para>
/// </remarks>
/// <param name="prior">The feature this one wraps. Owns the underlying connection.</param>
/// <param name="stream">The capturing stream handed out in place of the real one.</param>
internal sealed class ResponseCaptureBodyFeature(
    IHttpResponseBodyFeature prior,
    ResponseCaptureStream stream) : IHttpResponseBodyFeature
{
    private readonly IHttpResponseBodyFeature _prior = prior;
    private readonly ResponseCaptureStream _stream = stream;

    private PipeWriter? _writer;

    /// <inheritdoc />
    public Stream Stream => _stream;

    /// <inheritdoc />
    /// <remarks>
    /// <c>leaveOpen</c> because the capture stream wraps a connection this feature does not own.
    /// </remarks>
    public PipeWriter Writer =>
        _writer ??= PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <inheritdoc />
    public void DisableBuffering() => _prior.DisableBuffering();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default) => _prior.StartAsync(cancellationToken);

    /// <inheritdoc />
    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
    {
        // Anything still sitting in the pipe writer has to reach the connection first, or the file would
        // overtake the bytes written before it.
        await FlushWriterAsync(cancellationToken);
        await _prior.SendFileAsync(path, offset, count, cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteAsync()
    {
        if (_writer is not null)
        {
            await _writer.CompleteAsync();
        }

        await _prior.CompleteAsync();
    }

    /// <summary>
    /// Pushes anything buffered in <see cref="Writer"/> down to the connection.
    /// </summary>
    /// <param name="cancellationToken">A token that propagates cancellation.</param>
    /// <returns>A task that completes when the buffer has been drained.</returns>
    /// <remarks>
    /// Called before the middleware puts the original feature back: a handler that wrote through
    /// <see cref="HttpResponse.BodyWriter"/> without flushing would otherwise lose those bytes, because
    /// the host goes on to complete a feature that knows nothing about this writer.
    /// </remarks>
    internal async ValueTask FlushWriterAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken);
        }
    }
}
