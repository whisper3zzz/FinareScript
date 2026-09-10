using Finare;

namespace Samples.Patching;

public static class EditForRules
{
    [Visual]
    public static int Run(int count, bool enabled)
    {
        int cursor = count;
        for (int index = 0; index < count; index++)
        {
            cursor = cursor + index;
        }

        for (cursor = 0; cursor < count; cursor++)
        {
            cursor = cursor + 1;
        }

        return cursor;
    }
}
