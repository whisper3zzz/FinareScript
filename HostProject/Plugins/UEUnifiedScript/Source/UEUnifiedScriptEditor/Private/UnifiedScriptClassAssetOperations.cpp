#include "UnifiedScriptClassAssetOperations.h"

#include "AssetToolsModule.h"
#include "Dom/JsonObject.h"
#include "Engine/Blueprint.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "Internationalization/Regex.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "ObjectTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UObject/ObjectRedirector.h"
#include "UObject/SavePackage.h"

namespace UE::UnifiedScript::ClassAssetOperations::Private
{
bool GAssetToolsMutationActive = false;

class FScopedAssetToolsMutation
{
public:
    FScopedAssetToolsMutation()
    {
        check(!GAssetToolsMutationActive);
        GAssetToolsMutationActive = true;
    }

    ~FScopedAssetToolsMutation()
    {
        GAssetToolsMutationActive = false;
    }
};

struct FFileSnapshot
{
    FString Path;
    bool bExisted = false;
    TArray<uint8> Bytes;
};

struct FClassOperationContext
{
    UUnifiedScriptAsset* ScriptAsset = nullptr;
    UBlueprint* Blueprint = nullptr;
    FString ProjectRoot;
    FString DeploymentPath;
    FString FirstPlayRequestPath;
    FString SourcePath;
    FString ConfiguredSourcePath;
    FString BlueprintPackagePath;
    FString ScriptAssetPackagePath;
    FString OldTypeName;
    FString ManagedNamespace;
    FString ManagedTypeName;
    FString DeclarationAssetPath;
    FString ClassId;
    FString Source;
    TSharedPtr<FJsonObject> DeploymentRoot;
    TArray<uint8> OriginalDeploymentBytes;
};

struct FTargetIdentity
{
    FString TypeName;
    FString BlueprintPackagePath;
    FString BlueprintPackageDirectory;
    FString BlueprintAssetPath;
    FString BlueprintClassPath;
    FString ScriptAssetName;
    FString ScriptAssetPackagePath;
    FString ScriptAssetObjectPath;
    FString SourcePath;
    FString ConfiguredSourcePath;
    FString ManagedTypeName;
    FString DeclarationAssetPath;
    FString ClassId;
};

bool IsManagedIdentifier(const FString& Value)
{
    FRegexMatcher Matcher(
        FRegexPattern(TEXT("^[A-Za-z_][A-Za-z0-9_]*$")),
        Value);
    return Matcher.FindNext();
}

bool ReadJsonFile(const FString& Path, TSharedPtr<FJsonObject>& OutRoot, FString& OutError)
{
    FString Json;
    if (!FFileHelper::LoadFileToString(Json, *Path))
    {
        OutError = FString::Printf(TEXT("Cannot read '%s'."), *Path);
        return false;
    }
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, OutRoot) || !OutRoot.IsValid())
    {
        OutError = FString::Printf(
            TEXT("Invalid JSON in '%s': %s"),
            *Path,
            *Reader->GetErrorMessage());
        return false;
    }
    return true;
}

FString ResolveRelativeTo(const FString& BaseDirectory, const FString& Value)
{
    FString Result = Value;
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(BaseDirectory, Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

bool WriteBytesAtomically(
    const FString& Path,
    const TArray<uint8>& Bytes,
    FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString Temporary = Path + TEXT(".unifiedscript-class-op.tmp");
    if (!FFileHelper::SaveArrayToFile(Bytes, *Temporary))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot write '%s'."), *Temporary);
        return false;
    }
    if (!IFileManager::Get().Move(*Path, *Temporary, true, true, false, true))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot publish '%s'."), *Path);
        return false;
    }
    return true;
}

bool WriteTextAtomically(const FString& Path, const FString& Text, FString& OutError)
{
    FTCHARToUTF8 Utf8(*Text);
    TArray<uint8> Bytes;
    Bytes.Append(
        reinterpret_cast<const uint8*>(Utf8.Get()),
        Utf8.Length());
    return WriteBytesAtomically(Path, Bytes, OutError);
}

bool CaptureFile(const FString& Path, FFileSnapshot& OutSnapshot, FString& OutError)
{
    FFileSnapshot Snapshot;
    Snapshot.Path = Path;
    Snapshot.bExisted = IFileManager::Get().FileExists(*Path);
    if (Snapshot.bExisted && !FFileHelper::LoadFileToArray(Snapshot.Bytes, *Path))
    {
        OutError = FString::Printf(TEXT("Cannot snapshot '%s'."), *Path);
        return false;
    }
    OutSnapshot = MoveTemp(Snapshot);
    return true;
}

bool RestoreFiles(const TArray<FFileSnapshot>& Snapshots, FString& OutError)
{
    TArray<FString> Failures;
    for (const FFileSnapshot& Snapshot : Snapshots)
    {
        FString RestoreError;
        if (Snapshot.bExisted)
        {
            if (!WriteBytesAtomically(Snapshot.Path, Snapshot.Bytes, RestoreError))
            {
                Failures.Add(RestoreError);
            }
        }
        else if (IFileManager::Get().FileExists(*Snapshot.Path) &&
            !IFileManager::Get().Delete(*Snapshot.Path, false, true))
        {
            Failures.Add(FString::Printf(
                TEXT("Cannot remove newly published file '%s'."),
                *Snapshot.Path));
        }
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}

bool VerifyFilesUnchanged(
    const TArray<FFileSnapshot>& Snapshots,
    FString& OutError)
{
    for (const FFileSnapshot& Snapshot : Snapshots)
    {
        const bool bExists = IFileManager::Get().FileExists(*Snapshot.Path);
        if (bExists != Snapshot.bExisted)
        {
            OutError = FString::Printf(
                TEXT("Script Class transaction conflict: '%s' changed after preflight."),
                *Snapshot.Path);
            return false;
        }
        if (!bExists)
        {
            continue;
        }
        TArray<uint8> CurrentBytes;
        if (!FFileHelper::LoadFileToArray(CurrentBytes, *Snapshot.Path) ||
            CurrentBytes != Snapshot.Bytes)
        {
            OutError = FString::Printf(
                TEXT("Script Class transaction conflict: '%s' changed after preflight."),
                *Snapshot.Path);
            return false;
        }
    }
    return true;
}

bool SerializeJson(
    const TSharedRef<FJsonObject>& Root,
    FString& OutJson,
    FString& OutError)
{
    OutJson.Reset();
    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Root, Writer))
    {
        OutError = TEXT("Cannot serialize the Script Class transaction JSON.");
        return false;
    }
    OutJson.AppendChar(TEXT('\n'));
    return true;
}

UUnifiedScriptAsset* GetBlueprintScriptAsset(UBlueprint* Blueprint)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return nullptr;
    }
    if (const AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptComponent != nullptr
            ? Defaults->ScriptComponent->ScriptAsset.Get()
            : nullptr;
    }
    if (const UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        return Defaults->ScriptAsset.Get();
    }
    return nullptr;
}

bool SetBlueprintScriptAsset(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FString& OutError)
{
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        OutError = TEXT("The generated Blueprint proxy is unavailable.");
        return false;
    }
    if (AUnifiedScriptActor* Defaults = Cast<AUnifiedScriptActor>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        if (Defaults->ScriptComponent == nullptr)
        {
            OutError = TEXT("The generated Actor proxy has no Script Component.");
            return false;
        }
        Defaults->ScriptComponent->Modify();
        Defaults->ScriptComponent->ScriptAsset = ScriptAsset;
        return true;
    }
    if (UUnifiedScriptComponent* Defaults = Cast<UUnifiedScriptComponent>(
            Blueprint->GeneratedClass->GetDefaultObject()))
    {
        Defaults->Modify();
        Defaults->ScriptAsset = ScriptAsset;
        return true;
    }
    OutError = TEXT("The generated proxy does not use a UnifiedScript host class.");
    return false;
}

bool BuildContext(
    UUnifiedScriptAsset* ScriptAsset,
    FClassOperationContext& OutContext,
    FString& OutError)
{
    if (!IsValid(ScriptAsset) || !ScriptAsset->ScriptClassId.IsValid() ||
        ScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::None)
    {
        OutError = TEXT("The selected Script Asset is not a persistent Script Class.");
        return false;
    }

    UClass* GeneratedClass = ScriptAsset->GeneratedClassPath.TryLoadClass<UObject>();
    UBlueprint* Blueprint = GeneratedClass != nullptr
        ? Cast<UBlueprint>(GeneratedClass->ClassGeneratedBy)
        : nullptr;
    if (Blueprint == nullptr || GetBlueprintScriptAsset(Blueprint) != ScriptAsset)
    {
        OutError = TEXT("The selected Script Asset is not owned by its exact generated Blueprint proxy.");
        return false;
    }

    FString ManagedNamespace;
    FString OldTypeName;
    if (!ScriptAsset->ScriptTypeName.Split(
            TEXT("."),
            &ManagedNamespace,
            &OldTypeName,
            ESearchCase::CaseSensitive,
            ESearchDir::FromEnd) ||
        !IsManagedIdentifier(OldTypeName))
    {
        OutError = FString::Printf(
            TEXT("Managed type identity '%s' is invalid."),
            *ScriptAsset->ScriptTypeName);
        return false;
    }

    FClassOperationContext Context;
    Context.ScriptAsset = ScriptAsset;
    Context.Blueprint = Blueprint;
    Context.ProjectRoot = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
    FPaths::NormalizeDirectoryName(Context.ProjectRoot);
    Context.FirstPlayRequestPath =
        FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(Context.ProjectRoot);
    Context.SourcePath =
        FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(ScriptAsset->SourcePath);
    Context.ConfiguredSourcePath = ScriptAsset->SourcePath;
    Context.BlueprintPackagePath = Blueprint->GetOutermost()->GetName();
    Context.ScriptAssetPackagePath = ScriptAsset->GetOutermost()->GetName();
    Context.OldTypeName = OldTypeName;
    Context.ManagedNamespace = ManagedNamespace;
    Context.ManagedTypeName = ScriptAsset->ScriptTypeName;
    Context.DeclarationAssetPath = ScriptAsset->DeclarationAssetPath;
    Context.ClassId = FString::Printf(
        TEXT("us_%s"),
        *ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower());

    if (!FFileHelper::LoadFileToString(Context.Source, *Context.SourcePath))
    {
        OutError = FString::Printf(
            TEXT("Cannot read managed source '%s'."),
            *Context.SourcePath);
        return false;
    }

    const FString OnboardingPath = FPaths::Combine(
        Context.ProjectRoot,
        TEXT("Config/UnifiedScript.onboarding.json"));
    TSharedPtr<FJsonObject> Onboarding;
    if (!ReadJsonFile(OnboardingPath, Onboarding, OutError))
    {
        return false;
    }
    const TSharedPtr<FJsonObject>* Deployment = nullptr;
    FString Manifest;
    if (!Onboarding->TryGetObjectField(TEXT("deployment"), Deployment) ||
        Deployment == nullptr ||
        !(*Deployment)->TryGetStringField(TEXT("manifest"), Manifest))
    {
        OutError = TEXT("UnifiedScript onboarding does not identify the deployment manifest.");
        return false;
    }
    Context.DeploymentPath = ResolveRelativeTo(
        FPaths::GetPath(OnboardingPath),
        Manifest);
    if (!ReadJsonFile(Context.DeploymentPath, Context.DeploymentRoot, OutError) ||
        !FFileHelper::LoadFileToArray(
            Context.OriginalDeploymentBytes,
            *Context.DeploymentPath))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Cannot snapshot deployment manifest '%s'."),
                *Context.DeploymentPath);
        }
        return false;
    }

    OutContext = MoveTemp(Context);
    OutError.Reset();
    return true;
}

bool BuildTarget(
    const FClassOperationContext& Context,
    const FString& TargetBlueprintPackagePath,
    const FGuid& ClassId,
    FTargetIdentity& OutTarget,
    FString& OutError)
{
    FText PackageError;
    if (!FPackageName::IsValidLongPackageName(
            TargetBlueprintPackagePath,
            false,
            &PackageError))
    {
        OutError = PackageError.ToString();
        return false;
    }
    const FString TypeName = FPackageName::GetLongPackageAssetName(
        TargetBlueprintPackagePath);
    if (!IsManagedIdentifier(TypeName))
    {
        OutError = FString::Printf(
            TEXT("'%s' is not a valid managed class name."),
            *TypeName);
        return false;
    }

    const FString PackageDirectory =
        FPackageName::GetLongPackagePath(TargetBlueprintPackagePath);
    const FString ScriptAssetName = TEXT("SA_") + TypeName;
    const FString ScriptAssetPackagePath =
        PackageDirectory / ScriptAssetName;
    if (TargetBlueprintPackagePath == Context.BlueprintPackagePath ||
        FindPackage(nullptr, *TargetBlueprintPackagePath) != nullptr ||
        FPackageName::DoesPackageExist(TargetBlueprintPackagePath) ||
        FindPackage(nullptr, *ScriptAssetPackagePath) != nullptr ||
        FPackageName::DoesPackageExist(ScriptAssetPackagePath))
    {
        OutError = FString::Printf(
            TEXT("Target Script Class assets already exist at '%s'."),
            *TargetBlueprintPackagePath);
        return false;
    }

    FTargetIdentity Target;
    Target.TypeName = TypeName;
    Target.BlueprintPackagePath = TargetBlueprintPackagePath;
    Target.BlueprintPackageDirectory = PackageDirectory;
    Target.BlueprintAssetPath = FString::Printf(
        TEXT("%s.%s"),
        *TargetBlueprintPackagePath,
        *TypeName);
    Target.BlueprintClassPath = Target.BlueprintAssetPath + TEXT("_C");
    Target.ScriptAssetName = ScriptAssetName;
    Target.ScriptAssetPackagePath = ScriptAssetPackagePath;
    Target.ScriptAssetObjectPath = FString::Printf(
        TEXT("%s.%s"),
        *ScriptAssetPackagePath,
        *ScriptAssetName);
    Target.SourcePath = FPaths::Combine(
        FPaths::GetPath(Context.SourcePath),
        TypeName + TEXT(".cs"));
    Target.ConfiguredSourcePath = FPaths::GetPath(Context.ConfiguredSourcePath) /
        (TypeName + TEXT(".cs"));
    Target.ConfiguredSourcePath.ReplaceInline(TEXT("\\"), TEXT("/"));
    Target.ManagedTypeName = Context.ManagedNamespace + TEXT(".") + TypeName;
    Target.DeclarationAssetPath = FPaths::GetPath(Context.DeclarationAssetPath) /
        TypeName;
    Target.ClassId = FString::Printf(
        TEXT("us_%s"),
        *ClassId.ToString(EGuidFormats::Digits).ToLower());
    if (IFileManager::Get().FileExists(*Target.SourcePath))
    {
        OutError = FString::Printf(
            TEXT("Target managed source already exists: '%s'."),
            *Target.SourcePath);
        return false;
    }
    OutTarget = MoveTemp(Target);
    OutError.Reset();
    return true;
}

bool ReplaceUniqueCapture(
    FString& Source,
    const FRegexPattern& Pattern,
    int32 CaptureIndex,
    const FString& Expected,
    const FString& Replacement,
    const TCHAR* Description,
    FString& OutError)
{
    FRegexMatcher Matcher(Pattern, Source);
    if (!Matcher.FindNext() || Matcher.GetCaptureGroup(CaptureIndex) != Expected)
    {
        OutError = FString::Printf(
            TEXT("Cannot find the exact %s identity '%s' in managed source."),
            Description,
            *Expected);
        return false;
    }
    const int32 Begin = Matcher.GetCaptureGroupBeginning(CaptureIndex);
    const int32 End = Matcher.GetCaptureGroupEnding(CaptureIndex);
    if (Matcher.FindNext())
    {
        OutError = FString::Printf(
            TEXT("Managed source contains more than one %s identity."),
            Description);
        return false;
    }
    Source = Source.Left(Begin) + Replacement + Source.Mid(End);
    return true;
}

bool TransformSource(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FString& OutSource,
    FString& OutError)
{
    FString Source = Context.Source;
    const FRegexPattern ScriptAssetPattern(TEXT(
        R"REGEX(\[\s*ScriptAsset\s*\(\s*"([^"]+)"\s*,\s*ClassId\s*=\s*"([^"]+)"\s*\)\s*\])REGEX"));
    if (!ReplaceUniqueCapture(
            Source,
            ScriptAssetPattern,
            2,
            Context.ClassId,
            Target.ClassId,
            TEXT("Script Class ID"),
            OutError) ||
        !ReplaceUniqueCapture(
            Source,
            ScriptAssetPattern,
            1,
            Context.DeclarationAssetPath,
            Target.DeclarationAssetPath,
            TEXT("declaration asset path"),
            OutError))
    {
        return false;
    }

    const FRegexPattern ClassPattern(FString::Printf(
        TEXT(R"(\bclass\s+(%s)\b)"),
        *Context.OldTypeName));
    if (!ReplaceUniqueCapture(
            Source,
            ClassPattern,
            1,
            Context.OldTypeName,
            Target.TypeName,
            TEXT("class declaration"),
            OutError))
    {
        return false;
    }
    OutSource = MoveTemp(Source);
    OutError.Reset();
    return true;
}

TSharedPtr<FJsonObject> FindDeploymentEntry(
    const FClassOperationContext& Context,
    TArray<TSharedPtr<FJsonValue>>& OutScripts,
    FString& OutError)
{
    const TArray<TSharedPtr<FJsonValue>>* ExistingScripts = nullptr;
    if (!Context.DeploymentRoot->TryGetArrayField(TEXT("scripts"), ExistingScripts) ||
        ExistingScripts == nullptr)
    {
        OutError = TEXT("UnifiedScript deployment scripts are unavailable.");
        return nullptr;
    }
    OutScripts = *ExistingScripts;
    TSharedPtr<FJsonObject> Match;
    for (const TSharedPtr<FJsonValue>& Value : OutScripts)
    {
        const TSharedPtr<FJsonObject> Entry = Value->AsObject();
        FString AssetPath;
        FString TypeName;
        if (Entry.IsValid() &&
            Entry->TryGetStringField(TEXT("assetPath"), AssetPath) &&
            Entry->TryGetStringField(TEXT("typeName"), TypeName) &&
            AssetPath == Context.ScriptAssetPackagePath &&
            TypeName == Context.ManagedTypeName)
        {
            if (Match.IsValid())
            {
                OutError = TEXT("Deployment contains duplicate entries for the selected Script Class.");
                return nullptr;
            }
            Match = Entry;
        }
    }
    if (!Match.IsValid())
    {
        OutError = TEXT("Deployment does not contain the exact selected Script Class identity.");
    }
    return Match;
}

bool BuildDeployment(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    bool bDuplicate,
    FString& OutJson,
    FString& OutError)
{
    TArray<TSharedPtr<FJsonValue>> Scripts;
    const TSharedPtr<FJsonObject> Existing = FindDeploymentEntry(
        Context,
        Scripts,
        OutError);
    if (!Existing.IsValid())
    {
        return false;
    }

    TSharedPtr<FJsonObject> TargetEntry = Existing;
    if (bDuplicate)
    {
        TargetEntry = MakeShared<FJsonObject>();
        TargetEntry->Values = Existing->Values;
        Scripts.Add(MakeShared<FJsonValueObject>(TargetEntry));
        Context.DeploymentRoot->SetArrayField(TEXT("scripts"), Scripts);
    }
    TargetEntry->SetStringField(TEXT("assetPath"), Target.ScriptAssetPackagePath);
    TargetEntry->SetStringField(TEXT("typeName"), Target.ManagedTypeName);
    TargetEntry->SetStringField(
        TEXT("sourcePath"),
        Target.ConfiguredSourcePath);
    TargetEntry->SetStringField(
        TEXT("declarationAssetPath"),
        Target.DeclarationAssetPath);
    return SerializeJson(Context.DeploymentRoot.ToSharedRef(), OutJson, OutError);
}

bool BuildFirstPlayRequest(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    FString& OutJson,
    FString& OutError)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"),
        TEXT("unifiedscript.first-play-request/v0.1"));
    Root->SetStringField(TEXT("requestId"), Target.ClassId);
    Root->SetStringField(TEXT("status"), TEXT("buildRequired"));
    Root->SetStringField(
        TEXT("classKind"),
        Context.ScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::Actor
            ? TEXT("actor")
            : TEXT("actorComponent"));
    Root->SetStringField(TEXT("blueprintAssetPath"), Target.BlueprintAssetPath);
    Root->SetStringField(TEXT("scriptAssetPath"), Target.ScriptAssetObjectPath);
    Root->SetStringField(
        TEXT("sourcePath"),
        FPaths::ConvertRelativePathToFull(Target.SourcePath));
    Root->SetStringField(TEXT("managedTypeName"), Target.ManagedTypeName);
    return SerializeJson(Root, OutJson, OutError);
}

bool CaptureExternalSnapshots(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    TArray<FFileSnapshot>& OutSnapshots,
    FString& OutError)
{
    const FString Paths[] = {
        Context.SourcePath,
        Target.SourcePath,
        Context.DeploymentPath,
        Context.FirstPlayRequestPath,
    };
    TArray<FFileSnapshot> Snapshots;
    for (const FString& Path : Paths)
    {
        if (Snapshots.ContainsByPredicate(
                [&Path](const FFileSnapshot& Item) { return Item.Path == Path; }))
        {
            continue;
        }
        FFileSnapshot Snapshot;
        if (!CaptureFile(Path, Snapshot, OutError))
        {
            return false;
        }
        Snapshots.Add(MoveTemp(Snapshot));
    }
    OutSnapshots = MoveTemp(Snapshots);
    return true;
}

bool PublishExternalFiles(
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FString& Source,
    const FString& Deployment,
    const FString& FirstPlayRequest,
    bool bRename,
    FString& OutError)
{
    if (!WriteTextAtomically(Target.SourcePath, Source, OutError) ||
        !WriteTextAtomically(Context.DeploymentPath, Deployment, OutError) ||
        !WriteTextAtomically(
            Context.FirstPlayRequestPath,
            FirstPlayRequest,
            OutError))
    {
        return false;
    }
    if (bRename && Context.SourcePath != Target.SourcePath &&
        !IFileManager::Get().Delete(*Context.SourcePath, false, true))
    {
        OutError = FString::Printf(
            TEXT("Cannot remove renamed source '%s'."),
            *Context.SourcePath);
        return false;
    }
    return true;
}

void ConfigureTargetScriptAsset(
    UUnifiedScriptAsset& ScriptAsset,
    const FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const FGuid& ClassId)
{
    ScriptAsset.Modify();
    ScriptAsset.ScriptClassId = ClassId;
    ScriptAsset.GeneratedClassPath = FSoftClassPath(Target.BlueprintClassPath);
    ScriptAsset.ScriptTypeName = Target.ManagedTypeName;
    ScriptAsset.SourcePath = Target.ConfiguredSourcePath;
    ScriptAsset.DeclarationAssetPath = Target.DeclarationAssetPath;
    ScriptAsset.MarkPackageDirty();
}

bool SaveClassPackages(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FString& OutError)
{
    if (Blueprint == nullptr || ScriptAsset == nullptr)
    {
        OutError = TEXT("Cannot save the complete Script Class asset transaction.");
        return false;
    }
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    SaveArguments.SaveFlags = SAVE_None;
    const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(
        Blueprint->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    const FString ScriptAssetFilename = FPackageName::LongPackageNameToFilename(
        ScriptAsset->GetOutermost()->GetName(),
        FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(
            Blueprint->GetOutermost(),
            Blueprint,
            *BlueprintFilename,
            SaveArguments) ||
        !UPackage::SavePackage(
            ScriptAsset->GetOutermost(),
            ScriptAsset,
            *ScriptAssetFilename,
            SaveArguments))
    {
        OutError = TEXT("Cannot save the complete Script Class asset transaction.");
        return false;
    }
    return true;
}

void FillResult(
    const TCHAR* Operation,
    const FTargetIdentity& Target,
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FUnifiedScriptClassAssetOperationResult& OutResult)
{
    OutResult.Operation = Operation;
    OutResult.TransactionId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    OutResult.BlueprintAssetPath = Target.BlueprintAssetPath;
    OutResult.ScriptAssetPath = Target.ScriptAssetObjectPath;
    OutResult.SourcePath = Target.SourcePath;
    OutResult.ManagedTypeName = Target.ManagedTypeName;
    OutResult.Blueprint = Blueprint;
    OutResult.ScriptAsset = ScriptAsset;
}

bool ShouldFail(
    EUnifiedScriptClassAssetOperationFailurePoint Actual,
    EUnifiedScriptClassAssetOperationFailurePoint Requested,
    FString& OutError)
{
    if (Actual != Requested)
    {
        return false;
    }
    OutError = FString::Printf(
        TEXT("Injected Script Class transaction failure at phase %d."),
        static_cast<int32>(Actual));
    return true;
}

bool RollbackRename(
    FClassOperationContext& Context,
    const FTargetIdentity& Target,
    const TArray<FFileSnapshot>& FileSnapshots,
    FString& OutError)
{
    TArray<FString> Failures;
    Context.ScriptAsset->Modify();
    Context.ScriptAsset->GeneratedClassPath = FSoftClassPath(FString::Printf(
        TEXT("%s.%s_C"),
        *Context.BlueprintPackagePath,
        *Context.OldTypeName));
    Context.ScriptAsset->ScriptTypeName = Context.ManagedTypeName;
    Context.ScriptAsset->SourcePath = Context.ConfiguredSourcePath;
    Context.ScriptAsset->DeclarationAssetPath = Context.DeclarationAssetPath;

    const FString OldDirectory = FPackageName::GetLongPackagePath(
        Context.BlueprintPackagePath);
    const FString OldSidecarDirectory = FPackageName::GetLongPackagePath(
        Context.ScriptAssetPackagePath);
    const FString OldSidecarName = FPackageName::GetLongPackageAssetName(
        Context.ScriptAssetPackagePath);
    TArray<FAssetRenameData> ReverseRenames;
    ReverseRenames.Emplace(Context.Blueprint, OldDirectory, Context.OldTypeName);
    ReverseRenames.Emplace(Context.ScriptAsset, OldSidecarDirectory, OldSidecarName);
    {
        FScopedAssetToolsMutation Mutation;
        if (!FAssetToolsModule::GetModule().Get().RenameAssets(ReverseRenames))
        {
            Failures.Add(TEXT("cannot restore renamed UE assets"));
        }
    }
    TArray<UObject*> TargetRedirectors;
    const FString TargetObjectPaths[] = {
        Target.BlueprintAssetPath,
        Target.ScriptAssetObjectPath,
    };
    for (const FString& ObjectPath : TargetObjectPaths)
    {
        if (UObjectRedirector* Redirector = LoadObject<UObjectRedirector>(
                nullptr,
                *ObjectPath,
                nullptr,
                LOAD_NoWarn | LOAD_Quiet))
        {
            TargetRedirectors.Add(Redirector);
        }
    }
    if (!TargetRedirectors.IsEmpty())
    {
        FScopedAssetToolsMutation Mutation;
        if (ObjectTools::DeleteObjectsUnchecked(TargetRedirectors) !=
            TargetRedirectors.Num())
        {
            Failures.Add(TEXT("cannot remove rollback redirectors"));
        }
    }
    const FString TargetPackages[] = {
        Target.BlueprintPackagePath,
        Target.ScriptAssetPackagePath,
    };
    for (const FString& PackageName : TargetPackages)
    {
        const FString Filename = FPackageName::LongPackageNameToFilename(
            PackageName,
            FPackageName::GetAssetPackageExtension());
        if (IFileManager::Get().FileExists(*Filename) &&
            !IFileManager::Get().Delete(*Filename, false, true))
        {
            Failures.Add(FString::Printf(
                TEXT("cannot remove rollback package '%s'"),
                *Filename));
        }
    }
    FString RestoreError;
    if (!RestoreFiles(FileSnapshots, RestoreError))
    {
        Failures.Add(RestoreError);
    }
    FString SaveError;
    if (!SaveClassPackages(Context.Blueprint, Context.ScriptAsset, SaveError))
    {
        Failures.Add(SaveError);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}

bool RollbackDuplicate(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    const TArray<FFileSnapshot>& FileSnapshots,
    FString& OutError)
{
    TArray<FString> Failures;
    if (Blueprint != nullptr || ScriptAsset != nullptr)
    {
        TArray<UObject*> Objects;
        if (Blueprint != nullptr)
        {
            Objects.Add(Blueprint);
        }
        if (ScriptAsset != nullptr)
        {
            Objects.Add(ScriptAsset);
        }
        FScopedAssetToolsMutation Mutation;
        if (ObjectTools::DeleteObjectsUnchecked(Objects) != Objects.Num())
        {
            Failures.Add(TEXT("cannot remove duplicated UE assets"));
        }
    }
    FString RestoreError;
    if (!RestoreFiles(FileSnapshots, RestoreError))
    {
        Failures.Add(RestoreError);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}
}

bool FUnifiedScriptClassAssetOperations::Rename(
    UUnifiedScriptAsset* ScriptAsset,
    const FString& TargetBlueprintPackagePath,
    FUnifiedScriptClassAssetOperationResult& OutResult,
    EUnifiedScriptClassAssetOperationFailurePoint FailurePoint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutResult = {};
    FClassOperationContext Context;
    FTargetIdentity Target;
    FString Error;
    if (!BuildContext(ScriptAsset, Context, Error) ||
        !BuildTarget(
            Context,
            TargetBlueprintPackagePath,
            ScriptAsset->ScriptClassId,
            Target,
            Error))
    {
        OutResult.Operation = TEXT("rename");
        OutResult.Error = Error;
        return false;
    }
    FillResult(TEXT("rename"), Target, Context.Blueprint, ScriptAsset, OutResult);

    FString TargetSource;
    FString Deployment;
    FString FirstPlayRequest;
    TArray<FFileSnapshot> FileSnapshots;
    if (!TransformSource(Context, Target, TargetSource, Error) ||
        !BuildDeployment(Context, Target, false, Deployment, Error) ||
        !BuildFirstPlayRequest(Context, Target, FirstPlayRequest, Error) ||
        !CaptureExternalSnapshots(Context, Target, FileSnapshots, Error))
    {
        OutResult.Error = Error;
        return false;
    }

    TArray<FAssetRenameData> Renames;
    Renames.Emplace(
        Context.Blueprint,
        Target.BlueprintPackageDirectory,
        Target.TypeName);
    Renames.Emplace(
        Context.ScriptAsset,
        Target.BlueprintPackageDirectory,
        Target.ScriptAssetName);
    {
        FScopedAssetToolsMutation Mutation;
        if (!FAssetToolsModule::GetModule().Get().RenameAssets(Renames))
        {
            OutResult.Error = TEXT("AssetTools could not rename the complete Script Class asset pair.");
            return false;
        }
    }

    ConfigureTargetScriptAsset(
        *ScriptAsset,
        Context,
        Target,
        ScriptAsset->ScriptClassId);
    FBlueprintEditorUtils::MarkBlueprintAsModified(Context.Blueprint);
    if (ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterAssetMutation,
            FailurePoint,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !PublishExternalFiles(
            Context,
            Target,
            TargetSource,
            Deployment,
            FirstPlayRequest,
            true,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication,
            FailurePoint,
            Error) ||
        !SaveClassPackages(Context.Blueprint, ScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackRename(
            Context,
            Target,
            FileSnapshots,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    OutResult.bSucceeded = true;
    return true;
}

bool FUnifiedScriptClassAssetOperations::Duplicate(
    UUnifiedScriptAsset* ScriptAsset,
    const FString& TargetBlueprintPackagePath,
    FUnifiedScriptClassAssetOperationResult& OutResult,
    EUnifiedScriptClassAssetOperationFailurePoint FailurePoint)
{
    using namespace UE::UnifiedScript::ClassAssetOperations::Private;
    OutResult = {};
    FClassOperationContext Context;
    FTargetIdentity Target;
    FString Error;
    const FGuid NewClassId = FGuid::NewGuid();
    if (!BuildContext(ScriptAsset, Context, Error) ||
        !BuildTarget(
            Context,
            TargetBlueprintPackagePath,
            NewClassId,
            Target,
            Error))
    {
        OutResult.Operation = TEXT("duplicate");
        OutResult.Error = Error;
        return false;
    }

    FString TargetSource;
    FString Deployment;
    FString FirstPlayRequest;
    TArray<FFileSnapshot> FileSnapshots;
    if (!TransformSource(Context, Target, TargetSource, Error) ||
        !BuildDeployment(Context, Target, true, Deployment, Error) ||
        !BuildFirstPlayRequest(Context, Target, FirstPlayRequest, Error) ||
        !CaptureExternalSnapshots(Context, Target, FileSnapshots, Error))
    {
        OutResult.Operation = TEXT("duplicate");
        OutResult.Error = Error;
        return false;
    }

    UBlueprint* NewBlueprint = nullptr;
    UUnifiedScriptAsset* NewScriptAsset = nullptr;
    {
        FScopedAssetToolsMutation Mutation;
        IAssetTools& AssetTools = FAssetToolsModule::GetModule().Get();
        NewBlueprint = Cast<UBlueprint>(AssetTools.DuplicateAsset(
            Target.TypeName,
            Target.BlueprintPackageDirectory,
            Context.Blueprint));
        if (NewBlueprint != nullptr)
        {
            NewScriptAsset = Cast<UUnifiedScriptAsset>(AssetTools.DuplicateAsset(
                Target.ScriptAssetName,
                Target.BlueprintPackageDirectory,
                ScriptAsset));
        }
    }
    FillResult(TEXT("duplicate"), Target, NewBlueprint, NewScriptAsset, OutResult);
    if (NewBlueprint == nullptr || NewScriptAsset == nullptr)
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            RollbackError);
        OutResult.Error = TEXT("AssetTools could not duplicate the complete Script Class asset pair.");
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    ConfigureTargetScriptAsset(
        *NewScriptAsset,
        Context,
        Target,
        NewClassId);
    if (!SetBlueprintScriptAsset(NewBlueprint, NewScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }
    FBlueprintEditorUtils::MarkBlueprintAsModified(NewBlueprint);
    FKismetEditorUtilities::CompileBlueprint(NewBlueprint);
    if (!SetBlueprintScriptAsset(NewBlueprint, NewScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    if (ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterAssetMutation,
            FailurePoint,
            Error) ||
        !VerifyFilesUnchanged(FileSnapshots, Error) ||
        !PublishExternalFiles(
            Context,
            Target,
            TargetSource,
            Deployment,
            FirstPlayRequest,
            false,
            Error) ||
        ShouldFail(
            EUnifiedScriptClassAssetOperationFailurePoint::AfterExternalPublication,
            FailurePoint,
            Error) ||
        !SaveClassPackages(NewBlueprint, NewScriptAsset, Error))
    {
        FString RollbackError;
        OutResult.bRolledBack = RollbackDuplicate(
            NewBlueprint,
            NewScriptAsset,
            FileSnapshots,
            RollbackError);
        OutResult.Error = Error;
        if (!RollbackError.IsEmpty())
        {
            OutResult.Error += TEXT(" Rollback failed: ") + RollbackError;
        }
        return false;
    }

    OutResult.bSucceeded = true;
    return true;
}

bool FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive()
{
    return UE::UnifiedScript::ClassAssetOperations::Private::GAssetToolsMutationActive;
}
