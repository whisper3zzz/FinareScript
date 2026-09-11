#if WITH_DEV_AUTOMATION_TESTS

#include "FinareClassAssetOperations.h"
#include "FinareClassFactory.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "AssetToolsModule.h"
#include "Dom/JsonObject.h"
#include "EdGraphSchema_K2.h"
#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "HAL/FileManager.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "PackageTools.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareProjectSetup.h"
#include "UObject/SavePackage.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::InheritedClassAssetOperations::Tests
{
	FString ResolveConfiguredPath(const FString& Value)
	{
		FString Result = Value;
		Result.ReplaceInline(
			TEXT("{ProjectDir}"),
			*FPaths::ConvertRelativePathToFull(FPaths::ProjectDir()),
			ESearchCase::IgnoreCase);
		Result = FPaths::ConvertRelativePathToFull(Result);
		FPaths::NormalizeFilename(Result);
		return Result;
	}

	bool ReadBytes(const FString& Path, TArray<uint8>& OutBytes)
	{
		OutBytes.Reset();
		return FFileHelper::LoadFileToArray(OutBytes, *Path);
	}

	bool ResolveProjectDeploymentPath(FString& OutDeploymentPath, FString& OutError)
	{
		const FString ConfigPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectDir(),
			TEXT("Config/Finare.onboarding.json")));
		FString Json;
		if (!FFileHelper::LoadFileToString(Json, *ConfigPath))
		{
			OutError = FString::Printf(
				TEXT("Cannot read packaged project onboarding configuration '%s'."),
				*ConfigPath);
			return false;
		}
		TSharedPtr<FJsonObject> Config;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
		const TSharedPtr<FJsonObject>* Deployment = nullptr;
		FString Manifest;
		if (!FJsonSerializer::Deserialize(Reader, Config) || !Config.IsValid() ||
			!Config->TryGetObjectField(TEXT("deployment"), Deployment) ||
			Deployment == nullptr ||
			!(*Deployment)->TryGetStringField(TEXT("manifest"), Manifest) ||
			Manifest.IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("Packaged project onboarding configuration '%s' has no deployment manifest."),
				*ConfigPath);
			return false;
		}
		if (FPaths::IsRelative(Manifest))
		{
			Manifest = FPaths::Combine(FPaths::GetPath(ConfigPath), Manifest);
		}
		OutDeploymentPath = FPaths::ConvertRelativePathToFull(Manifest);
		FPaths::NormalizeFilename(OutDeploymentPath);
		OutError.Reset();
		return true;
	}

	struct FScopedCleanup
	{
		FAutomationTestBase* Test = nullptr;
		FString DeploymentPath;
		TArray<uint8> DeploymentBytes;
		bool bDeploymentSnapshotReady = false;
		FString FirstPlayPath;
		TArray<uint8> FirstPlayBytes;
		bool bHadFirstPlay = false;
		bool bFirstPlaySnapshotReady = false;
		TArray<FString> SourcePaths;
		TArray<FString> PackageNames;
		FString PackageRootDirectory;

		~FScopedCleanup()
		{
			TArray<UPackage*> Packages;
			for (const FString& PackageName : PackageNames)
			{
				if (UPackage* Package = FindPackage(nullptr, *PackageName))
				{
					Package->SetDirtyFlag(false);
					Packages.AddUnique(Package);
				}
			}
			if (Packages.Num() > 0)
			{
				if (!UPackageTools::UnloadPackages(Packages))
				{
					const FString Message = TEXT(
						"Inherited class asset operations cleanup could not unload one or more owned packages");
					UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
					if (Test != nullptr)
					{
						Test->AddError(Message);
					}
				}
			}
			for (const FString& SourcePath : SourcePaths)
			{
				if (IFileManager::Get().FileExists(*SourcePath) &&
					!IFileManager::Get().Delete(*SourcePath, false, true) &&
					IFileManager::Get().FileExists(*SourcePath))
				{
					const FString Message = FString::Printf(
						TEXT("Inherited class asset operations cleanup could not delete owned source %s"), *SourcePath);
					UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
					if (Test != nullptr)
					{
						Test->AddError(Message);
					}
				}
			}
			for (const FString& PackageName : PackageNames)
			{
				const FString PackageFilename = FPackageName::LongPackageNameToFilename(
					PackageName,
					FPackageName::GetAssetPackageExtension());
				if (IFileManager::Get().FileExists(*PackageFilename) &&
					!IFileManager::Get().Delete(*PackageFilename, false, true) &&
					IFileManager::Get().FileExists(*PackageFilename))
				{
					const FString Message = FString::Printf(
						TEXT("Inherited class asset operations cleanup could not delete owned package %s"),
						*PackageFilename);
					UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
					if (Test != nullptr)
					{
						Test->AddError(Message);
					}
				}
			}
			if (!PackageRootDirectory.IsEmpty() && IFileManager::Get().DirectoryExists(*PackageRootDirectory) &&
				!IFileManager::Get().DeleteDirectory(*PackageRootDirectory, false, false) &&
				IFileManager::Get().DirectoryExists(*PackageRootDirectory))
			{
				const FString Message = FString::Printf(
					TEXT("Inherited class asset operations cleanup could not remove owned package directory %s"),
					*PackageRootDirectory);
				UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
				if (Test != nullptr)
				{
					Test->AddError(Message);
				}
			}
			if (bDeploymentSnapshotReady)
			{
				if (!FFileHelper::SaveArrayToFile(DeploymentBytes, *DeploymentPath))
				{
					const FString Message = FString::Printf(
						TEXT("Inherited class asset operations cleanup could not restore deployment manifest %s"),
						*DeploymentPath);
					UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
					if (Test != nullptr)
					{
						Test->AddError(Message);
					}
				}
			}
			if (bFirstPlaySnapshotReady)
			{
				if (bHadFirstPlay)
				{
					if (!FFileHelper::SaveArrayToFile(FirstPlayBytes, *FirstPlayPath))
					{
						const FString Message = FString::Printf(
							TEXT("Inherited class asset operations cleanup could not restore first-play request %s"),
							*FirstPlayPath);
						UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
						if (Test != nullptr)
						{
							Test->AddError(Message);
						}
					}
				}
				else
				{
					if (IFileManager::Get().FileExists(*FirstPlayPath) &&
						!IFileManager::Get().Delete(*FirstPlayPath, false, true) &&
						IFileManager::Get().FileExists(*FirstPlayPath))
					{
						const FString Message = FString::Printf(
							TEXT("Inherited class asset operations cleanup could not remove first-play request %s"),
							*FirstPlayPath);
						UE_LOG(LogTemp, Error, TEXT("%s"), *Message);
						if (Test != nullptr)
						{
							Test->AddError(Message);
						}
					}
				}
			}
		}
	};

	UFinareAsset* GetScriptAsset(const UBlueprint* Blueprint)
	{
		const AFinareActor* Defaults = Blueprint != nullptr && Blueprint->GeneratedClass != nullptr
			                               ? Cast<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())
			                               : nullptr;
		return Defaults != nullptr && Defaults->ScriptComponent != nullptr
			       ? Defaults->ScriptComponent->ScriptAsset.Get()
			       : nullptr;
	}

	UFinareComponent* GetComponent(const UBlueprint* Blueprint)
	{
		const AFinareActor* Defaults = Blueprint != nullptr && Blueprint->GeneratedClass != nullptr
			                               ? Cast<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())
			                               : nullptr;
		return Defaults != nullptr ? Defaults->ScriptComponent : nullptr;
	}

	bool GetVariableGuid(const UBlueprint* Blueprint, const FName VariableName, FGuid& OutGuid)
	{
		if (Blueprint == nullptr)
		{
			return false;
		}
		for (const FBPVariableDescription& Variable : Blueprint->NewVariables)
		{
			if (Variable.VarName == VariableName)
			{
				OutGuid = Variable.VarGuid;
				return OutGuid.IsValid();
			}
		}
		return false;
	}

	bool SetComponentBoolDefault(
		FAutomationTestBase& Test,
		UBlueprint& Blueprint,
		const FName PropertyName,
		const bool Value)
	{
		UFinareComponent* Component = GetComponent(&Blueprint);
		FBoolProperty* Property = Component != nullptr
			                          ? FindFProperty<FBoolProperty>(Component->GetClass(), PropertyName)
			                          : nullptr;
		if (!Test.TestNotNull(
			*FString::Printf(TEXT("Finds %s on %s component CDO"), *PropertyName.ToString(), *Blueprint.GetName()),
			Property) || Component == nullptr)
		{
			return false;
		}
		Component->Modify();
		Component->PreEditChange(Property);
		Property->SetPropertyValue_InContainer(Component, Value);
		FPropertyChangedEvent PropertyChanged(Property);
		Component->PostEditChangeProperty(PropertyChanged);
		FBlueprintEditorUtils::MarkBlueprintAsModified(&Blueprint);
		Blueprint.MarkPackageDirty();
		return Test.TestEqual(
			*FString::Printf(TEXT("Writes %s on %s component CDO"), *PropertyName.ToString(), *Blueprint.GetName()),
			Property->GetPropertyValue_InContainer(Component),
			Value);
	}

	bool AddStateVariables(FAutomationTestBase& Test, UBlueprint& Blueprint)
	{
		FEdGraphPinType Scalar;
		Scalar.PinCategory = UEdGraphSchema_K2::PC_Int;
		FEdGraphPinType Array = Scalar;
		Array.ContainerType = EPinContainerType::Array;
		FEdGraphPinType Set = Scalar;
		Set.ContainerType = EPinContainerType::Set;
		FEdGraphPinType Map;
		Map.PinCategory = UEdGraphSchema_K2::PC_Int;
		Map.PinValueType.TerminalCategory = UEdGraphSchema_K2::PC_Int;
		Map.ContainerType = EPinContainerType::Map;
		return
			Test.TestTrue(TEXT("Adds an ordinary scalar state variable"),
			              FBlueprintEditorUtils::AddMemberVariable(&Blueprint, TEXT("AssetOpsScalar"), Scalar,
			                                                       TEXT("7"))) &&
			Test.TestTrue(TEXT("Adds an ordinary array state variable"),
			              FBlueprintEditorUtils::AddMemberVariable(&Blueprint, TEXT("AssetOpsValues"), Array,
			                                                       TEXT("(2,4)"))) &&
			Test.TestTrue(TEXT("Adds an ordinary set state variable"),
			              FBlueprintEditorUtils::AddMemberVariable(&Blueprint, TEXT("AssetOpsEnabled"), Set,
			                                                       TEXT("(6,8)"))) &&
			Test.TestTrue(TEXT("Adds an ordinary map state variable"),
			              FBlueprintEditorUtils::AddMemberVariable(&Blueprint, TEXT("AssetOpsWeights"), Map, TEXT("")));
	}

	template <typename TProperty>
	bool ImportValue(UObject* Object, const TProperty* Property, const TCHAR* Text)
	{
		return Object != nullptr && Property != nullptr &&
			Property->ImportText_Direct(
				Text,
				Property->ContainerPtrToValuePtr<void>(Object),
				Object,
				PPF_None,
				nullptr) != nullptr;
	}

	template <typename TProperty>
	bool ImportValueChecked(
		FAutomationTestBase& Test,
		UObject* Object,
		const TProperty* Property,
		const TCHAR* Text,
		const TCHAR* Label)
	{
		return Test.TestTrue(Label, ImportValue(Object, Property, Text));
	}

	TArray<int32> ReadArray(UObject* Object, const FArrayProperty* Property)
	{
		TArray<int32> Result;
		if (Object == nullptr || Property == nullptr || CastField<FIntProperty>(Property->Inner) == nullptr)
		{
			return Result;
		}
		FScriptArrayHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
		const FIntProperty* Inner = CastField<FIntProperty>(Property->Inner);
		for (int32 Index = 0; Index < Helper.Num(); ++Index)
		{
			Result.Add(Inner->GetPropertyValue(Helper.GetRawPtr(Index)));
		}
		return Result;
	}

	TSet<int32> ReadSet(UObject* Object, const FSetProperty* Property)
	{
		TSet<int32> Result;
		if (Object == nullptr || Property == nullptr || CastField<FIntProperty>(Property->ElementProp) == nullptr)
		{
			return Result;
		}
		FScriptSetHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
		const FIntProperty* Element = CastField<FIntProperty>(Property->ElementProp);
		for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
		{
			if (Helper.IsValidIndex(Index))
			{
				Result.Add(Element->GetPropertyValue(Helper.GetElementPtr(Index)));
			}
		}
		return Result;
	}

	TMap<int32, int32> ReadMap(UObject* Object, const FMapProperty* Property)
	{
		TMap<int32, int32> Result;
		if (Object == nullptr || Property == nullptr ||
			CastField<FIntProperty>(Property->KeyProp) == nullptr ||
			CastField<FIntProperty>(Property->ValueProp) == nullptr)
		{
			return Result;
		}
		FScriptMapHelper Helper(Property, Property->ContainerPtrToValuePtr<void>(Object));
		const FIntProperty* Key = CastField<FIntProperty>(Property->KeyProp);
		const FIntProperty* Value = CastField<FIntProperty>(Property->ValueProp);
		for (int32 Index = 0; Index < Helper.GetMaxIndex(); ++Index)
		{
			if (Helper.IsValidIndex(Index))
			{
				Result.Add(
					Key->GetPropertyValue(Helper.GetKeyPtr(Index)),
					Value->GetPropertyValue(Helper.GetValuePtr(Index)));
			}
		}
		return Result;
	}

	bool StateMatches(UObject* Object, int32 Scalar, TConstArrayView<const int32> Array, const TSet<int32>& Set,
	                  const TMap<int32, int32>& Map)
	{
		const FIntProperty* ScalarProperty = Object != nullptr
			                                     ? FindFProperty<FIntProperty>(
				                                     Object->GetClass(), TEXT("AssetOpsScalar"))
			                                     : nullptr;
		const FArrayProperty* ArrayProperty = Object != nullptr
			                                      ? FindFProperty<FArrayProperty>(
				                                      Object->GetClass(), TEXT("AssetOpsValues"))
			                                      : nullptr;
		const FSetProperty* SetProperty = Object != nullptr
			                                  ? FindFProperty<FSetProperty>(Object->GetClass(), TEXT("AssetOpsEnabled"))
			                                  : nullptr;
		const FMapProperty* MapProperty = Object != nullptr
			                                  ? FindFProperty<FMapProperty>(Object->GetClass(), TEXT("AssetOpsWeights"))
			                                  : nullptr;
		const TArray<int32> ActualArray = ReadArray(Object, ArrayProperty);
		const TSet<int32> ActualSet = ReadSet(Object, SetProperty);
		const TMap<int32, int32> ActualMap = ReadMap(Object, MapProperty);
		return ScalarProperty != nullptr && ArrayProperty != nullptr && SetProperty != nullptr && MapProperty != nullptr
			&&
			ScalarProperty->GetPropertyValue_InContainer(Object) == Scalar &&
			ActualArray == TArray<int32>(Array) && ActualSet.Num() == Set.Num() && ActualSet.Includes(Set) &&
			ActualMap.Num() == Map.Num() && ActualMap.OrderIndependentCompareEqual(Map);
	}

	bool SavePackage(FAutomationTestBase& Test, UPackage* Package, UObject* Asset, const FString& PackageName,
	                 const TCHAR* Label)
	{
		if (Package == nullptr || Asset == nullptr)
		{
			return Test.TestFalse(*FString::Printf(TEXT("%s has valid package and asset"), Label), true);
		}
		FSavePackageArgs Args;
		Args.TopLevelFlags = RF_Public | RF_Standalone;
		const FString Filename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		return Test.TestTrue(
			*FString::Printf(TEXT("Saves %s"), Label),
			UPackage::SavePackage(Package, Asset, *Filename, Args));
	}

	UBlueprint* CreateOrdinaryChild(FAutomationTestBase& Test, UClass* ParentClass, UPackage* Package,
	                                const FString& Name)
	{
		UBlueprint* Blueprint = Package != nullptr
			                        ? FKismetEditorUtilities::CreateBlueprint(
				                        ParentClass,
				                        Package,
				                        FName(*Name),
				                        BPTYPE_Normal,
				                        UBlueprint::StaticClass(),
				                        UBlueprintGeneratedClass::StaticClass(),
				                        FName(TEXT("FinareInheritedClassAssetOperations")))
			                        : nullptr;
		if (!Test.TestNotNull(*FString::Printf(TEXT("Creates ordinary Blueprint %s"), *Name), Blueprint))
		{
			return nullptr;
		}
		FKismetEditorUtilities::CompileBlueprint(Blueprint);
		Test.TestEqual(*FString::Printf(TEXT("Compiles ordinary Blueprint %s"), *Name), Blueprint->Status, BS_UpToDate);
		return Blueprint;
	}

	bool CheckChain(
		FAutomationTestBase& Test,
		UBlueprint* Base,
		UBlueprint* Child,
		UBlueprint* Grandchild,
		UFinareAsset* Asset,
		const FString& Label,
		const bool ExpectedBaseStart = true,
		const bool ExpectedBaseTick = true,
		const bool ExpectedChildStart = true,
		const bool ExpectedChildTick = true,
		const bool ExpectedGrandchildStart = true,
		const bool ExpectedGrandchildTick = true)
	{
		const AFinareActor* BaseDefaults = Base != nullptr && Base->GeneratedClass != nullptr
			                                   ? Cast<AFinareActor>(Base->GeneratedClass->GetDefaultObject())
			                                   : nullptr;
		const AFinareActor* ChildDefaults = Child != nullptr && Child->GeneratedClass != nullptr
			                                    ? Cast<AFinareActor>(Child->GeneratedClass->GetDefaultObject())
			                                    : nullptr;
		const AFinareActor* GrandchildDefaults = Grandchild != nullptr && Grandchild->GeneratedClass != nullptr
			                                         ? Cast<AFinareActor>(
				                                         Grandchild->GeneratedClass->GetDefaultObject())
			                                         : nullptr;
		UFinareComponent* BaseComponent = GetComponent(Base);
		UFinareComponent* ChildComponent = GetComponent(Child);
		UFinareComponent* GrandchildComponent = GetComponent(Grandchild);
		const bool bIdentity = Base != nullptr && Child != nullptr && Grandchild != nullptr && Asset != nullptr &&
			Base->GeneratedClass != nullptr && Child->GeneratedClass != nullptr && Grandchild->GeneratedClass != nullptr
			&&
			Child->GeneratedClass->GetSuperClass() == Base->GeneratedClass &&
			Grandchild->GeneratedClass->GetSuperClass() == Child->GeneratedClass &&
			BaseComponent != nullptr && ChildComponent != nullptr && GrandchildComponent != nullptr &&
			BaseComponent != ChildComponent && BaseComponent != GrandchildComponent && ChildComponent !=
			GrandchildComponent &&
			BaseDefaults != nullptr && ChildDefaults != nullptr && GrandchildDefaults != nullptr &&
			BaseComponent->GetFName() == FName(TEXT("UnifiedScript")) &&
			ChildComponent->GetFName() == FName(TEXT("UnifiedScript")) &&
			GrandchildComponent->GetFName() == FName(TEXT("UnifiedScript")) &&
			BaseComponent->GetOuter() == BaseDefaults &&
			ChildComponent->GetOuter() == ChildDefaults &&
			GrandchildComponent->GetOuter() == GrandchildDefaults &&
			BaseComponent->bStartAutomatically == ExpectedBaseStart && BaseComponent->bTickAutomatically ==
			ExpectedBaseTick &&
			ChildComponent->bStartAutomatically == ExpectedChildStart && ChildComponent->bTickAutomatically ==
			ExpectedChildTick &&
			GrandchildComponent->bStartAutomatically == ExpectedGrandchildStart && GrandchildComponent->
			bTickAutomatically == ExpectedGrandchildTick &&
			BaseComponent->ScriptAsset.Get() == Asset && ChildComponent->ScriptAsset.Get() == Asset &&
			GrandchildComponent->ScriptAsset.Get() == Asset &&
			ChildComponent->GetArchetype() == BaseComponent && GrandchildComponent->GetArchetype() == ChildComponent;
		return Test.TestTrue(
			*FString::Printf(
				TEXT("%s preserves the exact ordinary parent chain, component archetypes, and ScriptAsset"), *Label),
			bIdentity);
	}

	bool CheckFullChainState(
		FAutomationTestBase& Test,
		UBlueprint* Base,
		UBlueprint* Child,
		UBlueprint* Grandchild,
		const bool bDuplicateChain,
		const TCHAR* Label)
	{
		const bool bValid = bDuplicateChain
			                    ? StateMatches(
				                    Base != nullptr && Base->GeneratedClass != nullptr
					                    ? Base->GeneratedClass->GetDefaultObject()
					                    : nullptr, 7, {2, 4}, {6, 8}, {{11, 13}, {17, 19}}) &&
			                    StateMatches(
				                    Child != nullptr && Child->GeneratedClass != nullptr
					                    ? Child->GeneratedClass->GetDefaultObject()
					                    : nullptr, 7, {2, 4}, {6, 8}, {{11, 13}, {17, 19}}) &&
			                    StateMatches(
				                    Grandchild != nullptr && Grandchild->GeneratedClass != nullptr
					                    ? Grandchild->GeneratedClass->GetDefaultObject()
					                    : nullptr, 7, {2, 4}, {6, 8}, {{11, 13}, {17, 19}})
			                    : StateMatches(
				                    Base != nullptr && Base->GeneratedClass != nullptr
					                    ? Base->GeneratedClass->GetDefaultObject()
					                    : nullptr, 7, {2, 4}, {6, 8}, {{11, 13}, {17, 19}}) &&
			                    StateMatches(
				                    Child != nullptr && Child->GeneratedClass != nullptr
					                    ? Child->GeneratedClass->GetDefaultObject()
					                    : nullptr, 77, {20, 21}, {30, 40}, {{5, 6}, {7, 8}}) &&
			                    StateMatches(
				                    Grandchild != nullptr && Grandchild->GeneratedClass != nullptr
					                    ? Grandchild->GeneratedClass->GetDefaultObject()
					                    : nullptr, 77, {20, 21}, {30, 40}, {{5, 6}, {7, 8}});
		return Test.TestTrue(*FString::Printf(TEXT("%s preserves complete base/child/grandchild state"), Label),
		                     bValid);
	}

	bool CheckVariableGuids(
		FAutomationTestBase& Test,
		UBlueprint* Blueprint,
		const TArray<FName>& VariableNames,
		const TArray<FGuid>& ExpectedGuids,
		const TCHAR* Label)
	{
		bool bValid = VariableNames.Num() == ExpectedGuids.Num();
		if (!bValid)
		{
			Test.AddError(FString::Printf(TEXT("%s has mismatched variable GUID expectation counts"), Label));
			return false;
		}
		for (int32 Index = 0; Index < VariableNames.Num(); ++Index)
		{
			FGuid ActualGuid;
			bValid = Test.TestTrue(
					*FString::Printf(TEXT("%s preserves variable GUID %s"), Label, *VariableNames[Index].ToString()),
					GetVariableGuid(Blueprint, VariableNames[Index], ActualGuid) && ActualGuid == ExpectedGuids[Index])
				&&
				bValid;
		}
		return bValid;
	}

	bool CheckSpawnedActor(
		FAutomationTestBase& Test,
		AFinareActor* Actor,
		UFinareAsset* Asset,
		UFinareComponent* ExpectedArchetype,
		const bool ExpectedStart,
		const bool ExpectedTick,
		const int32 ExpectedScalar,
		TConstArrayView<const int32> ExpectedArray,
		const TSet<int32>& ExpectedSet,
		const TMap<int32, int32>& ExpectedMap,
		const TCHAR* Label)
	{
		UFinareComponent* Component = Actor != nullptr ? Actor->ScriptComponent : nullptr;
		const bool bValid = Actor != nullptr && Component != nullptr &&
			Component->GetFName() == FName(TEXT("UnifiedScript")) &&
			Component->GetOuter() == Actor &&
			Component->GetArchetype() == ExpectedArchetype &&
			Component->ScriptAsset.Get() == Asset &&
			Component->bStartAutomatically == ExpectedStart &&
			Component->bTickAutomatically == ExpectedTick &&
			StateMatches(Actor, ExpectedScalar, ExpectedArray, ExpectedSet, ExpectedMap);
		if (!bValid)
		{
			Test.AddError(FString::Printf(
				TEXT(
					"%s identity/state mismatch: actor=%s component=%s outer=%s archetype=%s expectedArchetype=%s asset=%s expectedAsset=%s start=%d/%d tick=%d/%d"),
				Label,
				Actor != nullptr ? *Actor->GetPathName() : TEXT("<null>"),
				Component != nullptr ? *Component->GetPathName() : TEXT("<null>"),
				Component != nullptr && Component->GetOuter() != nullptr
					? *Component->GetOuter()->GetPathName()
					: TEXT("<null>"),
				Component != nullptr && Component->GetArchetype() != nullptr
					? *Component->GetArchetype()->GetPathName()
					: TEXT("<null>"),
				ExpectedArchetype != nullptr ? *ExpectedArchetype->GetPathName() : TEXT("<null>"),
				Component != nullptr && Component->ScriptAsset != nullptr
					? *Component->ScriptAsset->GetPathName()
					: TEXT("<null>"),
				Asset != nullptr ? *Asset->GetPathName() : TEXT("<null>"),
				Component != nullptr ? static_cast<int32>(Component->bStartAutomatically) : 0,
				static_cast<int32>(ExpectedStart),
				Component != nullptr ? static_cast<int32>(Component->bTickAutomatically) : 0,
				static_cast<int32>(ExpectedTick)));
		}
		return Test.TestTrue(Label, bValid);
	}

	bool CheckPersistentIdentity(
		FAutomationTestBase& Test,
		UBlueprint* Blueprint,
		UFinareAsset* Asset,
		const FGuid& ExpectedClassId,
		const FString& ExpectedGeneratedClassPath,
		const FString& ExpectedAssetPath,
		const TCHAR* Label)
	{
		const FString ActualGeneratedClassPath = Blueprint != nullptr && Blueprint->GeneratedClass != nullptr
			                                         ? Blueprint->GeneratedClass->GetPathName()
			                                         : FString();
		const FString ActualAssetPath = Asset != nullptr ? Asset->GetPathName() : FString();
		const bool bValid = Blueprint != nullptr && Blueprint->GeneratedClass != nullptr && Asset != nullptr &&
			ExpectedClassId.IsValid() &&
			Asset->ScriptClassId == ExpectedClassId &&
			ActualGeneratedClassPath.Equals(ExpectedGeneratedClassPath, ESearchCase::CaseSensitive) &&
			Asset->GeneratedClassPath.ToString().Equals(ExpectedGeneratedClassPath, ESearchCase::CaseSensitive) &&
			ActualAssetPath.Equals(ExpectedAssetPath, ESearchCase::CaseSensitive);
		return Test.TestTrue(
			*FString::Printf(
				TEXT("%s preserves ClassId=%s, GeneratedClassPath=%s, and ScriptAsset=%s"),
				Label,
				*ExpectedClassId.ToString(EGuidFormats::Digits),
				*ExpectedGeneratedClassPath,
				*ExpectedAssetPath),
			bValid);
	}

	UBlueprint* FindBlueprint(const FString& ObjectPath)
	{
		return FindObject<UBlueprint>(nullptr, *ObjectPath);
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareInheritedClassAssetOperationsTest,
	"Finare.Unreal.ScriptClass.InheritedClassAssetOperations",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareInheritedClassAssetOperationsTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::InheritedClassAssetOperations::Tests;
	FScopedCleanup Cleanup;
	Cleanup.Test = this;
	FString DeploymentPath;
	FString DeploymentError;
	if (!TestTrue(
		TEXT("Resolves the configured deployment manifest"),
		ResolveProjectDeploymentPath(DeploymentPath, DeploymentError)))
	{
		if (!DeploymentError.IsEmpty())
		{
			AddError(DeploymentError);
		}
		return false;
	}
	if (!TestTrue(
		TEXT("Snapshots the configured deployment manifest"), ReadBytes(DeploymentPath, Cleanup.DeploymentBytes)))
	{
		return false;
	}
	Cleanup.DeploymentPath = DeploymentPath;
	Cleanup.bDeploymentSnapshotReady = true;
	Cleanup.FirstPlayPath = FFinareProjectSetup::GetFirstPlayRequestPath(FPaths::ProjectDir());
	Cleanup.bHadFirstPlay = IFileManager::Get().FileExists(*Cleanup.FirstPlayPath);
	if (Cleanup.bHadFirstPlay &&
		!TestTrue(TEXT("Snapshots the existing first-play request"), ReadBytes(Cleanup.FirstPlayPath,
		                                                                       Cleanup.FirstPlayBytes)))
	{
		Cleanup.FirstPlayPath.Reset();
		return false;
	}
	Cleanup.bFirstPlaySnapshotReady = true;

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(10);
	const FString Root = TEXT("/Game/M13C3InheritedAssetOps_") + Suffix;
	Cleanup.PackageRootDirectory = FPaths::ConvertRelativePathToFull(
		FPaths::Combine(FPaths::ProjectDir(), TEXT("Content"), Root.Mid(6)));
	const FString BaseName = TEXT("FactoryBase_") + Suffix;
	const FString ChildName = TEXT("FactoryChild_") + Suffix;
	const FString GrandchildName = TEXT("FactoryGrandchild_") + Suffix;
	const FString RenamedName = BaseName + TEXT("_Renamed");
	const FString DuplicateName = BaseName + TEXT("_Duplicate");
	const FString DuplicateChildName = DuplicateName + TEXT("_Child");
	const FString DuplicateGrandchildName = DuplicateName + TEXT("_Grandchild");
	const FString DirtyRenameName = BaseName + TEXT("_DirtyReject");
	const FString RenameRollbackName = BaseName + TEXT("_RenameRollback");
	const FString DuplicateRollbackName = BaseName + TEXT("_DuplicateRollback");
	const FString BasePackage = Root / BaseName;
	const FString ChildPackage = Root / ChildName;
	const FString GrandchildPackage = Root / GrandchildName;
	const FString SidecarPackage = Root / (TEXT("SA_") + BaseName);
	const FString RenamedPackage = Root / RenamedName;
	const FString RenamedSidecar = Root / (TEXT("SA_") + RenamedName);
	const FString DuplicatePackage = Root / DuplicateName;
	const FString DuplicateSidecar = Root / (TEXT("SA_") + DuplicateName);
	const FString DuplicateChildPackage = Root / DuplicateChildName;
	const FString DuplicateGrandchildPackage = Root / DuplicateGrandchildName;
	const FString DirtyRenamePackage = Root / DirtyRenameName;
	const FString DirtyRenameSidecar = Root / (TEXT("SA_") + DirtyRenameName);
	const FString RenameRollbackPackage = Root / RenameRollbackName;
	const FString RenameRollbackSidecar = Root / (TEXT("SA_") + RenameRollbackName);
	const FString DuplicateRollbackPackage = Root / DuplicateRollbackName;
	const FString DuplicateRollbackSidecar = Root / (TEXT("SA_") + DuplicateRollbackName);
	Cleanup.PackageNames = {
		BasePackage, ChildPackage, GrandchildPackage, SidecarPackage,
		RenamedPackage, RenamedSidecar, DuplicatePackage, DuplicateSidecar,
		DuplicateChildPackage, DuplicateGrandchildPackage,
		DirtyRenamePackage, DirtyRenameSidecar,
		RenameRollbackPackage, RenameRollbackSidecar,
		DuplicateRollbackPackage, DuplicateRollbackSidecar
	};

	UPackage* BaseOuter = CreatePackage(*BasePackage);
	UFinareActorClassFactory* Factory = NewObject<UFinareActorClassFactory>();
	UBlueprint* BaseBlueprint = BaseOuter != nullptr && Factory != nullptr
		                            ? Cast<UBlueprint>(Factory->FactoryCreateNew(
			                            UBlueprint::StaticClass(), BaseOuter, FName(*BaseName),
			                            RF_Public | RF_Standalone | RF_Transactional, nullptr, GWarn,
			                            TEXT("FinareInheritedClassAssetOperations")))
		                            : nullptr;
	UFinareAsset* ScriptAsset = GetScriptAsset(BaseBlueprint);
	if (!TestNotNull(TEXT("Factory creates the managed base Blueprint"), BaseBlueprint) ||
		!TestNotNull(TEXT("Factory creates the managed base ScriptAsset"), ScriptAsset))
	{
		return false;
	}
	Cleanup.SourcePaths.Add(ResolveConfiguredPath(ScriptAsset->SourcePath));
	Cleanup.PackageNames.AddUnique(ScriptAsset->GetOutermost()->GetName());
	const FGuid OriginalClassId = ScriptAsset->ScriptClassId;
	if (!AddStateVariables(*this, *BaseBlueprint))
	{
		return false;
	}
	const TArray<FName> StateVariableNames = {
		FName(TEXT("AssetOpsScalar")),
		FName(TEXT("AssetOpsValues")),
		FName(TEXT("AssetOpsEnabled")),
		FName(TEXT("AssetOpsWeights"))
	};
	TArray<FGuid> BaseVariableGuids;
	for (const FName VariableName : StateVariableNames)
	{
		FGuid VariableGuid;
		if (!TestTrue(
			*FString::Printf(TEXT("Captures stable GUID for %s"), *VariableName.ToString()),
			GetVariableGuid(BaseBlueprint, VariableName, VariableGuid)))
		{
			return false;
		}
		BaseVariableGuids.Add(VariableGuid);
	}
	FKismetEditorUtilities::CompileBlueprint(BaseBlueprint);
	if (!TestEqual(
		TEXT("Compiles the factory base before creating ordinary descendants"), BaseBlueprint->Status, BS_UpToDate))
	{
		return false;
	}
	const FString ScalarText = TEXT("7");
	const FIntProperty* BaseScalar = FindFProperty<FIntProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsScalar"));
	const FArrayProperty* BaseArray = FindFProperty<FArrayProperty>(BaseBlueprint->GeneratedClass,
	                                                                TEXT("AssetOpsValues"));
	const FSetProperty* BaseSet = FindFProperty<FSetProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsEnabled"));
	const FMapProperty* BaseMap = FindFProperty<FMapProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsWeights"));
	UObject* BaseCdo = BaseBlueprint->GeneratedClass->GetDefaultObject();
	if (!TestTrue(
			TEXT("Factory base exposes all ordinary state properties"),
			BaseScalar != nullptr && BaseArray != nullptr && BaseSet != nullptr && BaseMap != nullptr) ||
		!ImportValueChecked(*this, BaseCdo, BaseScalar, *ScalarText, TEXT("Imports base scalar default")) ||
		!ImportValueChecked(*this, BaseCdo, BaseArray, TEXT("(2,4)"), TEXT("Imports base array default")) ||
		!ImportValueChecked(*this, BaseCdo, BaseSet, TEXT("(6,8)"), TEXT("Imports base set default")) ||
		!ImportValueChecked(*this, BaseCdo, BaseMap, TEXT("((11,13),(17,19))"), TEXT("Imports base map default")))
	{
		return false;
	}
	FBlueprintEditorUtils::MarkBlueprintAsModified(BaseBlueprint);
	BaseBlueprint->MarkPackageDirty();
	FKismetEditorUtilities::CompileBlueprint(BaseBlueprint);
	BaseScalar = FindFProperty<FIntProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsScalar"));
	BaseArray = FindFProperty<FArrayProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsValues"));
	BaseSet = FindFProperty<FSetProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsEnabled"));
	BaseMap = FindFProperty<FMapProperty>(BaseBlueprint->GeneratedClass, TEXT("AssetOpsWeights"));
	BaseCdo = BaseBlueprint->GeneratedClass->GetDefaultObject();
	if (!TestTrue(
		TEXT("Base defaults survive the factory Blueprint compile"),
		StateMatches(BaseCdo, 7, {2, 4}, {6, 8}, {{11, 13}, {17, 19}})))
	{
		return false;
	}

	UPackage* ChildOuter = CreatePackage(*ChildPackage);
	UBlueprint* ChildBlueprint = CreateOrdinaryChild(*this, BaseBlueprint->GeneratedClass, ChildOuter, ChildName);
	if (!TestNotNull(TEXT("Creates the ordinary child of the factory class"), ChildBlueprint))
	{
		return false;
	}
	UObject* ChildCdo = ChildBlueprint->GeneratedClass->GetDefaultObject();
	const FIntProperty* ChildScalar = FindFProperty<FIntProperty>(ChildBlueprint->GeneratedClass,
	                                                              TEXT("AssetOpsScalar"));
	const FArrayProperty* ChildArray = FindFProperty<FArrayProperty>(ChildBlueprint->GeneratedClass,
	                                                                 TEXT("AssetOpsValues"));
	const FSetProperty* ChildSet = FindFProperty<FSetProperty>(ChildBlueprint->GeneratedClass, TEXT("AssetOpsEnabled"));
	const FMapProperty* ChildMap = FindFProperty<FMapProperty>(ChildBlueprint->GeneratedClass, TEXT("AssetOpsWeights"));
	if (!TestTrue(TEXT("Child exposes inherited state properties"), ChildScalar && ChildArray && ChildSet && ChildMap)
		||
		!ImportValueChecked(*this, ChildCdo, ChildScalar, TEXT("77"), TEXT("Imports child scalar override")) ||
		!ImportValueChecked(*this, ChildCdo, ChildArray, TEXT("(20,21)"), TEXT("Imports child array override")) ||
		!ImportValueChecked(*this, ChildCdo, ChildSet, TEXT("(30,40)"), TEXT("Imports child set override")) ||
		!ImportValueChecked(*this, ChildCdo, ChildMap, TEXT("((5,6),(7,8))"), TEXT("Imports child map override")))
	{
		return false;
	}
	if (!SetComponentBoolDefault(*this, *ChildBlueprint, TEXT("bStartAutomatically"), false) ||
		!SetComponentBoolDefault(*this, *ChildBlueprint, TEXT("bTickAutomatically"), false))
	{
		return false;
	}
	FBlueprintEditorUtils::MarkBlueprintAsModified(ChildBlueprint);
	ChildBlueprint->MarkPackageDirty();
	FKismetEditorUtilities::CompileBlueprint(ChildBlueprint);
	UFinareComponent* ChildComponentAfterCompile = GetComponent(ChildBlueprint);
	if (!TestTrue(TEXT("Child whole-value overrides survive the child compile"),
	              StateMatches(ChildBlueprint->GeneratedClass->GetDefaultObject(), 77, {20, 21}, {30, 40},
	                           {{5, 6}, {7, 8}}) &&
	              ChildComponentAfterCompile != nullptr && ChildComponentAfterCompile->bStartAutomatically == false &&
	              ChildComponentAfterCompile->bTickAutomatically == false))
	{
		return false;
	}
	UPackage* GrandchildOuter = CreatePackage(*GrandchildPackage);
	UBlueprint* GrandchildBlueprint = CreateOrdinaryChild(*this, ChildBlueprint->GeneratedClass, GrandchildOuter,
	                                                      GrandchildName);
	if (!TestNotNull(TEXT("Creates the ordinary grandchild after the child override is authored"), GrandchildBlueprint))
	{
		return false;
	}
	UFinareComponent* GrandchildComponentAfterCompile = GetComponent(GrandchildBlueprint);
	if (!TestTrue(
		TEXT("Child whole-value overrides and grandchild follower survive compile"),
		StateMatches(ChildBlueprint->GeneratedClass->GetDefaultObject(), 77, {20, 21}, {30, 40}, {{5, 6}, {7, 8}}) &&
		StateMatches(GrandchildBlueprint->GeneratedClass->GetDefaultObject(), 77, {20, 21}, {30, 40},
		             {{5, 6}, {7, 8}}) &&
		ChildComponentAfterCompile != nullptr && ChildComponentAfterCompile->bStartAutomatically == false &&
		ChildComponentAfterCompile->bTickAutomatically == false &&
		GrandchildComponentAfterCompile != nullptr && GrandchildComponentAfterCompile->bStartAutomatically == false &&
		GrandchildComponentAfterCompile->bTickAutomatically == false))
	{
		return false;
	}
	if (!CheckChain(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, ScriptAsset,
	                TEXT("Initial factory chain"), true, true, false, false, false, false))
	{
		return false;
	}

	const FString ChildPath = ChildBlueprint->GetPathName();
	const FString GrandchildPath = GrandchildBlueprint->GetPathName();
	const FString BasePath = BaseBlueprint->GetPathName();
	const FString BaseFilename = FPackageName::LongPackageNameToFilename(
		BasePackage, FPackageName::GetAssetPackageExtension());
	const FString ChildFilename = FPackageName::LongPackageNameToFilename(
		ChildPackage, FPackageName::GetAssetPackageExtension());
	const FString GrandchildFilename = FPackageName::LongPackageNameToFilename(
		GrandchildPackage, FPackageName::GetAssetPackageExtension());
	const FString SidecarFilename = FPackageName::LongPackageNameToFilename(
		SidecarPackage, FPackageName::GetAssetPackageExtension());
	if (!SavePackage(*this, BaseOuter, BaseBlueprint, BasePackage, TEXT("base package")) ||
		!SavePackage(*this, ChildOuter, ChildBlueprint, ChildPackage, TEXT("ordinary child package")) ||
		!SavePackage(*this, GrandchildOuter, GrandchildBlueprint, GrandchildPackage,
		             TEXT("ordinary grandchild package")) ||
		!SavePackage(*this, ScriptAsset->GetOutermost(), ScriptAsset, ScriptAsset->GetOutermost()->GetName(),
		             TEXT("factory sidecar package")))
	{
		return false;
	}
	IAssetRegistry& Registry = FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
	Registry.ScanModifiedAssetFiles({BaseFilename, ChildFilename, GrandchildFilename, SidecarFilename});
	TArray<FName> Referencers;
	Registry.GetReferencers(FName(*BasePackage), Referencers, UE::AssetRegistry::EDependencyCategory::Package);
	TArray<FName> ChildReferencers;
	Registry.GetReferencers(FName(*ChildPackage), ChildReferencers, UE::AssetRegistry::EDependencyCategory::Package);
	if (!TestTrue(
			TEXT("Asset Registry sees the direct ordinary child package as a base referencer"),
			Referencers.Contains(FName(*ChildPackage))) ||
		!TestTrue(
			TEXT("Asset Registry sees the grandchild against its immediate child"),
			ChildReferencers.Contains(FName(*GrandchildPackage))))
	{
		return false;
	}

	TArray<uint8> SourceBeforeDirtyReject;
	TArray<uint8> DeploymentBeforeDirtyReject;
	TArray<uint8> FirstPlayBeforeDirtyReject;
	TestTrue(TEXT("Snapshots managed source before dirty referencer rejection"), ReadBytes(Cleanup.SourcePaths[0],
		         SourceBeforeDirtyReject));
	TestTrue(TEXT("Snapshots deployment before dirty referencer rejection"), ReadBytes(Cleanup.DeploymentPath,
		         DeploymentBeforeDirtyReject));
	TestTrue(TEXT("Snapshots first-play before dirty referencer rejection"), ReadBytes(Cleanup.FirstPlayPath,
		         FirstPlayBeforeDirtyReject));

	const bool BaseDirtyBeforeReject = BaseOuter->IsDirty();
	const bool ChildDirtyBeforeReject = ChildOuter->IsDirty();
	const bool GrandchildDirtyBeforeReject = GrandchildOuter->IsDirty();
	const FString DirtyBasePathBefore = BaseBlueprint->GeneratedClass->GetPathName();
	const FString DirtyChildSuperBefore = ChildBlueprint->GeneratedClass->GetSuperClass()->GetPathName();
	ChildOuter->MarkPackageDirty();
	GrandchildOuter->MarkPackageDirty();
	FFinareClassAssetOperationResult DirtyReject;
	TestFalse(
		TEXT("Rename rejects dirty ordinary descendant referencers"),
		FFinareClassAssetOperations::Rename(ScriptAsset, DirtyRenamePackage, DirtyReject));
	TestTrue(
		TEXT("Dirty descendant rejection explains the save boundary"),
		DirtyReject.Error.Contains(TEXT("unsaved changes")));
	TestEqual(TEXT("Dirty rejection preserves base identity"), BaseBlueprint->GeneratedClass->GetPathName(),
	          DirtyBasePathBefore);
	TestEqual(
		TEXT("Dirty rejection preserves child parent identity"),
		ChildBlueprint->GeneratedClass->GetSuperClass()->GetPathName(), DirtyChildSuperBefore);
	TestTrue(TEXT("Dirty rejection preserves complete state values"), CheckChain(*this, BaseBlueprint, ChildBlueprint,
		         GrandchildBlueprint, ScriptAsset, TEXT("Dirty rejection chain"), true, true, false, false, false,
		         false) &&
	         CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                             TEXT("Dirty rejection")) &&
	         CheckVariableGuids(*this, BaseBlueprint, StateVariableNames, BaseVariableGuids, TEXT("Dirty rejection")));
	TestEqual(TEXT("Dirty rejection preserves base dirty flag"), BaseOuter->IsDirty(), BaseDirtyBeforeReject);
	TestTrue(
		TEXT("Dirty rejection leaves descendant packages dirty"), ChildOuter->IsDirty() && GrandchildOuter->IsDirty());
	TArray<uint8> SourceAfterDirtyReject;
	TArray<uint8> DeploymentAfterDirtyReject;
	TArray<uint8> FirstPlayAfterDirtyReject;
	TestTrue(TEXT("Reads managed source after dirty referencer rejection"), ReadBytes(Cleanup.SourcePaths[0],
		         SourceAfterDirtyReject));
	TestTrue(TEXT("Reads deployment after dirty referencer rejection"), ReadBytes(Cleanup.DeploymentPath,
		         DeploymentAfterDirtyReject));
	TestTrue(TEXT("Reads first-play after dirty referencer rejection"), ReadBytes(Cleanup.FirstPlayPath,
		         FirstPlayAfterDirtyReject));
	TestEqual(TEXT("Dirty rejection preserves managed source bytes"), SourceAfterDirtyReject, SourceBeforeDirtyReject);
	TestEqual(TEXT("Dirty rejection preserves deployment bytes"), DeploymentAfterDirtyReject,
	          DeploymentBeforeDirtyReject);
	TestEqual(TEXT("Dirty rejection preserves first-play bytes"), FirstPlayAfterDirtyReject,
	          FirstPlayBeforeDirtyReject);
	ChildOuter->SetDirtyFlag(false);
	GrandchildOuter->SetDirtyFlag(false);

	TArray<uint8> DeploymentBeforeRollback;
	TArray<uint8> FirstPlayBeforeRollback;
	TArray<uint8> SourceBeforeRollback;
	ReadBytes(Cleanup.DeploymentPath, DeploymentBeforeRollback);
	ReadBytes(Cleanup.FirstPlayPath, FirstPlayBeforeRollback);
	ReadBytes(Cleanup.SourcePaths[0], SourceBeforeRollback);
	const bool BaseDirtyBeforeRenameRollback = BaseBlueprint->GetOutermost()->IsDirty();
	const bool ChildDirtyBeforeRenameRollback = ChildBlueprint->GetOutermost()->IsDirty();
	const bool GrandchildDirtyBeforeRenameRollback = GrandchildBlueprint->GetOutermost()->IsDirty();
	const bool AssetDirtyBeforeRenameRollback = ScriptAsset->GetOutermost()->IsDirty();
	FFinareClassAssetOperationResult RenameRollback;
	TestFalse(TEXT("Injected rename publication failure rejects the operation"), FFinareClassAssetOperations::Rename(
		          ScriptAsset, RenameRollbackPackage, RenameRollback,
		          EFinareClassAssetOperationFailurePoint::AfterExternalPublication));
	TestTrue(TEXT("Injected rename failure rolls back with ordinary descendants present"), RenameRollback.bRolledBack);
	TArray<uint8> DeploymentAfterRollback;
	TArray<uint8> FirstPlayAfterRollback;
	TArray<uint8> SourceAfterRollback;
	ReadBytes(Cleanup.DeploymentPath, DeploymentAfterRollback);
	ReadBytes(Cleanup.FirstPlayPath, FirstPlayAfterRollback);
	ReadBytes(Cleanup.SourcePaths[0], SourceAfterRollback);
	TestEqual(TEXT("Rename rollback preserves deployment bytes with descendants"), DeploymentAfterRollback,
	          DeploymentBeforeRollback);
	TestEqual(TEXT("Rename rollback preserves first-Play bytes with descendants"), FirstPlayAfterRollback,
	          FirstPlayBeforeRollback);
	TestEqual(TEXT("Rename rollback preserves managed source bytes with descendants"), SourceAfterRollback,
	          SourceBeforeRollback);
	TestEqual(TEXT("Rename rollback preserves base dirty flag"), BaseBlueprint->GetOutermost()->IsDirty(),
	          BaseDirtyBeforeRenameRollback);
	TestEqual(TEXT("Rename rollback preserves child dirty flag"), ChildBlueprint->GetOutermost()->IsDirty(),
	          ChildDirtyBeforeRenameRollback);
	TestEqual(TEXT("Rename rollback preserves grandchild dirty flag"), GrandchildBlueprint->GetOutermost()->IsDirty(),
	          GrandchildDirtyBeforeRenameRollback);
	TestEqual(TEXT("Rename rollback preserves sidecar dirty flag"), ScriptAsset->GetOutermost()->IsDirty(),
	          AssetDirtyBeforeRenameRollback);
	TestTrue(TEXT("Rename rollback preserves descendant chain and overrides"), CheckChain(*this, BaseBlueprint,
		         ChildBlueprint, GrandchildBlueprint, ScriptAsset, TEXT("Rename rollback"), true, true, false, false,
		         false, false) &&
	         CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                             TEXT("Rename rollback")));

	BaseBlueprint = nullptr;
	ScriptAsset = nullptr;
	FFinareClassAssetOperationResult RenameResult;
	if (!TestTrue(
		TEXT("Clean rename updates a factory class with ordinary descendants"),
		FFinareClassAssetOperations::Rename(GetScriptAsset(FindBlueprint(BasePath)), RenamedPackage, RenameResult)))
	{
		return false;
	}
	BaseBlueprint = RenameResult.Blueprint;
	ScriptAsset = RenameResult.ScriptAsset;
	Cleanup.SourcePaths.Add(RenameResult.SourcePath);
	ChildBlueprint = FindBlueprint(ChildPath);
	GrandchildBlueprint = FindBlueprint(GrandchildPath);
	TestEqual(TEXT("Rename preserves stable factory ClassId"), ScriptAsset->ScriptClassId, OriginalClassId);
	TestEqual(TEXT("Rename updates exact ScriptAsset identity"), ScriptAsset->GetPathName(),
	          RenamedSidecar + TEXT(".SA_") + RenamedName);
	TestTrue(
		TEXT("Rename reports the direct ordinary child package as repaired"),
		RenameResult.RepairedUEReferencerPackages.Contains(ChildPackage));
	TestTrue(TEXT("Clean rename preserves descendants and whole-value overrides"), CheckChain(*this, BaseBlueprint,
		         ChildBlueprint, GrandchildBlueprint, ScriptAsset, TEXT("Clean rename"), true, true, false, false,
		         false, false) &&
	         CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                             TEXT("Clean rename")));
	TestFalse(TEXT("Clean rename removes the old base package"), IFileManager::Get().FileExists(*BaseFilename));
	TestFalse(TEXT("Clean rename removes the old sidecar package"), IFileManager::Get().FileExists(*SidecarFilename));

	const FString RenamedBasePath = BaseBlueprint->GetPathName();
	const FString RenamedAssetPath = ScriptAsset->GetPathName();
	const FString RenamedGeneratedClassPath = BaseBlueprint->GeneratedClass->GetPathName();
	CheckPersistentIdentity(*this, BaseBlueprint, ScriptAsset, OriginalClassId, RenamedGeneratedClassPath,
	                        RenamedAssetPath, TEXT("Renamed identity"));
	const FString RenamedBaseFilename = FPackageName::LongPackageNameToFilename(
		RenamedPackage, FPackageName::GetAssetPackageExtension());
	const FString RenamedSidecarFilename = FPackageName::LongPackageNameToFilename(
		RenamedSidecar, FPackageName::GetAssetPackageExtension());
	if (!SavePackage(*this, BaseBlueprint->GetOutermost(), BaseBlueprint, RenamedPackage,
	                 TEXT("renamed base package")) ||
		!SavePackage(*this, ChildBlueprint->GetOutermost(), ChildBlueprint, ChildPackage,
		             TEXT("renamed ordinary child package")) ||
		!SavePackage(*this, GrandchildBlueprint->GetOutermost(), GrandchildBlueprint, GrandchildPackage,
		             TEXT("renamed ordinary grandchild package")) ||
		!SavePackage(*this, ScriptAsset->GetOutermost(), ScriptAsset, RenamedSidecar, TEXT("renamed sidecar package")))
	{
		return false;
	}
	const TWeakObjectPtr<UBlueprint> WeakBase = BaseBlueprint;
	const TWeakObjectPtr<UBlueprint> WeakChild = ChildBlueprint;
	const TWeakObjectPtr<UBlueprint> WeakGrandchild = GrandchildBlueprint;
	const TWeakObjectPtr<UFinareAsset> WeakAsset = ScriptAsset;
	TArray<UPackage*> RenamePackages{
		GrandchildBlueprint->GetOutermost(), ChildBlueprint->GetOutermost(), BaseBlueprint->GetOutermost(),
		ScriptAsset->GetOutermost()
	};
	for (UPackage* Package : RenamePackages)
	{
		Package->SetDirtyFlag(false);
	}
	TestTrue(TEXT("Unloads renamed factory chain and sidecar"), UPackageTools::UnloadPackages(RenamePackages));
	CollectGarbage(RF_NoFlags);
	TestTrue(
		TEXT("Unload invalidates all renamed chain weak references"),
		!WeakBase.IsValid() && !WeakChild.IsValid() && !WeakGrandchild.IsValid() && !WeakAsset.IsValid());
	BaseBlueprint = LoadObject<UBlueprint>(nullptr, *RenamedBasePath);
	ChildBlueprint = LoadObject<UBlueprint>(nullptr, *ChildPath);
	GrandchildBlueprint = LoadObject<UBlueprint>(nullptr, *GrandchildPath);
	ScriptAsset = LoadObject<UFinareAsset>(nullptr, *RenamedAssetPath);
	if (!TestNotNull(TEXT("Reloads renamed base"), BaseBlueprint) || !
		TestNotNull(TEXT("Reloads renamed child"), ChildBlueprint) || !TestNotNull(
			TEXT("Reloads renamed grandchild"), GrandchildBlueprint) || !TestNotNull(
			TEXT("Reloads renamed sidecar"), ScriptAsset))
	{
		return false;
	}
	TestTrue(TEXT("Reloaded renamed chain preserves complete defaults"), CheckChain(*this, BaseBlueprint,
		         ChildBlueprint, GrandchildBlueprint, ScriptAsset, TEXT("Reloaded clean rename"), true, true, false,
		         false, false, false) &&
	         CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                             TEXT("Reloaded clean rename")));
	CheckPersistentIdentity(*this, BaseBlueprint, ScriptAsset, OriginalClassId, RenamedGeneratedClassPath,
	                        RenamedAssetPath, TEXT("Reloaded renamed identity"));
	CheckVariableGuids(*this, BaseBlueprint, StateVariableNames, BaseVariableGuids, TEXT("Reloaded rename"));

	FFinareClassAssetOperationResult DuplicateResult;
	if (!TestTrue(
		TEXT("Duplicates the renamed factory class"),
		FFinareClassAssetOperations::Duplicate(ScriptAsset, DuplicatePackage, DuplicateResult)))
	{
		return false;
	}
	Cleanup.SourcePaths.Add(DuplicateResult.SourcePath);
	const FString DuplicateBasePath = DuplicateResult.Blueprint->GetPathName();
	const FString DuplicateAssetPath = DuplicateResult.ScriptAsset->GetPathName();
	const FGuid DuplicateClassId = DuplicateResult.ScriptAsset->ScriptClassId;
	const FString DuplicateGeneratedClassPath = DuplicateResult.Blueprint->GeneratedClass->GetPathName();
	CheckPersistentIdentity(*this, DuplicateResult.Blueprint, DuplicateResult.ScriptAsset, DuplicateClassId,
	                        DuplicateGeneratedClassPath, DuplicateAssetPath, TEXT("Duplicate identity"));
	TestNotEqual(TEXT("Duplicate receives a new ClassId"), DuplicateResult.ScriptAsset->ScriptClassId, OriginalClassId);
	TestTrue(
		TEXT("Duplicate owns an independent ScriptAsset"),
		GetScriptAsset(DuplicateResult.Blueprint) == DuplicateResult.ScriptAsset && DuplicateResult.ScriptAsset !=
		ScriptAsset);
	TestTrue(TEXT("Original renamed chain remains on original ScriptAsset"), CheckChain(*this, BaseBlueprint,
		         ChildBlueprint, GrandchildBlueprint, ScriptAsset, TEXT("Original after duplicate"), true, true, false,
		         false, false, false));

	UPackage* DuplicateChildOuter = CreatePackage(*DuplicateChildPackage);
	UBlueprint* DuplicateChildBlueprint = CreateOrdinaryChild(*this, DuplicateResult.Blueprint->GeneratedClass,
	                                                          DuplicateChildOuter, DuplicateChildName);
	UPackage* DuplicateGrandchildOuter = CreatePackage(*DuplicateGrandchildPackage);
	UBlueprint* DuplicateGrandchildBlueprint = DuplicateChildBlueprint != nullptr
		                                           ? CreateOrdinaryChild(
			                                           *this, DuplicateChildBlueprint->GeneratedClass,
			                                           DuplicateGrandchildOuter, DuplicateGrandchildName)
		                                           : nullptr;
	if (!TestNotNull(TEXT("Creates an ordinary child under the duplicate"), DuplicateChildBlueprint) || !TestNotNull(
		TEXT("Creates an ordinary grandchild under the duplicate"), DuplicateGrandchildBlueprint))
	{
		return false;
	}
	TestTrue(TEXT("Duplicate descendants inherit the duplicate ScriptAsset and base defaults"), CheckChain(
		         *this, DuplicateResult.Blueprint, DuplicateChildBlueprint, DuplicateGrandchildBlueprint,
		         DuplicateResult.ScriptAsset, TEXT("Duplicate chain")) &&
	         CheckFullChainState(*this, DuplicateResult.Blueprint, DuplicateChildBlueprint,
	                             DuplicateGrandchildBlueprint, true, TEXT("Duplicate chain")));
	TArray<uint8> DeploymentBeforeDuplicateRollback;
	TArray<uint8> FirstPlayBeforeDuplicateRollback;
	TArray<uint8> OriginalSourceBeforeDuplicateRollback;
	TArray<uint8> DuplicateSourceBeforeDuplicateRollback;
	TestTrue(TEXT("Snapshots original managed source before duplicate rollback"), ReadBytes(RenameResult.SourcePath,
		         OriginalSourceBeforeDuplicateRollback));
	TestTrue(TEXT("Snapshots duplicate managed source before duplicate rollback"), ReadBytes(DuplicateResult.SourcePath,
		         DuplicateSourceBeforeDuplicateRollback));
	TestTrue(TEXT("Snapshots deployment before duplicate rollback with both chains"), ReadBytes(Cleanup.DeploymentPath,
		         DeploymentBeforeDuplicateRollback));
	TestTrue(TEXT("Snapshots first-play before duplicate rollback with both chains"), ReadBytes(Cleanup.FirstPlayPath,
		         FirstPlayBeforeDuplicateRollback));
	const bool OriginalBaseDirtyBeforeDuplicateRollback = BaseBlueprint->GetOutermost()->IsDirty();
	const bool OriginalChildDirtyBeforeDuplicateRollback = ChildBlueprint->GetOutermost()->IsDirty();
	const bool OriginalGrandchildDirtyBeforeDuplicateRollback = GrandchildBlueprint->GetOutermost()->IsDirty();
	const bool OriginalAssetDirtyBeforeDuplicateRollback = ScriptAsset->GetOutermost()->IsDirty();
	const bool DuplicateBaseDirtyBeforeDuplicateRollback = DuplicateResult.Blueprint->GetOutermost()->IsDirty();
	const bool DuplicateChildDirtyBeforeDuplicateRollback = DuplicateChildBlueprint->GetOutermost()->IsDirty();
	const bool DuplicateGrandchildDirtyBeforeDuplicateRollback = DuplicateGrandchildBlueprint->GetOutermost()->
		IsDirty();
	const bool DuplicateAssetDirtyBeforeDuplicateRollback = DuplicateResult.ScriptAsset->GetOutermost()->IsDirty();
	FFinareClassAssetOperationResult DuplicateRollback;
	TestFalse(
		TEXT("Injected duplicate publication failure rejects the operation with both chains present"),
		FFinareClassAssetOperations::Duplicate(ScriptAsset, DuplicateRollbackPackage, DuplicateRollback,
		                                       EFinareClassAssetOperationFailurePoint::AfterExternalPublication));
	TestTrue(TEXT("Duplicate rollback preserves both ordinary chains"), DuplicateRollback.bRolledBack &&
	         CheckChain(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, ScriptAsset,
	                    TEXT("Duplicate rollback original"), true, true, false, false, false, false) &&
	         CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                             TEXT("Duplicate rollback original")) &&
	         CheckChain(*this, DuplicateResult.Blueprint, DuplicateChildBlueprint, DuplicateGrandchildBlueprint,
	                    DuplicateResult.ScriptAsset, TEXT("Duplicate rollback duplicate")) &&
	         CheckFullChainState(*this, DuplicateResult.Blueprint, DuplicateChildBlueprint,
	                             DuplicateGrandchildBlueprint, true, TEXT("Duplicate rollback duplicate")));
	TArray<uint8> DeploymentAfterDuplicateRollback;
	TArray<uint8> FirstPlayAfterDuplicateRollback;
	TestTrue(TEXT("Reads deployment after duplicate rollback with both chains"), ReadBytes(Cleanup.DeploymentPath,
		         DeploymentAfterDuplicateRollback));
	TestTrue(TEXT("Reads first-play after duplicate rollback with both chains"), ReadBytes(Cleanup.FirstPlayPath,
		         FirstPlayAfterDuplicateRollback));
	TestEqual(TEXT("Duplicate rollback preserves deployment bytes with both chains"), DeploymentAfterDuplicateRollback,
	          DeploymentBeforeDuplicateRollback);
	TestEqual(TEXT("Duplicate rollback preserves first-play bytes with both chains"), FirstPlayAfterDuplicateRollback,
	          FirstPlayBeforeDuplicateRollback);
	TArray<uint8> OriginalSourceAfterDuplicateRollback;
	TArray<uint8> DuplicateSourceAfterDuplicateRollback;
	TestTrue(TEXT("Reads original managed source after duplicate rollback"), ReadBytes(RenameResult.SourcePath,
		         OriginalSourceAfterDuplicateRollback));
	TestTrue(TEXT("Reads duplicate managed source after duplicate rollback"), ReadBytes(DuplicateResult.SourcePath,
		         DuplicateSourceAfterDuplicateRollback));
	TestEqual(TEXT("Duplicate rollback preserves original managed source bytes"), OriginalSourceAfterDuplicateRollback,
	          OriginalSourceBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves duplicate managed source bytes"), DuplicateSourceAfterDuplicateRollback,
		DuplicateSourceBeforeDuplicateRollback);
	TestEqual(TEXT("Duplicate rollback preserves original base dirty flag"), BaseBlueprint->GetOutermost()->IsDirty(),
	          OriginalBaseDirtyBeforeDuplicateRollback);
	TestEqual(TEXT("Duplicate rollback preserves original child dirty flag"), ChildBlueprint->GetOutermost()->IsDirty(),
	          OriginalChildDirtyBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves original grandchild dirty flag"),
		GrandchildBlueprint->GetOutermost()->IsDirty(), OriginalGrandchildDirtyBeforeDuplicateRollback);
	TestEqual(TEXT("Duplicate rollback preserves original sidecar dirty flag"), ScriptAsset->GetOutermost()->IsDirty(),
	          OriginalAssetDirtyBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves duplicate base dirty flag"),
		DuplicateResult.Blueprint->GetOutermost()->IsDirty(), DuplicateBaseDirtyBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves duplicate child dirty flag"),
		DuplicateChildBlueprint->GetOutermost()->IsDirty(), DuplicateChildDirtyBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves duplicate grandchild dirty flag"),
		DuplicateGrandchildBlueprint->GetOutermost()->IsDirty(), DuplicateGrandchildDirtyBeforeDuplicateRollback);
	TestEqual(
		TEXT("Duplicate rollback preserves duplicate sidecar dirty flag"),
		DuplicateResult.ScriptAsset->GetOutermost()->IsDirty(), DuplicateAssetDirtyBeforeDuplicateRollback);
	const FString DuplicateChildPath = DuplicateChildBlueprint->GetPathName();
	const FString DuplicateGrandchildPath = DuplicateGrandchildBlueprint->GetPathName();
	TestTrue(
		TEXT("Saves duplicate descendants before delete guard"),
		SavePackage(*this, DuplicateResult.Blueprint->GetOutermost(), DuplicateResult.Blueprint, DuplicatePackage,
		            TEXT("duplicate base package")) &&
		SavePackage(*this, DuplicateChildOuter, DuplicateChildBlueprint, DuplicateChildPackage,
		            TEXT("duplicate child package")) && SavePackage(*this, DuplicateGrandchildOuter,
		                                                            DuplicateGrandchildBlueprint,
		                                                            DuplicateGrandchildPackage,
		                                                            TEXT("duplicate grandchild package")) &&
		SavePackage(*this, DuplicateResult.ScriptAsset->GetOutermost(), DuplicateResult.ScriptAsset, DuplicateSidecar,
		            TEXT("duplicate sidecar package")));
	Registry.ScanModifiedAssetFiles({
		RenamedBaseFilename, RenamedSidecarFilename, ChildFilename, GrandchildFilename,
		FPackageName::LongPackageNameToFilename(DuplicatePackage, FPackageName::GetAssetPackageExtension()),
		FPackageName::LongPackageNameToFilename(DuplicateChildPackage, FPackageName::GetAssetPackageExtension()),
		FPackageName::LongPackageNameToFilename(DuplicateGrandchildPackage, FPackageName::GetAssetPackageExtension())
	});
	const TWeakObjectPtr<UBlueprint> WeakDuplicateBase = DuplicateResult.Blueprint;
	const TWeakObjectPtr<UBlueprint> WeakDuplicateChild = DuplicateChildBlueprint;
	const TWeakObjectPtr<UBlueprint> WeakDuplicateGrandchild = DuplicateGrandchildBlueprint;
	const TWeakObjectPtr<UFinareAsset> WeakDuplicateAsset = DuplicateResult.ScriptAsset;
	TArray<UPackage*> DuplicatePackages{
		DuplicateGrandchildBlueprint->GetOutermost(),
		DuplicateChildBlueprint->GetOutermost(),
		DuplicateResult.Blueprint->GetOutermost(),
		DuplicateResult.ScriptAsset->GetOutermost()
	};
	for (UPackage* Package : DuplicatePackages)
	{
		Package->SetDirtyFlag(false);
	}
	TestTrue(
		TEXT("Unloads duplicate base, descendants, and sidecar"), UPackageTools::UnloadPackages(DuplicatePackages));
	CollectGarbage(RF_NoFlags);
	TestTrue(TEXT("Duplicate unload invalidates all old chain objects"),
	         !WeakDuplicateBase.IsValid() && !WeakDuplicateChild.IsValid() &&
	         !WeakDuplicateGrandchild.IsValid() && !WeakDuplicateAsset.IsValid());
	DuplicateResult.Blueprint = LoadObject<UBlueprint>(nullptr, *DuplicateBasePath);
	DuplicateChildBlueprint = LoadObject<UBlueprint>(nullptr, *DuplicateChildPath);
	DuplicateGrandchildBlueprint = LoadObject<UBlueprint>(nullptr, *DuplicateGrandchildPath);
	DuplicateResult.ScriptAsset = LoadObject<UFinareAsset>(nullptr, *DuplicateAssetPath);
	TestNotNull(TEXT("Reloads duplicate base after save/unload"), DuplicateResult.Blueprint);
	TestNotNull(TEXT("Reloads duplicate child after save/unload"), DuplicateChildBlueprint);
	TestNotNull(TEXT("Reloads duplicate grandchild after save/unload"), DuplicateGrandchildBlueprint);
	TestNotNull(TEXT("Reloads duplicate sidecar after save/unload"), DuplicateResult.ScriptAsset);
	if (DuplicateResult.Blueprint == nullptr || DuplicateChildBlueprint == nullptr ||
		DuplicateGrandchildBlueprint == nullptr || DuplicateResult.ScriptAsset == nullptr)
	{
		return false;
	}
	TestTrue(TEXT("Reloaded duplicate chain keeps independent identity, defaults, and component ownership"),
	         CheckChain(*this, DuplicateResult.Blueprint, DuplicateChildBlueprint, DuplicateGrandchildBlueprint,
	                    DuplicateResult.ScriptAsset, TEXT("Reloaded duplicate chain")) &&
	         CheckFullChainState(*this, DuplicateResult.Blueprint, DuplicateChildBlueprint,
	                             DuplicateGrandchildBlueprint, true, TEXT("Reloaded duplicate chain")));
	CheckPersistentIdentity(*this, DuplicateResult.Blueprint, DuplicateResult.ScriptAsset, DuplicateClassId,
	                        DuplicateGeneratedClassPath, DuplicateAssetPath, TEXT("Reloaded duplicate identity"));

	// The duplicate unload/reload GC pass can collect the separately loaded
	// original chain as well. Resolve those exact saved paths again before
	// using their generated classes for native instance checks.
	BaseBlueprint = LoadObject<UBlueprint>(nullptr, *RenamedBasePath);
	ChildBlueprint = LoadObject<UBlueprint>(nullptr, *ChildPath);
	GrandchildBlueprint = LoadObject<UBlueprint>(nullptr, *GrandchildPath);
	ScriptAsset = LoadObject<UFinareAsset>(nullptr, *RenamedAssetPath);
	if (!TestNotNull(TEXT("Re-resolves original renamed base before spawning"), BaseBlueprint) ||
		!TestNotNull(TEXT("Re-resolves original renamed child before spawning"), ChildBlueprint) ||
		!TestNotNull(TEXT("Re-resolves original renamed grandchild before spawning"), GrandchildBlueprint) ||
		!TestNotNull(TEXT("Re-resolves original renamed sidecar before spawning"), ScriptAsset) ||
		!TestNotNull(
			TEXT("Re-resolves original child generated class before spawning"), ChildBlueprint->GeneratedClass.Get()) ||
		!TestNotNull(
			TEXT("Re-resolves duplicate child generated class before spawning"),
			DuplicateChildBlueprint->GeneratedClass.Get()))
	{
		return false;
	}
	CheckChain(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, ScriptAsset,
	           TEXT("Re-resolved original chain before spawning"), true, true, false, false, false, false);
	CheckFullChainState(*this, BaseBlueprint, ChildBlueprint, GrandchildBlueprint, false,
	                    TEXT("Re-resolved original chain before spawning"));
	CheckVariableGuids(*this, BaseBlueprint, StateVariableNames, BaseVariableGuids, TEXT("Re-resolved original chain"));

	UWorld* SpawnWorld = UWorld::CreateWorld(EWorldType::Game, false);
	if (TestNotNull(TEXT("Creates a transient world for native inherited Actor spawn checks"), SpawnWorld))
	{
		SpawnWorld->AddToRoot();
		FWorldContext* SpawnContext = GEngine != nullptr
			                              ? &GEngine->CreateNewWorldContext(EWorldType::Game)
			                              : nullptr;
		if (SpawnContext != nullptr)
		{
			SpawnContext->SetCurrentWorld(SpawnWorld);
		}
		AFinareActor* OriginalActor = Cast<AFinareActor>(SpawnWorld->SpawnActor(ChildBlueprint->GeneratedClass));
		AFinareActor* DuplicateActor = Cast<AFinareActor>(
			SpawnWorld->SpawnActor(DuplicateChildBlueprint->GeneratedClass));
		const bool bOriginalSpawnValid = CheckSpawnedActor(
			*this,
			OriginalActor,
			ScriptAsset,
			GetComponent(ChildBlueprint),
			false,
			false,
			77,
			{20, 21},
			{30, 40},
			{{5, 6}, {7, 8}},
			TEXT("Original child Actor instance"));
		const bool bDuplicateSpawnValid = CheckSpawnedActor(
			*this,
			DuplicateActor,
			DuplicateResult.ScriptAsset,
			GetComponent(DuplicateChildBlueprint),
			true,
			true,
			7,
			{2, 4},
			{6, 8},
			{{11, 13}, {17, 19}},
			TEXT("Duplicate child Actor instance"));
		TestTrue(
			TEXT("Spawns original and duplicate ordinary Actor branches"), bOriginalSpawnValid && bDuplicateSpawnValid);
		if (OriginalActor != nullptr)
		{
			OriginalActor->Destroy();
		}
		if (DuplicateActor != nullptr)
		{
			DuplicateActor->Destroy();
		}
		if (SpawnContext != nullptr && GEngine != nullptr)
		{
			GEngine->DestroyWorldContext(SpawnWorld);
		}
		SpawnWorld->RemoveFromRoot();
		SpawnWorld->DestroyWorld(false);
	}
	FFinareClassAssetOperationResult DeleteGuard;
	TestFalse(
		TEXT("Delete rejects a duplicated class with ordinary descendants"),
		FFinareClassAssetOperations::Delete(DuplicateResult.ScriptAsset, DeleteGuard));
	TestTrue(
		TEXT("Delete guard reports ordinary derived referencers"),
		DeleteGuard.Error.Contains(TEXT("referenc")) || DeleteGuard.Error.Contains(TEXT("derived")));
	TestTrue(TEXT("Delete guard preserves duplicate chain"), CheckChain(*this, DuplicateResult.Blueprint,
	                                                                    FindBlueprint(DuplicateChildPath),
	                                                                    FindBlueprint(DuplicateGrandchildPath),
	                                                                    DuplicateResult.ScriptAsset,
	                                                                    TEXT("Delete guard")));
	return true;
}

#endif
