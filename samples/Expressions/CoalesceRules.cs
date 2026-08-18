using System;
using UnifiedScript;

namespace Game;

public static class CoalesceRules
{
    [Visual]
    public static int ResolveNumber(int? value, int fallback)
    {
        return value ?? fallback;
    }

    [Visual]
    public static string ResolveText(string? value, string fallback)
    {
        return value ?? fallback;
    }

    [Visual]
    public static int PollThenFallback(int fallback)
    {
        return Poll() ?? fallback;
    }

    [Visual]
    public static int ResolveMagnitude(int? value, int fallback)
    {
        return value ?? Math.Abs(fallback);
    }

    private static int? Poll()
    {
        return null;
    }
}
