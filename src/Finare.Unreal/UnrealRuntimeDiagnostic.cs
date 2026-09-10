using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finare.Unreal;

public sealed record UnrealRuntimeSourceLocation(
    string FilePath,
    int Line,
    int Column,
    string MethodName);

public sealed record UnrealRuntimeBridgeFailure(
    string Error,
    string HandleError,
    string TargetHandle,
    string MemberId,
    string MemberPath);

/// <summary>
/// Detached, bounded failure context crossing the managed World boundary.
/// Numeric native identities are strings so JSON consumers never lose uint64 precision.
/// </summary>
public sealed record UnrealRuntimeDiagnostic(
    string SchemaVersion,
    long Sequence,
    string TimestampUtc,
    string Kind,
    string Operation,
    string Message,
    string ExceptionType,
    string StackTrace,
    string WorldHostId,
    string InstanceId,
    int Generation,
    string AssemblyPath,
    string TypeName,
    string HostHandle,
    string TargetHandle,
    UnrealRuntimeSourceLocation? Source,
    IReadOnlyList<UnrealRuntimeSourceLocation> SourceFrames,
    UnrealRuntimeBridgeFailure? Bridge)
{
    public const string CurrentSchemaVersion = "unifiedscript.runtime-diagnostic/v0.1";
}

internal sealed record UnrealRuntimeDiagnosticContext(
    int Generation,
    string AssemblyPath,
    string TypeName,
    UnrealObjectHandle Host,
    UnrealObjectHandle Target);

internal static class UnrealRuntimeDiagnosticBuilder
{
    private const int MaximumMessageCharacters = 4096;
    private const int MaximumStackTraceCharacters = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static long _nextSequence;

    public static string CreateJson(
        ulong worldHostId,
        ulong instanceId,
        string operation,
        Exception exception,
        UnrealRuntimeDiagnosticContext? context)
        => JsonSerializer.Serialize(
            Create(worldHostId, instanceId, operation, exception, context),
            JsonOptions);

    internal static UnrealRuntimeDiagnostic Create(
        ulong worldHostId,
        ulong instanceId,
        string operation,
        Exception exception,
        UnrealRuntimeDiagnosticContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);

        Exception primary = Unwrap(exception);
        UnrealInvocationException? bridgeException =
            FindException<UnrealInvocationException>(primary);
        IReadOnlyList<UnrealRuntimeSourceLocation> sourceFrames = FindSources(primary);
        UnrealRuntimeSourceLocation? source = SelectPrimarySource(
            sourceFrames,
            context?.TypeName);
        string hostHandle = FormatHandle(context?.Host ?? default);
        string targetHandle = FormatHandle(context?.Target ?? default);
        UnrealRuntimeBridgeFailure? bridge = bridgeException is null
            ? null
            : new UnrealRuntimeBridgeFailure(
                bridgeException.Error.ToString(),
                bridgeException.HandleError.ToString(),
                FormatHandle(bridgeException.Target.IsWellFormed
                    ? bridgeException.Target
                    : context?.Target ?? default),
                bridgeException.MemberId is ulong memberId
                    ? $"0x{memberId:X16}"
                    : string.Empty,
                bridgeException.MemberPath);

        return new UnrealRuntimeDiagnostic(
            UnrealRuntimeDiagnostic.CurrentSchemaVersion,
            Interlocked.Increment(ref _nextSequence),
            DateTimeOffset.UtcNow.ToString("O"),
            bridgeException is null ? "managedException" : "nativeBridgeFailure",
            operation,
            Bound(primary.Message, MaximumMessageCharacters),
            primary.GetType().FullName ?? primary.GetType().Name,
            Bound(primary.ToString(), MaximumStackTraceCharacters),
            worldHostId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            context?.Generation ?? 0,
            context?.AssemblyPath ?? string.Empty,
            context?.TypeName ?? string.Empty,
            hostHandle,
            targetHandle,
            source,
            sourceFrames,
            bridge);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }
        return exception;
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        if (exception is TException match)
        {
            return match;
        }
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                if (FindException<TException>(inner) is { } nested)
                {
                    return nested;
                }
            }
        }
        return exception.InnerException is { } innerException
            ? FindException<TException>(innerException)
            : null;
    }

    private static IReadOnlyList<UnrealRuntimeSourceLocation> FindSources(Exception exception)
    {
        var result = new List<UnrealRuntimeSourceLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Exception? current = exception;
        while (current is not null && result.Count < 32)
        {
            foreach (StackFrame frame in new StackTrace(current, fNeedFileInfo: true)
                         .GetFrames() ?? [])
            {
                if (string.IsNullOrWhiteSpace(frame.GetFileName()) ||
                    frame.GetFileLineNumber() <= 0)
                {
                    continue;
                }
                UnrealRuntimeSourceLocation source = CreateSource(frame);
                string key = $"{source.FilePath}|{source.Line}|{source.Column}|{source.MethodName}";
                if (seen.Add(key))
                {
                    result.Add(source);
                    if (result.Count == 32)
                    {
                        break;
                    }
                }
            }
            current = current.InnerException;
        }
        return result;
    }

    private static UnrealRuntimeSourceLocation? SelectPrimarySource(
        IReadOnlyList<UnrealRuntimeSourceLocation> sources,
        string? scriptTypeName)
    {
        if (!string.IsNullOrWhiteSpace(scriptTypeName))
        {
            UnrealRuntimeSourceLocation? scriptFrame = sources.FirstOrDefault(source =>
                source.MethodName.StartsWith(scriptTypeName + ".", StringComparison.Ordinal));
            if (scriptFrame is not null)
            {
                return scriptFrame;
            }
        }
        UnrealRuntimeSourceLocation? userFrame = sources.FirstOrDefault(source =>
            !source.MethodName.StartsWith("Finare.Unreal.", StringComparison.Ordinal) &&
            !source.FilePath.EndsWith(".bindings.cs", StringComparison.OrdinalIgnoreCase));
        return userFrame ?? sources.FirstOrDefault();
    }

    private static UnrealRuntimeSourceLocation CreateSource(StackFrame frame)
    {
        MethodBase? method = frame.GetMethod();
        return new UnrealRuntimeSourceLocation(
            frame.GetFileName()!,
            frame.GetFileLineNumber(),
            Math.Max(frame.GetFileColumnNumber(), 1),
            method is null
                ? string.Empty
                : $"{method.DeclaringType?.FullName}.{method.Name}".TrimStart('.'));
    }

    private static string FormatHandle(UnrealObjectHandle handle) =>
        handle.IsWellFormed ? $"0x{handle.Value:X16}" : string.Empty;

    private static string Bound(string? value, int maximumCharacters)
    {
        string text = value ?? string.Empty;
        return text.Length <= maximumCharacters
            ? text
            : string.Concat(text.AsSpan(0, maximumCharacters), "\n...[truncated]");
    }
}
