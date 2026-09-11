using System.Text.Json;
using Finare.Semantics;

namespace Finare.Tests;

[Trait("Category", "Compiler")]
public sealed class MethodEffectCatalogTests
{
    [Fact]
    public void DefaultCatalog_IsPureForMathAndConservativeForUnknownCalls()
    {
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault();

        Assert.Equal(
            MethodEffects.None,
            catalog.Resolve(new MethodEffectSubject("System.Private.CoreLib", "System.MathF", "Max")));
        Assert.Equal(
            MethodEffects.ReadsState | MethodEffects.WritesState,
            catalog.Resolve(new MethodEffectSubject("Game", "Game.Inventory", "Consume")));
    }

    [Fact]
    public void ReadDocument_ParsesFlagEffectsAndOverridesDefaultRules()
    {
        const string json = """
            {
              "schemaVersion": "unifiedscript.effects/v0.1",
              "rules": [
                {
                  "id": "game.inventory.consume",
                  "assembly": "Game",
                  "containingType": "Game.Inventory",
                  "method": "Consume",
                  "effects": "writesState, gameThreadOnly"
                }
              ]
            }
            """;
        MethodEffectCatalogDocument document = MethodEffectCatalog.ReadDocument(json);
        MethodEffectCatalog catalog = MethodEffectCatalog.CreateDefault().WithOverrides(document.Rules);

        Assert.Equal(
            MethodEffects.WritesState | MethodEffects.GameThreadOnly,
            catalog.Resolve(new MethodEffectSubject("Game", "Game.Inventory", "Consume")));
    }

    [Fact]
    public void Constructor_IsOrderIndependentAndRejectsAmbiguousRuleIds()
    {
        MethodEffectRule[] rules =
        [
            new("rule.b", "*", "Game.Api", "*", MethodEffects.ReadsState),
            new("rule.a", "Game", "Game.Api", "Load", MethodEffects.None),
        ];
        var forward = new MethodEffectCatalog(rules);
        var reverse = new MethodEffectCatalog(rules.Reverse());

        Assert.Equal(forward.Id, reverse.Id);
        Assert.Throws<ArgumentException>(() => new MethodEffectCatalog(
        [
            rules[0],
            rules[0] with { Method = "Save" },
        ]));
    }

    [Fact]
    public void ReadDocument_RejectsUnknownSchema()
    {
        const string json = """
            { "schemaVersion": "unifiedscript.effects/v99", "rules": [] }
            """;

        Assert.Throws<JsonException>(() => MethodEffectCatalog.ReadDocument(json));
    }
}
