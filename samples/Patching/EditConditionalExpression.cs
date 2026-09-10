using Finare;

namespace Game;

public static class ConditionalExpressionRules
{
    [Visual]
    public static int Select(bool enabled, int value, int fallback)
    {
        return value;
    }
}
