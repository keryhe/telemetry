using System.IO.Compression;
using Grpc.Net.Compression;

namespace Keryhe.Telemetry.Collector;

/// <summary>
/// gzip for gRPC messages with a limit on the DECOMPRESSED size. grpc-dotnet checks <c>MaxReceiveMessageSize</c> against the
/// length on the wire, so a few kilobytes of gzip can expand into gigabytes before the message is deserialized (a decompression
/// bomb). This provider wraps the decompression stream so reading past <c>maxBytes</c> fails the call with
/// <c>RESOURCE_EXHAUSTED</c>, the same status an oversized plain message gets.
/// </summary>
public sealed class BoundedGzipCompressionProvider(int maxDecompressedBytes) : ICompressionProvider
{
    public string EncodingName => "gzip";

    public Stream CreateCompressionStream(Stream stream, CompressionLevel? compressionLevel) =>
        new GZipStream(stream, compressionLevel ?? CompressionLevel.Fastest, leaveOpen: true);

    public Stream CreateDecompressionStream(Stream stream) =>
        maxDecompressedBytes > 0
            ? new LimitedReadStream(new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true), maxDecompressedBytes)
            : new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        private void Count(int n)
        {
            _read += n;
            if (_read > limit)
                throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.ResourceExhausted, "Received message exceeds the maximum configured message size."));
        }

        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count(n); return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); Count(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            Count(n);
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
