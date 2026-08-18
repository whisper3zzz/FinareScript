using UnifiedScript;

namespace Samples.Patching;

public static class EditDecisionValues
{
    [Visual]
    public static int Run(int value, int stopAt, bool enabled)
    {
        int result = value + 1;
        if (enabled)
        {
            return result;
        }

        return value;
    }
}
