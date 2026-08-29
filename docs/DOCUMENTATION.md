# UnifiedScript documentation

This is the curated documentation entry point shipped with UnifiedScript
`1.0.0-rc.1`. The checked-in
`release/unifiedscript.release.json` is the machine-readable source of truth for
this catalog; SDK packaging fails if a listed document is missing, duplicated,
unsafe, or outside `docs`.

## Start here

- [SDK distribution and installation](UNREAL_SDK.md)
- [First script walkthrough](UNREAL_FIRST_SCRIPT.md)
- [Project onboarding](UNREAL_ONBOARDING.md)
- [Script deployment](UNREAL_DEPLOYMENT.md)
- [Executable sample matrix](UNREAL_SAMPLES.md)

## Authoring and API

- [Visual and C# authoring model](VISUAL_AUTHORING_MODEL.md)
- [Native Authoring Surface](AUTHORING_SURFACE.md)
- [Authoring diagnostics](AUTHORING_DIAGNOSTICS.md)
- [Semantic Graph diff and merge](SEMANTIC_DIFF_AND_MERGE.md)
- [Typed AI semantic patch proposals](AI_SEMANTIC_PROPOSALS.md)
- [Unreal API Contract](UNREAL_API_CONTRACT.md)
- [Project templates and generated API browser](PROJECT_TEMPLATES_AND_API_BROWSER.md)
- [Generated data-plane extensions](DATA_PLANE_EXTENSIONS.md)

## Debugging and profiling

- [Runtime diagnostics](RUNTIME_DIAGNOSTICS.md)
- [Native debug workbench](NATIVE_DEBUG_WORKBENCH.md)
- [Hot reload reliability](HOT_RELOAD_RELIABILITY.md)
- [Gameplay and bridge profiler](GAMEPLAY_PROFILER.md)
- [Integrated runtime insights](INTEGRATED_RUNTIME_INSIGHTS.md)
- [Matched performance workload suite](PERFORMANCE_WORKLOAD_SUITE.md)
- [Unreal performance gates](UNREAL_PERFORMANCE.md)

## Packaging, upgrades, and migration

- [Win64 release packaging](UNREAL_RELEASE.md)
- [Compatibility matrix](UNREAL_COMPATIBILITY_MATRIX.md)
- [Compiled program chunks](COMPILED_CHUNKS.md)
- [Versioning and upgrades](VERSIONING_AND_UPGRADES.md)
- [Puerts migration](PUERTS_MIGRATION.md)
- [Privacy-safe diagnostics bundles](DIAGNOSTICS_BUNDLES.md)
- [UnifiedScript 1.0 RC1 release notes](RELEASE_NOTES_1_0_RC1.md)

## Support boundary

The current release declaration is UE 5.8.1, Win64, and .NET 10. A document
describing a mechanism does not widen that declaration. Comparative performance
claims require the matched workload artifacts described in the performance
suite, and generated migration templates do not count as gameplay-equivalence
evidence until both runtime lanes provide completed captures.
