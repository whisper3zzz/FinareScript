namespace UnifiedScript.CSharp;

public static class SemanticDiagnosticCodes
{
    public const string GraphUnsafeSignature = "UVS2001";
    public const string ConditionalBranchEffectsNotSupported = "UVS2002";
    public const string ShortCircuitRightEffectsNotSupported = "UVS2003";
    public const string CoalesceRightEffectsNotSupported = "UVS2004";
    public const string CoalesceConversionNotSupported = "UVS2005";
}
