using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.Runtime;

public static class BreakpointProtocolJson
{
    public static string SerializeBindings(
        BreakpointBindingSnapshot snapshot,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidate(snapshot),
            UnifiedJson.CreateOptions(writeIndented));

    public static BreakpointBindingSnapshot DeserializeBindings(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        BreakpointBindingSnapshot? snapshot = JsonSerializer.Deserialize<BreakpointBindingSnapshot>(
            json,
            UnifiedJson.CreateOptions());
        return snapshot is null
            ? throw new JsonException("The breakpoint binding snapshot was empty.")
            : NormalizeAndValidate(snapshot);
    }

    public static string SerializeHit(CoreClrBreakpointHit hit, bool writeIndented = true) =>
        JsonSerializer.Serialize(
            NormalizeAndValidate(hit),
            UnifiedJson.CreateOptions(writeIndented));

    public static CoreClrBreakpointHit DeserializeHit(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        CoreClrBreakpointHit? hit = JsonSerializer.Deserialize<CoreClrBreakpointHit>(
            json,
            UnifiedJson.CreateOptions());
        return hit is null
            ? throw new JsonException("The breakpoint hit was empty.")
            : NormalizeAndValidate(hit);
    }

    public static string SerializeResolution(
        BreakpointHitResolution resolution,
        bool writeIndented = true) => JsonSerializer.Serialize(
            NormalizeAndValidate(resolution),
            UnifiedJson.CreateOptions(writeIndented));

    public static BreakpointHitResolution DeserializeResolution(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        BreakpointHitResolution? resolution = JsonSerializer.Deserialize<BreakpointHitResolution>(
            json,
            UnifiedJson.CreateOptions());
        return resolution is null
            ? throw new JsonException("The breakpoint hit resolution was empty.")
            : NormalizeAndValidate(resolution);
    }

    public static BreakpointBindingSnapshot NormalizeAndValidate(
        BreakpointBindingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != BreakpointBindingSnapshot.CurrentSchemaVersion)
        {
            throw UnsupportedSchema("breakpoint binding", snapshot.SchemaVersion,
                BreakpointBindingSnapshot.CurrentSchemaVersion);
        }

        ValidateGeneration(snapshot.Generation);
        if (snapshot.Bindings is null)
        {
            throw new JsonException("Breakpoint bindings cannot be null.");
        }

        var breakpointIds = new HashSet<SemanticId>();
        foreach (SemanticBreakpointBinding? binding in snapshot.Bindings)
        {
            ValidateBinding(binding, breakpointIds);
        }

        return snapshot with
        {
            Bindings = snapshot.Bindings
                .OrderBy(static binding => binding.BreakpointId.Value, StringComparer.Ordinal)
                .Select(static binding => binding with
                {
                    NodeSourceSpan = binding.NodeSourceSpan is null
                        ? null
                        : NormalizeSpan(binding.NodeSourceSpan),
                    Locations = binding.Locations
                        .OrderBy(static location => location.IlOffset)
                        .Select(static location => location with
                        {
                            Span = NormalizeSpan(location.Span),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
    }

    public static CoreClrBreakpointHit NormalizeAndValidate(CoreClrBreakpointHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.SchemaVersion != CoreClrBreakpointHit.CurrentSchemaVersion)
        {
            throw UnsupportedSchema(
                "breakpoint hit",
                hit.SchemaVersion,
                CoreClrBreakpointHit.CurrentSchemaVersion);
        }

        if (hit.DomainId.IsEmpty || hit.GenerationId.IsEmpty || hit.ImageKey.IsEmpty ||
            !IsMethodToken(hit.MetadataToken) || hit.IlOffset < 0)
        {
            throw new JsonException("A breakpoint hit has invalid required fields.");
        }

        return hit;
    }

    public static BreakpointHitResolution NormalizeAndValidate(
        BreakpointHitResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.SchemaVersion != BreakpointHitResolution.CurrentSchemaVersion)
        {
            throw UnsupportedSchema(
                "breakpoint resolution",
                resolution.SchemaVersion,
                BreakpointHitResolution.CurrentSchemaVersion);
        }

        CoreClrBreakpointHit hit = NormalizeAndValidate(resolution.Hit);
        if (resolution.MethodId.IsEmpty || resolution.Matches is null || resolution.Matches.Count == 0)
        {
            throw new JsonException("A breakpoint resolution has invalid required fields.");
        }

        var breakpointIds = new HashSet<SemanticId>();
        foreach (BreakpointHitMatch? match in resolution.Matches)
        {
            if (match is null || match.BreakpointId.IsEmpty || match.NodeId.IsEmpty ||
                !breakpointIds.Add(match.BreakpointId))
            {
                throw new JsonException("A breakpoint resolution has an invalid or duplicate match.");
            }
        }

        return resolution with
        {
            Hit = hit,
            Matches = resolution.Matches
                .OrderBy(static match => match.BreakpointId.Value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static void ValidateGeneration(CoreClrProgramGeneration? generation)
    {
        if (generation is null || generation.DomainId.IsEmpty || generation.Number <= 0 ||
            generation.GenerationId.IsEmpty || generation.ImageKey.IsEmpty ||
            generation.SourceCompilationId.IsEmpty || generation.SemanticConfigurationId.IsEmpty)
        {
            throw new JsonException("A CoreCLR program generation has invalid required fields.");
        }
    }

    private static void ValidateBinding(
        SemanticBreakpointBinding? binding,
        ISet<SemanticId> breakpointIds)
    {
        if (binding is null || binding.BreakpointId.IsEmpty || binding.NodeId.IsEmpty ||
            binding.Locations is null || !breakpointIds.Add(binding.BreakpointId))
        {
            throw new JsonException("A breakpoint binding has invalid or duplicate identity.");
        }

        switch (binding.State)
        {
            case BreakpointBindingState.Bound:
                if (binding.MethodId is not SemanticId methodId || methodId.IsEmpty ||
                    binding.MetadataToken is not int metadataToken || !IsMethodToken(metadataToken) ||
                    !IsValidSpan(binding.NodeSourceSpan) || binding.Locations.Count == 0 ||
                    !string.IsNullOrEmpty(binding.Message))
                {
                    throw new JsonException(
                        $"Bound breakpoint '{binding.BreakpointId}' has invalid required fields.");
                }

                var offsets = new HashSet<int>();
                foreach (CoreClrBreakpointLocation? location in binding.Locations)
                {
                    if (location is null || location.MetadataToken != metadataToken ||
                        location.IlOffset < 0 || !IsValidSpan(location.Span) ||
                        !offsets.Add(location.IlOffset))
                    {
                        throw new JsonException(
                            $"Bound breakpoint '{binding.BreakpointId}' has an invalid location.");
                    }
                }

                break;
            case BreakpointBindingState.Pending:
                ValidateUnboundShape(binding, requiresMessage: true);
                break;
            case BreakpointBindingState.Disabled:
                ValidateUnboundShape(binding, requiresMessage: false);
                break;
            default:
                throw new JsonException(
                    $"Breakpoint '{binding.BreakpointId}' has an unknown binding state.");
        }
    }

    private static void ValidateUnboundShape(
        SemanticBreakpointBinding binding,
        bool requiresMessage)
    {
        if (binding.MethodId is not null || binding.MetadataToken is not null ||
            binding.NodeSourceSpan is not null || binding.Locations.Count != 0 ||
            requiresMessage == string.IsNullOrWhiteSpace(binding.Message))
        {
            throw new JsonException(
                $"{binding.State} breakpoint '{binding.BreakpointId}' has invalid fields.");
        }
    }

    private static JsonException UnsupportedSchema(string kind, string actual, string expected) =>
        new($"Unsupported {kind} schema '{actual}'. Expected '{expected}'.");

    private static bool IsMethodToken(int token) =>
        (token & unchecked((int)0xff000000)) == 0x06000000;

    private static SourceSpan NormalizeSpan(SourceSpan span) => span with
    {
        FilePath = span.FilePath.Replace('\\', '/'),
    };

    private static bool IsValidSpan(SourceSpan? span) =>
        span is not null &&
        !string.IsNullOrWhiteSpace(span.FilePath) &&
        span.Start is not null && span.End is not null &&
        span.Start.Line > 0 && span.Start.Column > 0 &&
        span.End.Line > 0 && span.End.Column > 0 &&
        Compare(span.Start, span.End) <= 0;

    private static int Compare(SourcePosition left, SourcePosition right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }
}
