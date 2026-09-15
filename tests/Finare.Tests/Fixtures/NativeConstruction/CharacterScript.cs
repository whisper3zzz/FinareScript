using Finare;
using Finare.Unreal;
using Finare.NativeConstruction.Generated;

namespace Finare.NativeConstruction;

[ScriptAsset("Game/FinareAutomation/NativeConstruction/Character", ClassId = "us_bfd54612a1bd4f51962f93be6912c704")]
[UnrealScriptClass(UnrealScriptClassKind.Actor, BaseClassPath = "/Script/Engine.Character",
    GeneratedClassPath = "/Game/FinareAutomation/NativeConstruction/BP_Character.BP_Character_C")]
public sealed partial class CharacterScript : UnrealActorScript
{
    [Expose] public partial CameraComponent? Camera { get; set; } = null;
    [Expose] public partial int ConstructionValue { get; set; } = 0;

    protected override void OnConstruct(UnrealConstructionContext context)
    {
        var character = context.Host.As<Character>()!;
        var camera = context.CreateDefaultSubobject<CameraComponent>("PlayerCamera");
        var mesh = context.CreateDefaultSubobject<StaticMeshComponent>("WeaponMesh");
        context.SetupAttachment(camera, character.CapsuleComponent!);
        context.SetupAttachment(mesh, camera);
        camera.FieldOfView = 93.0f;
        Camera = camera;
        ConstructionValue = 37;
    }

    protected override void OnBeginPlay()
    {
        var character = new Character(HostHandle, Bridge);
        if (Camera is null || ConstructionValue != 37)
            throw new InvalidOperationException("The UE constructor did not initialize the Character.");
        character.Tags = [new UnrealName("ConstructedBeginPlay")];
    }
}
