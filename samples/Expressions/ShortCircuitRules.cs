using Finare;

namespace Game;

public static class ShortCircuitRules
{
    [Visual]
    public static bool All(bool enabled, bool ready)
    {
        return enabled && ready;
    }

    [Visual]
    public static bool Any(bool cached, bool ready)
    {
        return cached || ready;
    }

    [Visual]
    public static bool PollThenCheck(bool enabled)
    {
        return Poll() && enabled;
    }

    private static bool Poll()
    {
        return true;
    }
}
