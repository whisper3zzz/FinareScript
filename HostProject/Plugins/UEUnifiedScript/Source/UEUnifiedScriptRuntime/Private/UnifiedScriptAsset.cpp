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

bool UUnifiedScriptAsset::ValidateTarget(
    const UObject* Target,
    FString& OutError) const
{
    if (RequiredTargetClass.IsNull())
    {
        OutError.Reset();
        return true;
    }

    UClass* RequiredClass = RequiredTargetClass.LoadSynchronous();
    if (RequiredClass == nullptr)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' cannot load required target class '%s'."),
            *GetPathName(),
            *RequiredTargetClass.ToSoftObjectPath().ToString());
        return false;
    }
    if (Target == nullptr || !Target->IsA(RequiredClass))
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' requires target class '%s', but received '%s'."),
            *GetPathName(),
            *RequiredClass->GetPathName(),
            Target != nullptr ? *Target->GetClass()->GetPathName() : TEXT("<null>"));
        return false;
    }

    OutError.Reset();
    return true;
}

FString UUnifiedScriptAsset::ResolveRequiredTargetClassPath() const
{
    return RequiredTargetClass.IsNull()
        ? FString()
        : RequiredTargetClass.ToSoftObjectPath().ToString();
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

FString UUnifiedScriptAsset::ResolveApiNodeCatalogPath() const
{
    FString Result = ApiNodeCatalogPath;
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
