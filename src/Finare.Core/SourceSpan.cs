namespace Finare.Core;

public sealed record SourcePosition(int Line, int Column);

public sealed record SourceSpan(
    string FilePath,
    SourcePosition Start,
    SourcePosition End)
{
    public static SourceSpan Unknown(string filePath = "<unknown>") =>
        new(filePath, new SourcePosition(0, 0), new SourcePosition(0, 0));
}

