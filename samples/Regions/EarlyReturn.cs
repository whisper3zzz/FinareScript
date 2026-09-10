using Finare;

namespace Samples.Regions;

public static class EarlyReturn
{
    [Visual]
    public static bool ShouldApply(int damage, int threshold, bool disabled)
    {
        if ((damage > threshold) && !disabled)
        {
            return true;
        }

        return false;
    }
}
