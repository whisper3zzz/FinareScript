using UnifiedScript;

namespace Samples.Loops;

public static class DoWhileRules
{
    [Visual]
    public static int Countdown(int value, bool enabled)
    {
        do
        {
            value = value - 1;
            if (value == 2)
            {
                continue;
            }

            if (value == 1)
            {
                break;
            }
        }
        while (enabled && value > 0);

        return value;
    }
}
