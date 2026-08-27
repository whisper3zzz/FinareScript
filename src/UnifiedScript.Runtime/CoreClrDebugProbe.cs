using System.Reflection;

namespace UnifiedScript.Runtime;

public static class CoreClrDebugProbe
{
    private static readonly AsyncLocal<ICoreClrDebugProbeSink?> Current = new();
    private static readonly AsyncLocal<ProbeFrame?> CurrentFrame = new();

    public static CoreClrDebugValue Capture<T>(string name, T value) =>
        DebugValueSnapshotter.Capture(name, value, typeof(T));

    public static IDisposable Enter(string methodId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        ProbeFrame? previous = CurrentFrame.Value;
        var frame = new ProbeFrame(methodId, previous);
        CurrentFrame.Value = frame;
        return new FrameAttachment(frame, previous);
    }

    public static void Hit(
        string methodId,
        string nodeId,
        params CoreClrDebugValue[] locals)
    {
        IReadOnlyList<CoreClrDebugValue> capturedLocals = locals?.ToArray() ?? [];
        ProbeFrame? frame = CurrentFrame.Value;
        if (frame is not null && string.Equals(frame.MethodId, methodId, StringComparison.Ordinal))
        {
            frame.NodeId = nodeId;
            frame.Locals = capturedLocals;
        }

        Current.Value?.Hit(methodId, nodeId, capturedLocals, SnapshotFrames(frame));
    }

    internal static IDisposable Attach(ICoreClrDebugProbeSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ICoreClrDebugProbeSink? previous = Current.Value;
        ProbeFrame? previousFrame = CurrentFrame.Value;
        Current.Value = sink;
        CurrentFrame.Value = null;
        return new SinkAttachment(previous, previousFrame);
    }

    private static IReadOnlyList<CoreClrDebugFrameSnapshot> SnapshotFrames(ProbeFrame? frame)
    {
        var result = new List<CoreClrDebugFrameSnapshot>();
        for (ProbeFrame? current = frame; current is not null; current = current.Parent)
        {
            result.Add(new CoreClrDebugFrameSnapshot(
                current.MethodId,
                current.NodeId,
                current.Locals.ToArray()));
        }

        return result;
    }

    private sealed class SinkAttachment(
        ICoreClrDebugProbeSink? previous,
        ProbeFrame? previousFrame) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            Current.Value = previous;
            CurrentFrame.Value = previousFrame;
            disposed = true;
        }
    }

    private sealed class FrameAttachment(ProbeFrame frame, ProbeFrame? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (ReferenceEquals(CurrentFrame.Value, frame))
            {
                CurrentFrame.Value = previous;
            }

            disposed = true;
        }
    }

    private sealed class ProbeFrame(string methodId, ProbeFrame? parent)
    {
        public string MethodId { get; } = methodId;

        public ProbeFrame? Parent { get; } = parent;

        public string NodeId { get; set; } = string.Empty;

        public IReadOnlyList<CoreClrDebugValue> Locals { get; set; } = [];
    }
}

internal interface ICoreClrDebugProbeSink
{
    void Hit(
        string methodId,
        string nodeId,
        IReadOnlyList<CoreClrDebugValue> locals,
        IReadOnlyList<CoreClrDebugFrameSnapshot> frames);
}

internal sealed record CoreClrDebugFrameSnapshot(
    string MethodId,
    string NodeId,
    IReadOnlyList<CoreClrDebugValue> Locals);

internal static class DebugValueSnapshotter
{
    private const int MaxDepth = 3;
    private const int MaxChildren = 32;

    public static CoreClrDebugValue Capture(string name, object? value, Type declaredType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(declaredType);
        return CaptureCore(
            name,
            value,
            declaredType,
            depth: 0,
            new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static CoreClrDebugValue CaptureCore(
        string name,
        object? value,
        Type declaredType,
        int depth,
        HashSet<object> ancestors)
    {
        string typeName = ExecutionValueCodec.GetMetadataName(declaredType);
        if (value is null)
        {
            return new CoreClrDebugValue(
                name,
                typeName,
                "null",
                CoreClrDebugValueKind.Null,
                [],
                Truncated: false);
        }

        Type runtimeType = value.GetType();
        typeName = ExecutionValueCodec.GetMetadataName(runtimeType);
        if (IsScalar(runtimeType))
        {
            try
            {
                return new CoreClrDebugValue(
                    name,
                    typeName,
                    ExecutionValueCodec.EncodeScalar(value, runtimeType),
                    CoreClrDebugValueKind.Scalar,
                    [],
                    Truncated: false);
            }
            catch (Exception exception) when (exception is ExecutionInvocationException or
                InvalidCastException or FormatException)
            {
                return Unavailable(name, typeName);
            }
        }

        bool tracksIdentity = !runtimeType.IsValueType;
        if (tracksIdentity && !ancestors.Add(value))
        {
            return new CoreClrDebugValue(
                name,
                typeName,
                "<cycle>",
                CoreClrDebugValueKind.Cycle,
                [],
                Truncated: false);
        }

        try
        {
            if (value is Array array)
            {
                return CaptureArray(name, typeName, array, depth, ancestors);
            }

            return CaptureObject(name, typeName, value, runtimeType, depth, ancestors);
        }
        finally
        {
            if (tracksIdentity)
            {
                _ = ancestors.Remove(value);
            }
        }
    }

    private static CoreClrDebugValue CaptureArray(
        string name,
        string typeName,
        Array array,
        int depth,
        HashSet<object> ancestors)
    {
        if (depth >= MaxDepth)
        {
            return new CoreClrDebugValue(
                name,
                typeName,
                $"[{array.Length}]",
                CoreClrDebugValueKind.Array,
                [],
                Truncated: array.Length > 0);
        }

        Type elementType = array.GetType().GetElementType() ?? typeof(object);
        int count = Math.Min(array.Length, MaxChildren);
        var children = new List<CoreClrDebugValue>(count);
        System.Collections.IEnumerator values = array.GetEnumerator();
        for (int index = 0; index < count && values.MoveNext(); index++)
        {
            children.Add(CaptureCore(
                $"[{index}]",
                values.Current,
                elementType,
                depth + 1,
                ancestors));
        }

        return new CoreClrDebugValue(
            name,
            typeName,
            $"[{array.Length}]",
            CoreClrDebugValueKind.Array,
            children,
            Truncated: array.Length > count);
    }

    private static CoreClrDebugValue CaptureObject(
        string name,
        string typeName,
        object value,
        Type runtimeType,
        int depth,
        HashSet<object> ancestors)
    {
        FieldInfo[] fields = runtimeType
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(static field => field.Name, StringComparer.Ordinal)
            .ToArray();
        if (depth >= MaxDepth)
        {
            return new CoreClrDebugValue(
                name,
                typeName,
                $"{{{typeName}}}",
                CoreClrDebugValueKind.Object,
                [],
                Truncated: fields.Length > 0);
        }

        int count = Math.Min(fields.Length, MaxChildren);
        var children = new List<CoreClrDebugValue>(count);
        for (int index = 0; index < count; index++)
        {
            FieldInfo field = fields[index];
            try
            {
                children.Add(CaptureCore(
                    field.Name,
                    field.GetValue(value),
                    field.FieldType,
                    depth + 1,
                    ancestors));
            }
            catch (Exception exception) when (exception is ArgumentException or
                FieldAccessException or TargetException)
            {
                children.Add(Unavailable(
                    field.Name,
                    ExecutionValueCodec.GetMetadataName(field.FieldType)));
            }
        }

        return new CoreClrDebugValue(
            name,
            typeName,
            $"{{{typeName}}}",
            CoreClrDebugValueKind.Object,
            children,
            Truncated: fields.Length > count);
    }

    private static bool IsScalar(Type type) =>
        type.IsEnum ||
        type == typeof(IntPtr) ||
        type == typeof(UIntPtr) ||
        Type.GetTypeCode(type) != TypeCode.Object;

    private static CoreClrDebugValue Unavailable(string name, string typeName) => new(
        name,
        typeName,
        "<unavailable>",
        CoreClrDebugValueKind.Unavailable,
        [],
        Truncated: false);
}
