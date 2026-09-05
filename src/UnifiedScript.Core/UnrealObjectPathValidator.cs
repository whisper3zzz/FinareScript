namespace UnifiedScript.Core;

/// <summary>
/// Validates the syntax of an Unreal object path without consulting the engine
/// or attempting to load the referenced object.
/// </summary>
public static class UnrealObjectPathValidator
{
    public static bool IsValid(string? path, bool allowEmpty = true)
    {
        if (path is null)
        {
            return false;
        }
        if (path.Length == 0)
        {
            return allowEmpty;
        }
        if (path[0] != '/' || path.Length == 1 ||
            path.Contains('\0') || path.Contains('\\') ||
            path.Contains('\r') || path.Contains('\n') || path.Contains('\t') ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string[] colonParts = path.Split(':');
        if (colonParts.Length is < 1 or > 2 ||
            colonParts.Any(static part => part.Length == 0))
        {
            return false;
        }
        string objectPath = colonParts[0];
        if (!IsPackageObjectPath(objectPath))
        {
            return false;
        }
        if (colonParts.Length == 2)
        {
            string[] subobjects = colonParts[1].Split('.');
            if (subobjects.Any(static segment => !IsObjectName(segment)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPackageObjectPath(string path)
    {
        string[] segments = path[1..].Split('/');
        if (segments.Length < 2 ||
            segments[..^1].Any(static segment => !IsPackageName(segment)))
        {
            return false;
        }
        string leaf = segments[^1];
        int dot = leaf.IndexOf('.');
        return dot > 0 && dot < leaf.Length - 1 &&
            leaf.IndexOf('.', dot + 1) < 0 &&
            IsPackageName(leaf[..dot]) && IsObjectName(leaf[(dot + 1)..]) &&
            path[..(path.Length - leaf.Length + dot)].Length <= 1023 &&
            leaf[(dot + 1)..].Length <= 1023;
    }

    private static bool IsPackageName(string value)
    {
        if (value.Length == 0 || value is "." or "..")
        {
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character) ||
                character is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or
                '|' or '\'' or ' ' or ',' or '.' or '&' or '!' or '~' or
                '\n' or '\r' or '\t' or '@' or '#')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsObjectName(string value)
    {
        if (value.Length == 0 || value is "." or "..")
        {
            return false;
        }
        foreach (char character in value)
        {
            if (char.IsControl(character) ||
                character is '"' or '\'' or ' ' or ',' or '/' or '\\' or '.' or ':' or
                '?' or '*' or '<' or '>' or '|' or '&' or '!' or '~' or '@' or '#' or '(' or ')' or '{' or
                '}' or '[' or ']' or '=' or ';' or '^' or '%' or '$' or '`')
            {
                return false;
            }
        }
        return true;
    }
}
