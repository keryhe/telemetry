using System.IO.Compression;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Keryhe.Telemetry.Collector.Http;

public enum OtlpContentType { Protobuf, Json }

/// <summary>An OTLP/HTTP request this collector does not accept (a 415 to the client).</summary>
public sealed class UnsupportedOtlpRequestException(string message) : Exception(message);

/// <summary>
/// Reads an OTLP/HTTP request body: checks <c>Content-Type</c> (<c>application/x-protobuf</c> or <c>application/json</c>) and
/// <c>Content-Encoding</c> (none, <c>identity</c> or <c>gzip</c>), then reads the body in a streaming loop into memory, through a
/// <see cref="GZipStream"/> when compressed, stopping with <see cref="PayloadTooLargeException"/> as soon as the DECOMPRESSED size passes
/// <c>MaxReceiveMessageSizeBytes</c>: a small compressed body cannot expand without limit.
/// </summary>
public static class OtlpHttpBodyReader
{
    public const string ProtobufMediaType = "application/x-protobuf";
    public const string JsonMediaType = "application/json";

    /// <summary>The request's content type, or null when it is neither supported type (including a missing header).</summary>
    public static OtlpContentType? ContentTypeOf(HttpRequest request)
    {
        var raw = request.ContentType;
        if (string.IsNullOrEmpty(raw)) return null;
        var media = raw.Split(';', 2)[0].Trim();
        if (media.Equals(ProtobufMediaType, StringComparison.OrdinalIgnoreCase)) return OtlpContentType.Protobuf;
        if (media.Equals(JsonMediaType, StringComparison.OrdinalIgnoreCase)) return OtlpContentType.Json;
        return null;
    }

    public static async Task<T> ReadAsync<T>(HttpRequest request, OtlpContentType type, MessageParser<T> parser, long maxBytes, CancellationToken ct)
        where T : class, IMessage<T>, new()
    {
        var encoding = request.Headers.ContentEncoding.ToString().Trim();
        var gzip = encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase);
        if (!gzip && encoding.Length > 0 && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedOtlpRequestException($"Content-Encoding '{encoding}' is not supported (use gzip or none).");

        // Kestrel's own request-size limit (30 MB by default) must not cut a body this collector is configured to accept.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false, MaxRequestBodySize: { } kestrelMax } feature && kestrelMax < maxBytes)
            feature.MaxRequestBodySize = maxBytes;

        // A body that says it is too large on the wire need not be read at all (a gzip body can still expand past the limit, checked below).
        if (!gzip && request.ContentLength is { } declared && declared > maxBytes)
            throw new PayloadTooLargeException(maxBytes);

        using var buffer = new MemoryStream(request.ContentLength is { } n and > 0 and <= int.MaxValue ? (int)Math.Min(n * (gzip ? 4 : 1), maxBytes) : 4096);
        await using var source = gzip ? new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
        var stream = (Stream?)source ?? request.Body;

        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await ReadChunkAsync(stream, chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new PayloadTooLargeException(maxBytes);
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }

        var bytes = new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);
        try
        {
            return type == OtlpContentType.Json ? OtlpJsonReader.Parse<T>(bytes) : parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidOtlpBodyException("The body is not a valid protobuf OTLP export: " + ex.Message, ex);
        }
    }

    private static async ValueTask<int> ReadChunkAsync(Stream stream, byte[] chunk, CancellationToken ct)
    {
        try { return await stream.ReadAsync(chunk, ct); }
        catch (InvalidDataException ex) { throw new InvalidOtlpBodyException("The gzip body is corrupt: " + ex.Message, ex); }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { throw new PayloadTooLargeException(0); }
    }
}
