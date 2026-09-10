using Finare;

namespace Samples.Patching;

public static class EditLoopRules
{
    [Visual]
    public static int Run(int value, int stopAt, bool enabled)
    {
        int result = value;
        while (result > 0)
        {
            result = result - 1;
        }

        return result;
    }
}
