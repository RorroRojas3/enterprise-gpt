using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace Enterprise.Gpt.Api.Middleware;

/// <summary>
/// Substitutes <see cref="ResponseCaptureStream"/> for the response stream while leaving every other
/// response-body operation with the server's own feature.
/// </summary>
/// <remarks>
/// Not <c>StreamResponseBodyFeature</c>: its <c>CompleteAsync</c> never completes the prior feature, so
/// <see cref="HttpResponse.CompleteAsync"/> would not reach Kestrel. <see cref="SendFileAsync"/> bypasses
/// capture to keep zero-copy <c>sendfile</c>; file payloads would fail the content-type allow-list anyway.
/// </remarks>
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
    /// Pushes anything buffered in <see cref="Writer"/> to the connection before the middleware restores the
    /// original feature, which knows nothing of this writer.
    /// </summary>
    internal async ValueTask FlushWriterAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken);
        }
    }
}
