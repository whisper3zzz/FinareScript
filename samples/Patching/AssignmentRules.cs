using UnifiedScript;

namespace Samples.Patching;

public static class AssignmentRules
{
    [Visual]
    public static int Increment(int seed)
    {
        int result = seed;
        return result;
    }
}
