#include "Widgets/SFinareAuthoringSurface.h"

#include "AssetRegistry/AssetData.h"
#include "ContentBrowserModule.h"
#include "DesktopPlatformModule.h"
#include "Dom/JsonObject.h"
#include "EdGraph/EdGraph.h"
#include "Editor.h"
#include "Framework/Application/SlateApplication.h"
#include "Framework/Commands/GenericCommands.h"
#include "Framework/Commands/UICommandList.h"
#include "Graph/FinareGraphPresentation.h"
#include "Graph/FinareGraphLayoutStore.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformApplicationMisc.h"
#include "HttpModule.h"
#include "IDesktopPlatform.h"
#include "IContentBrowserSingleton.h"
#include "InputCoreTypes.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Misc/ConfigCacheIni.h"
#include "Misc/Crc.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Rendering/DrawElements.h"
#include "SourceCodeNavigation.h"
#include "Styling/AppStyle.h"
#include "FinareEditorModule.h"
#include "FinareAsset.h"
#include "FinareAuthoringWorkbench.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareEdGraph.h"
#include "FinareEditorDiagnostics.h"
#include "FinareWorldSubsystem.h"
#include "UObject/UObjectIterator.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Input/SMultiLineEditableTextBox.h"
#include "Widgets/Input/SSearchBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SExpandableArea.h"
#include "Widgets/SOverlay.h"
#include "Widgets/SWindow.h"
#include "Widgets/Layout/SSplitter.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SSeparator.h"
#include "Widgets/Layout/SWidgetSwitcher.h"
#include "Widgets/Views/STreeView.h"
#include "Widgets/Views/SListView.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/SFinareDebugPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FinareAuthoringSurface"

bool SFinareAuthoringSurface::HasProjectableInputDefault(
	const TSharedPtr<FJsonObject>& GeneratedNode,
	const TSharedPtr<FJsonObject>& ReflectedParameter,
	const FString& ParameterName)
{
	if (GeneratedNode.IsValid())
	{
		const TArray<TSharedPtr<FJsonValue>>* Pins = nullptr;
		if (!GeneratedNode->TryGetArrayField(TEXT("pins"), Pins) || Pins == nullptr)
		{
			return false;
		}

		for (const TSharedPtr<FJsonValue>& PinValue : *Pins)
		{
			const TSharedPtr<FJsonObject> Pin =
				PinValue.IsValid() ? PinValue->AsObject() : nullptr;
			FString PinName;
			FString PinDirection;
			FString GeneratedDefault;
			if (Pin.IsValid() &&
				Pin->TryGetStringField(TEXT("name"), PinName) &&
				PinName.Equals(ParameterName, ESearchCase::IgnoreCase) &&
				Pin->TryGetStringField(TEXT("direction"), PinDirection) &&
				PinDirection == TEXT("input") &&
				Pin->TryGetStringField(TEXT("defaultValue"), GeneratedDefault))
			{
				return true;
			}
		}
		return false;
	}

	FString ReflectedDefault;
	return ReflectedParameter.IsValid() &&
		ReflectedParameter->TryGetStringField(TEXT("defaultValue"), ReflectedDefault);
}

namespace UE::Finare::AuthoringSurface::Slate::Private
{
	const TCHAR* NodePaletteConfigSection = TEXT("Finare.AuthoringSurface.NodePalette");
	const TCHAR* CanvasConfigSection = TEXT("Finare.AuthoringSurface.Canvas");

	TSharedRef<STextBlock> MakeInspectorLabel(const FString& Text)
	{
		return SNew(STextBlock)
			.Text(FText::FromString(Text))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
			.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f));
	}

	TSharedRef<SWidget> MakeInspectorValue(const FString& Label, const FString& Value)
	{
		return SNew(SVerticalBox)
			+ SVerticalBox::Slot()
			.AutoHeight()
			[
				MakeInspectorLabel(Label)
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 2.0f, 0.0f, 10.0f)
			[
				SNew(STextBlock)
				.Text(FText::FromString(Value))
				.AutoWrapText(true)
			];
	}

	struct FStateAuthoringDialogResult
	{
		FString Name;
		FString TypeName;
		FString TypeKind;
		FString DefaultValue;
		FFinareAuthoringExposure Exposure;
	};

	bool ShowStateAuthoringDialog(
		const FFinareAuthoringState* Existing,
		FStateAuthoringDialogResult& OutResult)
	{
		TSharedPtr<SEditableTextBox> NameEditor;
		TSharedPtr<SEditableTextBox> TypeEditor;
		TSharedPtr<SEditableTextBox> KindEditor;
		TSharedPtr<SEditableTextBox> DefaultEditor;
		TSharedPtr<SEditableTextBox> CategoryEditor;
		TSharedPtr<SCheckBox> InstanceEditableCheck;
		TSharedPtr<SCheckBox> SaveGameCheck;
		TSharedPtr<SCheckBox> ReplicatedCheck;
		bool bAccepted = false;
		const TSharedRef<SWindow> Window = SNew(SWindow)
			.Title(Existing == nullptr
				       ? LOCTEXT("AddStateDialog", "Add Script State")
				       : LOCTEXT("EditStateDialog", "Edit Script State"))
			.ClientSize(FVector2D(560.0f, 480.0f))
			.SupportsMaximize(false)
			.SupportsMinimize(false);
		Window->SetContent(
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
			.Padding(14.0f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("NAME"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(NameEditor, SEditableTextBox)
					.Text(FText::FromString(Existing != nullptr ? Existing->Name : TEXT("NewState")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("C# TYPE  ·  bool / int / float / string / enum type"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(TypeEditor, SEditableTextBox)
					.Text(FText::FromString(
						Existing != nullptr ? Existing->TypeDisplayName : TEXT("int")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("TYPE KIND  ·  boolean / integer / floatingPoint / string / enum"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(KindEditor, SEditableTextBox)
					.Text(FText::FromString(
						Existing != nullptr ? Existing->TypeKind : TEXT("integer")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT(
						"DEFAULT  ·  strict JSON value; quote strings and use numeric strings for typed enums"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(DefaultEditor, SEditableTextBox)
					.Text(FText::FromString(
						Existing != nullptr ? Existing->DefaultValue : TEXT("0")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("CATEGORY"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 10.0f)
				[
					SAssignNew(CategoryEditor, SEditableTextBox)
					.Text(FText::FromString(
						Existing != nullptr ? Existing->Exposure.Category : TEXT("Gameplay")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SAssignNew(InstanceEditableCheck, SCheckBox)
						.IsChecked(Existing != nullptr && Existing->Exposure.bInstanceEditable
							           ? ECheckBoxState::Checked
							           : ECheckBoxState::Unchecked)
						[
							SNew(STextBlock).Text(LOCTEXT("StateInstanceEditable", "Instance Editable"))
						]
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(14.0f, 0.0f)
					[
						SAssignNew(SaveGameCheck, SCheckBox)
						.IsChecked(Existing != nullptr && Existing->Exposure.bSaveGame
							           ? ECheckBoxState::Checked
							           : ECheckBoxState::Unchecked)
						[
							SNew(STextBlock).Text(LOCTEXT("StateSaveGame", "Save Game"))
						]
					]
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SAssignNew(ReplicatedCheck, SCheckBox)
						.IsChecked(Existing != nullptr && Existing->Exposure.bReplicated
							           ? ECheckBoxState::Checked
							           : ECheckBoxState::Unchecked)
						[
							SNew(STextBlock).Text(LOCTEXT("StateReplicated", "Replicated"))
						]
					]
				]
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right).Padding(0.0f, 16.0f, 0.0f, 0.0f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SNew(SButton)
						.Text(LOCTEXT("CancelStateDialog", "Cancel"))
						.OnClicked_Lambda([Window]()
						{
							Window->RequestDestroyWindow();
							return FReply::Handled();
						})
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(8.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("PreviewStateDialog", "Preview"))
						.OnClicked_Lambda([Window, &bAccepted]()
						{
							bAccepted = true;
							Window->RequestDestroyWindow();
							return FReply::Handled();
						})
					]
				]
			]);
		FSlateApplication::Get().AddModalWindow(Window, FSlateApplication::Get().GetActiveTopLevelWindow());
		if (!bAccepted)
		{
			return false;
		}
		OutResult.Name = NameEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.TypeName = TypeEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.TypeKind = KindEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.DefaultValue = DefaultEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.Exposure.bInstanceEditable = InstanceEditableCheck->IsChecked();
		OutResult.Exposure.bSaveGame = SaveGameCheck->IsChecked();
		OutResult.Exposure.bReplicated = ReplicatedCheck->IsChecked();
		OutResult.Exposure.Category = CategoryEditor->GetText().ToString().TrimStartAndEnd();
		return true;
	}

	struct FFunctionAuthoringDialogResult
	{
		FString Name;
		FString ReturnType;
		FString Parameters;
		bool bStatic = false;
	};

	bool ShowFunctionAuthoringDialog(
		const FFinareAuthoringFunction* Existing,
		FFunctionAuthoringDialogResult& OutResult)
	{
		TSharedPtr<SEditableTextBox> NameEditor;
		TSharedPtr<SEditableTextBox> ReturnEditor;
		TSharedPtr<SEditableTextBox> ParametersEditor;
		TSharedPtr<SCheckBox> StaticCheck;
		FString ExistingParameters;
		if (Existing != nullptr)
		{
			ExistingParameters = FString::JoinBy(
				Existing->Parameters,
				TEXT(", "),
				[](const FFinareAuthoringParameter& Parameter)
				{
					return Parameter.Name + TEXT(":") + Parameter.TypeMetadataName;
				});
		}
		bool bAccepted = false;
		const TSharedRef<SWindow> Window = SNew(SWindow)
			.Title(Existing == nullptr
				       ? LOCTEXT("AddFunctionDialog", "Add Visual Function")
				       : LOCTEXT("EditFunctionDialog", "Edit Visual Function"))
			.ClientSize(FVector2D(560.0f, 330.0f))
			.SupportsMaximize(false)
			.SupportsMinimize(false);
		Window->SetContent(
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
			.Padding(14.0f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("NAME"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(NameEditor, SEditableTextBox)
					.Text(FText::FromString(Existing != nullptr ? Existing->Name : TEXT("NewFunction")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(
						TEXT("RETURN TYPE  ·  void / bool / integer / float / double / decimal / string"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
				[
					SAssignNew(ReturnEditor, SEditableTextBox)
					.Text(FText::FromString(
						Existing != nullptr ? Existing->ReturnTypeMetadataName : TEXT("void")))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					MakeInspectorLabel(TEXT("PARAMETERS  ·  name:type, name:type  ·  count stays fixed when editing"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 10.0f)
				[
					SAssignNew(ParametersEditor, SEditableTextBox)
					.Text(FText::FromString(ExistingParameters))
					.HintText(LOCTEXT("FunctionParametersHint", "amount:int, deltaSeconds:float"))
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(StaticCheck, SCheckBox)
					.IsChecked(Existing != nullptr && Existing->bStatic
						           ? ECheckBoxState::Checked
						           : ECheckBoxState::Unchecked)
					[
						SNew(STextBlock).Text(LOCTEXT("StaticVisualFunction", "Static function"))
					]
				]
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right).Padding(0.0f, 16.0f, 0.0f, 0.0f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SNew(SButton)
						.Text(LOCTEXT("CancelFunctionDialog", "Cancel"))
						.OnClicked_Lambda([Window]()
						{
							Window->RequestDestroyWindow();
							return FReply::Handled();
						})
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(8.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("PreviewFunctionDialog", "Preview"))
						.OnClicked_Lambda([Window, &bAccepted]()
						{
							bAccepted = true;
							Window->RequestDestroyWindow();
							return FReply::Handled();
						})
					]
				]
			]);
		FSlateApplication::Get().AddModalWindow(Window, FSlateApplication::Get().GetActiveTopLevelWindow());
		if (!bAccepted)
		{
			return false;
		}
		OutResult.Name = NameEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.ReturnType = ReturnEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.Parameters = ParametersEditor->GetText().ToString().TrimStartAndEnd();
		OutResult.bStatic = StaticCheck->IsChecked();
		return true;
	}

	FString SuggestSwitchCaseLabel(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringAction& SwitchStatement)
	{
		TSet<int32> UsedValues;
		const FFinareAuthoringRegion* SwitchRegion =
			Function.FindRegion(SwitchStatement.RegionId);
		if (SwitchRegion != nullptr && SwitchRegion->Kind == TEXT("switch"))
		{
			for (const FString& SectionId : SwitchRegion->ChildRegionIds)
			{
				const FFinareAuthoringRegion* Section = Function.FindRegion(SectionId);
				if (Section == nullptr)
				{
					continue;
				}
				for (const FString& ActionId : Section->DirectActionIds)
				{
					const FFinareAuthoringAction* Label = Function.FindAction(ActionId);
					int32 Value = 0;
					if (Label != nullptr && Label->GraphKind == TEXT("switchCase") &&
						LexTryParseString(
							Value,
							*Label->Properties.FindRef(TEXT("patternText"))))
					{
						UsedValues.Add(Value);
					}
				}
			}
		}
		int32 Candidate = 0;
		while (UsedValues.Contains(Candidate))
		{
			++Candidate;
		}
		return LexToString(Candidate);
	}

	bool IsStatementCopyCandidate(const FFinareAuthoringAction& Action)
	{
		return Action.GraphKind != TEXT("entry") &&
			Action.GraphKind != TEXT("unrealEventEntry") &&
			Action.GraphKind != TEXT("switchCase") &&
			Action.GraphKind != TEXT("return") &&
			Action.GraphKind != TEXT("break") &&
			Action.GraphKind != TEXT("continue");
	}

	FString SuggestCopiedLocalName(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringAction& Source)
	{
		if (Source.GraphKind != TEXT("variable"))
		{
			return FString();
		}

		FString BaseName = Source.Properties.FindRef(TEXT("name"));
		if (BaseName.IsEmpty())
		{
			BaseName = TEXT("value");
		}
		TSet<FString> UsedNames;
		for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
		{
			UsedNames.Add(Parameter.Name);
		}
		for (const FFinareAuthoringAction& Action : Function.Actions)
		{
			const FString Name = Action.Properties.FindRef(TEXT("name"));
			if (!Name.IsEmpty())
			{
				UsedNames.Add(Name);
			}
		}

		FString Candidate = BaseName + TEXT("Copy");
		int32 Suffix = 2;
		while (UsedNames.Contains(Candidate))
		{
			Candidate = FString::Printf(TEXT("%sCopy%d"), *BaseName, Suffix++);
		}
		return Candidate;
	}

	TMap<FString, FString> SuggestCopiedLocalNames(
		const FFinareAuthoringFunction& Function,
		const TArray<FString>& StatementActionIds)
	{
		TMap<FString, FString> Result;
		for (const FString& ActionId : StatementActionIds)
		{
			const FFinareAuthoringAction* Action = Function.FindAction(ActionId);
			if (Action != nullptr && Action->GraphKind == TEXT("variable"))
			{
				Result.Add(Action->Id, SuggestCopiedLocalName(Function, *Action));
			}
		}
		return Result;
	}

	FString SuggestPromotedLocalName(
		const FFinareAuthoringFunction& Function,
		const FFinareAuthoringPort& Port)
	{
		FString Role = Port.Name.IsEmpty() ? TEXT("Value") : Port.Name;
		for (int32 Index = Role.Len() - 1; Index >= 0; --Index)
		{
			if (!(FChar::IsAlnum(Role[Index]) || Role[Index] == TEXT('_')))
			{
				Role.RemoveAt(Index);
			}
		}
		if (Role.IsEmpty())
		{
			Role = TEXT("Value");
		}
		Role[0] = FChar::ToUpper(Role[0]);

		TSet<FString> UsedNames;
		for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
		{
			UsedNames.Add(Parameter.Name);
		}
		for (const FFinareAuthoringAction& Action : Function.Actions)
		{
			const FString Name = Action.Properties.FindRef(TEXT("name"));
			if (!Name.IsEmpty())
			{
				UsedNames.Add(Name);
			}
		}

		const FString BaseName = TEXT("promoted") + Role;
		FString Candidate = BaseName;
		int32 Suffix = 2;
		while (UsedNames.Contains(Candidate))
		{
			Candidate = FString::Printf(TEXT("%s%d"), *BaseName, Suffix++);
		}
		return Candidate;
	}

	FString BuildPreviewSideText(
		const FFinareAuthoringPreviewResult& Preview,
		bool bBefore)
	{
		FString Result;
		for (const FFinareAuthoringPreviewFile& File : Preview.Files)
		{
			if (!Result.IsEmpty())
			{
				Result += TEXT("\n\n");
			}
			Result += FString::Printf(TEXT("===== %s =====\n"), *File.Path);
			Result += bBefore ? File.BeforeText : File.AfterText;
		}
		return Result.IsEmpty() ? TEXT("(No C# source text changed.)") : Result;
	}

	FString BuildPreviewDiagnosticsText(const FFinareAuthoringPreviewResult& Preview)
	{
		FString Result;
		for (const FFinareAuthoringDiagnostic& Diagnostic : Preview.Diagnostics)
		{
			if (!Result.IsEmpty())
			{
				Result += TEXT("\n");
			}
			Result += Diagnostic.ToDisplayString();
		}
		return Result;
	}

	FString BuildSemanticReviewText(const FFinareAuthoringPreviewResult& Preview)
	{
		FString Result;
		for (const FFinareAuthoringSemanticChange& Change : Preview.SemanticChanges)
		{
			if (!Result.IsEmpty())
			{
				Result += TEXT("\n");
			}
			Result += FString::Printf(
				TEXT("[%s] %s · %s/%s\n%s"),
				*Change.Category,
				*Change.FunctionName,
				*Change.EntityKind,
				*Change.Facet,
				*Change.Summary);
		}
		return Result.IsEmpty()
			       ? TEXT("No executable graph semantics changed; review the declaration/source diff below.")
			       : Result;
	}
}

namespace
{
	struct FStaticLibraryReloadParticipant
	{
		TWeakObjectPtr<UFinareWorldSubsystem> World;
		uint64 WorldHostId = 0;
		int32 CandidateGeneration = 0;
		int32 ActiveGeneration = 0;
		bool bActiveGenerationKnown = false;
	};

	FString BuildStaticLibraryReloadTransaction(
		const FString& AssemblyPath,
		const FString& TypeName,
		const FString& IncomingTransactionJson,
		const FString& State,
		const TArray<TPair<FString, FString>>& Phases,
		const TArray<FStaticLibraryReloadParticipant>& Participants,
		const FString& Failure = FString())
	{
		TSharedPtr<FJsonObject> Root;
		if (!IncomingTransactionJson.IsEmpty())
		{
			const TSharedRef<TJsonReader<>> Reader =
				TJsonReaderFactory<>::Create(IncomingTransactionJson);
			FJsonSerializer::Deserialize(Reader, Root);
		}
		if (!Root.IsValid())
		{
			Root = MakeShared<FJsonObject>();
		}
		Root->SetStringField(
			TEXT("schemaVersion"),
			TEXT("unifiedscript.reload-transaction/v0.1"));
		FString TransactionId;
		if (!Root->TryGetStringField(TEXT("transactionId"), TransactionId) ||
			TransactionId.IsEmpty())
		{
			Root->SetStringField(
				TEXT("transactionId"),
				FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
		}
		FString Source;
		if (!Root->TryGetStringField(TEXT("source"), Source) || Source.IsEmpty())
		{
			Root->SetStringField(TEXT("source"), TEXT("staticFunctionLibrary"));
		}
		Root->SetStringField(TEXT("state"), State);
		Root->SetStringField(TEXT("scope"), TEXT("staticLibraryBatch"));
		Root->SetStringField(TEXT("generationScope"), TEXT("perParticipant"));
		Root->SetStringField(
			TEXT("target"),
			FString::Printf(TEXT("%s|%s"), *AssemblyPath, *TypeName));
		if (!Failure.IsEmpty())
		{
			Root->SetStringField(TEXT("failure"), Failure);
		}
		else
		{
			Root->RemoveField(TEXT("failure"));
		}

		const TArray<TSharedPtr<FJsonValue>>* ExistingPhases = nullptr;
		TArray<TSharedPtr<FJsonValue>> JsonPhases;
		if (Root->TryGetArrayField(TEXT("phases"), ExistingPhases) && ExistingPhases != nullptr)
		{
			JsonPhases = *ExistingPhases;
		}
		JsonPhases.Reserve(JsonPhases.Num() + Phases.Num());
		for (const TPair<FString, FString>& PhasePair : Phases)
		{
			const TSharedRef<FJsonObject> Phase = MakeShared<FJsonObject>();
			Phase->SetStringField(TEXT("name"), PhasePair.Key);
			Phase->SetStringField(TEXT("state"), PhasePair.Value);
			Phase->SetNumberField(TEXT("durationMicroseconds"), 0.0);
			Phase->SetStringField(
				TEXT("detail"),
				FString::Printf(TEXT("Static FunctionLibrary target %s."), *PhasePair.Value));
			JsonPhases.Add(MakeShared<FJsonValueObject>(Phase));
		}
		Root->SetArrayField(TEXT("phases"), MoveTemp(JsonPhases));

		TArray<TSharedPtr<FJsonValue>> GenerationTargets;
		for (const FStaticLibraryReloadParticipant& Participant : Participants)
		{
			const TSharedRef<FJsonObject> Target = MakeShared<FJsonObject>();
			Target->SetNumberField(
				TEXT("worldHostId"),
				static_cast<double>(Participant.WorldHostId));
			Target->SetNumberField(
				TEXT("candidateGeneration"),
				static_cast<double>(FMath::Max(Participant.CandidateGeneration, 0)));
			Target->SetBoolField(TEXT("candidateGenerationKnown"), Participant.CandidateGeneration > 0);
			Target->SetNumberField(
				TEXT("activeGeneration"),
				static_cast<double>(FMath::Max(Participant.ActiveGeneration, 0)));
			Target->SetBoolField(TEXT("activeGenerationKnown"), Participant.bActiveGenerationKnown);
			GenerationTargets.Add(MakeShared<FJsonValueObject>(Target));
		}
		Root->SetArrayField(TEXT("participants"), MoveTemp(GenerationTargets));
		Root->SetNumberField(TEXT("fromGeneration"), 0.0);
		Root->SetNumberField(TEXT("candidateGeneration"), 0.0);
		Root->SetNumberField(TEXT("activeGeneration"), 0.0);
		Root->SetBoolField(TEXT("fromGenerationKnown"), false);
		Root->SetBoolField(TEXT("candidateGenerationKnown"), false);
		Root->SetBoolField(TEXT("activeGenerationKnown"), false);
		if (!Participants.IsEmpty())
		{
			Root->SetStringField(TEXT("generationDetail"), TEXT("See participants for per-World generations."));
		}

		FString Json;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Json);
		return FJsonSerializer::Serialize(Root, Writer) ? Json : FString();
	}

	FFinareReloadBatchResult ReloadStaticLibraryForAsset(
		const UFinareAsset* Asset,
		const FString& ReloadTransactionJson)
	{
		FFinareReloadBatchResult Result;
		if (Asset == nullptr ||
			Asset->ScriptClassKind != EFinareClassKind::BlueprintFunctionLibrary)
		{
			return Result;
		}

		FString AssemblyPath;
		FString TypeName;
		FString ResolveError;
		if (!Asset->ResolveRuntimeConfiguration(true, AssemblyPath, TypeName, ResolveError))
		{
			Result.Matched = 1;
			Result.Failed = 1;
			Result.PrepareRejected = 1;
			Result.ObservedReloadTransactionJson = BuildStaticLibraryReloadTransaction(
				AssemblyPath,
				TypeName,
				ReloadTransactionJson,
				TEXT("failed"),
				{{TEXT("staticLibraryResolve"), TEXT("failed")}},
				{},
				ResolveError);
			return Result;
		}

		TArray<FStaticLibraryReloadParticipant> Participants;
		bool bPrepareFailed = false;
		bool bAbortFailed = false;
		FString Failure;
		auto AppendFailure = [&Failure](const FString& Error)
		{
			if (Error.IsEmpty())
			{
				return;
			}
			if (!Failure.IsEmpty())
			{
				Failure += TEXT("; ");
			}
			Failure += Error;
		};
		for (TObjectIterator<UFinareWorldSubsystem> It; It; ++It)
		{
			UFinareWorldSubsystem* WorldSubsystem = *It;
			if (!IsValid(WorldSubsystem) || !WorldSubsystem->IsManagedWorldReady())
			{
				continue;
			}

			int32 CandidateGeneration = 0;
			FString Error;
			if (!WorldSubsystem->PrepareManagedStaticLibraryReload(
				AssemblyPath,
				TypeName,
				CandidateGeneration,
				Error))
			{
				bPrepareFailed = true;
				++Result.Matched;
				++Result.PrepareRejected;
				AppendFailure(Error);
				continue;
			}
			if (CandidateGeneration == 0)
			{
				continue;
			}
			Participants.Add(FStaticLibraryReloadParticipant{
				WorldSubsystem,
				WorldSubsystem->GetWorldHostId(),
				CandidateGeneration,
				0,
				false
			});
			++Result.Matched;
			++Result.Prepared;
		}

		auto AbortParticipants = [
				&Participants,
				&Result,
				&AssemblyPath,
				&TypeName,
				&Failure,
				&AppendFailure,
				&bAbortFailed](
			const FString& Reason)
		{
			(void)Reason;
			for (FStaticLibraryReloadParticipant& Participant : Participants)
			{
				UFinareWorldSubsystem* WorldSubsystem = Participant.World.Get();
				if (WorldSubsystem == nullptr)
				{
					bAbortFailed = true;
					AppendFailure(TEXT("The static-library reload World was destroyed during rollback."));
					continue;
				}
				FString Error;
				if (WorldSubsystem->AbortManagedStaticLibraryReload(
					AssemblyPath,
					TypeName,
					Participant.CandidateGeneration,
					Error))
				{
					++Result.Aborted;
					Participant.ActiveGeneration = 0;
					Participant.bActiveGenerationKnown = false;
				}
				else
				{
					bAbortFailed = true;
					AppendFailure(Error);
				}
			}
		};

		if (bPrepareFailed)
		{
			AbortParticipants(TEXT("prepare"));
			Result.Failed = FMath::Max(Result.Matched, 1);
			Result.ObservedReloadTransactionJson = BuildStaticLibraryReloadTransaction(
				AssemblyPath,
				TypeName,
				ReloadTransactionJson,
				bAbortFailed ? TEXT("rollbackFailed") : TEXT("rolledBack"),
				{
					{TEXT("staticLibraryPrepare"), TEXT("failed")},
					{
						TEXT("staticLibraryAbort"),
						Participants.IsEmpty()
							? TEXT("notRequired")
							: (bAbortFailed ? TEXT("failed") : TEXT("succeeded"))
					}
				},
				Participants,
				Failure);
			return Result;
		}

		if (Participants.IsEmpty())
		{
			return Result;
		}

		for (FStaticLibraryReloadParticipant& Participant : Participants)
		{
			UFinareWorldSubsystem* WorldSubsystem = Participant.World.Get();
			FString Error;
			if (WorldSubsystem == nullptr ||
				!WorldSubsystem->CommitManagedStaticLibraryReload(
					AssemblyPath,
					TypeName,
					Participant.CandidateGeneration,
					Participant.ActiveGeneration,
					Error))
			{
				++Result.CommitRejected;
				AppendFailure(Error);
				AbortParticipants(TEXT("commit"));
				Result.Committed = 0;
				for (const FStaticLibraryReloadParticipant& Remaining : Participants)
				{
					Result.Committed += Remaining.ActiveGeneration > 0 ? 1 : 0;
				}
				Result.Failed = FMath::Max(Result.Matched, 1);
				Result.ObservedReloadTransactionJson = BuildStaticLibraryReloadTransaction(
					AssemblyPath,
					TypeName,
					ReloadTransactionJson,
					bAbortFailed ? TEXT("rollbackFailed") : TEXT("rolledBack"),
					{
						{TEXT("staticLibraryPrepare"), TEXT("succeeded")},
						{TEXT("staticLibraryCommit"), TEXT("failed")},
						{
							TEXT("staticLibraryAbort"),
							bAbortFailed ? TEXT("failed") : TEXT("succeeded")
						}
					},
					Participants,
					Failure);
				return Result;
			}
			Participant.bActiveGenerationKnown = true;
			++Result.Committed;
		}

		for (FStaticLibraryReloadParticipant& Participant : Participants)
		{
			UFinareWorldSubsystem* WorldSubsystem = Participant.World.Get();
			FString Error;
			if (WorldSubsystem == nullptr ||
				!WorldSubsystem->FinalizeManagedStaticLibraryReload(
					AssemblyPath,
					TypeName,
					Participant.CandidateGeneration,
					Error))
			{
				++Result.FinalizeFailed;
				++Result.Failed;
				AppendFailure(Error);
				continue;
			}
			++Result.Reloaded;
		}

		Result.ObservedReloadTransactionJson = BuildStaticLibraryReloadTransaction(
			AssemblyPath,
			TypeName,
			ReloadTransactionJson,
			Result.Failed == 0 ? TEXT("published") : TEXT("failed"),
			{
				{TEXT("staticLibraryPrepare"), TEXT("succeeded")},
				{TEXT("staticLibraryCommit"), TEXT("succeeded")},
				{
					TEXT("staticLibraryFinalize"),
					Result.Failed == 0 ? TEXT("succeeded") : TEXT("failed")
				}
			},
			Participants,
			Failure);
		return Result;
	}
}

class SFinareCanvasMinimap final : public SLeafWidget
{
public:
	SLATE_BEGIN_ARGS(SFinareCanvasMinimap)
		{
		}

		SLATE_ARGUMENT(SFinareAuthoringSurface*, Owner)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs)
	{
		Owner = InArgs._Owner;
		SetCanTick(true);
	}

	virtual void Tick(
		const FGeometry& AllottedGeometry,
		const double InCurrentTime,
		const float InDeltaTime) override
	{
		Invalidate(EInvalidateWidgetReason::Paint);
	}

	virtual FVector2D ComputeDesiredSize(float LayoutScaleMultiplier) const override
	{
		return FVector2D(184.0, 116.0);
	}

	virtual int32 OnPaint(
		const FPaintArgs& Args,
		const FGeometry& AllottedGeometry,
		const FSlateRect& MyCullingRect,
		FSlateWindowElementList& OutDrawElements,
		int32 LayerId,
		const FWidgetStyle& InWidgetStyle,
		bool bParentEnabled) const override
	{
		FSnapshot Snapshot;
		FTransform Transform;
		if (!BuildSnapshot(Snapshot) || !BuildTransform(AllottedGeometry, Snapshot, Transform))
		{
			return LayerId;
		}

		const FSlateBrush* WhiteBrush = FAppStyle::GetBrush(TEXT("WhiteBrush"));
		FSlateDrawElement::MakeBox(
			OutDrawElements,
			LayerId,
			AllottedGeometry.ToPaintGeometry(),
			WhiteBrush,
			ESlateDrawEffect::None,
			FLinearColor(0.018f, 0.024f, 0.034f, 0.94f));

		for (const TPair<FVector2f, FVector2f>& Wire : Snapshot.Wires)
		{
			const TArray<FVector2f> Points = {
				Transform.GraphToLocal(Wire.Key),
				Transform.GraphToLocal(Wire.Value)
			};
			FSlateDrawElement::MakeLines(
				OutDrawElements,
				LayerId + 1,
				AllottedGeometry.ToPaintGeometry(),
				Points,
				ESlateDrawEffect::None,
				FLinearColor(0.24f, 0.42f, 0.66f, 0.75f),
				true,
				1.0f);
		}

		for (const FItem& Item : Snapshot.Items)
		{
			const FVector2f TopLeft = Transform.GraphToLocal(
				FVector2f(Item.Bounds.Left, Item.Bounds.Top));
			const FVector2f BottomRight = Transform.GraphToLocal(
				FVector2f(Item.Bounds.Right, Item.Bounds.Bottom));
			const FVector2f Size(
				FMath::Max(2.0f, BottomRight.X - TopLeft.X),
				FMath::Max(2.0f, BottomRight.Y - TopLeft.Y));
			FSlateDrawElement::MakeBox(
				OutDrawElements,
				LayerId + 2,
				AllottedGeometry.ToPaintGeometry(Size, FSlateLayoutTransform(TopLeft)),
				WhiteBrush,
				ESlateDrawEffect::None,
				Item.Color);
		}

		const FVector2f ViewTopLeft = Transform.GraphToLocal(
			FVector2f(Snapshot.Viewport.Left, Snapshot.Viewport.Top));
		const FVector2f ViewBottomRight = Transform.GraphToLocal(
			FVector2f(Snapshot.Viewport.Right, Snapshot.Viewport.Bottom));
		const TArray<FVector2f> ViewOutline = {
			ViewTopLeft,
			FVector2f(ViewBottomRight.X, ViewTopLeft.Y),
			ViewBottomRight,
			FVector2f(ViewTopLeft.X, ViewBottomRight.Y),
			ViewTopLeft
		};
		FSlateDrawElement::MakeLines(
			OutDrawElements,
			LayerId + 3,
			AllottedGeometry.ToPaintGeometry(),
			ViewOutline,
			ESlateDrawEffect::None,
			FLinearColor(0.96f, 0.72f, 0.20f, 0.95f),
			true,
			1.5f);
		return LayerId + 3;
	}

	virtual FReply OnMouseButtonDown(
		const FGeometry& MyGeometry,
		const FPointerEvent& MouseEvent) override
	{
		if (MouseEvent.GetEffectingButton() != EKeys::LeftMouseButton)
		{
			return FReply::Unhandled();
		}
		Navigate(MyGeometry, MouseEvent);
		return FReply::Handled().CaptureMouse(AsShared());
	}

	virtual FReply OnMouseMove(
		const FGeometry& MyGeometry,
		const FPointerEvent& MouseEvent) override
	{
		if (!HasMouseCapture() || !MouseEvent.IsMouseButtonDown(EKeys::LeftMouseButton))
		{
			return FReply::Unhandled();
		}
		Navigate(MyGeometry, MouseEvent);
		return FReply::Handled();
	}

	virtual FReply OnMouseButtonUp(
		const FGeometry& MyGeometry,
		const FPointerEvent& MouseEvent) override
	{
		if (MouseEvent.GetEffectingButton() == EKeys::LeftMouseButton && HasMouseCapture())
		{
			Navigate(MyGeometry, MouseEvent);
			return FReply::Handled().ReleaseMouseCapture();
		}
		return FReply::Unhandled();
	}

private:
	struct FItem
	{
		FSlateRect Bounds;
		FLinearColor Color;
	};

	struct FSnapshot
	{
		TArray<FItem> Items;
		TArray<TPair<FVector2f, FVector2f>> Wires;
		FSlateRect Bounds;
		FSlateRect Viewport;
	};

	struct FTransform
	{
		FVector2f GraphOrigin = FVector2f::ZeroVector;
		FVector2f LocalOrigin = FVector2f::ZeroVector;
		float Scale = 1.0f;

		FVector2f GraphToLocal(const FVector2f& Point) const
		{
			return LocalOrigin + (Point - GraphOrigin) * Scale;
		}

		FVector2f LocalToGraph(const FVector2f& Point) const
		{
			return GraphOrigin + (Point - LocalOrigin) / Scale;
		}
	};

	bool BuildSnapshot(FSnapshot& Out) const
	{
		if (Owner == nullptr || !Owner->AuthoringGraph.IsValid() ||
			!Owner->GraphEditor.IsValid() || Owner->AuthoringGraph->Nodes.IsEmpty())
		{
			return false;
		}

		bool bHasBounds = false;
		FVector2f Min = FVector2f::ZeroVector;
		FVector2f Max = FVector2f::ZeroVector;
		const auto AddBounds = [&bHasBounds, &Min, &Max](const FSlateRect& Bounds)
		{
			const FVector2f CandidateMin(Bounds.Left, Bounds.Top);
			const FVector2f CandidateMax(Bounds.Right, Bounds.Bottom);
			if (!bHasBounds)
			{
				Min = CandidateMin;
				Max = CandidateMax;
				bHasBounds = true;
			}
			else
			{
				Min.X = FMath::Min(Min.X, CandidateMin.X);
				Min.Y = FMath::Min(Min.Y, CandidateMin.Y);
				Max.X = FMath::Max(Max.X, CandidateMax.X);
				Max.Y = FMath::Max(Max.Y, CandidateMax.Y);
			}
		};

		TMap<const UEdGraphNode*, FSlateRect> NodeBounds;
		for (const UEdGraphNode* Node : Owner->AuthoringGraph->Nodes)
		{
			if (Node == nullptr)
			{
				continue;
			}
			FSlateRect Bounds;
			if (!Owner->GraphEditor->GetBoundsForNode(Node, Bounds, 0.0f))
			{
				FVector2f Size(240.0f, 120.0f);
				if (const UFinareEdGraphCommentNode* Comment =
					Cast<UFinareEdGraphCommentNode>(Node))
				{
					Size = FVector2f(Comment->NodeWidth, Comment->NodeHeight);
				}
				else if (Cast<UFinareEdGraphRerouteNode>(Node) != nullptr)
				{
					Size = FVector2f(20.0f, 20.0f);
				}
				Bounds = FSlateRect(
					Node->NodePosX,
					Node->NodePosY,
					Node->NodePosX + Size.X,
					Node->NodePosY + Size.Y);
			}
			NodeBounds.Add(Node, Bounds);
			AddBounds(Bounds);

			FLinearColor Color(0.16f, 0.42f, 0.72f, 0.88f);
			if (Cast<UFinareEdGraphCommentNode>(Node) != nullptr)
			{
				Color = FLinearColor(0.18f, 0.34f, 0.52f, 0.34f);
			}
			else if (Cast<UFinareEdGraphRerouteNode>(Node) != nullptr)
			{
				Color = FLinearColor(0.72f, 0.76f, 0.82f, 0.95f);
			}
			Out.Items.Add({Bounds, Color});
		}

		for (const UEdGraphNode* Node : Owner->AuthoringGraph->Nodes)
		{
			const FSlateRect* SourceBounds = NodeBounds.Find(Node);
			if (Node == nullptr || SourceBounds == nullptr)
			{
				continue;
			}
			const FVector2f SourceCenter(
				(SourceBounds->Left + SourceBounds->Right) * 0.5f,
				(SourceBounds->Top + SourceBounds->Bottom) * 0.5f);
			for (const UEdGraphPin* Pin : Node->Pins)
			{
				if (Pin == nullptr || Pin->Direction != EGPD_Output)
				{
					continue;
				}
				for (const UEdGraphPin* LinkedPin : Pin->LinkedTo)
				{
					const UEdGraphNode* TargetNode = LinkedPin != nullptr
						                                 ? LinkedPin->GetOwningNode()
						                                 : nullptr;
					const FSlateRect* TargetBounds = NodeBounds.Find(TargetNode);
					if (TargetBounds != nullptr)
					{
						Out.Wires.Emplace(
							SourceCenter,
							FVector2f(
								(TargetBounds->Left + TargetBounds->Right) * 0.5f,
								(TargetBounds->Top + TargetBounds->Bottom) * 0.5f));
					}
				}
			}
		}

		FVector2f ViewLocation = FVector2f::ZeroVector;
		float ViewZoom = 1.0f;
		Owner->GraphEditor->GetViewLocation(ViewLocation, ViewZoom);
		ViewZoom = FMath::Max(ViewZoom, 0.01f);
		FVector2f ViewSize = Owner->GraphEditor->GetCachedGeometry().GetLocalSize() / ViewZoom;
		if (ViewSize.X <= 1.0f || ViewSize.Y <= 1.0f)
		{
			ViewSize = FVector2f(1280.0f, 720.0f) / ViewZoom;
		}
		Out.Viewport = FSlateRect(
			ViewLocation.X,
			ViewLocation.Y,
			ViewLocation.X + ViewSize.X,
			ViewLocation.Y + ViewSize.Y);
		AddBounds(Out.Viewport);
		if (!bHasBounds)
		{
			return false;
		}
		constexpr float GraphPadding = 80.0f;
		Out.Bounds = FSlateRect(
			Min.X - GraphPadding,
			Min.Y - GraphPadding,
			Max.X + GraphPadding,
			Max.Y + GraphPadding);
		return true;
	}

	static bool BuildTransform(
		const FGeometry& Geometry,
		const FSnapshot& Snapshot,
		FTransform& Out)
	{
		constexpr float Padding = 7.0f;
		const FVector2f LocalSize = Geometry.GetLocalSize();
		const FVector2f GraphSize(
			Snapshot.Bounds.Right - Snapshot.Bounds.Left,
			Snapshot.Bounds.Bottom - Snapshot.Bounds.Top);
		if (LocalSize.X <= Padding * 2.0f || LocalSize.Y <= Padding * 2.0f ||
			GraphSize.X <= 0.0f || GraphSize.Y <= 0.0f)
		{
			return false;
		}
		const FVector2f Available = LocalSize - FVector2f(Padding * 2.0f);
		Out.Scale = FMath::Min(Available.X / GraphSize.X, Available.Y / GraphSize.Y);
		if (!FMath::IsFinite(Out.Scale) || Out.Scale <= 0.0f)
		{
			return false;
		}
		Out.GraphOrigin = FVector2f(Snapshot.Bounds.Left, Snapshot.Bounds.Top);
		Out.LocalOrigin = FVector2f(Padding) + (Available - GraphSize * Out.Scale) * 0.5f;
		return true;
	}

	void Navigate(const FGeometry& Geometry, const FPointerEvent& MouseEvent)
	{
		FSnapshot Snapshot;
		FTransform Transform;
		if (Owner == nullptr || !BuildSnapshot(Snapshot) ||
			!BuildTransform(Geometry, Snapshot, Transform))
		{
			return;
		}
		FVector2f ViewLocation = FVector2f::ZeroVector;
		float ViewZoom = 1.0f;
		Owner->GraphEditor->GetViewLocation(ViewLocation, ViewZoom);
		ViewZoom = FMath::Max(ViewZoom, 0.01f);
		FVector2f ViewSize = Owner->GraphEditor->GetCachedGeometry().GetLocalSize() / ViewZoom;
		if (ViewSize.X <= 1.0f || ViewSize.Y <= 1.0f)
		{
			ViewSize = FVector2f(1280.0f, 720.0f) / ViewZoom;
		}
		const FVector2f LocalPoint = Geometry.AbsoluteToLocal(
			MouseEvent.GetScreenSpacePosition());
		const FVector2f GraphCenter = Transform.LocalToGraph(LocalPoint);
		Owner->GraphEditor->SetViewLocation(GraphCenter - ViewSize * 0.5f, ViewZoom);
		Owner->PersistCanvasView(false);
	}

	SFinareAuthoringSurface* Owner = nullptr;
};

void SFinareAuthoringSurface::Construct(const FArguments& InArgs)
{
	InitialSurfacePath = InArgs._InitialSurfacePath;
	DailyBuildCompletedHandle = IFinareEditorModule::Get().OnDailyBuildCompleted().AddSP(
		this, &SFinareAuthoringSurface::HandleDailyBuildCompleted);
	LoadNodePalettePreferences();
	LoadCanvasPreferences();

	AuthoringGraph.Reset(NewObject<UFinareEdGraph>(
		GetTransientPackage(),
		NAME_None,
		RF_Transient));
	AuthoringGraph->Schema = UFinareEdGraphSchema::StaticClass();
	AuthoringGraph->OnDataRewireRequested =
		FOnFinareDataRewireRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleDataRewireRequested);
	AuthoringGraph->OnControlRewireRequested =
		FOnFinareControlRewireRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleControlRewireRequested);
	AuthoringGraph->OnDataDisconnectRequested =
		FOnFinareDataDisconnectRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleDataDisconnectRequested);
	AuthoringGraph->OnRerouteRequested =
		FOnFinareRerouteRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleRerouteRequested);
	AuthoringGraph->OnNodeCreateRequested =
		FOnFinareNodeCreateRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleNodeCreateRequested);
	AuthoringGraph->OnStatementDeleteRequested =
		FOnFinareStatementDeleteRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleStatementDeleteRequested);
	AuthoringGraph->OnBreakpointToggleRequested =
		FOnFinareBreakpointToggleRequested::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleBreakpointToggleRequested);

	FGraphAppearanceInfo GraphAppearance;
	GraphAppearance.CornerText = LOCTEXT("GraphCorner", "FINARE  /  VISUAL BEHAVIOR");
	GraphAppearance.InstructionText = LOCTEXT(
		"GraphInstruction",
		"Select a function in My Script to inspect its visual behavior.");
	SGraphEditor::FGraphEditorEvents GraphEvents;
	GraphEvents.OnSelectionChanged = SGraphEditor::FOnSelectionChanged::CreateSP(
		this,
		&SFinareAuthoringSurface::HandleGraphSelectionChanged);
	GraphEvents.OnNodeDoubleClicked = FSingleNodeEvent::CreateSP(
		this,
		&SFinareAuthoringSurface::HandleGraphNodeDoubleClicked);

	GraphCommands = MakeShared<FUICommandList>();
	GraphCommands->MapAction(
		FGenericCommands::Get().Copy,
		FExecuteAction::CreateSP(this, &SFinareAuthoringSurface::HandleCopySelectedStatement),
		FCanExecuteAction::CreateSP(this, &SFinareAuthoringSurface::CanCopySelectedStatement));
	GraphCommands->MapAction(
		FGenericCommands::Get().Paste,
		FExecuteAction::CreateSP(this, &SFinareAuthoringSurface::HandlePasteStatement),
		FCanExecuteAction::CreateSP(this, &SFinareAuthoringSurface::CanPasteStatement));
	GraphCommands->MapAction(
		FGenericCommands::Get().Undo,
		FExecuteAction::CreateSP(this, &SFinareAuthoringSurface::HandleUndoAuthoringCommand),
		FCanExecuteAction::CreateSP(this, &SFinareAuthoringSurface::CanUndoAuthoringCommand));
	GraphCommands->MapAction(
		FGenericCommands::Get().Redo,
		FExecuteAction::CreateSP(this, &SFinareAuthoringSurface::HandleRedoAuthoringCommand),
		FCanExecuteAction::CreateSP(this, &SFinareAuthoringSurface::CanRedoAuthoringCommand));

	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
		.Padding(10.0f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.FillWidth(1.0f)
				.VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot()
					.AutoHeight()
					[
						SNew(STextBlock)
						.Text(LOCTEXT("Title", "Finare Script Asset"))
						.Font(FAppStyle::GetFontStyle(TEXT("HeadingExtraSmall")))
					]
					+ SVerticalBox::Slot()
					.AutoHeight()
					[
						SNew(STextBlock)
						.Text_Lambda([this]()
						{
							return FText::Format(
								LOCTEXT("Counts",
								        "C# <-> Visual  |  {0} assets  |  {1} states  |  {2} functions  |  {3} graph nodes  |  {4}  |  {5} source-only"),
								Document.Assets.Num(),
								Document.GetStateCount(),
								Document.GetFunctionCount(),
								Document.GetActionCount(),
								FText::FromString(VisualProfileDisplayName.IsEmpty()
									                  ? TEXT("profile pending")
									                  : VisualProfileDisplayName),
								OpaqueMethodCount);
						})
						.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
					]
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(4.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("SelectedAsset", "Selected Script Asset"))
					.ToolTipText(LOCTEXT("SelectedAssetTip",
					                     "Load the AuthoringSurfacePath from the selected Finare Data Asset."))
					.OnClicked(this, &SFinareAuthoringSurface::HandleLoadSelectedAsset)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(4.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("InitialSurface", "Initial Surface"))
					.ToolTipText(LOCTEXT(
						"InitialSurfaceTip",
						"Reload the surface path supplied by the project integration."))
					.Visibility_Lambda([this]()
					{
						return InitialSurfacePath.IsEmpty()
							       ? EVisibility::Collapsed
							       : EVisibility::Visible;
					})
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleLoadInitialSurface)
				]
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(2.0f, 8.0f, 2.0f, 0.0f)
			[
				SAssignNew(DebugPanel, SFinareDebugPanel)
				.ServerUrl_Lambda([this]()
				{
					return ServerUrlTextBox.IsValid()
						       ? ServerUrlTextBox->GetText().ToString()
						       : FString();
				})
				.OnNodeFocused(FOnFinareDebugNodeFocused::CreateSP(
					this,
					&SFinareAuthoringSurface::HandleDebugNodeFocused))
				.OnDebugStateChanged(FSimpleDelegate::CreateSP(
					this,
					&SFinareAuthoringSurface::RefreshDebugNodeStates))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(2.0f, 8.0f, 2.0f, 0.0f)
			[
				SAssignNew(DiagnosticsBox, SVerticalBox)
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.VAlign(VAlign_Center)
				.Padding(0.0f, 0.0f, 8.0f, 0.0f)
				[
					SNew(STextBlock)
					.Text(LOCTEXT("WorkbenchUrl", "Workbench"))
					.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
				]
				+ SHorizontalBox::Slot()
				.FillWidth(1.0f)
				[
					SAssignNew(ServerUrlTextBox, SEditableTextBox)
					.Text(FText::FromString(TEXT("http://127.0.0.1:4173")))
					.HintText(LOCTEXT("WorkbenchUrlHint", "http://127.0.0.1:4173"))
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(6.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SBox)
					.WidthOverride(132.0f)
					[
						SAssignNew(AuthoringGroupLabelTextBox, SEditableTextBox)
						.Text(LOCTEXT("DefaultAuthoringGroupLabel", "Grouped edit"))
						.HintText(LOCTEXT("AuthoringGroupLabelHint", "Transaction label"))
						.IsEnabled_Lambda([this]()
						{
							return !bPatchRequestPending && ActiveAuthoringGroupId.IsEmpty();
						})
					]
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(4.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text_Lambda([this]()
					{
						return ActiveAuthoringGroupId.IsEmpty()
							       ? LOCTEXT("BeginAuthoringGroup", "Begin Group")
							       : FText::Format(
								       LOCTEXT("CommitAuthoringGroup", "Commit ({0})"),
								       ActiveAuthoringGroupStagedCount);
					})
					.ToolTipText_Lambda([this]()
					{
						return ActiveAuthoringGroupId.IsEmpty()
							       ? LOCTEXT(
								       "BeginAuthoringGroupTip",
								       "Start an in-memory draft. Following edits are staged without source writes or runtime reload until Commit.")
							       : FText::Format(
								       LOCTEXT(
									       "CommitAuthoringGroupTip",
									       "Atomically publish {0} staged edit(s) as one named Undo/Redo transaction: {1}"),
								       ActiveAuthoringGroupStagedCount,
								       FText::FromString(ActiveAuthoringGroupDescription));
					})
					.IsEnabled_Lambda([this]()
					{
						return !bPatchRequestPending &&
						(ActiveAuthoringGroupId.IsEmpty()
							 ? AuthoringGroupLabelTextBox.IsValid() &&
							 !AuthoringGroupLabelTextBox->GetText().IsEmpty()
							 : ActiveAuthoringGroupStagedCount > 0);
					})
					.OnClicked_Lambda([this]()
					{
						return ActiveAuthoringGroupId.IsEmpty()
							       ? HandleBeginAuthoringGroup()
							       : HandleCommitAuthoringGroup();
					})
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(4.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("CancelAuthoringGroup", "Cancel Group"))
					.Visibility_Lambda([this]()
					{
						return ActiveAuthoringGroupId.IsEmpty()
							       ? EVisibility::Collapsed
							       : EVisibility::Visible;
					})
					.IsEnabled_Lambda([this]()
					{
						return !bPatchRequestPending && !ActiveAuthoringGroupId.IsEmpty();
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleCancelAuthoringGroup)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(6.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("UndoAuthoring", "Undo"))
					.IsEnabled_Lambda([this]()
					{
						return !bPatchRequestPending && ActiveAuthoringGroupId.IsEmpty() && bCanUndoAuthoring;
					})
					.ToolTipText_Lambda([this]()
					{
						return FText::FromString(
							bCanUndoAuthoring
								? FString::Printf(
									TEXT("Undo %s (%lld transaction%s available)"),
									UndoAuthoringDescription.IsEmpty()
										? TEXT("the latest authoring edit")
										: *UndoAuthoringDescription,
									UndoAuthoringDepth,
									UndoAuthoringDepth == 1 ? TEXT("") : TEXT("s"))
								: TEXT("No committed authoring transaction is available to undo."));
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleUndoAuthoringTransaction)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(4.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("RedoAuthoring", "Redo"))
					.IsEnabled_Lambda([this]()
					{
						return !bPatchRequestPending && ActiveAuthoringGroupId.IsEmpty() && bCanRedoAuthoring;
					})
					.ToolTipText_Lambda([this]()
					{
						return FText::FromString(
							bCanRedoAuthoring
								? FString::Printf(
									TEXT("Redo %s (%lld transaction%s available)"),
									RedoAuthoringDescription.IsEmpty()
										? TEXT("the next authoring edit")
										: *RedoAuthoringDescription,
									RedoAuthoringDepth,
									RedoAuthoringDepth == 1 ? TEXT("") : TEXT("s"))
								: TEXT("No undone authoring transaction is available to redo."));
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleRedoAuthoringTransaction)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(10.0f, 0.0f, 0.0f, 0.0f)
				.VAlign(VAlign_Center)
				[
					SNew(SCheckBox)
					.IsChecked(ECheckBoxState::Checked)
					.OnCheckStateChanged_Lambda([this](ECheckBoxState State)
					{
						bReloadMatchingComponents = State == ECheckBoxState::Checked;
					})
					[
						SNew(STextBlock).Text(LOCTEXT("LiveReload", "Reload Components"))
					]
				]
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.FillWidth(1.0f)
				[
					SAssignNew(PathTextBox, SEditableTextBox)
					.HintText(LOCTEXT("PathHint", "Authoring Surface .surface.json path"))
					.OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type CommitType)
					{
						if (CommitType == ETextCommit::OnEnter)
						{
							HandleLoad();
						}
					})
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(6.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("Browse", "Browse..."))
					.OnClicked(this, &SFinareAuthoringSurface::HandleBrowse)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(6.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("Reload", "Reload"))
					.OnClicked(this, &SFinareAuthoringSurface::HandleLoad)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(10.0f, 0.0f, 0.0f, 0.0f)
				.VAlign(VAlign_Center)
				[
					SNew(SCheckBox)
					.IsChecked(ECheckBoxState::Checked)
					.OnCheckStateChanged_Lambda([this](ECheckBoxState State)
					{
						bAutoReload = State == ECheckBoxState::Checked;
					})
					[
						SNew(STextBlock).Text(LOCTEXT("AutoReload", "Auto Reload"))
					]
				]
			]
			+ SVerticalBox::Slot()
			.FillHeight(1.0f)
			[
				SNew(SSplitter)
				+ SSplitter::Slot()
				.Value(0.20f)
				[
					SNew(SBorder)
					.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
					.Padding(0.0f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(10.0f, 8.0f)
						[
							SNew(SHorizontalBox)
							+ SHorizontalBox::Slot()
							.FillWidth(1.0f)
							.VAlign(VAlign_Center)
							[
								SNew(STextBlock)
								.Text(LOCTEXT("MyScript", "MY SCRIPT"))
								.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
								.ColorAndOpacity(FLinearColor(0.62f, 0.66f, 0.72f))
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							[
								SNew(SButton)
								.Text(LOCTEXT("NewScriptAsset", "+ Asset"))
								.ToolTipText(LOCTEXT(
									"NewScriptAssetTip",
									"Create a C#-owned Script Asset with visual Actor lifecycle hooks through the Workbench transaction."))
								.IsEnabled_Lambda([this]()
								{
									return !bPatchRequestPending &&
									(!WorkbenchSourcePath.IsEmpty() ||
										(!Document.Assets.IsEmpty() &&
											!Document.Assets[0].Span.FilePath.IsEmpty()));
								})
								.OnClicked(this, &SFinareAuthoringSurface::HandleCreateScriptAsset)
							]
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						[
							SNew(SSeparator)
						]
						+ SVerticalBox::Slot()
						.FillHeight(0.58f)
						.Padding(3.0f, 4.0f)
						[
							SAssignNew(TreeView, STreeView<FFinareSurfaceTreeItemPtr>)
							.TreeItemsSource(&RootItems)
							.OnGenerateRow(this, &SFinareAuthoringSurface::GenerateTreeRow)
							.OnGetChildren(this, &SFinareAuthoringSurface::GetTreeChildren)
							.OnSelectionChanged(this, &SFinareAuthoringSurface::HandleSelectionChanged)
							.OnMouseButtonDoubleClick(this, &SFinareAuthoringSurface::HandleTreeDoubleClick)
							.SelectionMode(ESelectionMode::Single)
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						[
							SNew(SSeparator)
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(10.0f, 8.0f, 10.0f, 3.0f)
						[
							SNew(SHorizontalBox)
							+ SHorizontalBox::Slot()
							.FillWidth(1.0f)
							[
								SNew(SVerticalBox)
								+ SVerticalBox::Slot()
								.AutoHeight()
								[
									SNew(STextBlock)
									.Text(LOCTEXT("NodePalette", "PALETTE"))
									.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
									.ColorAndOpacity(FLinearColor(0.62f, 0.66f, 0.72f))
								]
								+ SVerticalBox::Slot()
								.AutoHeight()
								.Padding(0.0f, 2.0f, 0.0f, 0.0f)
								[
									SNew(STextBlock)
									.Text(this, &SFinareAuthoringSurface::GetNodePaletteContextText)
									.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
									.ColorAndOpacity(FLinearColor(0.45f, 0.49f, 0.54f))
									.Clipping(EWidgetClipping::ClipToBounds)
								]
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.VAlign(VAlign_Top)
							[
								SNew(STextBlock)
								.Text(LOCTEXT("NodePaletteShortcut", "Ctrl+Space"))
								.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
								.ColorAndOpacity(FLinearColor(0.42f, 0.46f, 0.51f))
							]
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(6.0f, 2.0f, 6.0f, 4.0f)
						[
							SAssignNew(NodePaletteSearchBox, SSearchBox)
							.HintText(LOCTEXT("NodePaletteSearchHint", "Search nodes, APIs, events..."))
							.OnTextChanged(this, &SFinareAuthoringSurface::HandleNodePaletteSearchChanged)
							.OnTextCommitted(this, &SFinareAuthoringSurface::HandleNodePaletteSearchCommitted)
						]
						+ SVerticalBox::Slot()
						.FillHeight(0.42f)
						.Padding(3.0f, 0.0f, 3.0f, 4.0f)
						[
							SAssignNew(NodePaletteListView, SListView<FFinareNodePaletteItemPtr>)
							.ListItemsSource(&FilteredNodePaletteItems)
							.OnGenerateRow(this, &SFinareAuthoringSurface::GenerateNodePaletteRow)
							.OnMouseButtonDoubleClick(this, &SFinareAuthoringSurface::HandleNodePaletteDoubleClick)
							.SelectionMode(ESelectionMode::Single)
						]
					]
				]
				+ SSplitter::Slot()
				.Value(0.57f)
				[
					SNew(SBorder)
					.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
					.Padding(0.0f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(10.0f, 6.0f)
						[
							SNew(SHorizontalBox)
							+ SHorizontalBox::Slot()
							.FillWidth(1.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SVerticalBox)
								+ SVerticalBox::Slot()
								.AutoHeight()
								[
									SNew(STextBlock)
									.Text_Lambda([this]()
									{
										if (ViewMode == EFinareAuthoringViewMode::Code)
										{
											return FText::FromString(WorkbenchSourcePath.IsEmpty()
												                         ? TEXT("C# Source")
												                         : FPaths::GetCleanFilename(
													                         WorkbenchSourcePath));
										}
										return FText::FromString(
											ActiveFunctionId.IsEmpty()
												? TEXT("Visual Behavior")
												: ActiveFunctionSignature);
									})
									.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
								]
								+ SVerticalBox::Slot()
								.AutoHeight()
								[
									SNew(STextBlock)
									.Text_Lambda([this]()
									{
										if (ViewMode == EFinareAuthoringViewMode::Code)
										{
											return bHasCanonicalSourceSnapshot
												       ? FText::Format(
													       LOCTEXT(
														       "CodeSnapshotStatus",
														       "Canonical workspace snapshot  /  revision {0}  /  {1} characters  /  {2}"),
													       WorkbenchRevision,
													       CodeEditBuffer.Len(),
													       bCodeEditDirty
														       ? LOCTEXT("CodeSnapshotDirty", "unsaved edit")
														       : LOCTEXT("CodeSnapshotClean", "in sync"))
												       : LOCTEXT(
													       "CodeSnapshotPending",
													       "Canonical C# snapshot pending  /  switch or refresh to request the Workbench");
										}
										return FText::Format(
											LOCTEXT("GraphNodeCount",
											        "{0} visual nodes  /  {1} wires  /  Presentation v1"),
											ActiveGraphActionCount,
											GetVisibleGraphEdgeCount());
									})
									.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
									.ColorAndOpacity(FLinearColor(0.48f, 0.52f, 0.58f))
								]
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 8.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SBorder)
								.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
								.Padding(1.0f)
								[
									SNew(SHorizontalBox)
									+ SHorizontalBox::Slot()
									.AutoWidth()
									[
										SNew(SButton)
										.Text(LOCTEXT("GraphViewMode", "Graph"))
										.ToolTipText(LOCTEXT(
											"GraphViewModeTip",
											"Show the visual projection of this Script Asset."))
										.ButtonColorAndOpacity_Lambda([this]()
										{
											return ViewMode == EFinareAuthoringViewMode::Graph
												       ? FSlateColor(FLinearColor(0.10f, 0.36f, 0.64f))
												       : FSlateColor(FLinearColor(0.12f, 0.13f, 0.15f));
										})
										.OnClicked(
											this,
											&SFinareAuthoringSurface::HandleSetViewMode,
											EFinareAuthoringViewMode::Graph)
									]
									+ SHorizontalBox::Slot()
									.AutoWidth()
									[
										SNew(SButton)
										.Text(LOCTEXT("CodeViewMode", "C#"))
										.ToolTipText(LOCTEXT(
											"CodeViewModeTip",
											"Show the exact C# source from the same canonical Workbench snapshot."))
										.ButtonColorAndOpacity_Lambda([this]()
										{
											return ViewMode == EFinareAuthoringViewMode::Code
												       ? FSlateColor(FLinearColor(0.10f, 0.36f, 0.64f))
												       : FSlateColor(FLinearColor(0.12f, 0.13f, 0.15f));
										})
										.OnClicked(
											this,
											&SFinareAuthoringSurface::HandleSetViewMode,
											EFinareAuthoringViewMode::Code)
									]
								]
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 6.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SBox)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.WidthOverride(144.0f)
								[
									SAssignNew(ExtractionNameTextBox, SEditableTextBox)
									.Text(LOCTEXT("DefaultExtractionName", "ExtractedBlock"))
									.HintText(LOCTEXT("ExtractionNameHint", "New method name"))
									.ToolTipText(LOCTEXT(
										"ExtractionNameTip",
										"Name for the private Visual method created from the selected statements."))
								]
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 6.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SButton)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.Text(LOCTEXT("ExtractFunction", "Extract Function"))
								.ToolTipText(LOCTEXT(
									"ExtractFunctionTip",
									"Extract consecutive selected statements into a typed private Visual method; inputs and one optional output are inferred."))
								.IsEnabled(this, &SFinareAuthoringSurface::CanExtractSelectedStatements)
								.OnClicked(this, &SFinareAuthoringSurface::HandleExtractSelectedStatements)
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 6.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SButton)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.Text(LOCTEXT("CollapseScope", "Collapse Scope"))
								.ToolTipText(LOCTEXT(
									"CollapseScopeTip",
									"Wrap two or more consecutive selected statements in one identity-stable C# lexical scope."))
								.IsEnabled(this, &SFinareAuthoringSurface::CanCollapseSelectedStatements)
								.OnClicked(this, &SFinareAuthoringSurface::HandleCollapseSelectedStatements)
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 6.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SButton)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.Text(LOCTEXT("AddComment", "Add Comment"))
								.ToolTipText(LOCTEXT(
									"AddCommentTip",
									"Create a .usgraph-only comment around the selected nodes. Double-click its title to rename it."))
								.IsEnabled_Lambda([this]()
								{
									return GraphEditor.IsValid() && !ActiveFunctionId.IsEmpty();
								})
								.OnClicked(this, &SFinareAuthoringSurface::HandleAddComment)
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 8.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SCheckBox)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.IsChecked_Lambda([this]()
								{
									return bShowCanvasMinimap
										       ? ECheckBoxState::Checked
										       : ECheckBoxState::Unchecked;
								})
								.OnCheckStateChanged_Lambda([this](ECheckBoxState State)
								{
									bShowCanvasMinimap = State == ECheckBoxState::Checked;
									SaveCanvasPreferences();
								})
								.ToolTipText(LOCTEXT(
									"MinimapTip",
									"Show the interactive canvas overview. Click or drag it to navigate."))
								[
									SNew(STextBlock).Text(LOCTEXT("Minimap", "Minimap"))
								]
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.Padding(0.0f, 0.0f, 6.0f, 0.0f)
							.VAlign(VAlign_Center)
							[
								SNew(SButton)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.Text(LOCTEXT("FocusSelection", "Focus Selection"))
								.ToolTipText(LOCTEXT(
									"FocusSelectionTip",
									"Frame the selected nodes (F)."))
								.IsEnabled_Lambda([this]()
								{
									return GraphEditor.IsValid() &&
										!GraphEditor->GetSelectedNodes().IsEmpty();
								})
								.OnClicked(this, &SFinareAuthoringSurface::HandleFocusSelection)
							]
							+ SHorizontalBox::Slot()
							.AutoWidth()
							.VAlign(VAlign_Center)
							[
								SNew(SButton)
								.Visibility_Lambda([this]()
								{
									return ViewMode == EFinareAuthoringViewMode::Graph
										       ? EVisibility::Visible
										       : EVisibility::Collapsed;
								})
								.Text(LOCTEXT("FocusGraph", "Focus All"))
								.ToolTipText(LOCTEXT(
									"FocusGraphTip",
									"Frame every node in the current function (Home)."))
								.OnClicked(this, &SFinareAuthoringSurface::HandleFocusGraph)
							]
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						[
							SNew(SSeparator)
						]
						+ SVerticalBox::Slot()
						.FillHeight(1.0f)
						[
							SNew(SWidgetSwitcher)
							.WidgetIndex_Lambda([this]()
							{
								return ViewMode == EFinareAuthoringViewMode::Graph ? 0 : 1;
							})
							+ SWidgetSwitcher::Slot()
							[
								SNew(SOverlay)
								+ SOverlay::Slot()
								[
									SAssignNew(GraphEditor, SGraphEditor)
									.AdditionalCommands(GraphCommands)
									.GraphToEdit(AuthoringGraph.Get())
									.GraphEvents(GraphEvents)
									.Appearance(GraphAppearance)
									.IsEditable_Lambda([this]()
									{
										return !bPatchRequestPending && !bCodeEditDirty;
									})
									.IsEmpty_Lambda([this]()
									{
										return AuthoringGraph.IsValid() && AuthoringGraph->Nodes.IsEmpty();
									})
									.ShowGraphStateOverlay(false)
									.AllowConnectionSlicing(false)
								]
								+ SOverlay::Slot()
								.HAlign(HAlign_Right)
								.VAlign(VAlign_Bottom)
								.Padding(0.0f, 0.0f, 12.0f, 12.0f)
								[
									SNew(SBorder)
									.Visibility_Lambda([this]()
									{
										return bShowCanvasMinimap && AuthoringGraph.IsValid() &&
										       !AuthoringGraph->Nodes.IsEmpty()
											       ? EVisibility::Visible
											       : EVisibility::Collapsed;
									})
									.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
									.Padding(1.0f)
									.ToolTipText(LOCTEXT(
										"CanvasMinimapNavigationTip",
										"Canvas Minimap - click or drag to center the graph view."))
									[
										SNew(SBox)
										.WidthOverride(184.0f)
										.HeightOverride(116.0f)
										[
											SNew(SFinareCanvasMinimap)
											.Owner(this)
										]
									]
								]
							]
							+ SWidgetSwitcher::Slot()
							[
								SNew(SBorder)
								.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
								.Padding(10.0f)
								[
									SNew(SVerticalBox)
									+ SVerticalBox::Slot()
									.AutoHeight()
									.Padding(0.0f, 0.0f, 0.0f, 8.0f)
									[
										SNew(SHorizontalBox)
										+ SHorizontalBox::Slot()
										.FillWidth(1.0f)
										.VAlign(VAlign_Center)
										[
											SNew(STextBlock)
											.Text_Lambda([this]()
											{
												return FText::FromString(WorkbenchSourcePath.IsEmpty()
													                         ? TEXT(
														                         "No canonical source snapshot loaded")
													                         : WorkbenchSourcePath);
											})
											.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
											.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
											.Clipping(EWidgetClipping::ClipToBounds)
										]
										+ SHorizontalBox::Slot()
										.AutoWidth()
										.Padding(8.0f, 0.0f, 0.0f, 0.0f)
										[
											SNew(SButton)
											.Text(LOCTEXT("ApplyCodeSource", "Preview & Apply"))
											.ToolTipText(LOCTEXT(
												"ApplyCodeSourceTip",
												"Compile this bounded C# buffer, review its semantic/source diff, then atomically publish the confirmed candidate."))
											.ButtonStyle(FAppStyle::Get(), TEXT("PrimaryButton"))
											.IsEnabled_Lambda([this]()
											{
												return bHasCanonicalSourceSnapshot &&
													bCodeEditDirty && !bPatchRequestPending;
											})
											.OnClicked(
												this,
												&SFinareAuthoringSurface::HandleApplyCodeSource)
										]
										+ SHorizontalBox::Slot()
										.AutoWidth()
										.Padding(6.0f, 0.0f, 0.0f, 0.0f)
										[
											SNew(SButton)
											.Text(LOCTEXT("RevertCodeSource", "Revert"))
											.ToolTipText(LOCTEXT(
												"RevertCodeSourceTip",
												"Discard this local C# edit buffer and restore the loaded canonical revision."))
											.IsEnabled_Lambda([this]()
											{
												return bCodeEditDirty && !bPatchRequestPending;
											})
											.OnClicked(
												this,
												&SFinareAuthoringSurface::HandleRevertCodeSource)
										]
										+ SHorizontalBox::Slot()
										.AutoWidth()
										.Padding(6.0f, 0.0f, 0.0f, 0.0f)
										[
											SNew(SButton)
											.Text(LOCTEXT("RefreshCodeSnapshot", "Refresh Snapshot"))
											.ToolTipText(LOCTEXT(
												"RefreshCodeSnapshotTip",
												"Fetch C# and Graph again from one canonical Workbench revision. Revert local edits first."))
											.IsEnabled_Lambda([this]()
											{
												return !bPatchRequestPending && !bCodeEditDirty;
											})
											.OnClicked(
												this,
												&SFinareAuthoringSurface::HandleRefreshSourceSnapshot)
										]
									]
									+ SVerticalBox::Slot()
									.FillHeight(1.0f)
									[
										SNew(SMultiLineEditableTextBox)
										.Text_Lambda([this]()
										{
											return bHasCanonicalSourceSnapshot
												       ? FText::FromString(CodeEditBuffer)
												       : LOCTEXT(
													       "CanonicalSourceUnavailable",
													       "The C# view is bound to the Workbench snapshot, not a second editor buffer.\n\nStart the authoring host, then choose Refresh Snapshot.");
										})
										.IsReadOnly_Lambda([this]()
										{
											return !bHasCanonicalSourceSnapshot || bPatchRequestPending;
										})
										.OnTextChanged(
											this,
											&SFinareAuthoringSurface::HandleCodeTextChanged)
										.AutoWrapText(false)
										.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
									]
								]
							]
						]
					]
				]
				+ SSplitter::Slot()
				.Value(0.23f)
				[
					SNew(SBorder)
					.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
					.Padding(0.0f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot()
						.AutoHeight()
						.Padding(10.0f, 8.0f)
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Details", "DETAILS"))
							.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
							.ColorAndOpacity(FLinearColor(0.62f, 0.66f, 0.72f))
						]
						+ SVerticalBox::Slot()
						.AutoHeight()
						[
							SNew(SSeparator)
						]
						+ SVerticalBox::Slot()
						.FillHeight(1.0f)
						.Padding(10.0f, 8.0f)
						[
							SNew(SScrollBox)
							+ SScrollBox::Slot()
							[
								SAssignNew(InspectorBox, SVerticalBox)
							]
						]
					]
				]
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(2.0f, 8.0f, 2.0f, 0.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot()
				.FillWidth(1.0f)
				[
					SAssignNew(StatusText, STextBlock)
					.Text_Lambda([this]() { return FText::FromString(StatusMessage); })
					.ColorAndOpacity_Lambda([this]()
					{
						return bStatusError
							       ? FSlateColor(FLinearColor(0.95f, 0.28f, 0.22f))
							       : FSlateColor(FLinearColor(0.32f, 0.72f, 0.46f));
					})
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(8.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("RetryNativePublicationFinish", "Retry native finish"))
					.Visibility_Lambda([this]()
					{
						return PendingNativePublication.IsSet() && bPatchRequestPending
							       ? EVisibility::Visible
							       : EVisibility::Collapsed;
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleRetryNativePublicationFinish)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(8.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("RetryNativePublicationPrepare", "Retry native prepare"))
					.Visibility_Lambda([this]()
					{
						return bNativePublicationPrepareRecoveryPending &&
						       bNativePublicationReservationPending &&
						       !bPatchRequestPending
							       ? EVisibility::Visible
							       : EVisibility::Collapsed;
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleRetryNativePublicationPrepare)
				]
				+ SHorizontalBox::Slot()
				.AutoWidth()
				.Padding(8.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("RetryNativePublicationCancel", "Retry native cancel"))
					.Visibility_Lambda([this]()
					{
						return bNativePublicationCancelRecoveryPending &&
						       bNativePublicationReservationPending &&
						       !bPatchRequestPending
							       ? EVisibility::Visible
							       : EVisibility::Collapsed;
					})
					.OnClicked(this, &SFinareAuthoringSurface::HandleRetryNativePublicationCancel)
				]
			]
		]
	];

	RegisterActiveTimer(
		1.0f,
		FWidgetActiveTimerDelegate::CreateSP(
			this,
			&SFinareAuthoringSurface::HandleAutoReload));
	PathTextBox->SetText(FText::FromString(InitialSurfacePath));
	if (InitialSurfacePath.IsEmpty())
	{
		SetStatus(
			TEXT("Select a Finare Data Asset or browse to an Authoring Surface."),
			false);
		RebuildInspector(nullptr);
	}
	else if (!LoadSurface(InitialSurfacePath))
	{
		RebuildInspector(nullptr);
	}
}

SFinareAuthoringSurface::~SFinareAuthoringSurface()
{
	if (FModuleManager::Get().IsModuleLoaded(TEXT("FinareEditor")))
	{
		IFinareEditorModule::Get().OnDailyBuildCompleted().Remove(DailyBuildCompletedHandle);
	}
	PersistCanvasView(true);
	PersistCurrentLayout(true);
}

void SFinareAuthoringSurface::SetViewMode(
	EFinareAuthoringViewMode InViewMode)
{
	ViewMode = InViewMode;
}

FReply SFinareAuthoringSurface::HandleSetViewMode(
	EFinareAuthoringViewMode InViewMode)
{
	SetViewMode(InViewMode);
	if (InViewMode == EFinareAuthoringViewMode::Code &&
		!bHasCanonicalSourceSnapshot && !bPatchRequestPending)
	{
		RequestWorkspaceRefresh();
	}
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRefreshSourceSnapshot()
{
	RequestWorkspaceRefresh(true);
	return FReply::Handled();
}

void SFinareAuthoringSurface::HandleCodeTextChanged(const FText& Text)
{
	if (!bHasCanonicalSourceSnapshot)
	{
		return;
	}
	CodeEditBuffer = Text.ToString();
	bCodeEditDirty = !CodeEditBuffer.Equals(
		WorkbenchSourceText,
		ESearchCase::CaseSensitive);
}

FReply SFinareAuthoringSurface::HandleApplyCodeSource()
{
	if (!bHasCanonicalSourceSnapshot)
	{
		SetStatus(TEXT("There is no canonical C# snapshot to edit."), true);
		return FReply::Handled();
	}
	if (!bCodeEditDirty)
	{
		SetStatus(
			FString::Printf(
				TEXT("C# is already in sync with canonical revision %lld."),
				WorkbenchRevision),
			false);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringWorkbench::BuildSourceTextPatch(
		WorkbenchSourcePath,
		WorkbenchSourceText,
		CodeEditBuffer,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}

	SubmitAuthoringTransaction(
		MoveTemp(Patch),
		FFinareAuthoringWorkbench::BuildSourceTextUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		TEXT("Edit C# source"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRevertCodeSource()
{
	if (!bHasCanonicalSourceSnapshot)
	{
		SetStatus(TEXT("There is no canonical C# snapshot to restore."), true);
		return FReply::Handled();
	}
	CodeEditBuffer = WorkbenchSourceText;
	bCodeEditDirty = false;
	SetStatus(
		FString::Printf(
			TEXT("Restored the C# edit buffer to canonical revision %lld."),
			WorkbenchRevision),
		false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::OnPreviewKeyDown(
	const FGeometry& MyGeometry,
	const FKeyEvent& InKeyEvent)
{
	if (InKeyEvent.IsControlDown() && InKeyEvent.GetKey() == EKeys::S &&
		ViewMode == EFinareAuthoringViewMode::Code)
	{
		return HandleApplyCodeSource();
	}
	if (InKeyEvent.IsControlDown() && InKeyEvent.GetKey() == EKeys::SpaceBar &&
		NodePaletteSearchBox.IsValid())
	{
		FSlateApplication::Get().SetKeyboardFocus(
			NodePaletteSearchBox,
			EFocusCause::SetDirectly);
		NodePaletteSearchBox->SelectAllText();
		return FReply::Handled();
	}
	if (InKeyEvent.GetKey() == EKeys::Escape && NodePaletteSearchBox.IsValid() &&
		NodePaletteSearchBox->HasKeyboardFocus() && !NodePaletteSearchText.IsEmpty())
	{
		NodePaletteSearchBox->SetText(FText::GetEmpty());
		return FReply::Handled();
	}
	const bool bGraphFocused = GraphEditor.IsValid() &&
		(GraphEditor->HasKeyboardFocus() || GraphEditor->HasFocusedDescendants());
	if (bGraphFocused && !InKeyEvent.IsControlDown() && !InKeyEvent.IsAltDown() &&
		!InKeyEvent.IsShiftDown())
	{
		if (InKeyEvent.GetKey() == EKeys::C)
		{
			return HandleAddComment();
		}
		if (InKeyEvent.GetKey() == EKeys::F)
		{
			return GraphEditor->GetSelectedNodes().IsEmpty()
				       ? HandleFocusGraph()
				       : HandleFocusSelection();
		}
		if (InKeyEvent.GetKey() == EKeys::Home)
		{
			return HandleFocusGraph();
		}
	}
	return SCompoundWidget::OnPreviewKeyDown(MyGeometry, InKeyEvent);
}

bool SFinareAuthoringSurface::LoadSurface(const FString& ConfiguredPath)
{
	if (bCodeEditDirty)
	{
		SetStatus(
			TEXT("Revert or apply the unsaved C# edit before loading another Surface."),
			true);
		return false;
	}
	FFinareAuthoringSurfaceDocument Candidate;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::LoadFile(
		ConfiguredPath,
		Candidate,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return false;
	}

	PersistCanvasView(true);
	PersistCurrentLayout(true);
	WorkbenchSourcePath.Reset();
	WorkbenchSourceText.Reset();
	CodeEditBuffer.Reset();
	WorkbenchRevision = 0;
	bHasCanonicalSourceSnapshot = false;
	bCodeEditDirty = false;
	Document = MoveTemp(Candidate);
	LoadedSurfacePath = FFinareAuthoringSurfaceReader::ResolveConfiguredPath(ConfiguredPath);
	LoadedLayoutPath = FFinareGraphLayoutStore::GetLayoutPath(LoadedSurfacePath);
	LoadedSurfaceTimestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
	PathTextBox->SetText(FText::FromString(ConfiguredPath));
	RebuildTree();
	SetStatus(
		FString::Printf(
			TEXT("Loaded %s  |  compilation %s"),
			*LoadedSurfacePath,
			*Document.SourceCompilationId),
		false);
	return true;
}

bool SFinareAuthoringSurface::LoadScriptAsset(UFinareAsset* ScriptAsset)
{
	if (!IsValid(ScriptAsset))
	{
		SetStatus(TEXT("The requested Finare Data Asset is unavailable."), true);
		return false;
	}
	const FString SurfacePath = ScriptAsset->ResolveAuthoringSurfacePath();
	if (SurfacePath.IsEmpty())
	{
		SetStatus(
			FString::Printf(
				TEXT("Script Asset '%s' has no AuthoringSurfacePath."),
				*ScriptAsset->GetPathName()),
			true);
		return false;
	}
	if (!LoadSurface(SurfacePath))
	{
		return false;
	}
	LoadedScriptAsset = ScriptAsset;
	const FString ServerUrl = ScriptAsset->ResolveAuthoringServerUrl();
	if (!ServerUrl.IsEmpty())
	{
		ServerUrlTextBox->SetText(FText::FromString(ServerUrl));
	}
	// A project surface contains multiple classes. Opening one Script Asset
	// must not silently focus the first, potentially read-only, class instead.
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		if (Asset.AssetPath == ScriptAsset->DeclarationAssetPath && !Asset.Functions.IsEmpty())
		{
			SelectFunction(Asset.Functions[0].Id);
			break;
		}
	}
	return true;
}

FReply SFinareAuthoringSurface::HandleLoad()
{
	LoadedScriptAsset.Reset();
	LoadSurface(PathTextBox->GetText().ToString());
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleBrowse()
{
	IDesktopPlatform* DesktopPlatform = FDesktopPlatformModule::Get();
	if (DesktopPlatform == nullptr)
	{
		SetStatus(TEXT("Desktop file dialog is unavailable."), true);
		return FReply::Handled();
	}

	TArray<FString> Files;
	const FString InitialDirectory = LoadedSurfacePath.IsEmpty()
		                                 ? FPaths::ProjectDir()
		                                 : FPaths::GetPath(LoadedSurfacePath);
	if (DesktopPlatform->OpenFileDialog(
		FSlateApplication::Get().FindBestParentWindowHandleForDialogs(nullptr),
		TEXT("Open Finare Authoring Surface"),
		InitialDirectory,
		TEXT(""),
		TEXT("Finare Surface (*.surface.json)|*.surface.json|JSON (*.json)|*.json"),
		EFileDialogFlags::None,
		Files) && !Files.IsEmpty())
	{
		LoadedScriptAsset.Reset();
		LoadSurface(Files[0]);
	}
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleLoadSelectedAsset()
{
	TArray<FAssetData> SelectedAssets;
	FContentBrowserModule& ContentBrowser =
		FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
	ContentBrowser.Get().GetSelectedAssets(SelectedAssets);
	for (const FAssetData& AssetData : SelectedAssets)
	{
		if (UFinareAsset* Asset = Cast<UFinareAsset>(AssetData.GetAsset()))
		{
			LoadScriptAsset(Asset);
			return FReply::Handled();
		}
	}
	SetStatus(TEXT("Select a Finare Data Asset in the Content Browser first."), true);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleLoadInitialSurface()
{
	LoadedScriptAsset.Reset();
	LoadSurface(InitialSurfacePath);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleOpenSource(FFinareAuthoringSpan Span)
{
	const FString Path = ResolveSourcePath(Span.FilePath);
	if (Path.IsEmpty())
	{
		SetStatus(FString::Printf(TEXT("Source file cannot be resolved: '%s'."), *Span.FilePath), true);
		return FReply::Handled();
	}
	FSourceCodeNavigation::OpenSourceFile(Path, FMath::Max(1, Span.StartLine), FMath::Max(1, Span.StartColumn));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleCopyId(FString SemanticId)
{
	FPlatformApplicationMisc::ClipboardCopy(*SemanticId);
	SetStatus(FString::Printf(TEXT("Copied semantic ID %s"), *SemanticId), false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleCopyPatch(
	FFinareAuthoringEdit Edit,
	TSharedPtr<SEditableTextBox> ValueEditor)
{
	FString Patch;
	FString Error;
	const FString NewValue = ValueEditor.IsValid()
		                         ? ValueEditor->GetText().ToString()
		                         : FString();
	if (!FFinareAuthoringSurfaceReader::BuildSemanticPatch(
		Document,
		Edit,
		NewValue,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	FPlatformApplicationMisc::ClipboardCopy(*Patch);
	SetStatus(FString::Printf(TEXT("Copied semantic-patch v0.28 for %s"), *Edit.TargetNodeId), false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleApplyPatch(
	FFinareAuthoringEdit Edit,
	TSharedPtr<SEditableTextBox> ValueEditor)
{
	if (bPatchRequestPending)
	{
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	const FString NewValue = ValueEditor.IsValid()
		                         ? ValueEditor->GetText().ToString()
		                         : FString();
	if (!FFinareAuthoringSurfaceReader::BuildSemanticPatch(
		Document,
		Edit,
		NewValue,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}

	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Applying semantic patch to %s"), *Edit.TargetNodeId));
	return FReply::Handled();
}

void SFinareAuthoringSurface::HandleDataRewireRequested(
	const UEdGraphPin* OutputPin,
	const UEdGraphPin* InputPin)
{
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("A semantic patch is already in flight; wait for it to finish."), true);
		return;
	}
	const UFinareEdGraphNode* SourceNode = OutputPin != nullptr
		                                       ? Cast<UFinareEdGraphNode>(OutputPin->GetOwningNode())
		                                       : nullptr;
	const UFinareEdGraphNode* TargetNode = InputPin != nullptr
		                                       ? Cast<UFinareEdGraphNode>(InputPin->GetOwningNode())
		                                       : nullptr;
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (SourceNode == nullptr || TargetNode == nullptr || Function == nullptr)
	{
		SetStatus(TEXT("The active graph endpoints no longer match the loaded Surface."), true);
		return;
	}

	const FString TargetPort = InputPin->PinName.ToString();
	const FFinareAuthoringEdge* ExpectedEdge = nullptr;
	for (const FFinareAuthoringEdge& Edge : Function->Edges)
	{
		if (Edge.Kind == TEXT("data") && Edge.ToNode == TargetNode->SemanticId &&
			Edge.ToPort == TargetPort)
		{
			if (ExpectedEdge != nullptr)
			{
				SetStatus(TEXT("The selected input has multiple compiler-owned data edges."), true);
				return;
			}
			ExpectedEdge = &Edge;
		}
	}
	if (ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The selected input has no compiler-owned data edge to replace."), true);
		return;
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildDataEdgeReconnectionPatch(
		Document,
		*Function,
		*ExpectedEdge,
		SourceNode->SemanticId,
		OutputPin->PinName.ToString(),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}

	const bool bComputedSharedValue = OutputPin->LinkedTo.Num() > 0 &&
	(SourceNode->NodeKind == TEXT("binary") || SourceNode->NodeKind == TEXT("unary") ||
		SourceNode->NodeKind == TEXT("cast") || SourceNode->NodeKind == TEXT("conditional"));
	if (bComputedSharedValue)
	{
		FString Plan;
		FString PlanError;
		if (FFinareAuthoringSurfaceReader::BuildSharedDataEdgePatchPlan(
			Document,
			*Function,
			*ExpectedEdge,
			SourceNode->SemanticId,
			OutputPin->PinName.ToString(),
			Plan,
			PlanError))
		{
			SubmitAuthoringTransaction(
				MoveTemp(Plan),
				FFinareAuthoringWorkbench::BuildSemanticPatchPlanUrl(
					ServerUrlTextBox->GetText().ToString()),
				FString::Printf(
					TEXT("Sharing %s through an automatic local temporary"),
					*SourceNode->DisplayTitle));
			return;
		}
	}

	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(
			TEXT("Rewiring %s.%s -> %s.%s"),
			*SourceNode->DisplayTitle,
			*OutputPin->PinName.ToString(),
			*TargetNode->DisplayTitle,
			*TargetPort));
}

void SFinareAuthoringSurface::HandleDataDisconnectRequested(
	const UEdGraphPin* OutputPin,
	const UEdGraphPin* InputPin)
{
	if (bPatchRequestPending || OutputPin == nullptr || InputPin == nullptr)
	{
		return;
	}
	const UFinareEdGraphNode* TargetNode =
		Cast<UFinareEdGraphNode>(InputPin->GetOwningNode());
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (TargetNode == nullptr || Function == nullptr)
	{
		SetStatus(TEXT("The selected wire no longer belongs to the active Surface."), true);
		return;
	}
	const FFinareAuthoringEdge* ExpectedEdge = Function->Edges.FindByPredicate(
		[TargetNode, InputPin](const FFinareAuthoringEdge& Edge)
		{
			return Edge.Kind == TEXT("data") && Edge.ToNode == TargetNode->SemanticId &&
				Edge.ToPort == InputPin->PinName.ToString();
		});
	if (ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The selected data input has no compiler-owned edge."), true);
		return;
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildDataEdgeDisconnectionPatch(
		Document, *Function, *ExpectedEdge, Patch, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}
	SubmitSemanticPatch(MoveTemp(Patch), TEXT("Replacing the disconnected input with typed default(...)"));
}

void SFinareAuthoringSurface::HandleRerouteRequested(
	UEdGraphPin* PinA,
	UEdGraphPin* PinB,
	const FVector2D& Location)
{
	if (!AuthoringGraph.IsValid() || PinA == nullptr || PinB == nullptr ||
		!PinA->LinkedTo.Contains(PinB))
	{
		return;
	}
	UEdGraphPin* OutputPin = PinA->Direction == EGPD_Output ? PinA : PinB;
	UEdGraphPin* InputPin = PinA->Direction == EGPD_Input ? PinA : PinB;
	if (OutputPin->Direction != EGPD_Output || InputPin->Direction != EGPD_Input ||
		OutputPin->PinType.PinCategory != InputPin->PinType.PinCategory ||
		OutputPin->PinType.PinSubCategory != InputPin->PinType.PinSubCategory)
	{
		SetStatus(TEXT("A reroute requires one exact typed wire segment."), true);
		return;
	}

	const UFinareEdGraphRerouteNode* OutputReroute =
		Cast<UFinareEdGraphRerouteNode>(OutputPin->GetOwningNode());
	const UFinareEdGraphRerouteNode* InputReroute =
		Cast<UFinareEdGraphRerouteNode>(InputPin->GetOwningNode());
	FString EdgeId = OutputReroute != nullptr
		                 ? OutputReroute->EdgeId
		                 : InputReroute != nullptr
		                 ? InputReroute->EdgeId
		                 : FString();
	if (OutputReroute != nullptr && InputReroute != nullptr &&
		OutputReroute->EdgeId != InputReroute->EdgeId)
	{
		SetStatus(TEXT("The selected route segment no longer belongs to one semantic edge."), true);
		return;
	}
	if (EdgeId.IsEmpty())
	{
		const UFinareEdGraphNode* Source =
			Cast<UFinareEdGraphNode>(OutputPin->GetOwningNode());
		const UFinareEdGraphNode* Target =
			Cast<UFinareEdGraphNode>(InputPin->GetOwningNode());
		const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
		const FFinareAuthoringEdge* Edge =
			Source != nullptr && Target != nullptr && Function != nullptr
				? Function->Edges.FindByPredicate(
					[Source, Target, OutputPin, InputPin](
					const FFinareAuthoringEdge& Candidate)
					{
						return Candidate.FromNode == Source->SemanticId &&
							Candidate.FromPort == OutputPin->PinName.ToString() &&
							Candidate.ToNode == Target->SemanticId &&
							Candidate.ToPort == InputPin->PinName.ToString();
					})
				: nullptr;
		EdgeId = Edge != nullptr ? Edge->Id : FString();
	}
	if (EdgeId.IsEmpty())
	{
		SetStatus(TEXT("The selected segment has no compiler-owned semantic edge identity."), true);
		return;
	}

	int32 InsertIndex = OutputReroute != nullptr ? OutputReroute->PointIndex + 1 : 0;
	if (InputReroute != nullptr)
	{
		InsertIndex = OutputReroute != nullptr
			              ? FMath::Min(InsertIndex, InputReroute->PointIndex)
			              : InputReroute->PointIndex;
	}
	for (UEdGraphNode* Node : AuthoringGraph->Nodes)
	{
		UFinareEdGraphRerouteNode* Existing =
			Cast<UFinareEdGraphRerouteNode>(Node);
		if (Existing != nullptr && Existing->EdgeId == EdgeId &&
			Existing->PointIndex >= InsertIndex)
		{
			++Existing->PointIndex;
		}
	}

	UFinareEdGraphRerouteNode* Reroute =
		NewObject<UFinareEdGraphRerouteNode>(
			AuthoringGraph.Get(), NAME_None, RF_Transient);
	Reroute->CreateNewGuid();
	Reroute->EdgeId = EdgeId;
	Reroute->PointIndex = InsertIndex;
	Reroute->RoutedPinCategory = OutputPin->PinType.PinCategory;
	Reroute->RoutedPinSubCategory = OutputPin->PinType.PinSubCategory;
	Reroute->NodePosX = FMath::RoundToInt(Location.X);
	Reroute->NodePosY = FMath::RoundToInt(Location.Y);
	Reroute->AllocateDefaultPins();
	AuthoringGraph->AddNode(Reroute, true, false);

	OutputPin->BreakLinkTo(InputPin);
	OutputPin->MakeLinkTo(Reroute->FindPinChecked(TEXT("in"), EGPD_Input), false);
	Reroute->FindPinChecked(TEXT("out"), EGPD_Output)->MakeLinkTo(InputPin, false);
	AuthoringGraph->NotifyGraphChanged();
	GraphEditor->NotifyGraphChanged();
	PersistCurrentLayout(false);
	SetStatus(TEXT("Added a .usgraph-only reroute. Double-click any wire segment to add another."), false);
}

FReply SFinareAuthoringSurface::HandleAddComment()
{
	if (!AuthoringGraph.IsValid() || !GraphEditor.IsValid() || ActiveFunctionId.IsEmpty())
	{
		return FReply::Handled();
	}

	FVector2D Position(GraphEditor->GetPasteLocation2f());
	FVector2D Size(420.0, 220.0);
	FSlateRect SelectionBounds;
	if (GraphEditor->GetBoundsForSelectedNodes(SelectionBounds, 45.0f))
	{
		Position = FVector2D(SelectionBounds.Left, SelectionBounds.Top);
		Size = FVector2D(
			FMath::Max(220.0f, SelectionBounds.Right - SelectionBounds.Left),
			FMath::Max(120.0f, SelectionBounds.Bottom - SelectionBounds.Top));
	}

	UFinareEdGraphCommentNode* Comment =
		NewObject<UFinareEdGraphCommentNode>(
			AuthoringGraph.Get(), NAME_None, RF_Transient);
	Comment->CreateNewGuid();
	Comment->LayoutId = TEXT("comment_") +
		FGuid::NewGuid().ToString(EGuidFormats::Digits).ToLower();
	Comment->NodeComment = TEXT("Comment");
	Comment->NodePosX = FMath::RoundToInt(Position.X);
	Comment->NodePosY = FMath::RoundToInt(Position.Y);
	Comment->NodeWidth = FMath::RoundToInt(Size.X);
	Comment->NodeHeight = FMath::RoundToInt(Size.Y);
	Comment->MoveMode = ECommentBoxMode::GroupMovement;
	Comment->CommentColor = FLinearColor(0.10f, 0.24f, 0.42f, 0.35f);
	for (UObject* Selected : GraphEditor->GetSelectedNodes())
	{
		if (Cast<UFinareEdGraphNode>(Selected) != nullptr)
		{
			Comment->AddNodeUnderComment(Selected);
		}
	}
	AuthoringGraph->AddNode(Comment, true, false);
	AuthoringGraph->NotifyGraphChanged();
	GraphEditor->NotifyGraphChanged();
	GraphEditor->ClearSelectionSet();
	GraphEditor->SetNodeSelection(Comment, true);
	GraphEditor->IsNodeTitleVisible(Comment, true);
	PersistCurrentLayout(false);
	SetStatus(TEXT("Added a source-independent comment region to the .usgraph sidecar."), false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleApplyDataInputValue(
	FString TargetActionId,
	FString TargetPort,
	TSharedPtr<SEditableTextBox> ValueEditor)
{
	if (bPatchRequestPending || !ValueEditor.IsValid())
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringEdge* ExpectedEdge = Function != nullptr
		                                           ? Function->Edges.FindByPredicate(
			                                           [&TargetActionId, &TargetPort](const FFinareAuthoringEdge& Edge)
			                                           {
				                                           return Edge.Kind == TEXT("data") && Edge.ToNode ==
					                                           TargetActionId &&
					                                           Edge.ToPort == TargetPort;
			                                           })
		                                           : nullptr;
	if (Function == nullptr || ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The selected input no longer has its compiler-owned data edge."), true);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	const FString ValueText = ValueEditor->GetText().ToString();
	if (!FFinareAuthoringSurfaceReader::BuildDataInputValuePatch(
		Document, *Function, *ExpectedEdge, ValueText, Patch, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Setting %s.%s = %s"), *TargetActionId, *TargetPort, *ValueText));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleResetDataInputValue(
	FString TargetActionId,
	FString TargetPort)
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringEdge* ExpectedEdge =
		!bPatchRequestPending && Function != nullptr
			? Function->Edges.FindByPredicate(
				[&TargetActionId, &TargetPort](const FFinareAuthoringEdge& Edge)
				{
					return Edge.Kind == TEXT("data") && Edge.ToNode == TargetActionId &&
						Edge.ToPort == TargetPort;
				})
			: nullptr;
	if (ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The selected input no longer has its compiler-owned data edge."), true);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildDataInputValuePatch(
		Document, *Function, *ExpectedEdge, TEXT("default"), Patch, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Resetting %s.%s to its typed default"), *TargetActionId, *TargetPort));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandlePromoteDataInput(
	FString TargetActionId,
	FString TargetPort,
	TSharedPtr<SEditableTextBox> NameEditor)
{
	if (bPatchRequestPending || !NameEditor.IsValid())
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringEdge* ExpectedEdge = Function != nullptr
		                                           ? Function->Edges.FindByPredicate(
			                                           [&TargetActionId, &TargetPort](const FFinareAuthoringEdge& Edge)
			                                           {
				                                           return Edge.Kind == TEXT("data") && Edge.ToNode ==
					                                           TargetActionId &&
					                                           Edge.ToPort == TargetPort;
			                                           })
		                                           : nullptr;
	if (Function == nullptr || ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The selected input no longer has its compiler-owned data edge."), true);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	const FString LocalName = NameEditor->GetText().ToString();
	if (!FFinareAuthoringSurfaceReader::BuildDataInputPromotionPatch(
		Document, *Function, *ExpectedEdge, LocalName, Patch, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Promoting %s.%s to local '%s'"), *TargetActionId, *TargetPort, *LocalName));
	return FReply::Handled();
}

void SFinareAuthoringSurface::HandleControlRewireRequested(
	const UEdGraphPin* OutputPin,
	const UEdGraphPin* InputPin)
{
	if (bPatchRequestPending || OutputPin == nullptr || InputPin == nullptr)
	{
		return;
	}
	const UFinareEdGraphNode* SourceNode =
		Cast<UFinareEdGraphNode>(OutputPin->GetOwningNode());
	const UFinareEdGraphNode* TargetNode =
		Cast<UFinareEdGraphNode>(InputPin->GetOwningNode());
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (SourceNode == nullptr || TargetNode == nullptr || Function == nullptr)
	{
		SetStatus(TEXT("The control endpoints no longer belong to the active Surface."), true);
		return;
	}
	const FFinareAuthoringEdge* ExpectedEdge = Function->Edges.FindByPredicate(
		[TargetNode, InputPin](const FFinareAuthoringEdge& Edge)
		{
			return Edge.Kind == TEXT("control") && Edge.ToNode == TargetNode->SemanticId &&
				Edge.ToPort == InputPin->PinName.ToString();
		});
	if (ExpectedEdge == nullptr)
	{
		SetStatus(TEXT("The target has no unique compiler-owned control edge to transform."), true);
		return;
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildControlEdgeReconnectionPatch(
		Document,
		*Function,
		*ExpectedEdge,
		SourceNode->SemanticId,
		OutputPin->PinName.ToString(),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(
			TEXT("Moving %s after %s through structured control flow"),
			*TargetNode->DisplayTitle,
			*SourceNode->DisplayTitle));
}

void SFinareAuthoringSurface::HandleNodeCreateRequested(
	const FString& ActionId,
	const FVector2D& Location)
{
	if (bPatchRequestPending)
	{
		return;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (Function == nullptr)
	{
		SetStatus(TEXT("Select a function before creating a graph node."), true);
		return;
	}

	FString Patch;
	FString Error;
	bool bBuilt = false;
	if (const FFinareReflectedPaletteAction* Reflected =
		ReflectedPaletteActions.Find(ActionId))
	{
		if (Reflected->bEvent)
		{
			const FFinareAuthoringAsset* Asset = Document.Assets.FindByPredicate(
				[Function](const FFinareAuthoringAsset& Candidate)
				{
					return Candidate.Functions.ContainsByPredicate(
						[Function](const FFinareAuthoringFunction& CandidateFunction)
						{
							return CandidateFunction.Id == Function->Id;
						});
				});
			if (Asset == nullptr)
			{
				SetStatus(TEXT("The active function has no owning Script Asset."), true);
				return;
			}
			bBuilt = FFinareAuthoringSurfaceReader::BuildUnrealEventHandlerInsertionPatch(
				Document,
				*Asset,
				Reflected->HandlerName,
				Reflected->SourceTypeName,
				Reflected->EventPath,
				Reflected->EventParameters,
				Patch,
				Error);
			if (!bBuilt)
			{
				SetStatus(MoveTemp(Error), true);
				return;
			}
			PendingNodePaletteActionId = ActionId;
			SubmitAuthoringTransaction(
				MoveTemp(Patch),
				FFinareAuthoringWorkbench::BuildDeclarationPatchUrl(
					ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
				FString::Printf(TEXT("Creating UE Event Entry '%s'"), *Reflected->HandlerName));
			if (!bPatchRequestPending)
			{
				PendingNodePaletteActionId.Reset();
			}
			return;
		}

		const FFinareAuthoringRegion* Block = ResolveActiveBlockRegion(*Function);
		if (Block == nullptr)
		{
			SetStatus(TEXT("Select a statement inside an editable block first."), true);
			return;
		}
		if (Reflected->bInvocation)
		{
			bBuilt = FFinareAuthoringSurfaceReader::BuildMemberInvocationInsertionPatch(
				Document,
				*Function,
				*Block,
				Reflected->Receiver,
				Reflected->MemberName,
				Reflected->ResultTypeName,
				Reflected->ResultTypeId,
				Reflected->Arguments,
				Patch,
				Error);
		}
		else
		{
			bBuilt = FFinareAuthoringSurfaceReader::BuildMemberAccessInsertionPatch(
				Document,
				*Function,
				*Block,
				Reflected->Receiver,
				Reflected->MemberName,
				Reflected->ResultTypeName,
				Reflected->ResultTypeId,
				Patch,
				Error);
		}
	}
	else
	{
		const FFinareAuthoringRegion* Block = ResolveActiveBlockRegion(*Function);
		if (Block == nullptr)
		{
			SetStatus(TEXT("Select a statement inside an editable block first."), true);
			return;
		}
		bBuilt = FFinareAuthoringSurfaceReader::BuildBasicNodeInsertionPatch(
			Document, *Function, *Block, ActionId, Patch, Error);
	}
	if (!bBuilt)
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}
	PendingNodePaletteActionId = ActionId;
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Creating graph node '%s'"), *ActionId));
	if (!bPatchRequestPending)
	{
		PendingNodePaletteActionId.Reset();
	}
}

void SFinareAuthoringSurface::HandleStatementDeleteRequested(const UEdGraphNode* Node)
{
	if (bPatchRequestPending || Node == nullptr)
	{
		return;
	}
	const UFinareEdGraphNode* ScriptNode = Cast<UFinareEdGraphNode>(Node);
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* Statement = ScriptNode != nullptr && Function != nullptr
		                                          ? Function->FindAction(ScriptNode->SemanticId)
		                                          : nullptr;
	if (Function == nullptr || Statement == nullptr)
	{
		SetStatus(TEXT("The selected node no longer belongs to the active Surface."), true);
		return;
	}
	if (Statement->GraphKind == TEXT("switchCase"))
	{
		HandleDeleteSwitchSection(Statement->Id);
		return;
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildStatementDeletionPatch(
		Document, *Function, *Statement, Patch, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}
	SubmitSemanticPatch(MoveTemp(Patch), FString::Printf(TEXT("Deleting %s"), *Statement->Title));
}

const FFinareAuthoringAction*
SFinareAuthoringSurface::GetSingleSelectedStatementAction() const
{
	if (!GraphEditor.IsValid())
	{
		return nullptr;
	}
	const FGraphPanelSelectionSet Selection = GraphEditor->GetSelectedNodes();
	if (Selection.Num() != 1)
	{
		return nullptr;
	}

	const UFinareEdGraphNode* SelectedNode = nullptr;
	for (UObject* Selected : Selection)
	{
		SelectedNode = Cast<UFinareEdGraphNode>(Selected);
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* Action =
		SelectedNode != nullptr && SelectedNode->bCanDelete && Function != nullptr
			? Function->FindAction(SelectedNode->SemanticId)
			: nullptr;
	return Action != nullptr &&
	       UE::Finare::AuthoringSurface::Slate::Private::IsStatementCopyCandidate(*Action)
		       ? Action
		       : nullptr;
}

bool SFinareAuthoringSurface::CanCopySelectedStatement() const
{
	if (bPatchRequestPending)
	{
		return false;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const TArray<FString> StatementIds = GetSelectedStatementActionIds();
	const FFinareAuthoringAction* Destination =
		Function != nullptr && !StatementIds.IsEmpty()
			? Function->FindAction(StatementIds[0])
			: nullptr;
	FString IgnoredPatch;
	FString IgnoredError;
	return Function != nullptr && Destination != nullptr &&
		FFinareAuthoringSurfaceReader::BuildStatementRangeDuplicationPatch(
			Document,
			*Function,
			StatementIds,
			*Destination,
			UE::Finare::AuthoringSurface::Slate::Private::SuggestCopiedLocalNames(
				*Function,
				StatementIds),
			IgnoredPatch,
			IgnoredError);
}

void SFinareAuthoringSurface::HandleCopySelectedStatement()
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	TArray<FString> StatementIds = GetSelectedStatementActionIds();
	const FFinareAuthoringAction* Destination =
		Function != nullptr && !StatementIds.IsEmpty()
			? Function->FindAction(StatementIds[0])
			: nullptr;
	FString IgnoredPatch;
	FString Error;
	if (Function == nullptr || Destination == nullptr ||
		!FFinareAuthoringSurfaceReader::BuildStatementRangeDuplicationPatch(
			Document,
			*Function,
			StatementIds,
			*Destination,
			UE::Finare::AuthoringSurface::Slate::Private::SuggestCopiedLocalNames(
				*Function,
				StatementIds),
			IgnoredPatch,
			Error))
	{
		SetStatus(
			Error.IsEmpty()
				? TEXT("Select one consecutive range of non-terminal statements to copy.")
				: MoveTemp(Error),
			true);
		return;
	}
	StatementIds.Sort([Function](const FString& Left, const FString& Right)
	{
		const FFinareAuthoringAction* LeftAction = Function->FindAction(Left);
		const FFinareAuthoringAction* RightAction = Function->FindAction(Right);
		if (LeftAction == nullptr || RightAction == nullptr)
		{
			return Left < Right;
		}
		if (LeftAction->Span.StartLine != RightAction->Span.StartLine)
		{
			return LeftAction->Span.StartLine < RightAction->Span.StartLine;
		}
		if (LeftAction->Span.StartColumn != RightAction->Span.StartColumn)
		{
			return LeftAction->Span.StartColumn < RightAction->Span.StartColumn;
		}
		return Left < Right;
	});
	CopiedStatementIds = MoveTemp(StatementIds);
	CopiedStatementFunctionId = ActiveFunctionId;
	SetStatus(
		FString::Printf(
			TEXT("Copied %d semantic statement%s; select any ordinary statement in this function and paste after it."),
			CopiedStatementIds.Num(),
			CopiedStatementIds.Num() == 1 ? TEXT("") : TEXT("s")),
		false);
}

bool SFinareAuthoringSurface::CanPasteStatement() const
{
	if (bPatchRequestPending || CopiedStatementIds.IsEmpty() ||
		CopiedStatementFunctionId != ActiveFunctionId)
	{
		return false;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (Function == nullptr || !GraphEditor.IsValid())
	{
		return false;
	}
	for (const FString& ActionId : CopiedStatementIds)
	{
		const FFinareAuthoringAction* Source = Function->FindAction(ActionId);
		if (Source == nullptr ||
			!UE::Finare::AuthoringSurface::Slate::Private::IsStatementCopyCandidate(*Source))
		{
			return false;
		}
	}
	const int32 SelectionCount = GraphEditor->GetSelectedNodes().Num();
	return SelectionCount == 0 ||
		(SelectionCount == 1 && GetSingleSelectedStatementAction() != nullptr);
}

void SFinareAuthoringSurface::HandlePasteStatement()
{
	if (!CanPasteStatement())
	{
		SetStatus(
			TEXT(
				"Copy a consecutive statement range first, then select zero or one non-terminal destination statement in the same function."),
			true);
		return;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* FallbackDestination = Function != nullptr &&
	                                                    !CopiedStatementIds.IsEmpty()
		                                                    ? Function->FindAction(CopiedStatementIds.Last())
		                                                    : nullptr;
	const FFinareAuthoringAction* SelectedDestination =
		GetSingleSelectedStatementAction();
	const FFinareAuthoringAction* Destination = SelectedDestination != nullptr
		                                            ? SelectedDestination
		                                            : FallbackDestination;
	if (Function == nullptr || Destination == nullptr)
	{
		SetStatus(TEXT("The copied statement range no longer exists in the active Surface."), true);
		return;
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildStatementRangeDuplicationPatch(
		Document,
		*Function,
		CopiedStatementIds,
		*Destination,
		UE::Finare::AuthoringSurface::Slate::Private::SuggestCopiedLocalNames(
			*Function,
			CopiedStatementIds),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return;
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(
			TEXT("Pasting a semantic copy of %d statement%s"),
			CopiedStatementIds.Num(),
			CopiedStatementIds.Num() == 1 ? TEXT("") : TEXT("s")));
}

void SFinareAuthoringSurface::HandleUndoAuthoringCommand()
{
	HandleUndoAuthoringTransaction();
}

bool SFinareAuthoringSurface::CanUndoAuthoringCommand() const
{
	return !bPatchRequestPending && ActiveAuthoringGroupId.IsEmpty() && bCanUndoAuthoring;
}

void SFinareAuthoringSurface::HandleRedoAuthoringCommand()
{
	HandleRedoAuthoringTransaction();
}

bool SFinareAuthoringSurface::CanRedoAuthoringCommand() const
{
	return !bPatchRequestPending && ActiveAuthoringGroupId.IsEmpty() && bCanRedoAuthoring;
}

TArray<FString> SFinareAuthoringSurface::GetSelectedStatementActionIds() const
{
	TArray<FString> Result;
	if (!GraphEditor.IsValid())
	{
		return Result;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (Function == nullptr)
	{
		return Result;
	}
	for (UObject* Selected : GraphEditor->GetSelectedNodes())
	{
		const UFinareEdGraphNode* Node = Cast<UFinareEdGraphNode>(Selected);
		const FFinareAuthoringAction* Action =
			Node != nullptr && Node->bCanDelete
				? Function->FindAction(Node->SemanticId)
				: nullptr;
		if (Action == nullptr || Action->GraphKind == TEXT("switchCase"))
		{
			Result.Reset();
			return Result;
		}
		Result.Add(Action->Id);
	}
	return Result;
}

bool SFinareAuthoringSurface::CanExtractSelectedStatements() const
{
	if (bPatchRequestPending || !ExtractionNameTextBox.IsValid())
	{
		return false;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const TArray<FString> StatementIds = GetSelectedStatementActionIds();
	FString IgnoredPatch;
	FString IgnoredError;
	return Function != nullptr && !StatementIds.IsEmpty() &&
		FFinareAuthoringSurfaceReader::BuildCallableExtractionPatch(
			Document,
			*Function,
			StatementIds,
			ExtractionNameTextBox->GetText().ToString(),
			IgnoredPatch,
			IgnoredError);
}

FReply SFinareAuthoringSurface::HandleExtractSelectedStatements()
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const TArray<FString> StatementIds = GetSelectedStatementActionIds();
	const FString MethodName = ExtractionNameTextBox.IsValid()
		                           ? ExtractionNameTextBox->GetText().ToString()
		                           : FString();
	FString Patch;
	FString Error;
	if (Function == nullptr ||
		!FFinareAuthoringSurfaceReader::BuildCallableExtractionPatch(
			Document,
			*Function,
			StatementIds,
			MethodName,
			Patch,
			Error))
	{
		SetStatus(
			Error.IsEmpty()
				? TEXT("Select one consecutive statement range and enter a valid new method name.")
				: MoveTemp(Error),
			true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(
			TEXT("Extracting %d statement%s into %s"),
			StatementIds.Num(),
			StatementIds.Num() == 1 ? TEXT("") : TEXT("s"),
			*MethodName));
	return FReply::Handled();
}

bool SFinareAuthoringSurface::CanCollapseSelectedStatements() const
{
	if (bPatchRequestPending)
	{
		return false;
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const TArray<FString> StatementIds = GetSelectedStatementActionIds();
	FString IgnoredPatch;
	FString IgnoredError;
	return Function != nullptr && StatementIds.Num() >= 2 &&
		FFinareAuthoringSurfaceReader::BuildStatementCollapsePatch(
			Document,
			*Function,
			StatementIds,
			IgnoredPatch,
			IgnoredError);
}

FReply SFinareAuthoringSurface::HandleCollapseSelectedStatements()
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const TArray<FString> StatementIds = GetSelectedStatementActionIds();
	FString Patch;
	FString Error;
	if (Function == nullptr ||
		!FFinareAuthoringSurfaceReader::BuildStatementCollapsePatch(
			Document,
			*Function,
			StatementIds,
			Patch,
			Error))
	{
		SetStatus(
			Error.IsEmpty()
				? TEXT("Select two or more consecutive statement nodes in one block.")
				: MoveTemp(Error),
			true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(MoveTemp(Patch), TEXT("Collapsing statements into a lexical scope"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleExpandBlockRegion(FString RegionId)
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringRegion* Region = Function != nullptr
		                                       ? Function->FindRegion(RegionId)
		                                       : nullptr;
	FString Patch;
	FString Error;
	if (Function == nullptr || Region == nullptr ||
		!FFinareAuthoringSurfaceReader::BuildBlockExpansionPatch(
			Document,
			*Function,
			*Region,
			Patch,
			Error))
	{
		SetStatus(
			Error.IsEmpty() ? TEXT("The selected lexical scope can no longer be expanded.") : MoveTemp(Error),
			true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(MoveTemp(Patch), TEXT("Expanding a lexical scope"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleAddSwitchSection(
	FString SwitchActionId,
	TSharedPtr<SEditableTextBox> LabelEditor,
	bool bDefault)
{
	if (bPatchRequestPending)
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* SwitchStatement = Function != nullptr
		                                                ? Function->FindAction(SwitchActionId)
		                                                : nullptr;
	if (Function == nullptr || SwitchStatement == nullptr)
	{
		SetStatus(TEXT("The selected switch no longer belongs to the active Surface."), true);
		return FReply::Handled();
	}

	const FString LabelText = bDefault
		                          ? FString(TEXT("default"))
		                          : LabelEditor.IsValid()
		                          ? LabelEditor->GetText().ToString()
		                          : FString();
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildSwitchSectionInsertionPatch(
		Document,
		*Function,
		*SwitchStatement,
		LabelText,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Adding switch section '%s'"), *LabelText));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleReplaceSwitchSection(
	FString CaseActionId,
	TSharedPtr<SEditableTextBox> LabelEditor)
{
	if (bPatchRequestPending)
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* CaseLabel = Function != nullptr
		                                          ? Function->FindAction(CaseActionId)
		                                          : nullptr;
	if (Function == nullptr || CaseLabel == nullptr || !LabelEditor.IsValid())
	{
		SetStatus(TEXT("The selected case label no longer belongs to the active Surface."), true);
		return FReply::Handled();
	}

	const FString LabelText = LabelEditor->GetText().ToString();
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildSwitchSectionReplacementPatch(
		Document,
		*Function,
		*CaseLabel,
		LabelText,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Replacing switch section label with '%s'"), *LabelText));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleMoveSwitchSection(
	FString CaseActionId,
	int32 Direction)
{
	if (bPatchRequestPending)
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* CaseLabel = Function != nullptr
		                                          ? Function->FindAction(CaseActionId)
		                                          : nullptr;
	if (Function == nullptr || CaseLabel == nullptr)
	{
		SetStatus(TEXT("The selected case label no longer belongs to the active Surface."), true);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildSwitchSectionMovePatch(
		Document,
		*Function,
		*CaseLabel,
		Direction,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(
		MoveTemp(Patch),
		Direction < 0 ? TEXT("Moving switch section up") : TEXT("Moving switch section down"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleDeleteSwitchSection(FString CaseActionId)
{
	if (bPatchRequestPending)
	{
		return FReply::Handled();
	}
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* CaseLabel = Function != nullptr
		                                          ? Function->FindAction(CaseActionId)
		                                          : nullptr;
	if (Function == nullptr || CaseLabel == nullptr)
	{
		SetStatus(TEXT("The selected case label no longer belongs to the active Surface."), true);
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildSwitchSectionDeletionPatch(
		Document,
		*Function,
		*CaseLabel,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitSemanticPatch(MoveTemp(Patch), TEXT("Deleting switch section"));
	return FReply::Handled();
}

void SFinareAuthoringSurface::SubmitSemanticPatch(FString Patch, FString Activity)
{
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("A semantic patch is already in flight; wait for it to finish."), true);
		return;
	}

	const FString Url = FFinareAuthoringWorkbench::BuildSemanticPatchUrl(
		ServerUrlTextBox->GetText().ToString());
	SubmitAuthoringTransaction(MoveTemp(Patch), Url, MoveTemp(Activity));
}

void SFinareAuthoringSurface::SubmitDeclarationPatch(FString Patch, FString Activity)
{
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("A declaration patch is already in flight; wait for it to finish."), true);
		return;
	}
	const FString Url = FFinareAuthoringWorkbench::BuildDeclarationPatchUrl(
		ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString());
	SubmitAuthoringTransaction(MoveTemp(Patch), Url, MoveTemp(Activity));
}

FReply SFinareAuthoringSurface::HandleCreateScriptAsset()
{
	const FString TargetSourcePath = !WorkbenchSourcePath.IsEmpty()
		                                 ? WorkbenchSourcePath
		                                 : (!Document.Assets.IsEmpty() ? Document.Assets[0].Span.FilePath : FString());
	if (bPatchRequestPending || TargetSourcePath.IsEmpty())
	{
		return FReply::Handled();
	}
	FString SuggestedNamespace;
	if (!Document.Assets.IsEmpty())
	{
		FString ExistingType;
		Document.Assets[0].QualifiedName.Split(
			TEXT("."),
			&SuggestedNamespace,
			&ExistingType,
			ESearchCase::CaseSensitive,
			ESearchDir::FromEnd);
	}
	TSharedPtr<SEditableTextBox> NamespaceEditor;
	TSharedPtr<SEditableTextBox> TypeEditor;
	TSharedPtr<SEditableTextBox> AssetPathEditor;
	TSharedPtr<SCheckBox> LifecycleCheck;
	bool bAccepted = false;
	const TSharedRef<SWindow> Window = SNew(SWindow)
		.Title(LOCTEXT("CreateScriptAssetTitle", "Create Finare Asset"))
		.ClientSize(FVector2D(560.0f, 330.0f))
		.SupportsMaximize(false)
		.SupportsMinimize(false);
	Window->SetContent(
		SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
		.Padding(14.0f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock)
				.Text(FText::FromString(FString::Printf(
					TEXT("C# source: %s"),
					*TargetSourcePath)))
				.AutoWrapText(true)
				.ColorAndOpacity(FLinearColor(0.60f, 0.64f, 0.70f))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 10.0f, 0.0f, 2.0f)
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
					TEXT("NAMESPACE"))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(NamespaceEditor, SEditableTextBox)
				.Text(FText::FromString(SuggestedNamespace))
				.HintText(LOCTEXT("ScriptAssetNamespaceHint", "Game.Scripts"))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 8.0f, 0.0f, 2.0f)
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
					TEXT("C# TYPE"))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(TypeEditor, SEditableTextBox)
				.Text(LOCTEXT("DefaultScriptAssetType", "NewActorScript"))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 8.0f, 0.0f, 2.0f)
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
					TEXT("ASSET PATH"))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(AssetPathEditor, SEditableTextBox)
				.Text(LOCTEXT("DefaultScriptAssetPath", "Gameplay/NewActorScript"))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 10.0f, 0.0f, 0.0f)
			[
				SAssignNew(LifecycleCheck, SCheckBox)
				.IsChecked(ECheckBoxState::Checked)
				[
					SNew(STextBlock)
					.Text(LOCTEXT(
						"CreateActorLifecycle",
						"Actor behavior · create BeginPlay, Tick, and EndPlay graphs"))
				]
			]
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right).Padding(0.0f, 14.0f, 0.0f, 0.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.Text(LOCTEXT("CancelCreateScriptAsset", "Cancel"))
					.OnClicked_Lambda([Window]()
					{
						Window->RequestDestroyWindow();
						return FReply::Handled();
					})
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(8.0f, 0.0f, 0.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("PreviewCreateScriptAsset", "Preview Create"))
					.OnClicked_Lambda([Window, &bAccepted]()
					{
						bAccepted = true;
						Window->RequestDestroyWindow();
						return FReply::Handled();
					})
				]
			]
		]);
	FSlateApplication::Get().AddModalWindow(Window, FSlateApplication::Get().GetActiveTopLevelWindow());
	if (!bAccepted)
	{
		return FReply::Handled();
	}

	FString Patch;
	FString Error;
	const FString TypeName = TypeEditor->GetText().ToString().TrimStartAndEnd();
	if (!FFinareAuthoringSurfaceReader::BuildScriptAssetCreationPatch(
		Document,
		TargetSourcePath,
		NamespaceEditor->GetText().ToString().TrimStartAndEnd(),
		TypeName,
		AssetPathEditor->GetText().ToString().TrimStartAndEnd(),
		LifecycleCheck->IsChecked(),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Creating Script Asset %s"), *TypeName));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleUpdateScriptAssetPath(
	FString AssetId,
	TSharedPtr<SEditableTextBox> PathEditor)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	if (Asset == nullptr || !PathEditor.IsValid())
	{
		SetStatus(TEXT("The selected Script Asset is no longer available."), true);
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildScriptAssetPathUpdatePatch(
		Document,
		*Asset,
		PathEditor->GetText().ToString().TrimStartAndEnd(),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Changing Script Asset path %s"), *Asset->AssetPath));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleUpdateScriptClassBase(
	FString AssetId,
	FFinareClassBindingDeclaration ExpectedClassBinding,
	TSharedPtr<SEditableTextBox> BaseClassEditor)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	if (Asset == nullptr || !BaseClassEditor.IsValid())
	{
		SetStatus(TEXT("The selected Script Class is no longer available."), true);
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildScriptClassBasePatch(
		Document,
		*Asset,
		ExpectedClassBinding,
		BaseClassEditor->GetText().ToString().TrimStartAndEnd(),
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Changing native base class for %s"), *Asset->QualifiedName));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRemoveScriptAsset(FString AssetId)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	if (Asset == nullptr)
	{
		SetStatus(TEXT("The selected Script Asset is no longer available."), true);
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildScriptAssetRemovalPatch(
		Document,
		*Asset,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Removing Script Asset %s"), *Asset->AssetPath));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleAddState(FString AssetId)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	UE::Finare::AuthoringSurface::Slate::Private::FStateAuthoringDialogResult Result;
	if (Asset == nullptr ||
		!UE::Finare::AuthoringSurface::Slate::Private::ShowStateAuthoringDialog(
			nullptr,
			Result))
	{
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildStateAdditionPatch(
		Document,
		*Asset,
		Result.Name,
		Result.TypeName,
		Result.TypeKind,
		Result.DefaultValue,
		Result.Exposure,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Adding state %s"), *Result.Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleEditState(FString StateId)
{
	const FFinareAuthoringState* State = FindState(StateId);
	UE::Finare::AuthoringSurface::Slate::Private::FStateAuthoringDialogResult Result;
	if (State == nullptr ||
		!UE::Finare::AuthoringSurface::Slate::Private::ShowStateAuthoringDialog(
			State,
			Result))
	{
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildStateUpdatePatch(
		Document,
		*State,
		Result.Name,
		Result.TypeName,
		Result.TypeKind,
		Result.DefaultValue,
		Result.Exposure,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Editing state %s"), *State->Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRemoveState(FString StateId)
{
	const FFinareAuthoringState* State = FindState(StateId);
	if (State == nullptr)
	{
		SetStatus(TEXT("The selected state is no longer available."), true);
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildStateRemovalPatch(
		Document,
		*State,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Removing state %s"), *State->Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleAddFunction(FString AssetId)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	UE::Finare::AuthoringSurface::Slate::Private::FFunctionAuthoringDialogResult Result;
	if (Asset == nullptr ||
		!UE::Finare::AuthoringSurface::Slate::Private::ShowFunctionAuthoringDialog(
			nullptr,
			Result))
	{
		return FReply::Handled();
	}
	TArray<FFinareAuthoringParameter> Parameters;
	FString Error;
	if (!ParseFunctionParameters(Result.Parameters, Parameters, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	FString Patch;
	if (!FFinareAuthoringSurfaceReader::BuildFunctionAdditionPatch(
		Document,
		*Asset,
		Result.Name,
		Result.ReturnType,
		Result.bStatic,
		Parameters,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Adding function %s"), *Result.Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleEditFunction(FString FunctionId)
{
	const FFinareAuthoringFunction* Function = FindFunction(FunctionId);
	UE::Finare::AuthoringSurface::Slate::Private::FFunctionAuthoringDialogResult Result;
	if (Function == nullptr || IsEventFunction(*Function) || IsLifecycleFunction(*Function) ||
		!UE::Finare::AuthoringSurface::Slate::Private::ShowFunctionAuthoringDialog(
			Function,
			Result))
	{
		return FReply::Handled();
	}
	TArray<FFinareAuthoringParameter> Parameters;
	FString Error;
	if (!ParseFunctionParameters(Result.Parameters, Parameters, Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	FString Patch;
	if (!FFinareAuthoringSurfaceReader::BuildFunctionUpdatePatch(
		Document,
		*Function,
		Result.Name,
		Result.ReturnType,
		Result.bStatic,
		Parameters,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Editing function %s"), *Function->Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRemoveFunction(FString FunctionId)
{
	const FFinareAuthoringFunction* Function = FindFunction(FunctionId);
	if (Function == nullptr)
	{
		SetStatus(TEXT("The selected function is no longer available."), true);
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildFunctionRemovalPatch(
		Document,
		*Function,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(
			TEXT("Removing %s %s"),
			IsEventFunction(*Function) ? TEXT("Event Entry") : TEXT("function"),
			*Function->Name));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleAddLifecycleHook(
	FString AssetId,
	FString Hook)
{
	const FFinareAuthoringAsset* Asset = FindAsset(AssetId);
	if (Asset == nullptr)
	{
		return FReply::Handled();
	}
	FString Patch;
	FString Error;
	if (!FFinareAuthoringSurfaceReader::BuildLifecycleHookAdditionPatch(
		Document,
		*Asset,
		Hook,
		Patch,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return FReply::Handled();
	}
	SubmitDeclarationPatch(
		MoveTemp(Patch),
		FString::Printf(TEXT("Adding %s lifecycle hook"), *Hook));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleUndoAuthoringTransaction()
{
	SubmitAuthoringTransaction(
		TEXT("{}"),
		FFinareAuthoringWorkbench::BuildUndoUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		TEXT("Undoing authoring transaction"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRedoAuthoringTransaction()
{
	SubmitAuthoringTransaction(
		TEXT("{}"),
		FFinareAuthoringWorkbench::BuildRedoUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		TEXT("Redoing authoring transaction"));
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleBeginAuthoringGroup()
{
	const FString Description = AuthoringGroupLabelTextBox.IsValid()
		                            ? AuthoringGroupLabelTextBox->GetText().ToString().TrimStartAndEnd()
		                            : FString();
	if (Description.IsEmpty())
	{
		SetStatus(TEXT("Enter a non-empty grouped transaction label."), true);
		return FReply::Handled();
	}
	const FString GroupId = FString::Printf(
		TEXT("group-%s"),
		*FGuid::NewGuid().ToString(EGuidFormats::Digits));
	SubmitAuthoringTransaction(
		TEXT("{}"),
		FFinareAuthoringWorkbench::BuildGroupBeginUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		Description,
		GroupId,
		FString(),
		false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleCommitAuthoringGroup()
{
	if (ActiveAuthoringGroupId.IsEmpty())
	{
		SetStatus(TEXT("There is no active grouped authoring transaction to commit."), true);
		return FReply::Handled();
	}
	SubmitAuthoringTransaction(
		TEXT("{}"),
		FFinareAuthoringWorkbench::BuildGroupCommitUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		ActiveAuthoringGroupDescription,
		ActiveAuthoringGroupId,
		ActiveAuthoringGroupId,
		true);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleCancelAuthoringGroup()
{
	if (ActiveAuthoringGroupId.IsEmpty())
	{
		SetStatus(TEXT("There is no active grouped authoring transaction to cancel."), true);
		return FReply::Handled();
	}
	SubmitAuthoringTransaction(
		TEXT("{}"),
		FFinareAuthoringWorkbench::BuildGroupCancelUrl(
			ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString()),
		ActiveAuthoringGroupDescription,
		ActiveAuthoringGroupId,
		ActiveAuthoringGroupId,
		false);
	return FReply::Handled();
}

void SFinareAuthoringSurface::SubmitAuthoringTransaction(
	FString Payload,
	FString Url,
	FString Activity,
	FString TransactionId,
	FString GroupId,
	bool bPublishesRuntime)
{
	if (bNativePublicationReservationPending)
	{
		SetStatus(
			TEXT(
				"A native publication reservation is awaiting a verified finish or cancel; retry that recovery action before submitting another transaction."),
			true);
		return;
	}
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("An authoring transaction is already in flight; wait for it to finish."), true);
		return;
	}
	if (Url.IsEmpty())
	{
		SetStatus(TEXT("Enter a Workbench loopback URL before applying a patch."), true);
		return;
	}
	const bool bSourceTextTransaction = Url.EndsWith(
		TEXT("/api/authoring/source-text"),
		ESearchCase::CaseSensitive);
	if (bCodeEditDirty && !bSourceTextTransaction)
	{
		SetStatus(
			TEXT("Apply or revert the unsaved C# buffer before starting another authoring transaction."),
			true);
		return;
	}
	if (bPublishesRuntime && !PersistCurrentLayout(true))
	{
		return;
	}

	if (TransactionId.IsEmpty())
	{
		TransactionId = FGuid::NewGuid().ToString(EGuidFormats::Digits);
	}
	if (GroupId.IsEmpty() && !ActiveAuthoringGroupId.IsEmpty())
	{
		GroupId = ActiveAuthoringGroupId;
	}
	PendingAuthoringTransaction = FFinarePendingAuthoringTransaction{
		MoveTemp(Payload),
		MoveTemp(Url),
		MoveTemp(Activity),
		MoveTemp(TransactionId),
		MoveTemp(GroupId),
		bPublishesRuntime,
	};
	PendingNativePublicationContext.Reset();
	PendingNativePublicationContextCaptureError.Reset();
	FString InitialContextError;
	if (!CaptureNativePublicationContext(InitialContextError))
	{
		PendingNativePublicationContextCaptureError = MoveTemp(InitialContextError);
	}
	bHasAuthoringFailure = false;
	RebuildDiagnosticsPanel();
	if (FFinareAuthoringWorkbench::BuildPreviewUrl(
		PendingAuthoringTransaction->Url).IsEmpty())
	{
		DispatchAuthoringRequest(FString());
	}
	else
	{
		DispatchAuthoringPreview();
	}
}

void SFinareAuthoringSurface::HandlePreviewResponse(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson)
{
	bPatchRequestPending = false;
	if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
	{
		PendingNodePaletteActionId.Reset();
		HandleAuthoringFailure(
			bRequestSucceeded,
			StatusCode,
			ResponseJson,
			TEXT("The Workbench preview request failed; verify that the authoring host is running."));
		return;
	}

	FFinareAuthoringPreviewResult Preview;
	FString Error;
	if (!FFinareAuthoringWorkbench::ParsePreviewResponse(
		ResponseJson,
		Preview,
		Error))
	{
		PendingAuthoringTransaction.Reset();
		PendingNodePaletteActionId.Reset();
		SetStatus(MoveTemp(Error), true);
		return;
	}
	if (!PendingAuthoringTransaction.IsSet() ||
		Preview.TransactionId != PendingAuthoringTransaction->TransactionId)
	{
		PendingAuthoringTransaction.Reset();
		PendingNodePaletteActionId.Reset();
		SetStatus(TEXT("The Workbench preview does not match the pending transaction identity."), true);
		return;
	}

	if (!ShowDiffPreview(Preview))
	{
		const FString Activity = PendingAuthoringTransaction->Activity;
		PendingAuthoringTransaction.Reset();
		PendingNodePaletteActionId.Reset();
		SetStatus(
			FString::Printf(
				TEXT(
					"Cancelled '%s' after preview; the preview performed no source, artifact, history, or runtime write."),
				*Activity),
			false);
		return;
	}
	PendingAuthoringPreview = Preview;
	if (PendingNativePublicationContext.IsSet())
	{
		PendingNativePublicationContext->bHasExpectedPreviewRevisions = true;
		PendingNativePublicationContext->ExpectedBaselineRevision = Preview.BaselineRevision;
		PendingNativePublicationContext->ExpectedCandidateRevision = Preview.CandidateRevision;
	}
	if (!Preview.NativeBindingChanges.IsEmpty())
	{
		if (!PendingNativePublicationContext.IsSet())
		{
			PendingAuthoringPreview.Reset();
			PendingAuthoringTransaction.Reset();
			SetStatus(
				PendingNativePublicationContextCaptureError.IsEmpty()
					? TEXT(
						"Native publication preview has no captured owner context; reload the selected Script Asset and retry.")
					: MoveTemp(PendingNativePublicationContextCaptureError),
				true);
			return;
		}
		DispatchNativePublicationPrepare(Preview.PreviewToken);
	}
	else
	{
		DispatchAuthoringRequest(Preview.PreviewToken);
	}
}

bool SFinareAuthoringSurface::CaptureNativePublicationContext(FString& OutError)
{
	OutError.Reset();
	UFinareAsset* ScriptAsset = LoadedScriptAsset.Get();
	if (!IsValid(ScriptAsset) || !ScriptAsset->GeneratedClassPath.IsValid())
	{
		OutError = TEXT("Native publication preview requires the exact loaded Script Asset and generated class path.");
		return false;
	}
	UClass* GeneratedClass = ScriptAsset->GeneratedClassPath.TryLoadClass<UObject>();
	UFinareBlueprint* Blueprint = GeneratedClass != nullptr
		                              ? Cast<UFinareBlueprint>(GeneratedClass->ClassGeneratedBy)
		                              : nullptr;
	if (!IsValid(GeneratedClass) || !IsValid(Blueprint) ||
		Blueprint->GeneratedClass != GeneratedClass || Blueprint->ScriptAsset != ScriptAsset)
	{
		OutError = TEXT("Native publication preview could not resolve the exact generated owner Blueprint.");
		return false;
	}
	FFinareAuthoringWorkbench::FNativePublicationContext Context;
	Context.ScriptAsset = ScriptAsset;
	Context.Blueprint = Blueprint;
	Context.GeneratedClass = GeneratedClass;
	Context.ScriptAssetObjectPath = ScriptAsset->GetPathName();
	Context.ScriptClassId = ScriptAsset->ScriptClassId;
	Context.ScriptTypeName = ScriptAsset->ScriptTypeName;
	Context.GeneratedClassPath = ScriptAsset->GeneratedClassPath.ToString();
	Context.GeneratedOwnerPath = Blueprint->GetPathName();
	Context.NativeParentClassPath = Blueprint->ParentClass != nullptr
		                                ? Blueprint->ParentClass->GetPathName()
		                                : FString();
	PendingNativePublicationContext = MoveTemp(Context);
	return true;
}

bool SFinareAuthoringSurface::ShowDiffPreview(
	const FFinareAuthoringPreviewResult& Preview)
{
	bool bAccepted = false;
	const FString BeforeText =
		UE::Finare::AuthoringSurface::Slate::Private::BuildPreviewSideText(Preview, true);
	const FString AfterText =
		UE::Finare::AuthoringSurface::Slate::Private::BuildPreviewSideText(Preview, false);
	const FString DiagnosticsText =
		UE::Finare::AuthoringSurface::Slate::Private::BuildPreviewDiagnosticsText(Preview);
	const FString SemanticReviewText =
		UE::Finare::AuthoringSurface::Slate::Private::BuildSemanticReviewText(Preview);
	const FString LayoutRoundTripText = Preview.RoundTrip.bLayoutChecked
		                                    ? FString::Printf(
			                                    TEXT(
				                                    "layout %lld nodes + %lld graph comments + %lld routes kept; %lld new nodes unplaced"),
			                                    Preview.RoundTrip.PreservedNodeLayouts,
			                                    Preview.RoundTrip.PreservedGraphComments,
			                                    Preview.RoundTrip.PreservedRoutingHints,
			                                    Preview.RoundTrip.NewUnplacedNodes)
		                                    : FString(TEXT("layout not configured for this Workbench"));
	const FString RoundTripReviewText = FString::Printf(
		TEXT(
			"ROUND-TRIP GUARD  |  stable IDs %lld kept / +%lld / -%lld  |  source comments %lld kept / +%lld / -%lld  |  %s"),
		Preview.RoundTrip.PreservedSemanticIds,
		Preview.RoundTrip.IntroducedSemanticIds,
		Preview.RoundTrip.RetiredSemanticIds,
		Preview.RoundTrip.PreservedSourceComments,
		Preview.RoundTrip.AddedSourceComments,
		Preview.RoundTrip.RemovedSourceComments,
		*LayoutRoundTripText);
	const TSharedRef<SWindow> Window = SNew(SWindow)
		.Title(FText::FromString(FString::Printf(
			TEXT("Finare Diff Preview - %s"),
			Preview.ProposalTitle.IsEmpty() ? *Preview.Description : *Preview.ProposalTitle)))
		.ClientSize(FVector2D(1180.0f, 720.0f))
		.SupportsMaximize(true)
		.SupportsMinimize(false);
	Window->SetContent(
		SNew(SBorder)
		.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
		.Padding(12.0f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(STextBlock)
				.Text(FText::FromString(FString::Printf(
					TEXT("%s%s  |  revision %lld -> %lld  |  %s"),
					Preview.ProposalId.IsEmpty() ? TEXT("") : TEXT("AI PROPOSAL  |  "),
					Preview.ProposalId.IsEmpty() ? *Preview.Description : *Preview.ProposalId,
					Preview.BaselineRevision,
					Preview.CandidateRevision,
					Preview.bPublishesRuntime
						? TEXT("publishes source + artifacts + runtime")
						: TEXT("stages draft only; runtime unchanged"))))
				.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
			]
			+ SVerticalBox::Slot()
			.FillHeight(0.28f)
			.Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SBorder)
				.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
				.Padding(8.0f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 4.0f)
					[
						SNew(STextBlock)
						.Text(FText::FromString(FString::Printf(
							TEXT("SEMANTIC REVIEW  |  functions +%lld -%lld ~%lld  |  %lld stable-ID changes"),
							Preview.SemanticSummary.AddedFunctions,
							Preview.SemanticSummary.RemovedFunctions,
							Preview.SemanticSummary.ModifiedFunctions,
							Preview.SemanticSummary.ChangeCount)))
						.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
						.ColorAndOpacity(FLinearColor(0.32f, 0.68f, 0.95f))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 5.0f)
					[
						SNew(STextBlock)
						.Text(FText::FromString(RoundTripReviewText))
						.AutoWrapText(true)
						.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
						.ColorAndOpacity(FLinearColor(0.48f, 0.80f, 0.58f))
					]
					+ SVerticalBox::Slot().FillHeight(1.0f)
					[
						SNew(SMultiLineEditableTextBox)
						.Text(FText::FromString(SemanticReviewText))
						.IsReadOnly(true)
						.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
					]
				]
			]
			+ SVerticalBox::Slot()
			.FillHeight(0.72f)
			[
				SNew(SSplitter)
				+ SSplitter::Slot()
				.Value(0.5f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 4.0f)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("DiffBefore", "BEFORE"))
						.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
						.ColorAndOpacity(FLinearColor(0.88f, 0.36f, 0.30f))
					]
					+ SVerticalBox::Slot().FillHeight(1.0f).Padding(0.0f, 0.0f, 4.0f, 0.0f)
					[
						SNew(SMultiLineEditableTextBox)
						.Text(FText::FromString(BeforeText))
						.IsReadOnly(true)
						.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
					]
				]
				+ SSplitter::Slot()
				.Value(0.5f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(4.0f, 0.0f, 0.0f, 4.0f)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("DiffAfter", "AFTER"))
						.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
						.ColorAndOpacity(FLinearColor(0.32f, 0.75f, 0.45f))
					]
					+ SVerticalBox::Slot().FillHeight(1.0f).Padding(4.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SMultiLineEditableTextBox)
						.Text(FText::FromString(AfterText))
						.IsReadOnly(true)
						.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
					]
				]
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.Padding(0.0f, 8.0f, 0.0f, 0.0f)
			[
				SNew(STextBlock)
				.Visibility(Preview.Diagnostics.IsEmpty()
					            ? EVisibility::Collapsed
					            : EVisibility::Visible)
				.Text(FText::FromString(DiagnosticsText))
				.AutoWrapText(true)
				.ColorAndOpacity(FLinearColor(0.95f, 0.68f, 0.20f))
			]
			+ SVerticalBox::Slot()
			.AutoHeight()
			.HAlign(HAlign_Right)
			.Padding(0.0f, 10.0f, 0.0f, 0.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0.0f, 0.0f, 8.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("CancelDiff", "Cancel"))
					.OnClicked_Lambda([Window]()
					{
						Window->RequestDestroyWindow();
						return FReply::Handled();
					})
				]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.Text(Preview.bPublishesRuntime
						      ? LOCTEXT("ApplyDiff", "Apply Transaction")
						      : LOCTEXT("StageDiff", "Stage Edit"))
					.ButtonStyle(FAppStyle::Get(), TEXT("PrimaryButton"))
					.OnClicked_Lambda([Window, &bAccepted]()
					{
						bAccepted = true;
						Window->RequestDestroyWindow();
						return FReply::Handled();
					})
				]
			]
		]);
	FSlateApplication::Get().AddModalWindow(Window, SharedThis(this), false);
	return bAccepted;
}

void SFinareAuthoringSurface::ConfigureAuthoringRequest(
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe>& Request,
	const FString& Url,
	const FString& PreviewToken) const
{
	check(PendingAuthoringTransaction.IsSet());
	const FFinarePendingAuthoringTransaction& Transaction =
		PendingAuthoringTransaction.GetValue();
	Request->SetURL(Url);
	Request->SetVerb(TEXT("POST"));
	Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
	Request->SetHeader(TEXT("X-Finare-Transaction-Id"), Transaction.TransactionId);
	Request->SetHeader(TEXT("X-Finare-Transaction-Label"), Transaction.Activity);
	if (!Transaction.GroupId.IsEmpty())
	{
		Request->SetHeader(TEXT("X-Finare-Group-Id"), Transaction.GroupId);
	}
	if (WorkbenchRevision > 0)
	{
		Request->SetHeader(
			TEXT("X-Finare-Baseline-Revision"),
			LexToString(WorkbenchRevision));
	}
	if (!PreviewToken.IsEmpty())
	{
		Request->SetHeader(TEXT("X-Finare-Preview-Token"), PreviewToken);
	}
	Request->SetContentAsString(Transaction.Payload);
}

void SFinareAuthoringSurface::DispatchAuthoringPreview()
{
	check(PendingAuthoringTransaction.IsSet());
	const FString PreviewUrl = FFinareAuthoringWorkbench::BuildPreviewUrl(
		PendingAuthoringTransaction->Url);
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	ConfigureAuthoringRequest(Request, PreviewUrl, FString());
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				Pinned->HandlePreviewResponse(
					bSucceeded && Response.IsValid(),
					Response.IsValid() ? Response->GetResponseCode() : 0,
					Response.IsValid() ? Response->GetContentAsString() : FString());
			}
		});

	bPatchRequestPending = true;
	SetStatus(
		FString::Printf(
			TEXT("Preparing exact C# diff for %s ..."),
			*PendingAuthoringTransaction->Activity),
		false);
	if (!Request->ProcessRequest())
	{
		bPatchRequestPending = false;
		HandleAuthoringFailure(
			false,
			0,
			FString(),
			TEXT("The Workbench preview request could not be started."));
	}
}

void SFinareAuthoringSurface::DispatchAuthoringRequest(const FString& PreviewToken)
{
	check(PendingAuthoringTransaction.IsSet());
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	ConfigureAuthoringRequest(Request, PendingAuthoringTransaction->Url, PreviewToken);
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				Pinned->HandlePatchResponse(
					bSucceeded && Response.IsValid(),
					Response.IsValid() ? Response->GetResponseCode() : 0,
					Response.IsValid() ? Response->GetContentAsString() : FString());
			}
		});

	bPatchRequestPending = true;
	bPendingRequestPublishesRuntime = PendingAuthoringTransaction->bPublishesRuntime &&
	(ActiveAuthoringGroupId.IsEmpty() ||
		PendingAuthoringTransaction->Url.EndsWith(TEXT("/api/authoring/group/commit")));
	SetStatus(
		FString::Printf(
			TEXT("%s via %s ..."),
			*PendingAuthoringTransaction->Activity,
			*PendingAuthoringTransaction->Url),
		false);
	if (!Request->ProcessRequest())
	{
		bPatchRequestPending = false;
		HandleAuthoringFailure(
			false,
			0,
			FString(),
			TEXT("The Workbench HTTP request could not be started."));
	}
}

void SFinareAuthoringSurface::DispatchNativePublicationPrepare(const FString& PreviewToken)
{
	check(PendingAuthoringTransaction.IsSet());
	const UFinareAsset* Asset = LoadedScriptAsset.Get();
	const FString ServerUrl = Asset != nullptr
		                          ? Asset->ResolveAuthoringServerUrl()
		                          : FString();
	const FString Url = FFinareAuthoringWorkbench::BuildNativePrepareUrl(ServerUrl);
	if (Url.IsEmpty())
	{
		HandleNativePublicationPrepareResponse(false, 0, FString());
		return;
	}
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	Request->SetURL(Url);
	Request->SetVerb(TEXT("POST"));
	Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
	Request->SetHeader(TEXT("X-Finare-Transaction-Id"), PendingAuthoringTransaction->TransactionId);
	Request->SetHeader(TEXT("X-Finare-Transaction-Label"), PendingAuthoringTransaction->Activity);
	if (PendingAuthoringTransaction->GroupId.IsEmpty() == false)
	{
		Request->SetHeader(TEXT("X-Finare-Group-Id"), PendingAuthoringTransaction->GroupId);
	}
	if (WorkbenchRevision > 0)
	{
		Request->SetHeader(TEXT("X-Finare-Baseline-Revision"), LexToString(WorkbenchRevision));
	}
	Request->SetHeader(TEXT("X-Finare-Preview-Token"), PreviewToken);
	Request->SetContentAsString(TEXT("{}"));
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
#if WITH_DEV_AUTOMATION_TESTS
	if (NativePublicationTestTransport.IsSet())
	{
		bPatchRequestPending = true;
		SetStatus(
			FString::Printf(TEXT("Preparing native publication for %s ..."),
			                *PendingAuthoringTransaction->Activity),
			false);
		const bool bStarted = NativePublicationTestTransport.GetValue()(
			TEXT("POST"),
			Url,
			PendingAuthoringTransaction->TransactionId,
			PreviewToken,
			TEXT("{}"),
			[WeakThis](bool bSucceeded, int32 ResponseCode, FString ResponseJson)
			{
				if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
				{
					Pinned->HandleNativePublicationPrepareResponse(
						bSucceeded,
						ResponseCode,
						MoveTemp(ResponseJson));
				}
			});
		if (!bStarted)
		{
			HandleNativePublicationPrepareResponse(false, 0, FString());
		}
		return;
	}
#endif
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				Pinned->HandleNativePublicationPrepareResponse(
					bSucceeded && Response.IsValid(),
					Response.IsValid() ? Response->GetResponseCode() : 0,
					Response.IsValid() ? Response->GetContentAsString() : FString());
			}
		});
	bPatchRequestPending = true;
	SetStatus(
		FString::Printf(TEXT("Preparing native publication for %s ..."),
		                *PendingAuthoringTransaction->Activity),
		false);
	if (!Request->ProcessRequest())
	{
		HandleNativePublicationPrepareResponse(false, 0, FString());
	}
}

FReply SFinareAuthoringSurface::HandleRetryNativePublicationPrepare()
{
	if (!PendingAuthoringTransaction.IsSet() ||
		!PendingAuthoringPreview.IsSet() ||
		!bNativePublicationReservationPending ||
		!bNativePublicationPrepareRecoveryPending)
	{
		SetStatus(TEXT("There is no native publication prepare recovery to retry."), true);
		return FReply::Handled();
	}
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("The native publication recovery request is still in flight."), true);
		return FReply::Handled();
	}
	DispatchNativePublicationPrepare(PendingAuthoringPreview->PreviewToken);
	return FReply::Handled();
}

void SFinareAuthoringSurface::HandleNativePublicationPrepareResponse(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson)
{
	if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
	{
		PendingNativePublication.Reset();
		bPatchRequestPending = false;
		bNativePublicationReservationPending = true;
		bNativePublicationPrepareRecoveryPending = true;
		bNativePublicationCancelRecoveryPending = false;
		SetStatus(
			TEXT(
				"The native publication prepare response was uncertain; keep the reservation and retry native prepare."),
			true);
		return;
	}
	FFinareNativePublicationPrepareResult Prepare;
	FString Error;
	const bool bParsed = FFinareAuthoringWorkbench::ParseNativePublicationPrepareResponse(
		ResponseJson,
		Prepare,
		Error);
	const bool bMatchesPendingIdentity =
		bParsed && PendingAuthoringTransaction.IsSet() &&
		PendingAuthoringPreview.IsSet() &&
		Prepare.TransactionId == PendingAuthoringTransaction->TransactionId &&
		Prepare.PreviewToken == PendingAuthoringPreview->PreviewToken;
	if (!bMatchesPendingIdentity ||
		!PendingAuthoringTransaction.IsSet() ||
		!PendingAuthoringPreview.IsSet() ||
		Prepare.BaselineRevision != PendingAuthoringPreview->BaselineRevision ||
		Prepare.CandidateRevision != PendingAuthoringPreview->CandidateRevision)
	{
		if (Error.IsEmpty())
		{
			Error = TEXT("The native publication prepare response does not match the pending authoring transaction.");
		}
		// Keep a same-transaction prepare candidate so a rejected revision
		// response can still be cancelled after the output baseline is
		// proven. Foreign or malformed candidates are never retained.
		if (bMatchesPendingIdentity)
		{
			PendingNativePublication = Prepare;
			bNativePublicationReservationPending = true;
			bNativePublicationPrepareRecoveryPending = false;
		}
		else
		{
			PendingNativePublication.Reset();
			bPatchRequestPending = false;
			bNativePublicationReservationPending = true;
			bNativePublicationPrepareRecoveryPending = true;
			bNativePublicationCancelRecoveryPending = false;
			SetStatus(
				TEXT(
					"The native publication prepare response was malformed or foreign; keep the reservation and retry native prepare."),
				true);
			return;
		}
		// The server reservation is real even though this response was not
		// accepted.  A local success-shaped cancel response is not an ACK;
		// send the cancel request and retain the reservation on transport
		// failure.
		DispatchNativePublicationCancel(Error);
		return;
	}
	PendingNativePublication = Prepare;
	bNativePublicationReservationPending = true;
	bNativePublicationPrepareRecoveryPending = false;
	bNativePublicationCancelRecoveryPending = false;
	if (!ApplyNativePublication(Prepare, Error))
	{
		FString CancelError;
		if (!FFinareAuthoringWorkbench::CanCancelNativePublication(
			Prepare,
			CancelError))
		{
			bPatchRequestPending = false;
			SetStatus(
				FString::Printf(
					TEXT(
						"Native publication failed and its rollback is uncertain; keep the reservation for recovery. %s"),
					*CancelError),
				true);
			bNativePublicationCancelRecoveryPending = true;
			return;
		}
		DispatchNativePublicationCancel(
			Error.IsEmpty()
				? TEXT("Native publication failed after returning to its prepared baseline.")
				: Error);
		return;
	}
	if (!PendingNativePublicationContext.IsSet() ||
		!FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			PendingNativePublicationContext.GetValue(),
			Prepare,
			Error))
	{
		SetStatus(
			Error.IsEmpty()
				? TEXT(
					"Native publication was materialized but its durable completion marker could not be resolved; keep the reservation for recovery.")
				: MoveTemp(Error),
			true);
		return;
	}
	DispatchNativePublicationFinish();
}

bool SFinareAuthoringSurface::ApplyNativePublication(
	const FFinareNativePublicationPrepareResult& Prepare,
	FString& OutError)
{
	if (!PendingAuthoringPreview.IsSet() || !PendingNativePublicationContext.IsSet())
	{
		OutError = TEXT("The native publication candidate has no captured owner context or accepted preview.");
		return false;
	}
	return FFinareAuthoringWorkbench::ApplyNativePublication(
		PendingNativePublicationContext.GetValue(),
		Prepare,
		PendingAuthoringPreview->PreviewToken,
		OutError);
}

void SFinareAuthoringSurface::DispatchNativePublicationFinish()
{
	check(PendingAuthoringTransaction.IsSet());
	check(PendingAuthoringPreview.IsSet());
	const UFinareAsset* Asset = LoadedScriptAsset.Get();
	const FString Url = FFinareAuthoringWorkbench::BuildNativeFinishUrl(
		Asset != nullptr ? Asset->ResolveAuthoringServerUrl() : FString());
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	Request->SetURL(Url);
	Request->SetVerb(TEXT("POST"));
	Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
	Request->SetHeader(TEXT("X-Finare-Transaction-Id"), PendingAuthoringTransaction->TransactionId);
	Request->SetHeader(TEXT("X-Finare-Transaction-Label"), PendingAuthoringTransaction->Activity);
	if (WorkbenchRevision > 0)
	{
		Request->SetHeader(TEXT("X-Finare-Baseline-Revision"), LexToString(WorkbenchRevision));
	}
	Request->SetHeader(TEXT("X-Finare-Preview-Token"), PendingAuthoringPreview->PreviewToken);
	Request->SetContentAsString(FString::Printf(
		TEXT("{\"previewToken\":\"%s\",\"succeeded\":true}"),
		*PendingAuthoringPreview->PreviewToken));
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				if (!bSucceeded || !Response.IsValid() || Response->GetResponseCode() < 200 ||
					Response->GetResponseCode() >= 300)
				{
					Pinned->SetStatus(
						TEXT(
							"Native publication was applied locally but its completion acknowledgement is uncertain; resolve the reserved transaction before editing again."),
						true);
					return;
				}
				FString MarkerError;
				if (!Pinned->PendingNativePublication.IsSet() ||
					!Pinned->PendingNativePublicationContext.IsSet() ||
					!FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
						Pinned->PendingNativePublicationContext.GetValue(),
						Pinned->PendingNativePublication.GetValue(),
						MarkerError))
				{
					Pinned->SetStatus(
						MarkerError.IsEmpty()
							? TEXT(
								"Native publication completed but its durable completion marker could not be resolved; keep the reservation for recovery.")
							: MoveTemp(MarkerError),
						true);
					return;
				}
				TSharedPtr<FJsonObject> Root;
				const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(
					Response->GetContentAsString());
				const TSharedPtr<FJsonObject>* Workspace = nullptr;
				if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
					!Root->TryGetObjectField(TEXT("workspace"), Workspace) ||
					Workspace == nullptr || !Workspace->IsValid())
				{
					Pinned->SetStatus(TEXT("Native publication completed but returned no adoptable workspace."), true);
					return;
				}
				FString WorkspaceJson;
				const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&WorkspaceJson);
				if (!FJsonSerializer::Serialize(*Workspace, Writer))
				{
					Pinned->SetStatus(
						TEXT("Native publication completed but its workspace response was invalid."), true);
					return;
				}
				Pinned->PendingNativePublication.Reset();
				Pinned->PendingAuthoringPreview.Reset();
				Pinned->PendingNativePublicationContext.Reset();
				Pinned->PendingNativePublicationContextCaptureError.Reset();
				Pinned->bSkipDailyBuildForNativePublication = true;
				Pinned->HandlePatchResponse(true, 200, MoveTemp(WorkspaceJson));
			}
		});
	SetStatus(TEXT("Finalizing native publication ..."), false);
	if (!Request->ProcessRequest())
	{
		SetStatus(
			TEXT(
				"Native publication was applied locally but its completion acknowledgement is uncertain; resolve the reserved transaction before editing again."),
			true);
	}
}

FReply SFinareAuthoringSurface::HandleRetryNativePublicationFinish()
{
	if (!PendingNativePublication.IsSet() || !PendingAuthoringTransaction.IsSet() ||
		!PendingAuthoringPreview.IsSet())
	{
		SetStatus(TEXT("There is no pending native publication finish to retry."), true);
		return FReply::Handled();
	}
	FString MarkerError;
	if (!PendingNativePublicationContext.IsSet() ||
		!FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
			PendingNativePublicationContext.GetValue(),
			PendingNativePublication.GetValue(),
			MarkerError))
	{
		SetStatus(
			MarkerError.IsEmpty()
				? TEXT("Native publication completion is not recoverable yet; keep the reservation pending.")
				: MoveTemp(MarkerError),
			true);
		return FReply::Handled();
	}
	DispatchNativePublicationFinish();
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleRetryNativePublicationCancel()
{
	if (!PendingNativePublication.IsSet() ||
		!PendingAuthoringTransaction.IsSet() ||
		!PendingAuthoringPreview.IsSet() ||
		!bNativePublicationReservationPending)
	{
		SetStatus(TEXT("There is no native publication reservation ready for cancellation."), true);
		return FReply::Handled();
	}
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("The native publication recovery request is still in flight."), true);
		return FReply::Handled();
	}
	FString BaselineError;
	if (!FFinareAuthoringWorkbench::CanCancelNativePublication(
		PendingNativePublication.GetValue(),
		BaselineError))
	{
		SetStatus(
			BaselineError.IsEmpty()
				? TEXT("Native publication rollback is not confirmed; keep the reservation pending.")
				: MoveTemp(BaselineError),
			true);
		return FReply::Handled();
	}
	DispatchNativePublicationCancel(
		TEXT("Retrying native publication cancellation after verified rollback."));
	return FReply::Handled();
}

void SFinareAuthoringSurface::DispatchNativePublicationCancel(const FString& ErrorMessage)
{
	check(PendingAuthoringTransaction.IsSet());
	bNativePublicationReservationPending = true;
	bNativePublicationCancelRecoveryPending = true;
	const UFinareAsset* Asset = LoadedScriptAsset.Get();
	const FString Url = FFinareAuthoringWorkbench::BuildNativeCancelUrl(
		Asset != nullptr ? Asset->ResolveAuthoringServerUrl() : FString());
	if (Url.IsEmpty())
	{
		HandleNativePublicationCancel(false, 0, FString(), ErrorMessage);
		return;
	}
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	Request->SetURL(Url);
	Request->SetVerb(TEXT("POST"));
	Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
	Request->SetHeader(TEXT("X-Finare-Transaction-Id"), PendingAuthoringTransaction->TransactionId);
	Request->SetHeader(TEXT("X-Finare-Transaction-Label"), PendingAuthoringTransaction->Activity);
	if (WorkbenchRevision > 0)
	{
		Request->SetHeader(TEXT("X-Finare-Baseline-Revision"), LexToString(WorkbenchRevision));
	}
	if (PendingAuthoringPreview.IsSet())
	{
		Request->SetHeader(TEXT("X-Finare-Preview-Token"), PendingAuthoringPreview->PreviewToken);
	}
	Request->SetContentAsString(TEXT("{}"));
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
#if WITH_DEV_AUTOMATION_TESTS
	if (NativePublicationTestTransport.IsSet())
	{
		bPatchRequestPending = true;
		SetStatus(TEXT("Cancelling native publication ..."), false);
		const bool bStarted = NativePublicationTestTransport.GetValue()(
			TEXT("POST"),
			Url,
			PendingAuthoringTransaction->TransactionId,
			PendingAuthoringPreview.IsSet()
				? PendingAuthoringPreview->PreviewToken
				: FString(),
			TEXT("{}"),
			[WeakThis, ErrorMessage](bool bSucceeded, int32 ResponseCode, FString ResponseJson)
			{
				if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
				{
					Pinned->HandleNativePublicationCancel(
						bSucceeded,
						ResponseCode,
						MoveTemp(ResponseJson),
						ErrorMessage);
				}
			});
		if (!bStarted)
		{
			bPatchRequestPending = false;
			HandleNativePublicationCancel(
				false,
				0,
				FString(),
				ErrorMessage);
		}
		return;
	}
#endif
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis, ErrorMessage](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				Pinned->HandleNativePublicationCancel(
					bSucceeded && Response.IsValid(),
					Response.IsValid() ? Response->GetResponseCode() : 0,
					Response.IsValid() ? Response->GetContentAsString() : FString(),
					ErrorMessage);
			}
		});
	bPatchRequestPending = true;
	SetStatus(TEXT("Cancelling native publication ..."), false);
	if (!Request->ProcessRequest())
	{
		HandleNativePublicationCancel(false, 0, FString(), ErrorMessage);
	}
}

void SFinareAuthoringSurface::HandleNativePublicationCancelResponse(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson,
	FString ErrorMessage)
{
	bNativePublicationReservationPending = true;
	bNativePublicationCancelRecoveryPending = true;
	bPatchRequestPending = false;
	if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
	{
		HandleAuthoringFailure(
			bRequestSucceeded,
			StatusCode,
			MoveTemp(ResponseJson),
			TEXT("Native publication was rejected and its reservation could not be cancelled."),
			true);
		return;
	}
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ResponseJson);
	const TSharedPtr<FJsonObject>* Prepare = nullptr;
	FString Schema;
	FString State;
	FString TransactionId;
	FString PreparedTransactionId;
	FString PreparedPreviewToken;
	const bool bValidCancelAck =
		FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
		Root->TryGetStringField(TEXT("schemaVersion"), Schema) &&
		Schema == TEXT("unifiedscript.native-publication-finish/v0.1") &&
		Root->TryGetStringField(TEXT("state"), State) &&
		State == TEXT("cancelled") &&
		Root->TryGetStringField(TEXT("transactionId"), TransactionId) &&
		PendingAuthoringTransaction.IsSet() &&
		TransactionId == PendingAuthoringTransaction->TransactionId &&
		Root->TryGetObjectField(TEXT("prepare"), Prepare) &&
		Prepare != nullptr && Prepare->IsValid() &&
		(*Prepare)->TryGetStringField(TEXT("transactionId"), PreparedTransactionId) &&
		(*Prepare)->TryGetStringField(TEXT("previewToken"), PreparedPreviewToken) &&
		PreparedTransactionId == TransactionId &&
		PendingAuthoringPreview.IsSet() &&
		PreparedPreviewToken == PendingAuthoringPreview->PreviewToken;
	if (!bValidCancelAck)
	{
		const FString ValidationError =
			State == TEXT("adopted")
				? TEXT(
					"Native publication cancel returned an adopted completion; keep the reservation and resolve the completed transaction.")
				: TEXT(
					"Native publication cancel returned a malformed, foreign, or non-cancelled acknowledgement; keep the reservation for recovery.");
		FString AckError = ValidationError;
		if (!ErrorMessage.IsEmpty())
		{
			AckError += TEXT(" ");
			AckError += ErrorMessage;
		}
		HandleAuthoringFailure(
			false,
			StatusCode,
			MoveTemp(ResponseJson),
			MoveTemp(AckError),
			true);
		return;
	}
	PendingNativePublication.Reset();
	PendingAuthoringPreview.Reset();
	PendingNativePublicationContext.Reset();
	PendingNativePublicationContextCaptureError.Reset();
	bNativePublicationReservationPending = false;
	bNativePublicationPrepareRecoveryPending = false;
	bNativePublicationCancelRecoveryPending = false;
	const FString Activity = PendingAuthoringTransaction.IsSet()
		                         ? PendingAuthoringTransaction->Activity
		                         : FString(TEXT("native publication"));
	PendingAuthoringTransaction.Reset();
	SetStatus(
		FString::Printf(TEXT("Cancelled '%s'; no source, artifact, history, or UE package was adopted."),
		                *Activity),
		true);
}

void SFinareAuthoringSurface::HandleNativePublicationCancel(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson,
	FString ErrorMessage)
{
	if (!PendingAuthoringTransaction.IsSet())
	{
		bNativePublicationReservationPending = false;
		bNativePublicationPrepareRecoveryPending = false;
		bNativePublicationCancelRecoveryPending = false;
		bPatchRequestPending = false;
		SetStatus(MoveTemp(ErrorMessage), true);
		return;
	}
	bNativePublicationReservationPending = true;
	bNativePublicationCancelRecoveryPending = true;
	if (bRequestSucceeded && StatusCode >= 200 && StatusCode < 300)
	{
		HandleNativePublicationCancelResponse(
			bRequestSucceeded,
			StatusCode,
			MoveTemp(ResponseJson),
			MoveTemp(ErrorMessage));
		return;
	}
	bPatchRequestPending = false;
	HandleAuthoringFailure(
		bRequestSucceeded,
		StatusCode,
		MoveTemp(ResponseJson),
		MoveTemp(ErrorMessage),
		true);
}

void SFinareAuthoringSurface::HandlePatchResponse(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson)
{
	const bool bPublishedRuntime = bPendingRequestPublishesRuntime;
	const bool bSkipDailyBuild = bSkipDailyBuildForNativePublication;
	bSkipDailyBuildForNativePublication = false;
	const FString CompletedNodePaletteActionId = MoveTemp(PendingNodePaletteActionId);
	PendingNodePaletteActionId.Reset();
	bPatchRequestPending = false;
	bPendingRequestPublishesRuntime = true;
	if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
	{
		HandleAuthoringFailure(
			bRequestSucceeded,
			StatusCode,
			ResponseJson,
			TEXT("The Workbench loopback request failed; verify that the authoring host is running."));
		return;
	}

	FFinareAuthoringWorkspaceResult Workspace;
	FString Error;
	if (!FFinareAuthoringWorkbench::ParseWorkspaceResponse(
		ResponseJson,
		Workspace,
		Error))
	{
		PendingAuthoringTransaction.Reset();
		SetStatus(MoveTemp(Error), true);
		return;
	}
	const bool bWorkspaceGroupActive = Workspace.Group.bActive;
	const int64 WorkspaceRevision = Workspace.Revision;
	const int64 WorkspaceGeneration = Workspace.Generation;
	const FString ReloadTransactionJson = Workspace.ReloadTransactionJson;
	AdoptWorkspace(MoveTemp(Workspace));
	PendingAuthoringPreview.Reset();
	PendingNativePublication.Reset();
	PendingNativePublicationContext.Reset();
	PendingNativePublicationContextCaptureError.Reset();
	bNativePublicationReservationPending = false;
	bNativePublicationPrepareRecoveryPending = false;
	bNativePublicationCancelRecoveryPending = false;
	PendingAuthoringTransaction.Reset();
	bHasAuthoringFailure = false;
	RebuildDiagnosticsPanel();
	if (!CompletedNodePaletteActionId.IsEmpty())
	{
		RecordNodePaletteUsage(CompletedNodePaletteActionId);
	}
	RebuildTree();

	int32 Matched = 0;
	int32 Reloaded = 0;
	int32 Failed = 0;
	FString ObservedReloadTransactionJson;
	const bool bDailyBuildQueued = bPublishedRuntime && !bSkipDailyBuild &&
		IFinareEditorModule::Get().RequestDailyBuild(
			EFinareDailyBuildReason::GraphPublication,
			bReloadMatchingComponents);
	const TCHAR* DailyBuildStatus = bSkipDailyBuild
		                                ? TEXT("not requested for native publication")
		                                : (bDailyBuildQueued ? TEXT("queued") : TEXT("failed to queue"));
	const FString HistoryPersistenceDetail = HistoryPersistenceMessage.IsEmpty()
		                                         ? FString()
		                                         : FString::Printf(TEXT(" (%s)"), *HistoryPersistenceMessage);
	if (bWorkspaceGroupActive)
	{
		SetStatus(
			FString::Printf(
				TEXT(
					"Grouped draft '%s': %lld staged edit%s at draft revision %lld; source, history, runtime generation %lld, and components are unchanged."),
				*ActiveAuthoringGroupDescription,
				ActiveAuthoringGroupStagedCount,
				ActiveAuthoringGroupStagedCount == 1 ? TEXT("") : TEXT("s"),
				WorkspaceRevision,
				WorkspaceGeneration),
			false);
		return;
	}
	FString CompletionStatus = bPublishedRuntime
		                           ? FString::Printf(
			                           TEXT(
				                           "Applied revision %lld / Workbench generation %lld; history %lld undo / %lld redo; journal %s%s; Surface refreshed; shared daily Build & Reload %s."),
			                           WorkspaceRevision,
			                           WorkspaceGeneration,
			                           UndoAuthoringDepth,
			                           RedoAuthoringDepth,
			                           *HistoryPersistenceState,
			                           *HistoryPersistenceDetail,
			                           DailyBuildStatus)
		                           : FString::Printf(
			                           TEXT(
				                           "Grouped draft discarded; committed revision %lld / Workbench generation %lld remains active; history %lld undo / %lld redo; journal %s%s; Surface restored; %d reload targets matched, %d updated, %d failed."),
			                           WorkspaceRevision,
			                           WorkspaceGeneration,
			                           UndoAuthoringDepth,
			                           RedoAuthoringDepth,
			                           *HistoryPersistenceState,
			                           *HistoryPersistenceDetail,
			                           Matched,
			                           Reloaded,
			                           Failed);
	const FString& TransactionJson = ObservedReloadTransactionJson.IsEmpty()
		                                 ? ReloadTransactionJson
		                                 : ObservedReloadTransactionJson;
	if (!TransactionJson.IsEmpty())
	{
		FFinareReloadTransactionResult ObservedTransaction;
		FString TransactionError;
		if (FFinareAuthoringWorkbench::ParseReloadTransaction(
			TransactionJson,
			ObservedTransaction,
			TransactionError))
		{
			CompletionStatus += TEXT("\n");
			CompletionStatus += ObservedTransaction.ToDisplayString();
			if (Failed > 0 || ObservedTransaction.State == TEXT("restartRequired") ||
				ObservedTransaction.State == TEXT("rolledBack"))
			{
				FFinareEditorDiagnostics::Publish(
					LOCTEXT("ReloadDiagnosticsPage", "Script Class reload"),
					{
						FFinareEditorDiagnostics::FromReloadTransaction(
							ObservedTransaction,
							WorkbenchSourcePath)
					});
			}
		}
	}
	SetStatus(
		CompletionStatus,
		(bPublishedRuntime && !bSkipDailyBuild && !bDailyBuildQueued) || Failed > 0 ||
		HistoryPersistenceState == TEXT("rejected"));
}

void SFinareAuthoringSurface::AdoptWorkspace(
	FFinareAuthoringWorkspaceResult Workspace)
{
	WorkbenchSourcePath = MoveTemp(Workspace.SourcePath);
	WorkbenchSourceText = MoveTemp(Workspace.SourceText);
	WorkbenchScriptAssetsJson = MoveTemp(Workspace.ScriptAssetsJson);
	CodeEditBuffer = WorkbenchSourceText;
	bHasCanonicalSourceSnapshot = true;
	bCodeEditDirty = false;
	Document = MoveTemp(Workspace.Surface);
	WorkbenchRevision = Workspace.Revision;
	bCanUndoAuthoring = Workspace.History.bCanUndo;
	bCanRedoAuthoring = Workspace.History.bCanRedo;
	UndoAuthoringDepth = Workspace.History.UndoDepth;
	RedoAuthoringDepth = Workspace.History.RedoDepth;
	UndoAuthoringDescription = MoveTemp(Workspace.History.UndoDescription);
	RedoAuthoringDescription = MoveTemp(Workspace.History.RedoDescription);
	HistoryPersistenceState = MoveTemp(Workspace.History.PersistenceState);
	HistoryPersistenceMessage = MoveTemp(Workspace.History.PersistenceMessage);
	VisualProfileDisplayName = MoveTemp(Workspace.Profile.DisplayName);
	OpaqueMethodCount = Workspace.Profile.OpaqueMethodCount;
	ActiveAuthoringGroupId = Workspace.Group.bActive ? MoveTemp(Workspace.Group.Id) : FString();
	ActiveAuthoringGroupDescription = Workspace.Group.bActive
		                                  ? MoveTemp(Workspace.Group.Description)
		                                  : FString();
	ActiveAuthoringGroupStagedCount = Workspace.Group.bActive
		                                  ? Workspace.Group.StagedOperationCount
		                                  : 0;
	if (!LoadedSurfacePath.IsEmpty())
	{
		LoadedSurfaceTimestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
	}
	RebuildTree();
}

bool SFinareAuthoringSurface::FindClassBinding(
	const FString& AssetId,
	FFinareClassBindingDeclaration& OutBinding) const
{
	OutBinding = FFinareClassBindingDeclaration();
	if (WorkbenchScriptAssetsJson.IsEmpty() || AssetId.IsEmpty())
	{
		return false;
	}
	TSharedPtr<FJsonObject> Root;
	if (!FJsonSerializer::Deserialize(
			TJsonReaderFactory<>::Create(WorkbenchScriptAssetsJson), Root) ||
		!Root.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
	if (!Root->TryGetArrayField(TEXT("assets"), Assets) || Assets == nullptr)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Assets)
	{
		const TSharedPtr<FJsonObject> Asset = Value.IsValid() ? Value->AsObject() : nullptr;
		FString CurrentId;
		if (!Asset.IsValid() || !Asset->TryGetStringField(TEXT("id"), CurrentId) ||
			CurrentId != AssetId)
		{
			continue;
		}
		TOptional<FFinareClassBindingDeclaration> Binding;
		FString Error;
		if (!FFinareClassBindingDeclaration::ReadJson(Asset, Binding, Error) || !Binding.IsSet())
		{
			return false;
		}
		OutBinding = Binding.GetValue();
		return true;
	}
	return false;
}

void SFinareAuthoringSurface::HandleAuthoringFailure(
	bool bRequestSucceeded,
	int32 StatusCode,
	const FString& ResponseJson,
	const FString& NetworkMessage,
	bool bPreserveNativeReservation)
{
	if (!bPreserveNativeReservation)
	{
		PendingAuthoringTransaction.Reset();
		PendingAuthoringPreview.Reset();
		PendingNativePublication.Reset();
		PendingNativePublicationContext.Reset();
		PendingNativePublicationContextCaptureError.Reset();
		bNativePublicationReservationPending = false;
		bNativePublicationPrepareRecoveryPending = false;
		bNativePublicationCancelRecoveryPending = false;
	}
	FFinareAuthoringErrorResult Failure;
	FString ParseError;
	if (bRequestSucceeded && FFinareAuthoringWorkbench::ParseErrorResponse(
		ResponseJson,
		StatusCode,
		Failure,
		ParseError))
	{
		LastAuthoringFailure = MoveTemp(Failure);
	}
	else
	{
		LastAuthoringFailure = FFinareAuthoringErrorResult{};
		LastAuthoringFailure.StatusCode = StatusCode;
		LastAuthoringFailure.Code = bRequestSucceeded ? TEXT("INVALID_RESPONSE") : TEXT("NO_RESPONSE");
		LastAuthoringFailure.Message = bRequestSucceeded && !ParseError.IsEmpty()
			                               ? MoveTemp(ParseError)
			                               : NetworkMessage;
	}
	bHasAuthoringFailure = true;
	RebuildDiagnosticsPanel();
	FFinareEditorDiagnostics::Publish(
		LOCTEXT("AuthoringDiagnosticsPage", "Authoring transaction"),
		FFinareEditorDiagnostics::FromAuthoringFailure(LastAuthoringFailure));
	SetStatus(LastAuthoringFailure.ToDisplayString(), true);
}

void SFinareAuthoringSurface::RebuildDiagnosticsPanel()
{
	if (!DiagnosticsBox.IsValid())
	{
		return;
	}
	DiagnosticsBox->ClearChildren();
	if (!bHasAuthoringFailure)
	{
		return;
	}

	const bool bRollbackConfirmed = LastAuthoringFailure.Rollback.IsConfirmed();
	const bool bRollbackFailed = LastAuthoringFailure.Rollback.State == TEXT("failed");
	const FString RollbackTitle = bRollbackConfirmed
		                              ? FString::Printf(
			                              TEXT("ROLLBACK CONFIRMED  ·  %s  ·  revision %lld / generation %lld"),
			                              *LastAuthoringFailure.Rollback.State.ToUpper(),
			                              LastAuthoringFailure.Rollback.Revision,
			                              LastAuthoringFailure.Rollback.Generation)
		                              : bRollbackFailed
		                              ? TEXT("ROLLBACK FAILED  ·  stop authoring and inspect the host")
		                              : TEXT("ROLLBACK NOT CONFIRMED  ·  no server receipt was returned");
	const FLinearColor RollbackColor = bRollbackConfirmed
		                                   ? FLinearColor(0.30f, 0.78f, 0.45f)
		                                   : FLinearColor(0.95f, 0.28f, 0.22f);

	const TSharedRef<SVerticalBox> Body = SNew(SVerticalBox);
	Body->AddSlot().AutoHeight()
	[
		SNew(STextBlock)
		.Text(FText::FromString(LastAuthoringFailure.ToDisplayString()))
		.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		.AutoWrapText(true)
		.ColorAndOpacity(FLinearColor(0.96f, 0.40f, 0.34f))
	];
	Body->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 2.0f)
	[
		SNew(STextBlock)
		.Text(FText::FromString(RollbackTitle))
		.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		.ColorAndOpacity(RollbackColor)
	];
	if (!LastAuthoringFailure.Rollback.Message.IsEmpty())
	{
		Body->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 4.0f)
		[
			SNew(STextBlock)
			.Text(FText::FromString(LastAuthoringFailure.Rollback.Message))
			.AutoWrapText(true)
			.ColorAndOpacity(FLinearColor(0.64f, 0.67f, 0.72f))
		];
	}
	if (LastAuthoringFailure.ReloadTransaction.IsValid())
	{
		Body->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 2.0f)
		[
			SNew(STextBlock)
			.Text(FText::FromString(
				LastAuthoringFailure.ReloadTransaction.ToDisplayString()))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
			.AutoWrapText(true)
			.ColorAndOpacity(FLinearColor(0.42f, 0.72f, 0.96f))
		];
		for (const FFinareReloadPhaseResult& Phase :
		     LastAuthoringFailure.ReloadTransaction.Phases)
		{
			Body->AddSlot().AutoHeight().Padding(8.0f, 1.0f, 0.0f, 1.0f)
			[
				SNew(STextBlock)
				.Text(FText::FromString(FString::Printf(
					TEXT("%s  ·  %s  ·  %lld us  ·  %s"),
					*Phase.Name,
					*Phase.State,
					Phase.DurationMicroseconds,
					*Phase.Detail)))
				.AutoWrapText(true)
				.ColorAndOpacity(FLinearColor(0.68f, 0.71f, 0.76f))
			];
		}
	}
	if (!LastAuthoringFailure.QuickFixes.IsEmpty())
	{
		const TSharedRef<SHorizontalBox> Fixes = SNew(SHorizontalBox);
		for (const FFinareAuthoringQuickFix& Fix : LastAuthoringFailure.QuickFixes)
		{
			Fixes->AddSlot().AutoWidth().Padding(0.0f, 3.0f, 6.0f, 4.0f)
			[
				SNew(SButton)
				.Text(FText::FromString(Fix.Label))
				.OnClicked(this, &SFinareAuthoringSurface::HandleQuickFix, Fix.Kind)
			];
		}
		Body->AddSlot().AutoHeight()[Fixes];
	}

	for (const FFinareAuthoringDiagnostic& Diagnostic :
	     LastAuthoringFailure.Diagnostics)
	{
		const bool bError = Diagnostic.Severity == TEXT("error");
		Body->AddSlot().AutoHeight().Padding(0.0f, 3.0f)
		[
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("Brushes.Panel")))
			.Padding(6.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.0f).VAlign(VAlign_Center)
				[
					SNew(STextBlock)
					.Text(FText::FromString(Diagnostic.ToDisplayString()))
					.AutoWrapText(true)
					.ColorAndOpacity(bError
						                 ? FLinearColor(0.96f, 0.42f, 0.35f)
						                 : FLinearColor(0.95f, 0.70f, 0.24f))
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(8.0f, 0.0f, 4.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("OpenDiagnosticSource", "Open Source"))
					.IsEnabled(Diagnostic.Span.FilePath != TEXT("<unknown>"))
					.OnClicked(this, &SFinareAuthoringSurface::HandleOpenSource, Diagnostic.Span)
				]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.Text(LOCTEXT("CopyDiagnostic", "Copy"))
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleCopyDiagnostic,
						Diagnostic)
				]
			]
		];
	}

	DiagnosticsBox->AddSlot().AutoHeight()
	[
		SNew(SBox)
		.MaxDesiredHeight(220.0f)
		[
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
			.Padding(8.0f)
			[
				SNew(SScrollBox)
				+ SScrollBox::Slot()
				[
					Body
				]
			]
		]
	];
}

FReply SFinareAuthoringSurface::HandleCopyDiagnostic(
	FFinareAuthoringDiagnostic Diagnostic)
{
	const FString Text = Diagnostic.ToDisplayString();
	FPlatformApplicationMisc::ClipboardCopy(*Text);
	SetStatus(FString::Printf(TEXT("Copied diagnostic %s."), *Diagnostic.Code), false);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleQuickFix(FString Kind)
{
	if (Kind == TEXT("openFirstDiagnostic"))
	{
		const FFinareAuthoringDiagnostic* Diagnostic =
			LastAuthoringFailure.Diagnostics.FindByPredicate([](
				const FFinareAuthoringDiagnostic& Candidate)
				{
					return Candidate.Span.IsValid();
				});
		return Diagnostic != nullptr
			       ? HandleOpenSource(Diagnostic->Span)
			       : FReply::Handled();
	}
	if (Kind == TEXT("refreshWorkspace"))
	{
		RequestWorkspaceRefresh();
		return FReply::Handled();
	}
	SetStatus(FString::Printf(TEXT("Unsupported authoring quick fix '%s'."), *Kind), true);
	return FReply::Handled();
}

void SFinareAuthoringSurface::HandleDailyBuildCompleted(const FFinareDailyBuildResult& Result)
{
	if (Result.IsSuccess() && (bHasCanonicalSourceSnapshot || LoadedScriptAsset.IsValid()))
	{
		RequestWorkspaceRefresh(true);
	}
}

void SFinareAuthoringSurface::RequestWorkspaceRefresh(bool bReadDisk, bool bHasFreshRevision)
{
	if (bNativePublicationReservationPending)
	{
		SetStatus(
			TEXT(
				"A native publication reservation is awaiting a verified finish or cancel; resolve it before refreshing the workspace."),
			true);
		return;
	}
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("Wait for the active Workbench request before refreshing."), true);
		return;
	}
	if (bCodeEditDirty)
	{
		SetStatus(
			TEXT("Apply or revert the unsaved C# buffer before refreshing the canonical workspace."),
			true);
		return;
	}
	// Refresh is also the recovery action for a stale client. Fetch the latest
	// revision before importing disk inputs; a concurrent commit still conflicts.
	const bool bImportDisk = bReadDisk && bHasFreshRevision;
	const FString ServerUrl = ServerUrlTextBox.IsValid() ? ServerUrlTextBox->GetText().ToString() : FString();
	const FString Url = bImportDisk
		                    ? FFinareAuthoringWorkbench::BuildRefreshUrl(ServerUrl)
		                    : FFinareAuthoringWorkbench::BuildWorkspaceUrl(ServerUrl);
	if (Url.IsEmpty())
	{
		SetStatus(TEXT("Enter a Workbench loopback URL before refreshing."), true);
		return;
	}

	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request =
		FHttpModule::Get().CreateRequest();
	Request->SetURL(Url);
	Request->SetVerb(bImportDisk ? TEXT("POST") : TEXT("GET"));
	if (bImportDisk)
	{
		Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
		Request->SetHeader(TEXT("X-Finare-Transaction-Id"), FGuid::NewGuid().ToString(EGuidFormats::Digits));
		if (bHasCanonicalSourceSnapshot)
		{
			Request->SetHeader(TEXT("X-Finare-Baseline-Revision"), LexToString(WorkbenchRevision));
		}
		Request->SetContentAsString(TEXT("{}"));
	}
	const TWeakPtr<SFinareAuthoringSurface> WeakThis = SharedThis(this);
	Request->OnProcessRequestComplete().BindLambda(
		[WeakThis, bFetchBeforeImport = bReadDisk && !bHasFreshRevision](
		FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
		{
			if (const TSharedPtr<SFinareAuthoringSurface> Pinned = WeakThis.Pin())
			{
				const bool bAdopted = Pinned->HandleWorkspaceRefreshResponse(
					bSucceeded && Response.IsValid(),
					Response.IsValid() ? Response->GetResponseCode() : 0,
					Response.IsValid() ? Response->GetContentAsString() : FString());
				if (bFetchBeforeImport && bAdopted)
				{
					Pinned->RequestWorkspaceRefresh(true, true);
				}
			}
		});
	bPatchRequestPending = true;
	SetStatus(bReadDisk
		          ? TEXT("Reading current project sources and refreshing the Authoring Surface ...")
		          : TEXT("Refreshing the Authoring Surface from the Workbench ..."), false);
	if (!Request->ProcessRequest())
	{
		bPatchRequestPending = false;
		SetStatus(TEXT("The Workbench refresh request could not be started."), true);
	}
}

bool SFinareAuthoringSurface::HandleWorkspaceRefreshResponse(
	bool bRequestSucceeded,
	int32 StatusCode,
	FString ResponseJson)
{
	bPatchRequestPending = false;
	if (!bRequestSucceeded || StatusCode < 200 || StatusCode >= 300)
	{
		HandleAuthoringFailure(
			bRequestSucceeded,
			StatusCode,
			ResponseJson,
			TEXT("The Workbench refresh request failed; verify that the authoring host is running."));
		return false;
	}
	FFinareAuthoringWorkspaceResult Workspace;
	FString Error;
	if (!FFinareAuthoringWorkbench::ParseWorkspaceResponse(
		ResponseJson,
		Workspace,
		Error))
	{
		SetStatus(MoveTemp(Error), true);
		return false;
	}
	const int64 Revision = Workspace.Revision;
	const int64 Generation = Workspace.Generation;
	AdoptWorkspace(MoveTemp(Workspace));
	bHasAuthoringFailure = false;
	RebuildDiagnosticsPanel();
	SetStatus(
		FString::Printf(
			TEXT("Refreshed Authoring Surface at revision %lld / generation %lld."),
			Revision,
			Generation),
		false);
	return true;
}

FFinareReloadBatchResult
SFinareAuthoringSurface::ReloadAllScriptClassComponents(
	const FString& ReloadTransactionJson)
{
	TArray<TWeakObjectPtr<UFinareAsset>> Assets;
	for (TObjectIterator<UFinareComponent> It; It; ++It)
	{
		UFinareComponent* Component = *It;
		UFinareAsset* Asset = Component != nullptr ? Component->ScriptAsset.Get() : nullptr;
		if (!IsValid(Component) || Component->IsTemplate() ||
			Component->GetWorld() == nullptr || Asset == nullptr)
		{
			continue;
		}
		Assets.AddUnique(Asset);
	}
	Assets.Sort([](
		const TWeakObjectPtr<UFinareAsset>& Left,
		const TWeakObjectPtr<UFinareAsset>& Right)
		{
			const UFinareAsset* LeftAsset = Left.Get();
			const UFinareAsset* RightAsset = Right.Get();
			return LeftAsset != nullptr && RightAsset != nullptr
				       ? LeftAsset->GetPathName() < RightAsset->GetPathName()
				       : LeftAsset != nullptr;
		});

	FFinareReloadBatchResult Aggregate;
	for (const TWeakObjectPtr<UFinareAsset>& WeakAsset : Assets)
	{
		const UFinareAsset* Asset = WeakAsset.Get();
		if (Asset == nullptr)
		{
			continue;
		}
		if (Asset->ScriptClassKind == EFinareClassKind::BlueprintFunctionLibrary)
		{
			continue;
		}
		const FFinareReloadBatchResult Batch =
			ReloadMatchingComponentsForAsset(Asset, FString(), ReloadTransactionJson);
		Aggregate.Matched += Batch.Matched;
		Aggregate.Reloaded += Batch.Reloaded;
		Aggregate.Failed += Batch.Failed;
		Aggregate.PreflightRejected += Batch.PreflightRejected;
		Aggregate.Prepared += Batch.Prepared;
		Aggregate.PrepareRejected += Batch.PrepareRejected;
		Aggregate.Committed += Batch.Committed;
		Aggregate.CommitRejected += Batch.CommitRejected;
		Aggregate.Aborted += Batch.Aborted;
		Aggregate.FinalizeFailed += Batch.FinalizeFailed;
		if (!Batch.ObservedReloadTransactionJson.IsEmpty())
		{
			Aggregate.ObservedReloadTransactionJson =
				Batch.ObservedReloadTransactionJson;
		}
	}

	TArray<TWeakObjectPtr<UFinareAsset>> StaticAssets;
	for (TObjectIterator<UFinareAsset> It; It; ++It)
	{
		UFinareAsset* Asset = *It;
		if (IsValid(Asset) &&
			!Asset->IsTemplate() &&
			Asset->ScriptClassKind == EFinareClassKind::BlueprintFunctionLibrary)
		{
			StaticAssets.Add(Asset);
		}
	}
	StaticAssets.Sort([](
		const TWeakObjectPtr<UFinareAsset>& Left,
		const TWeakObjectPtr<UFinareAsset>& Right)
		{
			const UFinareAsset* LeftAsset = Left.Get();
			const UFinareAsset* RightAsset = Right.Get();
			return LeftAsset != nullptr && RightAsset != nullptr
				       ? LeftAsset->GetPathName() < RightAsset->GetPathName()
				       : LeftAsset != nullptr;
		});
	TArray<TPair<FString, FString>> StaticKeys;
	for (const TWeakObjectPtr<UFinareAsset>& WeakAsset : StaticAssets)
	{
		const UFinareAsset* Asset = WeakAsset.Get();
		if (Asset == nullptr)
		{
			continue;
		}
		FString AssemblyPath;
		FString TypeName;
		FString ResolveError;
		const bool bResolved = Asset->ResolveRuntimeConfiguration(
			true,
			AssemblyPath,
			TypeName,
			ResolveError);
		const FString ResolvedPath = bResolved ? AssemblyPath : Asset->GetPathName();
		const FString ResolvedType = bResolved ? TypeName : FString();
		const bool bDuplicate = StaticKeys.ContainsByPredicate(
			[&ResolvedPath, &ResolvedType](const TPair<FString, FString>& Existing)
			{
				return Existing.Key.Equals(ResolvedPath, ESearchCase::CaseSensitive) &&
					Existing.Value.Equals(ResolvedType, ESearchCase::CaseSensitive);
			});
		if (bDuplicate)
		{
			continue;
		}
		StaticKeys.Add({ResolvedPath, ResolvedType});
		const FFinareReloadBatchResult Batch =
			ReloadStaticLibraryForAsset(Asset, ReloadTransactionJson);
		Aggregate.Matched += Batch.Matched;
		Aggregate.Reloaded += Batch.Reloaded;
		Aggregate.Failed += Batch.Failed;
		Aggregate.PreflightRejected += Batch.PreflightRejected;
		Aggregate.Prepared += Batch.Prepared;
		Aggregate.PrepareRejected += Batch.PrepareRejected;
		Aggregate.Committed += Batch.Committed;
		Aggregate.CommitRejected += Batch.CommitRejected;
		Aggregate.Aborted += Batch.Aborted;
		Aggregate.FinalizeFailed += Batch.FinalizeFailed;
		if (!Batch.ObservedReloadTransactionJson.IsEmpty())
		{
			Aggregate.ObservedReloadTransactionJson =
				Batch.ObservedReloadTransactionJson;
		}
	}
	return Aggregate;
}

FFinareReloadBatchResult
SFinareAuthoringSurface::ReloadMatchingComponentsForAsset(
	const UFinareAsset* PreferredAsset,
	const FString& LoadedSurfacePath,
	const FString& ReloadTransactionJson)
{
	FFinareReloadBatchResult Result;
	TArray<TWeakObjectPtr<UFinareComponent>> MatchingComponents;
	for (TObjectIterator<UFinareComponent> It; It; ++It)
	{
		UFinareComponent* Component = *It;
		if (!IsValid(Component) || Component->IsTemplate() || Component->GetWorld() == nullptr)
		{
			continue;
		}

		const UFinareAsset* ComponentAsset = Component->ScriptAsset;
		const bool bMatchesSelectedAsset = PreferredAsset != nullptr &&
		(ComponentAsset == PreferredAsset ||
			Component->ActiveScriptAssetPath == PreferredAsset->GetPathName());
		bool bMatchesSurfacePath = false;
		if (PreferredAsset == nullptr && ComponentAsset != nullptr && !LoadedSurfacePath.IsEmpty())
		{
			bMatchesSurfacePath =
				FFinareAuthoringSurfaceReader::ResolveConfiguredPath(
					ComponentAsset->ResolveAuthoringSurfacePath()) == LoadedSurfacePath;
		}
		if (!bMatchesSelectedAsset && !bMatchesSurfacePath)
		{
			continue;
		}

		MatchingComponents.Add(Component);
	}

	MatchingComponents.Sort([](
		const TWeakObjectPtr<UFinareComponent>& Left,
		const TWeakObjectPtr<UFinareComponent>& Right)
		{
			const UFinareComponent* LeftComponent = Left.Get();
			const UFinareComponent* RightComponent = Right.Get();
			return LeftComponent != nullptr && RightComponent != nullptr
				       ? LeftComponent->GetPathName() < RightComponent->GetPathName()
				       : LeftComponent != nullptr;
		});
	Result.Matched = MatchingComponents.Num();

	TArray<FString> PreflightTransactions;
	PreflightTransactions.SetNum(MatchingComponents.Num());
	for (int32 Index = 0; Index < MatchingComponents.Num(); ++Index)
	{
		UFinareComponent* Component = MatchingComponents[Index].Get();
		if (Component == nullptr ||
			!Component->PreflightScriptReloadWithTransaction(
				ReloadTransactionJson,
				PreflightTransactions[Index]))
		{
			++Result.PreflightRejected;
			if (Result.ObservedReloadTransactionJson.IsEmpty() && Component != nullptr)
			{
				Result.ObservedReloadTransactionJson = PreflightTransactions[Index];
			}
		}
	}
	if (Result.PreflightRejected > 0)
	{
		// The batch barrier deliberately counts every match as failed because
		// none was allowed to cross into runtime mutation.
		Result.Failed = Result.Matched;
		return Result;
	}

	auto AbortPreparedBatch = [&MatchingComponents, &Result](const FString& Reason)
	{
		for (const TWeakObjectPtr<UFinareComponent>& WeakComponent :
		     MatchingComponents)
		{
			UFinareComponent* Component = WeakComponent.Get();
			if (Component == nullptr || !Component->HasPreparedScriptReload())
			{
				continue;
			}
			Component->AbortPreparedScriptReload(Reason);
			++Result.Aborted;
			if (!Component->LastReloadTransaction.IsEmpty())
			{
				Result.ObservedReloadTransactionJson =
					Component->LastReloadTransaction;
			}
		}
	};

	for (int32 Index = 0; Index < MatchingComponents.Num(); ++Index)
	{
		UFinareComponent* Component = MatchingComponents[Index].Get();
		if (Component == nullptr ||
			!Component->PrepareScriptReloadWithTransaction(
				PreflightTransactions[Index]))
		{
			++Result.PrepareRejected;
			if (Component != nullptr && !Component->LastReloadTransaction.IsEmpty())
			{
				Result.ObservedReloadTransactionJson =
					Component->LastReloadTransaction;
			}
			AbortPreparedBatch(
				TEXT(
					"The all-instance reload batch aborted because another candidate failed during managed load, start, or event rebinding."));
			Result.Failed = Result.Matched;
			return Result;
		}
		++Result.Prepared;
	}

	for (const TWeakObjectPtr<UFinareComponent>& WeakComponent :
	     MatchingComponents)
	{
		UFinareComponent* Component = WeakComponent.Get();
		if (Component == nullptr || !Component->CommitPreparedScriptReload())
		{
			++Result.CommitRejected;
			if (Component != nullptr && !Component->LastReloadTransaction.IsEmpty())
			{
				Result.ObservedReloadTransactionJson =
					Component->LastReloadTransaction;
			}
			AbortPreparedBatch(
				TEXT(
					"The all-instance reload batch rolled back because a prepared candidate could not publish its generation pointer."));
			Result.Failed = Result.Matched;
			Result.Committed = 0;
			return Result;
		}
		++Result.Committed;
	}

	for (const TWeakObjectPtr<UFinareComponent>& WeakComponent :
	     MatchingComponents)
	{
		UFinareComponent* Component = WeakComponent.Get();
		if (Component == nullptr || !Component->FinalizePreparedScriptReload())
		{
			++Result.FinalizeFailed;
			++Result.Failed;
		}
		else
		{
			++Result.Reloaded;
		}
		if (Component != nullptr && !Component->LastReloadTransaction.IsEmpty())
		{
			Result.ObservedReloadTransactionJson = Component->LastReloadTransaction;
		}
	}

	if (PreferredAsset != nullptr &&
		PreferredAsset->ScriptClassKind == EFinareClassKind::BlueprintFunctionLibrary)
	{
		const FFinareReloadBatchResult StaticResult =
			ReloadStaticLibraryForAsset(PreferredAsset, ReloadTransactionJson);
		Result.Matched += StaticResult.Matched;
		Result.Reloaded += StaticResult.Reloaded;
		Result.Failed += StaticResult.Failed;
		Result.PreflightRejected += StaticResult.PreflightRejected;
		Result.Prepared += StaticResult.Prepared;
		Result.PrepareRejected += StaticResult.PrepareRejected;
		Result.Committed += StaticResult.Committed;
		Result.CommitRejected += StaticResult.CommitRejected;
		Result.Aborted += StaticResult.Aborted;
		Result.FinalizeFailed += StaticResult.FinalizeFailed;
		if (!StaticResult.ObservedReloadTransactionJson.IsEmpty())
		{
			Result.ObservedReloadTransactionJson =
				StaticResult.ObservedReloadTransactionJson;
		}
	}
	return Result;
}

void SFinareAuthoringSurface::ReloadMatchingComponents(
	const FString& ReloadTransactionJson,
	int32& OutMatched,
	int32& OutReloaded,
	int32& OutFailed,
	FString& OutObservedReloadTransactionJson)
{
	const FFinareReloadBatchResult Result = ReloadMatchingComponentsForAsset(
		LoadedScriptAsset.Get(),
		LoadedSurfacePath,
		ReloadTransactionJson);
	OutMatched = Result.Matched;
	OutReloaded = Result.Reloaded;
	OutFailed = Result.Failed;
	OutObservedReloadTransactionJson = Result.ObservedReloadTransactionJson;
}

EActiveTimerReturnType SFinareAuthoringSurface::HandleAutoReload(
	double CurrentTime,
	float DeltaTime)
{
	if (bPendingGraphFocus && GraphEditor.IsValid())
	{
		GraphEditor->ZoomToFit(false);
		bPendingGraphFocus = false;
	}
	if (bAutoReload && !LoadedSurfacePath.IsEmpty())
	{
		const FDateTime Timestamp = IFileManager::Get().GetTimeStamp(*LoadedSurfacePath);
		if (Timestamp != FDateTime::MinValue() && Timestamp != LoadedSurfaceTimestamp)
		{
			LoadSurface(LoadedSurfacePath);
		}
	}
	PersistCanvasView(true);
	PersistCurrentLayout(true);
	return EActiveTimerReturnType::Continue;
}

TSharedRef<ITableRow> SFinareAuthoringSurface::GenerateTreeRow(
	FFinareSurfaceTreeItemPtr Item,
	const TSharedRef<STableViewBase>& OwnerTable) const
{
	return SNew(STableRow<FFinareSurfaceTreeItemPtr>, OwnerTable)
		.Padding(FMargin(2.0f, 3.0f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			.Padding(0.0f, 0.0f, 7.0f, 0.0f)
			[
				SNew(SBorder)
				.BorderImage(FAppStyle::GetBrush(TEXT("WhiteBrush")))
				.BorderBackgroundColor(GetKindColor(Item->Kind))
				.Padding(FMargin(3.0f, 7.0f))
			]
			+ SHorizontalBox::Slot()
			.FillWidth(1.0f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot()
				.AutoHeight()
				[
					SNew(STextBlock)
					.Text(FText::FromString(Item->Label))
					.HighlightText(PathTextBox.IsValid() ? PathTextBox->GetText() : FText::GetEmpty())
				]
				+ SVerticalBox::Slot()
				.AutoHeight()
				[
					SNew(STextBlock)
					.Text(FText::FromString(Item->Detail))
					.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
					.ColorAndOpacity(FLinearColor(0.52f, 0.55f, 0.6f))
				]
			]
		];
}

void SFinareAuthoringSurface::GetTreeChildren(
	FFinareSurfaceTreeItemPtr Item,
	TArray<FFinareSurfaceTreeItemPtr>& OutChildren) const
{
	if (Item.IsValid())
	{
		OutChildren.Append(Item->Children);
	}
}

void SFinareAuthoringSurface::HandleSelectionChanged(
	FFinareSurfaceTreeItemPtr Item,
	ESelectInfo::Type SelectInfo)
{
	if (Item.IsValid() && Item->Kind == EFinareSurfaceTreeItemKind::Function)
	{
		RebuildGraph(Item);
	}
	else if (Item.IsValid() && !ActiveFunctionId.IsEmpty())
	{
		const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
		if (Function != nullptr)
		{
			const FString PreviousBlockRegionId = ActiveBlockRegionId;
			for (const FFinareAuthoringRegion& Region : Function->Regions)
			{
				if (Region.Id == Item->SemanticId ||
					Region.DirectStatementIds.Contains(Item->SemanticId))
				{
					ActiveBlockRegionId = Region.Id;
					if (AuthoringGraph.IsValid())
					{
						AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
					}
					break;
				}
			}
			if (PreviousBlockRegionId != ActiveBlockRegionId)
			{
				RebuildNodePalette(*Function);
			}
		}
	}
	RebuildInspector(Item);
}

void SFinareAuthoringSurface::HandleTreeDoubleClick(
	FFinareSurfaceTreeItemPtr Item)
{
	if (Item.IsValid() && Item->Span.IsValid())
	{
		HandleOpenSource(Item->Span);
	}
}

void SFinareAuthoringSurface::HandleGraphSelectionChanged(
	const FGraphPanelSelectionSet& Selection)
{
	for (UObject* Object : Selection)
	{
		if (const UFinareEdGraphNode* Node = Cast<UFinareEdGraphNode>(Object))
		{
			if (const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId))
			{
				const FString PreviousBlockRegionId = ActiveBlockRegionId;
				const FFinareAuthoringAction* Action = Function->FindAction(Node->SemanticId);
				for (const FFinareAuthoringRegion& Region : Function->Regions)
				{
					if (Region.DirectStatementIds.Contains(Node->SemanticId) ||
						(Action != nullptr && Region.Id == Action->RegionId &&
							(Region.Kind == TEXT("block") || Region.Kind == TEXT("then") ||
								Region.Kind == TEXT("else") || Region.Kind == TEXT("loopBody") ||
								Region.Kind == TEXT("switchSection"))))
					{
						ActiveBlockRegionId = Region.Id;
						if (AuthoringGraph.IsValid())
						{
							AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
						}
						break;
					}
				}
				if (PreviousBlockRegionId != ActiveBlockRegionId)
				{
					RebuildNodePalette(*Function);
				}
			}
			RebuildInspector(FindSurfaceItem(Node->SemanticId));
			return;
		}
	}
}

void SFinareAuthoringSurface::HandleGraphNodeDoubleClicked(UEdGraphNode* Node)
{
	if (const UFinareEdGraphNode* FinareNode = Cast<UFinareEdGraphNode>(Node))
	{
		const FFinareSurfaceTreeItemPtr Item = FindSurfaceItem(FinareNode->SemanticId);
		if (Item.IsValid() && Item->Span.IsValid())
		{
			HandleOpenSource(Item->Span);
		}
	}
}

TSharedRef<ITableRow> SFinareAuthoringSurface::GenerateNodePaletteRow(
	FFinareNodePaletteItemPtr Item,
	const TSharedRef<STableViewBase>& OwnerTable)
{
	check(Item.IsValid());
	return SNew(STableRow<FFinareNodePaletteItemPtr>, OwnerTable)
		.Padding(FMargin(2.0f, 1.0f))
		.ToolTipText(FText::FromString(Item->Action.Tooltip))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			.Padding(0.0f, 0.0f, 3.0f, 0.0f)
			[
				SNew(SButton)
				.Text_Lambda([Item]()
				{
					return FText::FromString(Item->bFavorite ? TEXT("\u2605") : TEXT("\u2606"));
				})
				.ToolTipText_Lambda([Item]()
				{
					return Item->bFavorite
						       ? LOCTEXT("RemovePaletteFavorite", "Remove from Favorites")
						       : LOCTEXT("AddPaletteFavorite", "Add to Favorites");
				})
				.ContentPadding(FMargin(3.0f, 0.0f))
				.OnClicked(this, &SFinareAuthoringSurface::HandleToggleNodePaletteFavorite, Item)
			]
			+ SHorizontalBox::Slot()
			.FillWidth(1.0f)
			.VAlign(VAlign_Center)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot()
				.AutoHeight()
				[
					SNew(STextBlock)
					.Text(FText::FromString(Item->Action.Title))
					.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
					.ColorAndOpacity(Item->Action.bEnabled
						                 ? FSlateColor::UseForeground()
						                 : FSlateColor(FLinearColor(0.42f, 0.44f, 0.48f)))
					.HighlightText_Lambda([this]()
					{
						return FText::FromString(NodePaletteSearchText);
					})
				]
				+ SVerticalBox::Slot()
				.AutoHeight()
				[
					SNew(STextBlock)
					.Text(FText::FromString(Item->Action.Category))
					.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
					.ColorAndOpacity(FLinearColor(0.46f, 0.50f, 0.55f))
				]
			]
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			.Padding(3.0f, 0.0f)
			[
				SNew(STextBlock)
				.Text_Lambda([Item]()
				{
					if (!Item->Action.bEnabled)
					{
						return LOCTEXT("UnavailablePaletteAction", "UNAVAILABLE");
					}
					if (Item->Action.bDeprecated)
					{
						return LOCTEXT("DeprecatedPaletteAction", "DEPRECATED");
					}
					return Item->Action.bLatent
						       ? LOCTEXT("LatentPaletteAction", "LATENT")
						       : FText::GetEmpty();
				})
				.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
				.ColorAndOpacity(Item->Action.bDeprecated
					                 ? FLinearColor(0.90f, 0.52f, 0.20f)
					                 : FLinearColor(0.48f, 0.62f, 0.82f))
			]
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			.Padding(3.0f, 0.0f)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("RecentPaletteAction", "RECENT"))
				.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
				.ColorAndOpacity(FLinearColor(0.34f, 0.62f, 0.86f))
				.Visibility(Item->RecentRank == INDEX_NONE
					            ? EVisibility::Collapsed
					            : EVisibility::Visible)
			]
			+ SHorizontalBox::Slot()
			.AutoWidth()
			.VAlign(VAlign_Center)
			[
				SNew(SButton)
				.Text(LOCTEXT("InsertPaletteAction", "+"))
				.ToolTipText(LOCTEXT(
					"InsertPaletteActionTip",
					"Submit this node through the active Workbench transaction."))
				.IsEnabled_Lambda([this, Item]()
				{
					return !bPatchRequestPending && Item->Action.bEnabled;
				})
				.ContentPadding(FMargin(5.0f, 0.0f))
				.OnClicked(this, &SFinareAuthoringSurface::HandleInsertNodePaletteItem, Item)
			]
		];
}

void SFinareAuthoringSurface::HandleNodePaletteSearchChanged(const FText& Text)
{
	NodePaletteSearchText = Text.ToString();
	RefreshNodePaletteItems();
}

void SFinareAuthoringSurface::HandleNodePaletteSearchCommitted(
	const FText& Text,
	ETextCommit::Type CommitType)
{
	NodePaletteSearchText = Text.ToString();
	if (CommitType == ETextCommit::OnEnter && !FilteredNodePaletteItems.IsEmpty())
	{
		HandleInsertNodePaletteItem(FilteredNodePaletteItems[0]);
	}
}

void SFinareAuthoringSurface::HandleNodePaletteDoubleClick(
	FFinareNodePaletteItemPtr Item)
{
	HandleInsertNodePaletteItem(MoveTemp(Item));
}

FReply SFinareAuthoringSurface::HandleInsertNodePaletteItem(
	FFinareNodePaletteItemPtr Item)
{
	if (!Item.IsValid())
	{
		return FReply::Handled();
	}
	if (bPatchRequestPending)
	{
		SetStatus(TEXT("Wait for the active authoring transaction before placing another node."), true);
		return FReply::Handled();
	}
	if (!Item->Action.bEnabled)
	{
		SetStatus(
			Item->Action.UnavailableReason.IsEmpty()
				? TEXT("This generated API node is unavailable in the active function context.")
				: Item->Action.UnavailableReason,
			true);
		return FReply::Handled();
	}
	HandleNodeCreateRequested(Item->Action.ActionId, FVector2D::ZeroVector);
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleToggleNodePaletteFavorite(
	FFinareNodePaletteItemPtr Item)
{
	if (Item.IsValid())
	{
		const bool bFavorite = FFinareNodePaletteModel::ToggleFavorite(
			FavoriteNodePaletteActionIds,
			Item->Action.ActionId);
		SaveNodePalettePreferences();
		RefreshNodePaletteItems();
		SetStatus(
			bFavorite
				? FString::Printf(
					TEXT("Added '%s' to the node Palette favorites."),
					*Item->Action.Title)
				: FString::Printf(
					TEXT("Removed '%s' from the node Palette favorites."),
					*Item->Action.Title),
			false);
	}
	return FReply::Handled();
}

void SFinareAuthoringSurface::RebuildTree()
{
	const FString PreferredFunctionId = ActiveFunctionId;
	FFinareSurfaceTreeItemPtr FirstFunctionItem;
	RootItems.Reset();
	SurfaceItemsById.Reset();
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		FFinareSurfaceTreeItemPtr AssetItem = MakeShared<FFinareSurfaceTreeItem>();
		AssetItem->Kind = EFinareSurfaceTreeItemKind::Asset;
		AssetItem->Label = Asset.AssetPath;
		AssetItem->Detail = Asset.QualifiedName;
		AssetItem->SemanticId = Asset.Id;
		AssetItem->Span = Asset.Span;
		SurfaceItemsById.Add(AssetItem->SemanticId, AssetItem);

		if (!Asset.States.IsEmpty())
		{
			FFinareSurfaceTreeItemPtr StateGroup = MakeShared<FFinareSurfaceTreeItem>();
			StateGroup->Kind = EFinareSurfaceTreeItemKind::Group;
			StateGroup->Label = FString::Printf(TEXT("State (%d)"), Asset.States.Num());
			StateGroup->Detail = TEXT("C#-owned declarations");
			for (const FFinareAuthoringState& State : Asset.States)
			{
				FFinareSurfaceTreeItemPtr StateItem = MakeShared<FFinareSurfaceTreeItem>();
				StateItem->Kind = EFinareSurfaceTreeItemKind::State;
				StateItem->Label = State.Name;
				StateItem->Detail = FString::Printf(
					TEXT("%s = %s%s"),
					*State.TypeDisplayName,
					*State.DefaultValue,
					State.Exposure.bInstanceEditable ? TEXT("  [Editable]") : TEXT(""));
				StateItem->SemanticId = State.Id;
				StateItem->Category = State.Exposure.Category;
				StateItem->Span = State.Span;
				if (const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(State.Id);
					Owner != nullptr && Owner->Id == Asset.Id)
				{
					SurfaceItemsById.Add(StateItem->SemanticId, StateItem);
				}
				StateGroup->Children.Add(MoveTemp(StateItem));
			}
			AssetItem->Children.Add(MoveTemp(StateGroup));
		}

		if (!Asset.Functions.IsEmpty())
		{
			FFinareSurfaceTreeItemPtr FunctionGroup = MakeShared<FFinareSurfaceTreeItem>();
			FunctionGroup->Kind = EFinareSurfaceTreeItemKind::Group;
			FunctionGroup->Label = FString::Printf(TEXT("Behavior (%d)"), Asset.Functions.Num());
			FunctionGroup->Detail = TEXT("Visual Profile functions");
			for (const FFinareAuthoringFunction& Function : Asset.Functions)
			{
				FFinareSurfaceTreeItemPtr FunctionItem = MakeShared<FFinareSurfaceTreeItem>();
				FunctionItem->Kind = EFinareSurfaceTreeItemKind::Function;
				FunctionItem->Label = Function.Name;
				FunctionItem->Detail = BuildFunctionSignature(Function);
				FunctionItem->SemanticId = Function.Id;
				const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(Function.Id);
				const bool bIndexFunction = Owner != nullptr && Owner->Id == Asset.Id;
				if (bIndexFunction)
				{
					SurfaceItemsById.Add(FunctionItem->SemanticId, FunctionItem);
					if (!FirstFunctionItem.IsValid())
					{
						FirstFunctionItem = FunctionItem;
					}
				}
				if (const FFinareAuthoringRegion* Root = Function.FindRegion(Function.RootRegionId))
				{
					FunctionItem->Span = Root->Span;
					AddRegionTree(Function, *Root, FunctionItem, bIndexFunction);
				}
				FunctionGroup->Children.Add(MoveTemp(FunctionItem));
			}
			AssetItem->Children.Add(MoveTemp(FunctionGroup));
		}
		RootItems.Add(MoveTemp(AssetItem));
	}

	TreeView->RequestTreeRefresh();
	TFunction<void(const FFinareSurfaceTreeItemPtr&)> ExpandAll;
	ExpandAll = [this, &ExpandAll](const FFinareSurfaceTreeItemPtr& Item)
	{
		TreeView->SetItemExpansion(Item, true);
		for (const FFinareSurfaceTreeItemPtr& Child : Item->Children)
		{
			ExpandAll(Child);
		}
	};
	for (const FFinareSurfaceTreeItemPtr& Root : RootItems)
	{
		ExpandAll(Root);
	}

	FFinareSurfaceTreeItemPtr ItemToSelect;
	if (!PreferredFunctionId.IsEmpty())
	{
		const FFinareSurfaceTreeItemPtr* Existing = SurfaceItemsById.Find(PreferredFunctionId);
		if (Existing != nullptr && (*Existing)->Kind == EFinareSurfaceTreeItemKind::Function)
		{
			ItemToSelect = *Existing;
		}
	}
	if (!ItemToSelect.IsValid())
	{
		ItemToSelect = FirstFunctionItem;
	}
	if (!ItemToSelect.IsValid() && !RootItems.IsEmpty())
	{
		ItemToSelect = RootItems[0];
	}
	if (ItemToSelect.IsValid())
	{
		TreeView->SetSelection(ItemToSelect);
	}
	else
	{
		RebuildInspector(nullptr);
	}
}

void SFinareAuthoringSurface::AddRegionTree(
	const FFinareAuthoringFunction& Function,
	const FFinareAuthoringRegion& Region,
	const FFinareSurfaceTreeItemPtr& Parent,
	bool bIndexItems)
{
	FFinareSurfaceTreeItemPtr RegionItem = MakeShared<FFinareSurfaceTreeItem>();
	RegionItem->Kind = EFinareSurfaceTreeItemKind::Region;
	RegionItem->Label = Region.Kind.ToUpper();
	RegionItem->Detail = Region.bImplicit ? TEXT("implicit region") : TEXT("structured region");
	RegionItem->SemanticId = Region.Id;
	RegionItem->Category = Region.Kind;
	RegionItem->Span = Region.Span;
	if (bIndexItems)
	{
		SurfaceItemsById.Add(RegionItem->SemanticId, RegionItem);
	}

	struct FOrderedChild
	{
		int32 Line = 0;
		bool bRegion = false;
		FString Id;
	};
	TArray<FOrderedChild> Ordered;
	for (const FString& ActionId : Region.DirectActionIds)
	{
		if (const FFinareAuthoringAction* Action = Function.FindAction(ActionId))
		{
			Ordered.Add({Action->Span.StartLine, false, ActionId});
		}
	}
	for (const FString& ChildId : Region.ChildRegionIds)
	{
		if (const FFinareAuthoringRegion* Child = Function.FindRegion(ChildId))
		{
			Ordered.Add({Child->Span.StartLine, true, ChildId});
		}
	}
	Ordered.Sort([](const FOrderedChild& Left, const FOrderedChild& Right)
	{
		if (Left.Line != Right.Line)
		{
			return Left.Line < Right.Line;
		}
		return Left.bRegion < Right.bRegion;
	});
	for (const FOrderedChild& Child : Ordered)
	{
		if (Child.bRegion)
		{
			if (const FFinareAuthoringRegion* ChildRegion = Function.FindRegion(Child.Id))
			{
				AddRegionTree(Function, *ChildRegion, RegionItem, bIndexItems);
			}
		}
		else if (const FFinareAuthoringAction* Action = Function.FindAction(Child.Id))
		{
			FFinareSurfaceTreeItemPtr ActionItem = MakeShared<FFinareSurfaceTreeItem>();
			ActionItem->Kind = EFinareSurfaceTreeItemKind::Action;
			ActionItem->Label = Action->Title;
			ActionItem->Detail = Action->Summary;
			ActionItem->SemanticId = Action->Id;
			ActionItem->Category = Action->Category;
			ActionItem->GraphKind = Action->GraphKind;
			ActionItem->Span = Action->Span;
			ActionItem->Edits = Action->EditCapabilities;
			if (bIndexItems)
			{
				SurfaceItemsById.Add(ActionItem->SemanticId, ActionItem);
			}
			RegionItem->Children.Add(MoveTemp(ActionItem));
		}
	}
	if (Parent->Kind == EFinareSurfaceTreeItemKind::Region)
	{
		Parent->Children.Add(MoveTemp(RegionItem));
	}
}

void SFinareAuthoringSurface::RebuildGraph(
	const FFinareSurfaceTreeItemPtr& FunctionItem)
{
	if (!AuthoringGraph.IsValid() || !FunctionItem.IsValid())
	{
		return;
	}

	PersistCanvasView(true);
	PersistCurrentLayout(true);

	if (GraphEditor.IsValid())
	{
		GraphEditor->ClearSelectionSet();
	}
	AuthoringGraph->Nodes.Reset();
	GraphNodesBySemanticId.Reset();
	PersistedCanvasLayoutFingerprint.Reset();
	PersistedCanvasViewFingerprint.Reset();
	ActiveFunctionId = FunctionItem->SemanticId;
	ActiveBlockRegionId.Reset();
	ActiveFunctionSignature = FunctionItem->Detail;
	ActiveGraphActionCount = 0;
	AuthoringGraph->PaletteActions.Reset();
	ReflectedPaletteActions.Reset();
	RefreshNodePaletteItems();

	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	if (DebugPanel.IsValid())
	{
		DebugPanel->SetExecutionContext(Document, ActiveFunctionId);
	}
	if (Function != nullptr)
	{
		FFinareGraphFunctionLayout Layout;
		FString LayoutError;
		if (!LoadedLayoutPath.IsEmpty() &&
			!FFinareGraphLayoutStore::LoadFunction(
				LoadedLayoutPath, Function->Id, Layout, LayoutError))
		{
			SetStatus(MoveTemp(LayoutError), true);
		}
		if (const FFinareAuthoringRegion* Root = Function->FindRegion(Function->RootRegionId))
		{
			for (const FString& ChildId : Root->ChildRegionIds)
			{
				const FFinareAuthoringRegion* Child = Function->FindRegion(ChildId);
				if (Child != nullptr && Child->Kind == TEXT("block"))
				{
					ActiveBlockRegionId = Child->Id;
					break;
				}
			}
		}
		AuthoringGraph->ActiveBlockRegionId = ActiveBlockRegionId;
		RebuildNodePalette(*Function);
		TSet<FString> VisibleActionIds;
		for (const FFinareAuthoringAction& Action : Function->Actions)
		{
			if (!FFinareGraphPresentation::ShouldInlineAction(Action, *Function))
			{
				VisibleActionIds.Add(Action.Id);
			}
		}

		TMultiMap<FString, FString> IncomingNodes;
		for (const FFinareAuthoringEdge& Edge : Function->Edges)
		{
			if (VisibleActionIds.Contains(Edge.FromNode) && VisibleActionIds.Contains(Edge.ToNode))
			{
				IncomingNodes.Add(Edge.ToNode, Edge.FromNode);
			}
		}

		TMap<FString, int32> CachedDepths;
		TSet<FString> Visiting;
		TFunction<int32(const FString&)> ComputeDepth;
		ComputeDepth = [&IncomingNodes, &CachedDepths, &Visiting, &ComputeDepth](
			const FString& NodeId) -> int32
			{
				if (const int32* Cached = CachedDepths.Find(NodeId))
				{
					return *Cached;
				}
				if (Visiting.Contains(NodeId))
				{
					return 0;
				}

				Visiting.Add(NodeId);
				int32 Depth = 0;
				TArray<FString> Sources;
				IncomingNodes.MultiFind(NodeId, Sources);
				for (const FString& SourceId : Sources)
				{
					Depth = FMath::Max(Depth, ComputeDepth(SourceId) + 1);
				}
				Visiting.Remove(NodeId);
				CachedDepths.Add(NodeId, Depth);
				return Depth;
			};

		TMap<int32, int32> RowsByDepth;
		for (const FFinareAuthoringAction& Action : Function->Actions)
		{
			if (!VisibleActionIds.Contains(Action.Id))
			{
				continue;
			}
			const int32 Depth = ComputeDepth(Action.Id);
			int32& Row = RowsByDepth.FindOrAdd(Depth);
			const FVector2D* SavedPosition = Layout.NodePositions.Find(Action.Id);
			AddGraphNode(
				FindSurfaceItem(Action.Id),
				Action,
				*Function,
				SavedPosition != nullptr ? FMath::RoundToInt(SavedPosition->X) : Depth * 330,
				SavedPosition != nullptr ? FMath::RoundToInt(SavedPosition->Y) : Row * 180);
			++Row;
		}

		for (const FFinareAuthoringEdge& Edge : Function->Edges)
		{
			const TWeakObjectPtr<UFinareEdGraphNode>* FromWeak =
				GraphNodesBySemanticId.Find(Edge.FromNode);
			const TWeakObjectPtr<UFinareEdGraphNode>* ToWeak =
				GraphNodesBySemanticId.Find(Edge.ToNode);
			UFinareEdGraphNode* FromNode = FromWeak != nullptr ? FromWeak->Get() : nullptr;
			UFinareEdGraphNode* ToNode = ToWeak != nullptr ? ToWeak->Get() : nullptr;
			UEdGraphPin* FromPin = FromNode != nullptr
				                       ? FromNode->FindPin(Edge.FromPort, EGPD_Output)
				                       : nullptr;
			UEdGraphPin* ToPin = ToNode != nullptr
				                     ? ToNode->FindPin(Edge.ToPort, EGPD_Input)
				                     : nullptr;
			if (FromPin != nullptr && ToPin != nullptr)
			{
				UEdGraphPin* PreviousOutput = FromPin;
				const FFinareGraphRoutingHintLayout* Route =
					Layout.RoutingHints.FindByPredicate(
						[&Edge](const FFinareGraphRoutingHintLayout& Candidate)
						{
							return Candidate.EdgeId == Edge.Id;
						});
				if (Route != nullptr)
				{
					for (int32 PointIndex = 0;
					     PointIndex < Route->ControlPoints.Num();
					     ++PointIndex)
					{
						const FVector2D& Point = Route->ControlPoints[PointIndex];
						UFinareEdGraphRerouteNode* Reroute =
							NewObject<UFinareEdGraphRerouteNode>(
								AuthoringGraph.Get(), NAME_None, RF_Transient);
						Reroute->CreateNewGuid();
						Reroute->EdgeId = Edge.Id;
						Reroute->PointIndex = PointIndex;
						Reroute->RoutedPinCategory = FromPin->PinType.PinCategory;
						Reroute->RoutedPinSubCategory = FromPin->PinType.PinSubCategory;
						Reroute->NodePosX = FMath::RoundToInt(Point.X);
						Reroute->NodePosY = FMath::RoundToInt(Point.Y);
						Reroute->AllocateDefaultPins();
						AuthoringGraph->AddNode(Reroute, false, false);
						PreviousOutput->MakeLinkTo(
							Reroute->FindPinChecked(TEXT("in"), EGPD_Input), false);
						PreviousOutput = Reroute->FindPinChecked(TEXT("out"), EGPD_Output);
					}
				}
				PreviousOutput->MakeLinkTo(ToPin, false);
			}
		}
		for (const FFinareGraphCommentLayout& SavedComment : Layout.CommentRegions)
		{
			UFinareEdGraphCommentNode* Comment =
				NewObject<UFinareEdGraphCommentNode>(
					AuthoringGraph.Get(), NAME_None, RF_Transient);
			Comment->CreateNewGuid();
			Comment->LayoutId = SavedComment.Id;
			Comment->NodeComment = SavedComment.Title;
			Comment->NodePosX = FMath::RoundToInt(SavedComment.Position.X);
			Comment->NodePosY = FMath::RoundToInt(SavedComment.Position.Y);
			Comment->NodeWidth = FMath::RoundToInt(SavedComment.Size.X);
			Comment->NodeHeight = FMath::RoundToInt(SavedComment.Size.Y);
			Comment->bLayoutCollapsed = SavedComment.bCollapsed;
			Comment->MoveMode = ECommentBoxMode::GroupMovement;
			Comment->CommentColor = FLinearColor(0.10f, 0.24f, 0.42f, 0.35f);
			for (const FString& MemberId : SavedComment.NodeIds)
			{
				if (const TWeakObjectPtr<UFinareEdGraphNode>* Member =
					GraphNodesBySemanticId.Find(MemberId))
				{
					if (Member->IsValid())
					{
						Comment->AddNodeUnderComment(Member->Get());
					}
				}
			}
			AuthoringGraph->AddNode(Comment, false, false);
		}
		ActiveGraphActionCount = GraphNodesBySemanticId.Num();
		PersistedCanvasLayoutFingerprint = BuildCanvasLayoutFingerprint();
	}

	AuthoringGraph->NotifyGraphChanged();
	if (GraphEditor.IsValid())
	{
		GraphEditor->NotifyGraphChanged();
		const bool bHasNodes = !AuthoringGraph->Nodes.IsEmpty();
		bPendingGraphFocus = bHasNodes && !RestoreCanvasView();
	}
}

UFinareEdGraphNode* SFinareAuthoringSurface::AddGraphNode(
	const FFinareSurfaceTreeItemPtr& Item,
	const FFinareAuthoringAction& Action,
	const FFinareAuthoringFunction& Function,
	int32 NodePosX,
	int32 NodePosY)
{
	if (!AuthoringGraph.IsValid() || !Item.IsValid())
	{
		return nullptr;
	}

	UFinareEdGraphNode* Node = NewObject<UFinareEdGraphNode>(
		AuthoringGraph.Get(),
		NAME_None,
		RF_Transient);
	Node->CreateNewGuid();
	Node->SemanticId = Item->SemanticId;
	Node->DisplayTitle = FFinareGraphPresentation::GetNodeTitle(Action);
	Node->Summary = FFinareGraphPresentation::GetNodeSubtitle(Action);
	Node->NodeKind = Item->Kind == EFinareSurfaceTreeItemKind::Region
		                 ? TEXT("region")
		                 : Item->GraphKind;
	Node->Category = Item->Category;
	Node->SourceDisplay = Item->Span.ToDisplayString();
	Node->AdvancedTooltip = FFinareGraphPresentation::GetNodeTooltip(Action);
	Node->DebugTargetId = Action.DebugTargetId;
	Node->bBreakpointEnabled = DebugPanel.IsValid() &&
		DebugPanel->IsBreakpointEnabled(Action.DebugTargetId);
	Node->DebugBreakpointState = DebugPanel.IsValid()
		                             ? DebugPanel->GetBreakpointState(Action.DebugTargetId)
		                             : TEXT("disabled");
	Node->bDebugPaused = DebugPanel.IsValid() &&
		DebugPanel->IsPausedNode(Action.DebugTargetId);
	Node->bDebugFaulted = DebugPanel.IsValid() &&
		DebugPanel->IsRuntimeFailureNode(Action.DebugTargetId);
	Node->bShowSummary = FFinareGraphPresentation::ShouldShowSummary(Action);
	Node->bCompactPresentation = FFinareGraphPresentation::IsCompactNode(Action);
	Node->bCanDelete = Action.GraphKind == TEXT("switchCase") ||
		Function.Regions.ContainsByPredicate([&Action](
			const FFinareAuthoringRegion& Region)
			{
				return Region.DirectStatementIds.Contains(Action.Id);
			});
	Node->NodePosX = NodePosX;
	Node->NodePosY = NodePosY;
	Node->AllocateDefaultPins();
	for (const FFinareAuthoringPort& Port : Action.Ports)
	{
		FEdGraphPinType PinType;
		PinType.PinCategory = Port.IsControl() ? TEXT("exec") : TEXT("data");
		PinType.PinSubCategory = Port.TypeDisplayName.IsEmpty()
			                         ? NAME_None
			                         : FName(*Port.TypeDisplayName);
		UEdGraphPin* Pin = Node->CreatePin(
			Port.IsInput() ? EGPD_Input : EGPD_Output,
			PinType,
			FName(*Port.Name));
		Pin->PinFriendlyName = FText::FromString(
			FFinareGraphPresentation::GetPinLabel(Action, Port, Function));
		Pin->PinToolTip = FFinareGraphPresentation::GetPinTooltip(
			Action,
			Port,
			Function);
		Pin->bHidden = FFinareGraphPresentation::ShouldHidePin(
			Action,
			Port,
			Function);
	}
	AuthoringGraph->AddNode(Node, false, false);
	GraphNodesBySemanticId.Add(Item->SemanticId, Node);
	return Node;
}

void SFinareAuthoringSurface::HandleBreakpointToggleRequested(
	const FString& DebugTargetId)
{
	if (DebugPanel.IsValid())
	{
		DebugPanel->ToggleBreakpoint(DebugTargetId);
		RefreshDebugNodeStates();
	}
}

void SFinareAuthoringSurface::HandleDebugNodeFocused(
	const FString& MethodId,
	const FString& NodeId)
{
	if (NodeId.IsEmpty())
	{
		return;
	}
	FString ActionId;
	FString FunctionId;
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			if (!MethodId.IsEmpty() && Function.Id != MethodId)
			{
				continue;
			}
			if (const FFinareAuthoringAction* Action = Function.Actions.FindByPredicate(
				[&NodeId](const FFinareAuthoringAction& Candidate)
				{
					return Candidate.DebugTargetId == NodeId;
				}))
			{
				FunctionId = Function.Id;
				ActionId = Action->Id;
				break;
			}
		}
		if (!ActionId.IsEmpty())
		{
			break;
		}
	}
	if (ActionId.IsEmpty())
	{
		return;
	}
	if (FunctionId != ActiveFunctionId && !SelectFunction(FunctionId))
	{
		return;
	}
	const TWeakObjectPtr<UFinareEdGraphNode>* Node =
		GraphNodesBySemanticId.Find(ActionId);
	if (GraphEditor.IsValid() && Node != nullptr && Node->IsValid())
	{
		GraphEditor->ClearSelectionSet();
		GraphEditor->SetNodeSelection(Node->Get(), true);
		GraphEditor->JumpToNode(Node->Get(), false);
	}
	RefreshDebugNodeStates();
}

void SFinareAuthoringSurface::RefreshDebugNodeStates()
{
	for (TPair<FString, TWeakObjectPtr<UFinareEdGraphNode>>& Entry :
	     GraphNodesBySemanticId)
	{
		if (UFinareEdGraphNode* Node = Entry.Value.Get())
		{
			Node->bBreakpointEnabled = DebugPanel.IsValid() &&
				DebugPanel->IsBreakpointEnabled(Node->DebugTargetId);
			Node->DebugBreakpointState = DebugPanel.IsValid()
				                             ? DebugPanel->GetBreakpointState(Node->DebugTargetId)
				                             : TEXT("disabled");
			Node->bDebugPaused = DebugPanel.IsValid() &&
				DebugPanel->IsPausedNode(Node->DebugTargetId);
			Node->bDebugFaulted = DebugPanel.IsValid() &&
				DebugPanel->IsRuntimeFailureNode(Node->DebugTargetId);
		}
	}
	if (AuthoringGraph.IsValid())
	{
		AuthoringGraph->NotifyGraphChanged();
	}
	if (GraphEditor.IsValid())
	{
		GraphEditor->NotifyGraphChanged();
	}
}

const FFinareAuthoringFunction* SFinareAuthoringSurface::FindFunction(
	const FString& FunctionId) const
{
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(FunctionId);
	if (Owner != nullptr)
	{
		for (const FFinareAuthoringFunction& Function : Owner->Functions)
		{
			if (Function.Id == FunctionId)
			{
				return &Function;
			}
		}
		return nullptr;
	}
	const FFinareAuthoringFunction* Match = nullptr;
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			if (Function.Id == FunctionId)
			{
				if (Match != nullptr)
				{
					return nullptr;
				}
				Match = &Function;
			}
		}
	}
	return Match;
}

const FFinareAuthoringAsset* SFinareAuthoringSurface::FindAsset(
	const FString& AssetId) const
{
	return Document.Assets.FindByPredicate([&AssetId](
		const FFinareAuthoringAsset& Asset)
		{
			return Asset.Id == AssetId;
		});
}

const FFinareAuthoringAsset* SFinareAuthoringSurface::FindOwningAsset(
	const FString& DeclarationId) const
{
	if (const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(DeclarationId))
	{
		return Owner;
	}
	const FFinareAuthoringAsset* Match = nullptr;
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		if (Asset.Id == DeclarationId ||
			Asset.States.ContainsByPredicate([&DeclarationId](const FFinareAuthoringState& State)
			{
				return State.Id == DeclarationId;
			}) ||
			Asset.Functions.ContainsByPredicate([&DeclarationId](const FFinareAuthoringFunction& Function)
			{
				return Function.Id == DeclarationId;
			}))
		{
			if (Match != nullptr)
			{
				return nullptr;
			}
			Match = &Asset;
		}
	}
	return Match;
}

const FFinareAuthoringState* SFinareAuthoringSurface::FindState(
	const FString& StateId) const
{
	const FFinareAuthoringAsset* Owner = Document.FindDeclarationOwnerAsset(StateId);
	if (Owner != nullptr)
	{
		return Owner->States.FindByPredicate(
			[&StateId](const FFinareAuthoringState& Candidate)
			{
				return Candidate.Id == StateId;
			});
	}
	const FFinareAuthoringState* Match = nullptr;
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		if (const FFinareAuthoringState* State = Asset.States.FindByPredicate(
			[&StateId](const FFinareAuthoringState& Candidate)
			{
				return Candidate.Id == StateId;
			}))
		{
			if (Match != nullptr)
			{
				return nullptr;
			}
			Match = State;
		}
	}
	return Match;
}

bool SFinareAuthoringSurface::IsLifecycleFunction(
	const FFinareAuthoringFunction& Function)
{
	return Function.Name == TEXT("OnBeginPlay") && Function.Parameters.IsEmpty() ||
		Function.Name == TEXT("OnTick") && Function.Parameters.Num() == 1 &&
		Function.Parameters[0].TypeMetadataName == TEXT("System.Single") ||
		Function.Name == TEXT("OnEndPlay") && Function.Parameters.IsEmpty();
}

bool SFinareAuthoringSurface::IsEventFunction(
	const FFinareAuthoringFunction& Function)
{
	return Function.Actions.ContainsByPredicate([](
		const FFinareAuthoringAction& Action)
		{
			return Action.GraphKind == TEXT("unrealEventEntry");
		});
}

bool SFinareAuthoringSurface::ParseFunctionParameters(
	const FString& Text,
	TArray<FFinareAuthoringParameter>& OutParameters,
	FString& OutError)
{
	OutParameters.Reset();
	FString Trimmed = Text;
	Trimmed.TrimStartAndEndInline();
	if (Trimmed.IsEmpty())
	{
		OutError.Reset();
		return true;
	}
	TArray<FString> Entries;
	Trimmed.ParseIntoArray(Entries, TEXT(","), true);
	TSet<FString> Names;
	for (FString Entry : Entries)
	{
		FString Name;
		FString Type;
		Entry.TrimStartAndEndInline();
		if (!Entry.Split(TEXT(":"), &Name, &Type) || Name.IsEmpty() || Type.IsEmpty())
		{
			OutError = FString::Printf(
				TEXT("Parameter '%s' must use name:type syntax."),
				*Entry);
			return false;
		}
		Name.TrimStartAndEndInline();
		Type.TrimStartAndEndInline();
		if (Name.IsEmpty() || Type.IsEmpty() || Names.Contains(Name))
		{
			OutError = TEXT("Function parameter names and types must be non-empty and unique.");
			return false;
		}
		Names.Add(Name);
		FFinareAuthoringParameter Parameter;
		Parameter.Name = MoveTemp(Name);
		Parameter.TypeMetadataName = MoveTemp(Type);
		OutParameters.Add(MoveTemp(Parameter));
	}
	if (OutParameters.Num() > 16)
	{
		OutError = TEXT("A visually authored function supports at most 16 parameters.");
		return false;
	}
	OutError.Reset();
	return true;
}

const FFinareAuthoringRegion* SFinareAuthoringSurface::FindRegion(
	const FFinareAuthoringFunction& Function,
	const FString& RegionId) const
{
	return Function.FindRegion(RegionId);
}

const FFinareAuthoringRegion* SFinareAuthoringSurface::ResolveActiveBlockRegion(
	const FFinareAuthoringFunction& Function) const
{
	const FFinareAuthoringRegion* Region = FindRegion(Function, ActiveBlockRegionId);
	if (Region != nullptr && !Region->SourceNodeId.IsEmpty())
	{
		return Region;
	}
	const FFinareAuthoringRegion* Root = FindRegion(Function, Function.RootRegionId);
	if (Root != nullptr)
	{
		for (const FString& ChildId : Root->ChildRegionIds)
		{
			const FFinareAuthoringRegion* Child = FindRegion(Function, ChildId);
			if (Child != nullptr && Child->Kind == TEXT("block"))
			{
				return Child;
			}
		}
	}
	return nullptr;
}

void SFinareAuthoringSurface::LoadNodePalettePreferences()
{
	FavoriteNodePaletteActionIds.Reset();
	RecentNodePaletteActionIds.Reset();
	if (GConfig != nullptr)
	{
		GConfig->GetArray(
			UE::Finare::AuthoringSurface::Slate::Private::NodePaletteConfigSection,
			TEXT("Favorites"),
			FavoriteNodePaletteActionIds,
			GEditorPerProjectIni);
		GConfig->GetArray(
			UE::Finare::AuthoringSurface::Slate::Private::NodePaletteConfigSection,
			TEXT("Recents"),
			RecentNodePaletteActionIds,
			GEditorPerProjectIni);
	}

	const auto NormalizeIds = [](TArray<FString>& Ids, int32 MaxCount)
	{
		TSet<FString> Seen;
		Ids.RemoveAll([&Seen](FString& Id)
		{
			Id.TrimStartAndEndInline();
			if (Id.IsEmpty() || Seen.Contains(Id))
			{
				return true;
			}
			Seen.Add(Id);
			return false;
		});
		if (Ids.Num() > MaxCount)
		{
			Ids.SetNum(MaxCount);
		}
	};
	NormalizeIds(FavoriteNodePaletteActionIds, 128);
	NormalizeIds(
		RecentNodePaletteActionIds,
		FFinareNodePaletteModel::MaxRecentActions);
}

void SFinareAuthoringSurface::SaveNodePalettePreferences() const
{
	if (GConfig == nullptr)
	{
		return;
	}
	GConfig->SetArray(
		UE::Finare::AuthoringSurface::Slate::Private::NodePaletteConfigSection,
		TEXT("Favorites"),
		FavoriteNodePaletteActionIds,
		GEditorPerProjectIni);
	GConfig->SetArray(
		UE::Finare::AuthoringSurface::Slate::Private::NodePaletteConfigSection,
		TEXT("Recents"),
		RecentNodePaletteActionIds,
		GEditorPerProjectIni);
	GConfig->Flush(false, GEditorPerProjectIni);
}

void SFinareAuthoringSurface::LoadCanvasPreferences()
{
	bShowCanvasMinimap = true;
	if (GConfig != nullptr)
	{
		GConfig->GetBool(
			UE::Finare::AuthoringSurface::Slate::Private::CanvasConfigSection,
			TEXT("ShowMinimap"),
			bShowCanvasMinimap,
			GEditorPerProjectIni);
	}
}

void SFinareAuthoringSurface::SaveCanvasPreferences() const
{
	if (GConfig == nullptr)
	{
		return;
	}
	GConfig->SetBool(
		UE::Finare::AuthoringSurface::Slate::Private::CanvasConfigSection,
		TEXT("ShowMinimap"),
		bShowCanvasMinimap,
		GEditorPerProjectIni);
	GConfig->Flush(false, GEditorPerProjectIni);
}

void SFinareAuthoringSurface::RefreshNodePaletteItems()
{
	FilteredNodePaletteItems.Reset();
	if (AuthoringGraph.IsValid())
	{
		TArray<FFinareNodePaletteItem> Items = FFinareNodePaletteModel::Build(
			AuthoringGraph->PaletteActions,
			NodePaletteSearchText,
			FavoriteNodePaletteActionIds,
			RecentNodePaletteActionIds,
			!ActiveBlockRegionId.IsEmpty());
		FilteredNodePaletteItems.Reserve(Items.Num());
		for (FFinareNodePaletteItem& Item : Items)
		{
			FilteredNodePaletteItems.Add(
				MakeShared<FFinareNodePaletteItem>(MoveTemp(Item)));
		}
	}
	if (NodePaletteListView.IsValid())
	{
		NodePaletteListView->RequestListRefresh();
	}
}

void SFinareAuthoringSurface::RecordNodePaletteUsage(const FString& ActionId)
{
	FFinareNodePaletteModel::RecordRecent(RecentNodePaletteActionIds, ActionId);
	SaveNodePalettePreferences();
	RefreshNodePaletteItems();
}

FText SFinareAuthoringSurface::GetNodePaletteContextText() const
{
	if (ActiveFunctionId.IsEmpty())
	{
		return LOCTEXT("NodePaletteNoFunction", "Select a function to discover valid nodes");
	}
	if (ActiveBlockRegionId.IsEmpty())
	{
		return LOCTEXT("NodePaletteFunctionContext", "Function context · events only");
	}
	return FText::Format(
		LOCTEXT("NodePaletteBlockContext", "{0} · active block"),
		FText::FromString(ActiveFunctionSignature));
}

void SFinareAuthoringSurface::RebuildNodePalette(
	const FFinareAuthoringFunction& Function)
{
	if (!AuthoringGraph.IsValid())
	{
		return;
	}
	AuthoringGraph->PaletteActions.Reset();
	const auto HasBoundType = [&Function](const FString& DisplayName)
	{
		return Function.Parameters.ContainsByPredicate(
				[&DisplayName](const FFinareAuthoringParameter& Parameter)
				{
					return Parameter.TypeDisplayName == DisplayName && !Parameter.TypeId.IsEmpty();
				}) ||
			Function.Actions.ContainsByPredicate(
				[&DisplayName](const FFinareAuthoringAction& Action)
				{
					return Action.Ports.ContainsByPredicate(
						[&DisplayName](const FFinareAuthoringPort& Port)
						{
							return !Port.IsControl() && Port.TypeDisplayName == DisplayName &&
								!Port.TypeId.IsEmpty();
						});
				});
	};
	if (HasBoundType(TEXT("int")))
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("local.int"), TEXT("Variables"), TEXT("Integer Local"),
				TEXT("Insert a typed int local initialized to 0."),
				TEXT("variable local integer int number")
			});
	}
	int32 IntParameterCount = 0;
	for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
	{
		if (Parameter.TypeDisplayName == TEXT("int") && !Parameter.TypeId.IsEmpty())
		{
			++IntParameterCount;
		}
	}
	if (IntParameterCount >= 1)
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.switch"), TEXT("Flow Control"), TEXT("Switch on Integer"),
				TEXT("Insert a typed switch with case 0 and default sections."),
				TEXT("switch case select integer branch")
			});
	}
	if (IntParameterCount >= 2)
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("value.add"), TEXT("Math|Integer"), TEXT("Add Expression"),
				TEXT("Insert a typed local containing the sum of two int parameters."),
				TEXT("add plus sum arithmetic math integer")
			});
	}
	if (HasBoundType(TEXT("bool")))
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.branch"), TEXT("Flow Control"), TEXT("Branch"),
				TEXT("Insert a structured C# if block with a true condition."),
				TEXT("if condition boolean bool branch")
			});
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.while"), TEXT("Flow Control"), TEXT("While Loop"),
				TEXT("Insert a structured C# while block with a true condition."),
				TEXT("while loop repeat condition")
			});
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.doWhile"), TEXT("Flow Control"), TEXT("Do While Loop"),
				TEXT("Insert a structured C# do/while block with a true condition."),
				TEXT("do while loop repeat condition")
			});
	}
	if (HasBoundType(TEXT("int")) && HasBoundType(TEXT("bool")))
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.for"), TEXT("Flow Control"), TEXT("For Loop"),
				TEXT("Insert a bounded C# for loop with an editable int index."),
				TEXT("for loop iterate index integer")
			});
	}
	bool bInsideLoop = false;
	bool bInsideSwitch = false;
	for (const FFinareAuthoringRegion* Context = ResolveActiveBlockRegion(Function);
	     Context != nullptr;
	     Context = Context->ParentRegionId.IsEmpty()
		               ? nullptr
		               : Function.FindRegion(Context->ParentRegionId))
	{
		bInsideLoop |= Context->Kind == TEXT("loop");
		bInsideSwitch |= Context->Kind == TEXT("switch");
	}
	if (bInsideLoop || bInsideSwitch)
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.break"), TEXT("Flow Control"), TEXT("Break"),
				TEXT("Exit the nearest selected loop or switch region."),
				TEXT("break exit loop switch")
			});
	}
	if (bInsideLoop)
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.continue"), TEXT("Flow Control"), TEXT("Continue"),
				TEXT("Continue the nearest selected loop region."),
				TEXT("continue next iteration loop")
			});
	}
	if (Function.Actions.ContainsByPredicate(
		[](const FFinareAuthoringAction& Action)
		{
			const FFinareAuthoringPort* ReturnValue =
				Action.GraphKind == TEXT("return")
					? Action.FindPort(TEXT("value"), TEXT("input"))
					: nullptr;
			return ReturnValue != nullptr && !ReturnValue->TypeId.IsEmpty();
		}))
	{
		AuthoringGraph->PaletteActions.Add(
			{
				TEXT("flow.return"), TEXT("Flow Control"), TEXT("Return"),
				TEXT("Insert a typed return default(T) at the end of the selected block."),
				TEXT("return result exit function default")
			});
	}
	ReflectedPaletteActions.Reset();

	FString ConfiguredCatalogPath;
	if (const UFinareAsset* ScriptAsset = LoadedScriptAsset.Get())
	{
		ConfiguredCatalogPath = ScriptAsset->ResolveApiNodeCatalogPath();
	}
	if (ConfiguredCatalogPath.IsEmpty())
	{
		ConfiguredCatalogPath =
			TEXT("{ProjectDir}/../samples/Unreal/FinareRuntime.api-nodes.json");
	}
	const FString ManifestPath = FFinareAuthoringSurfaceReader::ResolveConfiguredPath(
		ConfiguredCatalogPath);
	FString ManifestJson;
	TSharedPtr<FJsonObject> Manifest;
	if (!FFileHelper::LoadFileToString(ManifestJson, *ManifestPath) ||
		!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(ManifestJson), Manifest) ||
		!Manifest.IsValid())
	{
		RefreshNodePaletteItems();
		return;
	}
	FString CatalogSchema;
	if (!Manifest->TryGetStringField(TEXT("schemaVersion"), CatalogSchema) ||
		CatalogSchema != TEXT("unifiedscript.unreal-api-node-catalog/v0.2") &&
		CatalogSchema != TEXT("unifiedscript.unreal-api-node-catalog/v0.1"))
	{
		RefreshNodePaletteItems();
		return;
	}

	const auto FindGeneratedNode = [](
		const TSharedPtr<FJsonObject>& Type,
		const FString& ActionId) -> TSharedPtr<FJsonObject>
	{
		const TArray<TSharedPtr<FJsonValue>>* Nodes = nullptr;
		if (!Type.IsValid() || !Type->TryGetArrayField(TEXT("nodes"), Nodes) || Nodes == nullptr)
		{
			return nullptr;
		}
		for (const TSharedPtr<FJsonValue>& NodeValue : *Nodes)
		{
			const TSharedPtr<FJsonObject> Node =
				NodeValue.IsValid() ? NodeValue->AsObject() : nullptr;
			FString NodeId;
			if (Node.IsValid() && Node->TryGetStringField(TEXT("id"), NodeId) &&
				NodeId == ActionId)
			{
				return Node;
			}
		}
		return nullptr;
	};
	const auto FindGeneratedNodeByMemberPath = [](
		const TSharedPtr<FJsonObject>& Type,
		const FString& MemberPath,
		const FString& Kind) -> TSharedPtr<FJsonObject>
	{
		const TArray<TSharedPtr<FJsonValue>>* Nodes = nullptr;
		if (!Type.IsValid() || !Type->TryGetArrayField(TEXT("nodes"), Nodes) || Nodes == nullptr)
		{
			return nullptr;
		}
		for (const TSharedPtr<FJsonValue>& NodeValue : *Nodes)
		{
			const TSharedPtr<FJsonObject> Node =
				NodeValue.IsValid() ? NodeValue->AsObject() : nullptr;
			FString NodeMemberPath;
			FString NodeKind;
			if (Node.IsValid() &&
				Node->TryGetStringField(TEXT("memberPath"), NodeMemberPath) &&
				Node->TryGetStringField(TEXT("kind"), NodeKind) &&
				NodeMemberPath == MemberPath && NodeKind == Kind)
			{
				return Node;
			}
		}
		return nullptr;
	};
	const auto MakeGeneratedPaletteAction = [](
		const TSharedPtr<FJsonObject>& Node,
		const FString& ActionId,
		const FString& FallbackCategory,
		const FString& FallbackTitle,
		const FString& FallbackTooltip,
		const FString& Keywords,
		bool bRequiresBlock) -> FFinareGraphPaletteAction
	{
		FFinareGraphPaletteAction Action;
		Action.ActionId = ActionId;
		Action.Category = FallbackCategory;
		Action.Title = FallbackTitle;
		Action.Tooltip = FallbackTooltip;
		Action.Keywords = Keywords;
		Action.bRequiresBlock = bRequiresBlock;
		if (!Node.IsValid())
		{
			return Action;
		}
		Node->TryGetStringField(TEXT("category"), Action.Category);
		Node->TryGetStringField(TEXT("title"), Action.Title);
		Node->TryGetStringField(TEXT("tooltip"), Action.Tooltip);
		Node->TryGetBoolField(TEXT("deprecated"), Action.bDeprecated);
		Node->TryGetBoolField(TEXT("latent"), Action.bLatent);

		TArray<FString> Restrictions;
		FString LatentInfo;
		if (Action.bLatent)
		{
			Node->TryGetStringField(TEXT("latentInfoParameter"), LatentInfo);
			Restrictions.Add(LatentInfo.IsEmpty()
				                 ? TEXT("Latent")
				                 : FString::Printf(TEXT("Latent (%s)"), *LatentInfo));
		}
		FString ThreadRequirement;
		if (Node->TryGetStringField(TEXT("threadRequirement"), ThreadRequirement))
		{
			Restrictions.Add(ThreadRequirement == TEXT("anyThread")
				                 ? TEXT("Any thread")
				                 : TEXT("Game thread only"));
		}
		FString WorldRequirement;
		FString WorldContextParameter;
		Node->TryGetStringField(TEXT("worldRequirement"), WorldRequirement);
		Node->TryGetStringField(TEXT("worldContextParameter"), WorldContextParameter);
		if (WorldRequirement == TEXT("worldContext"))
		{
			Restrictions.Add(WorldContextParameter.IsEmpty()
				                 ? TEXT("Requires World context")
				                 : FString::Printf(
					                 TEXT("Requires World context (%s)"),
					                 *WorldContextParameter));
		}
		else if (!WorldContextParameter.IsEmpty())
		{
			Restrictions.Add(FString::Printf(
				TEXT("World context optional (%s)"),
				*WorldContextParameter));
		}

		TArray<FString> Defaults;
		const TArray<TSharedPtr<FJsonValue>>* Pins = nullptr;
		if (Node->TryGetArrayField(TEXT("pins"), Pins) && Pins != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& PinValue : *Pins)
			{
				const TSharedPtr<FJsonObject> Pin =
					PinValue.IsValid() ? PinValue->AsObject() : nullptr;
				FString PinName;
				FString DefaultValue;
				if (Pin.IsValid() && Pin->TryGetStringField(TEXT("name"), PinName) &&
					Pin->TryGetStringField(TEXT("defaultValue"), DefaultValue))
				{
					Defaults.Add(PinName + TEXT("=") + DefaultValue);
				}
			}
		}
		if (!Defaults.IsEmpty())
		{
			Restrictions.Add(TEXT("Defaults: ") + FString::Join(Defaults, TEXT(", ")));
		}
		if (Action.bDeprecated)
		{
			FString Message;
			Node->TryGetStringField(TEXT("deprecationMessage"), Message);
			Restrictions.Add(Message.IsEmpty()
				                 ? TEXT("Deprecated")
				                 : TEXT("Deprecated: ") + Message);
		}
		Action.RestrictionSummary = FString::Join(Restrictions, TEXT(" | "));
		if (!Action.RestrictionSummary.IsEmpty())
		{
			Action.Tooltip += TEXT("\n\nContract: ") + Action.RestrictionSummary;
			Action.Keywords += TEXT(" ") + Action.RestrictionSummary;
		}
		return Action;
	};

	const auto TypeDisplayName = [](const TSharedPtr<FJsonObject>& Type) -> FString
	{
		FString Kind;
		if (!Type.IsValid() || !Type->TryGetStringField(TEXT("kind"), Kind))
		{
			return FString();
		}
		if (Kind == TEXT("boolean"))
		{
			return TEXT("bool");
		}
		if (Kind == TEXT("int32"))
		{
			return TEXT("int");
		}
		if (Kind == TEXT("int64"))
		{
			return TEXT("long");
		}
		if (Kind == TEXT("float32"))
		{
			return TEXT("float");
		}
		if (Kind == TEXT("float64"))
		{
			return TEXT("double");
		}
		if (Kind == TEXT("string") || Kind == TEXT("name") || Kind == TEXT("text"))
		{
			return TEXT("string");
		}
		FString TypePath;
		if (Kind == TEXT("object") && Type->TryGetStringField(TEXT("typePath"), TypePath))
		{
			FString Left;
			FString Right;
			return TypePath.Split(TEXT("."), &Left, &Right, ESearchCase::CaseSensitive,
			                      ESearchDir::FromEnd)
				       ? Right
				       : TypePath;
		}
		return FString();
	};
	TFunction<FString(const TSharedPtr<FJsonObject>&)> ManagedEventTypeName;
	ManagedEventTypeName = [&ManagedEventTypeName](
		const TSharedPtr<FJsonObject>& Type) -> FString
		{
			FString Kind;
			if (!Type.IsValid() || !Type->TryGetStringField(TEXT("kind"), Kind))
			{
				return FString();
			}
			if (Kind == TEXT("boolean"))
			{
				return TEXT("bool");
			}
			if (Kind == TEXT("int8"))
			{
				return TEXT("sbyte");
			}
			if (Kind == TEXT("uint8"))
			{
				return TEXT("byte");
			}
			if (Kind == TEXT("int16"))
			{
				return TEXT("short");
			}
			if (Kind == TEXT("uint16"))
			{
				return TEXT("ushort");
			}
			if (Kind == TEXT("int32"))
			{
				return TEXT("int");
			}
			if (Kind == TEXT("uint32"))
			{
				return TEXT("uint");
			}
			if (Kind == TEXT("int64"))
			{
				return TEXT("long");
			}
			if (Kind == TEXT("uint64"))
			{
				return TEXT("ulong");
			}
			if (Kind == TEXT("float32"))
			{
				return TEXT("float");
			}
			if (Kind == TEXT("float64"))
			{
				return TEXT("double");
			}
			if (Kind == TEXT("string"))
			{
				return TEXT("string");
			}
			if (Kind == TEXT("name"))
			{
				return TEXT("global::Finare.Unreal.UnrealName");
			}
			if (Kind == TEXT("text"))
			{
				return TEXT("global::Finare.Unreal.UnrealText");
			}
			if (Kind == TEXT("object"))
			{
				return TEXT("global::Finare.Unreal.UnrealObjectHandle");
			}
			if (Kind == TEXT("class"))
			{
				return TEXT("global::Finare.Unreal.UnrealClassHandle");
			}
			if (Kind == TEXT("enum"))
			{
				return TEXT("global::Finare.Unreal.UnrealEnumValue");
			}
			FString TypePath;
			if (Kind == TEXT("struct") && Type->TryGetStringField(TEXT("typePath"), TypePath))
			{
				if (TypePath == TEXT("/Script/CoreUObject.Vector"))
				{
					return TEXT("global::Finare.Unreal.UnrealVector");
				}
				if (TypePath == TEXT("/Script/CoreUObject.Rotator"))
				{
					return TEXT("global::Finare.Unreal.UnrealRotator");
				}
				if (TypePath == TEXT("/Script/CoreUObject.Transform"))
				{
					return TEXT("global::Finare.Unreal.UnrealTransform");
				}
				return FString();
			}
			const TSharedPtr<FJsonObject>* Element = nullptr;
			if ((Kind == TEXT("array") || Kind == TEXT("set")) &&
				Type->TryGetObjectField(TEXT("elementType"), Element) && Element != nullptr)
			{
				const FString ManagedElement = ManagedEventTypeName(*Element);
				if (ManagedElement.IsEmpty())
				{
					return FString();
				}
				return Kind == TEXT("array")
					       ? FString::Printf(
						       TEXT("global::System.Collections.Generic.IReadOnlyList<%s>"),
						       *ManagedElement)
					       : FString::Printf(
						       TEXT("global::System.Collections.Generic.IReadOnlySet<%s>"),
						       *ManagedElement);
			}
			const TSharedPtr<FJsonObject>* Key = nullptr;
			const TSharedPtr<FJsonObject>* Value = nullptr;
			if (Kind == TEXT("map") && Type->TryGetObjectField(TEXT("keyType"), Key) &&
				Key != nullptr && Type->TryGetObjectField(TEXT("valueType"), Value) &&
				Value != nullptr)
			{
				const FString ManagedKey = ManagedEventTypeName(*Key);
				const FString ManagedValue = ManagedEventTypeName(*Value);
				return ManagedKey.IsEmpty() || ManagedValue.IsEmpty()
					       ? FString()
					       : FString::Printf(
						       TEXT("global::System.Collections.Generic.IReadOnlyDictionary<%s, %s>"),
						       *ManagedKey,
						       *ManagedValue);
			}
			return FString();
		};
	const auto FindBoundTypeId = [&Function](const FString& DisplayName) -> FString
	{
		for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
		{
			if (Parameter.TypeDisplayName == DisplayName && !Parameter.TypeId.IsEmpty())
			{
				return Parameter.TypeId;
			}
		}
		for (const FFinareAuthoringAction& Action : Function.Actions)
		{
			for (const FFinareAuthoringPort& Port : Action.Ports)
			{
				if (Port.TypeDisplayName == DisplayName && !Port.TypeId.IsEmpty())
				{
					return Port.TypeId;
				}
			}
		}
		return FString();
	};

	const TArray<TSharedPtr<FJsonValue>>* Types = nullptr;
	if (!Manifest->TryGetArrayField(TEXT("types"), Types) || Types == nullptr)
	{
		RefreshNodePaletteItems();
		return;
	}
	FString ManagedNamespace = TEXT("Finare.Unreal.Generated");
	Manifest->TryGetStringField(TEXT("managedNamespace"), ManagedNamespace);
	const FFinareAuthoringAsset* ActiveAsset = Document.Assets.FindByPredicate(
		[&Function](const FFinareAuthoringAsset& Asset)
		{
			return Asset.Functions.ContainsByPredicate(
				[&Function](const FFinareAuthoringFunction& Candidate)
				{
					return Candidate.Id == Function.Id;
				});
		});
	for (const TSharedPtr<FJsonValue>& TypeValue : *Types)
	{
		const TSharedPtr<FJsonObject> Type = TypeValue.IsValid() ? TypeValue->AsObject() : nullptr;
		FString TypeName;
		FString ManagedTypeName;
		const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
		if (!Type.IsValid() || !Type->TryGetStringField(TEXT("name"), TypeName))
		{
			continue;
		}
		if (!Type->TryGetStringField(TEXT("managedTypeName"), ManagedTypeName))
		{
			ManagedTypeName = TypeName;
		}
		const TArray<TSharedPtr<FJsonValue>>* Delegates = nullptr;
		if (Type->TryGetArrayField(TEXT("delegates"), Delegates) && Delegates != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& DelegateValue : *Delegates)
			{
				const TSharedPtr<FJsonObject> Delegate =
					DelegateValue.IsValid() ? DelegateValue->AsObject() : nullptr;
				FString MemberName;
				FString EventPath;
				const TSharedPtr<FJsonObject>* Traits = nullptr;
				const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
				bool bBlueprintAssignable = false;
				if (!Delegate.IsValid() ||
					!Delegate->TryGetStringField(TEXT("name"), MemberName) ||
					!Delegate->TryGetStringField(TEXT("path"), EventPath) ||
					!Delegate->TryGetObjectField(TEXT("traits"), Traits) || Traits == nullptr ||
					!(*Traits)->TryGetBoolField(
						TEXT("blueprintAssignable"),
						bBlueprintAssignable) || !bBlueprintAssignable ||
					!Delegate->TryGetArrayField(TEXT("parameters"), Parameters) ||
					Parameters == nullptr)
				{
					continue;
				}
				FString HandlerName = MemberName;
				for (int32 Suffix = 2; ActiveAsset != nullptr &&
				     ActiveAsset->Functions.ContainsByPredicate(
					     [&HandlerName](const FFinareAuthoringFunction& Candidate)
					     {
						     return Candidate.Name == HandlerName;
					     }); ++Suffix)
				{
					HandlerName = FString::Printf(TEXT("%s%d"), *MemberName, Suffix);
				}

				TArray<FFinareAuthoringParameter> EventParameters;
				TArray<FString> SignatureParts;
				bool bSupported = true;
				for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
				{
					const TSharedPtr<FJsonObject> Parameter =
						ParameterValue.IsValid() ? ParameterValue->AsObject() : nullptr;
					FString Name;
					FString Direction;
					const TSharedPtr<FJsonObject>* ParameterType = nullptr;
					if (!Parameter.IsValid() ||
						!Parameter->TryGetStringField(TEXT("name"), Name) ||
						!Parameter->TryGetStringField(TEXT("direction"), Direction) ||
						Direction != TEXT("input") ||
						!Parameter->TryGetObjectField(TEXT("type"), ParameterType) ||
						ParameterType == nullptr)
					{
						bSupported = false;
						break;
					}
					const FString ManagedType = ManagedEventTypeName(*ParameterType);
					if (ManagedType.IsEmpty())
					{
						bSupported = false;
						break;
					}
					FFinareAuthoringParameter EventParameter;
					EventParameter.Name = Name;
					EventParameter.TypeMetadataName = ManagedType;
					EventParameter.TypeDisplayName = TypeDisplayName(*ParameterType);
					EventParameters.Add(MoveTemp(EventParameter));
					SignatureParts.Add(ManagedType + TEXT(" ") + Name);
				}
				if (!bSupported)
				{
					continue;
				}

				const FString ActionId = FString::Printf(
					TEXT("ue.event|%s|%s"),
					*ManagedTypeName,
					*MemberName);
				FFinareReflectedPaletteAction EventAction;
				EventAction.bEvent = true;
				EventAction.EventPath = EventPath;
				EventAction.SourceTypeName =
					TEXT("global::") + ManagedNamespace + TEXT(".") + ManagedTypeName;
				EventAction.HandlerName = HandlerName;
				EventAction.EventParameters = MoveTemp(EventParameters);
				ReflectedPaletteActions.Add(ActionId, MoveTemp(EventAction));
				AuthoringGraph->PaletteActions.Add(MakeGeneratedPaletteAction(
					FindGeneratedNode(Type, ActionId),
					ActionId,
					TEXT("Unreal Events|") + TypeName,
					TEXT("Event ") + MemberName,
					FString::Printf(
						TEXT("Create a target-bound visual Event Entry.\n%s(%s)"),
						*MemberName,
						*FString::Join(SignatureParts, TEXT(", "))),
					FString::Printf(
						TEXT("unreal ue event delegate %s %s"),
						*TypeName,
						*MemberName),
					false));
			}
		}
		const FFinareAuthoringParameter* Receiver = Function.Parameters.FindByPredicate(
			[&ManagedTypeName](const FFinareAuthoringParameter& Parameter)
			{
				return !Parameter.TypeId.IsEmpty() &&
				(Parameter.TypeMetadataName == ManagedTypeName ||
					Parameter.TypeMetadataName.EndsWith(TEXT(".") + ManagedTypeName));
			});
		if (Receiver == nullptr)
		{
			continue;
		}
		const TArray<TSharedPtr<FJsonValue>>* Properties = nullptr;
		if (Type->TryGetArrayField(TEXT("properties"), Properties) && Properties != nullptr)
		{
			for (const TSharedPtr<FJsonValue>& PropertyValue : *Properties)
			{
				const TSharedPtr<FJsonObject> Property =
					PropertyValue.IsValid() ? PropertyValue->AsObject() : nullptr;
				FString MemberName;
				const TSharedPtr<FJsonObject>* PropertyType = nullptr;
				const TSharedPtr<FJsonObject>* Access = nullptr;
				bool bReadable = false;
				if (!Property.IsValid() ||
					!Property->TryGetStringField(TEXT("name"), MemberName) ||
					!Property->TryGetObjectField(TEXT("type"), PropertyType) || PropertyType == nullptr ||
					!Property->TryGetObjectField(TEXT("access"), Access) || Access == nullptr ||
					!(*Access)->TryGetBoolField(TEXT("readable"), bReadable) || !bReadable)
				{
					continue;
				}
				const FString ResultTypeName = TypeDisplayName(*PropertyType);
				const FString ResultTypeId = FindBoundTypeId(ResultTypeName);
				if (ResultTypeName.IsEmpty() || ResultTypeId.IsEmpty())
				{
					continue;
				}
				const FString ActionId = FString::Printf(
					TEXT("ue.get|%s|%s"), *ManagedTypeName, *MemberName);
				ReflectedPaletteActions.Add(ActionId,
				                            {false, *Receiver, MemberName, ResultTypeName, ResultTypeId, {}});
				AuthoringGraph->PaletteActions.Add(MakeGeneratedPaletteAction(
					FindGeneratedNode(Type, ActionId),
					ActionId,
					TEXT("Unreal API|") + TypeName + TEXT("|Properties"),
					MemberName,
					FString::Printf(
						TEXT("Read reflected property %s.%s."),
						*TypeName,
						*MemberName),
					FString::Printf(
						TEXT("unreal ue api property get read %s %s"),
						*TypeName,
						*MemberName),
					true));
			}
		}
		if (!Type->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr)
		{
			continue;
		}
		for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
		{
			const TSharedPtr<FJsonObject> ReflectedFunction =
				FunctionValue.IsValid() ? FunctionValue->AsObject() : nullptr;
			FString MemberName;
			const TSharedPtr<FJsonObject>* ReturnType = nullptr;
			const TSharedPtr<FJsonObject>* Traits = nullptr;
			const TArray<TSharedPtr<FJsonValue>>* Parameters = nullptr;
			FString MemberPath;
			bool bCallable = false;
			if (!ReflectedFunction.IsValid() ||
				!ReflectedFunction->TryGetStringField(TEXT("name"), MemberName) ||
				!ReflectedFunction->TryGetStringField(TEXT("path"), MemberPath) ||
				!ReflectedFunction->TryGetObjectField(TEXT("returnType"), ReturnType) ||
				ReturnType == nullptr ||
				!ReflectedFunction->TryGetObjectField(TEXT("traits"), Traits) || Traits == nullptr ||
				!(*Traits)->TryGetBoolField(TEXT("callable"), bCallable) || !bCallable ||
				!ReflectedFunction->TryGetArrayField(TEXT("parameters"), Parameters) ||
				Parameters == nullptr)
			{
				continue;
			}
			const TSharedPtr<FJsonObject> GeneratedNode = FindGeneratedNodeByMemberPath(
				Type,
				MemberPath,
				TEXT("function"));
			FString ActionId;
			if (!GeneratedNode.IsValid() ||
				!GeneratedNode->TryGetStringField(TEXT("id"), ActionId))
			{
				ActionId = FString::Printf(
					TEXT("ue.call|%s|%s"), *ManagedTypeName, *MemberName);
			}
			const FString ResultTypeName = TypeDisplayName(*ReturnType);
			const FString ResultTypeId = FindBoundTypeId(ResultTypeName);
			FFinareGraphPaletteAction PaletteAction = MakeGeneratedPaletteAction(
				GeneratedNode,
				ActionId,
				TEXT("Unreal API|") + TypeName,
				MemberName,
				FString::Printf(
					TEXT("Insert reflected call %s.%s(...)."),
					*TypeName,
					*MemberName),
				FString::Printf(
					TEXT("unreal ue api function call invoke %s %s"),
					*TypeName,
					*MemberName),
				true);

			TArray<FFinareAuthoringParameter> Arguments;
			bool bAllArgumentsAvailable = true;
			bool bOmittingTrailingDefaults = false;
			FString MissingArgument;
			for (const TSharedPtr<FJsonValue>& ParameterValue : *Parameters)
			{
				const TSharedPtr<FJsonObject> ParameterObject =
					ParameterValue.IsValid() ? ParameterValue->AsObject() : nullptr;
				FString Name;
				FString Direction;
				const TSharedPtr<FJsonObject>* ParameterType = nullptr;
				if (!ParameterObject.IsValid() ||
					!ParameterObject->TryGetStringField(TEXT("name"), Name) ||
					!ParameterObject->TryGetStringField(TEXT("direction"), Direction) ||
					!ParameterObject->TryGetObjectField(TEXT("type"), ParameterType) ||
					ParameterType == nullptr)
				{
					bAllArgumentsAvailable = false;
					break;
				}
				if (Direction != TEXT("input"))
				{
					bAllArgumentsAvailable = false;
					MissingArgument = Name + TEXT(" (") + Direction + TEXT(")");
					break;
				}
				const bool bHasDefault = HasProjectableInputDefault(
					GeneratedNode,
					ParameterObject,
					Name);
				if (bOmittingTrailingDefaults)
				{
					if (!bHasDefault)
					{
						bAllArgumentsAvailable = false;
						MissingArgument = Name;
						break;
					}
					continue;
				}
				const FString DisplayName = TypeDisplayName(*ParameterType);
				const FFinareAuthoringParameter* Argument = Function.Parameters.FindByPredicate(
					[&Name, &DisplayName](const FFinareAuthoringParameter& Parameter)
					{
						return Parameter.Name.Equals(Name, ESearchCase::IgnoreCase) &&
							Parameter.TypeDisplayName == DisplayName &&
							!Parameter.TypeId.IsEmpty();
					});
				if (Argument == nullptr)
				{
					if (bHasDefault)
					{
						bOmittingTrailingDefaults = true;
						continue;
					}
					bAllArgumentsAvailable = false;
					MissingArgument = Name;
					break;
				}
				Arguments.Add(*Argument);
			}

			FString ReturnKind;
			(*ReturnType)->TryGetStringField(TEXT("kind"), ReturnKind);
			if (PaletteAction.bLatent)
			{
				PaletteAction.bEnabled = false;
				PaletteAction.UnavailableReason =
					TEXT("Latent API nodes require continuation authoring and cannot be placed yet.");
			}
			else if (ReturnKind == TEXT("void"))
			{
				PaletteAction.bEnabled = false;
				PaletteAction.UnavailableReason =
					TEXT("Void API calls require execution-statement authoring and cannot be placed yet.");
			}
			else if (ResultTypeName.IsEmpty() || ResultTypeId.IsEmpty())
			{
				PaletteAction.bEnabled = false;
				PaletteAction.UnavailableReason =
					TEXT("The reflected return type is not bound in the active Visual function.");
			}
			else if (!bAllArgumentsAvailable)
			{
				PaletteAction.bEnabled = false;
				FString WorldContextParameter;
				if (GeneratedNode.IsValid())
				{
					GeneratedNode->TryGetStringField(
						TEXT("worldContextParameter"),
						WorldContextParameter);
				}
				PaletteAction.UnavailableReason = MissingArgument == WorldContextParameter
					                                  ? FString::Printf(
						                                  TEXT(
							                                  "Requires World context parameter '%s' in the active function."),
						                                  *MissingArgument)
					                                  : FString::Printf(
						                                  TEXT(
							                                  "Requires compatible input parameter '%s' in the active function."),
						                                  *MissingArgument);
			}
			if (!PaletteAction.UnavailableReason.IsEmpty())
			{
				PaletteAction.Tooltip +=
					TEXT("\n\nUnavailable: ") + PaletteAction.UnavailableReason;
				PaletteAction.RestrictionSummary +=
					(PaletteAction.RestrictionSummary.IsEmpty() ? FString() : TEXT(" | ")) +
					PaletteAction.UnavailableReason;
			}
			if (PaletteAction.bEnabled)
			{
				ReflectedPaletteActions.Add(ActionId,
				                            {
					                            true,
					                            *Receiver,
					                            MemberName,
					                            ResultTypeName,
					                            ResultTypeId,
					                            MoveTemp(Arguments)
				                            });
			}
			AuthoringGraph->PaletteActions.Add(MoveTemp(PaletteAction));
		}
	}
	RefreshNodePaletteItems();
}

bool SFinareAuthoringSurface::PersistCurrentLayout(bool bOnlyIfChanged)
{
	if (!AuthoringGraph.IsValid() || ActiveFunctionId.IsEmpty() || LoadedLayoutPath.IsEmpty() ||
		AuthoringGraph->Nodes.IsEmpty())
	{
		return true;
	}
	TArray<UEdGraphNode*> Nodes;
	for (UEdGraphNode* Node : AuthoringGraph->Nodes)
	{
		Nodes.Add(Node);
	}
	const FString CurrentFingerprint = BuildCanvasLayoutFingerprint();
	if (bOnlyIfChanged && CurrentFingerprint == PersistedCanvasLayoutFingerprint)
	{
		return true;
	}
	FString Error;
	if (FFinareGraphLayoutStore::SaveFunction(
		LoadedLayoutPath, ActiveFunctionId, Nodes, Error))
	{
		PersistedCanvasLayoutFingerprint = CurrentFingerprint;
		return true;
	}
	else if (!Error.IsEmpty())
	{
		SetStatus(MoveTemp(Error), true);
	}
	return false;
}

FString SFinareAuthoringSurface::BuildCanvasLayoutFingerprint() const
{
	if (!AuthoringGraph.IsValid())
	{
		return FString();
	}
	TArray<FString> Entries;
	for (const UEdGraphNode* Node : AuthoringGraph->Nodes)
	{
		if (const UFinareEdGraphNode* ScriptNode =
			Cast<UFinareEdGraphNode>(Node))
		{
			Entries.Add(FString::Printf(
				TEXT("S|%s|%d|%d"),
				*ScriptNode->SemanticId,
				ScriptNode->NodePosX,
				ScriptNode->NodePosY));
		}
		else if (const UFinareEdGraphCommentNode* Comment =
			Cast<UFinareEdGraphCommentNode>(Node))
		{
			Entries.Add(FString::Printf(
				TEXT("C|%s|%s|%d|%d|%d|%d|%d"),
				*Comment->LayoutId,
				*Comment->NodeComment,
				Comment->NodePosX,
				Comment->NodePosY,
				Comment->NodeWidth,
				Comment->NodeHeight,
				Comment->bLayoutCollapsed ? 1 : 0));
		}
		else if (const UFinareEdGraphRerouteNode* Reroute =
			Cast<UFinareEdGraphRerouteNode>(Node))
		{
			Entries.Add(FString::Printf(
				TEXT("R|%s|%d|%d|%d"),
				*Reroute->EdgeId,
				Reroute->PointIndex,
				Reroute->NodePosX,
				Reroute->NodePosY));
		}
	}
	Entries.Sort();
	return FString::Join(Entries, TEXT("\n"));
}

FString SFinareAuthoringSurface::BuildCanvasViewConfigKey() const
{
	if (LoadedSurfacePath.IsEmpty() || ActiveFunctionId.IsEmpty())
	{
		return FString();
	}
	FString CanonicalPath = LoadedSurfacePath;
	FPaths::NormalizeFilename(CanonicalPath);
	CanonicalPath.ToLowerInline();
	const uint32 Hash = FCrc::StrCrc32(*(CanonicalPath + TEXT("|") + ActiveFunctionId));
	return FString::Printf(TEXT("Viewport_%08x"), Hash);
}

void SFinareAuthoringSurface::PersistCanvasView(bool bOnlyIfChanged)
{
	if (GConfig == nullptr || !GraphEditor.IsValid())
	{
		return;
	}
	const FString Key = BuildCanvasViewConfigKey();
	if (Key.IsEmpty())
	{
		return;
	}

	FVector2f Location = FVector2f::ZeroVector;
	float Zoom = 1.0f;
	GraphEditor->GetViewLocation(Location, Zoom);
	if (!FMath::IsFinite(Location.X) || !FMath::IsFinite(Location.Y) ||
		!FMath::IsFinite(Zoom) || Zoom <= 0.01f)
	{
		return;
	}
	const FString Fingerprint = FString::Printf(
		TEXT("%.9g|%.9g|%.9g"),
		Location.X,
		Location.Y,
		Zoom);
	if (bOnlyIfChanged && Fingerprint == PersistedCanvasViewFingerprint)
	{
		return;
	}
	GConfig->SetString(
		UE::Finare::AuthoringSurface::Slate::Private::CanvasConfigSection,
		*Key,
		*Fingerprint,
		GEditorPerProjectIni);
	GConfig->Flush(false, GEditorPerProjectIni);
	PersistedCanvasViewFingerprint = Fingerprint;
}

bool SFinareAuthoringSurface::RestoreCanvasView()
{
	PersistedCanvasViewFingerprint.Reset();
	if (GConfig == nullptr || !GraphEditor.IsValid())
	{
		return false;
	}
	const FString Key = BuildCanvasViewConfigKey();
	FString Serialized;
	if (Key.IsEmpty() || !GConfig->GetString(
		UE::Finare::AuthoringSurface::Slate::Private::CanvasConfigSection,
		*Key,
		Serialized,
		GEditorPerProjectIni))
	{
		return false;
	}
	TArray<FString> Parts;
	Serialized.ParseIntoArray(Parts, TEXT("|"), false);
	FVector2f Location = FVector2f::ZeroVector;
	float Zoom = 1.0f;
	if (Parts.Num() != 3 ||
		!LexTryParseString(Location.X, *Parts[0]) ||
		!LexTryParseString(Location.Y, *Parts[1]) ||
		!LexTryParseString(Zoom, *Parts[2]) ||
		!FMath::IsFinite(Location.X) || !FMath::IsFinite(Location.Y) ||
		!FMath::IsFinite(Zoom) || Zoom <= 0.01f || Zoom > 10.0f)
	{
		return false;
	}
	GraphEditor->SetViewLocation(Location, Zoom);
	PersistedCanvasViewFingerprint = FString::Printf(
		TEXT("%.9g|%.9g|%.9g"),
		Location.X,
		Location.Y,
		Zoom);
	return true;
}

FFinareSurfaceTreeItemPtr SFinareAuthoringSurface::FindSurfaceItem(
	const FString& SemanticId) const
{
	if (const FFinareSurfaceTreeItemPtr* Item = SurfaceItemsById.Find(SemanticId))
	{
		return *Item;
	}
	return nullptr;
}

FReply SFinareAuthoringSurface::HandleFocusGraph()
{
	if (GraphEditor.IsValid() && !AuthoringGraph->Nodes.IsEmpty())
	{
		GraphEditor->ZoomToFit(false);
		bPendingGraphFocus = false;
	}
	return FReply::Handled();
}

FReply SFinareAuthoringSurface::HandleFocusSelection()
{
	if (GraphEditor.IsValid() && !GraphEditor->GetSelectedNodes().IsEmpty())
	{
		GraphEditor->ZoomToFit(true);
		bPendingGraphFocus = false;
	}
	return FReply::Handled();
}

int32 SFinareAuthoringSurface::GetVisibleGraphNodeCount() const
{
	return AuthoringGraph.IsValid() ? AuthoringGraph->Nodes.Num() : 0;
}

int32 SFinareAuthoringSurface::GetVisibleGraphEdgeCount() const
{
	const FFinareAuthoringFunction* Function = FindFunction(ActiveFunctionId);
	return Function != nullptr
		       ? FFinareGraphPresentation::CountVisibleEdges(*Function)
		       : 0;
}

FString SFinareAuthoringSurface::GetActiveFunctionId() const
{
	return ActiveFunctionId;
}

bool SFinareAuthoringSurface::SelectFunction(const FString& FunctionId)
{
	const FFinareSurfaceTreeItemPtr* Item = SurfaceItemsById.Find(FunctionId);
	if (Item == nullptr || !Item->IsValid() ||
		(*Item)->Kind != EFinareSurfaceTreeItemKind::Function)
	{
		return false;
	}
	RebuildGraph(*Item);
	return ActiveFunctionId == FunctionId;
}

bool SFinareAuthoringSurface::NavigateToSemanticId(const FString& SemanticId)
{
	if (SemanticId.IsEmpty())
	{
		return false;
	}
	FString OwningFunctionId;
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			bool bOwnsSemanticId = Function.Id == SemanticId ||
				Function.FindAction(SemanticId) != nullptr;
			if (!bOwnsSemanticId)
			{
				bOwnsSemanticId = Function.Regions.ContainsByPredicate(
					[&SemanticId](const FFinareAuthoringRegion& Region)
					{
						return Region.Id == SemanticId ||
							Region.DirectStatementIds.Contains(SemanticId);
					});
			}
			if (bOwnsSemanticId)
			{
				OwningFunctionId = Function.Id;
				break;
			}
		}
		if (!OwningFunctionId.IsEmpty())
		{
			break;
		}
	}
	if (!OwningFunctionId.IsEmpty() && ActiveFunctionId != OwningFunctionId &&
		!SelectFunction(OwningFunctionId))
	{
		return false;
	}

	const FFinareSurfaceTreeItemPtr Item = FindSurfaceItem(SemanticId);
	if (!Item.IsValid())
	{
		return false;
	}
	if (TreeView.IsValid())
	{
		TreeView->SetSelection(Item, ESelectInfo::Direct);
		TreeView->RequestScrollIntoView(Item);
	}
	if (GraphEditor.IsValid())
	{
		if (const TWeakObjectPtr<UFinareEdGraphNode>* Node =
			GraphNodesBySemanticId.Find(SemanticId))
		{
			if (Node->IsValid())
			{
				GraphEditor->JumpToNode(Node->Get(), false, true);
			}
		}
	}
	RebuildInspector(Item);
	return true;
}

bool SFinareAuthoringSurface::HasNodePaletteAction(const FString& ActionId) const
{
	return AuthoringGraph.IsValid() && AuthoringGraph->PaletteActions.ContainsByPredicate(
		[&ActionId](const FFinareGraphPaletteAction& Action)
		{
			return Action.ActionId == ActionId;
		});
}

bool SFinareAuthoringSurface::GetNodePaletteAction(
	const FString& ActionId,
	FFinareGraphPaletteAction& OutAction) const
{
	if (!AuthoringGraph.IsValid())
	{
		return false;
	}
	const FFinareGraphPaletteAction* Action =
		AuthoringGraph->PaletteActions.FindByPredicate(
			[&ActionId](const FFinareGraphPaletteAction& Candidate)
			{
				return Candidate.ActionId == ActionId;
			});
	if (Action == nullptr)
	{
		return false;
	}
	OutAction = *Action;
	return true;
}

bool SFinareAuthoringSurface::ToggleDebugBreakpoint(const FString& ActionId)
{
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			if (const FFinareAuthoringAction* Action = Function.FindAction(ActionId))
			{
				if (!DebugPanel.IsValid() || Action->DebugTargetId.IsEmpty())
				{
					return false;
				}
				const bool bChanged = DebugPanel->ToggleBreakpoint(Action->DebugTargetId);
				RefreshDebugNodeStates();
				return bChanged;
			}
		}
	}
	return false;
}

bool SFinareAuthoringSurface::IsDebugBreakpointEnabled(
	const FString& ActionId) const
{
	if (!DebugPanel.IsValid())
	{
		return false;
	}
	for (const FFinareAuthoringAsset& Asset : Document.Assets)
	{
		for (const FFinareAuthoringFunction& Function : Asset.Functions)
		{
			if (const FFinareAuthoringAction* Action = Function.FindAction(ActionId))
			{
				return !Action->DebugTargetId.IsEmpty() &&
					DebugPanel->IsBreakpointEnabled(Action->DebugTargetId);
			}
		}
	}
	return false;
}

TSharedRef<SWidget> SFinareAuthoringSurface::BuildAdvancedNodeDetails(
	const FFinareSurfaceTreeItemPtr& Item,
	const FFinareAuthoringAction& Action)
{
	TSharedRef<SVerticalBox> Body = SNew(SVerticalBox);
	Body->AddSlot().AutoHeight()
	[
		UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
			TEXT("NODE CONTRACT"),
			Action.GraphKind + TEXT("  ·  ") + Action.Category)
	];
	Body->AddSlot().AutoHeight()
	[
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.0f)
		[
			UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
				TEXT("SEMANTIC ID"), Action.Id)
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SButton)
			.Text(LOCTEXT("CopyAdvancedNodeId", "Copy ID"))
			.OnClicked(this, &SFinareAuthoringSurface::HandleCopyId, Action.Id)
		]
	];
	if (!Action.DebugTargetId.IsEmpty())
	{
		Body->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 8.0f)
		[
			SNew(SButton)
			.Text_Lambda([this, ActionId = Action.Id]()
			{
				return IsDebugBreakpointEnabled(ActionId)
					       ? LOCTEXT("RemoveSourceBreakpoint", "Remove Semantic Breakpoint")
					       : LOCTEXT("AddSourceBreakpoint", "Add Semantic Breakpoint");
			})
			.ToolTipText(FText::FromString(FString::Printf(
				TEXT("Bind the graph card and C# source span through debug target %s."),
				*Action.DebugTargetId)))
			.OnClicked_Lambda([this, ActionId = Action.Id]()
			{
				ToggleDebugBreakpoint(ActionId);
				return FReply::Handled();
			})
		];
	}

	const FString Effects = Action.Properties.FindRef(TEXT("effects"));
	const FString GraphSafe = Action.Properties.FindRef(TEXT("graphSafe"));
	const FString ResolvedType = Action.Properties.FindRef(TEXT("resolvedType"));
	const FString TypeId = Action.Properties.FindRef(TEXT("typeId"));
	Body->AddSlot().AutoHeight()
	[
		UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
			TEXT("EFFECT CONTRACT"),
			(Effects.IsEmpty() ? TEXT("none") : Effects) +
			(GraphSafe.IsEmpty() ? FString() : TEXT("  ·  graphSafe=") + GraphSafe))
	];
	if (!ResolvedType.IsEmpty() || !TypeId.IsEmpty())
	{
		Body->AddSlot().AutoHeight()
		[
			UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
				TEXT("RESOLVED TYPE"),
				ResolvedType + (TypeId.IsEmpty() ? FString() : TEXT("\n") + TypeId))
		];
	}

	if (!Action.Ports.IsEmpty())
	{
		Body->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 7.0f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("AdvancedPortContract", "PORT CONTRACT"))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		];
		for (const FFinareAuthoringPort& Port : Action.Ports)
		{
			const FString PortValue = FString::Printf(
				TEXT("%s  ·  %s\nIR name: %s%s"),
				*Port.Direction,
				Port.IsControl()
					? TEXT("control flow")
					: (Port.TypeDisplayName.IsEmpty() ? TEXT("unknown data") : *Port.TypeDisplayName),
				*Port.Name,
				Port.TypeId.IsEmpty() ? TEXT("") : *FString::Printf(TEXT("\nType ID: %s"), *Port.TypeId));
			Body->AddSlot().AutoHeight()
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
					Port.IsInput() ? TEXT("INPUT PIN") : TEXT("OUTPUT PIN"),
					PortValue)
			];
		}
	}

	TArray<FString> PropertyKeys;
	Action.Properties.GetKeys(PropertyKeys);
	PropertyKeys.Sort();
	if (!PropertyKeys.IsEmpty())
	{
		Body->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 7.0f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT(
				"AdvancedGeneratedMetadata",
				"COMPILER / GENERATED API METADATA"))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		];
		for (const FString& Key : PropertyKeys)
		{
			Body->AddSlot().AutoHeight()
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
					Key,
					Action.Properties.FindRef(Key))
			];
		}
	}

	if (!Action.DeclarationReferenceIds.IsEmpty())
	{
		Body->AddSlot().AutoHeight()
		[
			UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
				TEXT("DECLARATION REFERENCES"),
				FString::Join(Action.DeclarationReferenceIds, TEXT("\n")))
		];
	}
	if (!Action.EditCapabilities.IsEmpty())
	{
		TArray<FString> EditContracts;
		for (const FFinareAuthoringEdit& Edit : Action.EditCapabilities)
		{
			EditContracts.Add(Edit.Kind + TEXT("  ·  ") + Edit.Operation);
		}
		Body->AddSlot().AutoHeight()
		[
			UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
				TEXT("SOURCE EDIT CAPABILITIES"),
				FString::Join(EditContracts, TEXT("\n")))
		];
	}

	if (Item->Span.IsValid())
	{
		Body->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.0f)
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
					TEXT("SOURCE"), Item->Span.ToDisplayString())
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SButton)
				.Text(LOCTEXT("OpenAdvancedNodeSource", "Open Source"))
				.OnClicked(this, &SFinareAuthoringSurface::HandleOpenSource, Item->Span)
			]
		];
		Body->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 8.0f)
		[
			SNew(SBox)
			.MinDesiredHeight(130.0f)
			[
				SNew(SMultiLineEditableTextBox)
				.Text(FText::FromString(BuildSourcePreview(Item->Span)))
				.IsReadOnly(true)
				.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
			]
		];
	}
	return Body;
}

void SFinareAuthoringSurface::RebuildInspector(
	const FFinareSurfaceTreeItemPtr& Item)
{
	InspectorBox->ClearChildren();
	if (!Item.IsValid())
	{
		InspectorBox->AddSlot().AutoHeight()
		[
			SNew(STextBlock)
			.Text(LOCTEXT("EmptyInspector", "Load an Authoring Surface and select an item."))
			.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
		];
		return;
	}

	const FFinareAuthoringFunction* ActiveFunction = FindFunction(ActiveFunctionId);
	const FFinareAuthoringAction* ActiveAction =
		ActiveFunction != nullptr && Item->Kind == EFinareSurfaceTreeItemKind::Action
			? ActiveFunction->FindAction(Item->SemanticId)
			: nullptr;
	const FString InspectorSummary = ActiveAction != nullptr
		                                 ? FFinareGraphPresentation::GetNodeSubtitle(*ActiveAction)
		                                 : Item->Detail;

	InspectorBox->AddSlot().AutoHeight()
	[
		SNew(STextBlock)
		.Text(FText::FromString(Item->Label))
		.Font(FAppStyle::GetFontStyle(TEXT("HeadingExtraSmall")))
	];
	InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 14.0f)
	[
		SNew(STextBlock)
		.Text(FText::FromString(InspectorSummary))
		.Visibility(InspectorSummary.IsEmpty() ? EVisibility::Collapsed : EVisibility::Visible)
		.AutoWrapText(true)
		.ColorAndOpacity(FLinearColor(0.68f, 0.7f, 0.74f))
	];

	if ((Item->Kind == EFinareSurfaceTreeItemKind::State ||
			Item->Kind == EFinareSurfaceTreeItemKind::Function) &&
		!Item->SemanticId.IsEmpty())
	{
		if (const FFinareAuthoringAsset* Owner =
			Document.FindDeclarationOwnerAsset(Item->SemanticId))
		{
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 10.0f)
			[
				UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
					TEXT("SOURCE OWNER"),
					FString::Printf(TEXT("%s  ·  %s"), *Owner->QualifiedName, *Owner->AssetPath))
			];
		}
	}

	if (ActiveAction != nullptr)
	{
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 10.0f)
		[
			SNew(STextBlock)
			.Text(FText::FromString(
				TEXT("Action") +
				(ActiveAction->Category.IsEmpty()
					 ? FString()
					 : TEXT("  ·  ") + ActiveAction->Category)))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
			.ColorAndOpacity(FLinearColor(0.48f, 0.53f, 0.60f))
		];
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 12.0f)
		[
			SNew(SExpandableArea)
			.InitiallyCollapsed(true)
			.AreaTitle(LOCTEXT(
				"AdvancedNodeDetails",
				"Advanced Node Details  ·  types, effects, source, IDs, API"))
			.Padding(FMargin(8.0f, 6.0f))
			.BodyContent()
			[
				BuildAdvancedNodeDetails(Item, *ActiveAction)
			]
		];
	}
	else
	{
		InspectorBox->AddSlot().AutoHeight()
		[
			UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
				TEXT("KIND"),
				GetKindLabel(Item->Kind) +
				(Item->Category.IsEmpty() ? FString() : TEXT("  ·  ") + Item->Category) +
				(Item->GraphKind.IsEmpty() ? FString() : TEXT("  ·  ") + Item->GraphKind))
		];
		if (!Item->SemanticId.IsEmpty())
		{
			InspectorBox->AddSlot().AutoHeight()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.0f)
				[
					UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
						TEXT("SEMANTIC ID"), Item->SemanticId)
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(SButton)
					.Text(LOCTEXT("CopyId", "Copy ID"))
					.OnClicked(this, &SFinareAuthoringSurface::HandleCopyId, Item->SemanticId)
				]
			];
		}
		if (Item->Span.IsValid())
		{
			InspectorBox->AddSlot().AutoHeight()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.0f)
				[
					UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorValue(
						TEXT("SOURCE"), Item->Span.ToDisplayString())
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(SButton)
					.Text(LOCTEXT("OpenSource", "Open Source"))
					.OnClicked(this, &SFinareAuthoringSurface::HandleOpenSource, Item->Span)
				]
			];
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 14.0f)
			[
				SNew(SBox)
				.MinDesiredHeight(130.0f)
				[
					SNew(SMultiLineEditableTextBox)
					.Text(FText::FromString(BuildSourcePreview(Item->Span)))
					.IsReadOnly(true)
					.Font(FAppStyle::GetFontStyle(TEXT("MonoFont")))
				]
			];
		}
	}

	if (Item->Kind == EFinareSurfaceTreeItemKind::Asset)
	{
		const FFinareAuthoringAsset* Asset = FindAsset(Item->SemanticId);
		if (Asset != nullptr)
		{
			TSharedPtr<SEditableTextBox> AssetPathEditor;
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("AssetDeclarationAuthoring", "SCRIPT ASSET AUTHORING"))
				.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
			];
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SBorder)
				.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
				.Padding(8.0f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
							TEXT("ENGINE-FACING ASSET PATH"))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 3.0f, 0.0f, 8.0f)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.0f)
						[
							SAssignNew(AssetPathEditor, SEditableTextBox)
							.Text(FText::FromString(Asset->AssetPath))
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("ApplyAssetPath", "Preview Path"))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleUpdateScriptAssetPath,
								Asset->Id,
								AssetPathEditor)
						]
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth()
						[
							SNew(SButton)
							.Text(LOCTEXT("AddAssetState", "+ State"))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleAddState,
								Asset->Id)
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("AddAssetFunction", "+ Function"))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleAddFunction,
								Asset->Id)
						]
						+ SHorizontalBox::Slot().AutoWidth()
						[
							SNew(SButton)
							.Text(LOCTEXT("RemoveScriptAsset", "Remove Asset"))
							.ToolTipText(LOCTEXT(
								"RemoveScriptAssetTip",
								"Preview removal of the complete C# Script Asset type before any source write."))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleRemoveScriptAsset,
								Asset->Id)
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 10.0f, 0.0f, 0.0f)
					[
						([&]() -> TSharedRef<SWidget>
						{
							FFinareClassBindingDeclaration Binding;
							const bool bEditableBinding = FindClassBinding(Asset->Id, Binding) &&
								(Binding.Kind == TEXT("Actor") || Binding.Kind == TEXT("ActorComponent"));
							if (!bEditableBinding)
							{
								return SNew(SWidgetSwitcher)
									.Visibility(EVisibility::Collapsed);
							}
							TSharedPtr<SEditableTextBox> BaseClassEditor;
							const FString CurrentBase = Binding.BaseClassPath.IsSet()
								                            ? Binding.BaseClassPath.GetValue()
								                            : FString();
							return SNew(SBorder)
								.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
								.Padding(8.0f)
								[
									SNew(SVerticalBox)
									+ SVerticalBox::Slot().AutoHeight()
									[
										UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
											TEXT("NATIVE BASE CLASS (/Script/... or /Game/..._C)"))
									]
									+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 3.0f, 0.0f, 0.0f)
									[
										SNew(SHorizontalBox)
										+ SHorizontalBox::Slot().FillWidth(1.0f)
										[
											SAssignNew(BaseClassEditor, SEditableTextBox)
											.Text(FText::FromString(CurrentBase))
											.HintText(LOCTEXT("NativeBaseClassHint", "/Script/Engine.Actor"))
										]
										+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
										[
											SNew(SButton)
											.Text(LOCTEXT("ApplyNativeBaseClass", "Preview Base"))
											.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
											.OnClicked(
												this,
												&SFinareAuthoringSurface::HandleUpdateScriptClassBase,
												Asset->Id,
												Binding,
												BaseClassEditor)
										]
										+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
										[
											SNew(SButton)
											.Text(LOCTEXT("ClearNativeBaseClass", "Clear"))
											.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
											.OnClicked_Lambda(
												[this, AssetId = Asset->Id, Binding, BaseClassEditor]() mutable
												{
													BaseClassEditor->SetText(FText::GetEmpty());
													return HandleUpdateScriptClassBase(
														AssetId, Binding, BaseClassEditor);
												})
										]
									]
								];
						}())
					]
				]
			];

			const auto HasFunction = [Asset](const TCHAR* Name)
			{
				return Asset->Functions.ContainsByPredicate([Name](
					const FFinareAuthoringFunction& Function)
					{
						return Function.Name == Name;
					});
			};
			const bool bHasAnyActorLifecycle = HasFunction(TEXT("OnBeginPlay")) ||
				HasFunction(TEXT("OnTick")) || HasFunction(TEXT("OnEndPlay"));
			if (bHasAnyActorLifecycle &&
				(!HasFunction(TEXT("OnBeginPlay")) || !HasFunction(TEXT("OnTick")) ||
					!HasFunction(TEXT("OnEndPlay"))))
			{
				InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 10.0f)
				[
					SNew(SBorder)
					.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
					.Padding(8.0f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot().AutoHeight()
						[
							UE::Finare::AuthoringSurface::Slate::Private::MakeInspectorLabel(
								TEXT("ACTOR LIFECYCLE HOOKS"))
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 5.0f, 0.0f, 0.0f)
						[
							SNew(SHorizontalBox)
							+ SHorizontalBox::Slot().AutoWidth()
							[
								SNew(SButton)
								.Text(LOCTEXT("AddBeginPlayHook", "+ BeginPlay"))
								.Visibility(HasFunction(TEXT("OnBeginPlay"))
									            ? EVisibility::Collapsed
									            : EVisibility::Visible)
								.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
								.OnClicked(
									this,
									&SFinareAuthoringSurface::HandleAddLifecycleHook,
									Asset->Id,
									FString(TEXT("beginPlay")))
							]
							+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f)
							[
								SNew(SButton)
								.Text(LOCTEXT("AddTickHook", "+ Tick"))
								.Visibility(HasFunction(TEXT("OnTick"))
									            ? EVisibility::Collapsed
									            : EVisibility::Visible)
								.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
								.OnClicked(
									this,
									&SFinareAuthoringSurface::HandleAddLifecycleHook,
									Asset->Id,
									FString(TEXT("tick")))
							]
							+ SHorizontalBox::Slot().AutoWidth()
							[
								SNew(SButton)
								.Text(LOCTEXT("AddEndPlayHook", "+ EndPlay"))
								.Visibility(HasFunction(TEXT("OnEndPlay"))
									            ? EVisibility::Collapsed
									            : EVisibility::Visible)
								.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
								.OnClicked(
									this,
									&SFinareAuthoringSurface::HandleAddLifecycleHook,
									Asset->Id,
									FString(TEXT("endPlay")))
							]
						]
					]
				];
			}
		}
	}
	else if (Item->Kind == EFinareSurfaceTreeItemKind::State)
	{
		if (const FFinareAuthoringState* State = FindState(Item->SemanticId))
		{
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 10.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.Text(LOCTEXT("EditScriptState", "Edit State..."))
					.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleEditState,
						State->Id)
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f)
				[
					SNew(SButton)
					.Text(LOCTEXT("RemoveScriptState", "Remove State"))
					.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleRemoveState,
						State->Id)
				]
			];
		}
	}
	else if (Item->Kind == EFinareSurfaceTreeItemKind::Function)
	{
		if (const FFinareAuthoringFunction* Function = FindFunction(Item->SemanticId))
		{
			const bool bGeneratedSignature =
				IsEventFunction(*Function) || IsLifecycleFunction(*Function);
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 10.0f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton)
					.Text(LOCTEXT("EditVisualFunction", "Edit Function..."))
					.Visibility(bGeneratedSignature
						            ? EVisibility::Collapsed
						            : EVisibility::Visible)
					.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleEditFunction,
						Function->Id)
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f)
				[
					SNew(SButton)
					.Text(IsEventFunction(*Function)
						      ? LOCTEXT("RemoveEventEntry", "Remove Event Entry")
						      : LOCTEXT("RemoveVisualFunction", "Remove Function"))
					.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
					.OnClicked(
						this,
						&SFinareAuthoringSurface::HandleRemoveFunction,
						Function->Id)
				]
			];
		}
	}

	const FFinareAuthoringRegion* ActiveRegion =
		ActiveFunction != nullptr && Item->Kind == EFinareSurfaceTreeItemKind::Region
			? ActiveFunction->FindRegion(Item->SemanticId)
			: nullptr;
	const FFinareAuthoringRegion* ActiveRegionParent =
		ActiveFunction != nullptr && ActiveRegion != nullptr
			? ActiveFunction->FindRegion(ActiveRegion->ParentRegionId)
			: nullptr;
	if (ActiveFunction != nullptr && ActiveAction != nullptr)
	{
		TArray<const FFinareAuthoringEdge*> InputEdges;
		for (const FFinareAuthoringPort& Port : ActiveAction->Ports)
		{
			if (!Port.IsInput() || Port.IsControl())
			{
				continue;
			}
			if (const FFinareAuthoringEdge* Edge = ActiveFunction->Edges.FindByPredicate(
				[ActiveAction, &Port](const FFinareAuthoringEdge& Candidate)
				{
					return Candidate.Kind == TEXT("data") &&
						Candidate.ToNode == ActiveAction->Id &&
						Candidate.ToPort == Port.Name;
				}))
			{
				InputEdges.Add(Edge);
			}
		}

		if (!InputEdges.IsEmpty())
		{
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
			[
				SNew(STextBlock)
				.Text(LOCTEXT("PinAuthoring", "PIN AUTHORING"))
				.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
			];
		}
		for (const FFinareAuthoringEdge* Edge : InputEdges)
		{
			const FFinareAuthoringPort* Port =
				ActiveAction->FindPort(Edge->ToPort, TEXT("input"));
			const FFinareAuthoringAction* Source =
				ActiveFunction->FindAction(Edge->FromNode);
			if (Port == nullptr || Source == nullptr)
			{
				continue;
			}

			const FString CurrentValue = Source->GraphKind == TEXT("literal")
				                             ? Source->Properties.FindRef(TEXT("value"))
				                             : FString();
			const FString SourceText = FString::Printf(
				TEXT("%s  ·  %s  ·  source %s"),
				*Port->Name,
				*Port->TypeDisplayName,
				*(Source->Summary.IsEmpty() ? Source->Title : Source->Summary));
			TSharedPtr<SEditableTextBox> ValueEditor;
			TSharedPtr<SEditableTextBox> NameEditor;
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SBorder)
				.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
				.Padding(8.0f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock)
						.Text(FText::FromString(SourceText))
						.AutoWrapText(true)
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 6.0f, 0.0f, 0.0f)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.0f)
						[
							SAssignNew(ValueEditor, SEditableTextBox)
							.Text(FText::FromString(CurrentValue))
							.HintText(LOCTEXT("PinLiteralHint", "C# literal, e.g. 42, 1.0f, false, \"text\""))
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("ApplyPinValue", "Set Value"))
							.ToolTipText(LOCTEXT(
								"ApplyPinValueTip",
								"Replace this exact compiler-owned input edge with a type-checked C# literal."))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleApplyDataInputValue,
								ActiveAction->Id,
								Port->Name,
								ValueEditor)
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("ResetPinValue", "Reset"))
							.ToolTipText(LOCTEXT(
								"ResetPinValueTip",
								"Replace this input with its bounded typed default value."))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandleResetDataInputValue,
								ActiveAction->Id,
								Port->Name)
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 6.0f, 0.0f, 0.0f)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.0f)
						[
							SAssignNew(NameEditor, SEditableTextBox)
							.Text(FText::FromString(
								UE::Finare::AuthoringSurface::Slate::Private::
								SuggestPromotedLocalName(*ActiveFunction, *Port)))
							.HintText(LOCTEXT("PromotedLocalNameHint", "local name"))
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("PromotePinToLocal", "Promote to Local"))
							.ToolTipText(LOCTEXT(
								"PromotePinToLocalTip",
								"Introduce a typed local immediately before the containing statement and replace this source expression with the local reference."))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(
								this,
								&SFinareAuthoringSurface::HandlePromoteDataInput,
								ActiveAction->Id,
								Port->Name,
								NameEditor)
						]
					]
				]
			];
		}
	}
	if (ActiveRegion != nullptr && ActiveRegion->Kind == TEXT("block") &&
		ActiveRegionParent != nullptr && ActiveRegionParent->Kind != TEXT("function"))
	{
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 10.0f)
		[
			SNew(SButton)
			.Text(LOCTEXT("ExpandLexicalScope", "Expand Scope"))
			.ToolTipText(LOCTEXT(
				"ExpandLexicalScopeTip",
				"Move this lexical scope's statements back into its parent as one Workbench transaction."))
			.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
			.OnClicked(
				this,
				&SFinareAuthoringSurface::HandleExpandBlockRegion,
				ActiveRegion->Id)
		];
	}
	if (ActiveFunction != nullptr && ActiveAction != nullptr &&
		ActiveAction->GraphKind == TEXT("switch"))
	{
		TSharedPtr<SEditableTextBox> LabelEditor;
		const bool bHasDefault =
			ActiveAction->Properties.FindRef(TEXT("hasDefault")) == TEXT("true");
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("SwitchSections", "SWITCH SECTIONS"))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		];
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 10.0f)
		[
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
			.Padding(8.0f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock)
					.Text(LOCTEXT(
						"SwitchPatternHelp",
						"Pattern: 0, > 10, int value, var value, _, not ..., (...), and/or; optional 'when boolParameter'."))
					.AutoWrapText(true)
					.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
					.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 6.0f, 0.0f, 0.0f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.0f)
					[
						SAssignNew(LabelEditor, SEditableTextBox)
						.Text(FText::FromString(
							UE::Finare::AuthoringSurface::Slate::Private::
							SuggestSwitchCaseLabel(*ActiveFunction, *ActiveAction)))
						.HintText(LOCTEXT("SwitchCaseHint", "case pattern"))
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("AddSwitchCase", "Add Case"))
						.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleAddSwitchSection,
							ActiveAction->Id,
							LabelEditor,
							false)
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("AddSwitchDefault", "Add Default"))
						.IsEnabled_Lambda([this, bHasDefault]()
						{
							return !bPatchRequestPending && !bHasDefault;
						})
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleAddSwitchSection,
							ActiveAction->Id,
							LabelEditor,
							true)
					]
				]
			]
		];
	}
	else if (ActiveFunction != nullptr && ActiveAction != nullptr &&
		ActiveAction->GraphKind == TEXT("switchCase"))
	{
		TSharedPtr<SEditableTextBox> LabelEditor;
		FString CurrentLabel = ActiveAction->Properties.FindRef(TEXT("displayText"));
		if (CurrentLabel.StartsWith(TEXT("case ")))
		{
			CurrentLabel.RightChopInline(5);
		}
		if (CurrentLabel.EndsWith(TEXT(":")))
		{
			CurrentLabel.LeftChopInline(1);
		}
		if (CurrentLabel.IsEmpty())
		{
			CurrentLabel = ActiveAction->Properties.FindRef(TEXT("isDefault")) == TEXT("true")
				               ? TEXT("default")
				               : ActiveAction->Properties.FindRef(TEXT("patternText"));
		}

		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("SwitchSectionEdit", "SWITCH SECTION"))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		];
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 10.0f)
		[
			SNew(SBorder)
			.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
			.Padding(8.0f)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SAssignNew(LabelEditor, SEditableTextBox)
					.Text(FText::FromString(CurrentLabel))
					.HintText(LOCTEXT("SwitchSectionLabelHint", "case pattern or default"))
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 6.0f, 0.0f, 0.0f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SNew(SButton)
						.Text(LOCTEXT("ApplySwitchLabel", "Apply Label"))
						.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleReplaceSwitchSection,
							ActiveAction->Id,
							LabelEditor)
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("MoveSwitchSectionUp", "Move Up"))
						.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleMoveSwitchSection,
							ActiveAction->Id,
							-1)
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("MoveSwitchSectionDown", "Move Down"))
						.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleMoveSwitchSection,
							ActiveAction->Id,
							1)
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
					[
						SNew(SButton)
						.Text(LOCTEXT("DeleteSwitchSection", "Delete Section"))
						.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
						.OnClicked(
							this,
							&SFinareAuthoringSurface::HandleDeleteSwitchSection,
							ActiveAction->Id)
					]
				]
			]
		];
	}

	if (!Item->Edits.IsEmpty())
	{
		InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 4.0f, 0.0f, 8.0f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("SemanticEdits", "SEMANTIC EDITS"))
			.Font(FAppStyle::GetFontStyle(TEXT("SmallFontBold")))
		];
		for (const FFinareAuthoringEdit& Edit : Item->Edits)
		{
			TSharedPtr<SEditableTextBox> ValueEditor;
			const FString Options = Edit.Options.IsEmpty()
				                        ? FString()
				                        : FString::Printf(
					                        TEXT("Options: %s"), *FString::Join(Edit.Options, TEXT("  ")));
			InspectorBox->AddSlot().AutoHeight().Padding(0.0f, 0.0f, 0.0f, 8.0f)
			[
				SNew(SBorder)
				.BorderImage(FAppStyle::GetBrush(TEXT("ToolPanel.GroupBorder")))
				.Padding(8.0f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Text(FText::FromString(Edit.Label))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0.0f, 2.0f, 0.0f, 5.0f)
					[
						SNew(STextBlock)
						.Text(FText::FromString(Options))
						.Font(FAppStyle::GetFontStyle(TEXT("SmallFont")))
						.ColorAndOpacity(FLinearColor(0.55f, 0.58f, 0.62f))
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.0f)
						[
							SAssignNew(ValueEditor, SEditableTextBox)
							.Text(FText::FromString(Edit.CurrentValue))
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("CopyPatch", "Copy Patch"))
							.ToolTipText(LOCTEXT("CopyPatchTip",
							                     "Copy a typed semantic-patch v0.28 payload using this value."))
							.OnClicked(this, &SFinareAuthoringSurface::HandleCopyPatch, Edit, ValueEditor)
						]
						+ SHorizontalBox::Slot().AutoWidth().Padding(6.0f, 0.0f, 0.0f, 0.0f)
						[
							SNew(SButton)
							.Text(LOCTEXT("ApplyPatch", "Apply & Reload"))
							.ToolTipText(LOCTEXT(
								"ApplyPatchTip",
								"Submit the typed patch to the transactional Workbench and reload matching components."))
							.IsEnabled_Lambda([this]() { return !bPatchRequestPending; })
							.OnClicked(this, &SFinareAuthoringSurface::HandleApplyPatch, Edit, ValueEditor)
						]
					]
				]
			];
		}
	}
}

void SFinareAuthoringSurface::SetStatus(FString Message, bool bError)
{
	StatusMessage = MoveTemp(Message);
	bStatusError = bError;
}

FString SFinareAuthoringSurface::ResolveSourcePath(const FString& SourcePath) const
{
	TArray<FString> Candidates;
	if (!FPaths::IsRelative(SourcePath))
	{
		Candidates.Add(SourcePath);
	}
	else
	{
		Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), SourcePath));
		Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), TEXT(".."), SourcePath));
		if (!LoadedSurfacePath.IsEmpty())
		{
			Candidates.Add(FPaths::Combine(FPaths::GetPath(LoadedSurfacePath), SourcePath));
		}
	}
	for (FString Candidate : Candidates)
	{
		Candidate = FPaths::ConvertRelativePathToFull(Candidate);
		FPaths::NormalizeFilename(Candidate);
		if (IFileManager::Get().FileExists(*Candidate))
		{
			return Candidate;
		}
	}
	return FString();
}

FString SFinareAuthoringSurface::BuildSourcePreview(
	const FFinareAuthoringSpan& Span) const
{
	const FString Path = ResolveSourcePath(Span.FilePath);
	FString Source;
	if (Path.IsEmpty() || !FFileHelper::LoadFileToString(Source, *Path))
	{
		return FString::Printf(TEXT("Source preview unavailable\n%s"), *Span.ToDisplayString());
	}
	TArray<FString> Lines;
	Source.ParseIntoArrayLines(Lines, false);
	const int32 FirstLine = FMath::Max(1, Span.StartLine - 2);
	const int32 LastLine = FMath::Min(Lines.Num(), FMath::Max(Span.EndLine, Span.StartLine) + 2);
	TArray<FString> Preview;
	for (int32 Line = FirstLine; Line <= LastLine; ++Line)
	{
		Preview.Add(FString::Printf(
			TEXT("%s %4d  %s"),
			Line >= Span.StartLine && Line <= Span.EndLine ? TEXT(">") : TEXT(" "),
			Line,
			*Lines[Line - 1]));
	}
	return FString::Join(Preview, TEXT("\n"));
}

FString SFinareAuthoringSurface::BuildFunctionSignature(
	const FFinareAuthoringFunction& Function) const
{
	TArray<FString> Parameters;
	for (const FFinareAuthoringParameter& Parameter : Function.Parameters)
	{
		Parameters.Add(Parameter.TypeMetadataName + TEXT(" ") + Parameter.Name);
	}
	return FString::Printf(
		TEXT("%s(%s) -> %s"),
		*Function.Name,
		*FString::Join(Parameters, TEXT(", ")),
		*Function.ReturnTypeMetadataName);
}

FString SFinareAuthoringSurface::GetKindLabel(
	EFinareSurfaceTreeItemKind Kind)
{
	switch (Kind)
	{
	case EFinareSurfaceTreeItemKind::Asset: return TEXT("SCRIPT ASSET");
	case EFinareSurfaceTreeItemKind::Group: return TEXT("GROUP");
	case EFinareSurfaceTreeItemKind::State: return TEXT("STATE");
	case EFinareSurfaceTreeItemKind::Function: return TEXT("FUNCTION");
	case EFinareSurfaceTreeItemKind::Region: return TEXT("REGION");
	case EFinareSurfaceTreeItemKind::Action: return TEXT("ACTION");
	default: return TEXT("ITEM");
	}
}

FSlateColor SFinareAuthoringSurface::GetKindColor(
	EFinareSurfaceTreeItemKind Kind)
{
	switch (Kind)
	{
	case EFinareSurfaceTreeItemKind::Asset: return FLinearColor(0.2f, 0.55f, 0.95f);
	case EFinareSurfaceTreeItemKind::State: return FLinearColor(0.65f, 0.38f, 0.92f);
	case EFinareSurfaceTreeItemKind::Function: return FLinearColor(0.15f, 0.72f, 0.62f);
	case EFinareSurfaceTreeItemKind::Region: return FLinearColor(0.95f, 0.62f, 0.18f);
	case EFinareSurfaceTreeItemKind::Action: return FLinearColor(0.88f, 0.32f, 0.34f);
	default: return FLinearColor(0.42f, 0.45f, 0.5f);
	}
}

#undef LOCTEXT_NAMESPACE
