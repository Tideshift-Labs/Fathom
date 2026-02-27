# Proposal: Solution-Wide Symbol Actions

**Status:** In Progress (API research complete, implementation next)
**Created:** 2026-02-12
**Updated:** 2026-02-15

## Problem

Fathom's current code navigation is file-centric. Every endpoint requires the caller to already know which file to look at:

```
GET /describe_code?file=Source/MyActor.cpp
GET /inspect?file=Source/MyActor.cpp
GET /classes?search=MyActor
```

This is backwards from how developers and LLMs actually think. In Rider, a developer hits `Ctrl+Shift+Alt+T` (Go to Symbol) and types a name. They don't provide a file path. The IDE searches the entire solution and returns matching symbols with their locations.

LLM agents need the same capability. When an agent encounters `FMyPlayerController` in a conversation, it needs to ask "where is this defined?" and "who uses it?" without knowing which file it lives in. Today, the agent must first call `/files` to get all files, then guess which file to `/describe_code` on, then hope it picked the right one. This is slow, brittle, and wastes context window.

## Goal

Expose Rider's solution-wide symbol navigation as HTTP endpoints and MCP tools. No file path required. Just a symbol name.

## C++ API Status (from experimentation)

The standard ReSharper SDK APIs (`ISymbolScope`, `IFinder`) are **dead ends for C++**. They only index CLR/.NET types. See `docs/cpp-symbol-api-findings.md` for full details.

The working C++ symbol infrastructure is entirely separate, lives in closed-source assemblies, and requires reflection:

| Component | Access | What it does |
|---|---|---|
| `CppGlobalSymbolCache` | `solution.GetComponent<>()` via reflection | Central C++ cache. Gateway to everything. |
| `CppSymbolNameCache` | `.SymbolNameCache` property on above | **Name-indexed symbol lookup.** The key API. |
| `CppGotoSymbolUtil` | Static class, no instantiation | Rider's Go to Symbol logic. `GetSymbolsInScopeByName()` works. |
| `CppWordIndex` | `solution.GetComponent<>()` via reflection | Word-to-file mapping. Fallback for text search. |

### Confirmed working APIs

| API | Confirmed | Notes |
|---|---|---|
| `CppSymbolNameCache.GetSymbolsByShortName("AActor")` | Yes | Returns ~50 ICppSymbol instances. Direct name lookup. |
| `CppSymbolNameCache.GetDerivedClasses("AActor")` | Untested | API exists on the resolved cache. For inheritors. |
| `CppGotoSymbolUtil.GetSymbolsInScopeByName(cache, scope, name, ctx)` | Yes | Same results, more complex setup. |
| `CppGotoSymbolUtil.GetSymbolsFromPsiFile(file)` | Yes | All symbols in a single file. |
| `ICppSymbol.Name.ToString()` | Yes | Returns short name ("AActor") |
| `ICppSymbol.ContainingFile.FullPath` | Yes | Returns absolute file path |
| `ICppSymbol.LocateDocumentRange(solution)` | Yes | Returns DocumentRange with line number |

### Dead ends (do not revisit)

| API | Why |
|---|---|
| `ISymbolScope.GetElementsByShortName()` | Returns 0 C++ results across all 4,816 modules |
| `IFinder.FindReferences()` | Returns 0 references for C++ elements |
| `IFinder.FindImplementingMembers()` | CLR-only |

## Rider Feature Mapping

What Rider gives developers for symbol navigation, mapped to proposed Fathom endpoints and the underlying APIs.

### Tier 1: Core navigation (high value, build first)

| # | Rider Feature | Shortcut | Endpoint | API | Status |
|---|---|---|---|---|---|
| 1 | Go to Symbol | Ctrl+Shift+Alt+T | `GET /symbols` | `CppSymbolNameCache.GetSymbolsByShortName()` | **Ready to build** |
| 2 | Go to Declaration | Ctrl+B / Ctrl+Click | `GET /symbols/declaration` | Filter search results to definitions (`CppClassSymbol`), exclude forward decls (`CppFwdClassSymbol`). Return file + line + code snippet. | **Ready to build** |
| 3 | Find Inheritors | | `GET /symbols/inheritors` | `CppSymbolNameCache.GetDerivedClasses(name)` + existing `BlueprintQueryService` for BP inheritors | **Confirmed** (direct children, classes only) |
| 4 | Find Usages | Alt+F7 | `GET /symbols/usages` | `IFinder` is dead for C++. Needs research into C++-specific reference APIs. `CppWordIndex` as rough fallback. | **Needs research** |

### Tier 2: Extended navigation (medium value)

| # | Rider Feature | Shortcut | Endpoint | API | Status |
|---|---|---|---|---|---|
| 5 | Type Hierarchy | Ctrl+H | `GET /symbols/hierarchy` | Derived via `GetDerivedClasses()`. Bases via `CppClassSymbol` properties or `CppStructureWalker` base specifier reading. | **Partially feasible** |
| 6 | Symbol Members | Ctrl+F12 | `GET /symbols/members` | Find class via name lookup, then `GetSymbolsFromPsiFile()` on its file, filter to children. | **Likely works** |
| 7 | H/CPP Counterpart | Alt+F10 | `GET /symbols/counterpart` | Find all symbols for a name; they naturally span .h and .cpp files. | **Likely works** |

### Tier 3: Advanced features (lower priority)

| # | Rider Feature | Shortcut | Endpoint | API | Status |
|---|---|---|---|---|---|
| 8 | Quick Documentation | Ctrl+Q | `GET /symbols/doc` | Read comment block above declaration from source file. | **Easy once #2 works** |
| 9 | Signature / Quick Info | | `GET /symbols/signature` | Read declaration line(s) from source at symbol location. | **Easy once #2 works** |
| 10 | Find Implementations | Ctrl+Alt+B | `GET /symbols/implementations` | For virtual methods: find overrides across derived classes. Composable from #3 + #6. | **Composable** |
| 11 | Call Hierarchy | Ctrl+Alt+H | `GET /symbols/callers` | Needs cross-reference index. C++ call graph likely in closed-source analysis. | **Unknown, probably hard** |

### Already covered by existing Fathom endpoints

| Rider Feature | Fathom Equivalent | Notes |
|---|---|---|
| File Structure (Alt+7) | `GET /describe_code?file=X` | Shows classes, methods, fields, inheritance per file |
| Go to Class (C++) | `GET /classes?search=X` | Searches C++ classes with base class info |
| Blueprint Inheritors | `GET /blueprints?class=X` | Blueprint classes derived from C++ |
| Find in Files | `GET /files` | File listing; LLM does its own text search |

## API Design

### Endpoints

| Endpoint | Purpose | Parameters |
|---|---|---|
| `GET /symbols` | Search symbols by name | `query` (required), `kind`, `scope`, `limit` |
| `GET /symbols/declaration` | Go to definition (Ctrl+Click equivalent) | `symbol` (required), `containingType`, `kind`, `context_lines` |
| `GET /symbols/inheritors` | Find derived types | `symbol` (required) |
| `GET /symbols/usages` | Find all references | `symbol` (required), `containingType`, `kind`, `scope`, `limit` |
| `GET /symbols/members` | List members of a type | `symbol` (required), `kind` |
| `GET /symbols/hierarchy` | Full inheritance chain (up and down) | `symbol` (required) |

### Parameters

- **`query`** / **`symbol`**: The symbol name. Short name (unqualified), e.g. `AActor`, `BeginPlay`, `bIsActive`.
- **`containingType`**: Optional. Disambiguates overloaded/common names. e.g. `symbol=BeginPlay&containingType=AMyPlayerController` returns only that class's override, not all 50 `BeginPlay` methods in the solution.
- **`kind`**: Optional filter. One of: `class`, `struct`, `function`, `field`, `enum`, `namespace`, `all` (default: `all`).
- **`scope`**: `user` (default, files under SolutionDirectory) or `all` (includes engine/third-party).
- **`limit`**: Maximum results (default: 50, max: 200).
- **`context_lines`**: For `/symbols/declaration`, how many lines of source to include around the declaration (default: 30). Set to 0 for location only.

### Key design decision: `/symbols/declaration` includes code snippets

When a developer Ctrl+Clicks a symbol in Rider, they see the definition immediately. The file opens and scrolls to the right line. For an LLM equivalent, returning just a file path and line number forces a second round trip (`/describe_code` or file read) to see what's actually there.

`/symbols/declaration` should return an inline code snippet (the declaration + surrounding context), so the LLM gets the "Ctrl+Click experience" in one call.

### Future: position-based resolve

`containingType` covers ~95% of disambiguation cases but has gaps: overloaded methods with the same name on the same class, free functions, and cases where the LLM doesn't know the containing type. The true Ctrl+Click equivalent is position-based:

```
GET /symbols/resolve?file=Source/MyActor.cpp&line=42&column=15
```

This would walk the PSI tree at the given source location, resolve the reference via the C++ semantic model, and return where the target is defined. No name or disambiguation needed. Deferred for now since name-based covers the common cases and the APIs are confirmed. Add this once `/symbols/declaration` is proven in practice and we see how often LLMs hit the disambiguation gaps.

### Key design decision: `containingType` for disambiguation

A name-based search for `BeginPlay` could return dozens of methods across dozens of classes. Rider avoids this because Ctrl+Click resolves the specific reference under the cursor through the semantic model. LLMs don't have cursor context, but they usually know the containing type from the code they're reading.

The `containingType` parameter lets the LLM say "I want `AMyActor::BeginPlay`, not all of them." The service filters results where the symbol's parent matches.

### Response models

```json
// GET /symbols?query=AActor
{
  "query": "AActor",
  "results": [
    {
      "name": "AActor",
      "kind": "class",
      "file": "Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h",
      "line": 42,
      "symbolType": "CppClassSymbol"
    },
    {
      "name": "AActor",
      "kind": "forward_declaration",
      "file": "Engine/Source/Editor/Blutility/Public/EditorUtilityLibrary.h",
      "line": 25,
      "symbolType": "CppFwdClassSymbol"
    }
  ],
  "totalMatches": 50,
  "truncated": true
}
```

```json
// GET /symbols/declaration?symbol=AActor
// The "Ctrl+Click" equivalent: definition + code snippet in one response
{
  "symbol": "AActor",
  "kind": "class",
  "file": "D:/UE/Engines/UE_5.7/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h",
  "line": 42,
  "snippet": "UCLASS(BlueprintType, Blueprintable, meta=(ShortTooltip=\"An Actor is ...\"))\nclass ENGINE_API AActor : public UObject\n{\n\tGENERATED_BODY()\n\npublic:\n\t/** Constructor */\n\tAActor();\n\t...",
  "forwardDeclarations": 48,
  "note": "48 forward declarations across engine headers (not shown)"
}
```

```json
// GET /symbols/declaration?symbol=BeginPlay&containingType=AMyPlayerController
// Disambiguated: only the specific override
{
  "symbol": "BeginPlay",
  "containingType": "AMyPlayerController",
  "declarations": [
    {
      "file": "Source/MyGame/MyPlayerController.h",
      "line": 23,
      "kind": "method",
      "snippet": "protected:\n\tvirtual void BeginPlay() override;",
      "context": "declaration"
    },
    {
      "file": "Source/MyGame/MyPlayerController.cpp",
      "line": 45,
      "kind": "method",
      "snippet": "void AMyPlayerController::BeginPlay()\n{\n\tSuper::BeginPlay();\n\t// ...\n}",
      "context": "definition"
    }
  ]
}
```

```json
// GET /symbols/inheritors?symbol=AActor
{
  "symbol": "AActor",
  "kind": "class",
  "cppInheritors": [
    {
      "name": "APawn",
      "file": "Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h",
      "line": 30,
      "kind": "class"
    },
    {
      "name": "AMyActor",
      "file": "Source/MyGame/MyActor.h",
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

```json
// GET /symbols/members?symbol=AMyActor
{
  "symbol": "AMyActor",
  "kind": "class",
  "file": "Source/MyGame/MyActor.h",
  "members": [
    { "name": "BeginPlay", "kind": "method", "line": 23, "signature": "virtual void BeginPlay() override" },
    { "name": "Tick", "kind": "method", "line": 25, "signature": "virtual void Tick(float DeltaTime) override" },
    { "name": "Health", "kind": "field", "line": 30, "signature": "float Health" },
    { "name": "bIsAlive", "kind": "field", "line": 33, "signature": "bool bIsAlive" }
  ]
}
```

### MCP tools

```
search_symbols          -> GET /symbols?query=...&kind=...&limit=...
symbol_declaration      -> GET /symbols/declaration?symbol=...&containingType=...&context_lines=...
symbol_inheritors       -> GET /symbols/inheritors?symbol=...
symbol_usages           -> GET /symbols/usages?symbol=...&containingType=...&limit=...
symbol_members          -> GET /symbols/members?symbol=...&kind=...
symbol_hierarchy        -> GET /symbols/hierarchy?symbol=...
```

## Implementation Plan

### Phase 1: SymbolSearchService + /symbols + /symbols/declaration

**Goal:** Symbol search by name and go-to-definition with code snippet. These two are the foundation.

**New files:**
- `Services/SymbolSearchService.cs` - Core service using `CppSymbolNameCache`
- `Handlers/SymbolsHandler.cs` - HTTP handler for all `/symbols` routes
- `Models/SymbolModels.cs` - Response DTOs

**SymbolSearchService implementation (C++ path):**

```
SymbolSearchService(ISolution solution)
  // Constructor: resolve CppGlobalSymbolCache via reflection,
  // then access .SymbolNameCache property

SearchByName(string query, string kindFilter, int limit)
  -> ReadLockCookie.Execute(() => {
       CppSymbolNameCache.GetSymbolsByShortName(query)
       For each ICppSymbol:
         name = symbol.Name.ToString()
         file = symbol.ContainingFile.FullPath
         line = symbol.LocateDocumentRange(solution).StartOffset.ToDocumentCoords().Line + 1
         kind = MapSymbolType(symbol.GetType().Name)  // CppClassSymbol -> "class", etc.
       Sort: definitions (CppClassSymbol) first, forward decls last
       Filter by kind if specified
       Deduplicate by (name, file, line)
       Return capped to limit
     })

GetDeclaration(string symbolName, string containingType, int contextLines)
  -> ReadLockCookie.Execute(() => {
       results = SearchByName(symbolName)
       Filter to definitions only (exclude CppFwdClassSymbol)
       If containingType specified: filter by parent symbol name
       For each definition:
         Get file path and line from symbol
         Read contextLines lines of source from the file around the declaration
       Return location + code snippet
     })
```

**Symbol type mapping:**

| `GetType().Name` | Kind | Show by default |
|---|---|---|
| `CppClassSymbol` | `class` | Yes (definition) |
| `CppFwdClassSymbol` | `forward_declaration` | Deprioritize |
| `CppFunctionSymbol` | `function` | Yes |
| `CppSimpleDeclaratorSymbol` | `variable` | Yes |
| `CppNamespaceSymbol` | `namespace` | Yes |
| `CppEnumSymbol` | `enum` | Yes |
| Other | `symbol` | Yes |

**Wiring:**
- Add `SymbolSearchService` as a field on `FathomRiderHttpServer`
- Add `SymbolsHandler` to the `_handlers` array
- Add MCP tool definitions to `FathomMcpServer.Tools`

### Phase 2: /symbols/inheritors

**Goal:** Find derived types, combining C++ inheritors with Blueprint inheritors.

```
FindInheritors(string symbolName)
  -> ReadLockCookie.Execute(() => {
       // C++ inheritors
       CppSymbolNameCache.GetDerivedClasses(symbolName)
       For each result: extract name, file, line

       // Blueprint inheritors (UE5 only, reuse existing service)
       If BlueprintQueryService available:
         _blueprintQuery.Query(symbolName, ...)

       Return combined result
     })
```

### Phase 3: /symbols/members

**Goal:** List all members of a class/struct by name (no file path needed).

```
GetMembers(string symbolName, string kindFilter)
  -> ReadLockCookie.Execute(() => {
       Find the class definition via SearchByName (filter to CppClassSymbol)
       Get the file: symbol.ContainingFile
       Get all symbols in that file: CppGotoSymbolUtil.GetSymbolsFromPsiFile(file)
       Filter to symbols whose Parent matches the target class
       Return members with name, kind, line, signature
     })
```

### Phase 4: /symbols/usages (requires research)

**Goal:** Find all references to a symbol across the solution.

`IFinder.FindReferences()` does not work for C++. Research needed:

1. Search C++ assemblies for `CppFindUsages`, `CppReferenceSearcher`, or similar types
2. Check if `UEAssetUsagesSearcher.GetFindUsagesResults()` works for C++ code references (not just Blueprint)
3. `CppWordIndex.GetFilesContainingWord()` as rough fallback (text matches, not semantic)
4. Look for `CppRenameRefactoring`-related types (rename must find all references internally)

### Phase 5: /symbols/hierarchy

**Goal:** Full inheritance chain: base classes upward + derived classes downward.

Combines:
- Upward: read base specifiers from `CppClassSymbol`. `CppStructureWalker` already reads `BaseSpecifierList` from PSI. Recursively look up each base.
- Downward: `CppSymbolNameCache.GetDerivedClasses()`, recursively.

### Phase 6: Tier 3 features

- `/symbols/doc` and `/symbols/signature` are simple once `/symbols/declaration` works (read source lines at the declaration offset)
- `/symbols/implementations` composes inheritors + members
- `/symbols/callers` deferred until C++ cross-reference APIs are found

## Files to Create / Modify

### New files

| File | Purpose |
|---|---|
| `Services/SymbolSearchService.cs` | Core service: reflection-based access to CppSymbolNameCache, search, declaration, inheritors, members |
| `Handlers/SymbolsHandler.cs` | HTTP handler for all `/symbols/*` routes |
| `Models/SymbolModels.cs` | `SymbolSearchResult`, `SymbolDeclaration`, `SymbolInheritor`, `SymbolMember` DTOs |

### Modified files

| File | Change |
|---|---|
| `FathomRiderHttpServer.cs` | Add `SymbolSearchService` field, wire in constructor, add `SymbolsHandler` to `_handlers` array |
| `Mcp/FathomMcpServer.cs` | Add `ToolDef` entries for symbol tools |

### Files to remove (after verification)

| File | Why |
|---|---|
| `Handlers/DebugSymbolHandler.cs` | Temporary debug endpoints, replaced by real service |

## Risks

### Find usages for C++

The biggest unknown. `IFinder` is confirmed dead for C++. If no C++-specific reference API exists, `/symbols/usages` will be limited to text-based matching via `CppWordIndex` (which finds files containing a word, not semantic references). This is still useful but produces false positives.

### Performance on large UE5 solutions

`GetSymbolsByShortName("AActor")` returns 50 results in ~5 seconds. Common names like `Get` or `Set` could return thousands. The `limit` parameter and `scope=user` default are essential.

### Forward declaration noise

For "AActor", 48 of 50 results are forward declarations. The service must sort definitions first and either hide or group forward declarations. The `symbolType` field lets callers filter client-side too.

### PSI readiness

Symbol search depends on C++ indexing being complete. For UE5 projects, initial indexing takes minutes after solution load. Check cache readiness and return a clear "indexing in progress" error.

## Verification

```bash
# Phase 1: Symbol search
curl "http://localhost:{port}/symbols?query=AActor"
curl "http://localhost:{port}/symbols?query=BeginPlay&kind=function"

# Phase 1: Declaration (Ctrl+Click equivalent)
curl "http://localhost:{port}/symbols/declaration?symbol=AActor"
curl "http://localhost:{port}/symbols/declaration?symbol=BeginPlay&containingType=AMyPlayerController"

# Phase 2: Inheritors
curl "http://localhost:{port}/symbols/inheritors?symbol=AActor"

# Phase 3: Members
curl "http://localhost:{port}/symbols/members?symbol=AMyActor"
```

Each endpoint should also be testable via MCP tool call through the `/mcp` endpoint.
