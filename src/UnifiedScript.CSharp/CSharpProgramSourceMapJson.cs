using System.Text.Json;
using UnifiedScript.Core;

namespace UnifiedScript.CSharp;

public static class CSharpProgramSourceMapJson
{
    public static string Serialize(CSharpProgramSourceMap document, bool writeIndented = true) =>
        JsonSerializer.Serialize(
            NormalizeAndValidate(document),
            UnifiedJson.CreateOptions(writeIndented));

    public static CSharpProgramSourceMap Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        CSharpProgramSourceMap? document = JsonSerializer.Deserialize<CSharpProgramSourceMap>(
            json,
            UnifiedJson.CreateOptions());
        return document is null
            ? throw new JsonException("The C# program source map was empty.")
            : NormalizeAndValidate(document);
    }

    public static CSharpProgramSourceMap NormalizeAndValidate(CSharpProgramSourceMap document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != CSharpProgramSourceMap.CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Unsupported source-map schema '{document.SchemaVersion}'. " +
                $"Expected '{CSharpProgramSourceMap.CurrentSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(document.AssemblyName) ||
            document.SourceCompilationId.IsEmpty ||
            document.SemanticConfigurationId.IsEmpty ||
            document.Methods is null)
        {
            throw new JsonException("A C# program source map has invalid required fields.");
        }

        var methodIds = new HashSet<SemanticId>();
        var metadataTokens = new HashSet<int>();
        var nodeIds = new HashSet<SemanticId>();
        foreach (CSharpMethodSourceMap? method in document.Methods)
        {
            ValidateMethod(method, methodIds, metadataTokens, nodeIds);
        }

        return document with
        {
            Methods = document.Methods
                .OrderBy(static method => method.MetadataName, StringComparer.Ordinal)
                .ThenBy(static method => method.MethodId.Value, StringComparer.Ordinal)
                .Select(NormalizeMethod)
                .ToArray(),
        };
    }

    private static void ValidateMethod(
        CSharpMethodSourceMap? method,
        ISet<SemanticId> methodIds,
        ISet<int> metadataTokens,
        ISet<SemanticId> nodeIds)
    {
        if (method is null || method.MethodId.IsEmpty ||
            string.IsNullOrWhiteSpace(method.MetadataName) ||
            (method.MetadataToken & unchecked((int)0xff000000)) != 0x06000000 ||
            method.EntryIlOffset < 0 ||
            method.SequencePoints is null ||
            method.Nodes is null)
        {
            throw new JsonException("A C# method source map has invalid required fields.");
        }

        if (!methodIds.Add(method.MethodId))
        {
            throw new JsonException($"Duplicate source-map method '{method.MethodId}'.");
        }

        if (!metadataTokens.Add(method.MetadataToken))
        {
            throw new JsonException(
                $"Duplicate source-map metadata token '0x{method.MetadataToken:x8}'.");
        }

        var offsets = new HashSet<int>();
        foreach (CSharpSequencePoint? point in method.SequencePoints)
        {
            if (point is null || point.IlOffset < 0 || !IsValidSpan(point.Span) ||
                !offsets.Add(point.IlOffset))
            {
                throw new JsonException(
                    $"Method '{method.MethodId}' has an invalid or duplicate sequence point.");
            }
        }

        if (method.SequencePoints.Count == 0 &&
            (method.EntryIlOffset is not null || method.Nodes.Count != 0))
        {
            throw new JsonException(
                $"Method '{method.MethodId}' without sequence points cannot map an entry or nodes.");
        }

        if (method.EntryIlOffset is int entryIlOffset && !offsets.Contains(entryIlOffset))
        {
            throw new JsonException(
                $"Method '{method.MethodId}' entry IL offset has no visible sequence point.");
        }

        foreach (CSharpNodeIlMap? node in method.Nodes)
        {
            if (node is null || node.NodeId.IsEmpty || !IsValidSpan(node.SourceSpan) ||
                node.IlOffsets is null || node.IlOffsets.Count == 0 ||
                node.IlOffsets.Any(offset => !offsets.Contains(offset)) ||
                node.IlOffsets.Distinct().Count() != node.IlOffsets.Count)
            {
                throw new JsonException(
                    $"Method '{method.MethodId}' has an invalid semantic-node mapping.");
            }

            if (!nodeIds.Add(node.NodeId))
            {
                throw new JsonException($"Duplicate source-map node '{node.NodeId}'.");
            }
        }
    }

    private static CSharpMethodSourceMap NormalizeMethod(CSharpMethodSourceMap method) => method with
    {
        SequencePoints = method.SequencePoints
            .OrderBy(static point => point.IlOffset)
            .Select(static point => point with { Span = NormalizeSpan(point.Span) })
            .ToArray(),
        Nodes = method.Nodes
            .OrderBy(static node => node.NodeId.Value, StringComparer.Ordinal)
            .Select(static node => node with
            {
                SourceSpan = NormalizeSpan(node.SourceSpan),
                IlOffsets = node.IlOffsets.Order().ToArray(),
            })
            .ToArray(),
    };

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
