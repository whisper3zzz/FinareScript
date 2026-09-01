#include "UnifiedScriptClassFactory.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "HAL/FileManager.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Internationalization/Regex.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptActor.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassOperationJournal.h"
#include "UnifiedScriptClassBases.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"

#define LOCTEXT_NAMESPACE "UnifiedScriptClassFactory"

namespace UE::UnifiedScript::ClassFactory::Private
{
struct FProjectClassSettings
{
    FString ProjectRoot;
    FString PluginRoot;
    FString ManagedProject;
    FString ManagedNamespace;
    FString AssemblyName;
    FString SourceDirectory;
    FString DeploymentPath;
    FString CompiledChunkPath;
    TSharedPtr<FJsonObject> DeploymentRoot;
    TSharedPtr<FJsonObject> BaselineDeployment;
};

struct FScriptClassDescriptor
{
    const TCHAR* KindToken = TEXT("");
    const TCHAR* RequiredTargetClassPath = TEXT("");
    UClass* ParentClass = nullptr;
    UClass* RequiredTargetClass = nullptr;
    EBlueprintType BlueprintType = BPTYPE_Normal;
    bool bSupportsFirstPlay = false;
};

bool TryDescribeScriptClassKind(
    EUnifiedScriptClassKind Kind,
    FScriptClassDescriptor& OutDescriptor)
{
    switch (Kind)
    {
    case EUnifiedScriptClassKind::Actor:
        OutDescriptor = {
            TEXT("actor"),
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptActor"),
            AUnifiedScriptActor::StaticClass(),
            AUnifiedScriptActor::StaticClass(),
            BPTYPE_Normal,
            true,
        };
        return true;
    case EUnifiedScriptClassKind::ActorComponent:
        OutDescriptor = {
            TEXT("actorComponent"),
            TEXT("/Script/Engine.Actor"),
            UUnifiedScriptComponent::StaticClass(),
            AActor::StaticClass(),
            BPTYPE_Normal,
            true,
        };
        return true;
    case EUnifiedScriptClassKind::Object:
        OutDescriptor = {
            TEXT("object"),
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptObject"),
            UUnifiedScriptObject::StaticClass(),
            UUnifiedScriptObject::StaticClass(),
            BPTYPE_Normal,
            false,
        };
        return true;
    case EUnifiedScriptClassKind::BlueprintFunctionLibrary:
        OutDescriptor = {
            TEXT("blueprintFunctionLibrary"),
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptBlueprintFunctionLibrary"),
            UUnifiedScriptBlueprintFunctionLibrary::StaticClass(),
            UUnifiedScriptBlueprintFunctionLibrary::StaticClass(),
            BPTYPE_FunctionLibrary,
            false,
        };
        return true;
    default:
        OutDescriptor = {};
        return false;
    }
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

bool ReadXmlElement(const FString& Xml, const TCHAR* Element, FString& OutValue)
{
    const FRegexPattern Pattern(FString::Printf(
        TEXT("<%s(?:\\s[^>]*)?>([^<]+)</%s>"),
        Element,
        Element));
    FRegexMatcher Matcher(Pattern, Xml);
    if (!Matcher.FindNext())
    {
        return false;
    }
    OutValue = Matcher.GetCaptureGroup(1);
    OutValue.TrimStartAndEndInline();
    return !OutValue.IsEmpty();
}

bool IsManagedIdentifier(const FString& Value)
{
    const FRegexPattern Pattern(TEXT("^[A-Za-z_][A-Za-z0-9_]*$"));
    FRegexMatcher Matcher(Pattern, Value);
    return Matcher.FindNext();
}

bool IsManagedNamespace(const FString& Value)
{
    TArray<FString> Segments;
    Value.ParseIntoArray(Segments, TEXT("."), false);
    return !Segments.IsEmpty() && Segments.ContainsByPredicate(
        [](const FString& Segment) { return !IsManagedIdentifier(Segment); }) == false;
}

bool ReadProjectSettings(FProjectClassSettings& OutSettings, FString& OutError)
{
    FProjectClassSettings Settings;
    Settings.ProjectRoot = FPaths::ConvertRelativePathToFull(FPaths::ProjectDir());
    FPaths::NormalizeDirectoryName(Settings.ProjectRoot);
    const FString ConfigPath = FPaths::Combine(
        Settings.ProjectRoot,
        TEXT("Config/UnifiedScript.onboarding.json"));
    const FString ConfigDirectory = FPaths::GetPath(ConfigPath);

    TSharedPtr<FJsonObject> Config;
    if (!ReadJsonFile(ConfigPath, Config, OutError))
    {
        return false;
    }
    FString Schema;
    const TSharedPtr<FJsonObject>* Managed = nullptr;
    const TSharedPtr<FJsonObject>* Deployment = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* Projects = nullptr;
    if (!Config->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.unreal-project-onboarding/v0.1") ||
        !Config->TryGetObjectField(TEXT("managed"), Managed) || Managed == nullptr ||
        !(*Managed)->TryGetArrayField(TEXT("projects"), Projects) || Projects == nullptr ||
        Projects->IsEmpty() ||
        !Config->TryGetObjectField(TEXT("deployment"), Deployment) || Deployment == nullptr)
    {
        OutError = TEXT("UnifiedScript onboarding configuration is incomplete.");
        return false;
    }

    FString ManagedProjectValue;
    FString DeploymentValue;
    if (!(*Projects)[0]->TryGetString(ManagedProjectValue) ||
        !(*Deployment)->TryGetStringField(TEXT("manifest"), DeploymentValue))
    {
        OutError = TEXT("UnifiedScript onboarding does not identify its managed project and deployment manifest.");
        return false;
    }
    Settings.ManagedProject = ResolveRelativeTo(ConfigDirectory, ManagedProjectValue);
    Settings.SourceDirectory = FPaths::Combine(
        FPaths::GetPath(Settings.ManagedProject),
        TEXT("Classes"));
    Settings.DeploymentPath = ResolveRelativeTo(ConfigDirectory, DeploymentValue);

    FString PluginPath;
    if (!Config->TryGetStringField(TEXT("pluginPath"), PluginPath))
    {
        OutError = TEXT("UnifiedScript onboarding does not identify the project plugin path.");
        return false;
    }
    Settings.PluginRoot = ResolveRelativeTo(ConfigDirectory, PluginPath);

    FString ProjectXml;
    if (!FFileHelper::LoadFileToString(ProjectXml, *Settings.ManagedProject) ||
        !ReadXmlElement(ProjectXml, TEXT("AssemblyName"), Settings.AssemblyName))
    {
        Settings.AssemblyName = FPaths::GetBaseFilename(Settings.ManagedProject);
    }
    if (!ReadXmlElement(ProjectXml, TEXT("RootNamespace"), Settings.ManagedNamespace))
    {
        Settings.ManagedNamespace = Settings.AssemblyName;
    }
    if (!IsManagedNamespace(Settings.ManagedNamespace))
    {
        OutError = FString::Printf(
            TEXT("Managed RootNamespace '%s' cannot be used for a Script Class."),
            *Settings.ManagedNamespace);
        return false;
    }

    if (!ReadJsonFile(Settings.DeploymentPath, Settings.DeploymentRoot, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Cannot snapshot UnifiedScript deployment manifest '%s'."),
                *Settings.DeploymentPath);
        }
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
    if (!Settings.DeploymentRoot->TryGetArrayField(TEXT("scripts"), Scripts) ||
        Scripts == nullptr || Scripts->IsEmpty())
    {
        OutError = TEXT("UnifiedScript deployment manifest requires an existing baseline script.");
        return false;
    }
    Settings.BaselineDeployment = (*Scripts)[0]->AsObject();
    if (!Settings.BaselineDeployment.IsValid())
    {
        OutError = TEXT("UnifiedScript deployment baseline script is invalid.");
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Chunks = nullptr;
    if ((*Managed)->TryGetArrayField(TEXT("chunks"), Chunks) && Chunks != nullptr)
    {
        for (const TSharedPtr<FJsonValue>& ChunkValue : *Chunks)
        {
            const TSharedPtr<FJsonObject> Chunk = ChunkValue->AsObject();
            FString ProjectValue;
            FString StageValue;
            if (Chunk.IsValid() &&
                Chunk->TryGetStringField(TEXT("project"), ProjectValue) &&
                Chunk->TryGetStringField(TEXT("stagePath"), StageValue) &&
                ResolveRelativeTo(ConfigDirectory, ProjectValue) == Settings.ManagedProject)
            {
                Settings.CompiledChunkPath = ResolveRelativeTo(ConfigDirectory, StageValue);
                break;
            }
        }
    }

    OutSettings = MoveTemp(Settings);
    OutError.Reset();
    return true;
}

FString ToConfiguredPath(const FString& AbsolutePath, const FProjectClassSettings& Settings)
{
    FString Relative = AbsolutePath;
    if (Relative.StartsWith(Settings.PluginRoot, ESearchCase::IgnoreCase))
    {
        Relative.RightChopInline(Settings.PluginRoot.Len());
        Relative.RemoveFromStart(TEXT("/"));
        return FString::Printf(TEXT("{PluginDir}/%s"), *Relative);
    }
    const FString ProjectDirectoryBase = Settings.ProjectRoot + TEXT("/");
    FPaths::MakePathRelativeTo(Relative, *ProjectDirectoryBase);
    FPaths::NormalizeFilename(Relative);
    return FString::Printf(TEXT("{ProjectDir}/%s"), *Relative);
}

FString RenderManagedSource(
    const FString& ManagedNamespace,
    const FString& TypeName,
    const FString& DeclarationAssetPath,
    const FGuid& ClassId,
    const FString& GeneratedClassPath,
    EUnifiedScriptClassKind Kind)
{
    const FString SemanticId = FString::Printf(
        TEXT("us_%s"),
        *ClassId.ToString(EGuidFormats::Digits).ToLower());
    const TCHAR* KindName = TEXT("");
    const TCHAR* BaseType = TEXT("");
    switch (Kind)
    {
    case EUnifiedScriptClassKind::Actor:
        KindName = TEXT("Actor");
        BaseType = TEXT("UnrealActorScript");
        break;
    case EUnifiedScriptClassKind::ActorComponent:
        KindName = TEXT("ActorComponent");
        BaseType = TEXT("UnrealActorComponentScript");
        break;
    case EUnifiedScriptClassKind::Object:
        KindName = TEXT("Object");
        BaseType = TEXT("UnrealObjectScript");
        break;
    case EUnifiedScriptClassKind::BlueprintFunctionLibrary:
        return FString::Printf(
            TEXT("using UnifiedScript;\n")
            TEXT("using UnifiedScript.Unreal;\n\n")
            TEXT("namespace %s;\n\n")
            TEXT("[ScriptAsset(\"%s\", ClassId = \"%s\")]\n")
            TEXT("[UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary, GeneratedClassPath = \"%s\")]\n")
            TEXT("public static class %s\n")
            TEXT("{\n")
            TEXT("    [Visual]\n")
            TEXT("    public static int AddOne(int value)\n")
            TEXT("    {\n")
            TEXT("        return value + 1;\n")
            TEXT("    }\n")
            TEXT("}\n"),
            *ManagedNamespace,
            *DeclarationAssetPath,
            *SemanticId,
            *GeneratedClassPath,
            *TypeName);
    default:
        return FString();
    }

    const TCHAR* InitializeHook = Kind == EUnifiedScriptClassKind::Object
        ? TEXT("OnInitialized")
        : TEXT("OnBeginPlay");
    const TCHAR* UpdateHook = Kind == EUnifiedScriptClassKind::Object
        ? TEXT("OnUpdate")
        : TEXT("OnTick");
    const TCHAR* BlueprintEventBlock = Kind == EUnifiedScriptClassKind::Object
        ? TEXT("")
        : TEXT("    [UnrealBlueprintEvent]\n")
          TEXT("    public void OnManagedActivated(int amount)\n")
          TEXT("    {\n")
          TEXT("        StartValue += amount;\n")
          TEXT("    }\n\n");
    const TCHAR* EndPlayBlock = Kind == EUnifiedScriptClassKind::Object
        ? TEXT("")
        : TEXT("\n")
          TEXT("    protected override void OnEndPlay()\n")
          TEXT("    {\n")
          TEXT("    }\n");
    return FString::Printf(
        TEXT("using UnifiedScript;\n")
        TEXT("using UnifiedScript.Unreal;\n\n")
        TEXT("namespace %s;\n\n")
        TEXT("[ScriptAsset(\"%s\", ClassId = \"%s\")]\n")
        TEXT("[UnrealScriptClass(UnrealScriptClassKind.%s, GeneratedClassPath = \"%s\")]\n")
        TEXT("public sealed class %s : %s\n")
        TEXT("{\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript\")]\n")
        TEXT("    public int StartValue { get; set; }\n\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript|State\")]\n")
        TEXT("    public UnrealName StateTag { get; set; }\n\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript|State\")]\n")
        TEXT("    public UnrealText DisplayText { get; set; }\n\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript|References\")]\n")
        TEXT("    public UnrealObjectHandle LinkedObject { get; set; }\n\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript|References\")]\n")
        TEXT("    public UnrealClassHandle LinkedClass { get; set; }\n\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript|State\")]\n")
        TEXT("    public UnrealVector SpawnOffset { get; set; }\n\n")
        TEXT("    public %s()\n")
        TEXT("    {\n")
        TEXT("        StartValue = 1;\n")
        TEXT("        StateTag = new UnrealName(\"Demo\");\n")
        TEXT("        DisplayText = new UnrealText(\"Ready\");\n")
        TEXT("        LinkedObject = default;\n")
        TEXT("        LinkedClass = default;\n")
        TEXT("        SpawnOffset = new UnrealVector(100.0, 0.0, 0.0);\n")
        TEXT("    }\n\n")
        TEXT("    [Visual]\n")
        TEXT("    public int AddOne(int value)\n")
        TEXT("    {\n")
        TEXT("        return value + 1;\n")
        TEXT("    }\n\n")
        TEXT("%s")
        TEXT("    protected override void %s()\n")
        TEXT("    {\n")
        TEXT("        StartValue = AddOne(StartValue);\n")
        TEXT("    }\n\n")
        TEXT("    protected override void %s(float deltaSeconds)\n")
        TEXT("    {\n")
        TEXT("    }\n")
        TEXT("%s")
        TEXT("}\n"),
        *ManagedNamespace,
        *DeclarationAssetPath,
        *SemanticId,
        KindName,
        *GeneratedClassPath,
        *TypeName,
        BaseType,
        *TypeName,
        BlueprintEventBlock,
        InitializeHook,
        UpdateHook,
        EndPlayBlock);
}

bool WriteTextAtomically(const FString& Path, const FString& Text, FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString Temporary = Path + TEXT(".unifiedscript.tmp");
    if (!FFileHelper::SaveStringToFile(
            Text,
            *Temporary,
            FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
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

bool WriteBytesAtomically(
    const FString& Path,
    const TArray<uint8>& Bytes,
    FString& OutError)
{
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const FString Temporary = Path + TEXT(".unifiedscript.rollback.tmp");
    if (!FFileHelper::SaveArrayToFile(Bytes, *Temporary))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot restore '%s'."), *Temporary);
        return false;
    }
    if (!IFileManager::Get().Move(*Path, *Temporary, true, true, false, true))
    {
        IFileManager::Get().Delete(*Temporary, false, true);
        OutError = FString::Printf(TEXT("Cannot publish restored file '%s'."), *Path);
        return false;
    }
    return true;
}

bool RestoreFileSnapshot(
    const FString& Path,
    bool bExisted,
    const TArray<uint8>& OriginalBytes,
    FString& OutError)
{
    if (bExisted)
    {
        return WriteBytesAtomically(Path, OriginalBytes, OutError);
    }
    if (!IFileManager::Get().FileExists(*Path) ||
        IFileManager::Get().Delete(*Path, false, true))
    {
        return true;
    }
    OutError = FString::Printf(TEXT("Cannot remove newly published file '%s'."), *Path);
    return false;
}

bool CaptureCreationRecoverySnapshots(
    const FProjectClassSettings& Settings,
    const FString& SourcePath,
    const FString& FirstPlayRequestPath,
    const FString& BlueprintPackagePath,
    const FString& ScriptAssetPackagePath,
    TArray<UE::UnifiedScript::ClassOperationJournal::FSnapshot>& OutSnapshots,
    FString& OutError)
{
    using namespace UE::UnifiedScript::ClassOperationJournal;
    TArray<FString> Paths = {
        SourcePath,
        Settings.DeploymentPath,
        FirstPlayRequestPath,
    };
    const TCHAR* PackageExtensions[] = {
        TEXT(".uasset"),
        TEXT(".uexp"),
        TEXT(".ubulk"),
        TEXT(".uptnl"),
    };
    for (const FString& PackagePath : {
             BlueprintPackagePath,
             ScriptAssetPackagePath,
         })
    {
        const FString BaseFilename =
            FPackageName::LongPackageNameToFilename(PackagePath);
        for (const TCHAR* Extension : PackageExtensions)
        {
            Paths.Add(BaseFilename + Extension);
        }
    }

    OutSnapshots.Reset();
    OutSnapshots.Reserve(Paths.Num());
    for (const FString& Path : Paths)
    {
        FSnapshot Snapshot;
        if (!CaptureSnapshot(Path, Snapshot, OutError))
        {
            OutSnapshots.Reset();
            return false;
        }
        OutSnapshots.Add(MoveTemp(Snapshot));
    }
    return true;
}

bool RestoreCreationRecoverySnapshots(
    const TArray<UE::UnifiedScript::ClassOperationJournal::FSnapshot>& Snapshots,
    bool bSourceDirectoryExisted,
    const FString& SourceDirectory,
    FString& OutError)
{
    TArray<FString> Failures;
    for (const UE::UnifiedScript::ClassOperationJournal::FSnapshot& Snapshot :
         Snapshots)
    {
        FString RestoreError;
        if (!RestoreFileSnapshot(
                Snapshot.Path,
                Snapshot.bExisted,
                Snapshot.Bytes,
                RestoreError))
        {
            Failures.Add(RestoreError);
        }
    }
    if (!bSourceDirectoryExisted)
    {
        // Never recurse: preserve anything concurrently added by the user.
        IFileManager::Get().DeleteDirectory(*SourceDirectory, false, false);
    }
    OutError = FString::Join(Failures, TEXT("; "));
    return Failures.IsEmpty();
}

bool SaveCreatedClassPackages(
    UBlueprint* Blueprint,
    UUnifiedScriptAsset* ScriptAsset,
    FString& OutError)
{
    if (Blueprint == nullptr || ScriptAsset == nullptr)
    {
        OutError = TEXT("Cannot save an incomplete Script Class asset pair.");
        return false;
    }
    FSavePackageArgs SaveArguments;
    SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
    const FString BlueprintFilename =
        FPackageName::LongPackageNameToFilename(
            Blueprint->GetOutermost()->GetName(),
            FPackageName::GetAssetPackageExtension());
    const FString ScriptAssetFilename =
        FPackageName::LongPackageNameToFilename(
            ScriptAsset->GetOutermost()->GetName(),
            FPackageName::GetAssetPackageExtension());
    if (!UPackage::SavePackage(
            Blueprint->GetOutermost(),
            Blueprint,
            *BlueprintFilename,
            SaveArguments))
    {
        OutError = FString::Printf(
            TEXT("Cannot save generated Blueprint package '%s'."),
            *BlueprintFilename);
        return false;
    }
    if (!UPackage::SavePackage(
            ScriptAsset->GetOutermost(),
            ScriptAsset,
            *ScriptAssetFilename,
            SaveArguments))
    {
        OutError = FString::Printf(
            TEXT("Cannot save Script Asset package '%s'."),
            *ScriptAssetFilename);
        return false;
    }
    return true;
}

bool AppendDeployment(
    FProjectClassSettings& Settings,
    const FString& ScriptAssetPackagePath,
    const FString& SourcePath,
    const FString& TypeName,
    const FString& DeclarationAssetPath,
    EUnifiedScriptClassKind Kind,
    FString& OutJson,
    FString& OutError)
{
    FScriptClassDescriptor Descriptor;
    if (!TryDescribeScriptClassKind(Kind, Descriptor))
    {
        OutError = TEXT("UnifiedScript Class kind is not supported.");
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* ExistingScripts = nullptr;
    if (!Settings.DeploymentRoot->TryGetArrayField(
            TEXT("scripts"),
            ExistingScripts) || ExistingScripts == nullptr)
    {
        OutError = TEXT("UnifiedScript deployment scripts are unavailable.");
        return false;
    }

    TArray<TSharedPtr<FJsonValue>> Scripts = *ExistingScripts;
    for (const TSharedPtr<FJsonValue>& Value : Scripts)
    {
        const TSharedPtr<FJsonObject> Entry = Value->AsObject();
        FString ExistingAssetPath;
        FString ExistingTypeName;
        if (Entry.IsValid() &&
            ((Entry->TryGetStringField(TEXT("assetPath"), ExistingAssetPath) &&
                ExistingAssetPath == ScriptAssetPackagePath) ||
             (Entry->TryGetStringField(TEXT("typeName"), ExistingTypeName) &&
                ExistingTypeName == TypeName)))
        {
            OutError = FString::Printf(
                TEXT("Deployment already contains Script Class '%s'."),
                *TypeName);
            return false;
        }
    }

    const TSharedRef<FJsonObject> Entry = MakeShared<FJsonObject>();
    const TCHAR* CopiedFields[] = {
        TEXT("assemblyPath"),
        TEXT("declarationPath"),
        TEXT("authoringSurfacePath"),
        TEXT("apiNodeCatalogPath"),
        TEXT("authoringServerUrl"),
    };
    Entry->SetStringField(TEXT("assetPath"), ScriptAssetPackagePath);
    for (const TCHAR* Field : CopiedFields)
    {
        FString Value;
        if (Settings.BaselineDeployment->TryGetStringField(Field, Value))
        {
            Entry->SetStringField(Field, Value);
        }
    }
    Entry->SetStringField(TEXT("typeName"), TypeName);
    Entry->SetStringField(
        TEXT("requiredTargetClass"),
        Descriptor.RequiredTargetClassPath);
    Entry->SetStringField(TEXT("sourcePath"), ToConfiguredPath(SourcePath, Settings));
    Entry->SetStringField(TEXT("declarationAssetPath"), DeclarationAssetPath);
    Scripts.Add(MakeShared<FJsonValueObject>(Entry));
    Settings.DeploymentRoot->SetArrayField(TEXT("scripts"), Scripts);

    const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Settings.DeploymentRoot.ToSharedRef(), Writer))
    {
        OutError = TEXT("Cannot serialize the updated UnifiedScript deployment manifest.");
        return false;
    }
    OutJson.AppendChar(TEXT('\n'));
    return true;
}

void ConfigureScriptAsset(
    UUnifiedScriptAsset& Asset,
    const FProjectClassSettings& Settings,
    const FGuid& ClassId,
    EUnifiedScriptClassKind Kind,
    const FString& GeneratedClassPath,
    const FString& SourcePath,
    const FString& TypeName,
    const FString& DeclarationAssetPath)
{
    FScriptClassDescriptor Descriptor;
    check(TryDescribeScriptClassKind(Kind, Descriptor));
    Asset.ScriptClassId = ClassId;
    Asset.ScriptClassKind = Kind;
    Asset.GeneratedClassPath = FSoftClassPath(GeneratedClassPath);
    Asset.ScriptTypeName = TypeName;
    Asset.SourcePath = ToConfiguredPath(SourcePath, Settings);
    Asset.DeclarationAssetPath = DeclarationAssetPath;
    if (!Settings.CompiledChunkPath.IsEmpty())
    {
        Asset.CompiledChunkPath = ToConfiguredPath(Settings.CompiledChunkPath, Settings);
    }

    FString Value;
    if (Settings.BaselineDeployment->TryGetStringField(TEXT("assemblyPath"), Value))
    {
        Asset.ScriptAssemblyPath = Value;
    }
    if (Settings.BaselineDeployment->TryGetStringField(TEXT("declarationPath"), Value))
    {
        Asset.DeclarationPath = Value;
    }
    if (Settings.BaselineDeployment->TryGetStringField(TEXT("authoringSurfacePath"), Value))
    {
        Asset.AuthoringSurfacePath = Value;
    }
    if (Settings.BaselineDeployment->TryGetStringField(TEXT("apiNodeCatalogPath"), Value))
    {
        Asset.ApiNodeCatalogPath = Value;
    }
    if (Settings.BaselineDeployment->TryGetStringField(TEXT("authoringServerUrl"), Value))
    {
        Asset.AuthoringServerUrl = Value;
    }
    Asset.RequiredTargetClass = TSoftClassPtr<UObject>(Descriptor.RequiredTargetClass);
}
}

UUnifiedScriptClassFactory::UUnifiedScriptClassFactory(
    const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    bCreateNew = true;
    bEditAfterNew = true;
    bSkipClassPicker = true;
    SupportedClass = UUnifiedScriptBlueprint::StaticClass();
}

UObject* UUnifiedScriptClassFactory::FactoryCreateNew(
    UClass* Class,
    UObject* InParent,
    FName Name,
    EObjectFlags Flags,
    UObject* Context,
    FFeedbackContext* Warn,
    FName CallingContext)
{
    using namespace UE::UnifiedScript::ClassFactory::Private;
#if WITH_DEV_AUTOMATION_TESTS
    LastRecoveryReceiptPathForTesting.Reset();
#endif
    FString Error;
    FProjectClassSettings Settings;
    FScriptClassDescriptor Descriptor;
    const FString TypeName = Name.ToString();
    if (ScriptClassKind == EUnifiedScriptClassKind::None || InParent == nullptr ||
        !IsManagedIdentifier(TypeName) ||
        !TryDescribeScriptClassKind(ScriptClassKind, Descriptor) ||
        !ReadProjectSettings(Settings, Error))
    {
        if (Error.IsEmpty())
        {
            Error = FString::Printf(TEXT("'%s' is not a valid managed class name."), *TypeName);
        }
        Warn->Logf(ELogVerbosity::Error, TEXT("UnifiedScript Class creation failed: %s"), *Error);
        return nullptr;
    }

    const FString SourcePath = FPaths::Combine(Settings.SourceDirectory, TypeName + TEXT(".cs"));
    if (IFileManager::Get().FileExists(*SourcePath))
    {
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class source already exists: %s"),
            *SourcePath);
        return nullptr;
    }

    const FString BlueprintPackagePath = InParent->GetPathName();
    const FString BlueprintClassPath = FString::Printf(
        TEXT("%s.%s_C"),
        *BlueprintPackagePath,
        *TypeName);
    const FString PackageDirectory = FPackageName::GetLongPackagePath(BlueprintPackagePath);
    const FString ScriptAssetName = FString::Printf(TEXT("SA_%s"), *TypeName);
    const FString ScriptAssetPackagePath = FString::Printf(
        TEXT("%s/%s"),
        *PackageDirectory,
        *ScriptAssetName);
    if (FindPackage(nullptr, *ScriptAssetPackagePath) != nullptr ||
        FPackageName::DoesPackageExist(ScriptAssetPackagePath))
    {
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class sidecar already exists: %s"),
            *ScriptAssetPackagePath);
        return nullptr;
    }

    const FString ProjectName = FApp::GetProjectName();
    const FString DeclarationAssetPath = FString::Printf(
        TEXT("%s/%s"),
        *ProjectName,
        *TypeName);
    const FString ManagedTypeName = FString::Printf(
        TEXT("%s.%s"),
        *Settings.ManagedNamespace,
        *TypeName);
    const FGuid ClassId = FGuid::NewGuid();
    const FString BlueprintAssetPath = FString::Printf(
        TEXT("%s.%s"),
        *BlueprintPackagePath,
        *TypeName);
    const FString ScriptAssetObjectPath = FString::Printf(
        TEXT("%s.%s"),
        *ScriptAssetPackagePath,
        *ScriptAssetName);
    const FString FirstPlayRequestPath =
        FUnifiedScriptProjectSetup::GetFirstPlayRequestPath(Settings.ProjectRoot);
    const bool bSourceDirectoryExisted =
        IFileManager::Get().DirectoryExists(*Settings.SourceDirectory);
    FString DeploymentJson;
    if (!AppendDeployment(
            Settings,
            ScriptAssetPackagePath,
            SourcePath,
            ManagedTypeName,
            DeclarationAssetPath,
            ScriptClassKind,
            DeploymentJson,
            Error))
    {
        Warn->Logf(ELogVerbosity::Error, TEXT("UnifiedScript Class creation failed: %s"), *Error);
        return nullptr;
    }

    const FString Source = RenderManagedSource(
        Settings.ManagedNamespace,
        TypeName,
        DeclarationAssetPath,
        ClassId,
        BlueprintClassPath,
        ScriptClassKind);
    TArray<UE::UnifiedScript::ClassOperationJournal::FSnapshot>
        RecoverySnapshots;
    UE::UnifiedScript::ClassOperationJournal::FReceipt RecoveryReceipt;
    const FString TransactionId =
        FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    if (!CaptureCreationRecoverySnapshots(
            Settings,
            SourcePath,
            FirstPlayRequestPath,
            BlueprintPackagePath,
            ScriptAssetPackagePath,
            RecoverySnapshots,
            Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Begin(
            TEXT("create"),
            TransactionId,
            RecoverySnapshots,
            RecoveryReceipt,
            Error))
    {
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class creation failed: %s"),
            *Error);
        return nullptr;
    }
#if WITH_DEV_AUTOMATION_TESTS
    LastRecoveryReceiptPathForTesting = RecoveryReceipt.ReceiptPath;
#endif
    const auto RollbackCreation = [&]()
    {
        FString RollbackError;
        const bool bRestored = RestoreCreationRecoverySnapshots(
            RecoverySnapshots,
            bSourceDirectoryExisted,
            Settings.SourceDirectory,
            RollbackError);
        FString CloseError;
        if (bRestored &&
            !UE::UnifiedScript::ClassOperationJournal::Close(
                RecoveryReceipt,
                CloseError))
        {
            RollbackError = CloseError;
        }
        if (!RollbackError.IsEmpty())
        {
            Error += FString::Printf(
                TEXT(" Rollback also failed: %s"),
                *RollbackError);
        }
    };
    if (!WriteTextAtomically(SourcePath, Source, Error) ||
        !WriteTextAtomically(Settings.DeploymentPath, DeploymentJson, Error))
    {
        RollbackCreation();
        Warn->Logf(ELogVerbosity::Error, TEXT("UnifiedScript Class creation failed: %s"), *Error);
        return nullptr;
    }

    FString PublishedFirstPlayRequestPath;
    if (Descriptor.bSupportsFirstPlay)
    {
        FString FirstPlayError;
        const FString RequestId = FString::Printf(
            TEXT("us_%s"),
            *ClassId.ToString(EGuidFormats::Digits).ToLower());
        if (!FUnifiedScriptProjectSetup::WriteFirstPlayRequest(
                Settings.ProjectRoot,
                RequestId,
                Descriptor.KindToken,
                BlueprintAssetPath,
                ScriptAssetObjectPath,
                SourcePath,
                ManagedTypeName,
                PublishedFirstPlayRequestPath,
                FirstPlayError))
        {
            Error = MoveTemp(FirstPlayError);
            RollbackCreation();
            Warn->Logf(
                ELogVerbosity::Error,
                TEXT("UnifiedScript Class creation failed: %s"),
                *Error);
            return nullptr;
        }
    }

    if (!UE::UnifiedScript::ClassOperationJournal::Advance(
            RecoveryReceipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::AssetsMutated,
            Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Advance(
            RecoveryReceipt,
            UE::UnifiedScript::ClassOperationJournal::EPhase::ExternalPublished,
            Error))
    {
        RollbackCreation();
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class creation failed: %s"),
            *Error);
        return nullptr;
    }
#if WITH_DEV_AUTOMATION_TESTS
    if (FailurePointForTesting ==
        EUnifiedScriptClassCreationFailurePoint::AfterExternalPublication)
    {
        Error = TEXT("Injected Script Class creation failure after external publication.");
        RollbackCreation();
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class creation failed: %s"),
            *Error);
        return nullptr;
    }
#endif

    ParentClass = Descriptor.ParentClass;
    BlueprintType = Descriptor.BlueprintType;
    UBlueprint* Blueprint = FKismetEditorUtilities::CreateBlueprint(
        ParentClass,
        InParent,
        Name,
        BlueprintType,
        UUnifiedScriptBlueprint::StaticClass(),
        UUnifiedScriptBlueprintGeneratedClass::StaticClass(),
        CallingContext);
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        Error = TEXT("Cannot create the generated Blueprint proxy.");
        RollbackCreation();
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class creation failed: %s"),
            *Error);
        return nullptr;
    }

    UPackage* ScriptPackage = CreatePackage(*ScriptAssetPackagePath);
    UUnifiedScriptAsset* ScriptAsset = NewObject<UUnifiedScriptAsset>(
        ScriptPackage,
        *ScriptAssetName,
        RF_Public | RF_Standalone | RF_Transactional);
    ConfigureScriptAsset(
        *ScriptAsset,
        Settings,
        ClassId,
        ScriptClassKind,
        BlueprintClassPath,
        SourcePath,
        ManagedTypeName,
        DeclarationAssetPath);
    FAssetRegistryModule::AssetCreated(ScriptAsset);
    ScriptAsset->MarkPackageDirty();

    UUnifiedScriptBlueprint* UnifiedBlueprint = CastChecked<UUnifiedScriptBlueprint>(Blueprint);
    UnifiedBlueprint->ScriptAsset = ScriptAsset;

    if (ScriptClassKind == EUnifiedScriptClassKind::Actor)
    {
        AUnifiedScriptActor* Defaults = CastChecked<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject());
        Defaults->ScriptComponent->ScriptAsset = ScriptAsset;
    }
    else if (ScriptClassKind == EUnifiedScriptClassKind::ActorComponent)
    {
        UUnifiedScriptComponent* Defaults = CastChecked<UUnifiedScriptComponent>(Blueprint->GeneratedClass->GetDefaultObject());
        Defaults->ScriptAsset = ScriptAsset;
    }
    FBlueprintEditorUtils::MarkBlueprintAsModified(Blueprint);
    Blueprint->MarkPackageDirty();

    if (!SaveCreatedClassPackages(Blueprint, ScriptAsset, Error) ||
        !UE::UnifiedScript::ClassOperationJournal::Close(
            RecoveryReceipt,
            Error))
    {
        RollbackCreation();
        Warn->Logf(
            ELogVerbosity::Error,
            TEXT("UnifiedScript Class creation failed: %s"),
            *Error);
        return nullptr;
    }

    if (Descriptor.bSupportsFirstPlay)
    {
        Warn->Logf(
            ELogVerbosity::Display,
            TEXT("UnifiedScript Class is ready to build. Open Project Setup and choose Build, Restart & Continue. Request: %s"),
            *PublishedFirstPlayRequestPath);
    }
    else
    {
        Warn->Logf(
            ELogVerbosity::Display,
            TEXT("UnifiedScript Class proxy '%s' is ready for the shared Build workflow."),
            *BlueprintAssetPath);
    }
    return Blueprint;
}

UUnifiedScriptObjectClassFactory::UUnifiedScriptObjectClassFactory(
    const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    ScriptClassKind = EUnifiedScriptClassKind::Object;
    ParentClass = UUnifiedScriptObject::StaticClass();
}

FText UUnifiedScriptObjectClassFactory::GetDisplayName() const
{
    return LOCTEXT("ObjectClassFactory", "UnifiedScript Object Class");
}

UUnifiedScriptActorClassFactory::UUnifiedScriptActorClassFactory(
    const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    ScriptClassKind = EUnifiedScriptClassKind::Actor;
    ParentClass = AUnifiedScriptActor::StaticClass();
}

FText UUnifiedScriptActorClassFactory::GetDisplayName() const
{
    return LOCTEXT("ActorClassFactory", "UnifiedScript Actor Class");
}

UUnifiedScriptActorComponentClassFactory::UUnifiedScriptActorComponentClassFactory(
    const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    ScriptClassKind = EUnifiedScriptClassKind::ActorComponent;
    ParentClass = UUnifiedScriptComponent::StaticClass();
}

FText UUnifiedScriptActorComponentClassFactory::GetDisplayName() const
{
    return LOCTEXT("ActorComponentClassFactory", "UnifiedScript Actor Component Class");
}

UUnifiedScriptBlueprintFunctionLibraryClassFactory::
    UUnifiedScriptBlueprintFunctionLibraryClassFactory(
        const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    ScriptClassKind = EUnifiedScriptClassKind::BlueprintFunctionLibrary;
    ParentClass = UUnifiedScriptBlueprintFunctionLibrary::StaticClass();
    BlueprintType = BPTYPE_FunctionLibrary;
}

FText UUnifiedScriptBlueprintFunctionLibraryClassFactory::GetDisplayName() const
{
    return LOCTEXT(
        "BlueprintFunctionLibraryClassFactory",
        "UnifiedScript Blueprint Function Library");
}

#undef LOCTEXT_NAMESPACE
