#include "UnifiedScriptAsset.h"

#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Components/ActorComponent.h"
#include "GameFramework/Actor.h"
#include "Kismet/BlueprintFunctionLibrary.h"

namespace UE::UnifiedScript::Asset::Private
{
FString ResolveRuntimePath(const FString& ConfiguredPath)
{
    FString Result = ConfiguredPath;
    FString ProjectDir = FPlatformProperties::RequiresCookedData()
        ? FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../.."))
        : FPaths::ProjectDir();
    ProjectDir = FPaths::ConvertRelativePathToFull(ProjectDir);
    FPaths::NormalizeDirectoryName(ProjectDir);

    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (Plugin.IsValid())
    {
        const FString PluginBaseDir =
            FPaths::ConvertRelativePathToFull(Plugin->GetBaseDir());
        Result.ReplaceInline(
            TEXT("{PluginDir}"),
            *PluginBaseDir,
            ESearchCase::IgnoreCase);
    }
    Result.ReplaceInline(
        TEXT("{ProjectDir}"),
        *ProjectDir,
        ESearchCase::IgnoreCase);
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(ProjectDir, Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}
}

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

bool UUnifiedScriptAsset::ResolveRuntimeConfiguration(
    bool bPreferCompiledChunk,
    FString& OutAssemblyPath,
    FString& OutTypeName,
    FString& OutError) const
{
    if (!ResolveConfiguration(
            bPreferCompiledChunk,
            OutAssemblyPath,
            OutTypeName,
            OutError))
    {
        return false;
    }

    OutAssemblyPath =
        UE::UnifiedScript::Asset::Private::ResolveRuntimePath(OutAssemblyPath);
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

bool UUnifiedScriptAsset::ValidateDeclarationClassBinding(
    const TOptional<FUnifiedScriptClassBindingDeclaration>& Binding,
    FString& OutError) const
{
    if (!Binding.IsSet())
    {
        OutError.Reset();
        return true;
    }

    const FUnifiedScriptClassBindingDeclaration& Declaration = Binding.GetValue();
    if (!Declaration.Validate(OutError))
    {
        return false;
    }

    if (ScriptClassKind == EUnifiedScriptClassKind::None)
    {
        if (Declaration.BaseClassPath.IsSet() || Declaration.GeneratedClassPath.IsSet())
        {
            OutError = FString::Printf(
                TEXT("UnifiedScript Asset '%s' has class binding paths but no persistent class kind."),
                *GetPathName());
            return false;
        }
        OutError.Reset();
        return true;
    }

    if (!ScriptClassId.IsValid() || !GeneratedClassPath.IsValid())
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' has no loadable persistent generated owner for class binding validation."),
            *GetPathName());
        return false;
    }

    const TCHAR* ExpectedKind = nullptr;
    switch (ScriptClassKind)
    {
    case EUnifiedScriptClassKind::Actor:
        ExpectedKind = TEXT("Actor");
        break;
    case EUnifiedScriptClassKind::ActorComponent:
        ExpectedKind = TEXT("ActorComponent");
        break;
    case EUnifiedScriptClassKind::Object:
        ExpectedKind = TEXT("Object");
        break;
    case EUnifiedScriptClassKind::BlueprintFunctionLibrary:
        ExpectedKind = TEXT("BlueprintFunctionLibrary");
        break;
    default:
        break;
    }
    if (ExpectedKind == nullptr ||
        !Declaration.Kind.Equals(ExpectedKind, ESearchCase::CaseSensitive))
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' class binding kind '%s' does not match persistent kind '%s'."),
            *GetPathName(),
            *Declaration.Kind,
            ExpectedKind != nullptr ? ExpectedKind : TEXT("<unknown>"));
        return false;
    }

    UClass* OwnerClass = GeneratedClassPath.TryLoadClass<UObject>();
    if (OwnerClass == nullptr)
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' cannot load generated class owner '%s' for class binding validation."),
            *GetPathName(),
            *GeneratedClassPath.ToString());
        return false;
    }
    const FString ActualOwnerPath = OwnerClass->GetPathName();
    if (!ActualOwnerPath.Equals(GeneratedClassPath.ToString(), ESearchCase::CaseSensitive))
    {
        OutError = FString::Printf(
            TEXT("UnifiedScript Asset '%s' configured generated owner '%s' resolves to '%s'."),
            *GetPathName(),
            *GeneratedClassPath.ToString(),
            *ActualOwnerPath);
        return false;
    }

    if (Declaration.GeneratedClassPath.IsSet())
    {
        const FString& ExpectedPath = Declaration.GeneratedClassPath.GetValue();
        if (!ActualOwnerPath.Equals(ExpectedPath, ESearchCase::CaseSensitive))
        {
            OutError = FString::Printf(
                TEXT("UnifiedScript Asset '%s' class binding generated class '%s' does not match selected owner '%s'."),
                *GetPathName(),
                *ExpectedPath,
                *GeneratedClassPath.ToString());
            return false;
        }
    }

    if (OwnerClass != nullptr)
    {
        bool bFamilyMatches = false;
        if (Declaration.Kind.Equals(TEXT("Actor"), ESearchCase::CaseSensitive))
        {
            bFamilyMatches = OwnerClass->IsChildOf(AActor::StaticClass());
        }
        else if (Declaration.Kind.Equals(TEXT("ActorComponent"), ESearchCase::CaseSensitive))
        {
            bFamilyMatches = OwnerClass->IsChildOf(UActorComponent::StaticClass());
        }
        else if (Declaration.Kind.Equals(TEXT("Object"), ESearchCase::CaseSensitive))
        {
            bFamilyMatches = OwnerClass->IsChildOf(UObject::StaticClass()) &&
                !OwnerClass->IsChildOf(AActor::StaticClass()) &&
                !OwnerClass->IsChildOf(UActorComponent::StaticClass()) &&
                !OwnerClass->IsChildOf(UBlueprintFunctionLibrary::StaticClass());
        }
        else if (Declaration.Kind.Equals(TEXT("BlueprintFunctionLibrary"), ESearchCase::CaseSensitive))
        {
            bFamilyMatches = OwnerClass->IsChildOf(UBlueprintFunctionLibrary::StaticClass());
        }
        if (!bFamilyMatches)
        {
            OutError = FString::Printf(
                TEXT("UnifiedScript Asset '%s' generated owner '%s' is not in class-binding family '%s'."),
                *GetPathName(),
                *OwnerClass->GetPathName(),
                *Declaration.Kind);
            return false;
        }

        if (Declaration.BaseClassPath.IsSet())
        {
            const FString ActualBasePath = OwnerClass->GetSuperClass() != nullptr
                ? OwnerClass->GetSuperClass()->GetPathName()
                : FString();
            if (!ActualBasePath.Equals(
                    Declaration.BaseClassPath.GetValue(), ESearchCase::CaseSensitive))
            {
                OutError = FString::Printf(
                    TEXT("UnifiedScript Asset '%s' class binding base '%s' does not match owner base '%s'."),
                    *GetPathName(),
                    *Declaration.BaseClassPath.GetValue(),
                    *ActualBasePath);
                return false;
            }
        }
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
    const UObject* Candidate =
        ScriptClassKind == EUnifiedScriptClassKind::Actor ||
        ScriptClassKind == EUnifiedScriptClassKind::Object
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
