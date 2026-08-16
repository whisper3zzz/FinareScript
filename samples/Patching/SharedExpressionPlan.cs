using UnifiedScript;

namespace Samples.Patching;

public static class SharedExpressionPlan
{
    [Visual]
    public static float Combine(float damage)
    {
        return damage * 2.0f + damage * 3.0f + 1.0f;
    }
}
