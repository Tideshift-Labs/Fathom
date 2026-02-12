# Agent Instructions: Fathom (Rider Plugin)

## Style

- **Never use emdashes** (`—`). Rephrase instead.

## Key Documentation

- **[README.md](README.md)**: Architecture, HTTP API, build commands, development workflow.
- **[docs/LEARNINGS.md](docs/LEARNINGS.md)**: Dead-end approaches and hard-won lessons. **Read the "DEAD END" sections before attempting alternative inspection APIs.** Multiple approaches (RunLocalInspections for C++, ForceReHighlight for non-open files, SolutionAnalysisManager as a component) were fully explored and proven not to work.
- **[docs/ue-companion-plugin.md](docs/ue-companion-plugin.md)**: Blueprint audit architecture, explaining how the Rider plugin and UE companion plugin couple via filesystem conventions.

## Reflection Gotchas

This plugin accesses closed-source JetBrains APIs via reflection. Key pitfalls:

- **`GetComponent<T>()` is an extension method**, not an instance method on `ISolution`. You must search interfaces, base types, then scan assemblies for static extension methods.
- **`ResolveComponent(Type)` helper** exists for component resolution when generic type parameters don't work at runtime.
- **Type names may differ from guesses.** Use diagnostic dumps (stderr or HTTP response with `&debug=true`) when debugging reflection failures.
- **Blueprint APIs** (`UE4AssetsCache`, `UE4SearchUtil`) are accessed via reflection on `JetBrains.ReSharper.Feature.Services.Cpp.dll` with no compile-time reference.

## UE Project Detection

Two approaches are used, depending on the need:

- **Lightweight**: Check for `.uproject` files in the solution directory. Fast, no reflection needed.
- **Full info**: `GetUEProjectInfo()` via `ICppUE4SolutionDetector` reflection. Returns engine path, project path, target platform. Engine path comes from `UnrealContext.Path` property.

## Commandlet Detection

When the Rider plugin spawns `UnrealEditor-Cmd.exe -run=BlueprintAudit`, it looks for **"unknown commandlet"** patterns in the process output to detect whether the companion UE plugin is installed. If the commandlet isn't registered (plugin not installed), the output contains this pattern and the refresh fails gracefully.

## Cross-Repo Coordination

- **Audit schema version** must match between `BlueprintAuditService.AuditSchemaVersion` (this repo) and `FBlueprintAuditor::AuditSchemaVersion` in the [CoRider-UnrealEngine](https://github.com/kvirani/CoRider-UnrealEngine) repo. Bump both together.
- **Audit output path**: `Saved/Audit/v<N>/Blueprints/...`. Version segment must match on both sides.

## Build

```powershell
.\scripts\setup.ps1             # First-time only
.\gradlew.bat :compileDotNet    # Compile
.\gradlew.bat :runIde           # Launch sandbox Rider with plugin
```
