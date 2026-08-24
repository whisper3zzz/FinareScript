#include "UnifiedScriptAsset.h"

bool UUnifiedScriptAsset::ResolveConfiguration(
    FString& OutAssemblyPath,
    FString& OutTypeName,
    FString& OutError) const
{
    OutAssemblyPath = ScriptAssemblyPath;
    OutTypeName = ScriptTypeName;
    OutAssemblyPath.TrimStartAndEndInline();
    OutTypeName.TrimStartAndEndInline();

    if (SchemaVersion != 1)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' uses unsupported schema version %d."),
            *GetPathName(),
            SchemaVersion);
        return false;
    }
    if (OutAssemblyPath.IsEmpty() || OutTypeName.IsEmpty())
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' requires ScriptAssemblyPath and ScriptTypeName."),
            *GetPathName());
        return false;
    }

    OutError.Reset();
    return true;
}

void UUnifiedScriptAsset::ResolveDeclarationConfiguration(
    FString& OutDeclarationPath,
    FString& OutDeclarationAssetPath) const
{
    OutDeclarationPath = DeclarationPath;
    OutDeclarationAssetPath = DeclarationAssetPath;
    OutDeclarationPath.TrimStartAndEndInline();
    OutDeclarationAssetPath.TrimStartAndEndInline();
}

FString UUnifiedScriptAsset::ResolveAuthoringSurfacePath() const
{
    FString Result = AuthoringSurfacePath;
    Result.TrimStartAndEndInline();
    return Result;
}

FString UUnifiedScriptAsset::ResolveAuthoringServerUrl() const
{
    FString Result = AuthoringServerUrl;
    Result.TrimStartAndEndInline();
    while (Result.EndsWith(TEXT("/")))
    {
        Result.LeftChopInline(1);
    }
    return Result;
}
