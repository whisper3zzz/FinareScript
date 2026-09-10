using Finare;

namespace Samples.Patching;

public static class GuardRules
{
    [Visual]
    public static bool ShouldApply(int damage, int threshold, bool disabled)
    {
        return true;
    }
}
