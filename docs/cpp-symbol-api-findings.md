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
