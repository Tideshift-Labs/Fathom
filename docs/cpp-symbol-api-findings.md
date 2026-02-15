# C++ Symbol API Findings

Exploration of how to look up C++ symbols by name in the ReSharper/Rider SDK, similar to Rider's "Go to Symbol" (Ctrl+Shift+Alt+T).

**Date:** 2026-02-14
**Context:** Building `/symbols` endpoint for Fathom so LLMs can look up C++ symbols without knowing file paths.

---

## Dead Ends (confirmed)

### ISymbolScope.GetElementsByShortName() - CLR only

`IPsiServices.Symbols.GetSymbolScope(module, ...).GetElementsByShortName("AActor")` returns **0 results** for C++.

The `SymbolCache` (type: `JetBrains.ReSharper.Psi.Caches.SymbolCache.SymbolCache`) only indexes .NET/CLR types. Every PSI module returns the same 26,770 .NET names (`StubHelpers`, `JsonIgnoreAttribute`, etc.). Zero C++ names appear in any module's scope.

Tested across all 4,816 modules (including `ProjectPsiModule`, `CppExternalModuleImpl`, `UE4UAssetModule`). Case-insensitive retry also returns 0.

**Do not revisit.** Same silent-failure pattern as `RunLocalInspections` for C++ (see LEARNINGS.md Dead End #1).

### IFinder.FindReferences() - does not work for C++ elements

Even when given a valid `CppResolveEntityDeclaredElement` obtained from PSI tree walking, `IFinder.FindReferences()` returns **0 references** (237ms search time).

The element was valid (had 1 declaration, implemented `ICppDeclaredElement`), but the finder infrastructure does not index C++ cross-references.

**Do not revisit** for C++ usage. `IFinder` is a CLR-only facility.

### Reverse check confirms the gap

A C++ element obtained via PSI tree walk (`CppResolveEntityDeclaredElement`) is **not findable** via `ISymbolScope.GetElementsByShortName()` even when searching by its exact `ShortName`. The symbol scope simply does not include C++ elements.

---

## What Works: C++ Symbol Infrastructure

The C++ symbol system is entirely separate from the CLR `SymbolCache`. All types are in closed-source assemblies (`JetBrains.ReSharper.Psi.Cpp.dll` and `JetBrains.ReSharper.Feature.Services.Cpp.dll`) and must be accessed via reflection.

### PSI Module Overview (UE5 project)

| Module Type | Count | Contains C++ | Notes |
|---|---|---|---|
| `ProjectPsiModule` | 1,847 | Yes (some) | Per-project modules. Engine module had 43 C++ files |
| `UE4BuildAndTargetFilesModule` | 1,059 | No | Build.cs / Target.cs files |
| `SolutionFolderPsiModule` | 884 | No | Virtual folder modules |
| `UeIniModule` | 790 | No | .ini config files |
| `ProjectAssemblyPsiModule` | 228 | No | .NET assembly references |
| `CppExternalModuleImpl` | 1 | Yes | External C++ module |
| `UE4UAssetModule` | 1 | No | .uasset file module |

---

## CppWordIndex (the file finder)

**Type:** `JetBrains.ReSharper.Feature.Services.Cpp.Caches.CppWordIndex`
**Runtime type:** `CppUnrealWordIndex` (UE-specific subclass)
**Component:** Yes, resolves via `solution.GetComponent<CppWordIndex>()`

This is the C++ word index. It maps words to source files.

### Key Methods

| Method | Parameters | Returns | Notes |
|---|---|---|---|
| `GetFilesContainingWord` | `string word` | `ICollection<IPsiSourceFile>` | **WORKS for C++!** `"AActor"` returns 10+ engine files |
| `GetFilesContainingWords` | `IEnumerable<string>` | `ICollection<IPsiSourceFile>` | Files containing ALL words |
| `GetFilesContainingAllSubwords` | `string query` | `ICollection<IPsiSourceFile>` | Splits query into subwords |
| `GetFilesContainingAnySubword` | `IEnumerable<string>` | `ICollection<IPsiSourceFile>` | Files containing ANY subword |
| `GetFilesContainingAnyQuery` | `IEnumerable<string>, bool ensureIsWord` | `IEnumerable<IPsiSourceFile>` | Flexible multi-query search |
| `CanContainWord` | `IPsiSourceFile, string` | `bool` | Fast check on a specific file |
| `GetSubwords` | `string text` | `IEnumerable<string>` | Camel-case splitting |

### Test Result

```
GetFilesContainingWord("AActor"):
  D:\Epic\UE_5.7\Engine\Source\Runtime\Engine\Public\Model.h
  D:\Epic\UE_5.7\Engine\Source\Runtime\Engine\Private\HUD.cpp
  D:\Epic\UE_5.7\Engine\Source\Editor\UnrealEd\Public\Editor.h
  D:\Epic\UE_5.7\Engine\Source\Editor\UnrealEd\Public\EdMode.h
  D:\Epic\UE_5.7\Engine\Source\Runtime\Engine\Private\Pawn.cpp
  D:\Epic\UE_5.7\Engine\Source\Runtime\Engine\Private\Actor.cpp
  D:\Epic\UE_5.7\Engine\Source\Runtime\Engine\Private\Level.cpp
  ...10+ files
```

This is a **word-level** index (not a symbol-level index). `"AActor"` appears in these files as text. To find the actual class declaration, you would need to walk the PSI tree of the returned files and find the symbol.

### Usage Pattern

```csharp
// Resolve via reflection (closed-source assembly)
var wordIndexType = FindType("CppWordIndex", "JetBrains.ReSharper.Feature.Services.Cpp");
var wordIndex = solution.GetComponent(wordIndexType); // via reflection

// Find files containing a symbol name
var files = wordIndex.GetFilesContainingWord("AActor"); // returns IPsiSourceFile[]

// Then walk each file's PSI tree to find the actual declaration
foreach (var file in files)
{
    var psiFile = file.GetPrimaryPsiFile();
    // ... use CppGotoSymbolUtil.GetSymbolsFromPsiFile() or tree walk
}
```

---

## CppGlobalSymbolCache (the symbol table cache)

**Type:** `JetBrains.ReSharper.Psi.Cpp.Caches.CppGlobalSymbolCache`
**Component:** Yes, resolves via `solution.GetComponent<CppGlobalSymbolCache>()`

This is the central C++ symbol table cache. It manages `CppFileSymbolTable` instances for all indexed C++ files. It does NOT have a direct "lookup by name" method. Its role is file-level: it knows which files have been processed and provides their symbol tables.

### Key Methods

| Method | Parameters | Returns | Notes |
|---|---|---|---|
| `GetFileSymbolTable` | `Int64 tableId` | `CppFileSymbolTable` | Get a file's symbol table by ID |
| `GetTablesForFile` | `CppFileLocation` | `CppList<CppFileSymbolTable>` | All tables for a file location |
| `GetAllTables` | `bool contextIsC` | `CppList<CppFileSymbolTable>` | ALL symbol tables (could be huge) |
| `GetAllFilesWithTables` | none | `List<CppFileLocation>` | All files that have symbol tables |
| `ContainsFile` | `CppFileLocation` | `bool` | Check if file is indexed |
| `IsFileProcessed` | `CppFileLocation` | `bool` | Check if file indexing is complete |
| `CanContainWord` | `IPsiSourceFile, string` | `bool` | Word-level check (delegates to word index) |
| `GetInclusionContextNoThrow` | `IPsiSourceFile` | `CppInclusionContextResult` | Get file's include context |

### Key Properties

| Property | Type | Notes |
|---|---|---|
| `GlobalCache` | `ICppGlobalCache` | Interface to the global cache (may have more lookup methods) |
| `CppModule` | `CppExternalModule` | The external C++ module |
| `LinkageCache` | `CppLinkageEntityCache` | Linkage entity cache |
| `IncludesGraphCache` | `CppIncludesGraphCache` | Include dependency graph |

### What It Does NOT Have

No `GetSymbolsByName(string)`, `FindSymbol(string)`, or similar direct name-to-symbol lookup. The cache is organized by file location, not by symbol name. Name-based lookup goes through `CppGotoSymbolUtil` (see below).

---

## CppGotoSymbolUtil (the Go to Symbol API)

**Type:** `JetBrains.ReSharper.Feature.Services.Cpp.Navigation.Goto.CppGotoSymbolUtil`
**Static class:** All methods are static. No component resolution needed.

This is the class that powers Rider's "Go to Symbol" for C++. It operates on `ICppSymbol` (not `IDeclaredElement`).

### Key Methods

| Method | Parameters | Returns | Notes |
|---|---|---|---|
| **`GetSymbolsInScopeByName`** | `CppGlobalSymbolCache, INavigationScope, string name, GotoContext` | `IEnumerable<ICppSymbol>` | **THE KEY API**: name-based symbol lookup |
| **`GetSymbolsFromPsiFile`** | `IPsiSourceFile` | `IEnumerable<ICppSymbol>` | All symbols in a file |
| `GetSymbolsInScope` | `CppGlobalSymbolCache, INavigationScope, GotoContext` | `IEnumerable<ICppSymbol>` | All symbols in scope |
| `GetAllSymbols` | `CppSymbolNameCache, GotoContext` | `List<ICppSymbol>` | ALL symbols (needs `CppSymbolNameCache`) |
| `GetSymbolsInFileMemberScope` | `FileMemberNavigationScope` | `IEnumerable<ICppSymbol>` | Symbols in a file member scope |
| `FilterSymbolsToShow` | `IEnumerable<ICppSymbol>` | `IEnumerable<ICppSymbol>` | Filters symbols for display |
| `GetShortName` | `ICppSymbol` | `string` | Extract short name from symbol |
| `IsValidSymbol` | `ICppSymbol` | `bool` | Check if symbol should be shown |
| `CreateOccurrence` | `IPsiServices, ICppSymbol, bool fileMember` | `CppDeclaredElementOccurrence` | Create navigation occurrence |

### The Golden Method: `GetSymbolsInScopeByName`

```csharp
// Pseudocode (all via reflection since types are closed-source)
var cache = solution.GetComponent<CppGlobalSymbolCache>();
var scope = /* need to construct INavigationScope */;
var context = /* need to construct GotoContext */;

var symbols = CppGotoSymbolUtil.GetSymbolsInScopeByName(cache, scope, "AActor", context);
```

**Open question:** How to construct `INavigationScope` and `GotoContext`. These are navigation framework types. `INavigationScope` has implementations like `SolutionNavigationScope` and `ProjectNavigationScope`. `GotoContext` may be constructable with default values.

### Also Note: CppSymbolNameCache

Referenced by `GetAllSymbols(CppSymbolNameCache, GotoContext)`. This is another component that may provide direct name-based access. Needs exploration.

---

## CppFileSymbolTable (per-file symbols)

**Type:** `JetBrains.ReSharper.Psi.Cpp.Caches.CppFileSymbolTable`

Represents the symbol table for a single C++ file. Obtained from `CppGlobalSymbolCache.GetFileSymbolTable(tableId)` or `GetTablesForFile(location)`.

### Key Properties

| Property | Type | Notes |
|---|---|---|
| `GlobalNamespaceSymbol` | `CppNamespaceSymbol` | Root namespace, can traverse to find all symbols |
| `FileSymbols` | `CppFileSymbols` | Raw symbol data |
| `File` | `CppFileLocation` | Source file location |
| `PreprocessorSymbols` | `ICppSymbol[]` | Macro definitions |
| `LanguageKind` | `CppLanguageKind` | C vs C++ |

### Key Methods

| Method | Parameters | Returns |
|---|---|---|
| `ContainsSymbol` | `ICppSymbol` | `bool` |

The `GlobalNamespaceSymbol` property returns a `CppNamespaceSymbol` which likely has child symbols. This could be traversed to find all declarations in a file.

---

## ICppSymbol (the C++ element type)

C++ uses `ICppSymbol` (and subtypes like `ICppParserSymbol`) instead of `IDeclaredElement` for symbol lookup. The `CppGotoSymbolUtil` methods operate on `ICppSymbol`.

Key observations from `CppResolveEntityDeclaredElement` (obtained via PSI tree walk):
- Implements `ICppDeclaredElement`, `ICppTypedDeclaredElement`, `IUnmanagedCppDeclaredElement`
- Implements `IDeclaredElement` (but this doesn't help with CLR-only APIs)
- Implements `INavigatableDeclaredElement` (navigation support)
- Does NOT implement `ITypeElement`, `ITypeMember`, `IFunction`, etc. (CLR-only interfaces)

The relationship between `ICppSymbol` and `ICppDeclaredElement` needs further exploration. `CppGotoSymbolUtil.CreateOccurrence()` takes `ICppSymbol` and produces a `CppDeclaredElementOccurrence`, suggesting there's a path from symbol to declared element.

---

## Recommended Approach for Symbol Lookup

### Strategy A: CppGotoSymbolUtil.GetSymbolsInScopeByName (ideal)

If we can construct the required `INavigationScope` and `GotoContext` parameters, this is the direct equivalent of Rider's Go to Symbol. Returns `ICppSymbol` instances filtered by name.

**Next steps:**
1. Find `INavigationScope` implementations (likely `SolutionNavigationScope`)
2. Find how to construct `GotoContext` (may have a default/empty constructor)
3. Try invoking `GetSymbolsInScopeByName` via reflection
4. Extract file path and line number from returned `ICppSymbol` instances

### Strategy B: CppWordIndex + PSI tree walk (proven to work, fallback)

Two-step approach using APIs that are already confirmed working:

1. `CppWordIndex.GetFilesContainingWord("AActor")` to find candidate files
2. Walk each file's PSI tree (same as `CppStructureWalker`) to find the actual class/function declaration matching the name
3. Return file path + line number

**Pros:** Both steps are individually proven. Word index returns results. PSI tree walk extracts elements.
**Cons:** Slow for common names (word index returns many files). Word-level, not symbol-level (false positives from comments, strings).

### Strategy C: CppGotoSymbolUtil.GetSymbolsFromPsiFile (hybrid)

1. Use `CppWordIndex.GetFilesContainingWord()` to find candidate files
2. Use `CppGotoSymbolUtil.GetSymbolsFromPsiFile(file)` to get all `ICppSymbol` instances from each file
3. Filter by `CppGotoSymbolUtil.GetShortName(symbol) == query`

This avoids manual tree walking and uses the same symbol extraction that Rider's Go to Symbol uses, but scoped to files identified by the word index.

### Strategy D: CppSymbolNameCache (unexplored)

`CppGotoSymbolUtil.GetAllSymbols(CppSymbolNameCache, GotoContext)` references a `CppSymbolNameCache` component. If this is a name-indexed cache of all C++ symbols, it could provide direct lookup without the word index detour. Needs exploration.

---

## Types to Explore Next

| Type | Assembly | Why |
|---|---|---|
| `CppSymbolNameCache` | Unknown | Referenced by `GetAllSymbols()`, may have name-based lookup |
| `ICppGlobalCache` | `JetBrains.ReSharper.Psi.Cpp` | Interface on `CppGlobalSymbolCache.GlobalCache` property |
| `INavigationScope` | `JetBrains.ReSharper.Feature.Services` | Required parameter for `GetSymbolsInScopeByName` |
| `GotoContext` | `JetBrains.ReSharper.Feature.Services` | Required parameter for `GetSymbolsInScopeByName` |
| `CppNamespaceSymbol` | `JetBrains.ReSharper.Psi.Cpp` | Root of per-file symbol tree, may be traversable |
| `ICppSymbol` | `JetBrains.ReSharper.Psi.Cpp` | Base interface for C++ symbols |
| `CppDeclaredElementOccurrence` | `JetBrains.ReSharper.Feature.Services.Cpp` | Created from ICppSymbol, contains navigation info |

---

## Concrete Next Steps (for continuing this work)

### What exists right now

The file `src/dotnet/ReSharperPlugin.Fathom/Handlers/DebugSymbolHandler.cs` contains temporary debug endpoints wired into `InspectionHttpServer2.cs`. These were used for the experiments above:

| Endpoint | Purpose | Status |
|---|---|---|
| `GET /debug/symbol-scope?query=X` | Test `ISymbolScope` | Done, confirmed dead for C++ |
| `GET /debug/find-refs?query=X` | Test `IFinder.FindReferences` | Done, confirmed dead for C++ |
| `GET /debug/declarations?query=X` | Test `IDeclaredElement.GetDeclarations` | Done |
| `GET /debug/symbol-diag?query=X` | Deep diagnostic: module survey, PSI walk, assembly scan | Done, found C++ types |
| `GET /debug/cpp-cache?query=X` | Probe C++ caches via reflection | Done, found working APIs |

These endpoints should be kept until the real `/symbols` endpoint is built, then removed.

### Step 1: Probe Strategy A (CppGotoSymbolUtil.GetSymbolsInScopeByName)

Add a new debug endpoint `/debug/cpp-goto?query=AActor` that:

1. Resolves `CppGlobalSymbolCache` as a component (already confirmed working).
2. Finds `INavigationScope` implementations via assembly scan. Look for `SolutionNavigationScope` or similar in `JetBrains.ReSharper.Feature.Services`. Check its constructors -- it likely takes an `ISolution`.
3. Finds `GotoContext` and checks its constructors. May have a parameterless constructor or a simple factory.
4. Invokes `CppGotoSymbolUtil.GetSymbolsInScopeByName(cache, scope, "AActor", context)` via reflection.
5. For each returned `ICppSymbol`: extract short name, file path, line number. The `ICppSymbol` interface likely has properties for location info, or use `CppGotoSymbolUtil.CreateOccurrence()` to convert to a navigable occurrence.

Also probe `CppSymbolNameCache` in the same endpoint -- find the type, try resolving it as a component, dump its API. If it has a name-based lookup method, this could be simpler than `GetSymbolsInScopeByName`.

**Pattern to follow:** The `HandleCppCache` method in `DebugSymbolHandler.cs` shows the reflection pattern: `FindTypeByName()` to locate closed-source types, `TryResolveComponent()` to get instances, `DumpPublicApi()` to see methods, then targeted invocation.

### Step 2: If Strategy A fails, implement Strategy C

Strategy C is the safe fallback because both pieces are already confirmed working:

1. `CppWordIndex.GetFilesContainingWord(query)` returns `ICollection<IPsiSourceFile>` (confirmed: "AActor" returns 10+ engine files).
2. `CppGotoSymbolUtil.GetSymbolsFromPsiFile(file)` returns `IEnumerable<ICppSymbol>` (not yet tested but is a public static method on a known type).
3. Filter: `CppGotoSymbolUtil.GetShortName(symbol) == query`.

Build this as a method on a new `SymbolSearchService.cs` (see proposal `proposals/005-symbol-actions.md` for the service/handler pattern). The word index step narrows the file set, then `GetSymbolsFromPsiFile` extracts symbols without manual PSI tree walking.

### Step 3: Build the real SymbolSearchService

Once you know which strategy works, create:

- `Services/SymbolSearchService.cs` -- wraps the working C++ lookup API. Accepts a symbol name, returns structured results (name, kind, file, line). Uses `ReadLockCookie.Execute()` for PSI access.
- `Handlers/SymbolsHandler.cs` -- HTTP handler for `GET /symbols?query=X`. Follows the same patterns as `ClassesHandler.cs`.
- `Models/SymbolModels.cs` -- DTOs for the response.

Wire into `InspectionHttpServer2.cs` (add service as field, add handler to `_handlers` array) and `Mcp/FathomMcpServer.cs` (add `search_symbols` tool definition).

See `proposals/005-symbol-actions.md` for the full API design including `/symbols/declaration`, `/symbols/usages`, `/symbols/inheritors`.

### Step 4: Extract ICppSymbol info

For each `ICppSymbol` returned by the lookup, you need to extract:
- **Name**: `CppGotoSymbolUtil.GetShortName(symbol)`
- **File path**: Unknown property on `ICppSymbol`. Options to try:
  - `ICppSymbol` may have a `Location` or `File` property
  - `CppGotoSymbolUtil.CreateOccurrence(psiServices, symbol, false)` returns a `CppDeclaredElementOccurrence` which likely has navigation coordinates
  - The `CppFileSymbolTable` that contains the symbol has a `File` property (`CppFileLocation`)
- **Line number**: Once you have a file and offset, use the standard `DocumentOffset.ToDocumentCoords()` pattern from `InspectionService.cs`
- **Kind**: Check `ICppSymbol` subtype or properties. Types to look for: `CppClassSymbol`, `CppFunctionSymbol`, `CppNamespaceSymbol`, etc.

### Step 5: Remove debug endpoints

Once `/symbols` is working, remove `DebugSymbolHandler.cs` and its registration in `InspectionHttpServer2.cs`.

---

## Key Reflection Patterns (reference)

All C++ symbol types are closed-source. Use these patterns from the existing codebase:

**Find a type by name:**
```csharp
// DebugSymbolHandler.FindTypeByName() or scan AppDomain.CurrentDomain.GetAssemblies()
var type = FindTypeByName("CppGlobalSymbolCache", "JetBrains.ReSharper.Psi.Cpp");
```

**Resolve a component:**
```csharp
// ReflectionService.ResolveComponent(type) handles multiple strategies
var cache = reflectionService.ResolveComponent(cppGlobalCacheType);
```

**Invoke a static method:**
```csharp
var method = gotoUtilType.GetMethod("GetSymbolsInScopeByName", BindingFlags.Public | BindingFlags.Static);
var result = method.Invoke(null, new object[] { cache, scope, "AActor", context });
```

**Iterate closed-source results:**
```csharp
if (result is IEnumerable enumerable)
{
    foreach (var symbol in enumerable)
    {
        // Use reflection to read properties on ICppSymbol
        var nameProp = symbol.GetType().GetProperty("ShortName");
        var name = nameProp?.GetValue(symbol) as string;
    }
}
```

**Existing examples in codebase:**
- `Services/BlueprintQueryService.cs` -- reflection-based access to `UE4AssetsCache`
- `Services/ReflectionService.cs` -- component resolution via reflection
- `Services/CppStructureWalker.cs` -- reading C++ PSI properties via reflection
