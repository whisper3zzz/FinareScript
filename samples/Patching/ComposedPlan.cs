using UnifiedScript;

namespace Samples.Patching;

public static class ComposedPlan
{
    [Visual]
    public static float Scale(float damage)
    {
        float obsolete = 1.0f;
        return damage * 2.0f;
    }
}
