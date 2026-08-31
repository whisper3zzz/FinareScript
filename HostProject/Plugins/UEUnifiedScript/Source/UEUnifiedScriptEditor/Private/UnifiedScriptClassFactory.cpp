#include "UnifiedScriptClassFactory.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Dom/JsonObject.h"
#include "Framework/Docking/TabManager.h"
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
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptProjectSetup.h"
#include "UObject/Package.h"

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
    const FGuid& ClassId)
{
    const FString SemanticId = FString::Printf(
        TEXT("us_%s"),
        *ClassId.ToString(EGuidFormats::Digits).ToLower());
    return FString::Printf(
        TEXT("using UnifiedScript;\n")
        TEXT("using UnifiedScript.Unreal;\n\n")
        TEXT("namespace %s;\n\n")
        TEXT("[ScriptAsset(\"%s\", ClassId = \"%s\")]\n")
        TEXT("public sealed class %s : UnrealActorScript\n")
        TEXT("{\n")
        TEXT("    [Expose(InstanceEditable = true, Category = \"UnifiedScript\")]\n")
        TEXT("    public int StartValue { get; set; } = 1;\n\n")
        TEXT("    [Visual]\n")
        TEXT("    public int AddOne(int value)\n")
        TEXT("    {\n")
        TEXT("        return value + 1;\n")
        TEXT("    }\n\n")
        TEXT("    protected override void OnBeginPlay()\n")
        TEXT("    {\n")
        TEXT("        StartValue = AddOne(StartValue);\n")
        TEXT("    }\n\n")
        TEXT("    protected override void OnTick(float deltaSeconds)\n")
        TEXT("    {\n")
        TEXT("    }\n")
        TEXT("}\n"),
        *ManagedNamespace,
        *DeclarationAssetPath,
        *SemanticId,
        *TypeName);
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
        Kind == EUnifiedScriptClassKind::Actor
            ? TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptActor")
            : TEXT("/Script/Engine.Actor"));
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
    Asset.RequiredTargetClass = Kind == EUnifiedScriptClassKind::Actor
        ? TSoftClassPtr<UObject>(AUnifiedScriptActor::StaticClass())
        : TSoftClassPtr<UObject>(AActor::StaticClass());
}
}

UUnifiedScriptClassFactory::UUnifiedScriptClassFactory(
    const FObjectInitializer& ObjectInitializer)
    : Super(ObjectInitializer)
{
    bCreateNew = true;
    bEditAfterNew = true;
    bSkipClassPicker = true;
    SupportedClass = UBlueprint::StaticClass();
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
    FString Error;
    FProjectClassSettings Settings;
    const FString TypeName = Name.ToString();
    if (ScriptClassKind == EUnifiedScriptClassKind::None ||
        !IsManagedIdentifier(TypeName) ||
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

    ParentClass = ScriptClassKind == EUnifiedScriptClassKind::Actor
        ? AUnifiedScriptActor::StaticClass()
        : UUnifiedScriptComponent::StaticClass();
    UBlueprint* Blueprint = Cast<UBlueprint>(Super::FactoryCreateNew(
        Class,
        InParent,
        Name,
        Flags,
        Context,
        Warn,
        CallingContext));
    if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
    {
        return nullptr;
    }

    const FString Source = RenderManagedSource(
        Settings.ManagedNamespace,
        TypeName,
        DeclarationAssetPath,
        ClassId);
    if (!WriteTextAtomically(SourcePath, Source, Error) ||
        !WriteTextAtomically(Settings.DeploymentPath, DeploymentJson, Error))
    {
        IFileManager::Get().Delete(*SourcePath, false, true);
        Warn->Logf(ELogVerbosity::Error, TEXT("UnifiedScript Class creation failed: %s"), *Error);
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

    if (ScriptClassKind == EUnifiedScriptClassKind::Actor)
    {
        AUnifiedScriptActor* Defaults = CastChecked<AUnifiedScriptActor>(Blueprint->GeneratedClass->GetDefaultObject());
        Defaults->ScriptComponent->ScriptAsset = ScriptAsset;
    }
    else
    {
        UUnifiedScriptComponent* Defaults = CastChecked<UUnifiedScriptComponent>(Blueprint->GeneratedClass->GetDefaultObject());
        Defaults->ScriptAsset = ScriptAsset;
    }
    FBlueprintEditorUtils::MarkBlueprintAsModified(Blueprint);
    Blueprint->MarkPackageDirty();

    FString FirstPlayRequestPath;
    FString FirstPlayError;
    const FString RequestId = FString::Printf(
        TEXT("us_%s"),
        *ClassId.ToString(EGuidFormats::Digits).ToLower());
    if (!FUnifiedScriptProjectSetup::WriteFirstPlayRequest(
            Settings.ProjectRoot,
            RequestId,
            ScriptClassKind == EUnifiedScriptClassKind::Actor
                ? TEXT("actor")
                : TEXT("actorComponent"),
            Blueprint->GetPathName(),
            ScriptAsset->GetPathName(),
            SourcePath,
            ManagedTypeName,
            FirstPlayRequestPath,
            FirstPlayError))
    {
        Warn->Logf(
            ELogVerbosity::Warning,
            TEXT("UnifiedScript Class was created, but its first-Play handoff could not be written: %s"),
            *FirstPlayError);
    }
    else
    {
        Warn->Logf(
            ELogVerbosity::Display,
            TEXT("UnifiedScript Class is ready to build. Open Project Setup and choose Build, Restart & Continue. Request: %s"),
            *FirstPlayRequestPath);
        if (!FApp::IsUnattended())
        {
            FGlobalTabmanager::Get()->TryInvokeTab(
                FName(TEXT("UnifiedScriptProjectSetup")));
        }
    }
    return Blueprint;
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

#undef LOCTEXT_NAMESPACE
