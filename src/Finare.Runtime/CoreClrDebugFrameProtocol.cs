using System.Buffers.Binary;
using System.Text.Json;
using Finare.Core;

namespace Finare.Runtime;

public enum CoreClrDebugFrameKind : ushort
{
    Request = 1,
    Response = 2,
}

public sealed record CoreClrDebugFrameError(
    string Code,
    string Message,
    string Type);

public sealed record CoreClrDebugFrame(
    string SchemaVersion,
    CoreClrDebugFrameKind Kind,
    long RequestId,
    string Operation,
    bool? Succeeded,
    JsonElement Payload,
    CoreClrDebugFrameError? Error)
{
    public const string CurrentSchemaVersion = "unifiedscript.debug-frame/v0.1";

    public static CoreClrDebugFrame Request(
        long requestId,
        string operation,
        JsonElement payload) => new(
            CurrentSchemaVersion,
            CoreClrDebugFrameKind.Request,
            requestId,
            operation,
            null,
            payload,
            null);

    public static CoreClrDebugFrame Success(
        long requestId,
        string operation,
        JsonElement payload) => new(
            CurrentSchemaVersion,
            CoreClrDebugFrameKind.Response,
            requestId,
            operation,
            true,
            payload,
            null);

    public static CoreClrDebugFrame Failure(
        long requestId,
        string operation,
        CoreClrDebugFrameError error) => new(
            CurrentSchemaVersion,
            CoreClrDebugFrameKind.Response,
            requestId,
            operation,
            false,
            CoreClrDebugFrameCodec.EmptyPayload,
            error);
}

public sealed class CoreClrDebugFrameProtocolException : Exception
{
    public CoreClrDebugFrameProtocolException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public static class CoreClrDebugFrameCodec
{
    public const int HeaderSize = 24;
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const ushort ProtocolVersion = 1;

    private const uint Magic = 0x42445355; // "USDB" in little-endian byte order.
    private static readonly JsonSerializerOptions JsonOptions =
        UnifiedJson.CreateOptions(writeIndented: false);

    internal static JsonElement EmptyPayload { get; } =
        JsonSerializer.SerializeToElement(new EmptyFramePayload(), JsonOptions);

    public static async ValueTask WriteAsync(
        Stream stream,
        CoreClrDebugFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateFrame(frame);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new FrameEnvelope(
                frame.SchemaVersion,
                frame.RequestId,
                frame.Operation,
                frame.Succeeded,
                frame.Payload,
                frame.Error),
            JsonOptions);
        if (payload.Length is < 2 or > MaximumPayloadBytes)
        {
            throw ProtocolError(
                "UDBF1003",
                $"Debug frame payload size {payload.Length} is outside 2-{MaximumPayloadBytes} bytes.");
        }

        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4, 2),
            ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(6, 2),
            (ushort)frame.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8, 4),
            checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(16, 8),
            checked((ulong)frame.RequestId));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<CoreClrDebugFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[HeaderSize];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (first == 0)
        {
            return null;
        }
        await ReadExactlyAsync(
            stream,
            header.AsMemory(1),
            "Debug connection ended inside the fixed frame header.",
            cancellationToken);

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        ushort rawKind = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        uint rawLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        ulong rawRequestId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16, 8));
        if (magic != Magic)
        {
            throw ProtocolError("UDBF1001", "Debug frame magic is not USDB.");
        }
        if (version != ProtocolVersion)
        {
            throw ProtocolError(
                "UDBF1002",
                $"Unsupported debug frame protocol version {version}; expected {ProtocolVersion}.");
        }
        if (!Enum.IsDefined(typeof(CoreClrDebugFrameKind), rawKind))
        {
            throw ProtocolError("UDBF1001", $"Unknown debug frame kind {rawKind}.");
        }
        if (reserved != 0)
        {
            throw ProtocolError("UDBF1001", "Debug frame reserved header bits must be zero.");
        }
        if (rawLength is < 2 or > MaximumPayloadBytes)
        {
            throw ProtocolError(
                "UDBF1003",
                $"Debug frame payload size {rawLength} is outside 2-{MaximumPayloadBytes} bytes.");
        }
        if (rawRequestId is 0 or > long.MaxValue)
        {
            throw ProtocolError("UDBF1001", "Debug frame request ID is invalid.");
        }

        byte[] payload = new byte[checked((int)rawLength)];
        await ReadExactlyAsync(
            stream,
            payload,
            "Debug connection ended inside the declared frame payload.",
            cancellationToken);
        FrameEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FrameEnvelope>(payload, JsonOptions) ??
                throw new JsonException("The debug frame envelope was empty.");
        }
        catch (JsonException exception)
        {
            throw ProtocolError(
                "UDBF1004",
                $"Debug frame contains invalid JSON: {exception.Message}");
        }

        var frame = new CoreClrDebugFrame(
            envelope.SchemaVersion,
            (CoreClrDebugFrameKind)rawKind,
            envelope.RequestId,
            envelope.Operation,
            envelope.Succeeded,
            envelope.Payload,
            envelope.Error);
        ValidateFrame(frame);
        if (frame.RequestId != checked((long)rawRequestId))
        {
            throw ProtocolError(
                "UDBF1005",
                "Debug frame header and JSON envelope have different request IDs.");
        }
        return frame;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        string truncatedMessage,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw ProtocolError("UDBF1001", truncatedMessage);
            }
            offset += read;
        }
    }

    private static void ValidateFrame(CoreClrDebugFrame? frame)
    {
        if (frame is null ||
            frame.SchemaVersion != CoreClrDebugFrame.CurrentSchemaVersion ||
            !Enum.IsDefined(frame.Kind) ||
            frame.RequestId <= 0 ||
            !IsValidOperation(frame.Operation) ||
            frame.Payload.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolError(
                "UDBF1004",
                "Debug frame has an invalid schema, identity, operation, or payload object.");
        }

        bool validRequest = frame.Kind == CoreClrDebugFrameKind.Request &&
            frame.Succeeded is null && frame.Error is null;
        bool validSuccess = frame.Kind == CoreClrDebugFrameKind.Response &&
            frame.Succeeded == true && frame.Error is null;
        bool validFailure = frame.Kind == CoreClrDebugFrameKind.Response &&
            frame.Succeeded == false && frame.Error is
            {
                Code.Length: > 0,
                Message: not null,
                Type.Length: > 0,
            };
        if (!validRequest && !validSuccess && !validFailure)
        {
            throw ProtocolError(
                "UDBF1004",
                "Debug frame request/response success and error fields are inconsistent.");
        }
        if (frame.Error is { } error &&
            (string.IsNullOrEmpty(error.Code) || error.Code.Length > 32 ||
             error.Message is null || error.Message.Length > 4096 ||
             string.IsNullOrEmpty(error.Type) || error.Type.Length > 256))
        {
            throw ProtocolError("UDBF1004", "Debug frame error fields exceed their bounds.");
        }
    }

    private static bool IsValidOperation(string? operation) =>
        !string.IsNullOrWhiteSpace(operation) &&
        operation.Length <= 64 &&
        operation.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    private static CoreClrDebugFrameProtocolException ProtocolError(
        string code,
        string message) => new(code, message);

    private sealed record FrameEnvelope(
        string SchemaVersion,
        long RequestId,
        string Operation,
        bool? Succeeded,
        JsonElement Payload,
        CoreClrDebugFrameError? Error);

    private sealed record EmptyFramePayload;
}
