using Finare.Core;

namespace Finare.Runtime;

internal static class CoreClrDebugExpressionEvaluator
{
    private const int MaxExpressionLength = 256;
    private const int MaxSegments = 16;

    public static CoreClrDebugEvaluation Evaluate(
        CoreClrProgramGeneration generation,
        SemanticId pauseId,
        long frameId,
        string expression,
        IReadOnlyList<CoreClrDebugValue> locals)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(locals);
        string normalized = expression?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > MaxExpressionLength)
        {
            throw InvalidExpression(
                $"A watch expression must contain between 1 and {MaxExpressionLength} characters.");
        }

        int offset = 0;
        string rootName = ReadIdentifier(normalized, ref offset);
        CoreClrDebugValue value = locals.FirstOrDefault(local =>
                string.Equals(local.Name, rootName, StringComparison.Ordinal)) ??
            throw InvalidExpression($"Local or parameter '{rootName}' is not available in frame '{frameId}'.");
        int segments = 0;
        while (offset < normalized.Length)
        {
            if (++segments > MaxSegments)
            {
                throw InvalidExpression(
                    $"A watch expression may traverse at most {MaxSegments} fields or array elements.");
            }

            if (normalized[offset] == '.')
            {
                offset++;
                string member = ReadIdentifier(normalized, ref offset);
                value = value.Children.FirstOrDefault(child =>
                        string.Equals(child.Name, member, StringComparison.Ordinal)) ??
                    throw InvalidExpression(
                        $"Value '{value.Name}' has no captured public field named '{member}'.");
                continue;
            }

            if (normalized[offset] == '[')
            {
                int close = normalized.IndexOf(']', offset + 1);
                if (close < 0 || !int.TryParse(
                        normalized.AsSpan(offset + 1, close - offset - 1),
                        out int index) || index < 0)
                {
                    throw InvalidExpression("Array traversal requires a non-negative integer index.");
                }

                string childName = $"[{index}]";
                value = value.Children.FirstOrDefault(child => child.Name == childName) ??
                    throw InvalidExpression(
                        $"Array element '{childName}' was not captured or is outside the snapshot bound.");
                offset = close + 1;
                continue;
            }

            throw InvalidExpression(
                $"Unexpected token '{normalized[offset]}' at character {offset + 1}. " +
                "Only local names, public-field access, and constant array indexes are allowed.");
        }

        return new CoreClrDebugEvaluation(
            CoreClrDebugEvaluation.CurrentSchemaVersion,
            generation,
            pauseId,
            frameId,
            normalized,
            value with { Name = normalized });
    }

    private static string ReadIdentifier(string expression, ref int offset)
    {
        int start = offset;
        if (offset < expression.Length && expression[offset] == '@')
        {
            offset++;
        }

        if (offset >= expression.Length ||
            !(char.IsLetter(expression[offset]) || expression[offset] == '_'))
        {
            throw InvalidExpression($"Expected an identifier at character {start + 1}.");
        }

        offset++;
        while (offset < expression.Length &&
               (char.IsLetterOrDigit(expression[offset]) || expression[offset] == '_'))
        {
            offset++;
        }

        string identifier = expression[start..offset];
        return identifier.StartsWith('@') ? identifier[1..] : identifier;
    }

    private static BreakpointProtocolException InvalidExpression(string message) => new(
        "UDB1009",
        message);
}
