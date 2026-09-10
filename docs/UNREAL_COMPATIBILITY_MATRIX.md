# Unreal compatibility and packaging matrix

Finare declares support only for rows that pass a real build and runtime
gate. The primary release-candidate lane is the downloaded Unreal Engine 5.8.1
Win64 build at `D:\UE_5.8`; a successful editor compile alone is not evidence
for another engine, target, or platform.

## Run the matrix

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-compatibility-matrix.ps1 `
  -EngineRoot D:\UE_5.8
```

The runner writes a versioned report under:

```text
artifacts/ue/matrix/<timestamp>/compatibility-matrix.json
```

It stops on the first failed phase, preserves that phase log and a failed
top-level report, and byte-for-byte restores the source HostProject's local
`DefaultEngine.ini` and `DefaultInput.ini` files.

## Declared matrix

| Target | Platform | Configuration | Required evidence |
| --- | --- | --- | --- |
| Editor | Win64 | Development | UBT build and all `Finare.Unreal` Automation tests |
| Game | Win64 | Development | Build, Cook, Stage, Pak/IoStore, Archive, closure checks, packaged chunk smoke |
| Game | Win64 | Shipping | Build, Cook, Stage, Pak/IoStore, Archive, closure checks, packaged chunk smoke |
| SDK-installed Game | Win64 | Development | install into a plugin-free project export, compile with packaged CLI/runtime, then run the complete package smoke |

Every Game row must contain `.pak`, `.utoc`, and `.ucas` outputs. Its packaged
process must start managed `OnStart`, `OnTick`, and `OnStop` from a verified
`.uschunk`, exit successfully, and report a `hostfxr.dll` below the packaged
`Plugins/Finare/Binaries/ThirdParty/DotNet/10.0` tree.

The packaged smoke poisons both `DOTNET_ROOT` and
`FINARE_DOTNET_ROOT`. The closure check also requires `hostfxr`,
`hostpolicy`, `coreclr`, `System.Private.CoreLib`, `System.Runtime`, the managed
Finare assemblies, runtime config, Demo assembly, and Demo chunk. This
proves the packaged runtime does not fall back to the source engine or a
machine-wide .NET installation.

## SDK consumer lane

The matrix packages the same versioned SDK twice and requires identical archive
SHA-256, byte length, and payload count. It then:

1. exports the committed HostProject Config, Source, and `.uproject` with
   `git archive`, deliberately excluding `Plugins`;
2. extracts and installs the first SDK archive with its manifest/hash verifier;
3. generates a small consumer `.csproj` that references only the installed SDK
   managed assemblies;
4. invokes the SDK's packaged CLI to emit and inspect a Release `.uschunk`;
5. builds, cooks, stages, packs, archives, and starts that consumer package.

This is a reproducible clean-project and clean-runtime simulation. Compilation
still uses the declared UE installation and its bundled .NET SDK; the packaged
executable does not.

SDK extraction and the plugin-free consumer checkout use a unique, short-lived
directory below the system temporary root. This keeps the Windows PowerShell
5.1 installer inside its declared path-length boundary even when the durable
matrix report itself is nested deeply. The install report is copied back into
the matrix evidence directory before the scratch directory is removed.

## Local M11.2 evidence

The 2026-08-28 run at
`artifacts/ue/matrix/20260828-171222/compatibility-matrix.json` passed all nine
phases in 247,473 ms and produced four declared UE 5.8.1 Win64 rows:

- Unreal Engine 5.8.1, changelist 56057345, `++UE5+Release-5.8`;
- Editor Development build and 17 / 17 Automation tests;
- source Development and Shipping Build/Cook/Stage/Pak/Archive plus packaged
  chunk smoke;
- deterministic `0.1.0-m11.2-rc.1` SDK with 171 payload files and archive
  SHA-256
  `5ac04abf3edc4c48048e220ade2127034930fbf72172e478967475d3f3b52a8a`;
- SDK installation, installed-CLI chunk emission, and installed-project
  Development package smoke.

The gate exposed a real configuration-switch defect before passing: the Debug
chunk carried Debug dependency hashes while an implicit Release CLI build had
restaged Release managed DLLs. CLI compilation is now explicit and occurs
before the selected Demo configuration; staging always overwrites the selected
configuration and verifies its managed-host SHA-256 before chunk emission.

## Explicitly unsupported rows

The current report records these as `not-declared`, rather than inferring
support:

- WindowsServer: no packaged Server target and executable smoke lane;
- Linux: no Linux CoreCLR staging, cook/package, and lifecycle lane;
- macOS, iOS, Android, and consoles: no platform runtime-closure and packaged
  smoke evidence.

Adding a row requires its own runtime distribution, generated ABI validation,
cook/stage rules, lifecycle tests, and packaged executable proof.
