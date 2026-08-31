#include "UnifiedScriptAsset.h"

bool UUnifiedScriptAsset::ResolveConfiguration(
    FString& OutAssemblyPath,
    FString& OutTypeName,
    FString& OutError) const
{
    return ResolveConfiguration(true, OutAssemblyPath, OutTypeName, OutError);
}

bool UUnifiedScriptAsset::ResolveConfiguration(
    bool bPreferCompiledChunk,
    FString& OutAssemblyPath,
    FString& OutTypeName,
    FString& OutError) const
{
    OutAssemblyPath = bPreferCompiledChunk ? CompiledChunkPath : FString();
    OutAssemblyPath.TrimStartAndEndInline();
    if (OutAssemblyPath.IsEmpty())
    {
        OutAssemblyPath = ScriptAssemblyPath;
    }
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
            TEXT("UnifiedScript Asset '%s' requires CompiledChunkPath or ScriptAssemblyPath, and ScriptTypeName."),
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

bool UUnifiedScriptAsset::ValidateDeclarationIdentity(
    const FGuid& DeclarationId,
    const FString& DeclarationQualifiedName,
    FString& OutError) const
{
    if (!ScriptClassId.IsValid())
    {
        OutError.Reset();
        return true;
    }

    if (DeclarationId != ScriptClassId)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Class '%s' expects semantic ID '%s', but the selected declaration has '%s'. Rebuild or recreate the stale class proxy instead of binding by name."),
            *GetPathName(),
            *ScriptClassId.ToString(EGuidFormats::Digits),
            *DeclarationId.ToString(EGuidFormats::Digits));
        return false;
    }
    FString ExpectedTypeName = ScriptTypeName;
    ExpectedTypeName.TrimStartAndEndInline();
    if (DeclarationQualifiedName != ExpectedTypeName)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Class '%s' expects managed type '%s', but the selected declaration contains '%s'."),
            *GetPathName(),
            *ExpectedTypeName,
            *DeclarationQualifiedName);
        return false;
    }

    OutError.Reset();
    return true;
}

bool UUnifiedScriptAsset::ValidateGeneratedClassIdentity(
    const UObject* Host,
    const UObject* Target,
    FString& OutError) const
{
    if (ScriptClassKind == EUnifiedScriptClassKind::None)
    {
        OutError.Reset();
        return true;
    }
    if (!ScriptClassId.IsValid() || !GeneratedClassPath.IsValid())
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Class '%s' has incomplete persistent class identity."),
            *GetPathName());
        return false;
    }

    UClass* ExpectedClass = GeneratedClassPath.TryLoadClass<UObject>();
    const UObject* Candidate = ScriptClassKind == EUnifiedScriptClassKind::Actor
        ? Target
        : Host;
    if (ExpectedClass == nullptr || Candidate == nullptr || !Candidate->IsA(ExpectedClass))
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Class '%s' requires exact generated class '%s', but received '%s'."),
            *GetPathName(),
            *GeneratedClassPath.ToString(),
            Candidate != nullptr ? *Candidate->GetClass()->GetPathName() : TEXT("<null>"));
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

FString UUnifiedScriptAsset::ResolveCompiledChunkPath() const
{
    FString Result = CompiledChunkPath;
    Result.TrimStartAndEndInline();
    return Result;
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
