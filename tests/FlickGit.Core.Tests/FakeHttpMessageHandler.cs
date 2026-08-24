using System.Net;
using System.Text;

namespace FlickGit.Tests;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a canned transcript.
///
/// The BCL already provides the seam, so no <c>IHttpClient</c> interface is invented for this —
/// Hard Requirement 2 forbids an abstraction with one implementation, and
/// <c>new HttpClient(handler)</c> is the substitution point the framework intends.
///
/// The body is delivered in small chunks on purpose. A streaming reader that happens to work when
/// the whole response arrives in one read is a reader that has not been tested: the interesting bugs
/// are all at the boundary between two chunks.
/// </summary>
internal sealed class FakeHttpMessageHandler(
    HttpStatusCode status,
    string body,
    int chunkSize = 7) : HttpMessageHandler
{
    /// <summary>The request body that was sent, so a test can assert on what left the machine.</summary>
    public string? SentBody { get; private set; }

    public int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests++;

        if (request.Content is not null)
            SentBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var response = new HttpResponseMessage(status)
        {
            Content = new StreamContent(new ChunkedStream(Encoding.UTF8.GetBytes(body), chunkSize)),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        return response;
    }

    /// <summary>A stream that hands back a few bytes at a time, the way a socket does.</summary>
    private sealed class ChunkedStream(byte[] content, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int remaining = content.Length - _position;

            if (remaining <= 0)
                return 0;

            int take = Math.Min(Math.Min(chunkSize, buffer.Length), remaining);
            content.AsSpan(_position, take).CopyTo(buffer);
            _position += take;

            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
            //Nothing is buffered, and nothing writes.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
