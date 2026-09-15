#include "FinareBlueprintClassMaterializer.h"
#include "FinareSoftObjectPath.h"

#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "EdGraph/EdGraphPin.h"
#include "EdGraphSchema_K2.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "Engine/InheritableComponentHandler.h"
#include "Engine/SCS_Node.h"
#include "Engine/SimpleConstructionScript.h"
#include "Editor/TransBuffer.h"
#include "Editor/Transactor.h"
#include "GameFramework/Actor.h"
#include "Components/ActorComponent.h"
#include "Interfaces/IPluginManager.h"
#include "K2Node_FunctionEntry.h"
#include "K2Node_FunctionResult.h"
#include "K2Node_Event.h"
#include "K2Node_CallParentFunction.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformFileManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/ArchiveReplaceObjectRef.h"
#include "Serialization/ArchiveUObject.h"
#include "Serialization/StructuredArchive.h"
#include "FinareAsset.h"
#include "FinareAuthoringActivationScope.h"
#include "FinareClassBinding.h"
#include "FinareBlueprint.h"
#include "FinareActor.h"
#include "FinareClassBases.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "Misc/SecureHash.h"
#include "FinareDeclarationMaterializer.h"
#include "FinareClassOperationJournal.h"
#include "FinareAuthoringTransBuffer.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/EnumProperty.h"
#include "UObject/GCObject.h"
#include "UObject/ObjectKey.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/StrongObjectPtr.h"
#include "UObject/UObjectHash.h"
#include "UObject/UObjectArray.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::BlueprintClass::Private
{
	namespace ClassOperationJournal = UE::Finare::ClassOperationJournal;
	constexpr TCHAR LegacySchemaVersion[] = TEXT("unifiedscript.script-asset/v0.1");
	constexpr TCHAR OverrideSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.2");
	constexpr TCHAR ClassBindingSchemaVersion[] = TEXT("unifiedscript.script-asset/v0.3");
	constexpr TCHAR DefaultCategory[] = TEXT("Finare");

	const FName ManagedStateKey(TEXT("UnifiedScript.ManagedState"));
	const FName StateIdKey(TEXT("UnifiedScript.StateId"));
	const FName TypeMetadataNameKey(TEXT("UnifiedScript.TypeMetadataName"));
	const FName DeclarationDefaultKey(TEXT("UnifiedScript.DeclarationDefault"));
	const FName ManagedFunctionKey(TEXT("UnifiedScript.ManagedFunction"));
	const FName FunctionIdKey(TEXT("UnifiedScript.FunctionId"));
	const FName ManagedFunctionNameKey(TEXT("UnifiedScript.ManagedName"));
	const FName BlueprintEventKey(TEXT("UnifiedScript.BlueprintEvent"));
	const FName BlueprintEventSuperModeKey(TEXT("UnifiedScript.SuperMode"));
	const FName BlueprintOverrideDeclaringTypePathKey(TEXT("UnifiedScript.BlueprintOverrideDeclaringTypePath"));
	const FName BlueprintOverrideFunctionNameKey(TEXT("UnifiedScript.BlueprintOverrideFunctionName"));
	const FName WorldContextObjectName(TEXT("WorldContextObject"));

	struct FSnapshotObjectIdentity
	{
		FObjectKey Key;
		FString CapturedObjectPath;
		TStrongObjectPtr<UBlueprint> OwnerBlueprint;
		TStrongObjectPtr<UClass> ExpectedClass;
		bool bClassDefaultObject = false;

		void Capture(UObject* Object)
		{
			Key = FObjectKey(Object);
			CapturedObjectPath = Object != nullptr ? Object->GetPathName() : FString();
			ExpectedClass.Reset(Object != nullptr ? Object->GetClass() : nullptr);
			OwnerBlueprint.Reset();
			bClassDefaultObject = false;
			if (Object == nullptr || Object->GetClass() == nullptr)
			{
				return;
			}
			UBlueprint* GeneratedBy = Cast<UBlueprint>(Object->GetClass()->ClassGeneratedBy);
			if (GeneratedBy != nullptr && GeneratedBy->GeneratedClass == Object->GetClass())
			{
				OwnerBlueprint.Reset(GeneratedBy);
				bClassDefaultObject = Object->HasAnyFlags(RF_ClassDefaultObject);
			}
		}
	};

	bool ReadJsonNumberToken(const TSharedPtr<FJsonValue>& Value, FString& OutToken)
	{
		return Value.IsValid() && Value->Type == EJson::Number &&
			Value->TryGetString(OutToken) && !OutToken.IsEmpty();
	}

	bool IsJsonIntegerToken(const FString& Token)
	{
		int32 Start = Token.StartsWith(TEXT("-")) ? 1 : 0;
		if (Start == Token.Len() || (Token[Start] == TEXT('0') && Token.Len() > Start + 1))
		{
			return false;
		}
		for (int32 Index = Start; Index < Token.Len(); ++Index)
		{
			if (Token[Index] < TEXT('0') || Token[Index] > TEXT('9'))
			{
				return false;
			}
		}
		return true;
	}

	bool ParseJsonInt64(const TSharedPtr<FJsonValue>& Value, int64& OutValue)
	{
		FString Token;
		if (!ReadJsonNumberToken(Value, Token) || !IsJsonIntegerToken(Token))
		{
			return false;
		}
		const bool bNegative = Token.StartsWith(TEXT("-"));
		const int32 Start = bNegative ? 1 : 0;
		const uint64 Limit = bNegative ? static_cast<uint64>(MAX_int64) + 1ull : static_cast<uint64>(MAX_int64);
		uint64 Magnitude = 0;
		for (int32 Index = Start; Index < Token.Len(); ++Index)
		{
			const uint64 Digit = static_cast<uint64>(Token[Index] - TEXT('0'));
			if (Magnitude > (Limit - Digit) / 10ull)
			{
				return false;
			}
			Magnitude = Magnitude * 10ull + Digit;
		}
		if (bNegative && Magnitude == static_cast<uint64>(MAX_int64) + 1ull)
		{
			OutValue = MIN_int64;
		}
		else if (bNegative)
		{
			OutValue = -static_cast<int64>(Magnitude);
		}
		else
		{
			OutValue = static_cast<int64>(Magnitude);
		}
		return true;
	}

	bool ParseJsonDouble(const TSharedPtr<FJsonValue>& Value, double& OutValue)
	{
		FString Token;
		return ReadJsonNumberToken(Value, Token) && LexTryParseString(OutValue, *Token) &&
			FMath::IsFinite(OutValue);
	}

	struct FStateDeclaration
	{
		FGuid Id;
		FString SemanticId;
		FName Name;
		FString TypeMetadataName;
		FEdGraphPinType PinType;
		FString DefaultValue;
		FString Category;
		uint64 PropertyFlags = 0;
		FName RepNotifyFunc;
		ELifetimeCondition ReplicationCondition = COND_None;
	};

	struct FPlannedState
	{
		FStateDeclaration Declaration;
		bool bExisting = false;
		FName ExistingName;
		FString AppliedDefault;
		bool bPreserveDefaultOverride = false;
	};

	enum class EFunctionValueTypeKind : uint8
	{
		None,
		Enum,
		Struct,
		Array,
		Set,
		Map,
		Object,
		Interface,
	};

	struct FFunctionValueTypeDeclaration
	{
		EFunctionValueTypeKind Kind = EFunctionValueTypeKind::None;
		FString UnrealTypePath;
		FString UnrealNativeName;
		TArray<TArray<FName>> StructFieldPaths;
		TArray<FString> ArgumentMetadataNames;
		TArray<FFunctionValueTypeDeclaration> TypeArguments;
	};

	struct FFunctionParameterDeclaration
	{
		FGuid Id;
		FName Name;
		/** Native inherited property name; empty for ordinary managed functions. */
		FName NativeName;
		FString TypeMetadataName;
		FEdGraphPinType PinType;
		FFunctionValueTypeDeclaration ValueType;
		EFinareManagedParameterDirection Direction =
			EFinareManagedParameterDirection::Input;
	};

	struct FFunctionDeclaration
	{
		FGuid Id;
		FString SemanticId;
		FName ManagedName;
		FString ManagedNameText;
		FName InternalName;
		FString ReturnTypeMetadataName;
		FEdGraphPinType ReturnPinType;
		FFunctionValueTypeDeclaration ReturnValueType;
		bool bHasReturnValue = false;
		bool bIsStatic = false;
		bool bBlueprintEvent = false;
		bool bBlueprintOverride = false;
		FString BlueprintOverrideDeclaringTypePath;
		FString BlueprintOverrideFunctionNameText;
		FName BlueprintOverrideFunctionName;
		bool bInterfaceFunction = false;
		EFinareRpcTarget RpcTarget = EFinareRpcTarget::None;
		bool bReliable = false;
		FName RepNotifyProperty;
		TArray<FString> InterfacePaths;
		EFinareBlueprintEventSuperMode BlueprintEventSuperMode =
			EFinareBlueprintEventSuperMode::Explicit;
		TArray<FFunctionParameterDeclaration> Parameters;
	};

	struct FLifecycleDeclaration
	{
		EFinareManagedLifecycleHookKind Kind =
			EFinareManagedLifecycleHookKind::BeginPlay;
		FName ManagedName;
		FGuid Id;
	};

	struct FInputActionDeclaration
	{
		FName ManagedName;
		FGuid BindingId;
		FString ActionPath;
		EFinareManagedInputTriggerEvent TriggerEvent =
			EFinareManagedInputTriggerEvent::Triggered;
		EFinareManagedInputValueType ValueType =
			EFinareManagedInputValueType::Boolean;
		bool bConsumeInput = false;
	};

	struct FDelegateBindingDeclaration
	{
		FName ManagedName;
		FGuid BindingId;
		FString SourceTypePath;
		FString EventPath;
		EFinareManagedDelegateSource Source =
			EFinareManagedDelegateSource::Target;
		FString SourceMember;
	};

	struct FInterfaceFunctionDeclaration
	{
		FString InterfacePath;
		FName FunctionName;
		FName ManagedName;
		FGuid BindingId;
		FGuid FunctionId;
	};

	struct FPlannedFunction
	{
		FFunctionDeclaration Declaration;
		bool bExisting = false;
		bool bNeedsUpdate = false;
	};

	struct FSyncPlan
	{
		TArray<FPlannedState> States;
		TArray<FName> StaleGeneratedVariables;
		TArray<FPlannedFunction> Functions;
		TArray<FGuid> StaleGeneratedFunctionIds;
		TMap<EFinareManagedLifecycleHookKind, FFinareManagedLifecycleBinding>
		LifecycleBindings;
		TArray<FFinareManagedInputActionBinding> InputBindings;
		TArray<FFinareManagedDelegateBinding> DelegateBindings;
		TArray<FFinareManagedInterfaceFunctionBinding> InterfaceBindings;
		TArray<FString> InterfacePathsToRemove;
		TArray<FString> InterfacePathsToAdd;
		bool bLifecycleBindingsChanged = false;
		bool bGameplayBindingsChanged = false;
	};

	struct FParentChangePlan
	{
		UClass* CurrentParent = nullptr;
		UClass* RequestedParent = nullptr;
		bool bChanged = false;
	};

	struct FParentDefaultOverrideSnapshot
	{
		UObject* OldObject = nullptr;
		TStrongObjectPtr<UObject> OldObjectRoot;
		TStrongObjectPtr<UObject> OldArchetypeRoot;
		FSnapshotObjectIdentity ObjectIdentity;
		bool bFollowsArchetype = false;
		FName PropertyName;
		FInstancedPropertyBag Value;
	};

	struct FParentDefaultSnapshotReferenceGuard final : FGCObject
	{
		TArray<FParentDefaultOverrideSnapshot>& Snapshots;

		explicit FParentDefaultSnapshotReferenceGuard(
			TArray<FParentDefaultOverrideSnapshot>& InSnapshots)
			: Snapshots(InSnapshots)
		{
		}

		virtual void AddReferencedObjects(FReferenceCollector& Collector) override
		{
			for (FParentDefaultOverrideSnapshot& Snapshot : Snapshots)
			{
				Snapshot.Value.AddStructReferencedObjects(Collector);
			}
		}

		virtual FString GetReferencerName() const override
		{
			return TEXT("FinareParentDefaultSnapshots");
		}
	};

	struct FManagedStateValueSnapshot
	{
		UObject* OldObject = nullptr;
		TStrongObjectPtr<UObject> OldObjectRoot;
		TStrongObjectPtr<UObject> OldArchetypeRoot;
		FSnapshotObjectIdentity ObjectIdentity;
		bool bFollowsArchetype = false;
		bool bRestoreOnSuccess = true;
		FGuid StateId;
		FName OldPropertyName;
		FName NewPropertyName;
		FInstancedPropertyBag Value;
	};

	struct FManagedStateSnapshotReferenceGuard final : FGCObject
	{
		TArray<FManagedStateValueSnapshot>& Snapshots;

		explicit FManagedStateSnapshotReferenceGuard(
			TArray<FManagedStateValueSnapshot>& InSnapshots)
			: Snapshots(InSnapshots)
		{
		}

		virtual void AddReferencedObjects(FReferenceCollector& Collector) override
		{
			for (FManagedStateValueSnapshot& Snapshot : Snapshots)
			{
				Snapshot.Value.AddStructReferencedObjects(Collector);
			}
		}

		virtual FString GetReferencerName() const override
		{
			return TEXT("FinareManagedStateSnapshots");
		}
	};

	const FName CollectionSnapshotValueName(TEXT("FinareSnapshotValue"));

	struct FCollectionReplacementTarget
	{
		FObjectKey Identity;
		// Reinstance can publish a worldless destination without another rooted
		// owner.  Keep each exact destination alive for this operation while the
		// stable identity chain is resolved; retired intermediates still resolve
		// through their later key edge instead of becoming a terminal value.
		TStrongObjectPtr<UObject> DestinationRoot;
		bool bExplicitNull = false;
	};

	using FCollectionReplacementMap = TMap<FObjectKey, FCollectionReplacementTarget>;

	UObject* ResolveReplacedObject(
		const FObjectKey& OldObjectKey,
		const FCollectionReplacementMap& ReplacedObjects,
		bool& bFoundReplacement,
		bool* bExplicitNull = nullptr);

	UObject* ResolveSnapshotObject(
		FSnapshotObjectIdentity& Identity,
		UObject* OldObjectRoot,
		const FCollectionReplacementMap& ReplacedObjects,
		bool& bFoundReplacement,
		bool* bExplicitNull = nullptr);

	TMap<UObject*, UObject*> FlattenReplacedObjects(
		const FCollectionReplacementMap& ReplacedObjects);

	bool ValidateSnapshotReplacementReferences(
		const FProperty* SnapshotProperty,
		void* SnapshotContainer,
		const FCollectionReplacementMap& ReplacedObjects,
		const FString& SnapshotDescription,
		FString& OutError);

	struct FScriptComponentOwnerSnapshot
	{
		struct FEditablePropertySnapshot
		{
			FName PropertyName = NAME_None;
			bool bFollowsArchetype = false;
		};

		// These snapshots live on the stack while Blueprint compilation can run
		// GC.  TObjectPtr fields in a local TArray are not GC roots by themselves.
		TStrongObjectPtr<UBlueprint> OwnerBlueprint;
		bool bHadComponent = false;
		TStrongObjectPtr<UFinareComponent> ComponentRoot;
		TStrongObjectPtr<UClass> ComponentClass;
		TStrongObjectPtr<UFinareAsset> ScriptAsset;
		FString ScriptAssetPath;
		TArray<FGuid> ScriptStateOverrideIds;
		bool bScriptStateOverrideBaselineInitialized = false;
		TArray<FEditablePropertySnapshot> EditableProperties;
		FInstancedPropertyBag EditableValues;
	};

	struct FScriptComponentSnapshotReferenceGuard final : FGCObject
	{
		TArray<FScriptComponentOwnerSnapshot>& Snapshots;

		explicit FScriptComponentSnapshotReferenceGuard(
			TArray<FScriptComponentOwnerSnapshot>& InSnapshots)
			: Snapshots(InSnapshots)
		{
		}

		virtual void AddReferencedObjects(FReferenceCollector& Collector) override
		{
			for (FScriptComponentOwnerSnapshot& Snapshot : Snapshots)
			{
				Snapshot.EditableValues.AddStructReferencedObjects(Collector);
			}
		}

		virtual FString GetReferencerName() const override
		{
			return TEXT("FinareScriptComponentSnapshots");
		}
	};

	struct FManagedGeneratedClassSnapshot
	{
		bool bCaptured = false;
		TMap<FName, FFinareManagedFunctionBinding> ManagedFunctionBindings;
		TMap<EFinareManagedLifecycleHookKind, FFinareManagedLifecycleBinding>
		ManagedLifecycleBindings;
		TArray<FFinareManagedInputActionBinding> ManagedInputActionBindings;
		TArray<FFinareManagedDelegateBinding> ManagedDelegateBindings;
		TArray<FFinareManagedInterfaceFunctionBinding> ManagedInterfaceBindings;
		TStrongObjectPtr<UFinareAsset> ManagedScriptAsset;
		bool bHasManagedConstruction = false;
		FString ManagedConstructionProgramHash;
		FString ManagedConstructionError;
		TSet<FName> ManagedConstructionProperties;
		TMap<FName, FString> ManagedConstructionDefaults;

		void Capture(const UFinareBlueprintGeneratedClass& GeneratedClass)
		{
			bCaptured = true;
			ManagedFunctionBindings = GeneratedClass.ManagedFunctionBindings;
			ManagedLifecycleBindings = GeneratedClass.ManagedLifecycleBindings;
			ManagedInputActionBindings = GeneratedClass.ManagedInputActionBindings;
			ManagedDelegateBindings = GeneratedClass.ManagedDelegateBindings;
			ManagedInterfaceBindings = GeneratedClass.ManagedInterfaceBindings;
			ManagedScriptAsset.Reset(GeneratedClass.ManagedScriptAsset.Get());
			bHasManagedConstruction = GeneratedClass.bHasManagedConstruction;
			ManagedConstructionProgramHash = GeneratedClass.ManagedConstructionProgramHash;
			ManagedConstructionError = GeneratedClass.ManagedConstructionError;
			ManagedConstructionProperties = GeneratedClass.ManagedConstructionProperties;
			ManagedConstructionDefaults = GeneratedClass.ManagedConstructionDefaults;
		}

		bool RestoreTo(UBlueprint& Blueprint, FString& OutError) const
		{
			if (!bCaptured)
			{
				OutError.Reset();
				return true;
			}
			UFinareBlueprintGeneratedClass* GeneratedClass =
				Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
			if (GeneratedClass == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Blueprint '%s' lost its Finare generated class while restoring the authoring transaction."),
					*Blueprint.GetPathName());
				return false;
			}
			GeneratedClass->ManagedFunctionBindings = ManagedFunctionBindings;
			GeneratedClass->ManagedLifecycleBindings = ManagedLifecycleBindings;
			GeneratedClass->ManagedInputActionBindings = ManagedInputActionBindings;
			GeneratedClass->ManagedDelegateBindings = ManagedDelegateBindings;
			GeneratedClass->ManagedInterfaceBindings = ManagedInterfaceBindings;
			GeneratedClass->ManagedScriptAsset = ManagedScriptAsset.Get();
			GeneratedClass->bHasManagedConstruction = bHasManagedConstruction;
			GeneratedClass->ManagedConstructionProgramHash = ManagedConstructionProgramHash;
			GeneratedClass->ManagedConstructionError = ManagedConstructionError;
			GeneratedClass->ManagedConstructionProperties = ManagedConstructionProperties;
			GeneratedClass->ManagedConstructionDefaults = ManagedConstructionDefaults;
			OutError.Reset();
			return true;
		}
	};

	struct FBlueprintAuthoringSnapshot
	{
		TStrongObjectPtr<UBlueprint> Blueprint;
		TStrongObjectPtr<UPackage> Package;
		bool bPackageDirty = false;
		TStrongObjectPtr<UClass> OriginalParent;
		TStrongObjectPtr<UClass> OriginalGeneratedClass;
		TStrongObjectPtr<UClass> OriginalSkeletonGeneratedClass;
		FManagedGeneratedClassSnapshot ManagedGeneratedClass;

		void Capture(UBlueprint& InBlueprint)
		{
			Blueprint.Reset(&InBlueprint);
			Package.Reset(InBlueprint.GetOutermost());
			bPackageDirty = Package.IsValid() && Package->IsDirty();
			OriginalParent.Reset(InBlueprint.ParentClass.Get());
			OriginalGeneratedClass.Reset(InBlueprint.GeneratedClass.Get());
			OriginalSkeletonGeneratedClass.Reset(InBlueprint.SkeletonGeneratedClass.Get());
			if (const UFinareBlueprintGeneratedClass* GeneratedClass =
				Cast<UFinareBlueprintGeneratedClass>(InBlueprint.GeneratedClass))
			{
				ManagedGeneratedClass.Capture(*GeneratedClass);
			}
		}

		void RestorePackageDirtyFlag() const
		{
			if (Package.IsValid())
			{
				Package->SetDirtyFlag(bPackageDirty);
			}
		}
	};

	bool IsManagedState(const FBPVariableDescription& Variable);

	bool ArePropertiesExactlyCompatible(
		const FProperty* Left,
		const FProperty* Right);

	void CopyTypedPropertyValue(
		const FProperty* DestinationProperty,
		void* DestinationContainer,
		const FProperty* SourceProperty,
		const void* SourceContainer);

	bool AreTypedPropertyValuesIdentical(
		const FProperty* LeftProperty,
		const void* LeftContainer,
		const FProperty* RightProperty,
		const void* RightContainer);

	bool CaptureParentDefaultOverrides(
		const UBlueprint& Blueprint,
		UClass* OriginalParent,
		UClass* RequestedParent,
		TArray<FParentDefaultOverrideSnapshot>& OutSnapshots,
		FString& OutError)
	{
		OutSnapshots.Reset();
		if (Blueprint.GeneratedClass == nullptr || OriginalParent == nullptr ||
			RequestedParent == nullptr)
		{
			OutError.Reset();
			return true;
		}

		TArray<UObject*> Objects;
		GetObjectsOfClass(Blueprint.GeneratedClass, Objects, true, RF_MirroredGarbage);
		for (UObject* Object : Objects)
		{
			if (Object == nullptr ||
				Object->HasAnyFlags(RF_BeginDestroyed | RF_FinishDestroyed | RF_MirroredGarbage) ||
				Object->GetClass() == nullptr ||
				!Object->GetClass()->IsChildOf(Blueprint.GeneratedClass))
			{
				continue;
			}
			UBlueprint* GeneratedBy = Cast<UBlueprint>(Object->GetClass()->ClassGeneratedBy);
			if (GeneratedBy == nullptr || GeneratedBy->GeneratedClass != Object->GetClass())
			{
				continue;
			}
			const bool bRootClassDefaultObject =
				Object == Blueprint.GeneratedClass->GetDefaultObject(false);
			const bool bDerivedClassDefaultObject =
				Object->HasAnyFlags(RF_ClassDefaultObject) && Object->GetClass() != Blueprint.GeneratedClass;
			if (!bRootClassDefaultObject && !bDerivedClassDefaultObject && Object->IsTemplate())
			{
				continue;
			}
			UObject* OldArchetype = Object->GetArchetype();
			if (OldArchetype == nullptr || OldArchetype == Object ||
				OldArchetype->GetClass() == nullptr)
			{
				continue;
			}

			for (TFieldIterator<FProperty> It(
				     OriginalParent,
				     EFieldIteratorFlags::IncludeSuper);
			     It;
			     ++It)
			{
				FProperty* OldProperty = *It;
				if (OldProperty == nullptr ||
					OldProperty->HasAnyPropertyFlags(CPF_Transient | CPF_EditConst) ||
					!OldProperty->HasAnyPropertyFlags(CPF_Edit))
				{
					continue;
				}
				FProperty* ObjectProperty = FindFProperty<FProperty>(
					Object->GetClass(),
					OldProperty->GetFName());
				FProperty* ArchetypeProperty = FindFProperty<FProperty>(
					OldArchetype->GetClass(),
					OldProperty->GetFName());
				if (ObjectProperty == nullptr || ArchetypeProperty == nullptr ||
					!ArePropertiesExactlyCompatible(ObjectProperty, ArchetypeProperty) ||
					!ArePropertiesExactlyCompatible(ObjectProperty, OldProperty))
				{
					continue;
				}

				const bool bFollowsArchetype = AreTypedPropertyValuesIdentical(
					ObjectProperty,
					Object,
					ArchetypeProperty,
					OldArchetype);
				FProperty* NewProperty = FindFProperty<FProperty>(
					RequestedParent,
					OldProperty->GetFName());
				if (NewProperty == nullptr ||
					!ArePropertiesExactlyCompatible(OldProperty, NewProperty))
				{
					if (!bFollowsArchetype)
					{
						OutError = FString::Printf(
							TEXT(
								"Inherited property '%s' has an explicit value on '%s' but the requested parent removed or changed its reflected type."),
							*OldProperty->GetName(),
							*Object->GetPathName());
						return false;
					}
					continue;
				}
				FParentDefaultOverrideSnapshot& Snapshot = OutSnapshots.AddDefaulted_GetRef();
				Snapshot.OldObject = Object;
				Snapshot.OldObjectRoot = TStrongObjectPtr<UObject>(Object);
				Snapshot.OldArchetypeRoot = TStrongObjectPtr<UObject>(OldArchetype);
				Snapshot.ObjectIdentity.Capture(Object);
				Snapshot.bFollowsArchetype = bFollowsArchetype;
				Snapshot.PropertyName = OldProperty->GetFName();
				if (bFollowsArchetype)
				{
					continue;
				}
				if (Snapshot.Value.AddProperty(CollectionSnapshotValueName, OldProperty) !=
					EPropertyBagAlterationResult::Success)
				{
					OutError = FString::Printf(
						TEXT("Inherited property '%s' on '%s' cannot be represented by a typed preservation snapshot."),
						*OldProperty->GetName(),
						*Object->GetPathName());
					return false;
				}
				const FPropertyBagPropertyDesc* SnapshotDesc =
					Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
				if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Inherited property '%s' produced an incomplete typed preservation snapshot."),
						*OldProperty->GetName());
					return false;
				}
				CopyTypedPropertyValue(
					SnapshotDesc->CachedProperty,
					Snapshot.Value.GetMutableValue().GetMemory(),
					OldProperty,
					Object);
			}
		}
		OutError.Reset();
		return true;
	}

	bool RestoreParentDefaultOverrides(
		TArray<FParentDefaultOverrideSnapshot>& Snapshots,
		const FCollectionReplacementMap& ReplacedObjects,
		FString& OutError)
	{
		const TMap<UObject*, UObject*> FlattenedReplacedObjects =
			FlattenReplacedObjects(ReplacedObjects);
		const auto ResolveSnapshotTarget =
			[&ReplacedObjects](FParentDefaultOverrideSnapshot& Snapshot, bool& bFoundReplacement)
		{
			UObject* NewObject = ResolveSnapshotObject(
				Snapshot.ObjectIdentity,
				Snapshot.OldObjectRoot.Get(),
				ReplacedObjects,
				bFoundReplacement);
			return NewObject;
		};
		const auto GetArchetypeDepth = [](UObject* Object)
		{
			int32 Depth = 0;
			TSet<UObject*> Visited;
			for (UObject* Current = Object != nullptr ? Object->GetArchetype() : nullptr;
			     Current != nullptr && !Visited.Contains(Current) && Depth < 256;
			     Current = Current->GetArchetype())
			{
				Visited.Add(Current);
				++Depth;
			}
			return Depth;
		};
		TArray<int32> SnapshotOrder;
		SnapshotOrder.Reserve(Snapshots.Num());
		for (int32 Index = 0; Index < Snapshots.Num(); ++Index)
		{
			SnapshotOrder.Add(Index);
		}
		SnapshotOrder.Sort(
			[&Snapshots, &ResolveSnapshotTarget, &GetArchetypeDepth](int32 Left, int32 Right)
			{
				bool bLeftReplaced = false;
				bool bRightReplaced = false;
				const int32 LeftDepth = GetArchetypeDepth(
					ResolveSnapshotTarget(Snapshots[Left], bLeftReplaced));
				const int32 RightDepth = GetArchetypeDepth(
					ResolveSnapshotTarget(Snapshots[Right], bRightReplaced));
				return LeftDepth == RightDepth ? Left < Right : LeftDepth < RightDepth;
			});
		for (const int32 SnapshotIndex : SnapshotOrder)
		{
			FParentDefaultOverrideSnapshot& Snapshot = Snapshots[SnapshotIndex];
			bool bFoundReplacement = false;
			UObject* NewObject = ResolveSnapshotTarget(Snapshot, bFoundReplacement);
			if (NewObject == nullptr ||
				NewObject->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed) ||
				NewObject->GetClass() == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Inherited property '%s' lost its snapshotted owner during Blueprint reinstancing."),
					*Snapshot.PropertyName.ToString());
				return false;
			}
			FProperty* NewProperty = FindFProperty<FProperty>(
				NewObject->GetClass(),
				Snapshot.PropertyName);
			if (NewProperty == nullptr)
			{
				if (Snapshot.bFollowsArchetype)
				{
					continue;
				}
				OutError = FString::Printf(
					TEXT("Inherited property '%s' could not be restored on '%s' after Blueprint reinstancing."),
					*Snapshot.PropertyName.ToString(),
					*NewObject->GetPathName());
				return false;
			}
			UObject* NewArchetype = NewObject->GetArchetype();
			FProperty* NewArchetypeProperty = NewArchetype != nullptr && NewArchetype != NewObject
				                                  ? FindFProperty<FProperty>(
					                                  NewArchetype->GetClass(), Snapshot.PropertyName)
				                                  : nullptr;
			if (NewArchetype == nullptr || NewArchetypeProperty == nullptr ||
				!ArePropertiesExactlyCompatible(NewProperty, NewArchetypeProperty))
			{
				OutError = FString::Printf(
					TEXT("Inherited property '%s' lost its immediate archetype on '%s' after Blueprint reinstancing."),
					*Snapshot.PropertyName.ToString(),
					*NewObject->GetPathName());
				return false;
			}
			void* NewValue = NewProperty->ContainerPtrToValuePtr<void>(NewObject);
			if (Snapshot.bFollowsArchetype)
			{
				CopyTypedPropertyValue(
					NewProperty,
					NewObject,
					NewArchetypeProperty,
					NewArchetype);
			}
			else
			{
				const FPropertyBagPropertyDesc* SnapshotDesc =
					Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
				if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr ||
					!ArePropertiesExactlyCompatible(NewProperty, SnapshotDesc->CachedProperty))
				{
					OutError = FString::Printf(
						TEXT("Inherited property '%s' changed reflected identity while restoring '%s'."),
						*Snapshot.PropertyName.ToString(),
						*NewObject->GetPathName());
					return false;
				}
				void* SnapshotValue = SnapshotDesc->CachedProperty->ContainerPtrToValuePtr<void>(
					Snapshot.Value.GetMutableValue().GetMemory());
				if (!ValidateSnapshotReplacementReferences(
					SnapshotDesc->CachedProperty,
					SnapshotValue,
					ReplacedObjects,
					FString::Printf(
						TEXT("inherited property '%s' on '%s'"),
						*Snapshot.PropertyName.ToString(),
						*NewObject->GetPathName()),
					OutError))
				{
					return false;
				}
				FArchiveReplaceObjectRef<UObject> ReplaceArchive(
					NewObject,
					FlattenedReplacedObjects,
					EArchiveReplaceObjectFlags::DelayStart);
				FStructuredArchiveFromArchive StructuredArchive(ReplaceArchive);
				SnapshotDesc->CachedProperty->SerializeItem(StructuredArchive.GetSlot(), SnapshotValue);
				CopyTypedPropertyValue(
					NewProperty,
					NewObject,
					SnapshotDesc->CachedProperty,
					Snapshot.Value.GetMutableValue().GetMemory());
			}
			if (const FSetProperty* SetProperty = CastField<FSetProperty>(NewProperty))
			{
				FScriptSetHelper SetHelper(SetProperty, NewValue);
				SetHelper.Rehash();
			}
			else if (const FMapProperty* MapProperty = CastField<FMapProperty>(NewProperty))
			{
				FScriptMapHelper MapHelper(MapProperty, NewValue);
				MapHelper.Rehash();
			}
		}
		OutError.Reset();
		return true;
	}

	UFinareComponent* FindManagedScriptComponent(UClass* GeneratedClass)
	{
		if (GeneratedClass == nullptr)
		{
			return nullptr;
		}
		if (AFinareActor* ActorDefaults =
			Cast<AFinareActor>(GeneratedClass->GetDefaultObject()))
		{
			return ActorDefaults->ScriptComponent;
		}
		if (Cast<UFinareComponent>(GeneratedClass->GetDefaultObject()) != nullptr)
		{
			return Cast<UFinareComponent>(GeneratedClass->GetDefaultObject());
		}

		// A managed Actor whose explicit parent is an ordinary AActor owns its
		// carrier through SCS.  The ordinary AActor CDO does not necessarily
		// instantiate SCS components, so use the exact generated SCS template as
		// the authoring owner.  Runtime instances resolve the same template via
		// their archetype.
		UBlueprintGeneratedClass* BlueprintGeneratedClass =
			Cast<UBlueprintGeneratedClass>(GeneratedClass);
		UActorComponent* ExpectedTemplate = nullptr;
		for (UBlueprintGeneratedClass* CurrentClass = BlueprintGeneratedClass;
			CurrentClass != nullptr && ExpectedTemplate == nullptr;
			CurrentClass = Cast<UBlueprintGeneratedClass>(CurrentClass->GetSuperClass()))
		{
			if (CurrentClass->SimpleConstructionScript == nullptr)
			{
				continue;
			}
			if (USCS_Node* Node = CurrentClass->SimpleConstructionScript->FindSCSNode(
				TEXT("UnifiedScript")))
			{
				ExpectedTemplate = Node->GetActualComponentTemplate(BlueprintGeneratedClass);
				if (ExpectedTemplate == nullptr)
				{
					ExpectedTemplate = Node->ComponentTemplate;
				}
			}
		}
		return Cast<UFinareComponent>(ExpectedTemplate);
	}

	UFinareComponent* FindManagedScriptComponentTemplate(UBlueprint& Blueprint)
	{
		UBlueprintGeneratedClass* GeneratedClass =
			Cast<UBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		if (GeneratedClass == nullptr)
		{
			return nullptr;
		}
		for (UBlueprintGeneratedClass* CurrentClass = GeneratedClass;
			CurrentClass != nullptr;
			CurrentClass = Cast<UBlueprintGeneratedClass>(CurrentClass->GetSuperClass()))
		{
			if (CurrentClass->SimpleConstructionScript == nullptr)
			{
				continue;
			}
			USCS_Node* Node = CurrentClass->SimpleConstructionScript->FindSCSNode(
				TEXT("UnifiedScript"));
			if (Node != nullptr && Node->ComponentTemplate != nullptr)
			{
				if (UFinareComponent* Template = Cast<UFinareComponent>(
						Node->GetActualComponentTemplate(GeneratedClass)))
				{
					return Template;
				}
			}
		}
		return nullptr;
	}

	bool CopyScriptComponentConfiguration(
		const UFinareComponent& Source,
		UFinareComponent& Destination)
	{
		if (&Source == &Destination)
		{
			return true;
		}
		if (Source.GetClass() != Destination.GetClass())
		{
			return false;
		}
		for (TFieldIterator<FProperty> It(
				 Source.GetClass(),
				 EFieldIteratorFlags::IncludeSuper);
			 It;
			 ++It)
		{
			FProperty* Property = *It;
			if (Property == nullptr ||
				Property->HasAnyPropertyFlags(
					CPF_Transient | CPF_DuplicateTransient | CPF_EditConst) ||
				!Property->HasAnyPropertyFlags(CPF_Edit))
			{
				continue;
			}
			Property->CopyCompleteValue_InContainer(&Destination, &Source);
		}
		// Override IDs are persistent authoring metadata but intentionally are
		// not editable reflected properties.  Preserve them explicitly when the
		// legacy native carrier becomes an SCS template (or is restored).
		Destination.CopyScriptStateOverrideMetadata(Source);
		return true;
	}

	bool IsOrdinaryActorParent(const UClass* ParentClass)
	{
		return ParentClass != nullptr &&
			ParentClass->IsChildOf(AActor::StaticClass()) &&
			!ParentClass->IsChildOf(AFinareActor::StaticClass());
	}

	bool EnsureOrdinaryActorScriptComponent(
		UBlueprint& Blueprint,
		UFinareAsset& ScriptAsset,
		UFinareComponent* ExistingComponent,
		FString& OutError)
	{
		if (Blueprint.SimpleConstructionScript == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' has no SimpleConstructionScript for its managed Actor carrier."),
				*Blueprint.GetPathName());
			return false;
		}
		USCS_Node* Node = Blueprint.SimpleConstructionScript->FindSCSNode(
			TEXT("UnifiedScript"));
		bool bNewTemplate = false;
		UFinareComponent* Template = Node != nullptr
			                             ? Cast<UFinareComponent>(Node->ComponentTemplate)
			                             : nullptr;
		if (Node != nullptr && Template == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' already uses reserved SCS carrier name 'UnifiedScript' for a different component."),
				*Blueprint.GetPathName());
			return false;
		}
		if (Node == nullptr)
		{
			// A child Blueprint must override an inherited carrier through UE's
			// component handler.  A second local SCS node would shadow the parent
			// and make its template the wrong owner for this Script Asset.
			USCS_Node* InheritedNode = nullptr;
			for (UBlueprintGeneratedClass* CurrentClass =
					Cast<UBlueprintGeneratedClass>(Blueprint.GeneratedClass);
				CurrentClass != nullptr && InheritedNode == nullptr;
				CurrentClass = Cast<UBlueprintGeneratedClass>(CurrentClass->GetSuperClass()))
			{
				if (CurrentClass == Blueprint.GeneratedClass ||
					CurrentClass->SimpleConstructionScript == nullptr)
				{
					continue;
				}
				InheritedNode = CurrentClass->SimpleConstructionScript->FindSCSNode(
					TEXT("UnifiedScript"));
			}
			if (InheritedNode != nullptr)
			{
				UInheritableComponentHandler* Handler =
					Blueprint.GetInheritableComponentHandler(true);
				if (Handler == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Blueprint '%s' could not create its inherited SCS carrier override handler."),
						*Blueprint.GetPathName());
					return false;
				}
				const FComponentKey Key(InheritedNode);
				Handler->Modify();
				Template = Cast<UFinareComponent>(
					Handler->GetOverridenComponentTemplate(Key));
				if (Template == nullptr)
				{
					Template = Cast<UFinareComponent>(
						Handler->CreateOverridenComponentTemplate(Key));
					bNewTemplate = Template != nullptr;
				}
				if (Template == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Blueprint '%s' could not create its inherited SCS carrier override."),
						*Blueprint.GetPathName());
					return false;
				}
			}
			else
			{
				Node = Blueprint.SimpleConstructionScript->CreateNode(
					UFinareComponent::StaticClass(),
					TEXT("UnifiedScript"));
				if (Node == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Blueprint '%s' could not create its managed Actor SCS carrier."),
						*Blueprint.GetPathName());
					return false;
				}
				Blueprint.SimpleConstructionScript->AddNode(Node);
				if (Node->GetVariableName() != FName(TEXT("UnifiedScript")))
				{
					Blueprint.SimpleConstructionScript->RemoveNode(Node, false);
					OutError = FString::Printf(
						TEXT("Blueprint '%s' could not reserve the exact SCS carrier name 'UnifiedScript'; generated '%s' instead."),
						*Blueprint.GetPathName(),
						*Node->GetVariableName().ToString());
					return false;
				}
				Template = Cast<UFinareComponent>(Node->ComponentTemplate);
				bNewTemplate = Template != nullptr;
			}
		}
		if (Template == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' created an invalid managed Actor SCS carrier template."),
				*Blueprint.GetPathName());
			return false;
		}
		if (!bNewTemplate && Template->ScriptAsset.Get() != &ScriptAsset)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' SCS carrier 'UnifiedScript' is owned by another Script Asset."),
				*Blueprint.GetPathName());
			return false;
		}
		Template->Modify();
		if (ExistingComponent != nullptr && ExistingComponent != Template &&
			!CopyScriptComponentConfiguration(*ExistingComponent, *Template))
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' could not migrate its managed Actor carrier configuration into SCS."),
				*Blueprint.GetPathName());
			return false;
		}
		Template->ScriptAsset = &ScriptAsset;
		Blueprint.SimpleConstructionScript->ValidateSceneRootNodes();
		OutError.Reset();
		return true;
	}

	bool RemoveOrdinaryActorScriptComponent(
		UBlueprint& Blueprint,
		const UFinareAsset& ScriptAsset,
		FString& OutError)
	{
		if (Blueprint.SimpleConstructionScript == nullptr)
		{
			OutError.Reset();
			return true;
		}
		USCS_Node* Node = Blueprint.SimpleConstructionScript->FindSCSNode(
			TEXT("UnifiedScript"));
		if (Node != nullptr)
		{
			const UFinareComponent* Template = Cast<UFinareComponent>(Node->ComponentTemplate);
			if (Template == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Blueprint '%s' reserves SCS name 'UnifiedScript' for a non-Finare component."),
					*Blueprint.GetPathName());
				return false;
			}
			if (Template->ScriptAsset.Get() != &ScriptAsset)
			{
				OutError = FString::Printf(
					TEXT("Blueprint '%s' SCS carrier 'UnifiedScript' is owned by another Script Asset."),
					*Blueprint.GetPathName());
				return false;
			}
			Blueprint.SimpleConstructionScript->RemoveNode(Node, false);
			OutError.Reset();
			return true;
		}

		// An inherited carrier is represented by this Blueprint's override
		// record, if one exists.  Remove only that record and preserve the parent
		// SCS node for other generated classes.
		USCS_Node* InheritedNode = nullptr;
		for (UBlueprintGeneratedClass* CurrentClass =
				Cast<UBlueprintGeneratedClass>(Blueprint.GeneratedClass);
			CurrentClass != nullptr && InheritedNode == nullptr;
			CurrentClass = Cast<UBlueprintGeneratedClass>(CurrentClass->GetSuperClass()))
		{
			if (CurrentClass == Blueprint.GeneratedClass ||
				CurrentClass->SimpleConstructionScript == nullptr)
			{
				continue;
			}
			InheritedNode = CurrentClass->SimpleConstructionScript->FindSCSNode(
				TEXT("UnifiedScript"));
		}
		if (InheritedNode != nullptr)
		{
			if (UInheritableComponentHandler* Handler =
					Blueprint.GetInheritableComponentHandler(false))
			{
				const UFinareComponent* Template = Cast<UFinareComponent>(
					Handler->GetOverridenComponentTemplate(FComponentKey(InheritedNode)));
				if (Template != nullptr && Template->ScriptAsset.Get() != &ScriptAsset)
				{
					OutError = FString::Printf(
						TEXT("Blueprint '%s' inherited SCS carrier 'UnifiedScript' is owned by another Script Asset."),
						*Blueprint.GetPathName());
					return false;
				}
				Handler->Modify();
				Handler->RemoveOverridenComponentTemplate(FComponentKey(InheritedNode));
			}
		}
		OutError.Reset();
		return true;
	}

	bool ValidateScriptComponentIdentity(
		const UBlueprint& Blueprint,
		UFinareComponent* Component,
		const UClass* ExpectedComponentClass,
		FString& OutError)
	{
		UClass* GeneratedClass = Blueprint.GeneratedClass;
		UObject* CurrentCdo = GeneratedClass != nullptr
			                      ? GeneratedClass->GetDefaultObject(false)
			                      : nullptr;
		const bool bComponentIsClassDefaultObject = Component != nullptr && Component == CurrentCdo;
		const bool bClassIdentityMatches = bComponentIsClassDefaultObject
			                                   ? Component->GetClass() == GeneratedClass &&
			                                   GeneratedClass->ClassGeneratedBy == &Blueprint
			                                   : ExpectedComponentClass != nullptr &&
			                                   Component != nullptr && Component->GetClass() == ExpectedComponentClass;
		if (GeneratedClass == nullptr || CurrentCdo == nullptr || Component == nullptr ||
			!bClassIdentityMatches)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' could not resolve the exact managed ScriptComponent class identity."),
				*Blueprint.GetPathName());
			return false;
		}

		const bool bOrdinaryActor =
			GeneratedClass->GetSuperClass() != nullptr &&
			IsOrdinaryActorParent(GeneratedClass->GetSuperClass());
		const UFinareComponent* ExpectedTemplate = bOrdinaryActor
			? FindManagedScriptComponentTemplate(const_cast<UBlueprint&>(Blueprint))
			: nullptr;
		UObject* ExpectedOuter = bComponentIsClassDefaultObject
			                         ? GeneratedClass->GetOuter()
			                         : (bOrdinaryActor
				                         ? GeneratedClass
				                         : CurrentCdo);
		if (Component->GetOuter() != ExpectedOuter)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' changed the managed ScriptComponent outer from its CDO owner."),
				*Blueprint.GetPathName());
			return false;
		}

		// During preflight PrepareParentChange updates Blueprint.ParentClass before
		// the duplicate GeneratedClass is compiled.  The existing generated
		// hierarchy is therefore the only valid capture baseline; after compile
		// its SuperClass reflects the prospective parent for exact restore checks.
		UFinareComponent* ExpectedArchetype = bOrdinaryActor
			? (Blueprint.SimpleConstructionScript != nullptr &&
				Blueprint.SimpleConstructionScript->FindSCSNode(TEXT("UnifiedScript")) != nullptr
				? Component->GetClass()->GetDefaultObject<UFinareComponent>()
				: FindManagedScriptComponent(GeneratedClass->GetSuperClass()))
			: FindManagedScriptComponent(GeneratedClass->GetSuperClass());
		if (Component->GetArchetype() != ExpectedArchetype)
		{
			OutError = FString::Printf(
				TEXT("Blueprint '%s' changed the managed ScriptComponent immediate archetype identity."),
				*Blueprint.GetPathName());
			return false;
		}
		if (bOrdinaryActor)
		{
			if (ExpectedTemplate == nullptr || Component != ExpectedTemplate)
			{
				OutError = FString::Printf(
					TEXT("Blueprint '%s' changed the managed Actor SCS carrier template identity."),
					*Blueprint.GetPathName());
				return false;
			}
		}
		OutError.Reset();
		return true;
	}

	bool CaptureScriptComponentOwner(
		const UBlueprint& Blueprint,
		FScriptComponentOwnerSnapshot& OutSnapshot,
		FString& OutError)
	{
		OutSnapshot = FScriptComponentOwnerSnapshot();
		OutSnapshot.OwnerBlueprint.Reset(const_cast<UBlueprint*>(&Blueprint));
		UFinareComponent* Component = FindManagedScriptComponent(Blueprint.GeneratedClass);
		if (Component == nullptr)
		{
			OutError.Reset();
			return true;
		}
		// GetActualComponentTemplate returns the parent template when this
		// Blueprint has no inheritable-component override.  It is not owned by
		// this authoring object and must never be restored through the child.
		if (IsOrdinaryActorParent(Blueprint.GeneratedClass != nullptr
			? Blueprint.GeneratedClass->GetSuperClass()
			: nullptr) &&
			Component->GetOuter() != Blueprint.GeneratedClass)
		{
			OutError.Reset();
			return true;
		}
		OutSnapshot.bHadComponent = true;
		OutSnapshot.ComponentRoot.Reset(Component);
		OutSnapshot.ComponentClass.Reset(Component->GetClass());
		OutSnapshot.ScriptAsset.Reset(Component->ScriptAsset.Get());
		OutSnapshot.ScriptAssetPath = Component->ScriptAssetPath;
		Component->GetScriptStateOverrideMetadata(
			OutSnapshot.ScriptStateOverrideIds,
			OutSnapshot.bScriptStateOverrideBaselineInitialized);
		if (!ValidateScriptComponentIdentity(
			Blueprint,
			Component,
			OutSnapshot.ComponentClass.Get(),
			OutError))
		{
			return false;
		}
		UObject* OldArchetype = Component->GetArchetype();
		for (TFieldIterator<FProperty> It(
			     Component->GetClass(),
			     EFieldIteratorFlags::IncludeSuper);
		     It;
		     ++It)
		{
			FProperty* Property = *It;
			if (Property == nullptr ||
				Property->HasAnyPropertyFlags(CPF_Transient | CPF_EditConst) ||
				!Property->HasAnyPropertyFlags(CPF_Edit))
			{
				continue;
			}
			if (OutSnapshot.EditableValues.AddProperty(Property->GetFName(), Property) !=
				EPropertyBagAlterationResult::Success)
			{
				OutError = FString::Printf(
					TEXT(
						"Managed ScriptComponent property '%s' cannot be represented by a typed preservation snapshot."),
					*Property->GetName());
				return false;
			}
			const FPropertyBagPropertyDesc* SnapshotDesc =
				OutSnapshot.EditableValues.FindPropertyDescByName(Property->GetFName());
			if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Managed ScriptComponent property '%s' produced an incomplete typed preservation snapshot."),
					*Property->GetName());
				return false;
			}
			FScriptComponentOwnerSnapshot::FEditablePropertySnapshot& PropertySnapshot =
				OutSnapshot.EditableProperties.AddDefaulted_GetRef();
			PropertySnapshot.PropertyName = Property->GetFName();
			const FProperty* ArchetypeProperty = OldArchetype != nullptr
				                                     ? FindFProperty<FProperty>(
					                                     OldArchetype->GetClass(), Property->GetFName())
				                                     : nullptr;
			PropertySnapshot.bFollowsArchetype = ArchetypeProperty != nullptr &&
				ArePropertiesExactlyCompatible(Property, ArchetypeProperty) &&
				AreTypedPropertyValuesIdentical(Property, Component, ArchetypeProperty, OldArchetype);
			CopyTypedPropertyValue(
				SnapshotDesc->CachedProperty,
				OutSnapshot.EditableValues.GetMutableValue().GetMemory(),
				Property,
				Component);
		}
		OutError.Reset();
		return true;
	}

	bool RestoreScriptComponentOwner(
		FScriptComponentOwnerSnapshot& Snapshot,
		const FCollectionReplacementMap& ReplacedObjects,
		const bool bRollback,
		FString& OutError)
	{
		if (!Snapshot.bHadComponent)
		{
			OutError.Reset();
			return true;
		}
		UBlueprint* Blueprint = Snapshot.OwnerBlueprint.Get();
		UFinareComponent* Component = Blueprint != nullptr
			                              ? FindManagedScriptComponent(Blueprint->GeneratedClass)
			                              : nullptr;
		if (Blueprint == nullptr)
		{
			OutError = TEXT("Blueprint parent change lost the managed ScriptComponent owner Blueprint.");
			return false;
		}
		if (Blueprint->GeneratedClass == nullptr ||
			Blueprint->GeneratedClass->GetSuperClass() != Blueprint->ParentClass.Get())
		{
			OutError = FString::Printf(
				TEXT(
					"Blueprint '%s' has not compiled its current ParentClass before managed ScriptComponent identity restore."),
				*Blueprint->GetPathName());
			return false;
		}
		if (IsOrdinaryActorParent(Blueprint->GeneratedClass->GetSuperClass()) &&
			Component != nullptr && Component->GetOuter() != Blueprint->GeneratedClass)
		{
			// Native subobjects had distinct child CDO values. After switching to
			// SCS, restore them into this child's override, not the parent template.
			UFinareAsset* Binding = Snapshot.ScriptAsset.Get();
			if (Binding == nullptr)
			{
				Binding = Component->ScriptAsset.Get();
			}
			Blueprint->Modify();
			if (Binding == nullptr || !EnsureOrdinaryActorScriptComponent(
				*Blueprint, *Binding, nullptr, OutError))
			{
				if (OutError.IsEmpty())
				{
					OutError = TEXT("Cannot restore a child SCS carrier without its behavior owner.");
				}
				return false;
			}
			Component = FindManagedScriptComponent(Blueprint->GeneratedClass);
		}
		if (!ValidateScriptComponentIdentity(
			*Blueprint,
			Component,
			Snapshot.ComponentClass.Get(),
			OutError))
		{
			return false;
		}
		UObject* CurrentArchetype = Component->GetArchetype();
		Component->Modify();
		const TMap<UObject*, UObject*> FlattenedReplacedObjects =
			FlattenReplacedObjects(ReplacedObjects);
		for (const FScriptComponentOwnerSnapshot::FEditablePropertySnapshot& PropertySnapshot :
		     Snapshot.EditableProperties)
		{
			const FName PropertyName = PropertySnapshot.PropertyName;
			FProperty* TargetProperty = FindFProperty<FProperty>(
				Component->GetClass(),
				PropertyName);
			const FPropertyBagPropertyDesc* SnapshotDesc =
				Snapshot.EditableValues.FindPropertyDescByName(PropertyName);
			if (TargetProperty == nullptr || SnapshotDesc == nullptr ||
				SnapshotDesc->CachedProperty == nullptr ||
				!ArePropertiesExactlyCompatible(TargetProperty, SnapshotDesc->CachedProperty))
			{
				OutError = FString::Printf(
					TEXT(
						"Managed ScriptComponent property '%s' changed reflected identity during Blueprint reinstancing."),
					*PropertyName.ToString());
				return false;
			}
			void* TargetValue = TargetProperty->ContainerPtrToValuePtr<void>(Component);
			if (!bRollback && PropertySnapshot.bFollowsArchetype)
			{
				FProperty* ArchetypeProperty = CurrentArchetype != nullptr
					                               ? FindFProperty<FProperty>(
						                               CurrentArchetype->GetClass(), PropertyName)
					                               : nullptr;
				if (ArchetypeProperty == nullptr ||
					!ArePropertiesExactlyCompatible(TargetProperty, ArchetypeProperty))
				{
					OutError = FString::Printf(
						TEXT(
							"Managed ScriptComponent follower property '%s' lost its immediate archetype value during Blueprint reinstancing."),
						*PropertyName.ToString());
					return false;
				}
				CopyTypedPropertyValue(
					TargetProperty,
					Component,
					ArchetypeProperty,
					CurrentArchetype);
			}
			else
			{
				void* SnapshotValue = SnapshotDesc->CachedProperty->ContainerPtrToValuePtr<void>(
					Snapshot.EditableValues.GetMutableValue().GetMemory());
				if (!ValidateSnapshotReplacementReferences(
					SnapshotDesc->CachedProperty,
					SnapshotValue,
					ReplacedObjects,
					FString::Printf(
						TEXT("managed ScriptComponent property '%s' on '%s'"),
						*PropertyName.ToString(),
						*Component->GetPathName()),
					OutError))
				{
					return false;
				}
				FArchiveReplaceObjectRef<UObject> ReplaceArchive(
					Component,
					FlattenedReplacedObjects,
					EArchiveReplaceObjectFlags::DelayStart);
				FStructuredArchiveFromArchive StructuredArchive(ReplaceArchive);
				SnapshotDesc->CachedProperty->SerializeItem(StructuredArchive.GetSlot(), SnapshotValue);
				CopyTypedPropertyValue(
					TargetProperty,
					Component,
					SnapshotDesc->CachedProperty,
					Snapshot.EditableValues.GetMutableValue().GetMemory());
			}
			if (const FSetProperty* SetProperty = CastField<FSetProperty>(TargetProperty))
			{
				FScriptSetHelper SetHelper(SetProperty, TargetValue);
				SetHelper.Rehash();
			}
			else if (const FMapProperty* MapProperty = CastField<FMapProperty>(TargetProperty))
			{
				FScriptMapHelper MapHelper(MapProperty, TargetValue);
				MapHelper.Rehash();
			}
		}
		Component->SetScriptStateOverrideMetadata(
			Snapshot.ScriptStateOverrideIds,
			Snapshot.bScriptStateOverrideBaselineInitialized);
		OutError.Reset();
		return true;
	}

	bool CaptureScriptComponentOwners(
		UBlueprint& Blueprint,
		TArray<FScriptComponentOwnerSnapshot>& OutSnapshots,
		FString& OutError)
	{
		OutSnapshots.Reset();
		if (!CaptureScriptComponentOwner(Blueprint, OutSnapshots.AddDefaulted_GetRef(), OutError))
		{
			OutSnapshots.Reset();
			return false;
		}
		const UClass* OriginalGeneratedClass = Blueprint.GeneratedClass;
		if (OriginalGeneratedClass != nullptr)
		{
			for (TObjectIterator<UBlueprint> It; It; ++It)
			{
				UBlueprint* Descendant = *It;
				if (Descendant != nullptr && Descendant != &Blueprint &&
					Descendant->GeneratedClass != nullptr &&
					Descendant->GeneratedClass->IsChildOf(OriginalGeneratedClass))
				{
					if (!CaptureScriptComponentOwner(
						*Descendant,
						OutSnapshots.AddDefaulted_GetRef(),
						OutError))
					{
						OutSnapshots.Reset();
						return false;
					}
				}
			}
		}
		auto GetBlueprintDepth = [](const UBlueprint* Candidate)
		{
			int32 Depth = 0;
			TSet<UClass*> Visited;
			for (UClass* ParentClass = Candidate != nullptr
				                           ? Candidate->ParentClass.Get()
				                           : nullptr;
			     ParentClass != nullptr && !Visited.Contains(ParentClass);
			)
			{
				Visited.Add(ParentClass);
				++Depth;
				const UBlueprint* ParentBlueprint = Cast<UBlueprint>(ParentClass->ClassGeneratedBy);
				ParentClass = ParentBlueprint != nullptr
					              ? ParentBlueprint->ParentClass.Get()
					              : nullptr;
			}
			return Depth;
		};
		OutSnapshots.Sort(
			[&GetBlueprintDepth](
			const FScriptComponentOwnerSnapshot& Left,
			const FScriptComponentOwnerSnapshot& Right)
			{
				const int32 LeftDepth = GetBlueprintDepth(Left.OwnerBlueprint.Get());
				const int32 RightDepth = GetBlueprintDepth(Right.OwnerBlueprint.Get());
				if (LeftDepth != RightDepth)
				{
					return LeftDepth < RightDepth;
				}
				return Left.OwnerBlueprint.Get() != nullptr &&
					Right.OwnerBlueprint.Get() != nullptr &&
					Left.OwnerBlueprint->GetPathName() < Right.OwnerBlueprint->GetPathName();
			});
		for (FScriptComponentOwnerSnapshot& Snapshot : OutSnapshots)
		{
			if (Snapshot.OwnerBlueprint.Get() != &Blueprint)
			{
				continue;
			}
			// The root managed owner is the persisted behavior binding.  Its
			// ScriptAsset fields remain explicit even when the old parent happens
			// to carry the same value; a new ordinary parent may legitimately
			// expose a null component asset.
			for (FScriptComponentOwnerSnapshot::FEditablePropertySnapshot& PropertySnapshot :
			     Snapshot.EditableProperties)
			{
				if (PropertySnapshot.PropertyName == TEXT("ScriptAsset") ||
					PropertySnapshot.PropertyName == TEXT("ScriptAssetPath"))
				{
					PropertySnapshot.bFollowsArchetype = false;
				}
			}
			break;
		}
		OutError.Reset();
		return true;
	}

	/**
	 * A local transaction deliberately kept outside the editor undo stack.  The
	 * Blueprint and its serialized graph/class objects are recorded before the
	 * parent or the generated members are touched, so a compiler or package
	 * failure can restore the complete authoring object rather than only its
	 * variable list.
	 */
	class FLocalBlueprintTransaction final : public FGCObject
	{
	public:
		explicit FLocalBlueprintTransaction(UBlueprint& InBlueprint)
			: Blueprint(InBlueprint)
		{
		}

		bool Isolate(FString& OutError)
		{
			if (bIsolated)
			{
				return true;
			}
			if (GEditor == nullptr || GEditor->Trans == nullptr || GIsTransacting)
			{
				OutError = TEXT(
					"Cannot begin Blueprint parent transaction while another editor transaction is active.");
				return false;
			}
			LocalTransactor = TStrongObjectPtr<UTransBuffer>(
				NewObject<UFinareAuthoringTransBuffer>(GetTransientPackage()));
			if (!LocalTransactor.IsValid())
			{
				OutError = TEXT("Could not allocate a private Blueprint parent transaction buffer.");
				return false;
			}
			LocalTransactor->Initialize(64ull * 1024ull * 1024ull);
			// GEditor->Trans is a reflected property, but assigning the local
			// transactor into it removes the editor's only reflected reference to
			// this previous host.  Keep the host strongly rooted for the whole
			// swap and restoration; its current transaction is the owner of the
			// raw GUndo pointer saved below.
			PreviousTransactor.Reset(GEditor->Trans.Get());
			PreviousUndo = GUndo;
			GEditor->Trans = LocalTransactor.Get();
			GUndo = nullptr;
			bIsolated = true;
			OutError.Reset();
			return true;
		}

		bool Begin(FString& OutError, UObject* RelatedAsset = nullptr)
		{
			if (bActive)
			{
				return true;
			}
			if (!bIsolated || GEditor == nullptr || LocalTransactor == nullptr || GIsTransacting)
			{
				OutError = TEXT(
					"Cannot begin Blueprint parent transaction outside its isolated editor transaction scope.");
				return false;
			}
			const int32 BeginResult = LocalTransactor->Begin(
				TEXT("FinareBlueprintClassMaterializer"),
				FText::FromString(TEXT("Synchronize Finare Blueprint")));
			if (BeginResult != 0)
			{
				OutError = TEXT("Could not begin a private Blueprint parent transaction.");
				return false;
			}
			check(GUndo != nullptr);
			FAuthoringBlueprintTransaction* AuthoringTransaction =
				static_cast<FAuthoringBlueprintTransaction*>(GUndo);
			AuthoringTransaction->BeginExplicitAuthoringCapture();
			if (RelatedAsset != nullptr)
			{
				// The transactor ignores objects outside this explicit capture.
				// Include the Script Asset before changing its carrier requirement.
				Save(RelatedAsset);
				RelatedAssetPackage = RelatedAsset->GetOutermost();
				bRelatedAssetPackageDirty = RelatedAssetPackage.IsValid() && RelatedAssetPackage->IsDirty();
			}
			CaptureBlueprint(Blueprint);
			const UClass* OriginalGeneratedClass = Blueprint.GeneratedClass;
			if (OriginalGeneratedClass != nullptr)
			{
				for (TObjectIterator<UBlueprint> It; It; ++It)
				{
					UBlueprint* Descendant = *It;
					if (Descendant != nullptr && Descendant != &Blueprint &&
						Descendant->GeneratedClass != nullptr &&
						Descendant->GeneratedClass->IsChildOf(OriginalGeneratedClass))
					{
						CaptureBlueprint(*Descendant);
					}
				}
				AuthoringSnapshots.Sort(
					[OriginalGeneratedClass](
					const FBlueprintAuthoringSnapshot& Left,
					const FBlueprintAuthoringSnapshot& Right)
					{
						auto DepthFromRoot = [OriginalGeneratedClass](
							const FBlueprintAuthoringSnapshot& Snapshot)
						{
							int32 Depth = 0;
							for (const UClass* Current = Snapshot.OriginalGeneratedClass.Get();
							     Current != nullptr && Current != OriginalGeneratedClass;
							     Current = Current->GetSuperClass())
							{
								++Depth;
							}
							return Depth;
						};
						return DepthFromRoot(Left) < DepthFromRoot(Right);
					});
			}
			AuthoringTransaction->EndExplicitAuthoringCapture();
			bActive = true;
			OutError.Reset();
			return true;
		}

		bool Finalize(FString& OutError)
		{
			if (!bActive)
			{
				OutError.Reset();
				return true;
			}
			const int32 EndResult = LocalTransactor->End();
			if (EndResult != 1)
			{
				OutError = TEXT("Could not finalize the private Blueprint parent transaction.");
				return false;
			}
			bActive = false;
			bRollbackAvailable = true;
			OutError.Reset();
			return true;
		}

		bool Rollback(FString& OutError)
		{
			if (!bRollbackAvailable && !bActive)
			{
				OutError.Reset();
				return true;
			}
			if (!Finalize(OutError))
			{
				return false;
			}
			const bool bRestored = LocalTransactor->Undo(false);
			bRollbackAvailable = false;
			if (!bRestored)
			{
				OutError = TEXT("The private Blueprint parent transaction could not restore its captured state.");
				return false;
			}
			if (!RecompileRestoredAuthoring(OutError))
			{
				return false;
			}
			OutError.Reset();
			return true;
		}

		void DiscardRollbackAbility()
		{
			bRollbackAvailable = false;
		}

		void RestoreCapturedPackageDirtyFlags() const
		{
			for (const FBlueprintAuthoringSnapshot& Snapshot : AuthoringSnapshots)
			{
				Snapshot.RestorePackageDirtyFlag();
			}
			if (RelatedAssetPackage.IsValid())
			{
				RelatedAssetPackage->SetDirtyFlag(bRelatedAssetPackageDirty);
			}
		}

		virtual ~FLocalBlueprintTransaction() override
		{
			if (bActive)
			{
				FString Ignored;
				Rollback(Ignored);
			}
			ReleaseIsolation();
		}

		virtual void AddReferencedObjects(FReferenceCollector& Collector) override
		{
			if (LocalTransactor.IsValid())
			{
				UTransBuffer::AddReferencedObjects(LocalTransactor.Get(), Collector);
			}
			for (TObjectPtr<UObject>& Object : ReferencedObjects)
			{
				Collector.AddReferencedObject(Object);
			}
		}

		virtual FString GetReferencerName() const override
		{
			return TEXT("FLocalBlueprintTransaction");
		}

	private:
		void ReleaseIsolation()
		{
			if (!bIsolated)
			{
				return;
			}
			if (GEditor != nullptr && LocalTransactor.IsValid())
			{
				GEditor->Trans = PreviousTransactor.Get();
			}
			GUndo = PreviousUndo;
			PreviousUndo = nullptr;
			// Release only after both globals point back at the original host.
			PreviousTransactor.Reset();
			LocalTransactor.Reset();
			bIsolated = false;
			bActive = false;
			bRollbackAvailable = false;
		}

		void Save(UObject* Object)
		{
			if (Object != nullptr && GUndo != nullptr)
			{
				GUndo->SaveObject(Object);
				ReferencedObjects.AddUnique(Object);
			}
		}

		void CaptureObjectTree(UObject* Root)
		{
			if (Root == nullptr)
			{
				return;
			}
			Save(Root);
			ForEachObjectWithOuter(
				Root,
				[this](UObject* SubObject)
				{
					Save(SubObject);
				},
				EGetObjectsFlags::IncludeNestedObjects,
				RF_NoFlags,
				EInternalObjectFlags::None);
		}

		void CaptureBlueprint(UBlueprint& Candidate)
		{
			FBlueprintAuthoringSnapshot& AuthoringSnapshot = AuthoringSnapshots.AddDefaulted_GetRef();
			AuthoringSnapshot.Capture(Candidate);
			Save(&Candidate);
			if (UInheritableComponentHandler* Handler = Candidate.GetInheritableComponentHandler(false))
			{
				Save(Handler);
				TArray<UActorComponent*> Templates;
				Handler->GetAllTemplates(Templates, true);
				for (UActorComponent* Template : Templates)
				{
					CaptureObjectTree(Template);
				}
			}
			if (Candidate.GeneratedClass != nullptr)
			{
				CaptureObjectTree(Candidate.GeneratedClass->GetDefaultObject());
			}
			if (Candidate.SimpleConstructionScript != nullptr)
			{
				CaptureObjectTree(Candidate.SimpleConstructionScript);
				for (USCS_Node* Node : Candidate.SimpleConstructionScript->GetAllNodes())
				{
					if (Node != nullptr)
					{
						Save(Node);
						Save(Node->ComponentTemplate);
					}
				}
			}
			auto CaptureGraph = [this](UEdGraph* Graph)
			{
				if (Graph == nullptr)
				{
					return;
				}
				CaptureObjectTree(Graph);
				for (UEdGraphNode* Node : Graph->Nodes)
				{
					Save(Node);
				}
			};
			for (UEdGraph* Graph : Candidate.FunctionGraphs)
			{
				CaptureGraph(Graph);
			}
			for (UEdGraph* Graph : Candidate.UbergraphPages)
			{
				CaptureGraph(Graph);
			}
			for (UEdGraph* Graph : Candidate.MacroGraphs)
			{
				CaptureGraph(Graph);
			}
			for (const FBPInterfaceDescription& Interface : Candidate.ImplementedInterfaces)
			{
				for (UEdGraph* Graph : Interface.Graphs)
				{
					CaptureGraph(Graph);
				}
			}
		}

		bool RecompileRestoredAuthoring(FString& OutError)
		{
			for (const FBlueprintAuthoringSnapshot& Snapshot : AuthoringSnapshots)
			{
				UBlueprint* Candidate = Snapshot.Blueprint.Get();
				if (Candidate == nullptr ||
					Candidate->ParentClass != Snapshot.OriginalParent.Get())
				{
					OutError = Candidate != nullptr
						           ? FString::Printf(
							           TEXT(
								           "Blueprint '%s' did not restore its original parent before authoring rollback recompilation."),
							           *Candidate->GetPathName())
						           : TEXT(
							           "A Blueprint captured by the private transaction was destroyed before authoring rollback recompilation.");
					return false;
				}
			}

			for (const FBlueprintAuthoringSnapshot& Snapshot : AuthoringSnapshots)
			{
				UBlueprint* Candidate = Snapshot.Blueprint.Get();
				if (Candidate == nullptr)
				{
					OutError = TEXT(
						"A Blueprint captured by the private transaction was destroyed during authoring rollback recompilation.");
					return false;
				}

				if (UFinareBlueprintGeneratedClass* GeneratedClass =
					Cast<UFinareBlueprintGeneratedClass>(Candidate->GeneratedClass))
				{
					// Clear routes from the post-mutation generated class before
					// restoring the captured maps.  The mutated class may have
					// routes which are absent from the old snapshot.
					GeneratedClass->PrepareManagedFunctionsForRecompile();
				}
				if (!Snapshot.ManagedGeneratedClass.RestoreTo(*Candidate, OutError))
				{
					return false;
				}

				if (UBlueprintGeneratedClass* GeneratedClass =
					Cast<UBlueprintGeneratedClass>(Candidate->GeneratedClass))
				{
					if (UClass* OriginalParent = Snapshot.OriginalParent.Get())
					{
						GeneratedClass->PrepareToConformSparseClassData(
							OriginalParent->GetSparseClassDataStruct());
					}
				}

				FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(Candidate);
				FKismetEditorUtilities::CompileBlueprint(
					Candidate,
					EBlueprintCompileOptions::SkipSave |
					EBlueprintCompileOptions::UseDeltaSerializationDuringReinstancing);
				if (Candidate->Status == BS_Error || Candidate->GeneratedClass == nullptr)
				{
					OutError = FString::Printf(
						TEXT(
							"Blueprint '%s' failed to recompile its restored authoring state during transaction rollback."),
						*Candidate->GetPathName());
					return false;
				}

				if (!Snapshot.ManagedGeneratedClass.RestoreTo(*Candidate, OutError))
				{
					return false;
				}
				if (UFinareBlueprintGeneratedClass* GeneratedClass =
					Cast<UFinareBlueprintGeneratedClass>(Candidate->GeneratedClass))
				{
					GeneratedClass->BindManagedFunctions();
					for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
					     GeneratedClass->ManagedFunctionBindings)
					{
						if (!GeneratedClass->IsManagedFunctionBound(Pair.Key))
						{
							OutError = FString::Printf(
								TEXT(
									"Blueprint '%s' did not restore managed function binding '%s' during transaction rollback."),
								*Candidate->GetPathName(),
								*Pair.Key.ToString());
							return false;
						}
					}
				}
				else if (Snapshot.ManagedGeneratedClass.bCaptured)
				{
					OutError = FString::Printf(
						TEXT("Blueprint '%s' lost its Finare generated class after authoring rollback recompilation."),
						*Candidate->GetPathName());
					return false;
				}

				if (Candidate->GeneratedClass->GetSuperClass() != Candidate->ParentClass)
				{
					OutError = FString::Printf(
						TEXT(
							"Blueprint '%s' did not restore its original generated superclass during transaction rollback."),
						*Candidate->GetPathName());
					return false;
				}
			}

			OutError.Reset();
			return true;
		}

		UBlueprint& Blueprint;
		TStrongObjectPtr<UTransBuffer> LocalTransactor;
		TStrongObjectPtr<UTransactor> PreviousTransactor;
		ITransaction* PreviousUndo = nullptr;
		TArray<TObjectPtr<UObject>> ReferencedObjects;
		TArray<FBlueprintAuthoringSnapshot> AuthoringSnapshots;
		TWeakObjectPtr<UPackage> RelatedAssetPackage;
		bool bRelatedAssetPackageDirty = false;
		bool bIsolated = false;
		bool bActive = false;
		bool bRollbackAvailable = false;
	};

	bool PrepareParentChange(UBlueprint& Blueprint, UClass& NewParent, FString& OutError)
	{
		UFinareAsset* ScriptAsset = nullptr;
		if (const UFinareBlueprint* UnifiedBlueprint = Cast<UFinareBlueprint>(&Blueprint))
		{
			ScriptAsset = UnifiedBlueprint->ScriptAsset.Get();
		}
		const bool bWasOrdinaryActor = IsOrdinaryActorParent(Blueprint.ParentClass);
		const bool bWillBeOrdinaryActor = IsOrdinaryActorParent(&NewParent);
		UFinareComponent* ExistingComponent = ScriptAsset != nullptr &&
			ScriptAsset->ScriptClassKind == EFinareClassKind::Actor
			? FindManagedScriptComponent(Blueprint.GeneratedClass)
			: nullptr;
		Blueprint.Modify();
		if (Blueprint.SimpleConstructionScript != nullptr)
		{
			Blueprint.SimpleConstructionScript->Modify();
			for (USCS_Node* Node : Blueprint.SimpleConstructionScript->GetAllNodes())
			{
				if (Node != nullptr)
				{
					Node->Modify();
				}
			}
		}
		if (ScriptAsset != nullptr &&
			ScriptAsset->ScriptClassKind == EFinareClassKind::Actor &&
			bWasOrdinaryActor && !bWillBeOrdinaryActor &&
			!RemoveOrdinaryActorScriptComponent(Blueprint, *ScriptAsset, OutError))
		{
			return false;
		}
		Blueprint.ParentClass = &NewParent;
		if (ScriptAsset != nullptr &&
			ScriptAsset->ScriptClassKind == EFinareClassKind::Actor &&
			bWillBeOrdinaryActor &&
			!EnsureOrdinaryActorScriptComponent(
				Blueprint,
				*ScriptAsset,
				ExistingComponent,
				OutError))
		{
			return false;
		}
		if (Blueprint.SimpleConstructionScript != nullptr)
		{
			Blueprint.SimpleConstructionScript->ValidateSceneRootNodes();
		}
		FBlueprintEditorUtils::MarkBlueprintAsModified(&Blueprint);
		if (UBlueprintGeneratedClass* GeneratedClass =
			Cast<UBlueprintGeneratedClass>(Blueprint.GeneratedClass))
		{
			GeneratedClass->PrepareToConformSparseClassData(NewParent.GetSparseClassDataStruct());
		}
		OutError.Reset();
		return true;
	}

	UClass* LoadExactParentClass(const FString& Path, FString& OutError)
	{
		UObject* Loaded = StaticLoadObject(UClass::StaticClass(), nullptr, *Path);
		UClass* Result = Cast<UClass>(Loaded);
		if (Result == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Finare class binding base '%s' could not be loaded exactly."),
				*Path);
			return nullptr;
		}
		if (!Result->GetPathName().Equals(Path, ESearchCase::CaseSensitive))
		{
			OutError = FString::Printf(
				TEXT("Finare class binding base '%s' resolved to '%s'; redirects are not accepted."),
				*Path,
				*Result->GetPathName());
			return nullptr;
		}
		return Result;
	}

	bool ValidateParentFamily(
		const UFinareAsset& ScriptAsset,
		UClass& ParentClass,
		FString& OutError)
	{
		if (ParentClass.HasAnyClassFlags(CLASS_Interface | CLASS_Deprecated | CLASS_NewerVersionExists))
		{
			OutError = FString::Printf(
				TEXT("Finare class binding base '%s' is an interface, deprecated class, or newer-version class."),
				*ParentClass.GetPathName());
			return false;
		}

		bool bFamilyMatches = false;
		bool bCarrierMatches = false;
		switch (ScriptAsset.ScriptClassKind)
		{
		case EFinareClassKind::Actor:
			bFamilyMatches = ParentClass.IsChildOf(AActor::StaticClass());
			bCarrierMatches = ParentClass.IsChildOf(AFinareActor::StaticClass()) ||
				IsOrdinaryActorParent(&ParentClass);
			break;
		case EFinareClassKind::ActorComponent:
			bFamilyMatches = ParentClass.IsChildOf(UActorComponent::StaticClass());
			// Managed execution uses an internal UFinareComponent sidecar; the
			// generated native component itself is the receiver.  Any legal UE
			// ActorComponent family is therefore valid here.
			bCarrierMatches = bFamilyMatches;
			break;
		case EFinareClassKind::Object:
			bFamilyMatches = ParentClass.IsChildOf(UObject::StaticClass()) &&
				!ParentClass.IsChildOf(AActor::StaticClass()) &&
				!ParentClass.IsChildOf(UActorComponent::StaticClass()) &&
				!ParentClass.IsChildOf(UBlueprintFunctionLibrary::StaticClass());
			// Ordinary UObject Script Classes use a World-owned internal
			// execution sidecar.  The receiver itself remains the generated
			// UObject, so no FinareObject carrier parent is required.
			bCarrierMatches = bFamilyMatches;
			break;
		case EFinareClassKind::BlueprintFunctionLibrary:
			bFamilyMatches = ParentClass.IsChildOf(UBlueprintFunctionLibrary::StaticClass());
			bCarrierMatches = bFamilyMatches;
			break;
		default:
			break;
		}
		if (!bFamilyMatches)
		{
			OutError = FString::Printf(
				TEXT("Finare class binding base '%s' is not compatible with class kind %d."),
				*ParentClass.GetPathName(),
				static_cast<int32>(ScriptAsset.ScriptClassKind));
			return false;
		}
		if (!bCarrierMatches)
		{
			OutError = FString::Printf(
				TEXT(
					"Finare class binding base '%s' requires composition: its behavior carrier is not compatible with class kind %d."),
				*ParentClass.GetPathName(),
				static_cast<int32>(ScriptAsset.ScriptClassKind));
			return false;
		}
		OutError.Reset();
		return true;
	}

	bool BuildParentChangePlan(
		UFinareAsset& ScriptAsset,
		UBlueprint& Blueprint,
		const TOptional<FFinareClassBindingDeclaration>& Binding,
		FParentChangePlan& OutPlan,
		FString& OutError)
	{
		OutPlan = FParentChangePlan();
		OutPlan.CurrentParent = Blueprint.ParentClass;
		if (!Binding.IsSet() || !Binding->BaseClassPath.IsSet())
		{
			OutError.Reset();
			return true;
		}

		const FString& RequestedPath = Binding->BaseClassPath.GetValue();
		UClass* RequestedParent = LoadExactParentClass(RequestedPath, OutError);
		if (RequestedParent == nullptr || !ValidateParentFamily(ScriptAsset, *RequestedParent, OutError))
		{
			return false;
		}
		if (RequestedParent == Blueprint.GeneratedClass ||
			(Blueprint.GeneratedClass != nullptr &&
				RequestedParent->IsChildOf(Blueprint.GeneratedClass)))
		{
			OutError = FString::Printf(
				TEXT("Finare class binding base '%s' would create a self or descendant cycle."),
				*RequestedPath);
			return false;
		}
		for (UClass* Cursor = RequestedParent; Cursor != nullptr; Cursor = Cursor->GetSuperClass())
		{
			const UFinareBlueprintGeneratedClass* ManagedParent =
				Cast<UFinareBlueprintGeneratedClass>(Cursor);
			if (ManagedParent != nullptr && ManagedParent->ManagedScriptAsset != nullptr &&
				ManagedParent->ManagedScriptAsset != &ScriptAsset)
			{
				OutError = FString::Printf(
					TEXT(
						"Finare class binding base '%s' has an unrelated managed-generated owner '%s' in its ancestry."),
					*RequestedPath,
					*Cursor->GetPathName());
				return false;
			}
		}

		if (Blueprint.ParentClass == nullptr ||
			!Blueprint.ParentClass->GetPathName().Equals(RequestedPath, ESearchCase::CaseSensitive))
		{
			OutPlan.RequestedParent = RequestedParent;
			OutPlan.bChanged = Blueprint.ParentClass != RequestedParent;
		}
		auto RejectActiveManagedInstances = [&Blueprint, &OutError]() -> bool
		{
			for (TObjectIterator<UFinareComponent> It; It; ++It)
			{
				UFinareComponent* Component = *It;
				if (!IsValid(Component) || !Component->bScriptRunning)
				{
					continue;
				}
				const UFinareBlueprintGeneratedClass* ActiveClass =
					Component->GetActiveManagedGeneratedClass();
				if (ActiveClass != nullptr &&
					(ActiveClass == Blueprint.GeneratedClass.Get() ||
						(Blueprint.GeneratedClass != nullptr && ActiveClass->
							IsChildOf(Blueprint.GeneratedClass.Get()))))
				{
					OutError = FString::Printf(
						TEXT(
							"Cannot change parent of Blueprint '%s' while a managed instance is active; stop the affected World first."),
						*Blueprint.GetPathName());
					return false;
				}
			}
			return true;
		};
		if (OutPlan.bChanged && !RejectActiveManagedInstances())
		{
			return false;
		}
		OutError.Reset();
		return true;
	}

	bool ParseSemanticId(const FString& SemanticId, FGuid& OutId)
	{
		FString Digits = SemanticId;
		return Digits.RemoveFromStart(TEXT("us_")) &&
			Digits.Len() == 32 &&
			FGuid::ParseExact(Digits, EGuidFormats::Digits, OutId);
	}

	bool TryCanonicalEnumName(const UEnum* Enum, int64 Value, FString& OutName)
	{
		OutName.Reset();
		return Enum != nullptr && Enum->IsValidEnumValue(Value) &&
			Enum->FindAuthoredNameStringByValue(OutName, Value) && !OutName.IsEmpty();
	}

	bool IsBlueprintEnumValue(int64 Value)
	{
		// K2 exposes reflected UEnums as PC_Byte pins, including enum classes.
		return Value >= 0 && Value <= 255;
	}

	bool IsManagedState(const FBPVariableDescription& Variable)
	{
		return Variable.HasMetaData(ManagedStateKey) &&
			Variable.GetMetaData(ManagedStateKey) == TEXT("true");
	}

	int32 FindVariableByGuid(const UBlueprint& Blueprint, const FGuid& Id)
	{
		return Blueprint.NewVariables.IndexOfByPredicate(
			[&Id](const FBPVariableDescription& Variable)
			{
				return Variable.VarGuid == Id;
			});
	}

	int32 FindVariableByStateId(const UBlueprint& Blueprint, const FString& SemanticId)
	{
		return Blueprint.NewVariables.IndexOfByPredicate(
			[&SemanticId](const FBPVariableDescription& Variable)
			{
				return IsManagedState(Variable) &&
					Variable.HasMetaData(StateIdKey) &&
					Variable.GetMetaData(StateIdKey) == SemanticId;
			});
	}

	bool ValidateAddedStateNameConflicts(
		const UBlueprint& Blueprint,
		TConstArrayView<FStateDeclaration> Declarations,
		FString& OutError)
	{
		OutError.Reset();
		if (Blueprint.GeneratedClass == nullptr)
		{
			return true;
		}

		TArray<const FStateDeclaration*> AddedDeclarations;
		for (const FStateDeclaration& Declaration : Declarations)
		{
			int32 ExistingIndex = FindVariableByGuid(Blueprint, Declaration.Id);
			if (ExistingIndex == INDEX_NONE)
			{
				ExistingIndex = FindVariableByStateId(Blueprint, Declaration.SemanticId);
			}
			const bool bNameChanged = ExistingIndex != INDEX_NONE &&
				Blueprint.NewVariables[ExistingIndex].VarName != Declaration.Name;
			if (ExistingIndex == INDEX_NONE || bNameChanged)
			{
				AddedDeclarations.Add(&Declaration);
			}
		}
		if (AddedDeclarations.IsEmpty())
		{
			return true;
		}

		for (TObjectIterator<UBlueprint> It; It; ++It)
		{
			UBlueprint* Descendant = *It;
			UClass* DescendantClass = Descendant != nullptr
				                          ? Descendant->GeneratedClass
				                          : nullptr;
			if (Descendant == nullptr || DescendantClass == nullptr ||
				DescendantClass == Blueprint.GeneratedClass ||
				!DescendantClass->IsChildOf(Blueprint.GeneratedClass) ||
				DescendantClass->HasAnyClassFlags(CLASS_NewerVersionExists | CLASS_Deprecated) ||
				DescendantClass->ClassGeneratedBy != Descendant)
			{
				continue;
			}
			for (const FBPVariableDescription& Variable : Descendant->NewVariables)
			{
				if (IsManagedState(Variable))
				{
					continue;
				}
				for (const FStateDeclaration* Declaration : AddedDeclarations)
				{
					if (Variable.VarName == Declaration->Name)
					{
						OutError = FString::Printf(
							TEXT(
								"New state '%s' conflicts with user Blueprint variable '%s' on loaded descendant '%s'."),
							*Declaration->Name.ToString(),
							*Variable.VarName.ToString(),
							*Descendant->GetPathName());
						return false;
					}
				}
			}
		}
		return true;
	}

	bool ResolvePinType(
		const FString& TypeKind,
		const FString& MetadataName,
		FEdGraphPinType& OutType,
		const FFunctionValueTypeDeclaration* UnrealType = nullptr)
	{
		OutType = FEdGraphPinType();
		if (TypeKind == TEXT("object") && UnrealType != nullptr &&
			(UnrealType->Kind == EFunctionValueTypeKind::Object || UnrealType->Kind == EFunctionValueTypeKind::Interface))
		{
			UClass* Class = LoadObject<UClass>(nullptr, *UnrealType->UnrealTypePath);
			const bool bInterface = UnrealType->Kind == EFunctionValueTypeKind::Interface;
			if (Class == nullptr || Class->GetPathName() != UnrealType->UnrealTypePath ||
				Class->GetName() != UnrealType->UnrealNativeName || Class->HasAnyClassFlags(CLASS_Interface) != bInterface)
				return false;
			OutType.PinCategory = bInterface ? UEdGraphSchema_K2::PC_Interface : UEdGraphSchema_K2::PC_Object;
			OutType.PinSubCategoryObject = Class;
			return true;
		}
		if (TypeKind == TEXT("boolean") && MetadataName == TEXT("System.Boolean"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Boolean;
			return true;
		}
		if (TypeKind == TEXT("integer"))
		{
			if (MetadataName == TEXT("System.Byte"))
			{
				OutType.PinCategory = UEdGraphSchema_K2::PC_Byte;
				return true;
			}
			if (MetadataName == TEXT("System.Int32"))
			{
				OutType.PinCategory = UEdGraphSchema_K2::PC_Int;
				return true;
			}
			if (MetadataName == TEXT("System.Int64"))
			{
				OutType.PinCategory = UEdGraphSchema_K2::PC_Int64;
				return true;
			}
			return false;
		}
		if (TypeKind == TEXT("floatingPoint"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Real;
			if (MetadataName == TEXT("System.Single"))
			{
				OutType.PinSubCategory = UEdGraphSchema_K2::PC_Float;
				return true;
			}
			if (MetadataName == TEXT("System.Double"))
			{
				OutType.PinSubCategory = UEdGraphSchema_K2::PC_Double;
				return true;
			}
			return false;
		}
		if (TypeKind == TEXT("string") && MetadataName == TEXT("System.String"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_String;
			return true;
		}
		if (TypeKind == TEXT("name") &&
			MetadataName == TEXT("Finare.Unreal.UnrealName"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Name;
			return true;
		}
		if (TypeKind == TEXT("text") &&
			MetadataName == TEXT("Finare.Unreal.UnrealText"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Text;
			return true;
		}
		if (TypeKind == TEXT("object") &&
			MetadataName == TEXT("Finare.Unreal.UnrealObjectHandle"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Object;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		if (TypeKind == TEXT("class") &&
			MetadataName == TEXT("Finare.Unreal.UnrealClassHandle"))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_Class;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		if (TypeKind == TEXT("softObject") &&
			MetadataName == TEXT("Finare.Unreal.UnrealSoftObjectHandle") &&
			(UnrealType == nullptr || UnrealType->Kind == EFunctionValueTypeKind::None))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_SoftObject;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		if (TypeKind == TEXT("softClass") &&
			MetadataName == TEXT("Finare.Unreal.UnrealSoftClassHandle") &&
			(UnrealType == nullptr || UnrealType->Kind == EFunctionValueTypeKind::None))
		{
			OutType.PinCategory = UEdGraphSchema_K2::PC_SoftClass;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		if (TypeKind == TEXT("enum") && UnrealType != nullptr &&
			UnrealType->Kind == EFunctionValueTypeKind::Enum)
		{
			UEnum* Enum = LoadObject<UEnum>(nullptr, *UnrealType->UnrealTypePath);
			if (Enum == nullptr || Enum->GetPathName() != UnrealType->UnrealTypePath ||
				Enum->GetName() != UnrealType->UnrealNativeName)
			{
				return false;
			}
			OutType.PinCategory = UEdGraphSchema_K2::PC_Byte;
			OutType.PinSubCategoryObject = Enum;
			return true;
		}
		if (TypeKind == TEXT("struct") && UnrealType != nullptr &&
			UnrealType->Kind == EFunctionValueTypeKind::Struct &&
			(MetadataName == TEXT("Finare.Unreal.UnrealVector") ||
				MetadataName == TEXT("Finare.Unreal.UnrealRotator") ||
				MetadataName == TEXT("Finare.Unreal.UnrealTransform")) &&
			(MetadataName == TEXT("Finare.Unreal.UnrealVector")
				 ? UnrealType->UnrealTypePath == TEXT("/Script/CoreUObject.Vector")
				 : MetadataName == TEXT("Finare.Unreal.UnrealRotator")
				 ? UnrealType->UnrealTypePath == TEXT("/Script/CoreUObject.Rotator")
				 : UnrealType->UnrealTypePath == TEXT("/Script/CoreUObject.Transform")))
		{
			UScriptStruct* Struct = LoadObject<UScriptStruct>(
				nullptr,
				*UnrealType->UnrealTypePath);
			if (Struct == nullptr || Struct->GetName() != UnrealType->UnrealNativeName)
			{
				return false;
			}
			OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
			OutType.PinSubCategoryObject = Struct;
			return true;
		}
		return false;
	}

	bool ResolveFunctionPinType(
		const FString& MetadataName,
		const FFunctionValueTypeDeclaration& ValueType,
		bool bAllowVoid,
		FEdGraphPinType& OutType,
		bool& bOutHasValue)
	{
		bOutHasValue = MetadataName != TEXT("System.Void");
		if (!bOutHasValue)
		{
			OutType = FEdGraphPinType();
			return bAllowVoid;
		}
		if (ValueType.Kind == EFunctionValueTypeKind::Array ||
			ValueType.Kind == EFunctionValueTypeKind::Set ||
			ValueType.Kind == EFunctionValueTypeKind::Map)
		{
			const bool bMap = ValueType.Kind == EFunctionValueTypeKind::Map;
			const FString Definition = bMap
				                           ? TEXT("System.Collections.Generic.IReadOnlyDictionary")
				                           : ValueType.Kind == EFunctionValueTypeKind::Array
				                           ? TEXT("System.Collections.Generic.IReadOnlyList")
				                           : TEXT("System.Collections.Generic.IReadOnlySet");
			if (ValueType.TypeArguments.Num() != (bMap ? 2 : 1) ||
				ValueType.ArgumentMetadataNames.Num() != ValueType.TypeArguments.Num() ||
				MetadataName != Definition + TEXT("<") +
				FString::Join(ValueType.ArgumentMetadataNames, TEXT(", ")) + TEXT(">"))
			{
				return false;
			}
			bool bHasElement = false;
			if (!ResolveFunctionPinType(ValueType.ArgumentMetadataNames[0], ValueType.TypeArguments[0],
			                            false, OutType, bHasElement) || !bHasElement || OutType.IsContainer() ||
				(ValueType.Kind != EFunctionValueTypeKind::Array &&
					!FBlueprintEditorUtils::HasGetTypeHash(OutType)))
			{
				return false;
			}
			OutType.ContainerType = bMap
				                        ? EPinContainerType::Map
				                        : ValueType.Kind == EFunctionValueTypeKind::Array
				                        ? EPinContainerType::Array
				                        : EPinContainerType::Set;
			if (bMap)
			{
				FEdGraphPinType MappedType;
				if (!ResolveFunctionPinType(ValueType.ArgumentMetadataNames[1], ValueType.TypeArguments[1],
				                            false, MappedType, bHasElement) || !bHasElement || MappedType.IsContainer())
				{
					return false;
				}
				OutType.PinValueType.TerminalCategory = MappedType.PinCategory;
				OutType.PinValueType.TerminalSubCategory = MappedType.PinSubCategory;
				OutType.PinValueType.TerminalSubCategoryObject = MappedType.PinSubCategoryObject;
			}
			return true;
		}
		if (ValueType.Kind == EFunctionValueTypeKind::Object || ValueType.Kind == EFunctionValueTypeKind::Interface)
			return ResolvePinType(TEXT("object"), MetadataName, OutType, &ValueType);
		if (ValueType.Kind == EFunctionValueTypeKind::Enum)
		{
			UEnum* Enum = LoadObject<UEnum>(nullptr, *ValueType.UnrealTypePath);
			if (Enum == nullptr || Enum->GetName() != ValueType.UnrealNativeName)
			{
				return false;
			}
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Byte;
			OutType.PinSubCategoryObject = Enum;
			return true;
		}
		if (ValueType.Kind == EFunctionValueTypeKind::Struct)
		{
			UScriptStruct* Struct = LoadObject<UScriptStruct>(nullptr, *ValueType.UnrealTypePath);
			if (Struct == nullptr)
			{
				return false;
			}
			for (const TArray<FName>& FieldPath : ValueType.StructFieldPaths)
			{
				const UStruct* Owner = Struct;
				const FProperty* Field = nullptr;
				for (int32 Index = 0; Index < FieldPath.Num(); ++Index)
				{
					if (Owner != nullptr)
					{
						Field = FindFProperty<FProperty>(Owner, FieldPath[Index]);
					}
					else if (const FArrayProperty* Array = CastField<FArrayProperty>(Field);
						Array && FieldPath[Index] == FName(TEXT("$element")))
					{
						Field = Array->Inner;
					}
					else if (const FSetProperty* Set = CastField<FSetProperty>(Field);
						Set && FieldPath[Index] == FName(TEXT("$element")))
					{
						Field = Set->ElementProp;
					}
					else if (const FMapProperty* Map = CastField<FMapProperty>(Field))
					{
						Field = FieldPath[Index] == FName(TEXT("$key"))
							        ? Map->KeyProp
							        : FieldPath[Index] == FName(TEXT("$value"))
							        ? Map->ValueProp
							        : nullptr;
					}
					else
					{
						Field = nullptr;
					}
					if (Field == nullptr)
					{
						return false;
					}
					if (Index + 1 < FieldPath.Num())
					{
						const FStructProperty* Nested = CastField<FStructProperty>(Field);
						Owner = Nested != nullptr ? Nested->Struct : nullptr;
					}
				}
			}
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
			OutType.PinSubCategoryObject = Struct;
			return true;
		}
		FString TypeKind;
		if (MetadataName == TEXT("System.Boolean"))
		{
			TypeKind = TEXT("boolean");
		}
		else if (MetadataName == TEXT("System.Byte") ||
			MetadataName == TEXT("System.Int32") ||
			MetadataName == TEXT("System.Int64"))
		{
			TypeKind = TEXT("integer");
		}
		else if (MetadataName == TEXT("System.Single") ||
			MetadataName == TEXT("System.Double"))
		{
			TypeKind = TEXT("floatingPoint");
		}
		else if (MetadataName == TEXT("System.String"))
		{
			TypeKind = TEXT("string");
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealName"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Name;
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealText"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Text;
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealSoftObjectHandle") ||
			MetadataName == TEXT("Finare.Unreal.UnrealSoftClassHandle"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = MetadataName.EndsWith(TEXT("UnrealSoftClassHandle"))
				                      ? UEdGraphSchema_K2::PC_SoftClass
				                      : UEdGraphSchema_K2::PC_SoftObject;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealObjectHandle"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Object;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealClassHandle"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Class;
			OutType.PinSubCategoryObject = UObject::StaticClass();
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealVector"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
			OutType.PinSubCategoryObject = TBaseStructure<FVector>::Get();
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealRotator"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
			OutType.PinSubCategoryObject = TBaseStructure<FRotator>::Get();
			return true;
		}
		else if (MetadataName == TEXT("Finare.Unreal.UnrealTransform"))
		{
			OutType = FEdGraphPinType();
			OutType.PinCategory = UEdGraphSchema_K2::PC_Struct;
			OutType.PinSubCategoryObject = TBaseStructure<FTransform>::Get();
			return true;
		}
		else
		{
			return false;
		}
		return ResolvePinType(TypeKind, MetadataName, OutType);
	}

	FEdGraphPinType MakeWorldContextPinType()
	{
		FEdGraphPinType Result;
		Result.PinCategory = UEdGraphSchema_K2::PC_Object;
		Result.PinSubCategoryObject = UObject::StaticClass();
		return Result;
	}

	FName MakeManagedFunctionInternalName(const FGuid& Id)
	{
		return FName(*FString::Printf(
			TEXT("USF_%s"),
			*Id.ToString(EGuidFormats::Digits).ToLower()));
	}

	const FName& EffectiveParameterName(const FFunctionParameterDeclaration& Parameter)
	{
		return Parameter.NativeName.IsNone() ? Parameter.Name : Parameter.NativeName;
	}

	const TCHAR* ParameterDirectionName(EFinareManagedParameterDirection Direction)
	{
		switch (Direction)
		{
		case EFinareManagedParameterDirection::Input: return TEXT("input");
		case EFinareManagedParameterDirection::Output: return TEXT("out");
		case EFinareManagedParameterDirection::InputOutput: return TEXT("ref");
		default: return TEXT("unknown");
		}
	}

	bool ResolveBlueprintOverrideFunction(
		const UBlueprint& Blueprint,
		const FFunctionDeclaration& Declaration,
		UFunction*& OutFunction,
		FString& OutError,
		const UClass* ProspectiveParentClass = nullptr)
	{
		OutFunction = nullptr;
		if (!Declaration.bBlueprintOverride)
		{
			return true;
		}

		UClass* DeclaringClass = LoadObject<UClass>(
			nullptr,
			*Declaration.BlueprintOverrideDeclaringTypePath);
		if (DeclaringClass == nullptr ||
			DeclaringClass->GetPathName() != Declaration.BlueprintOverrideDeclaringTypePath)
		{
			OutError = FString::Printf(
				TEXT("Blueprint override '%s' cannot load declaring class '%s'."),
				*Declaration.ManagedName.ToString(),
				*Declaration.BlueprintOverrideDeclaringTypePath);
			return false;
		}
		const UClass* EffectiveParentClass = ProspectiveParentClass != nullptr
			                                     ? ProspectiveParentClass
			                                     : Blueprint.ParentClass;
		if (EffectiveParentClass == nullptr ||
			!EffectiveParentClass->IsChildOf(DeclaringClass))
		{
			OutError = FString::Printf(
				TEXT("Blueprint override '%s' targets '%s', but Blueprint parent '%s' is not its child."),
				*Declaration.ManagedName.ToString(),
				*Declaration.BlueprintOverrideDeclaringTypePath,
				*GetPathNameSafe(EffectiveParentClass));
			return false;
		}

		UFunction* Function = DeclaringClass->FindFunctionByName(
			FName(*Declaration.BlueprintOverrideFunctionNameText),
			EIncludeSuperFlag::ExcludeSuper);
		if (Function == nullptr || Function->GetOuterUClass() != DeclaringClass)
		{
			OutError = FString::Printf(
				TEXT("Blueprint override '%s' requires function '%s' to be declared directly by '%s'."),
				*Declaration.ManagedName.ToString(),
				*Declaration.BlueprintOverrideFunctionNameText,
				*Declaration.BlueprintOverrideDeclaringTypePath);
			return false;
		}
		if (!Function->HasAnyFunctionFlags(FUNC_BlueprintEvent) ||
			Function->HasAnyFunctionFlags(FUNC_Final | FUNC_Static | FUNC_Net))
		{
			OutError = FString::Printf(
				TEXT(
					"Blueprint override '%s' targets '%s:%s', which must be an instance Blueprint event without Final, Static, or Net flags."),
				*Declaration.ManagedName.ToString(),
				*Declaration.BlueprintOverrideDeclaringTypePath,
				*Declaration.BlueprintOverrideFunctionNameText);
			return false;
		}
		OutFunction = Function;
		return true;
	}

	bool OverridePinTypesCompatible(
		const FEdGraphPinType& ManagedType,
		const FEdGraphPinType& NativeType)
	{
		FEdGraphPinType Managed = ManagedType;
		FEdGraphPinType Native = NativeType;

		// C# directions carry the managed ref contract, while the inherited
		// graph pin carries the native const/reference qualifiers. Compare the
		// value shape after adopting those native qualifiers.
		Managed.bIsReference = Native.bIsReference;
		Managed.bIsConst = Native.bIsConst;
		if (Managed == Native)
		{
			return true;
		}

		// Container element/value identities remain exact. Broad UObject handles
		// are allowed only on scalar pins, where the native property may refine
		// the class restriction to a child Actor/UClass type.
		if (Managed.IsContainer() || Native.IsContainer())
		{
			return false;
		}
		if (Managed.PinCategory != Native.PinCategory ||
			Managed.PinSubCategory != Native.PinSubCategory ||
			Managed.PinValueType != Native.PinValueType ||
			Managed.bIsWeakPointer != Native.bIsWeakPointer)
		{
			return false;
		}
		if (Managed.PinCategory != UEdGraphSchema_K2::PC_Object &&
			Managed.PinCategory != UEdGraphSchema_K2::PC_Class)
		{
			return false;
		}

		const UClass* ManagedClass = Cast<UClass>(Managed.PinSubCategoryObject.Get());
		const UClass* NativeClass = Cast<UClass>(Native.PinSubCategoryObject.Get());
		return ManagedClass != nullptr && NativeClass != nullptr &&
			NativeClass->IsChildOf(ManagedClass);
	}

	bool NativeParameterDirection(
		const FProperty& Property,
		EFinareManagedParameterDirection& OutDirection)
	{
		if (!Property.HasAnyPropertyFlags(CPF_Parm) ||
			Property.HasAnyPropertyFlags(CPF_ReturnParm))
		{
			return false;
		}
		if (Property.HasAnyPropertyFlags(CPF_ConstParm))
		{
			// Native const& parameters are read-only inputs even when a producer
			// happens to carry an additional OutParm bit.
			OutDirection = EFinareManagedParameterDirection::Input;
		}
		else if (!Property.HasAnyPropertyFlags(CPF_OutParm))
		{
			OutDirection = EFinareManagedParameterDirection::Input;
		}
		else if (Property.HasAnyPropertyFlags(CPF_ReferenceParm))
		{
			OutDirection = EFinareManagedParameterDirection::InputOutput;
		}
		else
		{
			OutDirection = EFinareManagedParameterDirection::Output;
		}
		return true;
	}

	bool ValidateBlueprintOverrideSignature(
		const UFunction& NativeFunction,
		FFunctionDeclaration& Declaration,
		FString& OutError)
	{
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		TArray<const FProperty*> NativeParameters;
		for (TFieldIterator<FProperty> It(
			     &NativeFunction,
			     EFieldIteratorFlags::ExcludeSuper);
		     It;
		     ++It)
		{
			if (It->HasAnyPropertyFlags(CPF_Parm) &&
				!It->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				NativeParameters.Add(*It);
			}
		}
		if (NativeParameters.Num() != Declaration.Parameters.Num())
		{
			OutError = FString::Printf(
				TEXT("Blueprint override '%s' parameter count %d does not match native '%s' count %d."),
				*Declaration.ManagedName.ToString(),
				Declaration.Parameters.Num(),
				*NativeFunction.GetName(),
				NativeParameters.Num());
			return false;
		}
		for (int32 Index = 0; Index < NativeParameters.Num(); ++Index)
		{
			const FProperty& NativeParameter = *NativeParameters[Index];
			FEdGraphPinType NativePinType;
			EFinareManagedParameterDirection NativeDirection;
			if (!Schema->ConvertPropertyToPinType(&NativeParameter, NativePinType) ||
				!NativeParameterDirection(NativeParameter, NativeDirection))
			{
				OutError = FString::Printf(
					TEXT("Blueprint override '%s' cannot convert native parameter '%s'."),
					*Declaration.ManagedName.ToString(),
					*NativeParameter.GetName());
				return false;
			}
			FFunctionParameterDeclaration& ManagedParameter = Declaration.Parameters[Index];
			if (ManagedParameter.Direction != NativeDirection ||
				ManagedParameter.Direction == EFinareManagedParameterDirection::InputOutput &&
				!ManagedParameter.PinType.bIsReference ||
				!OverridePinTypesCompatible(ManagedParameter.PinType, NativePinType))
			{
				OutError = FString::Printf(
					TEXT("Blueprint override '%s' parameter '%s' (%s) does not match native '%s' (%s)."),
					*Declaration.ManagedName.ToString(),
					*ManagedParameter.Name.ToString(),
					ParameterDirectionName(ManagedParameter.Direction),
					*NativeParameter.GetName(),
					ParameterDirectionName(NativeDirection));
				return false;
			}
			ManagedParameter.NativeName = NativeParameter.GetFName();
		}

		const FProperty* NativeReturn = NativeFunction.GetReturnProperty();
		if (Declaration.bHasReturnValue != (NativeReturn != nullptr))
		{
			OutError = FString::Printf(
				TEXT("Blueprint override '%s' return shape does not match native '%s'."),
				*Declaration.ManagedName.ToString(),
				*NativeFunction.GetName());
			return false;
		}
		if (NativeReturn != nullptr)
		{
			FEdGraphPinType NativeReturnType;
			if (!Schema->ConvertPropertyToPinType(NativeReturn, NativeReturnType) ||
				!OverridePinTypesCompatible(Declaration.ReturnPinType, NativeReturnType))
			{
				OutError = FString::Printf(
					TEXT("Blueprint override '%s' return type does not match native '%s'."),
					*Declaration.ManagedName.ToString(),
					*NativeFunction.GetName());
				return false;
			}
		}
		return true;
	}

	const FKismetUserDeclaredFunctionMetadata* GetFunctionEntryMetadata(
		const UEdGraph& Graph)
	{
		TArray<UK2Node_FunctionEntry*> EntryNodes;
		Graph.GetNodesOfClass(EntryNodes);
		return EntryNodes.Num() == 1 ? &EntryNodes[0]->MetaData : nullptr;
	}

	bool HasManagedFunctionGraphMetadata(const UEdGraph& Graph)
	{
		const FKismetUserDeclaredFunctionMetadata* Metadata =
			GetFunctionEntryMetadata(Graph);
		return Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
			Metadata->GetMetaData(ManagedFunctionKey) == TEXT("true");
	}

	const FFinareManagedFunctionBinding* FindPersistedManagedFunctionBinding(
		const UBlueprint& Blueprint,
		const UEdGraph& Graph)
	{
		const UFinareBlueprintGeneratedClass* GeneratedClass =
			Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		const FFinareManagedFunctionBinding* Binding = GeneratedClass != nullptr
			                                               ? GeneratedClass->ManagedFunctionBindings.Find(
				                                               Graph.GetFName())
			                                               : nullptr;
		if (Binding == nullptr || !Binding->FunctionId.IsValid())
		{
			return nullptr;
		}

		// Interface implementation graphs are reconstructed by Kismet during
		// package load, and UE does not retain their entry-node metadata. The
		// product GeneratedClass binding is the durable ownership record for that
		// special graph kind. Ordinary function graphs additionally retain their
		// semantic GraphGuid so a stale binding cannot claim a user graph by name.
		return Binding->bInterfaceFunction || Graph.GraphGuid == Binding->FunctionId
			       ? Binding
			       : nullptr;
	}

	bool IsManagedFunctionGraph(
		const UBlueprint& Blueprint,
		const UEdGraph& Graph)
	{
		return HasManagedFunctionGraphMetadata(Graph) ||
			FindPersistedManagedFunctionBinding(Blueprint, Graph) != nullptr;
	}

	bool TryReadManagedFunctionGraphId(
		const UBlueprint& Blueprint,
		const UEdGraph& Graph,
		FGuid& OutId)
	{
		const FKismetUserDeclaredFunctionMetadata* Metadata =
			GetFunctionEntryMetadata(Graph);
		if (HasManagedFunctionGraphMetadata(Graph))
		{
			return Metadata != nullptr && Metadata->HasMetaData(FunctionIdKey) &&
				ParseSemanticId(Metadata->GetMetaData(FunctionIdKey), OutId);
		}
		if (const FFinareManagedFunctionBinding* Binding =
			FindPersistedManagedFunctionBinding(Blueprint, Graph))
		{
			OutId = Binding->FunctionId;
			return true;
		}
		return false;
	}

	UEdGraph* FindManagedFunctionGraphById(UBlueprint& Blueprint, const FGuid& Id)
	{
		TArray<UEdGraph*> Graphs;
		Graphs.Reserve(Blueprint.FunctionGraphs.Num() + Blueprint.ImplementedInterfaces.Num() * 4);
		for (UEdGraph* Graph : Blueprint.FunctionGraphs)
		{
			Graphs.AddUnique(Graph);
		}
		for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
		{
			for (UEdGraph* Graph : Interface.Graphs)
			{
				Graphs.AddUnique(Graph);
			}
		}
		for (UEdGraph* Graph : Graphs)
		{
			FGuid CandidateId;
			if (Graph != nullptr && IsManagedFunctionGraph(Blueprint, *Graph) &&
				TryReadManagedFunctionGraphId(Blueprint, *Graph, CandidateId) &&
				CandidateId == Id)
			{
				return Graph;
			}
		}
		return nullptr;
	}

	bool IsInterfaceGraph(const UBlueprint& Blueprint, const UEdGraph* Candidate)
	{
		if (Candidate == nullptr)
		{
			return false;
		}
		for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
		{
			if (Interface.Graphs.Contains(Candidate))
			{
				return true;
			}
		}
		return false;
	}

	UEdGraph* FindFunctionGraphByName(const UBlueprint& Blueprint, FName Name)
	{
		for (UEdGraph* Graph : Blueprint.FunctionGraphs)
		{
			if (Graph != nullptr && Graph->GetFName() == Name)
			{
				return Graph;
			}
		}
		for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
		{
			for (UEdGraph* Graph : Interface.Graphs)
			{
				if (Graph != nullptr && Graph->GetFName() == Name)
				{
					return Graph;
				}
			}
		}
		return nullptr;
	}

	UK2Node_Event* FindUserOwnedOverrideEvent(
		const UBlueprint& Blueprint,
		FName FunctionName)
	{
		for (UEdGraph* Ubergraph : Blueprint.UbergraphPages)
		{
			if (Ubergraph == nullptr)
			{
				continue;
			}
			TArray<UK2Node_Event*> EventNodes;
			Ubergraph->GetNodesOfClass(EventNodes);
			for (UK2Node_Event* EventNode : EventNodes)
			{
				if (EventNode == nullptr || !EventNode->bOverrideFunction ||
					EventNode->IsAutomaticallyPlacedGhostNode() ||
					EventNode->EventReference.GetMemberName() != FunctionName)
				{
					continue;
				}
				UFunction* Signature = EventNode->FindEventSignatureFunction();
				if (Signature != nullptr && Signature->GetFName() == FunctionName)
				{
					return EventNode;
				}
			}
		}
		return nullptr;
	}

	bool FunctionGraphSignatureMatches(
		const UEdGraph& Graph,
		const FFunctionDeclaration& Declaration)
	{
		if (Graph.GetFName() != Declaration.InternalName ||
			Graph.GraphGuid != Declaration.Id)
		{
			return false;
		}
		TArray<UK2Node_FunctionEntry*> EntryNodes;
		TArray<UK2Node_FunctionResult*> ResultNodes;
		Graph.GetNodesOfClass(EntryNodes);
		Graph.GetNodesOfClass(ResultNodes);
		const int32 ParameterOffset = Declaration.bIsStatic ? 1 : 0;
		int32 InputParameterCount = 0;
		int32 OutputParameterCount = 0;
		for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
		{
			if (Parameter.Direction ==
				EFinareManagedParameterDirection::Output)
			{
				++OutputParameterCount;
			}
			else
			{
				++InputParameterCount;
			}
		}
		if (EntryNodes.Num() != 1 || ResultNodes.Num() != 1 ||
			EntryNodes[0]->UserDefinedPins.Num() !=
			InputParameterCount + ParameterOffset ||
			ResultNodes[0]->UserDefinedPins.Num() !=
			(Declaration.bHasReturnValue ? 1 : 0) + OutputParameterCount)
		{
			return false;
		}
		const int32 RelevantFlags = FUNC_Public | FUNC_BlueprintCallable | FUNC_Static |
			FUNC_Event | FUNC_BlueprintEvent | FUNC_Net | FUNC_NetServer | FUNC_NetClient | FUNC_NetMulticast |
			FUNC_NetReliable;
		const int32 ExpectedFlags = FUNC_Public | FUNC_BlueprintCallable | FinareRpcFlags(
				Declaration.RpcTarget, Declaration.bReliable) |
			(Declaration.bIsStatic ? FUNC_Static : FUNC_None) |
			(Declaration.bBlueprintEvent
				 ? static_cast<int32>(FUNC_Event | FUNC_BlueprintEvent)
				 : FUNC_None);
		if ((EntryNodes[0]->GetExtraFlags() & RelevantFlags) != ExpectedFlags)
		{
			return false;
		}
		if (Declaration.bIsStatic)
		{
			const TSharedPtr<FUserPinInfo>& WorldContextPin =
				EntryNodes[0]->UserDefinedPins[0];
			if (!WorldContextPin.IsValid() ||
				WorldContextPin->PinName != WorldContextObjectName ||
				WorldContextPin->PinType != MakeWorldContextPinType() ||
				WorldContextPin->DesiredPinDirection != EGPD_Output)
			{
				return false;
			}
		}
		int32 EntryParameterIndex = ParameterOffset;
		int32 ResultParameterIndex = 0;
		for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
		{
			const bool bOutputOnly = Parameter.Direction ==
				EFinareManagedParameterDirection::Output;
			const TSharedPtr<FUserPinInfo>& Pin = bOutputOnly
				                                      ? ResultNodes[0]->UserDefinedPins[ResultParameterIndex++]
				                                      : EntryNodes[0]->UserDefinedPins[EntryParameterIndex++];
			if (!Pin.IsValid() || Pin->PinName != Parameter.Name ||
				Pin->PinType != Parameter.PinType ||
				Pin->DesiredPinDirection != (bOutputOnly ? EGPD_Input : EGPD_Output))
			{
				return false;
			}
		}
		if (Declaration.bHasReturnValue)
		{
			const TSharedPtr<FUserPinInfo>& Pin =
				ResultNodes[0]->UserDefinedPins[OutputParameterCount];
			if (!Pin.IsValid() || Pin->PinName != UEdGraphSchema_K2::PN_ReturnValue ||
				Pin->PinType != Declaration.ReturnPinType ||
				Pin->DesiredPinDirection != EGPD_Input)
			{
				return false;
			}
		}
		return true;
	}

	bool OverrideFunctionGraphSignatureMatches(
		const UBlueprint& Blueprint,
		const UEdGraph& Graph,
		const FFunctionDeclaration& Declaration)
	{
		UFunction* NativeFunction = nullptr;
		FString UnusedError;
		if (!ResolveBlueprintOverrideFunction(
				Blueprint,
				Declaration,
				NativeFunction,
				UnusedError) ||
			NativeFunction == nullptr ||
			Graph.GetFName() != Declaration.InternalName ||
			Graph.GraphGuid != Declaration.Id)
		{
			return false;
		}

		TArray<UK2Node_FunctionEntry*> EntryNodes;
		TArray<UK2Node_FunctionResult*> ResultNodes;
		Graph.GetNodesOfClass(EntryNodes);
		Graph.GetNodesOfClass(ResultNodes);
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		const int32 RelevantFlags = FUNC_Public | FUNC_BlueprintCallable |
			FUNC_Event | FUNC_BlueprintEvent | FUNC_Const;
		int32 ExpectedFlags = FUNC_Public | FUNC_BlueprintCallable |
			FUNC_Event | FUNC_BlueprintEvent;
		if (NativeFunction->HasAnyFunctionFlags(FUNC_Const))
		{
			ExpectedFlags |= FUNC_Const;
		}
		if (EntryNodes.Num() != 1 ||
			EntryNodes[0]->FunctionReference.GetMemberName() !=
			NativeFunction->GetFName() ||
			EntryNodes[0]->FunctionReference.GetMemberParentClass() !=
			NativeFunction->GetOuterUClass() ||
			(EntryNodes[0]->GetExtraFlags() & RelevantFlags) != ExpectedFlags)
		{
			return false;
		}

		int32 ExpectedEntryPins = 0;
		int32 ExpectedResultPins = 0;
		for (TFieldIterator<FProperty> It(
			     NativeFunction,
			     EFieldIteratorFlags::ExcludeSuper);
		     It;
		     ++It)
		{
			if (!It->HasAnyPropertyFlags(CPF_Parm) ||
				It->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			EFinareManagedParameterDirection NativeDirection;
			if (!NativeParameterDirection(**It, NativeDirection))
			{
				return false;
			}
			const bool bInput = NativeDirection !=
				EFinareManagedParameterDirection::Output;
			if (bInput)
			{
				++ExpectedEntryPins;
			}
			else
			{
				++ExpectedResultPins;
			}
		}
		if (NativeFunction->GetReturnProperty() != nullptr)
		{
			++ExpectedResultPins;
		}
		auto CountDataPins = [](const UK2Node* Node)
		{
			return Node == nullptr
				       ? 0
				       : Node->Pins.Num() - Node->Pins.ContainsByPredicate(
					       [](const UEdGraphPin* Pin)
					       {
						       return Pin != nullptr && Pin->PinType.PinCategory ==
							       UEdGraphSchema_K2::PC_Exec;
					       });
		};
		if (CountDataPins(EntryNodes[0]) != ExpectedEntryPins ||
			ResultNodes.Num() != (ExpectedResultPins > 0 ? 1 : 0) ||
			(ExpectedResultPins > 0 &&
				CountDataPins(ResultNodes[0]) != ExpectedResultPins))
		{
			return false;
		}

		int32 EntryIndex = 0;
		int32 ResultIndex = 0;
		for (TFieldIterator<FProperty> It(
			     NativeFunction,
			     EFieldIteratorFlags::ExcludeSuper);
		     It;
		     ++It)
		{
			if (!It->HasAnyPropertyFlags(CPF_Parm) ||
				It->HasAnyPropertyFlags(CPF_ReturnParm))
			{
				continue;
			}
			FEdGraphPinType NativePinType;
			if (!Schema->ConvertPropertyToPinType(*It, NativePinType))
			{
				return false;
			}
			EFinareManagedParameterDirection NativeDirection;
			if (!NativeParameterDirection(**It, NativeDirection))
			{
				return false;
			}
			const bool bInput = NativeDirection !=
				EFinareManagedParameterDirection::Output;
			const FName ExpectedName = It->GetFName();
			UEdGraphPin* Pin = bInput
				                   ? EntryNodes[0]->FindPin(ExpectedName, EGPD_Output)
				                   : ResultNodes[0]->FindPin(ExpectedName, EGPD_Input);
			if (Pin == nullptr || Pin->PinName != ExpectedName ||
				Pin->PinType != NativePinType ||
				Pin->Direction != (bInput ? EGPD_Output : EGPD_Input))
			{
				return false;
			}
		}
		if (const FProperty* NativeReturn = NativeFunction->GetReturnProperty())
		{
			FEdGraphPinType NativeReturnType;
			if (!Schema->ConvertPropertyToPinType(NativeReturn, NativeReturnType) ||
				ResultNodes.Num() != 1)
			{
				return false;
			}
			UEdGraphPin* ReturnPin = ResultNodes[0]->FindPin(
				UEdGraphSchema_K2::PN_ReturnValue,
				EGPD_Input);
			if (ReturnPin == nullptr ||
				ReturnPin->PinType != NativeReturnType ||
				ReturnPin->Direction != EGPD_Input)
			{
				return false;
			}
		}
		return true;
	}

	bool RuntimeValueTypeBindingMatches(
		const FFinareManagedValueTypeBinding& Binding,
		const FFunctionValueTypeDeclaration& Declaration)
	{
		if (Binding.UnrealTypePath != Declaration.UnrealTypePath ||
			Binding.StructFields.Num() != Declaration.StructFieldPaths.Num())
		{
			return false;
		}
		for (int32 Index = 0; Index < Binding.StructFields.Num(); ++Index)
		{
			if (Binding.StructFields[Index].Segments !=
				Declaration.StructFieldPaths[Index])
			{
				return false;
			}
		}
		return true;
	}

	bool RuntimeFunctionBindingContractMatches(
		const FFinareManagedFunctionBinding& Binding,
		const FFunctionDeclaration& Declaration)
	{
		if (!Binding.ManagedName.Equals(
				Declaration.ManagedNameText,
				ESearchCase::CaseSensitive) ||
			Binding.AbiVersion != 3 ||
			Binding.bStatic != Declaration.bIsStatic ||
			Binding.RpcTarget != Declaration.RpcTarget || Binding.bReliable != Declaration.bReliable ||
			Binding.RepNotifyProperty != Declaration.RepNotifyProperty ||
			Binding.bBlueprintEvent != Declaration.bBlueprintEvent ||
			Binding.bInterfaceFunction != Declaration.bInterfaceFunction ||
			Binding.BlueprintEventSuperMode != Declaration.BlueprintEventSuperMode ||
			!Binding.BlueprintOverrideDeclaringTypePath.Equals(
				Declaration.BlueprintOverrideDeclaringTypePath,
				ESearchCase::CaseSensitive) ||
			!Binding.BlueprintOverrideFunctionName.Equals(
				Declaration.BlueprintOverrideFunctionNameText,
				ESearchCase::CaseSensitive) ||
			Binding.ParameterTypes.Num() != Declaration.Parameters.Num() ||
			Binding.ParameterNames.Num() != Declaration.Parameters.Num() ||
			Binding.ParameterDirections.Num() != Declaration.Parameters.Num() ||
			!RuntimeValueTypeBindingMatches(
				Binding.ReturnType,
				Declaration.ReturnValueType))
		{
			return false;
		}
		for (int32 Index = 0; Index < Binding.ParameterTypes.Num(); ++Index)
		{
			if (Binding.ParameterNames[Index] !=
				EffectiveParameterName(Declaration.Parameters[Index]) ||
				Binding.ParameterDirections[Index] !=
				Declaration.Parameters[Index].Direction ||
				!RuntimeValueTypeBindingMatches(
					Binding.ParameterTypes[Index],
					Declaration.Parameters[Index].ValueType))
			{
				return false;
			}
		}
		return true;
	}

	bool RuntimeFunctionBindingMatches(
		const FFinareManagedFunctionBinding& Binding,
		const FFunctionDeclaration& Declaration)
	{
		return Binding.FunctionId == Declaration.Id &&
			RuntimeFunctionBindingContractMatches(Binding, Declaration);
	}

	FString BlueprintOverrideRouteForDiagnostics(
		const FString& DeclaringTypePath,
		const FString& FunctionName)
	{
		return DeclaringTypePath + TEXT(":") + FunctionName;
	}

	FString LiveBlueprintOverrideContractError(
		const UBlueprint& Blueprint,
		const FString& Route,
		const FString& Reason)
	{
		return FString::Printf(
			TEXT("Cannot synchronize live Blueprint '%s': override route '%s' %s. "
				"Stop the affected instance, synchronize the Script Class, and restart it "
				"to apply an override contract change."),
			*Blueprint.GetPathName(),
			*Route,
			*Reason);
	}

	bool ValidateLiveBlueprintOverrideContract(
		const UBlueprint& Blueprint,
		const UFinareBlueprintGeneratedClass& GeneratedClass,
		const TArray<FFunctionDeclaration>& FunctionDeclarations,
		FString& OutError)
	{
		TArray<const FFinareManagedFunctionBinding*> PersistedOverrides;
		for (const TPair<FName, FFinareManagedFunctionBinding>& Pair :
		     GeneratedClass.ManagedFunctionBindings)
		{
			const FFinareManagedFunctionBinding& Binding = Pair.Value;
			const bool bHasDeclaringType = !Binding.BlueprintOverrideDeclaringTypePath.IsEmpty();
			const bool bHasFunctionName = !Binding.BlueprintOverrideFunctionName.IsEmpty();
			if (bHasDeclaringType != bHasFunctionName)
			{
				OutError = LiveBlueprintOverrideContractError(
					Blueprint,
					BlueprintOverrideRouteForDiagnostics(
						Binding.BlueprintOverrideDeclaringTypePath,
						Binding.BlueprintOverrideFunctionName),
					TEXT("has an incomplete persisted route"));
				return false;
			}
			if (bHasDeclaringType)
			{
				PersistedOverrides.Add(&Binding);
			}
		}

		TArray<const FFunctionDeclaration*> IncomingOverrides;
		for (const FFunctionDeclaration& Declaration : FunctionDeclarations)
		{
			if (Declaration.bBlueprintOverride)
			{
				IncomingOverrides.Add(&Declaration);
			}
		}

		TSet<const FFunctionDeclaration*> MatchedIncoming;
		for (const FFinareManagedFunctionBinding* Binding : PersistedOverrides)
		{
			const FFunctionDeclaration* ByManagedName = nullptr;
			for (const FFunctionDeclaration* Declaration : IncomingOverrides)
			{
				if (Binding->ManagedName.Equals(
					Declaration->ManagedNameText,
					ESearchCase::CaseSensitive))
				{
					ByManagedName = Declaration;
					break;
				}
			}

			const FString PersistedRoute = BlueprintOverrideRouteForDiagnostics(
				Binding->BlueprintOverrideDeclaringTypePath,
				Binding->BlueprintOverrideFunctionName);
			if (ByManagedName == nullptr)
			{
				const FFunctionDeclaration* SameRoute = nullptr;
				for (const FFunctionDeclaration* Declaration : IncomingOverrides)
				{
					if (Binding->BlueprintOverrideDeclaringTypePath.Equals(
							Declaration->BlueprintOverrideDeclaringTypePath,
							ESearchCase::CaseSensitive) &&
						Binding->BlueprintOverrideFunctionName.Equals(
							Declaration->BlueprintOverrideFunctionNameText,
							ESearchCase::CaseSensitive))
					{
						SameRoute = Declaration;
						break;
					}
				}
				OutError = LiveBlueprintOverrideContractError(
					Blueprint,
					SameRoute != nullptr
						? BlueprintOverrideRouteForDiagnostics(
							SameRoute->BlueprintOverrideDeclaringTypePath,
							SameRoute->BlueprintOverrideFunctionNameText)
						: PersistedRoute,
					SameRoute != nullptr
						? TEXT("changed its managed method identity")
						: TEXT("was removed from the declaration"));
				return false;
			}

			const FString IncomingRoute = BlueprintOverrideRouteForDiagnostics(
				ByManagedName->BlueprintOverrideDeclaringTypePath,
				ByManagedName->BlueprintOverrideFunctionNameText);
			if (!Binding->BlueprintOverrideDeclaringTypePath.Equals(
					ByManagedName->BlueprintOverrideDeclaringTypePath,
					ESearchCase::CaseSensitive) ||
				!Binding->BlueprintOverrideFunctionName.Equals(
					ByManagedName->BlueprintOverrideFunctionNameText,
					ESearchCase::CaseSensitive))
			{
				OutError = LiveBlueprintOverrideContractError(
					Blueprint,
					IncomingRoute,
					FString::Printf(
						TEXT("retargeted the managed method from '%s'"),
						*PersistedRoute));
				return false;
			}
			if (!RuntimeFunctionBindingContractMatches(*Binding, *ByManagedName))
			{
				OutError = LiveBlueprintOverrideContractError(
					Blueprint,
					IncomingRoute,
					TEXT("changed its managed method, super policy, parameter mapping, direction, "
						"or return/nested type contract"));
				return false;
			}
			MatchedIncoming.Add(ByManagedName);
		}

		for (const FFunctionDeclaration* Declaration : IncomingOverrides)
		{
			if (!MatchedIncoming.Contains(Declaration))
			{
				const FString Route = BlueprintOverrideRouteForDiagnostics(
					Declaration->BlueprintOverrideDeclaringTypePath,
					Declaration->BlueprintOverrideFunctionNameText);
				OutError = LiveBlueprintOverrideContractError(
					Blueprint,
					Route,
					TEXT("was added to the declaration"));
				return false;
			}
		}
		return true;
	}

	bool FunctionGraphMatches(
		const UBlueprint& Blueprint,
		const UEdGraph& Graph,
		const FFunctionDeclaration& Declaration)
	{
		if (Declaration.bBlueprintOverride
			    ? !OverrideFunctionGraphSignatureMatches(Blueprint, Graph, Declaration)
			    : !FunctionGraphSignatureMatches(Graph, Declaration))
		{
			return false;
		}
		// Inherited function entries are not editable, so the editor helper does
		// not expose their metadata. Read the sole entry directly; this is also
		// the ownership marker persisted by ConfigureFunctionGraph.
		const FKismetUserDeclaredFunctionMetadata* Metadata =
			GetFunctionEntryMetadata(Graph);
		const bool bMetadataMatches =
			Metadata != nullptr && Metadata->HasMetaData(ManagedFunctionKey) &&
			Metadata->GetMetaData(ManagedFunctionKey).Equals(
				TEXT("true"), ESearchCase::CaseSensitive) &&
			Metadata->HasMetaData(FunctionIdKey) &&
			Metadata->GetMetaData(FunctionIdKey).Equals(
				Declaration.SemanticId, ESearchCase::CaseSensitive) &&
			Metadata->HasMetaData(ManagedFunctionNameKey) &&
			Metadata->GetMetaData(ManagedFunctionNameKey).Equals(
				Declaration.ManagedNameText, ESearchCase::CaseSensitive) &&
			Metadata->HasMetaData(TEXT("DisplayName")) &&
			Metadata->GetMetaData(TEXT("DisplayName")).Equals(
				Declaration.ManagedNameText, ESearchCase::CaseSensitive) &&
			Metadata->HasMetaData(BlueprintEventKey) &&
			Metadata->GetMetaData(BlueprintEventKey).Equals(
				Declaration.bBlueprintEvent ? TEXT("true") : TEXT("false"),
				ESearchCase::CaseSensitive) &&
			Metadata->HasMetaData(BlueprintEventSuperModeKey) &&
			Metadata->GetMetaData(BlueprintEventSuperModeKey).Equals(
				Declaration.bBlueprintEvent ? TEXT("explicit") : TEXT("none"),
				ESearchCase::CaseSensitive) &&
			(!Declaration.bBlueprintOverride ||
				(Metadata->HasMetaData(BlueprintOverrideDeclaringTypePathKey) &&
					Metadata->GetMetaData(BlueprintOverrideDeclaringTypePathKey).Equals(
						Declaration.BlueprintOverrideDeclaringTypePath,
						ESearchCase::CaseSensitive) &&
					Metadata->HasMetaData(BlueprintOverrideFunctionNameKey) &&
					Metadata->GetMetaData(BlueprintOverrideFunctionNameKey).Equals(
						Declaration.BlueprintOverrideFunctionNameText,
						ESearchCase::CaseSensitive))) &&
			(Declaration.bBlueprintOverride ||
				(!Metadata->HasMetaData(BlueprintOverrideDeclaringTypePathKey) &&
					!Metadata->HasMetaData(BlueprintOverrideFunctionNameKey))) &&
			(!Declaration.bIsStatic ||
				(Metadata->HasMetaData(TEXT("WorldContext")) &&
					Metadata->GetMetaData(TEXT("WorldContext")).Equals(
						WorldContextObjectName.ToString(), ESearchCase::CaseSensitive)));
		if (bMetadataMatches)
		{
			return true;
		}

		// UE reconstructs interface and inherited graphs from their native owner
		// on load and can drop their user-declared function metadata. The
		// serialized generated class binding carries the same semantic identity
		// and complete ABI shape, so it is the no-change authority for these
		// graph kinds.
		const FFinareManagedFunctionBinding* PersistedBinding =
			FindPersistedManagedFunctionBinding(Blueprint, Graph);
		return (Declaration.bInterfaceFunction || Declaration.bBlueprintOverride) &&
			PersistedBinding != nullptr &&
			RuntimeFunctionBindingMatches(*PersistedBinding, Declaration);
	}

	bool ValidateInterfaceFunctionSignature(
		const FFunctionDeclaration& Declaration,
		FString& OutError)
	{
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		for (const FString& InterfacePath : Declaration.InterfacePaths)
		{
			UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
			UFunction* Function = InterfaceClass != nullptr
				                      ? InterfaceClass->FindFunctionByName(
					                      Declaration.InternalName,
					                      EIncludeSuperFlag::IncludeSuper)
				                      : nullptr;
			if (InterfaceClass == nullptr ||
				!InterfaceClass->HasAnyClassFlags(CLASS_Interface) ||
				Function == nullptr ||
				!Function->HasAnyFunctionFlags(FUNC_BlueprintEvent))
			{
				OutError = FString::Printf(
					TEXT("Managed interface function '%s' cannot resolve Blueprint event '%s:%s'."),
					*Declaration.ManagedName.ToString(),
					*InterfacePath,
					*Declaration.InternalName.ToString());
				return false;
			}

			TArray<const FProperty*> NativeParameters;
			for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
			{
				const FProperty* Property = *It;
				if (Property->HasAnyPropertyFlags(CPF_Parm) &&
					!Property->HasAnyPropertyFlags(CPF_ReturnParm))
				{
					NativeParameters.Add(Property);
				}
			}
			if (NativeParameters.Num() != Declaration.Parameters.Num())
			{
				OutError = FString::Printf(
					TEXT("Managed interface function '%s' has %d parameters; native '%s:%s' has %d."),
					*Declaration.ManagedName.ToString(),
					Declaration.Parameters.Num(),
					*InterfacePath,
					*Declaration.InternalName.ToString(),
					NativeParameters.Num());
				return false;
			}
			for (int32 Index = 0; Index < NativeParameters.Num(); ++Index)
			{
				FEdGraphPinType NativePinType;
				if (!Schema->ConvertPropertyToPinType(NativeParameters[Index], NativePinType) ||
					NativePinType != Declaration.Parameters[Index].PinType)
				{
					OutError = FString::Printf(
						TEXT("Managed interface function '%s' parameter %d does not match native '%s:%s'."),
						*Declaration.ManagedName.ToString(),
						Index,
						*InterfacePath,
						*Declaration.InternalName.ToString());
					return false;
				}
			}

			const FProperty* NativeReturn = Function->GetReturnProperty();
			if (Declaration.bHasReturnValue != (NativeReturn != nullptr))
			{
				OutError = FString::Printf(
					TEXT("Managed interface function '%s' return shape does not match native '%s:%s'."),
					*Declaration.ManagedName.ToString(),
					*InterfacePath,
					*Declaration.InternalName.ToString());
				return false;
			}
			if (NativeReturn != nullptr)
			{
				FEdGraphPinType NativeReturnType;
				if (!Schema->ConvertPropertyToPinType(NativeReturn, NativeReturnType) ||
					NativeReturnType != Declaration.ReturnPinType)
				{
					OutError = FString::Printf(
						TEXT("Managed interface function '%s' return type does not match native '%s:%s'."),
						*Declaration.ManagedName.ToString(),
						*InterfacePath,
						*Declaration.InternalName.ToString());
					return false;
				}
			}
		}
		return true;
	}

	bool ReadCanonicalDefault(
		const FString& StateName,
		const FString& TypeKind,
		const FString& MetadataName,
		const FFunctionValueTypeDeclaration* UnrealType,
		const TSharedPtr<FJsonValue>& Value,
		FString& OutDefault,
		FString& OutError)
	{
		if (!Value.IsValid())
		{
			OutError = FString::Printf(
				TEXT("State '%s' is missing its default for '%s'."),
				*StateName,
				*MetadataName);
			return false;
		}

		if (Value->IsNull())
		{
			if ((UnrealType != nullptr && (UnrealType->Kind == EFunctionValueTypeKind::Object ||
				UnrealType->Kind == EFunctionValueTypeKind::Interface)) || MetadataName == TEXT("Finare.Unreal.UnrealObjectHandle") ||
				MetadataName == TEXT("Finare.Unreal.UnrealClassHandle"))
			{
				OutDefault = TEXT("None");
				return true;
			}
			OutError = FString::Printf(
				TEXT("State '%s' requires a non-null default for '%s'."),
				*StateName,
				*MetadataName);
			return false;
		}

		if (MetadataName == TEXT("System.Boolean") && Value->Type == EJson::Boolean)
		{
			OutDefault = Value->AsBool() ? TEXT("true") : TEXT("false");
			return true;
		}
		if (MetadataName == TEXT("System.String") && Value->Type == EJson::String)
		{
			OutDefault = Value->AsString();
			return true;
		}
		if ((MetadataName == TEXT("Finare.Unreal.UnrealName") ||
				MetadataName == TEXT("Finare.Unreal.UnrealText")) &&
			Value->Type == EJson::String)
		{
			OutDefault = Value->AsString();
			return true;
		}
		if ((MetadataName == TEXT("Finare.Unreal.UnrealSoftObjectHandle") ||
				MetadataName == TEXT("Finare.Unreal.UnrealSoftClassHandle")) &&
			Value->Type == EJson::String &&
			(UnrealType == nullptr || UnrealType->Kind == EFunctionValueTypeKind::None))
		{
			const FString PathText = Value->AsString();
			if (FinareSoftObjectPath::IsValid(PathText))
			{
				const FSoftObjectPath Path(PathText);
				if (PathText.IsEmpty() || (Path.IsValid() && Path.ToString() == PathText))
				{
					OutDefault = PathText.IsEmpty()
						             ? TEXT("None")
						             : FString::Printf(TEXT("\"%s\""), *PathText);
					return true;
				}
			}
		}
		if (TypeKind == TEXT("enum") && UnrealType != nullptr &&
			UnrealType->Kind == EFunctionValueTypeKind::Enum && Value->Type == EJson::String)
		{
			int64 EnumValue = 0;
			UEnum* Enum = LoadObject<UEnum>(nullptr, *UnrealType->UnrealTypePath);
			FString EnumName;
			if (Enum != nullptr && Enum->GetPathName() == UnrealType->UnrealTypePath &&
				Enum->GetName() == UnrealType->UnrealNativeName &&
				LexTryParseString(EnumValue, *Value->AsString()) &&
				IsBlueprintEnumValue(EnumValue) &&
				TryCanonicalEnumName(Enum, EnumValue, EnumName))
			{
				OutDefault = MoveTemp(EnumName);
				return true;
			}
		}
		if (MetadataName == TEXT("Finare.Unreal.UnrealVector") &&
			Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject> Object = Value->AsObject();
			double X = 0.0;
			double Y = 0.0;
			double Z = 0.0;
			if (Object.IsValid() && Object->Values.Num() == 3 &&
				Object->TryGetNumberField(TEXT("x"), X) && FMath::IsFinite(X) &&
				Object->TryGetNumberField(TEXT("y"), Y) && FMath::IsFinite(Y) &&
				Object->TryGetNumberField(TEXT("z"), Z) && FMath::IsFinite(Z))
			{
				OutDefault = FString::Printf(
					TEXT("(X=%s,Y=%s,Z=%s)"),
					*LexToString(X),
					*LexToString(Y),
					*LexToString(Z));
				return true;
			}
		}
		if (MetadataName == TEXT("Finare.Unreal.UnrealRotator") &&
			Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject> Object = Value->AsObject();
			double Pitch = 0.0;
			double Yaw = 0.0;
			double Roll = 0.0;
			if (Object.IsValid() && Object->Values.Num() == 3 &&
				Object->TryGetNumberField(TEXT("pitch"), Pitch) && FMath::IsFinite(Pitch) &&
				Object->TryGetNumberField(TEXT("yaw"), Yaw) && FMath::IsFinite(Yaw) &&
				Object->TryGetNumberField(TEXT("roll"), Roll) && FMath::IsFinite(Roll))
			{
				OutDefault = FString::Printf(
					TEXT("(Pitch=%s,Yaw=%s,Roll=%s)"),
					*LexToString(Pitch), *LexToString(Yaw), *LexToString(Roll));
				return true;
			}
		}
		if (MetadataName == TEXT("Finare.Unreal.UnrealTransform") &&
			Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject> Object = Value->AsObject();
			const TSharedPtr<FJsonObject>* Rotation = nullptr;
			const TSharedPtr<FJsonObject>* Translation = nullptr;
			const TSharedPtr<FJsonObject>* Scale = nullptr;
			double X = 0.0;
			double Y = 0.0;
			double Z = 0.0;
			double W = 0.0;
			double TX = 0.0;
			double TY = 0.0;
			double TZ = 0.0;
			double SX = 0.0;
			double SY = 0.0;
			double SZ = 0.0;
			if (Object.IsValid() && Object->Values.Num() == 3 &&
				Object->TryGetObjectField(TEXT("rotation"), Rotation) &&
				Object->TryGetObjectField(TEXT("translation"), Translation) &&
				Object->TryGetObjectField(TEXT("scale3D"), Scale) &&
				Rotation != nullptr && Translation != nullptr && Scale != nullptr &&
				(*Rotation)->Values.Num() == 4 && (*Translation)->Values.Num() == 3 &&
				(*Scale)->Values.Num() == 3 &&
				(*Rotation)->TryGetNumberField(TEXT("x"), X) && FMath::IsFinite(X) &&
				(*Rotation)->TryGetNumberField(TEXT("y"), Y) && FMath::IsFinite(Y) &&
				(*Rotation)->TryGetNumberField(TEXT("z"), Z) && FMath::IsFinite(Z) &&
				(*Rotation)->TryGetNumberField(TEXT("w"), W) && FMath::IsFinite(W) &&
				(*Translation)->TryGetNumberField(TEXT("x"), TX) && FMath::IsFinite(TX) &&
				(*Translation)->TryGetNumberField(TEXT("y"), TY) && FMath::IsFinite(TY) &&
				(*Translation)->TryGetNumberField(TEXT("z"), TZ) && FMath::IsFinite(TZ) &&
				(*Scale)->TryGetNumberField(TEXT("x"), SX) && FMath::IsFinite(SX) &&
				(*Scale)->TryGetNumberField(TEXT("y"), SY) && FMath::IsFinite(SY) &&
				(*Scale)->TryGetNumberField(TEXT("z"), SZ) && FMath::IsFinite(SZ) &&
				FMath::IsNearlyEqual(X * X + Y * Y + Z * Z + W * W, 1.0, 1e-6))
			{
				// BlueprintEditorUtils parses FTransform defaults through
				// FTransform::InitFromString, whose stable text form is
				// translation|rotator|scale.  Keep quaternion JSON/ABI intact;
				// the editor-facing default uses the engine's required Euler
				// representation and the CDO reader below uses the same form.
				const FRotator Rotator(FQuat(X, Y, Z, W));
				OutDefault = FString::Printf(
					TEXT("%s,%s,%s|%s,%s,%s|%s,%s,%s"),
					*LexToString(TX), *LexToString(TY), *LexToString(TZ),
					*LexToString(Rotator.Pitch), *LexToString(Rotator.Yaw),
					*LexToString(Rotator.Roll), *LexToString(SX),
					*LexToString(SY), *LexToString(SZ));
				return true;
			}
		}
		if (Value->Type != EJson::Number)
		{
			OutError = FString::Printf(
				TEXT("State '%s' has a default incompatible with '%s'."),
				*StateName,
				*MetadataName);
			return false;
		}

		double Number = 0.0;
		if (!ParseJsonDouble(Value, Number))
		{
			OutError = FString::Printf(TEXT("State '%s' has a non-finite default."), *StateName);
			return false;
		}
		if (MetadataName == TEXT("System.Byte"))
		{
			int64 Integer = 0;
			if (!ParseJsonInt64(Value, Integer) || Integer < 0 || Integer > MAX_uint8)
			{
				OutError = FString::Printf(TEXT("State '%s' default is outside System.Byte."), *StateName);
				return false;
			}
			OutDefault = LexToString(static_cast<uint8>(Integer));
			return true;
		}
		if (MetadataName == TEXT("System.Int32"))
		{
			int64 Integer = 0;
			if (!ParseJsonInt64(Value, Integer) || Integer < MIN_int32 || Integer > MAX_int32)
			{
				OutError = FString::Printf(TEXT("State '%s' default is outside System.Int32."), *StateName);
				return false;
			}
			OutDefault = LexToString(static_cast<int32>(Integer));
			return true;
		}
		if (MetadataName == TEXT("System.Int64"))
		{
			int64 Integer = 0;
			if (!ParseJsonInt64(Value, Integer))
			{
				OutError = FString::Printf(TEXT("State '%s' default is outside System.Int64."), *StateName);
				return false;
			}
			OutDefault = LexToString(Integer);
			return true;
		}
		if (MetadataName == TEXT("System.Single"))
		{
			const float Converted = static_cast<float>(Number);
			if (!FMath::IsFinite(Converted))
			{
				OutError = FString::Printf(TEXT("State '%s' default is outside System.Single."), *StateName);
				return false;
			}
			OutDefault = LexToString(Converted);
			return true;
		}
		if (MetadataName == TEXT("System.Double"))
		{
			OutDefault = LexToString(Number);
			return true;
		}

		OutError = FString::Printf(
			TEXT("State '%s' uses unsupported Blueprint state type '%s'."),
			*StateName,
			*MetadataName);
		return false;
	}

	bool ReadRequiredString(
		const TSharedPtr<FJsonObject>& Object,
		const TCHAR* Field,
		FString& OutValue,
		FString& OutError)
	{
		if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue))
		{
			OutError = FString::Printf(TEXT("Declaration requires string field '%s'."), Field);
			return false;
		}
		OutValue.TrimStartAndEndInline();
		if (OutValue.IsEmpty())
		{
			OutError = FString::Printf(TEXT("Declaration field '%s' cannot be empty."), Field);
			return false;
		}
		return true;
	}

	bool ParseFunctionValueType(
		const TSharedPtr<FJsonObject>& Owner,
		const TCHAR* Field,
		FFunctionValueTypeDeclaration& OutType,
		FString& OutError,
		int32 Depth = 0);

	bool AppendFunctionValueType(
		const TSharedPtr<FJsonObject>& Object,
		bool bTopLevel,
		TArray<FName>& Prefix,
		FFunctionValueTypeDeclaration& OutType,
		FString& OutError,
		int32 Depth)
	{
		FString Kind;
		FString UnrealTypePath;
		FString UnrealNativeName;
		const TArray<TSharedPtr<FJsonValue>>* Fields = nullptr;
		if (Depth > 16 || !ReadRequiredString(Object, TEXT("kind"), Kind, OutError))
		{
			OutError = TEXT("Function type nesting exceeds the supported depth or has no kind.");
			return false;
		}
		if (Kind == TEXT("array") || Kind == TEXT("set") || Kind == TEXT("map"))
		{
			const bool bMap = Kind == TEXT("map");
			const FString NativeName = bMap ? TEXT("TMap") : Kind == TEXT("array") ? TEXT("TArray") : TEXT("TSet");
			const TArray<TSharedPtr<FJsonValue>>* Arguments = nullptr;
			if (!Object->TryGetStringField(TEXT("unrealTypePath"), UnrealTypePath) ||
				!UnrealTypePath.IsEmpty() ||
				!ReadRequiredString(Object, TEXT("unrealNativeName"), UnrealNativeName, OutError) ||
				UnrealNativeName != NativeName ||
				!Object->TryGetArrayField(TEXT("fields"), Fields) || !Fields->IsEmpty() ||
				!Object->TryGetArrayField(TEXT("typeArguments"), Arguments) ||
				Arguments->Num() != (bMap ? 2 : 1))
			{
				OutError = TEXT("Container function type has invalid identity or type arguments.");
				return false;
			}
			if (bTopLevel)
			{
				OutType.Kind = bMap
					               ? EFunctionValueTypeKind::Map
					               : Kind == TEXT("array")
					               ? EFunctionValueTypeKind::Array
					               : EFunctionValueTypeKind::Set;
				OutType.UnrealNativeName = NativeName;
			}
			for (int32 Index = 0; Index < Arguments->Num(); ++Index)
			{
				const TSharedPtr<FJsonObject> Argument = (*Arguments)[Index].IsValid()
					                                         ? (*Arguments)[Index]->AsObject()
					                                         : nullptr;
				FString MetadataName;
				FFunctionValueTypeDeclaration Child;
				if (!ReadRequiredString(Argument, TEXT("typeMetadataName"), MetadataName, OutError) ||
					!ParseFunctionValueType(Argument, TEXT("unrealType"), Child, OutError, Depth + 1))
				{
					return false;
				}
				// Reserved segments cannot collide with native reflected field names.
				Prefix.Add(FName(bMap ? (Index == 0 ? TEXT("$key") : TEXT("$value")) : TEXT("$element")));
				OutType.StructFieldPaths.Add(Prefix);
				for (const TArray<FName>& ChildPath : Child.StructFieldPaths)
				{
					TArray<FName> Path = Prefix;
					Path.Append(ChildPath);
					OutType.StructFieldPaths.Add(MoveTemp(Path));
				}
				Prefix.Pop();
				if (bTopLevel)
				{
					OutType.ArgumentMetadataNames.Add(MetadataName);
					OutType.TypeArguments.Add(MoveTemp(Child));
				}
			}
			return true;
		}
		if (
			!ReadRequiredString(Object, TEXT("unrealTypePath"), UnrealTypePath, OutError) ||
			!ReadRequiredString(Object, TEXT("unrealNativeName"), UnrealNativeName, OutError) ||
			!Object->TryGetArrayField(TEXT("fields"), Fields) || Fields == nullptr)
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("Function Unreal value type requires a fields array.");
			}
			return false;
		}

		const EFunctionValueTypeKind ParsedKind = Kind == TEXT("enum")
			                                          ? EFunctionValueTypeKind::Enum
			                                          : Kind == TEXT("struct")
			                                          ? EFunctionValueTypeKind::Struct
			                                          : Kind == TEXT("object") ? EFunctionValueTypeKind::Object
			                                          : Kind == TEXT("interface") ? EFunctionValueTypeKind::Interface
			                                          : EFunctionValueTypeKind::None;
		if (ParsedKind == EFunctionValueTypeKind::None ||
			!UnrealTypePath.StartsWith(TEXT("/Script/")) ||
			Object->HasField(TEXT("typeArguments")) ||
			(ParsedKind != EFunctionValueTypeKind::Struct && !Fields->IsEmpty()))
		{
			OutError = FString::Printf(
				TEXT("Function Unreal value type '%s' has an invalid kind, path, or field contract."),
				*UnrealTypePath);
			return false;
		}
		if (bTopLevel)
		{
			OutType.Kind = ParsedKind;
			OutType.UnrealTypePath = UnrealTypePath;
			OutType.UnrealNativeName = UnrealNativeName;
		}

		if (ParsedKind != EFunctionValueTypeKind::Struct)
		{
			return true;
		}
		for (int32 Index = 0; Index < Fields->Num(); ++Index)
		{
			const TSharedPtr<FJsonObject> Field = (*Fields)[Index].IsValid()
				                                      ? (*Fields)[Index]->AsObject()
				                                      : nullptr;
			FString NativeName;
			double DeclaredIndex = -1.0;
			if (!ReadRequiredString(Field, TEXT("nativeName"), NativeName, OutError) ||
				!Field->TryGetNumberField(TEXT("index"), DeclaredIndex) ||
				DeclaredIndex != static_cast<double>(Index))
			{
				if (OutError.IsEmpty())
				{
					OutError = FString::Printf(
						TEXT("Function struct '%s' has invalid field identity at index %d."),
						*UnrealTypePath,
						Index);
				}
				return false;
			}
			Prefix.Add(FName(*NativeName));
			OutType.StructFieldPaths.Add(Prefix);
			const TSharedPtr<FJsonObject>* Nested = nullptr;
			if (Field->HasField(TEXT("unrealType")))
			{
				if (!Field->TryGetObjectField(TEXT("unrealType"), Nested) ||
					Nested == nullptr || !Nested->IsValid() ||
					!AppendFunctionValueType(*Nested, false, Prefix, OutType, OutError, Depth + 1))
				{
					Prefix.Pop();
					return false;
				}
			}
			Prefix.Pop();
		}
		return true;
	}

	bool ParseFunctionValueType(
		const TSharedPtr<FJsonObject>& Owner,
		const TCHAR* Field,
		FFunctionValueTypeDeclaration& OutType,
		FString& OutError,
		int32 Depth)
	{
		OutType = FFunctionValueTypeDeclaration();
		if (!Owner.IsValid() || !Owner->HasField(Field))
		{
			return true;
		}
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (!Owner->TryGetObjectField(Field, Object) || Object == nullptr || !Object->IsValid())
		{
			OutError = FString::Printf(TEXT("Function value type field '%s' must be an object."), Field);
			return false;
		}
		TArray<FName> Prefix;
		return AppendFunctionValueType(*Object, true, Prefix, OutType, OutError, Depth);
	}

	FFinareManagedValueTypeBinding MakeRuntimeValueTypeBinding(
		const FFunctionValueTypeDeclaration& Declaration)
	{
		FFinareManagedValueTypeBinding Result;
		// UObject wire cells remain handles. The generated FProperty owns the exact class/interface constraint.
		if (Declaration.Kind != EFunctionValueTypeKind::Object && Declaration.Kind != EFunctionValueTypeKind::Interface)
			Result.UnrealTypePath = Declaration.UnrealTypePath;
		Result.StructFields.Reserve(Declaration.StructFieldPaths.Num());
		for (const TArray<FName>& Path : Declaration.StructFieldPaths)
		{
			FFinareManagedStructFieldPath& Field = Result.StructFields.AddDefaulted_GetRef();
			Field.Segments = Path;
		}
		return Result;
	}

	bool ParseGameplayBindings(
		EFinareClassKind ClassKind,
		const TArray<TSharedPtr<FJsonValue>>& Functions,
		TArray<FInputActionDeclaration>& OutInputBindings,
		TArray<FDelegateBindingDeclaration>& OutDelegateBindings,
		TArray<FInterfaceFunctionDeclaration>& OutInterfaceBindings,
		TMap<FGuid, FName>& OutInterfaceFunctionNames,
		FString& OutError)
	{
		OutInputBindings.Reset();
		OutDelegateBindings.Reset();
		OutInterfaceBindings.Reset();
		OutInterfaceFunctionNames.Reset();
		const bool bGameplayClass = ClassKind == EFinareClassKind::Actor ||
			ClassKind == EFinareClassKind::ActorComponent;
		TSet<FGuid> SeenBindingIds;
		TSet<FString> SeenRoutes;

		for (const TSharedPtr<FJsonValue>& FunctionValue : Functions)
		{
			const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
				                                         ? FunctionValue->AsObject()
				                                         : nullptr;
			FString FunctionIdText;
			FString ManagedNameText;
			bool bIsStatic = false;
			bool bIsPublic = false;
			bool bIsEvent = false;
			if (!ReadRequiredString(Function, TEXT("id"), FunctionIdText, OutError) ||
				!ReadRequiredString(Function, TEXT("name"), ManagedNameText, OutError) ||
				!Function->TryGetBoolField(TEXT("isStatic"), bIsStatic) ||
				!Function->TryGetBoolField(TEXT("isPublic"), bIsPublic) ||
				!Function->TryGetBoolField(TEXT("isEvent"), bIsEvent))
			{
				if (OutError.IsEmpty())
				{
					OutError = TEXT("Gameplay bindings require complete function identity and flags.");
				}
				return false;
			}
			FGuid FunctionId;
			const FName ManagedName(*ManagedNameText);
			if (!ParseSemanticId(FunctionIdText, FunctionId) || ManagedName.IsNone())
			{
				OutError = FString::Printf(
					TEXT("Gameplay binding function '%s' has an invalid stable identity."),
					*ManagedNameText);
				return false;
			}

			const TArray<TSharedPtr<FJsonValue>>* InputActions = nullptr;
			const TArray<TSharedPtr<FJsonValue>>* DelegateBindings = nullptr;
			const TArray<TSharedPtr<FJsonValue>>* InterfaceFunctions = nullptr;
			if ((Function->HasField(TEXT("inputActions")) &&
					(!Function->TryGetArrayField(TEXT("inputActions"), InputActions) ||
						InputActions == nullptr)) ||
				(Function->HasField(TEXT("delegateBindings")) &&
					(!Function->TryGetArrayField(TEXT("delegateBindings"), DelegateBindings) ||
						DelegateBindings == nullptr)) ||
				(Function->HasField(TEXT("interfaceFunctions")) &&
					(!Function->TryGetArrayField(TEXT("interfaceFunctions"), InterfaceFunctions) ||
						InterfaceFunctions == nullptr)))
			{
				OutError = FString::Printf(
					TEXT("Gameplay binding arrays for '%s' are invalid."),
					*ManagedNameText);
				return false;
			}
			const bool bHasBindings =
				(InputActions != nullptr && !InputActions->IsEmpty()) ||
				(DelegateBindings != nullptr && !DelegateBindings->IsEmpty()) ||
				(InterfaceFunctions != nullptr && !InterfaceFunctions->IsEmpty());
			if (bHasBindings && !bGameplayClass)
			{
				OutError = FString::Printf(
					TEXT("Only Actor and ActorComponent Script Classes can bind gameplay method '%s'."),
					*ManagedNameText);
				return false;
			}

			if (InputActions != nullptr)
			{
				if (!InputActions->IsEmpty() && (!bIsPublic || bIsStatic || bIsEvent))
				{
					OutError = FString::Printf(
						TEXT("Enhanced Input handler '%s' must be a public instance non-delegate method."),
						*ManagedNameText);
					return false;
				}
				for (const TSharedPtr<FJsonValue>& BindingValue : *InputActions)
				{
					const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
						                                        ? BindingValue->AsObject()
						                                        : nullptr;
					FString BindingIdText;
					FString ActionPath;
					FString TriggerEvent;
					FString ValueType;
					bool bConsumeInput = false;
					if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
						!ReadRequiredString(Binding, TEXT("actionPath"), ActionPath, OutError) ||
						!ReadRequiredString(Binding, TEXT("triggerEvent"), TriggerEvent, OutError) ||
						!ReadRequiredString(Binding, TEXT("valueType"), ValueType, OutError) ||
						!Binding->TryGetBoolField(TEXT("consumeInput"), bConsumeInput))
					{
						return false;
					}
					FInputActionDeclaration Declaration;
					Declaration.ManagedName = ManagedName;
					Declaration.ActionPath = ActionPath;
					Declaration.bConsumeInput = bConsumeInput;
					if (TriggerEvent == TEXT("started"))
					{
						Declaration.TriggerEvent = EFinareManagedInputTriggerEvent::Started;
					}
					else if (TriggerEvent == TEXT("ongoing"))
					{
						Declaration.TriggerEvent = EFinareManagedInputTriggerEvent::Ongoing;
					}
					else if (TriggerEvent == TEXT("triggered"))
					{
						Declaration.TriggerEvent = EFinareManagedInputTriggerEvent::Triggered;
					}
					else if (TriggerEvent == TEXT("completed"))
					{
						Declaration.TriggerEvent = EFinareManagedInputTriggerEvent::Completed;
					}
					else if (TriggerEvent == TEXT("canceled"))
					{
						Declaration.TriggerEvent = EFinareManagedInputTriggerEvent::Canceled;
					}
					else
					{
						OutError = FString::Printf(
							TEXT("Enhanced Input handler '%s' has unsupported trigger '%s'."),
							*ManagedNameText,
							*TriggerEvent);
						return false;
					}
					if (ValueType == TEXT("boolean"))
					{
						Declaration.ValueType = EFinareManagedInputValueType::Boolean;
					}
					else if (ValueType == TEXT("axis1D"))
					{
						Declaration.ValueType = EFinareManagedInputValueType::Axis1D;
					}
					else if (ValueType == TEXT("axis2D"))
					{
						Declaration.ValueType = EFinareManagedInputValueType::Axis2D;
					}
					else if (ValueType == TEXT("axis3D"))
					{
						Declaration.ValueType = EFinareManagedInputValueType::Axis3D;
					}
					else
					{
						OutError = FString::Printf(
							TEXT("Enhanced Input handler '%s' has unsupported value type '%s'."),
							*ManagedNameText,
							*ValueType);
						return false;
					}
					const FString Route = FString::Printf(
						TEXT("input|%s|%s|%s"),
						*ManagedNameText,
						*ActionPath,
						*TriggerEvent);
					if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
						ActionPath.IsEmpty() || SeenBindingIds.Contains(Declaration.BindingId) ||
						SeenRoutes.Contains(Route))
					{
						OutError = FString::Printf(
							TEXT("Enhanced Input handler '%s' has an invalid or duplicate binding."),
							*ManagedNameText);
						return false;
					}
					SeenBindingIds.Add(Declaration.BindingId);
					SeenRoutes.Add(Route);
					OutInputBindings.Add(MoveTemp(Declaration));
				}
			}

			if (DelegateBindings != nullptr)
			{
				if (!DelegateBindings->IsEmpty() && (bIsStatic || !bIsEvent))
				{
					OutError = FString::Printf(
						TEXT("Delegate handler '%s' must be an instance native-event method."),
						*ManagedNameText);
					return false;
				}
				for (const TSharedPtr<FJsonValue>& BindingValue : *DelegateBindings)
				{
					const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
						                                        ? BindingValue->AsObject()
						                                        : nullptr;
					FString BindingIdText;
					FString SourceTypePath;
					FString EventPath;
					FString Source;
					if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
						!ReadRequiredString(Binding, TEXT("sourceTypePath"), SourceTypePath, OutError) ||
						!ReadRequiredString(Binding, TEXT("eventPath"), EventPath, OutError) ||
						!ReadRequiredString(Binding, TEXT("source"), Source, OutError))
					{
						return false;
					}
					FDelegateBindingDeclaration Declaration;
					Declaration.ManagedName = ManagedName;
					Declaration.SourceTypePath = SourceTypePath;
					Declaration.EventPath = EventPath;
					if (Source == TEXT("target"))
					{
						Declaration.Source = EFinareManagedDelegateSource::Target;
					}
					else if (Source == TEXT("host"))
					{
						Declaration.Source = EFinareManagedDelegateSource::Host;
					}
					else
					{
						OutError = FString::Printf(
							TEXT("Delegate handler '%s' has unsupported source '%s'."),
							*ManagedNameText,
							*Source);
						return false;
					}
					const TSharedPtr<FJsonValue> SourceMember =
						Binding->TryGetField(TEXT("sourceMember"));
					if (SourceMember.IsValid() && !SourceMember->IsNull() &&
						!SourceMember->TryGetString(Declaration.SourceMember))
					{
						OutError = FString::Printf(
							TEXT("Delegate handler '%s' sourceMember must be a string or null."),
							*ManagedNameText);
						return false;
					}
					const FString Route = FString::Printf(
						TEXT("delegate|%s|%s|%s|%s"),
						*ManagedNameText,
						*SourceTypePath,
						*EventPath,
						*Source);
					if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
						SourceTypePath.IsEmpty() || EventPath.IsEmpty() ||
						SeenBindingIds.Contains(Declaration.BindingId) ||
						SeenRoutes.Contains(Route))
					{
						OutError = FString::Printf(
							TEXT("Delegate handler '%s' has an invalid or duplicate binding."),
							*ManagedNameText);
						return false;
					}
					SeenBindingIds.Add(Declaration.BindingId);
					SeenRoutes.Add(Route);
					OutDelegateBindings.Add(MoveTemp(Declaration));
				}
			}

			if (InterfaceFunctions != nullptr)
			{
				if (!InterfaceFunctions->IsEmpty() && (!bIsPublic || bIsStatic || bIsEvent))
				{
					OutError = FString::Printf(
						TEXT("Interface implementation '%s' must be a public instance non-delegate method."),
						*ManagedNameText);
					return false;
				}
				for (const TSharedPtr<FJsonValue>& BindingValue : *InterfaceFunctions)
				{
					const TSharedPtr<FJsonObject> Binding = BindingValue.IsValid()
						                                        ? BindingValue->AsObject()
						                                        : nullptr;
					FString BindingIdText;
					FString InterfacePath;
					FString FunctionNameText;
					if (!ReadRequiredString(Binding, TEXT("id"), BindingIdText, OutError) ||
						!ReadRequiredString(Binding, TEXT("interfacePath"), InterfacePath, OutError) ||
						!ReadRequiredString(Binding, TEXT("functionName"), FunctionNameText, OutError))
					{
						return false;
					}
					FInterfaceFunctionDeclaration Declaration;
					Declaration.InterfacePath = InterfacePath;
					Declaration.FunctionName = FName(*FunctionNameText);
					Declaration.ManagedName = ManagedName;
					Declaration.FunctionId = FunctionId;
					const FString Route = FString::Printf(
						TEXT("interface|%s|%s"),
						*InterfacePath,
						*FunctionNameText);
					if (!ParseSemanticId(BindingIdText, Declaration.BindingId) ||
						!InterfacePath.StartsWith(TEXT("/Script/")) ||
						Declaration.FunctionName.IsNone() ||
						SeenBindingIds.Contains(Declaration.BindingId) ||
						SeenRoutes.Contains(Route))
					{
						OutError = FString::Printf(
							TEXT("Interface implementation '%s' has an invalid or duplicate binding."),
							*ManagedNameText);
						return false;
					}
					if (const FName* ExistingName = OutInterfaceFunctionNames.Find(FunctionId);
						ExistingName != nullptr && *ExistingName != Declaration.FunctionName)
					{
						OutError = FString::Printf(
							TEXT("Managed method '%s' cannot implement differently named native interface functions."),
							*ManagedNameText);
						return false;
					}
					OutInterfaceFunctionNames.Add(FunctionId, Declaration.FunctionName);
					SeenBindingIds.Add(Declaration.BindingId);
					SeenRoutes.Add(Route);
					OutInterfaceBindings.Add(MoveTemp(Declaration));
				}
			}
		}
		return true;
	}

	bool ParseDeclaration(
		UFinareAsset& ScriptAsset,
		const UBlueprint& Blueprint,
		const UClass* ProspectiveParentClass,
		const FString& DeclarationJson,
		TArray<FStateDeclaration>& OutStates,
		TArray<FFunctionDeclaration>& OutFunctions,
		TArray<FLifecycleDeclaration>& OutLifecycleHooks,
		TArray<FInputActionDeclaration>& OutInputBindings,
		TArray<FDelegateBindingDeclaration>& OutDelegateBindings,
		TArray<FInterfaceFunctionDeclaration>& OutInterfaceBindings,
		TOptional<FFinareClassBindingDeclaration>& OutClassBinding,
		FString& OutError)
	{
		OutClassBinding.Reset();
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
		if (!FJsonSerializer::Deserialize(
			Reader, Root, FJsonSerializer::EFlags::StoreNumbersAsStrings) || !Root.IsValid())
		{
			OutError = FString::Printf(
				TEXT("Script Class declaration JSON is invalid: %s"),
				*Reader->GetErrorMessage());
			return false;
		}

		FString ParsedSchema;
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetStringField(TEXT("schemaVersion"), ParsedSchema) ||
			(!ParsedSchema.Equals(LegacySchemaVersion, ESearchCase::CaseSensitive) &&
				!ParsedSchema.Equals(OverrideSchemaVersion, ESearchCase::CaseSensitive) &&
				!ParsedSchema.Equals(ClassBindingSchemaVersion, ESearchCase::CaseSensitive)) ||
			!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr ||
			Assets->IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("Script Class declaration requires schema '%s', '%s', or '%s' and a non-empty assets array."),
				LegacySchemaVersion,
				OverrideSchemaVersion,
				ClassBindingSchemaVersion);
			return false;
		}

		if (!ValidateFinareClassBindingsJson(*Assets, ParsedSchema, OutError))
		{
			return false;
		}

		if (ParsedSchema.Equals(LegacySchemaVersion, ESearchCase::CaseSensitive))
		{
			for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
			{
				const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
					                                      ? AssetValue->AsObject()
					                                      : nullptr;
				const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
				if (!Asset.IsValid() ||
					!Asset->TryGetArrayField(TEXT("functions"), Functions) ||
					Functions == nullptr)
				{
					continue;
				}
				for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
				{
					const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
						                                         ? FunctionValue->AsObject()
						                                         : nullptr;
					const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
					const TSharedPtr<FJsonObject>* Override = nullptr;
					if (Function.IsValid() &&
						Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) &&
						BlueprintEvent != nullptr && BlueprintEvent->IsValid() &&
						(*BlueprintEvent)->TryGetObjectField(TEXT("override"), Override) &&
						Override != nullptr && Override->IsValid())
					{
						OutError = FString::Printf(
							TEXT(
								"Script Class declaration schema '%s' cannot contain Blueprint override metadata; use '%s'."),
							LegacySchemaVersion,
							OverrideSchemaVersion);
						return false;
					}
				}
			}
		}

		FString RequestedAssetPath = ScriptAsset.DeclarationAssetPath;
		RequestedAssetPath.TrimStartAndEndInline();
		TSharedPtr<FJsonObject> SelectedAsset;
		FString SelectedAssetPath;
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Candidate = AssetValue.IsValid()
				                                          ? AssetValue->AsObject()
				                                          : nullptr;
			FString CandidatePath;
			if (!Candidate.IsValid() ||
				!Candidate->TryGetStringField(TEXT("assetPath"), CandidatePath))
			{
				OutError = TEXT("Script Class declaration contains an asset without assetPath.");
				return false;
			}
			if ((RequestedAssetPath.IsEmpty() && Assets->Num() == 1) ||
				CandidatePath == RequestedAssetPath)
			{
				SelectedAsset = Candidate;
				SelectedAssetPath = CandidatePath;
				break;
			}
		}
		if (!SelectedAsset.IsValid())
		{
			OutError = FString::Printf(
				TEXT("Script Class declaration does not contain assetPath '%s'."),
				*RequestedAssetPath);
			return false;
		}

		FString AssetIdText;
		FString QualifiedName;
		FGuid AssetId;
		if (!ReadRequiredString(SelectedAsset, TEXT("id"), AssetIdText, OutError) ||
			!ParseSemanticId(AssetIdText, AssetId) ||
			!ReadRequiredString(SelectedAsset, TEXT("qualifiedName"), QualifiedName, OutError))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("Script Class declaration has an invalid class identity.");
			}
			return false;
		}
		if (!ScriptAsset.ValidateDeclarationIdentity(AssetId, QualifiedName, OutError))
		{
			return false;
		}
		TOptional<FFinareClassBindingDeclaration> ClassBinding;
		if (!FFinareClassBindingDeclaration::ReadJson(
			SelectedAsset, ClassBinding, OutError))
		{
			return false;
		}
		// The existing generated owner must remain valid before a prospective
		// parent is considered.  BaseClassPath is deliberately validated in the
		// parent-change plan below; validating it against the old owner here
		// would reject the very reparent operation this materializer owns.
		TOptional<FFinareClassBindingDeclaration> ExistingOwnerBinding = ClassBinding;
		if (ExistingOwnerBinding.IsSet())
		{
			ExistingOwnerBinding->BaseClassPath.Reset();
		}
		if (!ScriptAsset.ValidateDeclarationClassBinding(ExistingOwnerBinding, OutError))
		{
			return false;
		}
		OutClassBinding = MoveTemp(ClassBinding);

		const TArray<TSharedPtr<FJsonValue>>* States = nullptr;
		const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
		if (!SelectedAsset->TryGetArrayField(TEXT("states"), States) || States == nullptr ||
			!SelectedAsset->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
		{
			OutError = TEXT("Script Class declaration requires states and functions arrays.");
			return false;
		}
		if (ScriptAsset.ScriptClassKind == EFinareClassKind::BlueprintFunctionLibrary &&
			!States->IsEmpty())
		{
			OutError = TEXT("BlueprintFunctionLibrary Script Classes cannot declare instance state.");
			return false;
		}

		FInstancedPropertyBag CollectionDefaults;
		bool bCollectionDefaultsMaterialized = false;

		TMap<FGuid, FName> InterfaceFunctionNames;
		if (!ParseGameplayBindings(
			ScriptAsset.ScriptClassKind,
			*Functions,
			OutInputBindings,
			OutDelegateBindings,
			OutInterfaceBindings,
			InterfaceFunctionNames,
			OutError))
		{
			return false;
		}

		const bool bGameplayLifecycle =
			ScriptAsset.ScriptClassKind == EFinareClassKind::Actor ||
			ScriptAsset.ScriptClassKind == EFinareClassKind::ActorComponent;
		TSet<FGuid> SeenIds;
		TSet<FName> SeenNames;
		OutStates.Reset();
		OutStates.Reserve(States->Num());
		for (const TSharedPtr<FJsonValue>& StateValue : *States)
		{
			const TSharedPtr<FJsonObject> State = StateValue.IsValid()
				                                      ? StateValue->AsObject()
				                                      : nullptr;
			FString SemanticId;
			FString Name;
			FString TypeKind;
			FString MetadataName;
			if (!ReadRequiredString(State, TEXT("id"), SemanticId, OutError) ||
				!ReadRequiredString(State, TEXT("name"), Name, OutError) ||
				!ReadRequiredString(State, TEXT("typeKind"), TypeKind, OutError) ||
				!ReadRequiredString(State, TEXT("typeMetadataName"), MetadataName, OutError))
			{
				return false;
			}

			FStateDeclaration Declaration;
			Declaration.SemanticId = SemanticId;
			Declaration.Name = FName(*Name);
			Declaration.TypeMetadataName = MetadataName;
			FFunctionValueTypeDeclaration StateUnrealType;
			if (!ParseSemanticId(SemanticId, Declaration.Id) ||
				Declaration.Name.IsNone() || SeenIds.Contains(Declaration.Id) ||
				SeenNames.Contains(Declaration.Name))
			{
				OutError = FString::Printf(
					TEXT("Script Class declaration has duplicate or invalid state '%s' (%s)."),
					*Name,
					*SemanticId);
				return false;
			}
			if (!ParseFunctionValueType(
				State,
				TEXT("unrealType"),
				StateUnrealType,
				OutError))
			{
				OutError = FString::Printf(
					TEXT(
						"State '%s' uses unsupported Blueprint state type '%s' (%s). Reviewed types are bool, byte, int, long, float, double, string, FName, FText, UObject/UClass handles, and FVector."),
					*Name,
					*TypeKind,
					*MetadataName);
				return false;
			}
			bool bHasStateValue = false;
			const bool bResolvedStateType =
			(StateUnrealType.Kind == EFunctionValueTypeKind::Array ||
				StateUnrealType.Kind == EFunctionValueTypeKind::Set ||
				StateUnrealType.Kind == EFunctionValueTypeKind::Map)
				? ResolveFunctionPinType(
					MetadataName,
					StateUnrealType,
					false,
					Declaration.PinType,
					bHasStateValue) && bHasStateValue
				: ResolvePinType(
					TypeKind,
					MetadataName,
					Declaration.PinType,
					&StateUnrealType);
			if (!bResolvedStateType)
			{
				OutError = FString::Printf(
					TEXT(
						"State '%s' uses unsupported Blueprint state type '%s' (%s). Reviewed types are bool, byte, int, long, float, double, string, FName, FText, UObject/UClass handles, and flat collections."),
					*Name,
					*TypeKind,
					*MetadataName);
				return false;
			}
			const bool bCollectionState =
				StateUnrealType.Kind == EFunctionValueTypeKind::Array ||
				StateUnrealType.Kind == EFunctionValueTypeKind::Set ||
				StateUnrealType.Kind == EFunctionValueTypeKind::Map;
			if (bCollectionState)
			{
				if (!bCollectionDefaultsMaterialized)
				{
					FString CollectionAssetPath;
					if (!FFinareDeclarationMaterializer::MaterializeJson(
						DeclarationJson,
						SelectedAssetPath,
						FInstancedPropertyBag(),
						CollectionDefaults,
						CollectionAssetPath,
						OutError))
					{
						return false;
					}
					bCollectionDefaultsMaterialized = true;
				}
				const FPropertyBagPropertyDesc* CollectionDesc =
					CollectionDefaults.FindPropertyDescByID(Declaration.Id);
				if (CollectionDesc == nullptr)
				{
					OutError = FString::Printf(
						TEXT("State '%s' collection default did not materialize."), *Name);
					return false;
				}
				const TValueOrError<FString, EPropertyBagResult> Serialized =
					CollectionDefaults.GetValueSerializedString(CollectionDesc->Name);
				if (!Serialized.IsValid())
				{
					OutError = FString::Printf(
						TEXT("State '%s' collection default could not be exported."), *Name);
					return false;
				}
				Declaration.DefaultValue = Serialized.GetValue();
			}
			else if (!ReadCanonicalDefault(
				Name,
				TypeKind,
				MetadataName,
				&StateUnrealType,
				State->TryGetField(TEXT("defaultValue")),
				Declaration.DefaultValue,
				OutError))
			{
				return false;
			}

			const TSharedPtr<FJsonObject>* Exposure = nullptr;
			bool bInstanceEditable = false;
			bool bSaveGame = false;
			bool bReplicated = false;
			if (!State->TryGetObjectField(TEXT("exposure"), Exposure) || Exposure == nullptr ||
				!(*Exposure)->TryGetBoolField(TEXT("instanceEditable"), bInstanceEditable) ||
				!(*Exposure)->TryGetBoolField(TEXT("saveGame"), bSaveGame) ||
				!(*Exposure)->TryGetBoolField(TEXT("replicated"), bReplicated))
			{
				OutError = FString::Printf(
					TEXT("State '%s' requires complete exposure metadata."),
					*Name);
				return false;
			}
			const TSharedPtr<FJsonValue> CategoryValue = (*Exposure)->TryGetField(TEXT("category"));
			if (CategoryValue.IsValid() && !CategoryValue->IsNull())
			{
				if (CategoryValue->Type != EJson::String)
				{
					OutError = FString::Printf(TEXT("State '%s' category must be a string or null."), *Name);
					return false;
				}
				Declaration.Category = CategoryValue->AsString();
				Declaration.Category.TrimStartAndEndInline();
				if (Declaration.Category.IsEmpty())
				{
					OutError = FString::Printf(TEXT("State '%s' category cannot be blank."), *Name);
					return false;
				}
			}
			if (Declaration.Category.IsEmpty())
			{
				Declaration.Category = DefaultCategory;
			}

			Declaration.PropertyFlags = CPF_Edit | CPF_BlueprintVisible | CPF_DisableEditOnInstance;
			if (bInstanceEditable)
			{
				Declaration.PropertyFlags &= ~static_cast<uint64>(CPF_DisableEditOnInstance);
			}
			if (bSaveGame)
			{
				Declaration.PropertyFlags |= CPF_SaveGame;
			}
			if (bReplicated)
			{
				Declaration.PropertyFlags |= CPF_Net;
			}
			FString Condition = TEXT("none");
			if ((*Exposure)->HasField(TEXT("replicationCondition")) &&
				!(*Exposure)->TryGetStringField(TEXT("replicationCondition"), Condition))
			{
				OutError = TEXT("State replicationCondition must be a named condition.");
				return false;
			}
			if (Condition == TEXT("none"))
			{
				Declaration.ReplicationCondition = COND_None;
			}
			else if (Condition == TEXT("initialOnly"))
			{
				Declaration.ReplicationCondition = COND_InitialOnly;
			}
			else if (Condition == TEXT("ownerOnly"))
			{
				Declaration.ReplicationCondition = COND_OwnerOnly;
			}
			else if (Condition == TEXT("skipOwner"))
			{
				Declaration.ReplicationCondition = COND_SkipOwner;
			}
			else if (Condition == TEXT("initialOrOwner"))
			{
				Declaration.ReplicationCondition = COND_InitialOrOwner;
			}
			else
			{
				OutError = TEXT("Unsupported state replication condition.");
				return false;
			}
			if ((!bReplicated && Declaration.ReplicationCondition != COND_None) || (bReplicated && !bGameplayLifecycle))
			{
				OutError = TEXT(
					"Replicated state requires an Actor/ActorComponent; conditions require replicated state.");
				return false;
			}

			SeenIds.Add(Declaration.Id);
			SeenNames.Add(Declaration.Name);
			OutStates.Add(MoveTemp(Declaration));
		}

		OutLifecycleHooks.Reset();
		const TArray<TSharedPtr<FJsonValue>>* LifecycleHooks = nullptr;
		if (SelectedAsset->HasField(TEXT("lifecycleHooks")) &&
			(!SelectedAsset->TryGetArrayField(TEXT("lifecycleHooks"), LifecycleHooks) ||
				LifecycleHooks == nullptr))
		{
			OutError = TEXT("Script Class lifecycleHooks must be an array.");
			return false;
		}
		if (LifecycleHooks != nullptr && !LifecycleHooks->IsEmpty() && !bGameplayLifecycle)
		{
			OutError = TEXT("Only Actor and ActorComponent Script Classes can declare gameplay lifecycle hooks.");
			return false;
		}
		TSet<FGuid> SeenLifecycleIds;
		TSet<EFinareManagedLifecycleHookKind> SeenLifecycleKinds;
		if (LifecycleHooks != nullptr)
		{
			OutLifecycleHooks.Reserve(LifecycleHooks->Num());
			for (const TSharedPtr<FJsonValue>& HookValue : *LifecycleHooks)
			{
				const TSharedPtr<FJsonObject> Hook = HookValue.IsValid()
					                                     ? HookValue->AsObject()
					                                     : nullptr;
				FString SemanticId;
				FString Kind;
				FString ManagedName;
				if (!ReadRequiredString(Hook, TEXT("id"), SemanticId, OutError) ||
					!ReadRequiredString(Hook, TEXT("kind"), Kind, OutError) ||
					!ReadRequiredString(Hook, TEXT("managedName"), ManagedName, OutError))
				{
					return false;
				}

				FLifecycleDeclaration Declaration;
				FString ExpectedName;
				if (Kind == TEXT("beginPlay"))
				{
					Declaration.Kind = EFinareManagedLifecycleHookKind::BeginPlay;
					ExpectedName = TEXT("OnBeginPlay");
				}
				else if (Kind == TEXT("tick"))
				{
					Declaration.Kind = EFinareManagedLifecycleHookKind::Tick;
					ExpectedName = TEXT("OnTick");
				}
				else if (Kind == TEXT("endPlay"))
				{
					Declaration.Kind = EFinareManagedLifecycleHookKind::EndPlay;
					ExpectedName = TEXT("OnEndPlay");
				}
				else
				{
					OutError = FString::Printf(
						TEXT("Lifecycle hook '%s' has an unsupported kind '%s'."),
						*ManagedName,
						*Kind);
					return false;
				}
				Declaration.ManagedName = FName(*ManagedName);
				if (!ParseSemanticId(SemanticId, Declaration.Id) ||
					Declaration.ManagedName.IsNone() || ManagedName != ExpectedName ||
					SeenLifecycleIds.Contains(Declaration.Id) ||
					SeenLifecycleKinds.Contains(Declaration.Kind))
				{
					OutError = FString::Printf(
						TEXT("Lifecycle hook '%s' has an invalid, duplicate, or mismatched stable identity."),
						*ManagedName);
					return false;
				}
				SeenLifecycleIds.Add(Declaration.Id);
				SeenLifecycleKinds.Add(Declaration.Kind);
				OutLifecycleHooks.Add(MoveTemp(Declaration));
			}
		}

		OutFunctions.Reset();
		const bool bStaticLibrary =
			ScriptAsset.ScriptClassKind == EFinareClassKind::BlueprintFunctionLibrary;
		if (ScriptAsset.ScriptClassKind != EFinareClassKind::Actor &&
			ScriptAsset.ScriptClassKind != EFinareClassKind::ActorComponent &&
			ScriptAsset.ScriptClassKind != EFinareClassKind::Object &&
			!bStaticLibrary)
		{
			return true;
		}

		TSet<FGuid> SeenFunctionIds;
		TSet<FName> SeenFunctionNames;
		TSet<FName> SeenInternalFunctionNames;
		OutFunctions.Reserve(Functions->Num());
		for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
		{
			const TSharedPtr<FJsonObject> Function = FunctionValue.IsValid()
				                                         ? FunctionValue->AsObject()
				                                         : nullptr;
			FString SemanticId;
			FString Name;
			FString ReturnType;
			bool bIsStatic = false;
			bool bIsPublic = false;
			bool bIsEvent = false;
			const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
			if (!ReadRequiredString(Function, TEXT("id"), SemanticId, OutError) ||
				!ReadRequiredString(Function, TEXT("name"), Name, OutError) ||
				!ReadRequiredString(
					Function,
					TEXT("returnTypeMetadataName"),
					ReturnType,
					OutError) ||
				!Function->TryGetBoolField(TEXT("isStatic"), bIsStatic) ||
				!Function->TryGetBoolField(TEXT("isPublic"), bIsPublic) ||
				!Function->TryGetBoolField(TEXT("isEvent"), bIsEvent) ||
				!Function->TryGetArrayField(TEXT("parameters"), Parameters) ||
				Parameters == nullptr)
			{
				if (OutError.IsEmpty())
				{
					OutError = FString::Printf(
						TEXT(
							"Function '%s' requires isStatic, isPublic, isEvent, and parameters. Rebuild its Declaration IR with the current SDK."),
						*Name);
				}
				return false;
			}
			if (!bIsPublic || bIsEvent)
			{
				const TSharedPtr<FJsonObject>* InvalidBlueprintEvent = nullptr;
				const TSharedPtr<FJsonObject>* InvalidOverride = nullptr;
				if (Function.IsValid() &&
					Function->TryGetObjectField(TEXT("blueprintEvent"), InvalidBlueprintEvent) &&
					InvalidBlueprintEvent != nullptr && InvalidBlueprintEvent->IsValid() &&
					(*InvalidBlueprintEvent)->TryGetObjectField(TEXT("override"), InvalidOverride) &&
					InvalidOverride != nullptr && InvalidOverride->IsValid())
				{
					OutError = FString::Printf(
						TEXT(
							"Blueprint override '%s' must be a public instance method and cannot be an event declaration."),
						*Name);
					return false;
				}
				continue;
			}

			const TSharedPtr<FJsonObject>* BlueprintEvent = nullptr;
			const TSharedPtr<FJsonObject>* BlueprintOverride = nullptr;
			FString BlueprintOverrideDeclaringTypePath;
			FString BlueprintOverrideFunctionNameText;
			FName BlueprintOverrideFunctionName;
			bool bBlueprintOverride = false;
			if (Function->HasField(TEXT("blueprintEvent")))
			{
				FString SuperMode;
				if (!Function->TryGetObjectField(TEXT("blueprintEvent"), BlueprintEvent) ||
					BlueprintEvent == nullptr || !BlueprintEvent->IsValid() ||
					!ReadRequiredString(*BlueprintEvent, TEXT("superMode"), SuperMode, OutError) ||
					SuperMode != TEXT("explicit"))
				{
					OutError = FString::Printf(
						TEXT("Blueprint event '%s' requires superMode 'explicit'."),
						*Name);
					return false;
				}
				if (!bGameplayLifecycle || bIsStatic)
				{
					OutError = FString::Printf(
						TEXT("Blueprint event '%s' must be an Actor or ActorComponent instance method."),
						*Name);
					return false;
				}
				if ((*BlueprintEvent)->HasField(TEXT("override")))
				{
					if ((!ParsedSchema.Equals(OverrideSchemaVersion, ESearchCase::CaseSensitive) &&
							!ParsedSchema.Equals(ClassBindingSchemaVersion, ESearchCase::CaseSensitive)) ||
						!(*BlueprintEvent)->TryGetObjectField(
							TEXT("override"),
							BlueprintOverride) ||
						BlueprintOverride == nullptr ||
						!BlueprintOverride->IsValid() ||
						!ReadRequiredString(
							*BlueprintOverride,
							TEXT("declaringTypePath"),
							BlueprintOverrideDeclaringTypePath,
							OutError))
					{
						OutError = FString::Printf(
							TEXT(
								"Blueprint override '%s' requires schema '%s' or '%s' and declaringTypePath/functionName metadata."),
							*Name,
							OverrideSchemaVersion,
							ClassBindingSchemaVersion);
						return false;
					}
					if (!ReadRequiredString(
						*BlueprintOverride,
						TEXT("functionName"),
						BlueprintOverrideFunctionNameText,
						OutError))
					{
						return false;
					}
					BlueprintOverrideFunctionName = FName(*BlueprintOverrideFunctionNameText);
					if (BlueprintOverrideFunctionName.IsNone())
					{
						OutError = FString::Printf(
							TEXT("Blueprint override '%s' has an invalid functionName."),
							*Name);
						return false;
					}
					bBlueprintOverride = true;
				}
			}
			if (bIsStatic != bStaticLibrary)
			{
				OutError = bStaticLibrary
					           ? FString::Printf(
						           TEXT(
							           "Function '%s' is not static. BlueprintFunctionLibrary Script Classes own only static managed methods."),
						           *Name)
					           : FString::Printf(
						           TEXT(
							           "Function '%s' is static. Actor and ActorComponent Script Classes expose instance managed methods; create a BlueprintFunctionLibrary Script Class for static calls."),
						           *Name);
				return false;
			}
			if (Parameters->Num() > 4)
			{
				OutError = FString::Printf(
					TEXT("Function '%s' has %d parameters; the fixed managed function ABI v1 supports at most four."),
					*Name,
					Parameters->Num());
				return false;
			}

			FFunctionDeclaration Declaration;
			Declaration.SemanticId = SemanticId;
			Declaration.ManagedName = FName(*Name);
			Declaration.ManagedNameText = Name;
			Declaration.ReturnTypeMetadataName = ReturnType;
			Declaration.bIsStatic = bIsStatic;
			Declaration.bBlueprintEvent = BlueprintEvent != nullptr;
			Declaration.bBlueprintOverride = bBlueprintOverride;
			Declaration.BlueprintOverrideDeclaringTypePath =
				BlueprintOverrideDeclaringTypePath;
			Declaration.BlueprintOverrideFunctionNameText =
				BlueprintOverrideFunctionNameText;
			Declaration.BlueprintOverrideFunctionName =
				BlueprintOverrideFunctionName;
			if (Function->HasField(TEXT("rpc")))
			{
				const TSharedPtr<FJsonObject>* Rpc = nullptr;
				FString Target;
				if (!Function->TryGetObjectField(TEXT("rpc"), Rpc) || Rpc == nullptr || !Rpc->IsValid() ||
					!(*Rpc)->TryGetStringField(TEXT("target"), Target) || !(*Rpc)->TryGetBoolField(
						TEXT("reliable"), Declaration.bReliable))
				{
					OutError = TEXT("RPC requires a target and explicit reliability.");
					return false;
				}
				if (Target == TEXT("server"))
				{
					Declaration.RpcTarget = EFinareRpcTarget::Server;
				}
				else if (Target == TEXT("client"))
				{
					Declaration.RpcTarget = EFinareRpcTarget::Client;
				}
				else if (Target == TEXT("multicast"))
				{
					Declaration.RpcTarget = EFinareRpcTarget::Multicast;
				}
				else
				{
					OutError = TEXT("Unsupported RPC target.");
					return false;
				}
			}
			if (Function->HasField(TEXT("repNotifyProperty")))
			{
				FString Property;
				if (!Function->TryGetStringField(TEXT("repNotifyProperty"), Property) || Property.IsEmpty())
				{
					OutError = TEXT("RepNotify requires an exact property name.");
					return false;
				}
				Declaration.RepNotifyProperty = FName(*Property);
			}
			if (!ParseSemanticId(SemanticId, Declaration.Id) ||
				Declaration.ManagedName.IsNone() ||
				SeenFunctionIds.Contains(Declaration.Id) ||
				SeenFunctionNames.Contains(Declaration.ManagedName))
			{
				OutError = FString::Printf(
					TEXT(
						"Script Class declaration has duplicate, overloaded, or invalid callable function '%s' (%s). Blueprint-callable managed overloads require distinct names."),
					*Name,
					*SemanticId);
				return false;
			}
			if (const FName* InterfaceFunctionName =
				InterfaceFunctionNames.Find(Declaration.Id))
			{
				Declaration.bInterfaceFunction = true;
				Declaration.InternalName = *InterfaceFunctionName;
				for (const FInterfaceFunctionDeclaration& Interface : OutInterfaceBindings)
				{
					if (Interface.FunctionId == Declaration.Id)
					{
						Declaration.InterfacePaths.AddUnique(Interface.InterfacePath);
					}
				}
			}
			else if (Declaration.bBlueprintOverride)
			{
				Declaration.InternalName = Declaration.BlueprintOverrideFunctionName;
			}
			else
			{
				Declaration.InternalName = MakeManagedFunctionInternalName(Declaration.Id);
			}
			if (SeenInternalFunctionNames.Contains(Declaration.InternalName))
			{
				OutError = FString::Printf(
					TEXT("Script Class declaration maps multiple managed methods to inherited Blueprint event '%s'."),
					*Declaration.InternalName.ToString());
				return false;
			}
			if (Declaration.bBlueprintOverride)
			{
				UFunction* NativeOverrideFunction = nullptr;
				if (!ResolveBlueprintOverrideFunction(
						Blueprint,
						Declaration,
						NativeOverrideFunction,
						OutError,
						ProspectiveParentClass) ||
					NativeOverrideFunction == nullptr)
				{
					return false;
				}
			}
			if (!ParseFunctionValueType(
					Function,
					TEXT("returnUnrealType"),
					Declaration.ReturnValueType,
					OutError) ||
				!ResolveFunctionPinType(
					ReturnType,
					Declaration.ReturnValueType,
					true,
					Declaration.ReturnPinType,
					Declaration.bHasReturnValue))
			{
				OutError = FString::Printf(
					TEXT(
						"Function '%s' has unsupported return type '%s'. %s Use reviewed values, array/set/map, or Soft References; K2 forbids direct nested containers and requires hashable set elements/map keys."),
					*Name,
					*ReturnType,
					*OutError);
				return false;
			}

			TSet<FGuid> SeenParameterIds;
			TSet<FName> SeenParameterNames;
			Declaration.Parameters.Reserve(Parameters->Num());
			for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
			{
				const TSharedPtr<FJsonObject> Parameter = ParameterValue.IsValid()
					                                          ? ParameterValue->AsObject()
					                                          : nullptr;
				FString ParameterId;
				FString ParameterName;
				FString ParameterType;
				if (!ReadRequiredString(Parameter, TEXT("id"), ParameterId, OutError) ||
					!ReadRequiredString(Parameter, TEXT("name"), ParameterName, OutError) ||
					!ReadRequiredString(
						Parameter,
						TEXT("typeMetadataName"),
						ParameterType,
						OutError))
				{
					return false;
				}
				FFunctionParameterDeclaration ParameterDeclaration;
				ParameterDeclaration.Name = FName(*ParameterName);
				ParameterDeclaration.TypeMetadataName = ParameterType;
				FString ParameterDirection = TEXT("input");
				if (Parameter->HasField(TEXT("direction")) &&
					!Parameter->TryGetStringField(
						TEXT("direction"),
						ParameterDirection))
				{
					OutError = FString::Printf(
						TEXT("Function '%s' parameter '%s' has an invalid direction."),
						*Name,
						*ParameterName);
					return false;
				}
				if (ParameterDirection == TEXT("output"))
				{
					ParameterDeclaration.Direction =
						EFinareManagedParameterDirection::Output;
				}
				else if (ParameterDirection == TEXT("inputOutput"))
				{
					ParameterDeclaration.Direction =
						EFinareManagedParameterDirection::InputOutput;
				}
				else if (ParameterDirection != TEXT("input"))
				{
					OutError = FString::Printf(
						TEXT("Function '%s' parameter '%s' uses unsupported direction '%s'."),
						*Name,
						*ParameterName,
						*ParameterDirection);
					return false;
				}
				bool bHasParameterValue = false;
				if (!ParseSemanticId(ParameterId, ParameterDeclaration.Id) ||
					ParameterDeclaration.Name.IsNone() ||
					SeenParameterIds.Contains(ParameterDeclaration.Id) ||
					SeenParameterNames.Contains(ParameterDeclaration.Name) ||
					!ParseFunctionValueType(
						Parameter,
						TEXT("unrealType"),
						ParameterDeclaration.ValueType,
						OutError) ||
					!ResolveFunctionPinType(
						ParameterType,
						ParameterDeclaration.ValueType,
						false,
						ParameterDeclaration.PinType,
						bHasParameterValue) ||
					!bHasParameterValue)
				{
					OutError = FString::Printf(
						TEXT(
							"Function '%s' has duplicate, invalid, or unsupported parameter '%s' of type '%s'. %s Use reviewed values, array/set/map, or Soft References; K2 forbids direct nested containers and requires hashable set elements/map keys."),
						*Name,
						*ParameterName,
						*ParameterType,
						*OutError);
					return false;
				}
				ParameterDeclaration.PinType.bIsReference =
					ParameterDeclaration.Direction ==
					EFinareManagedParameterDirection::InputOutput;
				SeenParameterIds.Add(ParameterDeclaration.Id);
				SeenParameterNames.Add(ParameterDeclaration.Name);
				Declaration.Parameters.Add(MoveTemp(ParameterDeclaration));
			}

			if (Declaration.bBlueprintOverride)
			{
				const bool bHasConflictingBinding =
					Declaration.RpcTarget != EFinareRpcTarget::None ||
					!Declaration.RepNotifyProperty.IsNone() ||
					InterfaceFunctionNames.Contains(Declaration.Id) ||
					OutInputBindings.ContainsByPredicate(
						[&Declaration](const FInputActionDeclaration& Binding)
						{
							return Binding.ManagedName == Declaration.ManagedName;
						}) ||
					OutDelegateBindings.ContainsByPredicate(
						[&Declaration](const FDelegateBindingDeclaration& Binding)
						{
							return Binding.ManagedName == Declaration.ManagedName;
						});
				if (bHasConflictingBinding)
				{
					OutError = FString::Printf(
						TEXT(
							"Blueprint override '%s' cannot own Input, Delegate, Interface, RPC, or RepNotify bindings."),
						*Declaration.ManagedName.ToString());
					return false;
				}

				UFunction* NativeOverrideFunction = nullptr;
				if (!ResolveBlueprintOverrideFunction(
						Blueprint,
						Declaration,
						NativeOverrideFunction,
						OutError,
						ProspectiveParentClass) ||
					NativeOverrideFunction == nullptr ||
					!ValidateBlueprintOverrideSignature(
						*NativeOverrideFunction,
						Declaration,
						OutError))
				{
					return false;
				}
			}

			if (Declaration.RpcTarget != EFinareRpcTarget::None || !Declaration.RepNotifyProperty.IsNone())
			{
				if (!bGameplayLifecycle || Declaration.bIsStatic || Declaration.bHasReturnValue ||
					Declaration.bBlueprintEvent || Declaration.bInterfaceFunction ||
					(Declaration.RpcTarget != EFinareRpcTarget::None && !Declaration.RepNotifyProperty.IsNone()) ||
					Declaration.Parameters.ContainsByPredicate([](const FFunctionParameterDeclaration& P)
					{
						return P.Direction != EFinareManagedParameterDirection::Input;
					}) ||
					OutInputBindings.ContainsByPredicate([&](const FInputActionDeclaration& B)
					{
						return B.ManagedName == Declaration.ManagedName;
					}) ||
					OutDelegateBindings.ContainsByPredicate([&](const FDelegateBindingDeclaration& B)
					{
						return B.ManagedName == Declaration.ManagedName;
					}))
				{
					OutError = TEXT(
						"RPC/RepNotify must be instance void input-only gameplay methods without other bindings.");
					return false;
				}
			}
			if (Declaration.RpcTarget != EFinareRpcTarget::None &&
				(Declaration.Parameters.Num() > 4 || Declaration.Parameters.ContainsByPredicate(
					[](const FFunctionParameterDeclaration& P)
					{
						const FEdGraphPinType& Pin = P.PinType;
						if (Pin.IsContainer() || Pin.PinCategory == UEdGraphSchema_K2::PC_SoftObject || Pin.PinCategory
							== UEdGraphSchema_K2::PC_SoftClass)
						{
							return true;
						}
						const UObject* Type = Pin.PinSubCategoryObject.Get();
						return Pin.PinCategory == UEdGraphSchema_K2::PC_Struct &&
							Type != TBaseStructure<FVector>::Get() && Type != TBaseStructure<FRotator>::Get() && Type !=
							TBaseStructure<FTransform>::Get();
					})))
			{
				OutError = TEXT(
					"RPC supports 0-4 scalar/common-value inputs; collections, Soft References and complex structures are not supported RPC payloads yet.");
				return false;
			}
			SeenFunctionIds.Add(Declaration.Id);
			SeenFunctionNames.Add(Declaration.ManagedName);
			SeenInternalFunctionNames.Add(Declaration.InternalName);
			OutFunctions.Add(MoveTemp(Declaration));
		}
		for (const FFunctionDeclaration& Function : OutFunctions)
		{
			if (Function.RepNotifyProperty.IsNone())
			{
				continue;
			}
			FStateDeclaration* State = OutStates.FindByPredicate([&](const FStateDeclaration& S)
			{
				return S.Name == Function.RepNotifyProperty;
			});
			if (State == nullptr || !(State->PropertyFlags & CPF_Net) || !State->RepNotifyFunc.IsNone() ||
				Function.Parameters.Num() > 1 || (Function.Parameters.Num() == 1 &&
					(Function.Parameters[0].TypeMetadataName != State->TypeMetadataName || Function.Parameters[0].
						PinType != State->PinType)))
			{
				OutError = TEXT(
					"RepNotify needs one replicated property and zero parameters or its exact previous-value type.");
				return false;
			}
			State->RepNotifyFunc = Function.InternalName;
			State->PropertyFlags |= CPF_RepNotify;
		}
		return true;
	}

	bool ReadSelectedClassBinding(
		UFinareAsset& ScriptAsset,
		const FString& DeclarationJson,
		TOptional<FFinareClassBindingDeclaration>& OutBinding,
		FString& OutError)
	{
		OutBinding.Reset();
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeclarationJson);
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
		{
			OutError = FString::Printf(
				TEXT("Script Class declaration JSON is invalid: %s"),
				*Reader->GetErrorMessage());
			return false;
		}
		FString Schema;
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
			!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr || Assets->IsEmpty() ||
			!ValidateFinareClassBindingsJson(*Assets, Schema, OutError))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("Script Class declaration has no valid assets array for parent planning.");
			}
			return false;
		}
		FString RequestedAssetPath = ScriptAsset.DeclarationAssetPath;
		RequestedAssetPath.TrimStartAndEndInline();
		TSharedPtr<FJsonObject> Selected;
		for (const TSharedPtr<FJsonValue>& AssetValue : *Assets)
		{
			const TSharedPtr<FJsonObject> Candidate = AssetValue.IsValid()
				                                          ? AssetValue->AsObject()
				                                          : nullptr;
			FString CandidatePath;
			if (Candidate.IsValid() && Candidate->TryGetStringField(TEXT("assetPath"), CandidatePath) &&
				((RequestedAssetPath.IsEmpty() && Assets->Num() == 1) || CandidatePath == RequestedAssetPath))
			{
				Selected = Candidate;
				break;
			}
		}
		if (!Selected.IsValid())
		{
			OutError = FString::Printf(
				TEXT("Script Class declaration does not contain assetPath '%s' for parent planning."),
				*RequestedAssetPath);
			return false;
		}
		return FFinareClassBindingDeclaration::ReadJson(Selected, OutBinding, OutError);
	}

	bool CanonicalCurrentDefault(
		const UBlueprint& Blueprint,
		const FBPVariableDescription& Variable,
		FString& OutValue,
		FString& OutError)
	{
		const UClass* GeneratedClass = Blueprint.GeneratedClass;
		const UObject* Cdo = GeneratedClass != nullptr
			                     ? GeneratedClass->GetDefaultObject(false)
			                     : nullptr;
		const FProperty* Property = GeneratedClass != nullptr
			                            ? FindFProperty<FProperty>(GeneratedClass, Variable.VarName)
			                            : nullptr;
		if (Cdo == nullptr || Property == nullptr)
		{
			OutValue = Variable.DefaultValue;
			return true;
		}

		if (CastField<FArrayProperty>(Property) != nullptr ||
			CastField<FSetProperty>(Property) != nullptr ||
			CastField<FMapProperty>(Property) != nullptr)
		{
			const void* Value = Property->ContainerPtrToValuePtr<void>(Cdo);
			Property->ExportTextItem_Direct(
				OutValue, Value, Value, const_cast<UObject*>(Cdo), PPF_Delimited);
			return true;
		}

		if (const FBoolProperty* Typed = CastField<FBoolProperty>(Property))
		{
			OutValue = Typed->GetPropertyValue_InContainer(Cdo) ? TEXT("true") : TEXT("false");
			return true;
		}
		if (const FEnumProperty* Typed = CastField<FEnumProperty>(Property))
		{
			const FNumericProperty* Underlying = Typed->GetUnderlyingProperty();
			const int64 Value = Underlying->GetSignedIntPropertyValue(
				Typed->ContainerPtrToValuePtr<void>(Cdo));
			if (!TryCanonicalEnumName(Typed->GetEnum(), Value, OutValue))
			{
				OutError = FString::Printf(
					TEXT("Generated enum state '%s' has no authored name for value %lld."),
					*Variable.VarName.ToString(),
					Value);
				return false;
			}
			return true;
		}
		if (const FByteProperty* Typed = CastField<FByteProperty>(Property))
		{
			if (Typed->IsEnum())
			{
				const int64 Value = static_cast<int64>(Typed->GetPropertyValue_InContainer(Cdo));
				if (!TryCanonicalEnumName(Typed->Enum, Value, OutValue))
				{
					OutError = FString::Printf(
						TEXT("Generated enum state '%s' has no authored name for value %lld."),
						*Variable.VarName.ToString(),
						Value);
					return false;
				}
				return true;
			}
			OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
			return true;
		}
		if (const FIntProperty* Typed = CastField<FIntProperty>(Property))
		{
			OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
			return true;
		}
		if (const FInt64Property* Typed = CastField<FInt64Property>(Property))
		{
			OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
			return true;
		}
		if (const FFloatProperty* Typed = CastField<FFloatProperty>(Property))
		{
			OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
			return true;
		}
		if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(Property))
		{
			OutValue = LexToString(Typed->GetPropertyValue_InContainer(Cdo));
			return true;
		}
		if (const FStrProperty* Typed = CastField<FStrProperty>(Property))
		{
			OutValue = Typed->GetPropertyValue_InContainer(Cdo);
			return true;
		}
		if (const FNameProperty* Typed = CastField<FNameProperty>(Property))
		{
			OutValue = Typed->GetPropertyValue_InContainer(Cdo).ToString();
			return true;
		}
		if (const FTextProperty* Typed = CastField<FTextProperty>(Property))
		{
			OutValue = Typed->GetPropertyValue_InContainer(Cdo).ToString();
			return true;
		}
		if (const FSoftClassProperty* Typed = CastField<FSoftClassProperty>(Property))
		{
			const FString Path = Typed->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().ToString();
			OutValue = Path.IsEmpty() ? TEXT("None") : FString::Printf(TEXT("\"%s\""), *Path);
			return true;
		}
		if (const FSoftObjectProperty* Typed = CastField<FSoftObjectProperty>(Property))
		{
			const FString Path = Typed->GetPropertyValue_InContainer(Cdo).ToSoftObjectPath().ToString();
			OutValue = Path.IsEmpty() ? TEXT("None") : FString::Printf(TEXT("\"%s\""), *Path);
			return true;
		}
		if (const FStructProperty* Typed = CastField<FStructProperty>(Property);
			Typed != nullptr && Typed->Struct == TBaseStructure<FVector>::Get())
		{
			const FVector& Vector = *Typed->ContainerPtrToValuePtr<FVector>(Cdo);
			OutValue = FString::Printf(
				TEXT("(X=%s,Y=%s,Z=%s)"),
				*LexToString(Vector.X),
				*LexToString(Vector.Y),
				*LexToString(Vector.Z));
			return true;
		}
		if (const FStructProperty* Typed = CastField<FStructProperty>(Property);
			Typed != nullptr && Typed->Struct == TBaseStructure<FRotator>::Get())
		{
			const FRotator& Rotator = *Typed->ContainerPtrToValuePtr<FRotator>(Cdo);
			OutValue = FString::Printf(
				TEXT("(Pitch=%s,Yaw=%s,Roll=%s)"),
				*LexToString(Rotator.Pitch),
				*LexToString(Rotator.Yaw),
				*LexToString(Rotator.Roll));
			return true;
		}
		if (const FStructProperty* Typed = CastField<FStructProperty>(Property);
			Typed != nullptr && Typed->Struct == TBaseStructure<FTransform>::Get())
		{
			const FTransform& Transform = *Typed->ContainerPtrToValuePtr<FTransform>(Cdo);
			const FQuat& Rotation = Transform.GetRotation();
			const FVector& Translation = Transform.GetTranslation();
			const FVector& Scale = Transform.GetScale3D();
			const FRotator Rotator(Rotation);
			OutValue = FString::Printf(
				TEXT("%s,%s,%s|%s,%s,%s|%s,%s,%s"),
				*LexToString(Translation.X), *LexToString(Translation.Y),
				*LexToString(Translation.Z), *LexToString(Rotator.Pitch),
				*LexToString(Rotator.Yaw), *LexToString(Rotator.Roll),
				*LexToString(Scale.X), *LexToString(Scale.Y),
				*LexToString(Scale.Z));
			return true;
		}
		if (const FClassProperty* Typed = CastField<FClassProperty>(Property))
		{
			const UObject* Value = Typed->GetObjectPropertyValue_InContainer(Cdo);
			OutValue = Value != nullptr ? Value->GetPathName() : TEXT("None");
			return true;
		}
		if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(Property))
		{
			const UObject* Value = Typed->GetObjectPropertyValue_InContainer(Cdo);
			OutValue = Value != nullptr ? Value->GetPathName() : TEXT("None");
			return true;
		}

		OutError = FString::Printf(
			TEXT("Generated state '%s' has unsupported reflected property '%s'."),
			*Variable.VarName.ToString(),
			*Property->GetClass()->GetName());
		return false;
	}

	bool ArePropertiesExactlyCompatible(
		const FProperty* Left,
		const FProperty* Right)
	{
		if (Left == nullptr || Right == nullptr ||
			Left->GetClass() != Right->GetClass() ||
			!Left->SameType(Right) ||
			Left->GetSize() != Right->GetSize())
		{
			return false;
		}
		if (const FObjectPropertyBase* LeftObject = CastField<FObjectPropertyBase>(Left))
		{
			const FObjectPropertyBase* RightObject = CastField<FObjectPropertyBase>(Right);
			return RightObject != nullptr &&
				LeftObject->PropertyClass == RightObject->PropertyClass;
		}
		if (const FStructProperty* LeftStruct = CastField<FStructProperty>(Left))
		{
			const FStructProperty* RightStruct = CastField<FStructProperty>(Right);
			return RightStruct != nullptr && LeftStruct->Struct == RightStruct->Struct;
		}
		if (const FEnumProperty* LeftEnum = CastField<FEnumProperty>(Left))
		{
			const FEnumProperty* RightEnum = CastField<FEnumProperty>(Right);
			return RightEnum != nullptr && LeftEnum->GetEnum() == RightEnum->GetEnum();
		}
		if (const FByteProperty* LeftByte = CastField<FByteProperty>(Left))
		{
			const FByteProperty* RightByte = CastField<FByteProperty>(Right);
			return RightByte != nullptr && LeftByte->Enum == RightByte->Enum;
		}
		if (const FArrayProperty* LeftArray = CastField<FArrayProperty>(Left))
		{
			const FArrayProperty* RightArray = CastField<FArrayProperty>(Right);
			return RightArray != nullptr &&
				ArePropertiesExactlyCompatible(LeftArray->Inner, RightArray->Inner);
		}
		if (const FSetProperty* LeftSet = CastField<FSetProperty>(Left))
		{
			const FSetProperty* RightSet = CastField<FSetProperty>(Right);
			return RightSet != nullptr &&
				ArePropertiesExactlyCompatible(LeftSet->ElementProp, RightSet->ElementProp);
		}
		if (const FMapProperty* LeftMap = CastField<FMapProperty>(Left))
		{
			const FMapProperty* RightMap = CastField<FMapProperty>(Right);
			return RightMap != nullptr &&
				ArePropertiesExactlyCompatible(LeftMap->KeyProp, RightMap->KeyProp) &&
				ArePropertiesExactlyCompatible(LeftMap->ValueProp, RightMap->ValueProp);
		}
		return true;
	}

	void CopyTypedPropertyValue(
		const FProperty* DestinationProperty,
		void* DestinationContainer,
		const FProperty* SourceProperty,
		const void* SourceContainer)
	{
		if (DestinationProperty == nullptr || DestinationContainer == nullptr ||
			SourceProperty == nullptr || SourceContainer == nullptr)
		{
			return;
		}
		const FBoolProperty* DestinationBool = CastField<FBoolProperty>(DestinationProperty);
		const FBoolProperty* SourceBool = CastField<FBoolProperty>(SourceProperty);
		if (DestinationBool != nullptr && SourceBool != nullptr &&
			DestinationProperty->ArrayDim == SourceProperty->ArrayDim)
		{
			for (int32 Index = 0; Index < DestinationProperty->ArrayDim; ++Index)
			{
				void* DestinationValue = DestinationProperty->ContainerPtrToValuePtr<void>(
					DestinationContainer,
					Index);
				const void* SourceValue = SourceProperty->ContainerPtrToValuePtr<void>(
					SourceContainer,
					Index);
				DestinationBool->SetPropertyValue(
					DestinationValue,
					SourceBool->GetPropertyValue(SourceValue));
			}
			return;
		}
		DestinationProperty->CopyCompleteValue(
			DestinationProperty->ContainerPtrToValuePtr<void>(DestinationContainer),
			SourceProperty->ContainerPtrToValuePtr<void>(SourceContainer));
	}

	bool AreTypedPropertyValuesIdentical(
		const FProperty* LeftProperty,
		const void* LeftContainer,
		const FProperty* RightProperty,
		const void* RightContainer)
	{
		if (LeftProperty == nullptr || LeftContainer == nullptr ||
			RightProperty == nullptr || RightContainer == nullptr ||
			!ArePropertiesExactlyCompatible(LeftProperty, RightProperty))
		{
			return false;
		}
		const FBoolProperty* LeftBool = CastField<FBoolProperty>(LeftProperty);
		const FBoolProperty* RightBool = CastField<FBoolProperty>(RightProperty);
		if (LeftBool != nullptr && RightBool != nullptr &&
			LeftProperty->ArrayDim == RightProperty->ArrayDim)
		{
			for (int32 Index = 0; Index < LeftProperty->ArrayDim; ++Index)
			{
				const void* LeftValue = LeftProperty->ContainerPtrToValuePtr<void>(
					LeftContainer,
					Index);
				const void* RightValue = RightProperty->ContainerPtrToValuePtr<void>(
					RightContainer,
					Index);
				if (LeftBool->GetPropertyValue(LeftValue) != RightBool->GetPropertyValue(RightValue))
				{
					return false;
				}
			}
			return true;
		}
		return LeftProperty->Identical(
			LeftProperty->ContainerPtrToValuePtr<void>(LeftContainer),
			RightProperty->ContainerPtrToValuePtr<void>(RightContainer),
			PPF_None);
	}

	bool IsCollectionProperty(const FProperty* Property)
	{
		return Property != nullptr &&
		(CastField<FArrayProperty>(Property) != nullptr ||
			CastField<FSetProperty>(Property) != nullptr ||
			CastField<FMapProperty>(Property) != nullptr);
	}

	bool ImportSerializedPropertyValue(
		const FProperty* Property,
		const FString& Text,
		void* OutValue)
	{
		if (Property == nullptr || OutValue == nullptr)
		{
			return false;
		}
		const TCHAR* End = Property->ImportText_Direct(
			*Text, OutValue, nullptr, PPF_None, nullptr);
		if (End == nullptr)
		{
			return false;
		}
		while (*End != TEXT('\0') && FChar::IsWhitespace(*End))
		{
			++End;
		}
		return *End == TEXT('\0');
	}

	bool SerializedPropertyValueEquals(
		const FProperty* Property,
		const FString& Left,
		const FString& Right,
		FString& OutError)
	{
		if (!IsCollectionProperty(Property))
		{
			return Left == Right;
		}
		void* LeftValue = FMemory::Malloc(Property->GetSize(), Property->GetMinAlignment());
		void* RightValue = FMemory::Malloc(Property->GetSize(), Property->GetMinAlignment());
		Property->InitializeValue(LeftValue);
		Property->InitializeValue(RightValue);
		const bool bLeftValid = ImportSerializedPropertyValue(Property, Left, LeftValue);
		const bool bRightValid = ImportSerializedPropertyValue(Property, Right, RightValue);
		const bool bEqual = bLeftValid && bRightValid && Property->Identical(LeftValue, RightValue, PPF_None);
		Property->DestroyValue(LeftValue);
		Property->DestroyValue(RightValue);
		FMemory::Free(LeftValue);
		FMemory::Free(RightValue);
		if (!bLeftValid || !bRightValid)
		{
			OutError = FString::Printf(
				TEXT("Generated collection property '%s' has an invalid canonical default."),
				*Property->GetName());
			return false;
		}
		return bEqual;
	}

	bool HasMetadataValue(
		const FBPVariableDescription& Variable,
		const FName& Key,
		const FString& Value)
	{
		return Variable.HasMetaData(Key) && Variable.GetMetaData(Key) == Value;
	}

	bool BuildPlan(
		const UBlueprint& Blueprint,
		const UClass* EffectiveParentClass,
		TConstArrayView<FStateDeclaration> Declarations,
		FSyncPlan& OutPlan,
		FFinareBlueprintClassSyncResult& OutResult,
		FString& OutError)
	{
		TMap<FGuid, int32> GeneratedById;
		TMap<FString, int32> GeneratedBySemanticId;
		TSet<FGuid> DesiredIds;
		for (const FStateDeclaration& Declaration : Declarations)
		{
			DesiredIds.Add(Declaration.Id);
		}

		for (int32 Index = 0; Index < Blueprint.NewVariables.Num(); ++Index)
		{
			const FBPVariableDescription& Variable = Blueprint.NewVariables[Index];
			if (!IsManagedState(Variable))
			{
				continue;
			}
			if (!Variable.HasMetaData(StateIdKey))
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint variable '%s' is missing UnifiedScript.StateId metadata."),
					*Variable.VarName.ToString());
				return false;
			}
			const FString SemanticId = Variable.GetMetaData(StateIdKey);
			FGuid StateId;
			if (!ParseSemanticId(SemanticId, StateId) ||
				GeneratedById.Contains(StateId) ||
				GeneratedBySemanticId.Contains(SemanticId))
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint variable '%s' has duplicate or invalid state identity '%s'."),
					*Variable.VarName.ToString(),
					*SemanticId);
				return false;
			}
			GeneratedById.Add(StateId, Index);
			GeneratedBySemanticId.Add(SemanticId, Index);
			if (!DesiredIds.Contains(StateId))
			{
				OutPlan.StaleGeneratedVariables.Add(Variable.VarName);
			}
		}

		for (const FStateDeclaration& Declaration : Declarations)
		{
			int32 ExistingIndex = FindVariableByGuid(Blueprint, Declaration.Id);
			const int32 MetadataIndex = FindVariableByStateId(Blueprint, Declaration.SemanticId);
			if (ExistingIndex != INDEX_NONE &&
				!IsManagedState(Blueprint.NewVariables[ExistingIndex]))
			{
				OutError = FString::Printf(
					TEXT("State '%s' semantic GUID collides with user Blueprint variable '%s'."),
					*Declaration.Name.ToString(),
					*Blueprint.NewVariables[ExistingIndex].VarName.ToString());
				return false;
			}
			if (ExistingIndex != INDEX_NONE && MetadataIndex != INDEX_NONE &&
				ExistingIndex != MetadataIndex)
			{
				OutError = FString::Printf(
					TEXT("State '%s' has conflicting Blueprint GUID and metadata owners."),
					*Declaration.Name.ToString());
				return false;
			}
			if (ExistingIndex == INDEX_NONE)
			{
				ExistingIndex = MetadataIndex;
			}

			const int32 NameIndex = Blueprint.NewVariables.IndexOfByPredicate(
				[&Declaration](const FBPVariableDescription& Variable)
				{
					return Variable.VarName == Declaration.Name;
				});
			if (NameIndex != INDEX_NONE && NameIndex != ExistingIndex)
			{
				const FBPVariableDescription& Owner = Blueprint.NewVariables[NameIndex];
				FGuid OwnerStateId;
				const bool bGeneratedOwner = IsManagedState(Owner) &&
					Owner.HasMetaData(StateIdKey) &&
					ParseSemanticId(Owner.GetMetaData(StateIdKey), OwnerStateId);
				if (!bGeneratedOwner || DesiredIds.Contains(OwnerStateId))
				{
					OutError = FString::Printf(
						TEXT("State '%s' collides with Blueprint variable '%s' owned by a different identity."),
						*Declaration.Name.ToString(),
						*Owner.VarName.ToString());
					return false;
				}
			}
			if (FindFProperty<FProperty>(EffectiveParentClass, Declaration.Name) != nullptr)
			{
				OutError = FString::Printf(
					TEXT("State '%s' masks a property declared by parent class '%s'."),
					*Declaration.Name.ToString(),
					*GetPathNameSafe(EffectiveParentClass));
				return false;
			}

			FPlannedState& Planned = OutPlan.States.AddDefaulted_GetRef();
			Planned.Declaration = Declaration;
			Planned.AppliedDefault = Declaration.DefaultValue;
			if (ExistingIndex == INDEX_NONE)
			{
				++OutResult.AddedPropertyCount;
				OutResult.bChanged = true;
				continue;
			}

			Planned.bExisting = true;
			const FBPVariableDescription& Existing = Blueprint.NewVariables[ExistingIndex];
			Planned.ExistingName = Existing.VarName;
			if (!Existing.HasMetaData(DeclarationDefaultKey))
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint variable '%s' is missing its previous declaration default."),
					*Existing.VarName.ToString());
				return false;
			}
			FString CurrentDefault;
			if (!CanonicalCurrentDefault(Blueprint, Existing, CurrentDefault, OutError))
			{
				return false;
			}
			const FString PreviousDeclarationDefault = Existing.GetMetaData(DeclarationDefaultKey);
			const FProperty* ExistingProperty = Blueprint.GeneratedClass != nullptr
				                                    ? FindFProperty<FProperty>(
					                                    Blueprint.GeneratedClass, Existing.VarName)
				                                    : nullptr;
			const bool bSameType = Existing.VarType == Declaration.PinType;
			bool bCurrentMatchesPrevious = CurrentDefault == PreviousDeclarationDefault;
			if (IsCollectionProperty(ExistingProperty))
			{
				bCurrentMatchesPrevious = SerializedPropertyValueEquals(
					ExistingProperty, CurrentDefault, PreviousDeclarationDefault, OutError);
				if (!OutError.IsEmpty())
				{
					return false;
				}
			}
			Planned.bPreserveDefaultOverride = !bCurrentMatchesPrevious;
			if (Planned.bPreserveDefaultOverride)
			{
				if (Existing.VarType != Declaration.PinType)
				{
					OutError = FString::Printf(
						TEXT(
							"State '%s' changes type while its Blueprint default is overridden. Reset the override or migrate it explicitly."),
						*Declaration.Name.ToString());
					return false;
				}
				Planned.AppliedDefault = CurrentDefault;
				++OutResult.PreservedDefaultOverrideCount;
			}

			const bool bRename = Existing.VarName != Declaration.Name;
			if (bRename)
			{
				++OutResult.RenamedPropertyCount;
				OutResult.bChanged = true;
			}
			const FString FriendlyName = FName::NameToDisplayString(
				Declaration.Name.ToString(),
				Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_Boolean);
			const bool bGuidChanged = Existing.VarGuid != Declaration.Id;
			const bool bTypeChanged = Existing.VarType != Declaration.PinType;
			const bool bFriendlyNameChanged = Existing.FriendlyName != FriendlyName;
			const bool bCategoryChanged = Existing.Category.ToString() != Declaration.Category;
			const bool bFlagsChanged = Existing.PropertyFlags != Declaration.PropertyFlags ||
				Existing.RepNotifyFunc != Declaration.RepNotifyFunc || Existing.ReplicationCondition != Declaration.
				ReplicationCondition;
			bool bDefaultChanged = CurrentDefault != Planned.AppliedDefault;
			if (bSameType && IsCollectionProperty(ExistingProperty))
			{
				bDefaultChanged = !SerializedPropertyValueEquals(
					ExistingProperty, CurrentDefault, Planned.AppliedDefault, OutError);
				if (!OutError.IsEmpty())
				{
					return false;
				}
			}
			const bool bMarkerChanged = !HasMetadataValue(Existing, ManagedStateKey, TEXT("true"));
			const bool bStateIdChanged =
				!HasMetadataValue(Existing, StateIdKey, Declaration.SemanticId);
			const bool bMetadataTypeChanged = !HasMetadataValue(
				Existing,
				TypeMetadataNameKey,
				Declaration.TypeMetadataName);
			bool bDeclarationDefaultChanged = !HasMetadataValue(
				Existing,
				DeclarationDefaultKey,
				Declaration.DefaultValue);
			if (bSameType && IsCollectionProperty(ExistingProperty))
			{
				bDeclarationDefaultChanged = !SerializedPropertyValueEquals(
					ExistingProperty, PreviousDeclarationDefault, Declaration.DefaultValue, OutError);
				if (!OutError.IsEmpty())
				{
					return false;
				}
			}
			const bool bUpdate = bGuidChanged || bTypeChanged || bFriendlyNameChanged ||
				bCategoryChanged || bFlagsChanged || bDefaultChanged || bMarkerChanged ||
				bStateIdChanged || bMetadataTypeChanged || bDeclarationDefaultChanged;
			if (bUpdate)
			{
				++OutResult.UpdatedPropertyCount;
				OutResult.bChanged = true;
			}
		}

		OutResult.DeclaredStateCount = Declarations.Num();
		OutResult.RemovedPropertyCount = OutPlan.StaleGeneratedVariables.Num();
		OutResult.bChanged |= !OutPlan.StaleGeneratedVariables.IsEmpty();
		return true;
	}

	bool BuildFunctionPlan(
		const UBlueprint& Blueprint,
		const UClass* EffectiveParentClass,
		TConstArrayView<FFunctionDeclaration> Declarations,
		FSyncPlan& OutPlan,
		FFinareBlueprintClassSyncResult& OutResult,
		FString& OutError)
	{
		TSet<FGuid> DesiredIds;
		for (const FFunctionDeclaration& Declaration : Declarations)
		{
			DesiredIds.Add(Declaration.Id);
		}

		TMap<FGuid, UEdGraph*> ExistingById;
		TArray<UEdGraph*> ExistingGraphs;
		ExistingGraphs.Reserve(
			Blueprint.FunctionGraphs.Num() + Blueprint.ImplementedInterfaces.Num() * 4);
		for (UEdGraph* Graph : Blueprint.FunctionGraphs)
		{
			ExistingGraphs.AddUnique(Graph);
		}
		for (const FBPInterfaceDescription& Interface : Blueprint.ImplementedInterfaces)
		{
			for (UEdGraph* Graph : Interface.Graphs)
			{
				ExistingGraphs.AddUnique(Graph);
			}
		}
		for (UEdGraph* Graph : ExistingGraphs)
		{
			if (Graph == nullptr || !IsManagedFunctionGraph(Blueprint, *Graph))
			{
				continue;
			}
			FGuid Id;
			if (!TryReadManagedFunctionGraphId(Blueprint, *Graph, Id) ||
				ExistingById.Contains(Id))
			{
				OutError = FString::Printf(
					TEXT(
						"Generated Blueprint function graph '%s' has duplicate or invalid UnifiedScript.FunctionId metadata."),
					*GetPathNameSafe(Graph));
				return false;
			}
			ExistingById.Add(Id, Graph);
			if (!DesiredIds.Contains(Id) && !IsInterfaceGraph(Blueprint, Graph))
			{
				OutPlan.StaleGeneratedFunctionIds.Add(Id);
			}
		}

		for (const FFunctionDeclaration& Declaration : Declarations)
		{
			if (Declaration.bInterfaceFunction &&
				!ValidateInterfaceFunctionSignature(Declaration, OutError))
			{
				return false;
			}
			UEdGraph* const* Existing = ExistingById.Find(Declaration.Id);
			if (Declaration.bBlueprintOverride &&
				FindUserOwnedOverrideEvent(
					Blueprint,
					Declaration.InternalName) != nullptr)
			{
				OutError = FString::Printf(
					TEXT("Managed Blueprint override '%s' collides with a user-owned override event '%s'."),
					*Declaration.ManagedName.ToString(),
					*Declaration.InternalName.ToString());
				return false;
			}
			UEdGraph* NameOwner = FindFunctionGraphByName(
				Blueprint,
				Declaration.InternalName);
			if (NameOwner != nullptr &&
				(Existing == nullptr || NameOwner != *Existing))
			{
				const bool bManagedNameOwner = IsManagedFunctionGraph(Blueprint, *NameOwner);
				FGuid NameOwnerId;
				const bool bStaleManagedNameOwner =
					bManagedNameOwner &&
					TryReadManagedFunctionGraphId(Blueprint, *NameOwner, NameOwnerId) &&
					OutPlan.StaleGeneratedFunctionIds.Contains(NameOwnerId);
				const bool bReplacingStaleManagedOverride =
					Declaration.bBlueprintOverride &&
					Existing == nullptr &&
					bStaleManagedNameOwner;
				// Duplicate projections carry the old managed graph until the
				// stale-ID removal below runs.  Replace it only when the graph is
				// still provably managed and its persisted ID is actually stale;
				// a same-named user graph or an untrusted binding remains a
				// collision.
				if (!bManagedNameOwner ||
					(Declaration.bBlueprintOverride && !bReplacingStaleManagedOverride))
				{
					OutError = FString::Printf(
						TEXT("Managed function '%s' collides with user-owned Blueprint graph '%s'."),
						*Declaration.ManagedName.ToString(),
						*NameOwner->GetName());
					return false;
				}
			}
			if (!Declaration.bInterfaceFunction && !Declaration.bBlueprintOverride &&
				EffectiveParentClass != nullptr &&
				EffectiveParentClass->FindFunctionByName(
					Declaration.InternalName,
					EIncludeSuperFlag::IncludeSuper) != nullptr)
			{
				OutError = FString::Printf(
					TEXT("Managed function '%s' collides with parent function '%s'."),
					*Declaration.ManagedName.ToString(),
					*Declaration.InternalName.ToString());
				return false;
			}

			FPlannedFunction& Planned = OutPlan.Functions.AddDefaulted_GetRef();
			Planned.Declaration = Declaration;
			Planned.bExisting = Existing != nullptr;
			Planned.bNeedsUpdate = Existing == nullptr ||
				!FunctionGraphMatches(Blueprint, **Existing, Declaration);
			if (!Planned.bExisting)
			{
				++OutResult.AddedFunctionCount;
			}
			else if (Planned.bNeedsUpdate)
			{
				++OutResult.UpdatedFunctionCount;
			}
			OutResult.bChanged |= Planned.bNeedsUpdate;
		}

		OutResult.DeclaredFunctionCount = Declarations.Num();
		OutResult.RemovedFunctionCount = OutPlan.StaleGeneratedFunctionIds.Num();
		OutResult.bChanged |= !OutPlan.StaleGeneratedFunctionIds.IsEmpty();
		return true;
	}

	bool BuildLifecyclePlan(
		const UBlueprint& Blueprint,
		TConstArrayView<FLifecycleDeclaration> Declarations,
		FSyncPlan& OutPlan,
		FFinareBlueprintClassSyncResult& OutResult,
		FString& OutError)
	{
		OutPlan.LifecycleBindings.Reset();
		for (const FLifecycleDeclaration& Declaration : Declarations)
		{
			FFinareManagedLifecycleBinding Binding;
			Binding.ManagedName = Declaration.ManagedName;
			Binding.HookId = Declaration.Id;
			OutPlan.LifecycleBindings.Add(Declaration.Kind, MoveTemp(Binding));
		}

		const UFinareBlueprintGeneratedClass* GeneratedClass =
			Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		if (!Declarations.IsEmpty() && GeneratedClass == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Generated Blueprint '%s' cannot persist managed lifecycle identities."),
				*Blueprint.GetPathName());
			return false;
		}

		bool bMatches = GeneratedClass != nullptr &&
			GeneratedClass->ManagedLifecycleBindings.Num() ==
			OutPlan.LifecycleBindings.Num();
		if (bMatches)
		{
			for (const TPair<
				     EFinareManagedLifecycleHookKind,
				     FFinareManagedLifecycleBinding>& Pair :
			     OutPlan.LifecycleBindings)
			{
				const FFinareManagedLifecycleBinding* Existing =
					GeneratedClass->ManagedLifecycleBindings.Find(Pair.Key);
				if (Existing == nullptr ||
					Existing->ManagedName != Pair.Value.ManagedName ||
					Existing->HookId != Pair.Value.HookId)
				{
					bMatches = false;
					break;
				}
			}
		}

		OutPlan.bLifecycleBindingsChanged = !bMatches;
		OutResult.DeclaredLifecycleHookCount = Declarations.Num();
		OutResult.UpdatedLifecycleHookCount = bMatches ? 0 : Declarations.Num();
		OutResult.bChanged |= !bMatches;
		return true;
	}

	bool InputBindingMatches(
		const FFinareManagedInputActionBinding& Left,
		const FFinareManagedInputActionBinding& Right)
	{
		return Left.ManagedName == Right.ManagedName &&
			Left.BindingId == Right.BindingId &&
			Left.ActionPath == Right.ActionPath &&
			Left.TriggerEvent == Right.TriggerEvent &&
			Left.ValueType == Right.ValueType &&
			Left.bConsumeInput == Right.bConsumeInput;
	}

	bool DelegateBindingMatches(
		const FFinareManagedDelegateBinding& Left,
		const FFinareManagedDelegateBinding& Right)
	{
		return Left.ManagedName == Right.ManagedName &&
			Left.BindingId == Right.BindingId &&
			Left.SourceTypePath == Right.SourceTypePath &&
			Left.EventPath == Right.EventPath &&
			Left.Source == Right.Source &&
			Left.SourceMember == Right.SourceMember;
	}

	bool InterfaceBindingMatches(
		const FFinareManagedInterfaceFunctionBinding& Left,
		const FFinareManagedInterfaceFunctionBinding& Right)
	{
		return Left.InterfacePath == Right.InterfacePath &&
			Left.FunctionName == Right.FunctionName &&
			Left.ManagedName == Right.ManagedName &&
			Left.BindingId == Right.BindingId &&
			Left.FunctionId == Right.FunctionId;
	}

	template <typename BindingType, typename PredicateType>
	bool BindingArraysMatch(
		const TArray<BindingType>& Left,
		const TArray<BindingType>& Right,
		PredicateType&& Predicate)
	{
		if (Left.Num() != Right.Num())
		{
			return false;
		}
		for (int32 Index = 0; Index < Left.Num(); ++Index)
		{
			if (!Predicate(Left[Index], Right[Index]))
			{
				return false;
			}
		}
		return true;
	}

	TArray<FFinareManagedInterfaceFunctionBinding> BindingsForInterface(
		const TArray<FFinareManagedInterfaceFunctionBinding>& Bindings,
		const FString& InterfacePath)
	{
		TArray<FFinareManagedInterfaceFunctionBinding> Result;
		for (const FFinareManagedInterfaceFunctionBinding& Binding : Bindings)
		{
			if (Binding.InterfacePath == InterfacePath)
			{
				Result.Add(Binding);
			}
		}
		return Result;
	}

	bool BuildGameplayBindingPlan(
		const UBlueprint& Blueprint,
		TConstArrayView<FInputActionDeclaration> InputDeclarations,
		TConstArrayView<FDelegateBindingDeclaration> DelegateDeclarations,
		TConstArrayView<FInterfaceFunctionDeclaration> InterfaceDeclarations,
		FSyncPlan& OutPlan,
		FFinareBlueprintClassSyncResult& OutResult,
		FString& OutError)
	{
		OutPlan.InputBindings.Reset();
		OutPlan.InputBindings.Reserve(InputDeclarations.Num());
		for (const FInputActionDeclaration& Declaration : InputDeclarations)
		{
			FFinareManagedInputActionBinding& Binding =
				OutPlan.InputBindings.AddDefaulted_GetRef();
			Binding.ManagedName = Declaration.ManagedName;
			Binding.BindingId = Declaration.BindingId;
			Binding.ActionPath = Declaration.ActionPath;
			Binding.TriggerEvent = Declaration.TriggerEvent;
			Binding.ValueType = Declaration.ValueType;
			Binding.bConsumeInput = Declaration.bConsumeInput;
		}

		OutPlan.DelegateBindings.Reset();
		OutPlan.DelegateBindings.Reserve(DelegateDeclarations.Num());
		for (const FDelegateBindingDeclaration& Declaration : DelegateDeclarations)
		{
			FFinareManagedDelegateBinding& Binding =
				OutPlan.DelegateBindings.AddDefaulted_GetRef();
			Binding.ManagedName = Declaration.ManagedName;
			Binding.BindingId = Declaration.BindingId;
			Binding.SourceTypePath = Declaration.SourceTypePath;
			Binding.EventPath = Declaration.EventPath;
			Binding.Source = Declaration.Source;
			Binding.SourceMember = Declaration.SourceMember;
		}

		OutPlan.InterfaceBindings.Reset();
		OutPlan.InterfaceBindings.Reserve(InterfaceDeclarations.Num());
		for (const FInterfaceFunctionDeclaration& Declaration : InterfaceDeclarations)
		{
			FFinareManagedInterfaceFunctionBinding& Binding =
				OutPlan.InterfaceBindings.AddDefaulted_GetRef();
			Binding.InterfacePath = Declaration.InterfacePath;
			Binding.FunctionName = Declaration.FunctionName;
			Binding.ManagedName = Declaration.ManagedName;
			Binding.BindingId = Declaration.BindingId;
			Binding.FunctionId = Declaration.FunctionId;
		}

		const UFinareBlueprintGeneratedClass* GeneratedClass =
			Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		if ((!InputDeclarations.IsEmpty() || !DelegateDeclarations.IsEmpty() ||
			!InterfaceDeclarations.IsEmpty()) && GeneratedClass == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Generated Blueprint '%s' cannot persist managed gameplay bindings."),
				*Blueprint.GetPathName());
			return false;
		}

		const TArray<FFinareManagedInputActionBinding> EmptyInputs;
		const TArray<FFinareManagedDelegateBinding> EmptyDelegates;
		const TArray<FFinareManagedInterfaceFunctionBinding> EmptyInterfaces;
		const TArray<FFinareManagedInputActionBinding>& PreviousInputs =
			GeneratedClass != nullptr
				? GeneratedClass->ManagedInputActionBindings
				: EmptyInputs;
		const TArray<FFinareManagedDelegateBinding>& PreviousDelegates =
			GeneratedClass != nullptr
				? GeneratedClass->ManagedDelegateBindings
				: EmptyDelegates;
		const TArray<FFinareManagedInterfaceFunctionBinding>& PreviousInterfaces =
			GeneratedClass != nullptr
				? GeneratedClass->ManagedInterfaceBindings
				: EmptyInterfaces;

		const bool bInputsMatch = BindingArraysMatch(
			PreviousInputs,
			OutPlan.InputBindings,
			InputBindingMatches);
		const bool bDelegatesMatch = BindingArraysMatch(
			PreviousDelegates,
			OutPlan.DelegateBindings,
			DelegateBindingMatches);
		const bool bInterfacesMatch = BindingArraysMatch(
			PreviousInterfaces,
			OutPlan.InterfaceBindings,
			InterfaceBindingMatches);

		TSet<FString> ExistingInterfacePaths;
		for (const FBPInterfaceDescription& Existing : Blueprint.ImplementedInterfaces)
		{
			if (const UClass* InterfaceClass = Existing.Interface.Get())
			{
				ExistingInterfacePaths.Add(InterfaceClass->GetPathName());
			}
		}
		TSet<FString> PreviousInterfacePaths;
		for (const FFinareManagedInterfaceFunctionBinding& Binding : PreviousInterfaces)
		{
			PreviousInterfacePaths.Add(Binding.InterfacePath);
		}
		TSet<FString> ExpectedInterfacePaths;
		for (const FFinareManagedInterfaceFunctionBinding& Binding :
		     OutPlan.InterfaceBindings)
		{
			ExpectedInterfacePaths.Add(Binding.InterfacePath);
		}

		TSet<FString> AllManagedInterfacePaths = PreviousInterfacePaths;
		AllManagedInterfacePaths.Append(ExpectedInterfacePaths);
		for (const FString& InterfacePath : AllManagedInterfacePaths)
		{
			const bool bWasGenerated = PreviousInterfacePaths.Contains(InterfacePath);
			const bool bExpected = ExpectedInterfacePaths.Contains(InterfacePath);
			const bool bExists = ExistingInterfacePaths.Contains(InterfacePath);
			if (bExpected && !bWasGenerated && bExists)
			{
				OutError = FString::Printf(
					TEXT("Managed interface '%s' collides with a user-owned Blueprint interface on '%s'."),
					*InterfacePath,
					*Blueprint.GetPathName());
				return false;
			}

			const TArray<FFinareManagedInterfaceFunctionBinding> PreviousForPath =
				BindingsForInterface(PreviousInterfaces, InterfacePath);
			const TArray<FFinareManagedInterfaceFunctionBinding> ExpectedForPath =
				BindingsForInterface(OutPlan.InterfaceBindings, InterfacePath);
			const bool bRouteMatches = BindingArraysMatch(
				PreviousForPath,
				ExpectedForPath,
				InterfaceBindingMatches);
			const bool bRefreshOwnedInterface =
				bWasGenerated && bExpected && !bRouteMatches;
			if (bExists && (bRefreshOwnedInterface || (bWasGenerated && !bExpected)))
			{
				OutPlan.InterfacePathsToRemove.Add(InterfacePath);
			}
			if (bExpected && (!bExists || bRefreshOwnedInterface))
			{
				OutPlan.InterfacePathsToAdd.Add(InterfacePath);
			}
		}
		OutPlan.InterfacePathsToRemove.Sort();
		OutPlan.InterfacePathsToAdd.Sort();

		OutPlan.bGameplayBindingsChanged = !bInputsMatch || !bDelegatesMatch ||
			!bInterfacesMatch || !OutPlan.InterfacePathsToRemove.IsEmpty() ||
			!OutPlan.InterfacePathsToAdd.IsEmpty();
		OutResult.DeclaredInputBindingCount = InputDeclarations.Num();
		OutResult.UpdatedInputBindingCount = bInputsMatch ? 0 : InputDeclarations.Num();
		OutResult.DeclaredDelegateBindingCount = DelegateDeclarations.Num();
		OutResult.UpdatedDelegateBindingCount =
			bDelegatesMatch ? 0 : DelegateDeclarations.Num();
		OutResult.DeclaredInterfaceBindingCount = InterfaceDeclarations.Num();
		OutResult.UpdatedInterfaceBindingCount =
			bInterfacesMatch ? 0 : InterfaceDeclarations.Num();
		for (FPlannedFunction& Planned : OutPlan.Functions)
		{
			if (!Planned.Declaration.bInterfaceFunction || Planned.bNeedsUpdate)
			{
				continue;
			}
			const bool bInterfaceWillBeRecreated =
				Planned.Declaration.InterfacePaths.ContainsByPredicate(
					[&OutPlan](const FString& InterfacePath)
					{
						return OutPlan.InterfacePathsToAdd.Contains(InterfacePath);
					});
			if (bInterfaceWillBeRecreated)
			{
				Planned.bNeedsUpdate = true;
				++OutResult.UpdatedFunctionCount;
			}
		}
		OutResult.bChanged |= OutPlan.bGameplayBindingsChanged;
		return true;
	}

	FName MakeTemporaryName(const UBlueprint& Blueprint, const FGuid& Id)
	{
		FString Base = FString::Printf(
			TEXT("__Finare_%s"),
			*Id.ToString(EGuidFormats::Digits).Left(12));
		FName Candidate(*Base);
		int32 Suffix = 1;
		while (Blueprint.NewVariables.ContainsByPredicate(
				[&Candidate](const FBPVariableDescription& Variable)
				{
					return Variable.VarName == Candidate;
				}) ||
			FindFProperty<FProperty>(Blueprint.ParentClass, Candidate) != nullptr)
		{
			Candidate = FName(*FString::Printf(TEXT("%s_%d"), *Base, Suffix++));
		}
		return Candidate;
	}

	bool ConfigureFunctionGraph(
		UBlueprint& Blueprint,
		const FFunctionDeclaration& Declaration,
		UEdGraph*& InOutGraph,
		FString& OutError)
	{
		bool bCreated = InOutGraph == nullptr;
		UFunction* OverrideFunction = nullptr;
		if (Declaration.bBlueprintOverride &&
			(!ResolveBlueprintOverrideFunction(
					Blueprint,
					Declaration,
					OverrideFunction,
					OutError) ||
				OverrideFunction == nullptr))
		{
			return false;
		}
		if (Declaration.bBlueprintOverride && !bCreated &&
			(InOutGraph->GetFName() != Declaration.InternalName ||
				!OverrideFunctionGraphSignatureMatches(
					Blueprint,
					*InOutGraph,
					Declaration)))
		{
			// This graph is product-owned (BuildFunctionPlan rejected every
			// user-owned graph/event collision). Recreate it from the newly
			// resolved native signature so retargeting an override remains a
			// valid transactional source update.
			FBlueprintEditorUtils::RemoveGraph(
				&Blueprint,
				InOutGraph,
				EGraphRemoveFlags::MarkTransient);
			InOutGraph = nullptr;
			bCreated = true;
		}
		if (InOutGraph == nullptr)
		{
			InOutGraph = FBlueprintEditorUtils::CreateNewGraph(
				&Blueprint,
				Declaration.InternalName,
				UEdGraph::StaticClass(),
				UEdGraphSchema_K2::StaticClass());
			if (InOutGraph == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Unreal could not create generated Blueprint function '%s'."),
					*Declaration.ManagedName.ToString());
				return false;
			}
			if (Declaration.bBlueprintOverride)
			{
				FBlueprintEditorUtils::AddFunctionGraph<UClass>(
					&Blueprint,
					InOutGraph,
					false,
					OverrideFunction->GetOuterUClass());
			}
			else
			{
				FBlueprintEditorUtils::AddFunctionGraph<UFunction>(
					&Blueprint,
					InOutGraph,
					true,
					nullptr);
			}
		}
		if (Declaration.bBlueprintOverride)
		{
			InOutGraph->Modify();
			InOutGraph->GraphGuid = Declaration.Id;
			TArray<UK2Node_FunctionEntry*> EntryNodes;
			TArray<UK2Node_FunctionResult*> ResultNodes;
			InOutGraph->GetNodesOfClass(EntryNodes);
			InOutGraph->GetNodesOfClass(ResultNodes);
			if (EntryNodes.Num() != 1)
			{
				OutError = FString::Printf(
					TEXT("Inherited Blueprint event '%s' requires one function entry node."),
					*Declaration.InternalName.ToString());
				return false;
			}
			UK2Node_FunctionEntry* Entry = EntryNodes[0];
			Entry->Modify();
			TArray<UK2Node_CallParentFunction*> ParentCalls;
			InOutGraph->GetNodesOfClass(ParentCalls);
			for (UK2Node_CallParentFunction* ParentCall : ParentCalls)
			{
				if (ParentCall != nullptr)
				{
					ParentCall->BreakAllNodeLinks();
					InOutGraph->RemoveNode(ParentCall);
				}
			}
			for (UK2Node_FunctionResult* Result : ResultNodes)
			{
				if (Result != nullptr)
				{
					Result->Modify();
					Result->BreakAllNodeLinks();
				}
			}
			Entry->BreakAllNodeLinks();
			if (ResultNodes.Num() == 1)
			{
				UEdGraphPin* ThenPin = Entry->FindPin(
					UEdGraphSchema_K2::PN_Then,
					EGPD_Output);
				UEdGraphPin* ExecutePin = ResultNodes[0]->FindPin(
					UEdGraphSchema_K2::PN_Execute,
					EGPD_Input);
				if (ThenPin == nullptr || ExecutePin == nullptr ||
					!GetDefault<UEdGraphSchema_K2>()->TryCreateConnection(
						ThenPin,
						ExecutePin))
				{
					OutError = FString::Printf(
						TEXT("Inherited Blueprint event '%s' could not create an empty managed body."),
						*Declaration.InternalName.ToString());
					return false;
				}
			}
			int32 ExtraFlags = FUNC_Public | FUNC_BlueprintCallable |
				FUNC_Event | FUNC_BlueprintEvent;
			if (OverrideFunction->HasAnyFunctionFlags(FUNC_Const))
			{
				ExtraFlags |= FUNC_Const;
			}
			Entry->CustomGeneratedFunctionName = Declaration.InternalName;
			Entry->SetExtraFlags(ExtraFlags);
			Entry->MetaData.Category = FText::FromString(DefaultCategory);
			Entry->MetaData.ToolTip = FText::FromString(FString::Printf(
				TEXT("Managed Blueprint override %s"),
				*Declaration.ManagedName.ToString()));
			Entry->MetaData.SetMetaData(ManagedFunctionKey, FString(TEXT("true")));
			Entry->MetaData.SetMetaData(FunctionIdKey, FString(Declaration.SemanticId));
			Entry->MetaData.SetMetaData(
				ManagedFunctionNameKey,
				FString(Declaration.ManagedNameText));
			Entry->MetaData.SetMetaData(
				TEXT("DisplayName"),
				FString(Declaration.ManagedNameText));
			Entry->MetaData.SetMetaData(BlueprintEventKey, FString(TEXT("true")));
			Entry->MetaData.SetMetaData(BlueprintEventSuperModeKey, FString(TEXT("explicit")));
			Entry->MetaData.SetMetaData(
				BlueprintOverrideDeclaringTypePathKey,
				Declaration.BlueprintOverrideDeclaringTypePath);
			Entry->MetaData.SetMetaData(
				BlueprintOverrideFunctionNameKey,
				Declaration.BlueprintOverrideFunctionNameText);
			return true;
		}

		InOutGraph->Modify();
		InOutGraph->GraphGuid = Declaration.Id;
		TArray<UK2Node_FunctionEntry*> EntryNodes;
		TArray<UK2Node_FunctionResult*> ResultNodes;
		InOutGraph->GetNodesOfClass(EntryNodes);
		InOutGraph->GetNodesOfClass(ResultNodes);
		if (EntryNodes.Num() == 1 && ResultNodes.IsEmpty())
		{
			if (UK2Node_FunctionResult* CreatedResult =
				FBlueprintEditorUtils::FindOrCreateFunctionResultNode(EntryNodes[0]))
			{
				ResultNodes.Add(CreatedResult);
			}
		}
		if (EntryNodes.Num() != 1 || ResultNodes.Num() != 1)
		{
			OutError = FString::Printf(
				TEXT("Generated Blueprint function '%s' requires one entry and one result node."),
				*Declaration.ManagedName.ToString());
			return false;
		}

		UK2Node_FunctionEntry* Entry = EntryNodes[0];
		UK2Node_FunctionResult* Result = ResultNodes[0];
		Entry->Modify();
		Result->Modify();
		Entry->CustomGeneratedFunctionName = Declaration.InternalName;
		Entry->SetExtraFlags(
			FUNC_Public | FUNC_BlueprintCallable | FinareRpcFlags(Declaration.RpcTarget, Declaration.bReliable) |
			(Declaration.bIsStatic ? FUNC_Static : FUNC_None) |
			(Declaration.bBlueprintEvent
				 ? static_cast<int32>(FUNC_Event | FUNC_BlueprintEvent)
				 : FUNC_None));
		Entry->MetaData.Category = FText::FromString(DefaultCategory);
		Entry->MetaData.ToolTip = FText::FromString(FString::Printf(
			TEXT("Managed Script Class method %s"),
			*Declaration.ManagedName.ToString()));
		Entry->MetaData.SetMetaData(ManagedFunctionKey, FString(TEXT("true")));
		Entry->MetaData.SetMetaData(FunctionIdKey, FString(Declaration.SemanticId));
		Entry->MetaData.SetMetaData(
			ManagedFunctionNameKey,
			FString(Declaration.ManagedNameText));
		Entry->MetaData.SetMetaData(
			TEXT("DisplayName"),
			FString(Declaration.ManagedNameText));
		Entry->MetaData.SetMetaData(
			BlueprintEventKey,
			FString(Declaration.bBlueprintEvent ? TEXT("true") : TEXT("false")));
		Entry->MetaData.SetMetaData(
			BlueprintEventSuperModeKey,
			FString(Declaration.bBlueprintEvent ? TEXT("explicit") : TEXT("none")));
		Entry->MetaData.RemoveMetaData(BlueprintOverrideDeclaringTypePathKey);
		Entry->MetaData.RemoveMetaData(BlueprintOverrideFunctionNameKey);
		if (Declaration.bIsStatic)
		{
			Entry->MetaData.SetMetaData(
				TEXT("WorldContext"),
				FString(WorldContextObjectName.ToString()));
			Entry->MetaData.SetMetaData(
				TEXT("DefaultToSelf"),
				FString(WorldContextObjectName.ToString()));
		}
		else
		{
			Entry->MetaData.RemoveMetaData(TEXT("WorldContext"));
			Entry->MetaData.RemoveMetaData(TEXT("DefaultToSelf"));
		}

		if (!bCreated && FunctionGraphSignatureMatches(*InOutGraph, Declaration))
		{
			return true;
		}

		const TArray<TSharedPtr<FUserPinInfo>> PreviousEntryPins = Entry->UserDefinedPins;
		for (const TSharedPtr<FUserPinInfo>& Pin : PreviousEntryPins)
		{
			Entry->RemoveUserDefinedPin(Pin);
		}
		const TArray<TSharedPtr<FUserPinInfo>> PreviousResultPins = Result->UserDefinedPins;
		for (const TSharedPtr<FUserPinInfo>& Pin : PreviousResultPins)
		{
			Result->RemoveUserDefinedPin(Pin);
		}
		if (Declaration.bIsStatic &&
			Entry->CreateUserDefinedPin(
				WorldContextObjectName,
				MakeWorldContextPinType(),
				EGPD_Output,
				false) == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Unreal could not create WorldContextObject for managed static function '%s'."),
				*Declaration.ManagedName.ToString());
			return false;
		}
		for (const FFunctionParameterDeclaration& Parameter : Declaration.Parameters)
		{
			UK2Node_EditablePinBase* PinOwner = Parameter.Direction ==
			                                    EFinareManagedParameterDirection::Output
				                                    ? static_cast<UK2Node_EditablePinBase*>(Result)
				                                    : static_cast<UK2Node_EditablePinBase*>(Entry);
			const EEdGraphPinDirection PinDirection = Parameter.Direction ==
			                                          EFinareManagedParameterDirection::Output
				                                          ? EGPD_Input
				                                          : EGPD_Output;
			if (PinOwner->CreateUserDefinedPin(
				Parameter.Name,
				Parameter.PinType,
				PinDirection,
				false) == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Unreal could not create parameter '%s' for managed function '%s'."),
					*Parameter.Name.ToString(),
					*Declaration.ManagedName.ToString());
				return false;
			}
		}
		if (Declaration.bHasReturnValue &&
			Result->CreateUserDefinedPin(
				UEdGraphSchema_K2::PN_ReturnValue,
				Declaration.ReturnPinType,
				EGPD_Input,
				false) == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Unreal could not create the return pin for managed function '%s'."),
				*Declaration.ManagedName.ToString());
			return false;
		}
		Entry->ReconstructNode();
		Result->ReconstructNode();
		Entry->BreakAllNodeLinks();
		Result->BreakAllNodeLinks();
		UEdGraphPin* ThenPin = Entry->FindPin(UEdGraphSchema_K2::PN_Then, EGPD_Output);
		UEdGraphPin* ExecutePin = Result->FindPin(UEdGraphSchema_K2::PN_Execute, EGPD_Input);
		const UEdGraphSchema_K2* Schema = GetDefault<UEdGraphSchema_K2>();
		if (ThenPin == nullptr || ExecutePin == nullptr ||
			!Schema->TryCreateConnection(ThenPin, ExecutePin))
		{
			OutError = FString::Printf(
				TEXT("Unreal could not connect generated function '%s' entry to its result."),
				*Declaration.ManagedName.ToString());
			return false;
		}
		return true;
	}

	struct FScopedBlueprintStructuralSuppression
	{
		UBlueprint& Blueprint;
		const bool bEnabled;
		const uint8 PreviousValue;
		bool bRestored = false;

		FScopedBlueprintStructuralSuppression(
			UBlueprint& InBlueprint,
			const bool bInEnabled)
			: Blueprint(InBlueprint)
			  , bEnabled(bInEnabled)
			  , PreviousValue(InBlueprint.bSuppressStructurallyModified)
		{
			if (bEnabled)
			{
				Blueprint.bSuppressStructurallyModified = true;
			}
		}

		void Restore()
		{
			if (!bRestored)
			{
				Blueprint.bSuppressStructurallyModified = PreviousValue;
				bRestored = true;
			}
		}

		~FScopedBlueprintStructuralSuppression()
		{
			Restore();
		}
	};

	bool ApplyPlan(
		UBlueprint& Blueprint,
		const FSyncPlan& Plan,
		FString& OutError,
		const bool bCombinedParentChange)
	{
		FScopedBlueprintStructuralSuppression StructuralSuppression(
			Blueprint,
			bCombinedParentChange);
		Blueprint.Modify();
		for (const FName& StaleName : Plan.StaleGeneratedVariables)
		{
			FBlueprintEditorUtils::RemoveMemberVariable(&Blueprint, StaleName);
		}
		for (const FGuid& StaleFunctionId : Plan.StaleGeneratedFunctionIds)
		{
			UEdGraph* StaleGraph = FindManagedFunctionGraphById(
				Blueprint,
				StaleFunctionId);
			if (StaleGraph != nullptr)
			{
				FBlueprintEditorUtils::RemoveGraph(
					&Blueprint,
					StaleGraph,
					EGraphRemoveFlags::MarkTransient);
			}
		}

		for (const FString& InterfacePath : Plan.InterfacePathsToRemove)
		{
			UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
			if (InterfaceClass == nullptr ||
				!InterfaceClass->HasAnyClassFlags(CLASS_Interface))
			{
				OutError = FString::Printf(
					TEXT("Cannot remove generated interface '%s' because its native class is unavailable."),
					*InterfacePath);
				return false;
			}
			FBlueprintEditorUtils::RemoveInterface(
				&Blueprint,
				InterfaceClass->GetClassPathName(),
				false);
		}
		for (const FString& InterfacePath : Plan.InterfacePathsToAdd)
		{
			UClass* InterfaceClass = LoadObject<UClass>(nullptr, *InterfacePath);
			if (InterfaceClass == nullptr ||
				!InterfaceClass->HasAnyClassFlags(CLASS_Interface))
			{
				OutError = FString::Printf(
					TEXT("Cannot implement generated interface '%s' because its native class is unavailable."),
					*InterfacePath);
				return false;
			}
			if (!FBlueprintEditorUtils::ImplementNewInterface(
				&Blueprint,
				InterfaceClass->GetClassPathName()))
			{
				OutError = FString::Printf(
					TEXT("Unreal could not implement generated interface '%s' on '%s'."),
					*InterfacePath,
					*Blueprint.GetPathName());
				return false;
			}
		}

		TMap<FGuid, FName> TemporaryNames;
		for (const FPlannedState& Planned : Plan.States)
		{
			if (!Planned.bExisting || Planned.ExistingName == Planned.Declaration.Name)
			{
				continue;
			}
			const int32 ExistingIndex = FindVariableByStateId(
				Blueprint,
				Planned.Declaration.SemanticId);
			if (ExistingIndex == INDEX_NONE)
			{
				OutError = FString::Printf(
					TEXT("Cannot find generated state '%s' during rename."),
					*Planned.Declaration.SemanticId);
				return false;
			}
			const FName TemporaryName = MakeTemporaryName(Blueprint, Planned.Declaration.Id);
			FBlueprintEditorUtils::RenameMemberVariable(
				&Blueprint,
				Blueprint.NewVariables[ExistingIndex].VarName,
				TemporaryName);
			TemporaryNames.Add(Planned.Declaration.Id, TemporaryName);
		}

		for (const FPlannedState& Planned : Plan.States)
		{
			if (Planned.bExisting)
			{
				continue;
			}
			if (!FBlueprintEditorUtils::AddMemberVariable(
				&Blueprint,
				Planned.Declaration.Name,
				Planned.Declaration.PinType,
				Planned.AppliedDefault))
			{
				OutError = FString::Printf(
					TEXT("Unreal could not add generated Blueprint property '%s'."),
					*Planned.Declaration.Name.ToString());
				return false;
			}
			const int32 AddedIndex = FBlueprintEditorUtils::FindNewVariableIndex(
				&Blueprint,
				Planned.Declaration.Name);
			if (AddedIndex == INDEX_NONE)
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint property '%s' disappeared after creation."),
					*Planned.Declaration.Name.ToString());
				return false;
			}
			Blueprint.NewVariables[AddedIndex].VarGuid = Planned.Declaration.Id;
		}

		for (const FPlannedState& Planned : Plan.States)
		{
			if (const FName* TemporaryName = TemporaryNames.Find(Planned.Declaration.Id))
			{
				FBlueprintEditorUtils::RenameMemberVariable(
					&Blueprint,
					*TemporaryName,
					Planned.Declaration.Name);
			}

			int32 VariableIndex = FindVariableByGuid(Blueprint, Planned.Declaration.Id);
			if (VariableIndex == INDEX_NONE)
			{
				VariableIndex = FindVariableByStateId(
					Blueprint,
					Planned.Declaration.SemanticId);
			}
			if (VariableIndex == INDEX_NONE)
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint property '%s' cannot be finalized."),
					*Planned.Declaration.Name.ToString());
				return false;
			}

			FBPVariableDescription& Variable = Blueprint.NewVariables[VariableIndex];
			Variable.VarName = Planned.Declaration.Name;
			Variable.VarGuid = Planned.Declaration.Id;
			Variable.VarType = Planned.Declaration.PinType;
			Variable.FriendlyName = FName::NameToDisplayString(
				Planned.Declaration.Name.ToString(),
				Planned.Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_Boolean);
			Variable.Category = FText::FromString(Planned.Declaration.Category);
			Variable.PropertyFlags = Planned.Declaration.PropertyFlags;
			Variable.RepNotifyFunc = Planned.Declaration.RepNotifyFunc;
			Variable.ReplicationCondition = Planned.Declaration.ReplicationCondition;
			Variable.DefaultValue = Planned.AppliedDefault;
			Variable.SetMetaData(ManagedStateKey, TEXT("true"));
			Variable.SetMetaData(StateIdKey, Planned.Declaration.SemanticId);
			Variable.SetMetaData(
				TypeMetadataNameKey,
				Planned.Declaration.TypeMetadataName);
			Variable.SetMetaData(
				DeclarationDefaultKey,
				Planned.Declaration.DefaultValue);
			if (Planned.Declaration.PinType.PinCategory == UEdGraphSchema_K2::PC_String)
			{
				Variable.SetMetaData(TEXT("MultiLine"), TEXT("true"));
			}
			else
			{
				Variable.RemoveMetaData(TEXT("MultiLine"));
			}
		}

		for (const FPlannedFunction& Planned : Plan.Functions)
		{
			if (!Planned.bNeedsUpdate)
			{
				continue;
			}
			UEdGraph* Graph = Planned.Declaration.bInterfaceFunction
				                  ? FindFunctionGraphByName(Blueprint, Planned.Declaration.InternalName)
				                  : (Planned.bExisting
					                     ? FindManagedFunctionGraphById(Blueprint, Planned.Declaration.Id)
					                     : nullptr);
			if ((Planned.bExisting || Planned.Declaration.bInterfaceFunction) &&
				Graph == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Generated Blueprint function '%s' disappeared before materialization."),
					*Planned.Declaration.ManagedName.ToString());
				return false;
			}
			if (!ConfigureFunctionGraph(
				Blueprint,
				Planned.Declaration,
				Graph,
				OutError))
			{
				return false;
			}
		}

		// Parent preparation is deliberately separate from compilation.  Refresh
		// generated nodes only after stale members and renamed declarations have
		// been applied, so a removed override is never reconstructed against the
		// old generated class before the combined compile.
		StructuralSuppression.Restore();
		if (bCombinedParentChange)
		{
			FBlueprintEditorUtils::RefreshAllNodes(&Blueprint);
		}
		FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
		FKismetEditorUtilities::CompileBlueprint(&Blueprint);
		if (Blueprint.Status == BS_Error)
		{
			OutError = FString::Printf(
				TEXT("Generated Blueprint '%s' failed to compile after state materialization."),
				*Blueprint.GetPathName());
			return false;
		}
		UFinareBlueprintGeneratedClass* GeneratedClass =
			Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		if ((!Plan.Functions.IsEmpty() || !Plan.LifecycleBindings.IsEmpty() ||
				!Plan.InputBindings.IsEmpty() || !Plan.DelegateBindings.IsEmpty() ||
				!Plan.InterfaceBindings.IsEmpty()) &&
			GeneratedClass == nullptr)
		{
			if (Blueprint.GetOuter() == GetTransientPackage())
			{
				// StaticDuplicateObject uses the stock generated-class type for the
				// disposable preflight copy. Its graph compile is the required
				// validation; persistent binding is applied to the exact proxy.
				return true;
			}
			OutError = FString::Printf(
				TEXT(
					"Generated Blueprint '%s' predates the managed-function class carrier. Recreate or migrate the Script Class proxy before exposing managed functions."),
				*Blueprint.GetPathName());
			return false;
		}
		if (GeneratedClass != nullptr)
		{
			GeneratedClass->Modify();
			const UFinareBlueprint* UnifiedBlueprint =
				Cast<UFinareBlueprint>(&Blueprint);
			GeneratedClass->ManagedScriptAsset = UnifiedBlueprint != nullptr
				                                     ? UnifiedBlueprint->ScriptAsset
				                                     : nullptr;
			GeneratedClass->ManagedLifecycleBindings = Plan.LifecycleBindings;
			GeneratedClass->ManagedInputActionBindings = Plan.InputBindings;
			GeneratedClass->ManagedDelegateBindings = Plan.DelegateBindings;
			GeneratedClass->ManagedInterfaceBindings = Plan.InterfaceBindings;
			GeneratedClass->ManagedFunctionBindings.Reset();
			for (const FPlannedFunction& Planned : Plan.Functions)
			{
				FFinareManagedFunctionBinding Binding;
				Binding.ManagedName = Planned.Declaration.ManagedName.ToString();
				Binding.FunctionId = Planned.Declaration.Id;
				Binding.AbiVersion = 3;
				Binding.bStatic = Planned.Declaration.bIsStatic;
				Binding.RpcTarget = Planned.Declaration.RpcTarget;
				Binding.bReliable = Planned.Declaration.bReliable;
				Binding.RepNotifyProperty = Planned.Declaration.RepNotifyProperty;
				Binding.bBlueprintEvent = Planned.Declaration.bBlueprintEvent;
				Binding.bInterfaceFunction =
					Planned.Declaration.bInterfaceFunction;
				Binding.BlueprintEventSuperMode =
					Planned.Declaration.BlueprintEventSuperMode;
				Binding.BlueprintOverrideDeclaringTypePath =
					Planned.Declaration.BlueprintOverrideDeclaringTypePath;
				Binding.BlueprintOverrideFunctionName =
					Planned.Declaration.BlueprintOverrideFunctionNameText;
				Binding.ReturnType = MakeRuntimeValueTypeBinding(
					Planned.Declaration.ReturnValueType);
				Binding.ParameterTypes.Reserve(Planned.Declaration.Parameters.Num());
				Binding.ParameterNames.Reserve(Planned.Declaration.Parameters.Num());
				Binding.ParameterDirections.Reserve(
					Planned.Declaration.Parameters.Num());
				for (const FFunctionParameterDeclaration& Parameter :
				     Planned.Declaration.Parameters)
				{
					Binding.ParameterTypes.Add(
						MakeRuntimeValueTypeBinding(Parameter.ValueType));
					Binding.ParameterNames.Add(EffectiveParameterName(Parameter));
					Binding.ParameterDirections.Add(Parameter.Direction);
				}
				GeneratedClass->ManagedFunctionBindings.Add(
					Planned.Declaration.InternalName,
					MoveTemp(Binding));
			}
			GeneratedClass->BindManagedFunctions();
			const bool bNetworked = Plan.States.ContainsByPredicate([](const FPlannedState& S)
				{
					return (S.Declaration.PropertyFlags & CPF_Net) != 0;
				}) ||
				Plan.Functions.ContainsByPredicate([](const FPlannedFunction& F)
				{
					return F.Declaration.RpcTarget != EFinareRpcTarget::None;
				});
			if (bNetworked)
			{
				if (AActor* Actor = Cast<AActor>(GeneratedClass->GetDefaultObject()))
				{
					Actor->SetReplicates(true);
				}
				if (UActorComponent* Component = Cast<UActorComponent>(GeneratedClass->GetDefaultObject()))
				{
					Component->
						SetIsReplicated(true);
				}
			}
			for (const FPlannedFunction& Planned : Plan.Functions)
			{
				if (!GeneratedClass->IsManagedFunctionBound(
					Planned.Declaration.InternalName))
				{
					OutError = FString::Printf(
						TEXT("Generated Blueprint function '%s' did not bind its native managed dispatcher."),
						*Planned.Declaration.ManagedName.ToString());
					return false;
				}
			}
		}
		return true;
	}

	/**
	 * Blueprint reinstancing normally delta-serializes collection properties entry
	 * by entry.  That is correct for ordinary Blueprint maps, but a Script Class
	 * state is one stable managed property: changing its default must not turn an
	 * explicitly overridden collection into a hybrid of the old and new defaults.
	 * Keep the complete value for compatible collection states while the generated
	 * class is rebuilt, then restore it through the engine replacement map.
	 */
	struct FCollectionInstanceOverrideSnapshot
	{
		UObject* OldObject = nullptr;
		TStrongObjectPtr<UObject> OldObjectRoot;
		TStrongObjectPtr<UObject> OldArchetypeRoot;
		FSnapshotObjectIdentity ObjectIdentity;
		bool bFollowsArchetype = false;
		FName OldPropertyName;
		FName NewPropertyName;
		FGuid StateId;
		FInstancedPropertyBag Value;
	};

	UObject* ResolveReplacedObject(
		const FObjectKey& OldObjectKey,
		const FCollectionReplacementMap& ReplacedObjects,
		bool& bFoundReplacement,
		bool* bExplicitNull)
	{
		bFoundReplacement = false;
		if (bExplicitNull != nullptr)
		{
			*bExplicitNull = false;
		}
		FObjectKey CurrentKey = OldObjectKey;
		TSet<FObjectKey> Visited;
		while (true)
		{
			if (Visited.Contains(CurrentKey))
			{
				return nullptr;
			}
			Visited.Add(CurrentKey);
			const FCollectionReplacementTarget* Next = ReplacedObjects.Find(CurrentKey);
			if (Next == nullptr)
			{
				return CurrentKey.ResolveObjectPtrEvenIfUnreachable();
			}
			bFoundReplacement = true;
			if (Next->bExplicitNull)
			{
				if (bExplicitNull != nullptr)
				{
					*bExplicitNull = true;
				}
				return nullptr;
			}
			if (Next->Identity == FObjectKey())
			{
				return nullptr;
			}
			// A reinstance can publish A->B and then retire B before publishing
			// B->C.  Continue through B by stable object identity so a recycled
			// address can never select an unrelated replacement entry.
			CurrentKey = Next->Identity;
		}
	}

	struct FReplacementChainDiagnostics
	{
		bool bFoundReplacement = false;
		bool bSelfEdge = false;
		bool bCycle = false;
	};

	FReplacementChainDiagnostics InspectReplacementChain(
		const FObjectKey& OldObjectKey,
		const FCollectionReplacementMap& ReplacedObjects)
	{
		FReplacementChainDiagnostics Diagnostics;
		FObjectKey CurrentKey = OldObjectKey;
		TSet<FObjectKey> Visited;
		while (true)
		{
			if (Visited.Contains(CurrentKey))
			{
				Diagnostics.bCycle = true;
				return Diagnostics;
			}
			Visited.Add(CurrentKey);
			const FCollectionReplacementTarget* Next = ReplacedObjects.Find(CurrentKey);
			if (Next == nullptr)
			{
				return Diagnostics;
			}
			Diagnostics.bFoundReplacement = true;
			if (Next->bExplicitNull || Next->Identity == FObjectKey())
			{
				return Diagnostics;
			}
			if (Next->Identity == CurrentKey)
			{
				Diagnostics.bSelfEdge = true;
				return Diagnostics;
			}
			if (Visited.Contains(Next->Identity))
			{
				Diagnostics.bCycle = true;
				return Diagnostics;
			}
			CurrentKey = Next->Identity;
		}
	}

	UObject* ResolveSnapshotObject(
		FSnapshotObjectIdentity& Identity,
		UObject* OldObjectRoot,
		const FCollectionReplacementMap& ReplacedObjects,
		bool& bFoundReplacement,
		bool* bExplicitNull)
	{
		bFoundReplacement = false;
		if (bExplicitNull != nullptr)
		{
			*bExplicitNull = false;
		}
		if (Identity.bClassDefaultObject && Identity.OwnerBlueprint.IsValid())
		{
			UClass* CurrentGeneratedClass = Identity.OwnerBlueprint->GeneratedClass.Get();
			UObject* CurrentCdo = CurrentGeneratedClass != nullptr
				                      ? CurrentGeneratedClass->GetDefaultObject(false)
				                      : nullptr;
			if (CurrentCdo == nullptr || CurrentCdo->GetClass() != CurrentGeneratedClass ||
				CurrentGeneratedClass->ClassGeneratedBy != Identity.OwnerBlueprint.Get())
			{
				return nullptr;
			}
			// A CDO's owner Blueprint is the stable authoring identity.  Do not
			// resolve it through a raw replacement address that may now belong to
			// a component or another reinstanced object.
			bFoundReplacement = CurrentCdo != Identity.Key.ResolveObjectPtrEvenIfUnreachable();
			return CurrentCdo;
		}

		UObject* NewObject = ResolveReplacedObject(
			Identity.Key,
			ReplacedObjects,
			bFoundReplacement,
			bExplicitNull);
		if (NewObject == nullptr && !bFoundReplacement)
		{
			NewObject = OldObjectRoot;
		}
		if (NewObject == nullptr)
		{
			return nullptr;
		}
		if (Identity.OwnerBlueprint.IsValid())
		{
			UClass* CurrentGeneratedClass = Identity.OwnerBlueprint->GeneratedClass.Get();
			if (CurrentGeneratedClass == nullptr || NewObject->GetClass() != CurrentGeneratedClass ||
				CurrentGeneratedClass->ClassGeneratedBy != Identity.OwnerBlueprint.Get())
			{
				return nullptr;
			}
		}
		else if (Identity.ExpectedClass.IsValid() &&
			NewObject->GetClass() != Identity.ExpectedClass.Get())
		{
			return nullptr;
		}
		return NewObject;
	}

	class FSnapshotObjectReferenceCollector final : public FArchiveUObject
	{
	public:
		FSnapshotObjectReferenceCollector()
		{
			ArIsObjectReferenceCollector = true;
			ArIsModifyingWeakAndStrongReferences = true;
		}

		virtual FArchive& operator<<(UObject*& Value) override
		{
			if (Value != nullptr)
			{
				ReferencedKeys.AddUnique(FObjectKey(Value));
			}
			return *this;
		}

		TArray<FObjectKey> ReferencedKeys;
	};

	bool ValidateSnapshotReplacementReferences(
		const FProperty* SnapshotProperty,
		void* SnapshotContainer,
		const FCollectionReplacementMap& ReplacedObjects,
		const FString& SnapshotDescription,
		FString& OutError)
	{
		if (SnapshotProperty == nullptr || SnapshotContainer == nullptr)
		{
			OutError.Reset();
			return true;
		}

		FSnapshotObjectReferenceCollector Collector;
		FStructuredArchiveFromArchive StructuredArchive(Collector);
		SnapshotProperty->SerializeItem(StructuredArchive.GetSlot(), SnapshotContainer);
		for (const FObjectKey& ReferencedKey : Collector.ReferencedKeys)
		{
			bool bFoundReplacement = false;
			bool bExplicitNull = false;
			UObject* FinalObject = ResolveReplacedObject(
				ReferencedKey,
				ReplacedObjects,
				bFoundReplacement,
				&bExplicitNull);
			if (bExplicitNull)
			{
				OutError = FString::Printf(
					TEXT(
						"Snapshot '%s' contains an intentional object reference whose owner was explicitly destroyed without an authorized replacement."),
					*SnapshotDescription);
				return false;
			}
			if (FinalObject == nullptr)
			{
				OutError = FString::Printf(
					TEXT(
						"Snapshot '%s' contains an object reference whose replacement identity has no live terminal object."),
					*SnapshotDescription);
				return false;
			}
			if (FinalObject->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed))
			{
				OutError = FString::Printf(
					TEXT(
						"Snapshot '%s' contains an object reference whose replacement terminal is destroyed or garbage."),
					*SnapshotDescription);
				return false;
			}
		}
		OutError.Reset();
		return true;
	}

	TMap<UObject*, UObject*> FlattenReplacedObjects(
		const FCollectionReplacementMap& ReplacedObjects)
	{
		TMap<UObject*, UObject*> Flattened;
		for (const TPair<FObjectKey, FCollectionReplacementTarget>& Pair : ReplacedObjects)
		{
			UObject* OldObject = Pair.Key.ResolveObjectPtrEvenIfUnreachable();
			if (OldObject == nullptr || FObjectKey(OldObject) != Pair.Key)
			{
				// The original object is gone and no GC rooted snapshot can still
				// require an archive replacement for this key.
				continue;
			}
			bool bFoundReplacement = false;
			bool bExplicitNull = false;
			UObject* FinalObject = ResolveReplacedObject(
				Pair.Key,
				ReplacedObjects,
				bFoundReplacement,
				&bExplicitNull);
			if (!bFoundReplacement)
			{
				continue;
			}
			if (FinalObject != nullptr &&
				!FinalObject->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed) &&
				FObjectKey(FinalObject) != FObjectKey())
			{
				// Unreachable objects can still participate in UE's replacement
				// semantics, but destroyed or mirrored-garbage terminals are no
				// longer valid values to write into a snapshot.  Actual snapshot
				// references to one are rejected by the validator above.
				Flattened.Add(OldObject, FinalObject);
			}
			else if (bExplicitNull)
			{
				// An explicit old->nullptr publication is different from a chain
				// whose terminal object disappeared; only the former clears refs.
				Flattened.Add(OldObject, nullptr);
			}
		}
		return Flattened;
	}

	FProperty* FindStateProperty(
		UClass* Class,
		const FGuid& StateId,
		FName FallbackName)
	{
		if (Class == nullptr)
		{
			return nullptr;
		}
		for (TFieldIterator<FProperty> It(Class); It; ++It)
		{
			FGuid PropertyStateId;
			if (It->HasMetaData(StateIdKey) &&
				ParseSemanticId(It->GetMetaData(StateIdKey), PropertyStateId) &&
				PropertyStateId == StateId)
			{
				return *It;
			}
		}
		return Class->FindPropertyByName(FallbackName);
	}

	bool CaptureManagedStateValueSnapshots(
		const UBlueprint& Blueprint,
		const FSyncPlan& Plan,
		TArray<FManagedStateValueSnapshot>& OutSnapshots,
		FString& OutError)
	{
		OutSnapshots.Reset();
		if (Blueprint.GeneratedClass == nullptr)
		{
			OutError.Reset();
			return true;
		}

		struct FManagedVariableToSnapshot
		{
			FGuid StateId;
			FName OldPropertyName;
			FName NewPropertyName;
			bool bRestoreOnSuccess = true;
		};

		TMap<FGuid, const FPlannedState*> PlannedStatesById;
		for (const FPlannedState& Planned : Plan.States)
		{
			PlannedStatesById.Add(Planned.Declaration.Id, &Planned);
		}

		TArray<UObject*> Objects;
		GetObjectsOfClass(Blueprint.GeneratedClass, Objects, true, RF_MirroredGarbage);
		for (UObject* Object : Objects)
		{
			if (Object == nullptr ||
				Object->HasAnyFlags(RF_BeginDestroyed | RF_FinishDestroyed | RF_MirroredGarbage) ||
				Object->GetClass() == nullptr ||
				!Object->GetClass()->IsChildOf(Blueprint.GeneratedClass))
			{
				continue;
			}
			UBlueprint* GeneratedBy = Cast<UBlueprint>(Object->GetClass()->ClassGeneratedBy);
			if (GeneratedBy == nullptr || GeneratedBy->GeneratedClass != Object->GetClass())
			{
				continue;
			}
			const bool bRootClassDefaultObject =
				Object == Blueprint.GeneratedClass->GetDefaultObject(false);
			const bool bDerivedClassDefaultObject =
				Object->HasAnyFlags(RF_ClassDefaultObject) && Object->GetClass() != Blueprint.GeneratedClass;
			if (!bRootClassDefaultObject && !bDerivedClassDefaultObject && Object->IsTemplate())
			{
				continue;
			}

			// GetObjectsOfClass(includeDerived=true) includes live instances and
			// CDOs for every generated class below the selected owner.  Walk the
			// object's actual generated-class chain back to the selected owner,
			// reading each declaration owner's NewVariables.  This includes
			// inherited selected-owner fields on a descendant object while still
			// keeping a descendant's own state on its original name.
			TArray<FManagedVariableToSnapshot> ManagedVariables;
			TSet<FGuid> SeenStateIds;
			for (UClass* DeclaringClass = Object->GetClass();
			     DeclaringClass != nullptr;
			     DeclaringClass = DeclaringClass->GetSuperClass())
			{
				const UBlueprint* DeclaringBlueprint = Cast<UBlueprint>(
					DeclaringClass->ClassGeneratedBy);
				if (DeclaringBlueprint == nullptr)
				{
					if (DeclaringClass == Blueprint.GeneratedClass)
					{
						break;
					}
					continue;
				}
				for (const FBPVariableDescription& Variable : DeclaringBlueprint->NewVariables)
				{
					if (!IsManagedState(Variable) || !Variable.HasMetaData(StateIdKey))
					{
						continue;
					}
					FGuid StateId;
					if (!ParseSemanticId(Variable.GetMetaData(StateIdKey), StateId) ||
						SeenStateIds.Contains(StateId))
					{
						continue;
					}
					SeenStateIds.Add(StateId);
					FManagedVariableToSnapshot& ManagedVariable = ManagedVariables.AddDefaulted_GetRef();
					ManagedVariable.StateId = StateId;
					ManagedVariable.OldPropertyName = Variable.VarName;
					ManagedVariable.NewPropertyName = Variable.VarName;
					if (DeclaringBlueprint == &Blueprint)
					{
						if (const FPlannedState* const* Planned = PlannedStatesById.Find(StateId))
						{
							ManagedVariable.NewPropertyName = (*Planned)->Declaration.Name;
							if (Object == Blueprint.GeneratedClass->GetDefaultObject(false))
							{
								ManagedVariable.bRestoreOnSuccess = (*Planned)->bPreserveDefaultOverride;
							}
						}
						else
						{
							// A removed selected-owner state is restored for
							// rollback but has no success target in the candidate
							// class.
							ManagedVariable.NewPropertyName = NAME_None;
						}
					}
				}
				if (DeclaringClass == Blueprint.GeneratedClass)
				{
					break;
				}
			}
			UObject* OldArchetype = Object->GetArchetype();
			for (const FManagedVariableToSnapshot& Variable : ManagedVariables)
			{
				FProperty* OldProperty = FindStateProperty(
					Object->GetClass(),
					Variable.StateId,
					Variable.OldPropertyName);
				if (OldProperty == nullptr || OldProperty->HasAnyPropertyFlags(CPF_Transient))
				{
					OutError = FString::Printf(
						TEXT("Managed state '%s' lost its reflected property on '%s' before typed preservation."),
						*Variable.OldPropertyName.ToString(),
						*Object->GetPathName());
					return false;
				}
				FProperty* ArchetypeProperty = OldArchetype != nullptr && OldArchetype != Object
					                               ? FindStateProperty(OldArchetype->GetClass(), Variable.StateId,
					                                                   Variable.OldPropertyName)
					                               : nullptr;
				if (ArchetypeProperty != nullptr &&
					!ArePropertiesExactlyCompatible(OldProperty, ArchetypeProperty))
				{
					OutError = FString::Printf(
						TEXT("Managed state '%s' has an incompatible immediate archetype on '%s'."),
						*Variable.OldPropertyName.ToString(),
						*Object->GetPathName());
					return false;
				}
				const bool bFollowsArchetype = ArchetypeProperty != nullptr &&
					AreTypedPropertyValuesIdentical(
						OldProperty,
						Object,
						ArchetypeProperty,
						OldArchetype);
				FManagedStateValueSnapshot& Snapshot = OutSnapshots.AddDefaulted_GetRef();
				Snapshot.OldObject = Object;
				Snapshot.OldObjectRoot = TStrongObjectPtr<UObject>(Object);
				Snapshot.OldArchetypeRoot = TStrongObjectPtr<UObject>(OldArchetype);
				Snapshot.ObjectIdentity.Capture(Object);
				Snapshot.bFollowsArchetype = bFollowsArchetype;
				Snapshot.StateId = Variable.StateId;
				Snapshot.OldPropertyName = Variable.OldPropertyName;
				Snapshot.NewPropertyName = Variable.NewPropertyName;
				Snapshot.bRestoreOnSuccess = Variable.bRestoreOnSuccess;
				if (bFollowsArchetype)
				{
					continue;
				}
				if (Snapshot.Value.AddProperty(CollectionSnapshotValueName, OldProperty) !=
					EPropertyBagAlterationResult::Success)
				{
					OutError = FString::Printf(
						TEXT("Managed state '%s' on '%s' cannot be represented by a typed preservation snapshot."),
						*Variable.OldPropertyName.ToString(),
						*Object->GetPathName());
					return false;
				}
				const FPropertyBagPropertyDesc* SnapshotDesc =
					Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
				if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Managed state '%s' produced an incomplete typed preservation snapshot."),
						*Variable.OldPropertyName.ToString());
					return false;
				}
				CopyTypedPropertyValue(
					SnapshotDesc->CachedProperty,
					Snapshot.Value.GetMutableValue().GetMemory(),
					OldProperty,
					Object);
			}
		}
		OutError.Reset();
		return true;
	}

	bool RestoreManagedStateValueSnapshots(
		TArray<FManagedStateValueSnapshot>& Snapshots,
		const FCollectionReplacementMap& ReplacedObjects,
		const bool bRollback,
		FString& OutError)
	{
		const TMap<UObject*, UObject*> FlattenedReplacedObjects =
			FlattenReplacedObjects(ReplacedObjects);
		TArray<int32> SnapshotOrder;
		SnapshotOrder.Reserve(Snapshots.Num());
		for (int32 Index = 0; Index < Snapshots.Num(); ++Index)
		{
			SnapshotOrder.Add(Index);
		}
		const auto ResolveSnapshotTarget =
			[&ReplacedObjects](FManagedStateValueSnapshot& Snapshot, bool& bFoundReplacement)
		{
			UObject* NewObject = ResolveSnapshotObject(
				Snapshot.ObjectIdentity,
				Snapshot.OldObjectRoot.Get(),
				ReplacedObjects,
				bFoundReplacement);
			return NewObject;
		};
		const auto GetArchetypeDepth = [](UObject* Object)
		{
			int32 Depth = 0;
			TSet<UObject*> Visited;
			for (UObject* Current = Object != nullptr ? Object->GetArchetype() : nullptr;
			     Current != nullptr && !Visited.Contains(Current) && Depth < 256;
			     Current = Current->GetArchetype())
			{
				Visited.Add(Current);
				++Depth;
			}
			return Depth;
		};
		SnapshotOrder.Sort(
			[&Snapshots, &ResolveSnapshotTarget, &GetArchetypeDepth](int32 Left, int32 Right)
			{
				bool bLeftReplaced = false;
				bool bRightReplaced = false;
				const int32 LeftDepth = GetArchetypeDepth(
					ResolveSnapshotTarget(Snapshots[Left], bLeftReplaced));
				const int32 RightDepth = GetArchetypeDepth(
					ResolveSnapshotTarget(Snapshots[Right], bRightReplaced));
				return LeftDepth == RightDepth ? Left < Right : LeftDepth < RightDepth;
			});

		for (const int32 SnapshotIndex : SnapshotOrder)
		{
			FManagedStateValueSnapshot& Snapshot = Snapshots[SnapshotIndex];
			const FName TargetName = bRollback
				                         ? Snapshot.OldPropertyName
				                         : Snapshot.NewPropertyName;
			if (TargetName.IsNone() || (!bRollback && !Snapshot.bRestoreOnSuccess &&
				!Snapshot.bFollowsArchetype))
			{
				continue;
			}
			bool bFoundReplacement = false;
			UObject* NewObject = ResolveSnapshotTarget(Snapshot, bFoundReplacement);
			if (NewObject == nullptr || NewObject->HasAnyFlags(RF_MirroredGarbage) ||
				NewObject->GetClass() == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Managed state '%s' lost its snapshotted owner during Blueprint reinstancing."),
					*TargetName.ToString());
				return false;
			}
			FProperty* NewProperty = FindStateProperty(
				NewObject->GetClass(),
				Snapshot.StateId,
				TargetName);
			if (NewProperty == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Managed state '%s' could not be restored on '%s' after Blueprint reinstancing."),
					*TargetName.ToString(),
					*NewObject->GetPathName());
				return false;
			}
			void* NewValue = NewProperty->ContainerPtrToValuePtr<void>(NewObject);
			if (Snapshot.bFollowsArchetype)
			{
				UObject* NewArchetype = NewObject->GetArchetype();
				FProperty* NewArchetypeProperty = NewArchetype != nullptr && NewArchetype != NewObject
					                                  ? FindStateProperty(
						                                  NewArchetype->GetClass(), Snapshot.StateId, TargetName)
					                                  : nullptr;
				if (NewArchetype == nullptr || NewArchetypeProperty == nullptr ||
					!ArePropertiesExactlyCompatible(NewProperty, NewArchetypeProperty))
				{
					OutError = FString::Printf(
						TEXT(
							"Managed state follower '%s' lost its immediate archetype on '%s' after Blueprint reinstancing."),
						*TargetName.ToString(),
						*NewObject->GetPathName());
					return false;
				}
				CopyTypedPropertyValue(
					NewProperty,
					NewObject,
					NewArchetypeProperty,
					NewArchetype);
			}
			else
			{
				const FPropertyBagPropertyDesc* SnapshotDesc =
					Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
				if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr ||
					!ArePropertiesExactlyCompatible(NewProperty, SnapshotDesc->CachedProperty))
				{
					OutError = FString::Printf(
						TEXT("Managed state '%s' changed reflected identity while restoring '%s'."),
						*Snapshot.OldPropertyName.ToString(),
						*NewObject->GetPathName());
					return false;
				}
				void* SnapshotValue = SnapshotDesc->CachedProperty->ContainerPtrToValuePtr<void>(
					Snapshot.Value.GetMutableValue().GetMemory());
				if (!ValidateSnapshotReplacementReferences(
					SnapshotDesc->CachedProperty,
					SnapshotValue,
					ReplacedObjects,
					FString::Printf(
						TEXT("managed state '%s' on '%s'"),
						*Snapshot.OldPropertyName.ToString(),
						*NewObject->GetPathName()),
					OutError))
				{
					return false;
				}
				FArchiveReplaceObjectRef<UObject> ReplaceArchive(
					NewObject,
					FlattenedReplacedObjects,
					EArchiveReplaceObjectFlags::DelayStart);
				FStructuredArchiveFromArchive StructuredArchive(ReplaceArchive);
				SnapshotDesc->CachedProperty->SerializeItem(StructuredArchive.GetSlot(), SnapshotValue);
				CopyTypedPropertyValue(
					NewProperty,
					NewObject,
					SnapshotDesc->CachedProperty,
					Snapshot.Value.GetMutableValue().GetMemory());
			}
			if (const FSetProperty* SetProperty = CastField<FSetProperty>(NewProperty))
			{
				FScriptSetHelper SetHelper(SetProperty, NewValue);
				SetHelper.Rehash();
			}
			else if (const FMapProperty* MapProperty = CastField<FMapProperty>(NewProperty))
			{
				FScriptMapHelper MapHelper(MapProperty, NewValue);
				MapHelper.Rehash();
			}
		}
		OutError.Reset();
		return true;
	}

	struct FCollectionSnapshotReferenceGuard final : FGCObject
	{
		TArray<FCollectionInstanceOverrideSnapshot>& Snapshots;

		explicit FCollectionSnapshotReferenceGuard(
			TArray<FCollectionInstanceOverrideSnapshot>& InSnapshots)
			: Snapshots(InSnapshots)
		{
		}

		virtual void AddReferencedObjects(FReferenceCollector& Collector) override
		{
			for (FCollectionInstanceOverrideSnapshot& Snapshot : Snapshots)
			{
				Snapshot.Value.AddStructReferencedObjects(Collector);
			}
		}

		virtual FString GetReferencerName() const override
		{
			return TEXT("FinareCollectionInstanceOverrideSnapshots");
		}
	};

	bool IsCollectionPin(const FEdGraphPinType& PinType)
	{
		return PinType.IsArray() || PinType.IsSet() || PinType.IsMap();
	}

	bool CaptureCollectionInstanceOverrides(
		UBlueprint& Blueprint,
		const FSyncPlan& Plan,
		TArray<FCollectionInstanceOverrideSnapshot>& OutSnapshots,
		FString& OutError)
	{
		OutSnapshots.Reset();
		OutError.Reset();
		UClass* OldClass = Blueprint.GeneratedClass;
		if (OldClass == nullptr)
		{
			return true;
		}
		UObject* OldCdo = OldClass->GetDefaultObject(false);
		if (OldCdo == nullptr)
		{
			return true;
		}

		TArray<UObject*> Instances;
		GetObjectsOfClass(OldClass, Instances, true, RF_MirroredGarbage);
		for (const FPlannedState& Planned : Plan.States)
		{
			const bool bNewCollection = IsCollectionPin(Planned.Declaration.PinType);
			const FName OldPropertyName = Planned.bExisting
				                              ? Planned.ExistingName
				                              : Planned.Declaration.Name;
			FProperty* Property = FindStateProperty(
				OldClass,
				Planned.Declaration.Id,
				OldPropertyName);
			if (Property == nullptr)
			{
				if (Planned.bExisting)
				{
					OutError = FString::Printf(
						TEXT("Collection state '%s' lost its existing property '%s' before instance preservation."),
						*Planned.Declaration.Name.ToString(),
						*OldPropertyName.ToString());
					return false;
				}
				continue;
			}
			const bool bOldCollection = IsCollectionProperty(Property);
			if (!bNewCollection && !bOldCollection)
			{
				continue;
			}
			const FBPVariableDescription* ExistingVariable = nullptr;
			for (const FBPVariableDescription& Variable : Blueprint.NewVariables)
			{
				if (Variable.VarName == OldPropertyName ||
					(Variable.HasMetaData(StateIdKey) &&
						Variable.GetMetaData(StateIdKey) == Planned.Declaration.SemanticId))
				{
					ExistingVariable = &Variable;
					break;
				}
			}
			const bool bExistingPinCompatible = ExistingVariable != nullptr &&
				ExistingVariable->VarType == Planned.Declaration.PinType;
			for (UObject* Instance : Instances)
			{
				if (Instance == nullptr || Instance == OldCdo ||
					Instance->HasAnyFlags(RF_BeginDestroyed | RF_FinishDestroyed | RF_MirroredGarbage) ||
					!Property->HasAnyPropertyFlags(CPF_Edit))
				{
					continue;
				}
				UClass* InstanceClass = Instance->GetClass();
				if (InstanceClass == nullptr || !InstanceClass->IsChildOf(OldClass) ||
					InstanceClass->HasAnyClassFlags(CLASS_NewerVersionExists | CLASS_Deprecated))
				{
					continue;
				}
				// GetObjectsOfClass(includeDerived=true) also sees skeleton,
				// reinstanced, and other transient generated classes.  Only a
				// class whose Blueprint still points back to this exact generated
				// class represents a live persisted CDO/instance that can carry
				// user defaults.
				UBlueprint* GeneratedBy = Cast<UBlueprint>(InstanceClass->ClassGeneratedBy);
				if (GeneratedBy == nullptr || GeneratedBy->GeneratedClass != InstanceClass)
				{
					continue;
				}
				const bool bDerivedClassDefaultObject =
					Instance->HasAnyFlags(RF_ClassDefaultObject) && InstanceClass != OldClass;
				if (!bDerivedClassDefaultObject && Instance->IsTemplate())
				{
					continue;
				}

				UObject* OldArchetype = Instance->GetArchetype();
				FProperty* ArchetypeProperty = nullptr;
				if (OldArchetype != nullptr && OldArchetype != Instance &&
					OldArchetype->GetClass() != nullptr)
				{
					ArchetypeProperty = FindStateProperty(
						OldArchetype->GetClass(),
						Planned.Declaration.Id,
						OldPropertyName);
				}
				const bool bFollowsArchetype = ArchetypeProperty != nullptr &&
					AreTypedPropertyValuesIdentical(
						Property,
						Instance,
						ArchetypeProperty,
						OldArchetype);
				if (bFollowsArchetype && !bNewCollection)
				{
					continue;
				}
				if (!bFollowsArchetype && (!bNewCollection || !bExistingPinCompatible))
				{
					OutError = FString::Printf(
						TEXT(
							"Collection state '%s' has a whole-value instance override on '%s' but its existing Blueprint pin type is incompatible with the new declaration."),
						*Planned.Declaration.Name.ToString(),
						*Instance->GetPathName());
					return false;
				}
				FCollectionInstanceOverrideSnapshot& Snapshot = OutSnapshots.AddDefaulted_GetRef();
				Snapshot.OldObject = Instance;
				Snapshot.OldObjectRoot = TStrongObjectPtr<UObject>(Instance);
				Snapshot.OldArchetypeRoot = TStrongObjectPtr<UObject>(OldArchetype);
				Snapshot.ObjectIdentity.Capture(Instance);
				Snapshot.bFollowsArchetype = bFollowsArchetype;
				Snapshot.OldPropertyName = OldPropertyName;
				Snapshot.NewPropertyName = Planned.Declaration.Name;
				Snapshot.StateId = Planned.Declaration.Id;
				if (bFollowsArchetype)
				{
					continue;
				}
				if (Snapshot.Value.AddProperty(CollectionSnapshotValueName, Property) !=
					EPropertyBagAlterationResult::Success)
				{
					OutError = FString::Printf(
						TEXT("Collection state '%s' could not own its existing property value."),
						*Planned.Declaration.Name.ToString());
					return false;
				}
				const FPropertyBagPropertyDesc* SnapshotDesc =
					Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
				if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr)
				{
					OutError = FString::Printf(
						TEXT("Collection state '%s' produced an incomplete override snapshot."),
						*Planned.Declaration.Name.ToString());
					return false;
				}
				CopyTypedPropertyValue(
					SnapshotDesc->CachedProperty,
					Snapshot.Value.GetMutableValue().GetMemory(),
					Property,
					Instance);
			}
		}
		return true;
	}

	bool RestoreCollectionInstanceOverrides(
		TArray<FCollectionInstanceOverrideSnapshot>& Snapshots,
		const FCollectionReplacementMap& ReplacedObjects,
		const bool bRollback,
		FString& OutError)
	{
		struct FRestoreTarget
		{
			FCollectionInstanceOverrideSnapshot* Snapshot = nullptr;
			UObject* Object = nullptr;
			FProperty* Property = nullptr;
			const FProperty* SnapshotProperty = nullptr;
			UObject* Archetype = nullptr;
			const FProperty* ArchetypeProperty = nullptr;
			bool bFollowsArchetype = false;
		};

		TArray<FRestoreTarget> Targets;
		const auto DescribeResolvedObject = [](UObject* Object)
		{
			if (Object == nullptr)
			{
				return FString(TEXT("<null>"));
			}
			const UClass* ObjectClass = Object->GetClass();
			return ObjectClass != nullptr
				       ? FString::Printf(
					       TEXT("%s [%s]"),
					       *Object->GetPathName(),
					       *ObjectClass->GetPathName())
				       : FString::Printf(TEXT("%s [<null-class>]"), *Object->GetPathName());
		};
		for (FCollectionInstanceOverrideSnapshot& Snapshot : Snapshots)
		{
			if (Snapshot.OldObject == nullptr ||
				Snapshot.ObjectIdentity.Key == FObjectKey())
			{
				OutError = TEXT(
					"Collection state override lost its captured owner identity before Blueprint reinstancing.");
				return false;
			}
			if (!Snapshot.bFollowsArchetype && !Snapshot.Value.IsValid())
			{
				OutError = FString::Printf(
					TEXT("Collection state override '%s' lost its typed value before Blueprint reinstancing."),
					*Snapshot.OldPropertyName.ToString());
				return false;
			}
			bool bFoundReplacement = false;
			bool bExplicitNull = false;
			UObject* NewObject = ResolveSnapshotObject(
				Snapshot.ObjectIdentity,
				Snapshot.OldObjectRoot.Get(),
				ReplacedObjects,
				bFoundReplacement,
				&bExplicitNull);
			bool bTerminalFoundReplacement = false;
			bool bTerminalExplicitNull = false;
			UObject* TerminalObject = ResolveReplacedObject(
				Snapshot.ObjectIdentity.Key,
				ReplacedObjects,
				bTerminalFoundReplacement,
				&bTerminalExplicitNull);
			if (!bTerminalFoundReplacement && NewObject != nullptr)
			{
				TerminalObject = NewObject;
			}
			UBlueprint* OwnerBlueprint = Snapshot.ObjectIdentity.OwnerBlueprint.Get();
			UClass* CurrentGeneratedClass = OwnerBlueprint != nullptr
				                                ? OwnerBlueprint->GeneratedClass.Get()
				                                : nullptr;
			const FString OwnerBlueprintPath = OwnerBlueprint != nullptr
				                                   ? OwnerBlueprint->GetPathName()
				                                   : TEXT("<none>");
			const FString CurrentGeneratedClassDescription =
				DescribeResolvedObject(CurrentGeneratedClass);
			const FString TerminalDescription = DescribeResolvedObject(TerminalObject);
			const FReplacementChainDiagnostics ChainDiagnostics = InspectReplacementChain(
				Snapshot.ObjectIdentity.Key,
				ReplacedObjects);
			const TCHAR* ChainState = ChainDiagnostics.bSelfEdge
				                          ? TEXT("self-edge")
				                          : ChainDiagnostics.bCycle
				                          ? TEXT("cycle")
				                          : ChainDiagnostics.bFoundReplacement
				                          ? TEXT("chain")
				                          : TEXT("none");
			if (bExplicitNull)
			{
				OutError = FString::Printf(
					TEXT(
						"Collection state override '%s' lost captured owner '%s' because the engine published an explicit null replacement; terminal=%s; bFoundReplacement=%s; replacementChain=%s; ownerBlueprint='%s'; currentGeneratedClass=%s."),
					*Snapshot.OldPropertyName.ToString(),
					Snapshot.ObjectIdentity.CapturedObjectPath.IsEmpty()
						? TEXT("<unknown>")
						: *Snapshot.ObjectIdentity.CapturedObjectPath,
					*TerminalDescription,
					bFoundReplacement ? TEXT("true") : TEXT("false"),
					ChainState,
					*OwnerBlueprintPath,
					*CurrentGeneratedClassDescription);
				return false;
			}
			if (NewObject == nullptr || NewObject->HasAnyFlags(RF_MirroredGarbage) ||
				NewObject->GetClass() == nullptr)
			{
				OutError = FString::Printf(
					TEXT(
						"Collection state override '%s' lost captured owner '%s' or failed replacement identity during Blueprint reinstancing; terminal=%s; bFoundReplacement=%s; replacementChain=%s; ownerBlueprint='%s'; currentGeneratedClass=%s."),
					*Snapshot.OldPropertyName.ToString(),
					Snapshot.ObjectIdentity.CapturedObjectPath.IsEmpty()
						? TEXT("<unknown>")
						: *Snapshot.ObjectIdentity.CapturedObjectPath,
					*TerminalDescription,
					bFoundReplacement ? TEXT("true") : TEXT("false"),
					ChainState,
					*OwnerBlueprintPath,
					*CurrentGeneratedClassDescription);
				return false;
			}
			const FName TargetName = bRollback
				                         ? Snapshot.OldPropertyName
				                         : Snapshot.NewPropertyName;
			FProperty* NewProperty = FindStateProperty(
				NewObject->GetClass(),
				Snapshot.StateId,
				TargetName);
			if (NewProperty == nullptr)
			{
				OutError = FString::Printf(
					TEXT("Collection state override '%s' could not be restored on '%s' after Blueprint reinstancing."),
					*TargetName.ToString(),
					*NewObject->GetPathName());
				return false;
			}
			if (Snapshot.bFollowsArchetype)
			{
				UObject* NewArchetype = NewObject->GetArchetype();
				FProperty* NewArchetypeProperty = NewArchetype != nullptr
					                                  ? FindStateProperty(
						                                  NewArchetype->GetClass(), Snapshot.StateId, TargetName)
					                                  : nullptr;
				if (NewArchetype == nullptr || NewArchetype == NewObject ||
					NewArchetype->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed) ||
					NewArchetypeProperty == nullptr ||
					!NewProperty->SameType(NewArchetypeProperty) ||
					NewProperty->GetSize() != NewArchetypeProperty->GetSize())
				{
					OutError = FString::Printf(
						TEXT(
							"Collection state follower '%s' could not resolve its final archetype on '%s' after Blueprint reinstancing."),
						*TargetName.ToString(),
						*NewObject->GetPathName());
					return false;
				}
				Targets.Add({
					&Snapshot,
					NewObject,
					NewProperty,
					nullptr,
					NewArchetype,
					NewArchetypeProperty,
					true
				});
				continue;
			}
			const FPropertyBagPropertyDesc* SnapshotDesc =
				Snapshot.Value.FindPropertyDescByName(CollectionSnapshotValueName);
			if (SnapshotDesc == nullptr || SnapshotDesc->CachedProperty == nullptr ||
				!NewProperty->SameType(SnapshotDesc->CachedProperty) ||
				NewProperty->GetSize() != SnapshotDesc->CachedProperty->GetSize())
			{
				OutError = FString::Printf(
					TEXT("Collection state override '%s' could not be restored on '%s' after Blueprint reinstancing."),
					*TargetName.ToString(),
					*NewObject->GetPathName());
				return false;
			}
			Targets.Add({&Snapshot, NewObject, NewProperty, SnapshotDesc->CachedProperty, nullptr, nullptr, false});
		}

		auto GetArchetypeDepth = [](UObject* Object)
		{
			int32 Depth = 0;
			TSet<UObject*> Visited;
			for (UObject* Current = Object != nullptr ? Object->GetArchetype() : nullptr;
			     Current != nullptr && !Visited.Contains(Current) && Depth < 256;
			     Current = Current->GetArchetype())
			{
				Visited.Add(Current);
				++Depth;
			}
			return Depth;
		};
		const auto IsArchetypeOf = [](UObject* Candidate, UObject* Object)
		{
			TSet<UObject*> Visited;
			for (UObject* Current = Object != nullptr ? Object->GetArchetype() : nullptr;
			     Current != nullptr && !Visited.Contains(Current);
			     Current = Current->GetArchetype())
			{
				if (Current == Candidate)
				{
					return true;
				}
				Visited.Add(Current);
			}
			return false;
		};
		Targets.Sort([&GetArchetypeDepth, &IsArchetypeOf](const FRestoreTarget& Left, const FRestoreTarget& Right)
		{
			if (IsArchetypeOf(Left.Object, Right.Object))
			{
				return true;
			}
			if (IsArchetypeOf(Right.Object, Left.Object))
			{
				return false;
			}
			return GetArchetypeDepth(Left.Object) < GetArchetypeDepth(Right.Object);
		});

		const TMap<UObject*, UObject*> FlattenedReplacedObjects =
			FlattenReplacedObjects(ReplacedObjects);
		TArray<TStrongObjectPtr<UPackage>> DerivedPackagesToDirty;
		const auto QueueDerivedPackageDirty = [&DerivedPackagesToDirty](UObject* Object)
		{
			if (Object == nullptr || !Object->HasAnyFlags(RF_ClassDefaultObject) ||
				Object->GetClass() == nullptr || Object->GetClass()->ClassGeneratedBy == nullptr)
			{
				return;
			}
			UPackage* Package = Object->GetOutermost();
			if (Package == nullptr)
			{
				return;
			}
			for (const TStrongObjectPtr<UPackage>& Existing : DerivedPackagesToDirty)
			{
				if (Existing.Get() == Package)
				{
					return;
				}
			}
			DerivedPackagesToDirty.Emplace(Package);
		};
		for (const FRestoreTarget& Target : Targets)
		{
			if (Target.Object == nullptr ||
				Target.Object->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed))
			{
				OutError = FString::Printf(
					TEXT("Collection state override '%s' lost its resolved owner before restore."),
					*Target.Snapshot->OldPropertyName.ToString());
				return false;
			}
			FCollectionInstanceOverrideSnapshot& Snapshot = *Target.Snapshot;
			bool bValueChanged = false;
			if (Target.bFollowsArchetype)
			{
				if (Target.Archetype == nullptr ||
					Target.Archetype->HasAnyFlags(RF_MirroredGarbage | RF_BeginDestroyed | RF_FinishDestroyed))
				{
					OutError = FString::Printf(
						TEXT("Collection state follower '%s' lost its final archetype before restore."),
						*Snapshot.NewPropertyName.ToString());
					return false;
				}
				bValueChanged = !AreTypedPropertyValuesIdentical(
					Target.Property,
					Target.Object,
					Target.ArchetypeProperty,
					Target.Archetype);
				CopyTypedPropertyValue(
					Target.Property,
					Target.Object,
					Target.ArchetypeProperty,
					Target.Archetype);
			}
			else
			{
				void* SnapshotValue = Target.SnapshotProperty->ContainerPtrToValuePtr<void>(
					Snapshot.Value.GetMutableValue().GetMemory());

				// Object/Class leaves need the same old-to-new mapping that Blueprint
				// reinstancing used for the containing instance.  Soft references are
				// handled by the archive as well and remain unloaded.
				if (!ValidateSnapshotReplacementReferences(
					Target.SnapshotProperty,
					SnapshotValue,
					ReplacedObjects,
					FString::Printf(
						TEXT("collection state '%s' on '%s'"),
						*Snapshot.NewPropertyName.ToString(),
						*Target.Object->GetPathName()),
					OutError))
				{
					return false;
				}
				FArchiveReplaceObjectRef<UObject> ReplaceArchive(
					Target.Object,
					FlattenedReplacedObjects,
					EArchiveReplaceObjectFlags::DelayStart);
				FStructuredArchiveFromArchive StructuredArchive(ReplaceArchive);
				Target.SnapshotProperty->SerializeItem(StructuredArchive.GetSlot(), SnapshotValue);
				bValueChanged = !AreTypedPropertyValuesIdentical(
					Target.Property,
					Target.Object,
					Target.SnapshotProperty,
					Snapshot.Value.GetMutableValue().GetMemory());
				CopyTypedPropertyValue(
					Target.Property,
					Target.Object,
					Target.SnapshotProperty,
					Snapshot.Value.GetMutableValue().GetMemory());
			}

			if (const FSetProperty* SetProperty = CastField<FSetProperty>(Target.Property))
			{
				FScriptSetHelper SetHelper(
					SetProperty,
					SetProperty->ContainerPtrToValuePtr<void>(Target.Object));
				SetHelper.Rehash();
			}
			else if (const FMapProperty* MapProperty = CastField<FMapProperty>(Target.Property))
			{
				FScriptMapHelper MapHelper(
					MapProperty,
					MapProperty->ContainerPtrToValuePtr<void>(Target.Object));
				MapHelper.Rehash();
			}

			if (!bRollback && bValueChanged)
			{
				QueueDerivedPackageDirty(Target.Object);
			}
		}
		if (!bRollback)
		{
			for (const TStrongObjectPtr<UPackage>& Package : DerivedPackagesToDirty)
			{
				if (Package.IsValid())
				{
					Package->SetDirtyFlag(true);
				}
			}
		}
		return true;
	}

	void DestroyCollectionInstanceOverrides(
		TArray<FCollectionInstanceOverrideSnapshot>& Snapshots)
	{
		for (FCollectionInstanceOverrideSnapshot& Snapshot : Snapshots)
		{
			// The property descriptor and storage are owned by the independent
			// bag, so their destruction remains valid after Blueprint has freed
			// the old class FFields.
			Snapshot.Value.Reset();
			Snapshot.OldObject = nullptr;
			Snapshot.OldPropertyName = NAME_None;
			Snapshot.NewPropertyName = NAME_None;
			Snapshot.StateId.Invalidate();
			Snapshot.OldObjectRoot.Reset();
			Snapshot.OldArchetypeRoot.Reset();
		}
		Snapshots.Reset();
	}

	FString ResolveConfiguredPath(const FString& ConfiguredPath)
	{
		FString Result = ConfiguredPath;
		Result.TrimStartAndEndInline();
		Result.ReplaceInline(TEXT("{ProjectDir}"), *FPaths::ProjectDir(), ESearchCase::IgnoreCase);
		if (const TSharedPtr<IPlugin> Plugin =
			IPluginManager::Get().FindPlugin(TEXT("Finare")))
		{
			Result.ReplaceInline(
				TEXT("{PluginDir}"),
				*Plugin->GetBaseDir(),
				ESearchCase::IgnoreCase);
		}
		Result = FPaths::ConvertRelativePathToFull(Result);
		FPaths::NormalizeFilename(Result);
		return Result;
	}

	bool SaveBlueprint(UBlueprint& Blueprint, FString& OutError)
	{
		UPackage* Package = Blueprint.GetOutermost();
		if (Package == nullptr || Package == GetTransientPackage() ||
			Package->GetName().StartsWith(TEXT("/Engine/Transient")))
		{
			return true;
		}

		const FString PackageName = Package->GetName();
		const FString Filename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(Package, &Blueprint, *Filename, SaveArguments))
		{
			OutError = FString::Printf(
				TEXT("Failed to save generated Blueprint '%s' to '%s'."),
				*Blueprint.GetPathName(),
				*Filename);
			return false;
		}
		return true;
	}

	/**
	 * Builds the deployment candidate for an Actor parent change.  The
	 * deployment file is an external publication participant, so the caller
	 * journals and writes this candidate together with the Blueprint package
	 * and managed outputs.  Updating the parsed object preserves unrelated
	 * entries and fields supplied by the project.
	 */
	bool BuildDeploymentParentCandidate(
		const UFinareAsset& ScriptAsset,
		const FString& RequiredTargetClassPath,
		FString& OutPath,
		TArray<uint8>& OutContent,
		FString& OutError,
		const TArray<FFinareExternalPublicationFile>* ExistingCandidates = nullptr)
	{
		OutPath.Reset();
		OutContent.Reset();
		OutError.Reset();
		const FString OnboardingPath = FPaths::Combine(
			FPaths::ProjectConfigDir(),
			TEXT("Finare.onboarding.json"));
		FString OnboardingJson;
		if (!FFileHelper::LoadFileToString(OnboardingJson, *OnboardingPath))
		{
			if (!IFileManager::Get().FileExists(*OnboardingPath))
			{
				return true;
			}
			OutError = FString::Printf(TEXT("Cannot read onboarding configuration '%s'."), *OnboardingPath);
			return false;
		}
		TSharedPtr<FJsonObject> Onboarding;
		const TSharedRef<TJsonReader<>> OnboardingReader =
			TJsonReaderFactory<>::Create(OnboardingJson);
		const TSharedPtr<FJsonObject>* Deployment = nullptr;
		FString Manifest;
		if (!FJsonSerializer::Deserialize(OnboardingReader, Onboarding) || !Onboarding.IsValid())
		{
			OutError = FString::Printf(TEXT("Invalid onboarding JSON '%s'."), *OnboardingPath);
			return false;
		}
		if (!Onboarding->TryGetObjectField(TEXT("deployment"), Deployment) ||
			Deployment == nullptr ||
			!(*Deployment)->TryGetStringField(TEXT("manifest"), Manifest) ||
			Manifest.IsEmpty())
		{
			return true;
		}
		OutPath = FPaths::ConvertRelativePathToFull(FPaths::GetPath(OnboardingPath), Manifest);
		FPaths::NormalizeFilename(OutPath);

		FString DeploymentJson;
		bool bLoadedCandidate = false;
		if (ExistingCandidates != nullptr)
		{
			for (const FFinareExternalPublicationFile& ExistingCandidate : *ExistingCandidates)
			{
				FString ExistingPath = FPaths::ConvertRelativePathToFull(ExistingCandidate.Path);
				FPaths::NormalizeFilename(ExistingPath);
				if (!ExistingPath.Equals(OutPath, ESearchCase::IgnoreCase))
				{
					continue;
				}
				if (bLoadedCandidate)
				{
					OutError = FString::Printf(TEXT("Duplicate deployment publication path '%s'."), *OutPath);
					return false;
				}
				if (ExistingCandidate.Content.IsEmpty())
				{
					OutError = FString::Printf(
						TEXT("The prepared deployment candidate '%s' is not valid UTF-8 JSON."),
						*OutPath);
					return false;
				}
				FFileHelper::BufferToString(
					DeploymentJson,
					ExistingCandidate.Content.GetData(),
					ExistingCandidate.Content.Num());
				if (DeploymentJson.IsEmpty())
				{
					OutError = FString::Printf(
						TEXT("The prepared deployment candidate '%s' is not valid UTF-8 JSON."),
						*OutPath);
					return false;
				}
				bLoadedCandidate = true;
			}
		}
		if (!bLoadedCandidate && !FFileHelper::LoadFileToString(DeploymentJson, *OutPath))
		{
			if (!IFileManager::Get().FileExists(*OutPath))
			{
				OutPath.Reset();
				return true;
			}
			OutError = FString::Printf(
				TEXT("Cannot read deployment manifest '%s' while synchronizing the Actor parent."),
				*OutPath);
			return false;
		}
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(DeploymentJson);
		const TArray<TSharedPtr<FJsonValue>>* Scripts = nullptr;
		if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
			!Root->TryGetArrayField(TEXT("scripts"), Scripts) || Scripts == nullptr)
		{
			OutError = FString::Printf(
				TEXT("Deployment manifest '%s' has no valid scripts array."),
				*OutPath);
			return false;
		}

		const FString ExpectedAssetPath = ScriptAsset.GetOutermost() != nullptr
			? ScriptAsset.GetOutermost()->GetName()
			: FString();
		const FString ExpectedTypeName = ScriptAsset.ScriptTypeName;
		const FString ExpectedDeclarationAssetPath = ScriptAsset.DeclarationAssetPath;
		const FString ExpectedScriptClassId = TEXT("us_") +
			ScriptAsset.ScriptClassId.ToString(EGuidFormats::Digits).ToLower();
		const FString ExpectedGeneratedClassPath = ScriptAsset.GeneratedClassPath.ToString();
		TSharedPtr<FJsonObject> Match;
		bool bIdentityMismatch = false;
		bool bPackageEntryFound = false;
		for (const TSharedPtr<FJsonValue>& Value : *Scripts)
		{
			const TSharedPtr<FJsonObject> Entry = Value.IsValid() ? Value->AsObject() : nullptr;
			FString AssetPath;
			FString TypeName;
			FString DeclarationAssetPath;
			if (Entry.IsValid() && Entry->TryGetStringField(TEXT("assetPath"), AssetPath) &&
				AssetPath == ExpectedAssetPath)
			{
				if (bPackageEntryFound)
				{
					OutError = FString::Printf(TEXT("Deployment manifest '%s' duplicates package '%s'."),
						*OutPath, *ExpectedAssetPath);
					return false;
				}
				bPackageEntryFound = true;
				if (!Entry->TryGetStringField(TEXT("typeName"), TypeName) ||
					TypeName != ExpectedTypeName ||
					!Entry->TryGetStringField(TEXT("declarationAssetPath"), DeclarationAssetPath) ||
					DeclarationAssetPath != ExpectedDeclarationAssetPath)
				{
					bIdentityMismatch = true;
					continue;
				}
				FString ExistingClassId;
				FString ExistingGeneratedClassPath;
				const bool bHasClassId = Entry->TryGetStringField(TEXT("scriptClassId"), ExistingClassId);
				const bool bHasGeneratedClassPath = Entry->TryGetStringField(
					TEXT("generatedClassPath"), ExistingGeneratedClassPath);
				if ((bHasClassId && ExistingClassId != ExpectedScriptClassId) ||
					(bHasGeneratedClassPath && ExistingGeneratedClassPath != ExpectedGeneratedClassPath))
				{
					bIdentityMismatch = true;
					continue;
				}
				if (Match.IsValid())
				{
					OutError = FString::Printf(
						TEXT("Deployment manifest '%s' contains duplicate entries for '%s' (%s)."),
						*OutPath,
						*ExpectedAssetPath,
						*ExpectedTypeName);
					return false;
				}
				Match = Entry;
			}
		}
		if (!Match.IsValid())
		{
			if (!bPackageEntryFound)
			{
				OutPath.Reset();
				return true;
			}
			if (bIdentityMismatch)
			{
				OutError = FString::Printf(
					TEXT("Deployment manifest '%s' has no stable identity match for Script Asset '%s' (%s)."),
					*OutPath,
					*ExpectedAssetPath,
					*ExpectedTypeName);
			}
			else
			{
				OutError = FString::Printf(
					TEXT("Deployment manifest '%s' has no entry for Script Asset '%s' (%s)."),
					*OutPath,
					*ExpectedAssetPath,
					*ExpectedTypeName);
			}
			return false;
		}
		FString ExistingRequiredTargetClass;
		if (Match->TryGetStringField(TEXT("requiredTargetClass"), ExistingRequiredTargetClass) &&
			ExistingRequiredTargetClass == RequiredTargetClassPath)
		{
			OutPath.Reset();
			return true;
		}
		Match->SetStringField(TEXT("requiredTargetClass"), RequiredTargetClassPath);

		FString CandidateJson;
		const TSharedRef<TJsonWriter<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TPrettyJsonPrintPolicy<TCHAR>>::Create(&CandidateJson);
		if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
		{
			OutError = FString::Printf(
				TEXT("Deployment manifest '%s' could not be serialized after the Actor parent update."),
				*OutPath);
			return false;
		}
		CandidateJson.AppendChar(TEXT('\n'));
		FTCHARToUTF8 Utf8(*CandidateJson);
		OutContent.Append(
			reinterpret_cast<const uint8*>(Utf8.Get()),
			Utf8.Length());
		return true;
	}

	bool SaveScriptAsset(UFinareAsset& ScriptAsset, FString& OutError)
	{
		UPackage* Package = ScriptAsset.GetOutermost();
		if (Package == nullptr || Package == GetTransientPackage() ||
			Package->GetName().StartsWith(TEXT("/Engine/Transient")))
		{
			return true;
		}

		const FString PackageName = Package->GetName();
		const FString Filename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Filename), true);
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_NoError;
		if (!UPackage::SavePackage(Package, &ScriptAsset, *Filename, SaveArguments))
		{
			OutError = FString::Printf(
				TEXT("Failed to save Finare Script Asset '%s' to '%s'."),
				*ScriptAsset.GetPathName(),
				*Filename);
			return false;
		}
		return true;
	}

	bool ResolveExternalPublicationPath(
		const FString& InputPath,
		FString& OutPath,
		FString& OutError)
	{
		if (InputPath.IsEmpty() || FPaths::IsRelative(InputPath))
		{
			OutError = FString::Printf(
				TEXT("The native publication output path '%s' must be a non-empty absolute path."),
				*InputPath);
			return false;
		}

		OutPath = FPaths::ConvertRelativePathToFull(InputPath);
		FPaths::NormalizeFilename(OutPath);
		if (OutPath.IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("The native publication output path '%s' is invalid."),
				*InputPath);
			return false;
		}
		return true;
	}

	bool WriteExternalPublicationFile(
		const FFinareExternalPublicationFile& File,
		FString& OutError)
	{
		FString AbsolutePath;
		if (!ResolveExternalPublicationPath(File.Path, AbsolutePath, OutError))
		{
			return false;
		}
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		if (PlatformFile.FileExists(*AbsolutePath) && PlatformFile.IsReadOnly(*AbsolutePath))
		{
			OutError = FString::Printf(
				TEXT("The native publication output '%s' is read-only and cannot be overwritten."),
				*AbsolutePath);
			return false;
		}

		const FString TemporaryPath = FString::Printf(
			TEXT("%s.finare-publication.%s.tmp"),
			*AbsolutePath,
			*FGuid::NewGuid().ToString(EGuidFormats::Digits));
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(AbsolutePath), true);
		if (!FFileHelper::SaveArrayToFile(File.Content, *TemporaryPath))
		{
			IFileManager::Get().Delete(*TemporaryPath, false, true);
			OutError = FString::Printf(
				TEXT("Cannot write prepared native publication '%s'."),
				*AbsolutePath);
			return false;
		}
		if (!IFileManager::Get().Move(*AbsolutePath, *TemporaryPath, true, false, false, true))
		{
			IFileManager::Get().Delete(*TemporaryPath, false, true);
			OutError = FString::Printf(
				TEXT("Cannot publish prepared native output '%s'."),
				*AbsolutePath);
			return false;
		}
		return true;
	}
} // namespace UE::Finare::BlueprintClass::Private

bool FFinareBlueprintClassMaterializer::Synchronize(
	UFinareAsset& ScriptAsset,
	bool bValidateOnly,
	FFinareBlueprintClassSyncResult& OutResult,
	FString& OutError)
{
	using namespace UE::Finare::BlueprintClass::Private;

	if (ScriptAsset.ScriptClassKind == EFinareClassKind::None ||
		!ScriptAsset.ScriptClassId.IsValid() ||
		!ScriptAsset.GeneratedClassPath.IsValid())
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' does not have complete Script Class identity."),
			*ScriptAsset.GetPathName());
		return false;
	}

	UClass* GeneratedClass = ScriptAsset.GeneratedClassPath.TryLoadClass<UObject>();
	UFinareBlueprint* Blueprint = GeneratedClass != nullptr
		                              ? Cast<UFinareBlueprint>(GeneratedClass->ClassGeneratedBy)
		                              : nullptr;
	if (Blueprint == nullptr || Blueprint->GeneratedClass != GeneratedClass ||
		Blueprint->ScriptAsset != &ScriptAsset)
	{
		OutError = FString::Printf(
			TEXT("Finare Asset '%s' does not resolve its exact generated Blueprint '%s' and sidecar."),
			*ScriptAsset.GetPathName(),
			*ScriptAsset.GeneratedClassPath.ToString());
		return false;
	}

	FString DeclarationPath;
	FString DeclarationAssetPath;
	ScriptAsset.ResolveDeclarationConfiguration(DeclarationPath, DeclarationAssetPath);
	if (DeclarationPath.IsEmpty() || DeclarationAssetPath.IsEmpty())
	{
		OutError = FString::Printf(
			TEXT("Finare Class '%s' has incomplete declaration configuration."),
			*ScriptAsset.GetPathName());
		return false;
	}
	const FString ResolvedPath = ResolveConfiguredPath(DeclarationPath);
	FString DeclarationJson;
	if (!FFileHelper::LoadFileToString(DeclarationJson, *ResolvedPath))
	{
		OutError = FString::Printf(
			TEXT("Cannot read Script Class declaration '%s'."),
			*ResolvedPath);
		return false;
	}
	return SynchronizeJson(
		ScriptAsset,
		*Blueprint,
		DeclarationJson,
		bValidateOnly,
		OutResult,
		OutError);
}

bool FFinareBlueprintClassMaterializer::SynchronizeJson(
	UFinareAsset& ScriptAsset,
	UBlueprint& Blueprint,
	const FString& DeclarationJson,
	bool bValidateOnly,
	FFinareBlueprintClassSyncResult& OutResult,
	FString& OutError)
{
	return SynchronizeJson(
		ScriptAsset,
		Blueprint,
		DeclarationJson,
		bValidateOnly,
		OutResult,
		OutError,
		FFinareBlueprintClassMaterializerOptions());
}

bool FFinareBlueprintClassMaterializer::SynchronizeJson(
	UFinareAsset& ScriptAsset,
	UBlueprint& Blueprint,
	const FString& DeclarationJson,
	bool bValidateOnly,
	FFinareBlueprintClassSyncResult& OutResult,
	FString& OutError,
	const FFinareBlueprintClassMaterializerOptions& InputOptions)
{
	using namespace UE::Finare::BlueprintClass::Private;
	FFinareBlueprintClassMaterializerOptions Options = InputOptions;

	OutResult = FFinareBlueprintClassSyncResult();
	OutError.Reset();
	const UFinareBlueprint* UnifiedBlueprint = Cast<UFinareBlueprint>(&Blueprint);
	if (UnifiedBlueprint == nullptr || UnifiedBlueprint->ScriptAsset != &ScriptAsset ||
		Blueprint.GeneratedClass == nullptr ||
		ScriptAsset.GeneratedClassPath.ToString() != Blueprint.GeneratedClass->GetPathName())
	{
		OutError = FString::Printf(
			TEXT("Blueprint '%s' is not the exact generated proxy for '%s'."),
			*Blueprint.GetPathName(),
			*ScriptAsset.GetPathName());
		return false;
	}

	TArray<FStateDeclaration> StateDeclarations;
	TArray<FFunctionDeclaration> FunctionDeclarations;
	TArray<FLifecycleDeclaration> LifecycleDeclarations;
	TArray<FInputActionDeclaration> InputDeclarations;
	TArray<FDelegateBindingDeclaration> DelegateDeclarations;
	TArray<FInterfaceFunctionDeclaration> InterfaceDeclarations;
	TOptional<FFinareClassBindingDeclaration> PlannedClassBinding;
	if (!ReadSelectedClassBinding(
		ScriptAsset,
		DeclarationJson,
		PlannedClassBinding,
		OutError))
	{
		return false;
	}
	if (Options.bOverrideParentClassPath)
	{
		if (!PlannedClassBinding.IsSet())
		{
			OutError = TEXT("Native history supplied a parent for a declaration without class binding.");
			return false;
		}
		if (PlannedClassBinding->BaseClassPath.IsSet() &&
			PlannedClassBinding->BaseClassPath.GetValue() != Options.ParentClassPathOverride)
		{
			OutError = TEXT("Native history parent conflicts with the explicit declaration parent.");
			return false;
		}
		PlannedClassBinding->BaseClassPath = Options.ParentClassPathOverride;
	}
	FParentChangePlan ParentPlan;
	if (!BuildParentChangePlan(
		ScriptAsset,
		Blueprint,
		PlannedClassBinding,
		ParentPlan,
		OutError))
	{
		return false;
	}
	TOptional<FFinareClassBindingDeclaration> ClassBinding;
	if (!ParseDeclaration(
		ScriptAsset,
		Blueprint,
		ParentPlan.bChanged ? ParentPlan.RequestedParent : nullptr,
		DeclarationJson,
		StateDeclarations,
		FunctionDeclarations,
		LifecycleDeclarations,
		InputDeclarations,
		DelegateDeclarations,
		InterfaceDeclarations,
		ClassBinding,
		OutError))
	{
		return false;
	}
	if (Options.bOverrideParentClassPath)
	{
		if (!ClassBinding.IsSet())
		{
			OutError = TEXT("Native history supplied a parent for a declaration without class binding.");
			return false;
		}
		if (ClassBinding->BaseClassPath.IsSet() &&
			ClassBinding->BaseClassPath.GetValue() != Options.ParentClassPathOverride)
		{
			OutError = TEXT("Native history parent conflicts with the explicit declaration parent.");
			return false;
		}
		ClassBinding->BaseClassPath = Options.ParentClassPathOverride;
	}
	// Rebuild from the fully validated selected asset so malformed or
	// mismatched multi-asset documents cannot smuggle a different parent
	// between the preflight ancestry check and member parsing.
	if (!BuildParentChangePlan(
		ScriptAsset,
		Blueprint,
		ClassBinding,
		ParentPlan,
		OutError))
	{
		return false;
	}
	// Reconcile the carrier requirement in the shared publication path, including
	// commandlet/C# synchronization. Workbench and direct callers share one journal.
	const UClass* TargetParentClass = ParentPlan.bChanged
		? ParentPlan.RequestedParent : Blueprint.ParentClass.Get();
	const bool bActorParentChange = ParentPlan.bChanged &&
		ScriptAsset.ScriptClassKind == EFinareClassKind::Actor;
	const bool bOrdinaryActor = ScriptAsset.ScriptClassKind == EFinareClassKind::Actor &&
		IsOrdinaryActorParent(TargetParentClass);
	const FString CurrentRequiredTargetPath =
		ScriptAsset.RequiredTargetClass.ToSoftObjectPath().ToString();
	const bool bRepairOrdinaryTarget = bOrdinaryActor &&
		CurrentRequiredTargetPath == AFinareActor::StaticClass()->GetPathName();
	UClass* RequiredTargetClassAfterParentChange = (bActorParentChange || bRepairOrdinaryTarget)
		? (bOrdinaryActor ? AActor::StaticClass() : AFinareActor::StaticClass())
		: nullptr;
	const bool bRequiredTargetClassChanged = RequiredTargetClassAfterParentChange != nullptr &&
		CurrentRequiredTargetPath != RequiredTargetClassAfterParentChange->GetPathName();
	if (bActorParentChange || bRepairOrdinaryTarget ||
		(bOrdinaryActor && CurrentRequiredTargetPath == AActor::StaticClass()->GetPathName()))
	{
		FString DeploymentPath;
		TArray<uint8> DeploymentContent;
		const FString TargetPath = (bOrdinaryActor ? AActor::StaticClass() : AFinareActor::StaticClass())->GetPathName();
		if (!BuildDeploymentParentCandidate(ScriptAsset, TargetPath, DeploymentPath,
			DeploymentContent, OutError, &Options.ExternalPublicationFiles))
		{
			return false;
		}
		if (!DeploymentPath.IsEmpty())
		{
			FFinareExternalPublicationFile* Existing = Options.ExternalPublicationFiles.FindByPredicate(
				[&DeploymentPath](const FFinareExternalPublicationFile& File)
				{
					FString Path = FPaths::ConvertRelativePathToFull(File.Path);
					FPaths::NormalizeFilename(Path);
					return Path.Equals(DeploymentPath, ESearchCase::IgnoreCase);
				});
			if (Existing == nullptr)
			{
				Existing = &Options.ExternalPublicationFiles.AddDefaulted_GetRef();
				Existing->Path = DeploymentPath;
			}
			Existing->Content = MoveTemp(DeploymentContent);
		}
	}
	const bool bHasExternalPublicationFiles = !Options.ExternalPublicationFiles.IsEmpty();
	const bool bTransactional = ParentPlan.bChanged || bHasExternalPublicationFiles || bRequiredTargetClassChanged;

	// A native publication may carry binding/source outputs even when the
	// physical UE parent remains unchanged.  It still replaces the managed
	// authoring generation, so the same exact owner/descendant active-instance
	// guard applies before any transaction or external output is committed.
	if (bTransactional && !ParentPlan.bChanged)
	{
		for (TObjectIterator<UFinareComponent> It; It; ++It)
		{
			UFinareComponent* Component = *It;
			if (!IsValid(Component) || !Component->bScriptRunning)
			{
				continue;
			}
			const UFinareBlueprintGeneratedClass* ActiveClass =
				Component->GetActiveManagedGeneratedClass();
			if (ActiveClass != nullptr &&
				(ActiveClass == Blueprint.GeneratedClass.Get() ||
					(Blueprint.GeneratedClass != nullptr &&
						ActiveClass->IsChildOf(Blueprint.GeneratedClass.Get()))))
			{
				OutError = FString::Printf(
					TEXT(
						"Cannot publish native changes for Blueprint '%s' while a managed instance is active; stop the affected World first."),
					*Blueprint.GetPathName());
				return false;
			}
		}
	}

	if (!Options.JournalTransactionId.IsEmpty())
	{
		FGuid ParsedJournalTransactionId;
		if (!FGuid::Parse(Options.JournalTransactionId, ParsedJournalTransactionId))
		{
			OutError = TEXT("The native publication journal transaction id is invalid.");
			return false;
		}
	}

#if WITH_EDITOR
	TOptional<FFinareAuthoringActivationScope> AuthoringActivationScope;
	if (bTransactional)
	{
		// Hold the authoring owner through preflight, reinstance, and local
		// transaction rollback so reinstanced BeginPlay cannot start a stale
		// managed generation before the operation has finished.  Same-parent
		// publication still recompiles/reinstances the exact owner and its
		// managed descendants when external outputs are part of the
		// transaction, so it needs the same lifetime barrier as a parent
		// change.
		AuthoringActivationScope.Emplace(&Blueprint);
	}
#endif

	FLocalBlueprintTransaction ParentTransaction(Blueprint);
	if (bTransactional && !ParentTransaction.Isolate(OutError))
	{
		return false;
	}

	UBlueprint* PlanningBlueprint = &Blueprint;
	TArray<FParentDefaultOverrideSnapshot> PlanningParentDefaultOverrides;
	FParentDefaultSnapshotReferenceGuard PlanningParentDefaultSnapshotReferenceGuard(
		PlanningParentDefaultOverrides);
	if (ParentPlan.bChanged)
	{
		PlanningBlueprint = Cast<UBlueprint>(StaticDuplicateObject(
			&Blueprint,
			GetTransientPackage(),
			MakeUniqueObjectName(
				GetTransientPackage(),
				Blueprint.GetClass(),
				TEXT("FinareParentChangePreflight"))));
		if (PlanningBlueprint == nullptr || ParentPlan.RequestedParent == nullptr ||
			!CaptureParentDefaultOverrides(
				*PlanningBlueprint,
				ParentPlan.CurrentParent,
				ParentPlan.RequestedParent,
				PlanningParentDefaultOverrides,
				OutError) ||
			!PrepareParentChange(*PlanningBlueprint, *ParentPlan.RequestedParent, OutError))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("Could not prepare the prospective Finare Blueprint parent.");
			}
			return false;
		}
	}

	if (!ValidateAddedStateNameConflicts(Blueprint, StateDeclarations, OutError))
	{
		return false;
	}

	// A live managed override may still be executing the persisted parent
	// route.  Validate the exact generated class and its active owners before
	// planning any graph, binding, or asset mutation.  ParseDeclaration has
	// already performed the native owner/signature validation, so this guard
	// compares the existing runtime contract without introducing a second
	// value-shape model.
	const UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
	bool bHasActiveManagedOverrideOwner = false;
	if (GeneratedClass != nullptr)
	{
		for (TObjectIterator<UFinareComponent> It; It; ++It)
		{
			UFinareComponent* Component = *It;
			if (!IsValid(Component) || Component->IsTemplate() ||
				Component->GetWorld() == nullptr ||
				!Component->OwnsActiveManagedOverride(GeneratedClass))
			{
				continue;
			}
			bHasActiveManagedOverrideOwner = true;
			break;
		}
	}
	if (bHasActiveManagedOverrideOwner)
	{
		FString CurrentContractJson;
		FString CurrentContractError;
		if (GeneratedClass == nullptr ||
			!GeneratedClass->BuildBlueprintOverrideActivationContract(
				CurrentContractJson,
				CurrentContractError))
		{
			OutError = FString::Printf(
				TEXT("Cannot synchronize live Blueprint '%s': its persisted override "
					"contract is invalid (%s). Stop the affected instance, synchronize "
					"the Script Class, and restart it to apply an override contract change."),
				*Blueprint.GetPathName(),
				*CurrentContractError);
			return false;
		}
		if (!ValidateLiveBlueprintOverrideContract(
			Blueprint,
			*GeneratedClass,
			FunctionDeclarations,
			OutError))
		{
			return false;
		}
	}

	const UClass* EffectiveParentClass = ParentPlan.bChanged
		                                     ? ParentPlan.RequestedParent
		                                     : Blueprint.ParentClass.Get();
	const UPackage* ScriptAssetPackage = ScriptAsset.GetOutermost();
	const bool bPersistentScriptAssetPackage = ScriptAssetPackage != nullptr &&
		ScriptAssetPackage != GetTransientPackage() &&
		!ScriptAssetPackage->GetName().StartsWith(TEXT("/Engine/Transient"));
	const FString ScriptAssetPackageFilename = bPersistentScriptAssetPackage
		? FPackageName::LongPackageNameToFilename(
			ScriptAssetPackage->GetName(),
			FPackageName::GetAssetPackageExtension())
		: FString();
	FSyncPlan Plan;
	if (!BuildPlan(Blueprint, EffectiveParentClass, StateDeclarations, Plan, OutResult, OutError) ||
		!BuildFunctionPlan(
			Blueprint,
			EffectiveParentClass,
			FunctionDeclarations,
			Plan,
			OutResult,
			OutError) ||
		!BuildLifecyclePlan(
			Blueprint,
			LifecycleDeclarations,
			Plan,
			OutResult,
			OutError) ||
		!BuildGameplayBindingPlan(
			Blueprint,
			InputDeclarations,
			DelegateDeclarations,
			InterfaceDeclarations,
			Plan,
			OutResult,
			OutError))
	{
		return OutError.IsEmpty();
	}

	OutResult.bChanged |= bTransactional;
	// A class with no exposed members still owns a managed receiver. Its
	// runtime asset association is part of materialization, independently of
	// whether any property, function, or explicit lifecycle binding changed.
	if (UnifiedBlueprint != nullptr)
	{
		const UFinareBlueprintGeneratedClass* RuntimeClass =
			Cast<UFinareBlueprintGeneratedClass>(Blueprint.GeneratedClass);
		OutResult.bChanged |= RuntimeClass != nullptr &&
			RuntimeClass->ManagedScriptAsset != UnifiedBlueprint->ScriptAsset;
		if (RuntimeClass && (RuntimeClass->bHasManagedConstruction ||
			(!ScriptAsset.ScriptTypeName.IsEmpty() &&
				(!ScriptAsset.CompiledChunkPath.IsEmpty() || !ScriptAsset.ScriptAssemblyPath.IsEmpty()))))
		{
			FString Program, Type, ConstructionError;
			if (!ScriptAsset.ResolveRuntimeConfiguration(true, Program, Type, ConstructionError))
			{ OutError = ConstructionError; return false; }
			const bool bProgramExists = IFileManager::Get().FileExists(*Program);
			const bool bProgramChanged = bProgramExists &&
				RuntimeClass->ManagedConstructionProgramHash != LexToString(FMD5Hash::HashFile(*Program));
			bool bCandidateHasHook = false;
			if (bProgramChanged && !FFinareCoreClrHost::Get().HasManagedConstruction(Program, Type, bCandidateHasHook, ConstructionError))
			{ OutError = ConstructionError; return false; }
			if ((RuntimeClass->bHasManagedConstruction || bCandidateHasHook) &&
				(bProgramChanged || !RuntimeClass->ValidateManagedConstruction(Program, ConstructionError)))
			{
				for (TObjectIterator<UFinareComponent> It; It; ++It)
					if (It->bScriptRunning && It->GetActiveManagedGeneratedClass() &&
						It->GetActiveManagedGeneratedClass()->IsChildOf(RuntimeClass))
					{ OutError = TEXT("Stop gameplay and rebuild the Script Class CDO before changing its native constructor program."); return false; }
				// A stopped program update must recreate its CDO, even when the
				// reflected declaration schema is unchanged.
				OutResult.bChanged = true;
			}
		}
	}

	if (!OutResult.bChanged)
	{
		return OutError.IsEmpty();
	}

	TArray<FParentDefaultOverrideSnapshot> ParentDefaultOverrides;
	if (ParentPlan.bChanged &&
		!CaptureParentDefaultOverrides(
			Blueprint,
			ParentPlan.CurrentParent,
			ParentPlan.RequestedParent,
			ParentDefaultOverrides,
			OutError))
	{
		return false;
	}
	TArray<FScriptComponentOwnerSnapshot> ScriptComponentOwners;
	if (bTransactional &&
		!CaptureScriptComponentOwners(Blueprint, ScriptComponentOwners, OutError))
	{
		return false;
	}
	FParentDefaultSnapshotReferenceGuard ParentDefaultSnapshotReferenceGuard(
		ParentDefaultOverrides);

	TArray<FManagedStateValueSnapshot> ManagedStateSnapshots;
	if (bTransactional &&
		!CaptureManagedStateValueSnapshots(
			Blueprint,
			Plan,
			ManagedStateSnapshots,
			OutError))
	{
		return false;
	}

	// Capture before validate-only returns and before the transactional
	// duplicate is applied.  Derived CDOs and live instances may own whole
	// collection values; followers are intentionally represented without a
	// value so restore can copy their final archetype instead of freezing the
	// old base value.
	TArray<FCollectionInstanceOverrideSnapshot> CollectionInstanceOverrides;
	FString CaptureError;
	if (!CaptureCollectionInstanceOverrides(
		Blueprint,
		Plan,
		CollectionInstanceOverrides,
		CaptureError))
	{
		OutError = CaptureError;
		DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
		return false;
	}
	FCollectionSnapshotReferenceGuard SnapshotReferenceGuard(CollectionInstanceOverrides);
	FManagedStateSnapshotReferenceGuard ManagedStateSnapshotReferenceGuard(ManagedStateSnapshots);
	FScriptComponentSnapshotReferenceGuard ScriptComponentSnapshotReferenceGuard(ScriptComponentOwners);

	FCollectionReplacementMap ReplacedObjects;
	const auto CaptureReplacementMap =
		[&ReplacedObjects](const FCoreUObjectDelegates::FReplacementObjectMap& ReplacementMap)
	{
		for (const TPair<UObject*, UObject*>& Pair : ReplacementMap)
		{
			if (Pair.Key == nullptr)
			{
				continue;
			}
			const FObjectKey OldKey(Pair.Key);
			const FObjectKey NewKey(Pair.Value);
			if (Pair.Value != nullptr && OldKey == NewKey)
			{
				// OnObjectsReinstanced may report an object that was
				// retained by the reinstancer as Old->Old.  This is a
				// stable terminal observation, not a replacement edge;
				// recording it would turn a valid chain into a cycle.
				continue;
			}
			// Raw pointers are converted immediately to serialised object
			// identities.  The event's addresses may be recycled before
			// the snapshot is restored, so they cannot be map keys.  The
			// late OnObjectsReinstanced event is required because the
			// reinstancer removes its temporary OnObjectsReplaced listener
			// before publishing the final actor/object map.
			FCollectionReplacementTarget Target;
			Target.Identity = FObjectKey(Pair.Value);
			Target.DestinationRoot.Reset(Pair.Value);
			Target.bExplicitNull = Pair.Value == nullptr;
			ReplacedObjects.Add(OldKey, MoveTemp(Target));
		}
	};
	const FDelegateHandle ReplacedObjectsHandle = FCoreUObjectDelegates::OnObjectsReplaced.AddLambda(
		CaptureReplacementMap);
	const FDelegateHandle ReinstancedObjectsHandle = FCoreUObjectDelegates::OnObjectsReinstanced.AddLambda(
		CaptureReplacementMap);
	ON_SCOPE_EXIT
	{
		FCoreUObjectDelegates::OnObjectsReinstanced.Remove(ReinstancedObjectsHandle);
	};
	if (bValidateOnly)
	{
		if (!ParentPlan.bChanged)
		{
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return OutError.IsEmpty();
		}
		UBlueprint* ValidationOnlyBlueprint = PlanningBlueprint;
		TArray<FManagedStateValueSnapshot> ValidationManagedStateSnapshots;
		TArray<FScriptComponentOwnerSnapshot> ValidationScriptComponentOwners;
		if (ValidationOnlyBlueprint == nullptr ||
			!CaptureManagedStateValueSnapshots(
				*ValidationOnlyBlueprint,
				Plan,
				ValidationManagedStateSnapshots,
				OutError) ||
			!CaptureScriptComponentOwners(
				*ValidationOnlyBlueprint,
				ValidationScriptComponentOwners,
				OutError))
		{
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return false;
		}
		FManagedStateSnapshotReferenceGuard ValidationManagedStateSnapshotReferenceGuard(
			ValidationManagedStateSnapshots);
		FScriptComponentSnapshotReferenceGuard ValidationScriptComponentSnapshotReferenceGuard(
			ValidationScriptComponentOwners);
		if (ValidationOnlyBlueprint != nullptr)
		{
			if (!ApplyPlan(
				*ValidationOnlyBlueprint,
				Plan,
				OutError,
				ParentPlan.bChanged))
			{
				if (OutError.IsEmpty())
				{
					OutError = TEXT("Could not compile the prospective parent Blueprint materialization.");
				}
				DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
				FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
				return false;
			}
			if (ParentPlan.bChanged &&
				!RestoreParentDefaultOverrides(
					PlanningParentDefaultOverrides,
					ReplacedObjects,
					OutError))
			{
				DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
				FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
				return false;
			}
			if (!RestoreManagedStateValueSnapshots(
				ValidationManagedStateSnapshots,
				ReplacedObjects,
				false,
				OutError))
			{
				DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
				FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
				return false;
			}
			for (FScriptComponentOwnerSnapshot& ComponentSnapshot :
			     ValidationScriptComponentOwners)
			{
				if (!RestoreScriptComponentOwner(
					ComponentSnapshot,
					ReplacedObjects,
					false,
					OutError))
				{
					DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
					FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
					return false;
				}
			}
		}
		DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
		FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
		return OutError.IsEmpty();
	}

	UBlueprint* ValidationBlueprint = ParentPlan.bChanged
		                                  ? PlanningBlueprint
		                                  : Cast<UBlueprint>(StaticDuplicateObject(
			                                  &Blueprint,
			                                  GetTransientPackage(),
			                                  MakeUniqueObjectName(
				                                  GetTransientPackage(),
				                                  Blueprint.GetClass(),
				                                  TEXT("FinareMaterializationPreflight"))));
	TArray<FManagedStateValueSnapshot> ValidationManagedStateSnapshots;
	TArray<FScriptComponentOwnerSnapshot> ValidationScriptComponentOwners;
	if (bTransactional && ValidationBlueprint != nullptr &&
		(!CaptureManagedStateValueSnapshots(
				*ValidationBlueprint,
				Plan,
				ValidationManagedStateSnapshots,
				OutError) ||
			!CaptureScriptComponentOwners(
				*ValidationBlueprint,
				ValidationScriptComponentOwners,
				OutError)))
	{
		ValidationBlueprint = nullptr;
	}
	FManagedStateSnapshotReferenceGuard ValidationManagedStateSnapshotReferenceGuard(
		ValidationManagedStateSnapshots);
	FScriptComponentSnapshotReferenceGuard ValidationScriptComponentSnapshotReferenceGuard(
		ValidationScriptComponentOwners);
	if (ValidationBlueprint == nullptr ||
		!ApplyPlan(
			*ValidationBlueprint,
			Plan,
			OutError,
			ParentPlan.bChanged))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("Could not create a transactional Blueprint materialization preflight.");
		}
		DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
		FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
		return false;
	}
	if (ParentPlan.bChanged &&
		!RestoreParentDefaultOverrides(
			PlanningParentDefaultOverrides,
			ReplacedObjects,
			OutError))
	{
		DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
		FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
		return false;
	}
	if (bTransactional)
	{
		if (!RestoreManagedStateValueSnapshots(
			ValidationManagedStateSnapshots,
			ReplacedObjects,
			false,
			OutError))
		{
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return false;
		}
		for (FScriptComponentOwnerSnapshot& ComponentSnapshot :
		     ValidationScriptComponentOwners)
		{
			if (!RestoreScriptComponentOwner(
				ComponentSnapshot,
				ReplacedObjects,
				false,
				OutError))
			{
				DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
				FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
				return false;
			}
		}
	}

	const UPackage* BlueprintPackage = Blueprint.GetOutermost();
	const bool bPersistentPackage = BlueprintPackage != nullptr &&
		BlueprintPackage != GetTransientPackage() &&
		!BlueprintPackage->GetName().StartsWith(TEXT("/Engine/Transient"));
	const FString PackageFilename =
		bPersistentPackage
			? FPackageName::LongPackageNameToFilename(
				BlueprintPackage->GetName(),
				FPackageName::GetAssetPackageExtension())
			: FString();
	bool bSaveStarted = false;
	bool bJournalActive = false;
	bool bJournalCloseAttempted = false;
	ClassOperationJournal::FReceipt JournalReceipt;
	auto CloseJournal = [
			&OutError,
			&JournalReceipt,
			&bJournalActive,
			&bJournalCloseAttempted]() -> bool
	{
		if (!bJournalActive)
		{
			return true;
		}
		bJournalCloseAttempted = true;
		FString JournalError;
		if (!ClassOperationJournal::Close(JournalReceipt, JournalError))
		{
			OutError += OutError.IsEmpty() ? FString() : TEXT(" ");
			OutError += TEXT("Cannot close parent change recovery journal: ") + JournalError;
			return false;
		}
		bJournalActive = false;
		return true;
	};
	auto RestorePackageSnapshots = [&]() -> bool
	{
		if (!bSaveStarted || !bJournalActive)
		{
			return true;
		}
		FString RestoreError;
		if (!ClassOperationJournal::RestoreCapturedFiles(JournalReceipt, RestoreError))
		{
			OutError += OutError.IsEmpty() ? FString() : TEXT(" ");
			OutError += TEXT("Failed to restore the Blueprint package bytes after parent transaction failure: ") +
				RestoreError;
			return false;
		}
		return true;
	};
	const TArray<FBPVariableDescription> OriginalVariables = Blueprint.NewVariables;
	const auto AppendError = [&OutError](const FString& Message)
	{
		if (!Message.IsEmpty())
		{
			if (!OutError.IsEmpty())
			{
				OutError += TEXT(" ");
			}
			OutError += Message;
		}
	};
	auto RollbackMemory = [&]() -> bool
	{
		if (!bTransactional)
		{
			Blueprint.NewVariables = OriginalVariables;
			FBlueprintEditorUtils::MarkBlueprintAsStructurallyModified(&Blueprint);
			FKismetEditorUtilities::CompileBlueprint(&Blueprint);
			if (Blueprint.Status == BS_Error || Blueprint.GeneratedClass == nullptr)
			{
				AppendError(
					TEXT("Blueprint materialization rollback could not recompile the original authoring state."));
				return false;
			}
			return true;
		}
		FString RollbackError;
		if (!ParentTransaction.Rollback(RollbackError))
		{
			AppendError(TEXT("Blueprint parent transaction rollback failed: ") + RollbackError);
			return false;
		}
		if (ParentPlan.bChanged &&
			(Blueprint.ParentClass != ParentPlan.CurrentParent ||
				Blueprint.GeneratedClass == nullptr ||
				Blueprint.GeneratedClass->GetSuperClass() != ParentPlan.CurrentParent))
		{
			AppendError(
				TEXT("Blueprint parent transaction rollback did not restore the original generated superclass."));
			return false;
		}
		if (ParentPlan.bChanged &&
			!RestoreParentDefaultOverrides(
				ParentDefaultOverrides,
				ReplacedObjects,
				RollbackError))
		{
			AppendError(
				TEXT("Blueprint parent transaction rollback could not restore inherited values: ") + RollbackError);
			return false;
		}
		if (bTransactional &&
			!RestoreManagedStateValueSnapshots(
				ManagedStateSnapshots,
				ReplacedObjects,
				true,
				RollbackError))
		{
			AppendError(
				TEXT("Blueprint parent transaction rollback could not restore managed values: ") + RollbackError);
			return false;
		}
		if (bTransactional)
		{
			for (FScriptComponentOwnerSnapshot& ComponentSnapshot : ScriptComponentOwners)
			{
				FString ComponentRestoreError;
				if (!RestoreScriptComponentOwner(
					ComponentSnapshot,
					ReplacedObjects,
					true,
					ComponentRestoreError))
				{
					AppendError(
						TEXT(
							"Blueprint parent transaction rollback could not restore the managed ScriptComponent owner: ")
						+
						ComponentRestoreError);
					return false;
				}
			}
		}
		return true;
	};
	auto RollbackAll = [&]() -> bool
	{
		bool bSucceeded = RollbackMemory();
		FString RollbackCollectionError;
		if (!RestoreCollectionInstanceOverrides(
			CollectionInstanceOverrides,
			ReplacedObjects,
			true,
			RollbackCollectionError))
		{
			AppendError(TEXT("Rollback also failed: ") + RollbackCollectionError);
			bSucceeded = false;
		}
		if (!RestorePackageSnapshots())
		{
			bSucceeded = false;
		}
		return bSucceeded;
	};
	auto FinishFailure = [&]()
	{
		const bool bRollbackSucceeded = RollbackAll();
		bool bRecoveryReceiptClosed = !bJournalActive;
		if (bRollbackSucceeded && bJournalActive && !bJournalCloseAttempted)
		{
			bRecoveryReceiptClosed = CloseJournal();
		}
		if (bRollbackSucceeded && bRecoveryReceiptClosed)
		{
			ParentTransaction.RestoreCapturedPackageDirtyFlags();
		}
		DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
		FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
	};

	const bool bNeedsJournal =
		((ParentPlan.bChanged || bRequiredTargetClassChanged) &&
			(!PackageFilename.IsEmpty() || !ScriptAssetPackageFilename.IsEmpty())) ||
		!Options.ExternalPublicationFiles.IsEmpty();
	if (bNeedsJournal)
	{
		ClassOperationJournal::FSnapshot PackageSnapshot;
		TArray<ClassOperationJournal::FSnapshot> JournalSnapshots;
		TSet<FString> JournalPaths;
		FString JournalError;
		bool bCaptured = true;
		if (!PackageFilename.IsEmpty())
		{
			bCaptured = ClassOperationJournal::CaptureSnapshot(
				PackageFilename,
				PackageSnapshot,
				JournalError);
			if (bCaptured)
			{
				JournalPaths.Add(FPaths::ConvertRelativePathToFull(PackageFilename));
				JournalSnapshots.Add(PackageSnapshot);
			}
		}
		if (bCaptured && bRequiredTargetClassChanged &&
			!ScriptAssetPackageFilename.IsEmpty() &&
			!JournalPaths.Contains(FPaths::ConvertRelativePathToFull(ScriptAssetPackageFilename)))
		{
			bCaptured = ClassOperationJournal::CaptureSnapshot(
				ScriptAssetPackageFilename,
				PackageSnapshot,
				JournalError);
			if (bCaptured)
			{
				JournalPaths.Add(FPaths::ConvertRelativePathToFull(ScriptAssetPackageFilename));
				JournalSnapshots.Add(PackageSnapshot);
			}
		}
		for (const FFinareExternalPublicationFile& ExternalFile :
		     Options.ExternalPublicationFiles)
		{
			FString ExternalPath;
			if (!ResolveExternalPublicationPath(ExternalFile.Path, ExternalPath, JournalError))
			{
				bCaptured = false;
				break;
			}
			if (JournalPaths.Contains(ExternalPath))
			{
				JournalError = FString::Printf(
					TEXT(
						"The native publication output path '%s' is duplicated or collides with the Blueprint package."),
					*ExternalPath);
				bCaptured = false;
				break;
			}
			IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
			if (PlatformFile.FileExists(*ExternalPath) && PlatformFile.IsReadOnly(*ExternalPath))
			{
				JournalError = FString::Printf(
					TEXT("The native publication output '%s' is read-only and cannot be overwritten."),
					*ExternalPath);
				bCaptured = false;
				break;
			}
			ClassOperationJournal::FSnapshot Snapshot;
			if (!ClassOperationJournal::CaptureSnapshot(
				ExternalPath,
				Snapshot,
				JournalError))
			{
				bCaptured = false;
				break;
			}
			JournalPaths.Add(ExternalPath);
			JournalSnapshots.Add(MoveTemp(Snapshot));
		}
		FGuid ParsedJournalTransactionId;
		const bool bHasJournalTransactionId = Options.JournalTransactionId.IsEmpty()
			                                      ? false
			                                      : FGuid::Parse(Options.JournalTransactionId,
			                                                     ParsedJournalTransactionId);
		if (!Options.JournalTransactionId.IsEmpty() && !bHasJournalTransactionId)
		{
			OutError = TEXT("The native publication journal transaction id is invalid.");
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return false;
		}
		const FString JournalTransactionId = bHasJournalTransactionId
			                                     ? Options.JournalTransactionId
			                                     : FGuid::NewGuid().ToString(EGuidFormats::Digits);
		if (!bCaptured ||
			!ClassOperationJournal::Begin(
				TEXT("parentChange"),
				JournalTransactionId,
				JournalSnapshots,
				JournalReceipt,
				JournalError))
		{
			OutError = FString::Printf(
				TEXT("Cannot begin the Blueprint parent change recovery journal: %s"),
				*JournalError);
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return false;
		}
		bJournalActive = true;
	}

	if (bTransactional)
	{
		if ((ParentPlan.bChanged && ParentPlan.RequestedParent == nullptr) ||
			!ParentTransaction.Begin(OutError, bRequiredTargetClassChanged ? &ScriptAsset : nullptr))
		{
			if (bJournalActive)
			{
				CloseJournal();
			}
			DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
			FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
			return false;
		}
		if (ParentPlan.bChanged &&
			!PrepareParentChange(Blueprint, *ParentPlan.RequestedParent, OutError))
		{
			FinishFailure();
			return false;
		}
		if (ParentPlan.bChanged &&
			Options.FailurePoint ==
			EFinareBlueprintClassMaterializerFailurePoint::AfterParentMutation)
		{
			OutError = TEXT("Injected parent mutation failure for Finare Blueprint transaction test.");
			FinishFailure();
			return false;
		}
	}

	const bool bApplied = ApplyPlan(
		Blueprint,
		Plan,
		OutError,
		ParentPlan.bChanged);
	if (!bApplied)
	{
		FinishFailure();
		return false;
	}
	if (ParentPlan.bChanged &&
		!RestoreParentDefaultOverrides(
			ParentDefaultOverrides,
			ReplacedObjects,
			OutError))
	{
		FinishFailure();
		return false;
	}
	if (bTransactional &&
		!RestoreManagedStateValueSnapshots(
			ManagedStateSnapshots,
			ReplacedObjects,
			false,
			OutError))
	{
		FinishFailure();
		return false;
	}
	if (bTransactional)
	{
		for (FScriptComponentOwnerSnapshot& ComponentSnapshot : ScriptComponentOwners)
		{
			if (!RestoreScriptComponentOwner(
				ComponentSnapshot,
				ReplacedObjects,
				false,
				OutError))
			{
				FinishFailure();
				return false;
			}
		}
	}
	FString RestoreError;
	if (!RestoreCollectionInstanceOverrides(
		CollectionInstanceOverrides,
		ReplacedObjects,
		false,
		RestoreError))
	{
		OutError = RestoreError;
		FinishFailure();
		return false;
	}
	if (bJournalActive)
	{
		FString JournalError;
		if (!ClassOperationJournal::Advance(
			JournalReceipt,
			ClassOperationJournal::EPhase::AssetsMutated,
			JournalError))
		{
			OutError = TEXT("Cannot advance the parent change recovery journal after Blueprint mutation: ") +
				JournalError;
			FinishFailure();
			return false;
		}
	}
	bSaveStarted = bTransactional;
	if (!SaveBlueprint(Blueprint, OutError))
	{
		FinishFailure();
		return false;
	}
	if (Options.FailurePoint ==
		EFinareBlueprintClassMaterializerFailurePoint::AfterPackageSave)
	{
		OutError = TEXT("Injected package publication failure for Finare Blueprint transaction test.");
		FinishFailure();
		return false;
	}
	if (bRequiredTargetClassChanged)
	{
		ScriptAsset.Modify();
		ScriptAsset.RequiredTargetClass =
			TSoftClassPtr<UObject>(RequiredTargetClassAfterParentChange);
		if (UPackage* Package = ScriptAsset.GetOutermost())
		{
			Package->SetDirtyFlag(true);
		}
		if (!SaveScriptAsset(ScriptAsset, OutError))
		{
			FinishFailure();
			return false;
		}
	}
	int32 ExternalPublicationWriteCount = 0;
	for (const FFinareExternalPublicationFile& ExternalFile :
	     Options.ExternalPublicationFiles)
	{
		if (!WriteExternalPublicationFile(ExternalFile, OutError))
		{
			FinishFailure();
			return false;
		}
		++ExternalPublicationWriteCount;
		if (Options.FailAfterExternalPublicationWrite != INDEX_NONE &&
			ExternalPublicationWriteCount == Options.FailAfterExternalPublicationWrite)
		{
			OutError = FString::Printf(
				TEXT("Injected external publication failure after %d external file writes."),
				ExternalPublicationWriteCount);
			FinishFailure();
			return false;
		}
	}
	if (bJournalActive)
	{
		FString JournalError;
		if (!ClassOperationJournal::Advance(
			JournalReceipt,
			ClassOperationJournal::EPhase::ExternalPublished,
			JournalError))
		{
			OutError = TEXT("Cannot finalize the parent change recovery journal: ") + JournalError;
			FinishFailure();
			return false;
		}
	}
	if (!ParentTransaction.Finalize(OutError))
	{
		FinishFailure();
		return false;
	}
	if (!CloseJournal())
	{
		FinishFailure();
		return false;
	}
	ParentTransaction.DiscardRollbackAbility();
	DestroyCollectionInstanceOverrides(CollectionInstanceOverrides);
	FCoreUObjectDelegates::OnObjectsReplaced.Remove(ReplacedObjectsHandle);
	return true;
}
