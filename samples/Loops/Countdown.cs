using UnifiedScript;

namespace Samples.Loops;

public static class Countdown
{
    [Visual]
    public static int Run(int value, int stopAt, bool skipEven)
    {
        while (value > 0)
        {
            if (value == stopAt)
            {
                break;
            }

            value = value - 1;
            if (skipEven && value > 0)
            {
                continue;
            }
        }

        return value;
    }
}
