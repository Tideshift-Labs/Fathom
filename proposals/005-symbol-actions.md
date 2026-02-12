# Proposal: Solution-Wide Symbol Actions

**Status:** Draft
**Date:** 2026-02-12

## Problem

CoRider's current code navigation is file-centric. Every endpoint requires the caller to already know which file to look at:

```
GET /describe_code?file=Source/MyActor.cpp
GET /inspect?file=Source/MyActor.cpp
GET /classes?search=MyActor
```

This is backwards from how developers and LLMs actually think. In Rider, a developer hits `Ctrl+Shift+Alt+T` (Go to Symbol) and types a name. They don't provide a file path. The IDE searches the entire solution and returns matching symbols with their locations.

LLM agents need the same capability. When an agent encounters `FMyPlayerController` in a conversation, it needs to ask "where is this defined?" and "who uses it?" without knowing which file it lives in. Today, the agent must first call `/files` to get all files, then guess which file to `/describe_code` on, then hope it picked the right one. This is slow, brittle, and wastes context window.

## Goal

Expose Rider's solution-wide symbol lookup, find usages, go-to-declaration, and find-inheritors as HTTP endpoints and MCP tools. No file path required. Just a symbol name.

## ReSharper SDK APIs

These are the public SDK APIs that power Rider's symbol navigation. None are currently used by CoRider.

### ISymbolScope (name-based lookup)

Namespace: `JetBrains.ReSharper.Psi`

```csharp
var psiServices = solution.GetPsiServices();
var symbolScope = psiServices.Symbols.GetSymbolScope(psiModule, caseSensitive: true, null);

// Find all elements named "FMyActor" (types, methods, fields, properties)
IList<IDeclaredElement> elements = symbolScope.GetElementsByShortName("FMyActor");

// Get every short name in scope (powers autocomplete/fuzzy search)
ICollection<string> allNames = symbolScope.GetAllShortNames();

// Look up a type by fully-qualified CLR name (C# only)
ITypeElement type = symbolScope.GetTypeElementByCLRName("MyNamespace.MyClass");
```

`ISymbolScope` is scoped to a single `IPsiModule`. To search the entire solution, iterate all modules via `solution.PsiModules()`. For C#, `GetPrimaryPsiModule(project)` gives you the module for a project. For C++, modules correspond to translation units or project-level groupings.

### IFinder (find usages / find references)

Namespace: `JetBrains.ReSharper.Psi.Search`

```csharp
var finder = psiServices.Finder;
var searchDomainFactory = solution.GetComponent<SearchDomainFactory>();
var searchDomain = searchDomainFactory.CreateSearchDomain(solution, false);

finder.FindReferences(targetElement, searchDomain,
    new FindResultConsumer(result =>
    {
        if (result is FindResultReference refResult)
        {
            var node = refResult.Reference.GetTreeNode();
            var file = node.GetSourceFile();
            var range = refResult.Reference.GetDocumentRange();
            // Extract file path + line number
        }
        return FindExecution.Continue;
    }),
    NullProgressIndicator.Create());
```

### IDeclaredElement.GetDeclarations() (go to definition)

```csharp
IList<IDeclaration> declarations = element.GetDeclarations();
foreach (var decl in declarations)
{
    IPsiSourceFile sourceFile = decl.GetSourceFile();
    TreeTextRange nameRange = decl.GetNameRange();
    // sourceFile.GetLocation() gives the absolute file path
    // nameRange gives the exact character offset of the name
}
```

### IFinder.FindImplementingMembers (find inheritors)

```csharp
finder.FindImplementingMembers(member, searchDomain,
    new FindResultConsumer(result =>
    {
        if (result is FindResultOverridableMember overrideResult)
        {
            var impl = overrideResult.OverridableMember;
            // impl.GetDeclarations() gives source locations
        }
        return FindExecution.Continue;
    }),
    searchInheritors: true,
    NullProgressIndicator.Create());
```

### Threading requirement

All PSI access must happen under a read lock:

```csharp
ReadLockCookie.Execute(() =>
{
    // Symbol scope, finder, declarations access here
});
```

This is the same pattern already used by `CodeStructureService`, `FileIndexService`, and `InspectionService`.

## C++ considerations

The APIs above (`ISymbolScope`, `IFinder`) are well-documented for C#. For C++, the situation is murkier because C++ PSI lives in the closed-source `JetBrains.ReSharper.Feature.Services.Cpp.dll`.

However, C++ PSI nodes do produce `IDeclaredElement` instances. CoRider's `CppStructureWalker` already extracts them via reflection on the `DeclaredElement` property. The question is whether `ISymbolScope.GetElementsByShortName()` returns C++ elements, and whether `IFinder.FindReferences()` works for C++ `IDeclaredElement` targets.

**Recommended approach:** Try the public APIs first. If they return empty results for C++ (similar to how `RunLocalInspections` silently failed for C++ before the `InspectCodeDaemon` discovery), fall back to reflection-based access on C++-specific caches. The `UEAssetUsagesSearcher` component (already documented in `LEARNINGS.md`) provides `GetFindUsagesResults()` and `GetGoToInheritorsResults()` specifically for C++ symbols in Blueprint contexts.

**Phased rollout makes this manageable:** Phase 1 tests with C# projects (guaranteed to work), Phase 2 extends to C++ with fallbacks.

## API Design

### New endpoints

| Endpoint | Purpose | Parameters |
|---|---|---|
| `GET /symbols` | Search symbols by name | `query` (required), `kind`, `limit` |
| `GET /symbols/declaration` | Go to definition | `symbol` (required), `kind` |
| `GET /symbols/usages` | Find all references | `symbol` (required), `kind`, `limit` |
| `GET /symbols/inheritors` | Find derived types / overrides | `symbol` (required) |

### Parameters

- **`query`** / **`symbol`**: The symbol name to search for. Short name (unqualified), e.g. `FMyActor`, `OnPossess`, `bIsActive`.
- **`kind`**: Optional filter. One of: `class`, `struct`, `method`, `field`, `property`, `enum`, `all` (default: `all`).
- **`limit`**: Maximum results to return (default: 50, max: 200).

### Response model

```json
// GET /symbols?query=FMyActor
{
  "query": "FMyActor",
  "results": [
    {
      "name": "FMyActor",
      "qualifiedName": "MyGame::FMyActor",
      "kind": "class",
      "file": "Source/MyGame/MyActor.h",
      "line": 15,
      "access": "public",
      "baseType": "AActor",
      "module": "MyGame"
    }
  ],
  "totalMatches": 1,
  "truncated": false
}
```

```json
// GET /symbols/usages?symbol=FMyActor
{
  "symbol": "FMyActor",
  "kind": "class",
  "definedAt": {
    "file": "Source/MyGame/MyActor.h",
    "line": 15
  },
  "usages": [
    {
      "file": "Source/MyGame/GameMode.cpp",
      "line": 42,
      "context": "FMyActor* Actor = GetWorld()->SpawnActor<FMyActor>();"
    },
    {
      "file": "Source/MyGame/PlayerController.cpp",
      "line": 87,
      "context": "if (auto* MA = Cast<FMyActor>(HitActor))"
    }
  ],
  "totalUsages": 2
}
```

```json
// GET /symbols/declaration?symbol=OnPossess
{
  "symbol": "OnPossess",
  "declarations": [
    {
      "file": "Source/MyGame/MyPlayerController.h",
      "line": 23,
      "kind": "method",
      "signature": "virtual void OnPossess(APawn* InPawn) override",
      "containingType": "AMyPlayerController"
    },
    {
      "file": "Source/MyGame/MyPlayerController.cpp",
      "line": 45,
      "kind": "method",
      "signature": "void AMyPlayerController::OnPossess(APawn* InPawn)",
      "containingType": "AMyPlayerController"
    }
  ]
}
```

```json
// GET /symbols/inheritors?symbol=AMyActor
{
  "symbol": "AMyActor",
  "kind": "class",
  "inheritors": [
    {
      "name": "AMyActorChild",
      "file": "Source/MyGame/MyActorChild.h",
      "line": 10,
      "kind": "class"
    }
  ],
  "blueprintInheritors": [
    {
      "name": "BP_MyActor_C",
      "assetPath": "/Game/Blueprints/BP_MyActor.uasset"
    }
  ]
}
```

### MCP tools

Four new tools in `CoRiderMcpServer`:

```
search_symbols      -> GET /symbols?query=...&kind=...&limit=...
symbol_declaration  -> GET /symbols/declaration?symbol=...&kind=...
symbol_usages       -> GET /symbols/usages?symbol=...&kind=...&limit=...
symbol_inheritors   -> GET /symbols/inheritors?symbol=...
```

## Implementation Plan

### Phase 1: SymbolSearchService + /symbols endpoint

**Goal:** Solution-wide symbol search by name, like Rider's "Go to Symbol".

**New files:**
- `Services/SymbolSearchService.cs` - Core service wrapping `ISymbolScope`
- `Handlers/SymbolsHandler.cs` - HTTP handler for `/symbols` and sub-routes
- `Models/SymbolModels.cs` - Response DTOs

**SymbolSearchService implementation:**

```
SymbolSearchService(ISolution solution)

SearchByName(string query, string kindFilter, int limit)
  -> ReadLockCookie.Execute(() => {
       Iterate solution.PsiModules()
       For each module: GetSymbolScope() -> GetElementsByShortName(query)
       If no exact matches: try prefix/substring match via GetAllShortNames()
       Filter by kind if specified
       Map IDeclaredElement -> SymbolResult (name, kind, file, line, module)
       Deduplicate by (name, file, line)
       Return capped to limit
     })
```

Key decisions:
- Iterate ALL `IPsiModule`s, not just the primary project module. This picks up engine headers, third-party code, and multi-project solutions.
- Filter results to user code by default (files under `solution.SolutionDirectory`), with an `&scope=all` parameter to include engine/third-party.
- Use `ReadLockCookie.Execute()` (same as existing services).
- Return both exact and prefix matches, sorted: exact matches first.

**Wiring:**
- Add `SymbolSearchService` as a field on `InspectionHttpServer2`
- Add `SymbolsHandler` to the `_handlers` array in `StartServer()`
- Add MCP tool definitions to `CoRiderMcpServer.Tools`

**Testing:**
- `curl http://localhost:{port}/symbols?query=FMyActor` on a UE5 project
- `curl http://localhost:{port}/symbols?query=InspectionService` on the CoRider solution itself (C#)
- Verify results include file path, line number, element kind

### Phase 2: Go to declaration (/symbols/declaration)

**Goal:** Given a symbol name, return all declaration sites.

**Add to SymbolSearchService:**

```
GetDeclarations(string symbolName, string kindFilter)
  -> ReadLockCookie.Execute(() => {
       Find element via SearchByName (reuse Phase 1)
       For each matching element: element.GetDeclarations()
       For each declaration: extract file, line, signature, containingType
       Return list
     })
```

For C++ out-of-line definitions (e.g. `void AMyActor::OnPossess(...)` in a .cpp file), the element should have declarations in both the .h and .cpp. The `GetDeclarations()` API should return both.

**Disambiguation:** When `query=OnPossess` matches methods on multiple classes, return all of them grouped by containing type. The caller can refine with `kind=method` or by inspecting `containingType` in the response.

### Phase 3: Find usages (/symbols/usages)

**Goal:** Find all references to a symbol across the solution.

**Add to SymbolSearchService:**

```
FindUsages(string symbolName, string kindFilter, int limit)
  -> ReadLockCookie.Execute(() => {
       Find element via SearchByName
       SearchDomainFactory.CreateSearchDomain(solution, false)
       psiServices.Finder.FindReferences(element, domain, consumer, progress)
       For each FindResultReference: extract file, line, context snippet
       Return capped to limit
     })
```

**Context snippet:** For each usage, read a short window (the line containing the reference) from the source file document. This gives the LLM enough context to understand how the symbol is being used without requesting the full file.

**Performance concern:** `FindReferences` on a widely-used symbol (e.g. `AActor`) in a large UE5 solution could return thousands of results and take seconds. The `limit` parameter is essential. Consider also:
- A `scope=user` default that restricts the search domain to files under `SolutionDirectory`
- A timeout (e.g. 10s) that returns partial results with a `truncated: true` flag

### Phase 4: Find inheritors (/symbols/inheritors)

**Goal:** Find types that derive from a given type, including Blueprint inheritors.

**Add to SymbolSearchService:**

```
FindInheritors(string symbolName)
  -> ReadLockCookie.Execute(() => {
       Find type element via SearchByName (filter to types only)

       // C++ / C# inheritors via IFinder
       psiServices.Finder.FindImplementingMembers(...) or
         iterate types checking GetSuperTypes()

       // Blueprint inheritors (UE5 only) via existing BlueprintQueryService
       If UE project: _blueprintQuery.Query(symbolName, ...)

       Combine and return
     })
```

This phase bridges the existing Blueprint derivation queries with the new symbol infrastructure. For UE5 projects, the response includes both C++ inheritors and Blueprint inheritors in separate arrays.

### Phase 5: C++ validation and reflection fallbacks

After Phases 1-4 work for C#, test each against C++ projects:

1. Does `GetElementsByShortName()` return C++ elements from C++ `IPsiModule`s?
2. Does `IFinder.FindReferences()` find references within C++ files?
3. Does `GetDeclarations()` return .h and .cpp declaration sites for C++ elements?

If any of these fail silently (the `RunLocalInspections` pattern), implement reflection-based fallbacks:

- **Symbol search fallback:** Walk all C++ `IPsiSourceFile`s, parse PSI trees (reuse `CppStructureWalker` logic), collect `IDeclaredElement` instances, filter by name. Cache results across requests.
- **Find usages fallback:** Use `UEAssetUsagesSearcher.GetFindUsagesResults()` via reflection (same pattern as `BlueprintQueryService` uses for `UE4AssetsCache`).
- **Find inheritors fallback:** Use `UEAssetUsagesSearcher.GetGoToInheritorsResults()` via reflection.

### Phase 6: Fuzzy/substring search

Enhance `/symbols` to support partial matches:

- If `GetElementsByShortName(query)` returns 0 results, fall back to `GetAllShortNames()` filtered by case-insensitive substring
- Add `&match=exact|prefix|substring` parameter (default: try exact, then prefix, then substring)
- For large solutions, `GetAllShortNames()` could return tens of thousands of names. Cache the name list and invalidate on PSI changes (or use a simple TTL).

## Files to Create / Modify

### New files

| File | Purpose |
|---|---|
| `Services/SymbolSearchService.cs` | Core service: search, declarations, usages, inheritors |
| `Handlers/SymbolsHandler.cs` | HTTP handler for `/symbols`, `/symbols/declaration`, `/symbols/usages`, `/symbols/inheritors` |
| `Models/SymbolModels.cs` | `SymbolSearchResult`, `SymbolDeclaration`, `SymbolUsage`, `SymbolInheritor` DTOs |
| `Formatting/SymbolsMarkdownFormatter.cs` | Markdown output for symbol results |

### Modified files

| File | Change |
|---|---|
| `InspectionHttpServer2.cs` | Add `SymbolSearchService` field, wire in constructor, add `SymbolsHandler` to `_handlers` array |
| `Mcp/CoRiderMcpServer.cs` | Add 4 new `ToolDef` entries for `search_symbols`, `symbol_declaration`, `symbol_usages`, `symbol_inheritors` |

## Risks and Open Questions

### C++ symbol scope coverage

The biggest unknown. `ISymbolScope.GetElementsByShortName()` may not include C++ elements, or may only include elements from already-indexed translation units. This needs to be tested early (Phase 1) before investing in the full pipeline.

**Mitigation:** Phase 5 has explicit fallback strategies using the same reflection approach that already works for Blueprint queries.

### Performance on large UE5 solutions

A UE5 solution can have 247K+ files. `GetAllShortNames()` across all modules could be expensive. `FindReferences()` on a common base class could take seconds.

**Mitigation:** Default scope to user code (`SolutionDirectory`), enforce limits, add timeouts, return `truncated` flags.

### Ambiguous symbol names

`OnPossess` could match methods on 5 different classes. `Init` could match dozens.

**Mitigation:** Return all matches with their containing type and file. Let the LLM choose. The `kind` filter helps narrow results.

### PSI readiness

Symbol search depends on the PSI being fully indexed. For UE5 projects, initial indexing can take minutes after solution load.

**Mitigation:** Check `IPsiSourceFile` availability and return a clear error ("Solution indexing in progress") if the PSI isn't ready. This is analogous to the Blueprint cache readiness check already in `BlueprintsHandler`.

## Verification

After each phase, verify with:

```bash
# Phase 1: Symbol search
curl "http://localhost:19876/symbols?query=FMyActor"
curl "http://localhost:19876/symbols?query=FMyActor&kind=class&format=json"

# Phase 2: Declaration
curl "http://localhost:19876/symbols/declaration?symbol=OnPossess"

# Phase 3: Usages
curl "http://localhost:19876/symbols/usages?symbol=FMyActor&limit=20"

# Phase 4: Inheritors
curl "http://localhost:19876/symbols/inheritors?symbol=AMyActor"
```

Each endpoint should also be testable via MCP tool call through the `/mcp` endpoint.
