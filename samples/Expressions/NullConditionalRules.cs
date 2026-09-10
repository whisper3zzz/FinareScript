#nullable enable

using System;

namespace Finare.Samples.Expressions;

[AttributeUsage(AttributeTargets.Method)]
public sealed class VisualAttribute : Attribute;

public static class NullConditionalRules
{
    [Visual]
    public static int? Measure(string? value)
    {
        return value?.Length;
    }

    [Visual]
    public static int? MeasureObserved(string? value)
    {
        return Observe(value)?.Length;
    }

    [Visual]
    public static int? MeasureFallback(string? primary, string? fallback)
    {
        return (primary ?? fallback)?.Length;
    }

    private static string? Observe(string? value) => value;
}
