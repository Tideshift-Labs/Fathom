# C++ Symbol API Findings

Exploration of how to look up C++ symbols by name in the ReSharper/Rider SDK, similar to Rider's "Go to Symbol" (Ctrl+Shift+Alt+T).

**Started:** 2026-02-14
**Updated:** 2026-02-15
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

### Key Discovery: SymbolNameCache property

The `SymbolNameCache` property returns a `CppSymbolNameCache` instance that HAS direct name-based lookup (`GetSymbolsByShortName(string)`). This is not a standalone component; it must be accessed through this property. See "Confirmed Working: CppSymbolNameCache" section.

### What It Does NOT Have (directly)

No `GetSymbolsByName(string)` method on `CppGlobalSymbolCache` itself. But its `SymbolNameCache` property provides exactly that.

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

**Answered:** `INavigationScope` uses `new SolutionNavigationScope(solution, false, null)`. `GotoContext` has a parameterless constructor. Both confirmed working. See "Confirmed Working: Strategy A" section below.

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

### Strategy D: CppSymbolNameCache.GetSymbolsByShortName (CONFIRMED - recommended)

Direct name-indexed lookup via `CppSymbolNameCache.GetSymbolsByShortName("AActor")`. The cache is accessed via `CppGlobalSymbolCache.SymbolNameCache` property. Returns `CppList<ICppSymbol>`.

This is the simplest path: one component resolution, one property access, one method call. Also provides `GetDerivedClasses(name)` for inheritor lookup. See "Confirmed Working: CppSymbolNameCache" section below for full API.

---

## Confirmed Working: Strategy A (2026-02-15)

### GetSymbolsInScopeByName - WORKS

Tested via `/debug/cpp-goto?query=AActor`. Returns **50 symbols** in ~4-6 seconds.

**Parameter construction:**

| Parameter | Type | How to construct |
|---|---|---|
| `cache` | `CppGlobalSymbolCache` | `solution.GetComponent<CppGlobalSymbolCache>()` via reflection |
| `scope` | `INavigationScope` | `new SolutionNavigationScope(solution, false, null)` via reflection |
| `name` | `string` | The query string |
| `gotoContext` | `GotoContext` | `new GotoContext()` (parameterless constructor) |

**Constructor details:**
- `SolutionNavigationScope` is in `JetBrains.ReSharper.Feature.Services.Navigation.Goto.Misc`. Constructor: `(ISolution solution, Boolean extendsFlag, INavigationProviderFilter filter)`. Pass `false` and `null`.
- `GotoContext` is in the same namespace. Has a public parameterless constructor.

### Data extraction - ALL SOLVED

| Data | Property/Method | Type | Notes |
|---|---|---|---|
| **Name** | `Name.ToString()` | `CppQualifiedName` | `.ToString()` returns `"AActor"`. Has `.Name` (CppQualifiedNamePart) and `.Qualifier` sub-properties |
| **File path** | `ContainingFile.FullPath` | `string` | Direct string property on `CppFileLocation`. Also `.Location` (VirtualFileSystemPath) |
| **Line number** | `LocateDocumentRange(solution)` | `DocumentRange` | Returns a valid `DocumentRange`. Use `range.StartOffset.ToDocumentCoords().Line + 1` |
| **Text offset** | `Location.TextOffset` | `int` | Raw character offset. `Location` is `CppSymbolLocation` |
| **Kind** | `.GetType().Name` | string | `CppClassSymbol`, `CppFwdClassSymbol`, `CppFunctionSymbol`, `CppSimpleDeclaratorSymbol`, etc. |

**GetShortName overload resolution:** `CppGotoSymbolUtil.GetShortName` has multiple overloads. Must iterate `GetMethods()` and pick the one whose parameter type is assignable from the symbol's runtime type, not use `GetMethod()` (which throws `AmbiguousMatchException`).

### CppFileLocation API (for reference)

Key properties: `FullPath` (string), `Location` (VirtualFileSystemPath), `Name` (string), `Data` (IPath).
Key methods: `GetRandomSourceFile(ISolution)` returns `IPsiSourceFile`, `GetDocument(ISolution)` returns `IDocument`.

### Symbol types observed

| Runtime type | Meaning | Count for "AActor" |
|---|---|---|
| `CppFwdClassSymbol` | Forward declaration (`class AActor;`) | ~48 |
| `CppClassSymbol` | Full class definition | 1 |
| `CppSimpleDeclaratorSymbol` | Variable/parameter declarations | 1 |

**Important:** For a `/symbols` endpoint, forward declarations should be deprioritized. `CppClassSymbol` is the actual definition. Filter or sort by type: definitions first, then forward declarations.

### CppFwdClassSymbol interfaces (for type checking)

```
ICppClassSymbol, ICppClassOrDelegateSymbol, ICppParserSymbol,
ICppSymbol, ICppSymbolOrModuleEntity, ICppType,
ICppTypeTemplateArgument, ICppTemplateArgument, ICppModuleTemplateArgument
```

---

## Confirmed Working: CppSymbolNameCache (2026-02-15)

**Type:** `JetBrains.ReSharper.Psi.Cpp.Caches.CppSymbolNameCache`
**Access:** `cppGlobalSymbolCache.SymbolNameCache` property (NOT resolvable as a standalone component)

This is a name-indexed cache of all C++ symbols. Simpler and potentially faster than `GetSymbolsInScopeByName`.

### Key Methods

| Method | Parameters | Returns | Notes |
|---|---|---|---|
| **`GetSymbolsByShortName`** | `string shortName` | `CppList<ICppSymbol>` | **Direct name lookup!** Confirmed working for "AActor" |
| `GetSymbolsByShortNameWithReadLockHeld` | `string shortName` | `CppList<ICppSymbol>` | Same but asserts read lock already held |
| **`GetDerivedClasses`** | `string name` | `CppList<ICppSymbol>` | **Find inheritors by name!** |
| `GetSymbolName` | `ICppSymbol symbol` | `string` | Reverse lookup: symbol to name |
| `FindCachedSymbol` | `ICppSymbol symbol` | `ICppSymbol` | Find canonical version of a symbol |
| `GetSortedNamesWithSymbols` | none | `List<string>` | All indexed symbol names (for autocomplete) |
| `GetSymbols` | none | `List<ICppSymbol>` | ALL symbols |
| `ForEachSymbolWithName` | `string shortName, Action<ICppSymbol>` | void | Callback-based iteration |
| `GetIncludersByFileName` | `string fileName` | `CppList<?>` | Files that include a given file name |
| `GetNamespaceAliases` | `string name` | `ICollection<?>` | Namespace alias lookup |
| `GetTypeAliases` | `string name` | `ICollection<?>` | Type alias (typedef/using) lookup |

### Comparison: CppSymbolNameCache vs GetSymbolsInScopeByName

| Aspect | CppSymbolNameCache | GetSymbolsInScopeByName |
|---|---|---|
| Access | Property on CppGlobalSymbolCache | Static method on CppGotoSymbolUtil |
| Dependencies | Just the cache object | Cache + INavigationScope + GotoContext |
| Filtering | Returns raw matches, no filtering | May apply Rider's display filtering |
| Extra features | `GetDerivedClasses`, `GetTypeAliases` | `FilterSymbolsToShow`, `IsValidSymbol` |
| Complexity | Simpler (1 reflection call) | More complex (4 params, 3 reflection types) |

**Recommendation:** Use `CppSymbolNameCache.GetSymbolsByShortName()` as the primary lookup for the service layer. It is simpler, has fewer dependencies, and provides `GetDerivedClasses` for free. Use `GetSymbolsInScopeByName` only if we need Rider's built-in display filtering.

---

## Confirmed Working: Strategy C fallback (2026-02-15)

`CppWordIndex.GetFilesContainingWord()` + `CppGotoSymbolUtil.GetSymbolsFromPsiFile()` works. Tested: scanned 503 symbols across 5 files, found 3 matches for "AActor". Slower and less complete than Strategy A/D, but useful as a last resort.

---

## Rider Symbol Actions: LLM Mapping

Rider provides these symbol navigation features to developers. The table below maps each to a proposed Fathom endpoint and notes API feasibility based on our findings.

### Tier 1: Core navigation (high value, build first)

| Rider Feature | Shortcut | Proposed Endpoint | API | Feasibility |
|---|---|---|---|---|
| Go to Symbol | Ctrl+Shift+Alt+T | `GET /symbols?query=X` | `CppSymbolNameCache.GetSymbolsByShortName()` | **Confirmed working** |
| Go to Declaration | Ctrl+B / Ctrl+Click | `GET /symbols/declaration?symbol=X&containingType=Y` | Filter `/symbols` results to `CppClassSymbol` (not `CppFwdClassSymbol`). Extract `ContainingFile.FullPath` + `LocateDocumentRange()`. Include code snippet. | **Confirmed working** (data extraction proven) |
| Find Usages | Alt+F7 | `GET /symbols/usages?symbol=X` | `IFinder.FindReferences()` is dead for C++ (see Dead Ends). Need C++-specific alternative. `CppWordIndex.GetFilesContainingWord()` gives file-level text matches as rough fallback. | **Needs research** |
| Go to Derived / Find Inheritors | | `GET /symbols/inheritors?symbol=X` | `CppSymbolNameCache.GetDerivedClasses(name)` | **Likely works** (API exists, untested) |

#### Declaration design notes (Ctrl+Click equivalent for LLMs)

Rider's Ctrl+Click resolves the specific reference under the cursor through the C++ semantic model. It knows the exact target. Our approach starts from a name string, which can be ambiguous (e.g. `BeginPlay` matches dozens of overrides).

Two design decisions address this:

1. **`containingType` parameter**: Lets the LLM disambiguate. `symbol=BeginPlay&containingType=AMyPlayerController` returns only that class's override. LLMs usually know the containing type from the code they're reading.

2. **Inline code snippets**: Rider opens the file and shows the code. Returning just a file+line forces the LLM into a second round trip. `/symbols/declaration` should return ~30 lines of source around the declaration, giving the "Ctrl+Click experience" in one call.

### Tier 2: Extended navigation (medium value)

| Rider Feature | Shortcut | Proposed Endpoint | API | Feasibility |
|---|---|---|---|---|
| Type Hierarchy | Ctrl+H | `GET /symbols/hierarchy?symbol=X` | Derived: `GetDerivedClasses()`. Bases: need to read base specifiers from `CppClassSymbol` properties (unexplored). | **Partially feasible** |
| Go to Base | Ctrl+U | (part of hierarchy) | Read base class info from `CppClassSymbol`. The PSI `ClassSpecifier` node has base specifier children. `CppStructureWalker` already reads `BaseSpecifierList`. | **Likely works** |
| Symbol Members | Ctrl+F12 | `GET /symbols/members?symbol=X` | Find the class via name lookup, then use `CppGotoSymbolUtil.GetSymbolsFromPsiFile()` on its file, filter to children of that class. Or walk `CppFileSymbolTable.GlobalNamespaceSymbol` tree. | **Likely works** |
| Navigate to H/CPP pair | Alt+F10 | `GET /symbols/counterpart?file=X` | A class declared in `.h` typically has definitions in `.cpp`. Find both via declaration lookup (the class symbol may appear in both). | **Likely works** |

### Tier 3: Advanced features (lower priority, explore later)

| Rider Feature | Shortcut | Proposed Endpoint | API | Feasibility |
|---|---|---|---|---|
| Call Hierarchy | Ctrl+Alt+H | `GET /symbols/callers?symbol=X` | Would need cross-reference index. `IFinder` is dead for C++. C++ call graph likely lives in closed-source analysis. | **Unknown, probably hard** |
| Quick Documentation | Ctrl+Q | `GET /symbols/doc?symbol=X` | Read the comment block above the declaration from the source file. No special API needed, just file reading with offset. | **Easy once declaration works** |
| Find Implementations | Ctrl+Alt+B | `GET /symbols/implementations?symbol=X` | For virtual methods: find all overrides across derived classes. Combines inheritors + member lookup. Complex but composable from Tier 1+2 APIs. | **Composable from other APIs** |
| Signature / Quick Info | | `GET /symbols/signature?symbol=X` | Read the declaration line(s) from the source file at the symbol's location. | **Easy once declaration works** |
| Type Aliases | | (part of search) | `CppSymbolNameCache.GetTypeAliases(name)` | **API exists** |

### What already exists in Fathom (no new work needed)

| Rider Feature | Fathom Equivalent | Notes |
|---|---|---|
| File Structure (Alt+7) | `GET /describe_code?file=X` | Already shows classes, methods, fields, inheritance per file |
| Find in Files | `GET /files` + text search | File listing exists; code search is done by the LLM directly |
| Go to Class (C++ only) | `GET /classes?search=X` | Already searches C++ classes with base class info |
| Blueprint Inheritors | `GET /blueprints?class=X` | Already finds Blueprint classes derived from C++ |

### Priority order for implementation

1. **`/symbols`** (search by name) -- the foundation everything else builds on
2. **`/symbols/declaration`** (go to definition) -- filter out forward decls, return the real definition
3. **`/symbols/inheritors`** (derived types) -- `GetDerivedClasses` is sitting right there
4. **`/symbols/usages`** (find references) -- needs research into C++ reference finding APIs
5. **`/symbols/members`** (class members by name) -- complements `/describe_code` (which requires a file path)
6. **`/symbols/hierarchy`** (full inheritance chain) -- combines base extraction + `GetDerivedClasses`

---

## Debug Endpoints

The file `src/dotnet/ReSharperPlugin.Fathom/Handlers/DebugSymbolHandler.cs` contains temporary debug endpoints wired into `InspectionHttpServer2.cs`:

| Endpoint | Purpose | Status |
|---|---|---|
| `GET /debug/symbol-scope?query=X` | Test `ISymbolScope` | Done, confirmed dead for C++ |
| `GET /debug/find-refs?query=X` | Test `IFinder.FindReferences` | Done, confirmed dead for C++ |
| `GET /debug/declarations?query=X` | Test `IDeclaredElement.GetDeclarations` | Done |
| `GET /debug/symbol-diag?query=X` | Deep diagnostic: module survey, PSI walk, assembly scan | Done, found C++ types |
| `GET /debug/cpp-cache?query=X` | Probe C++ caches via reflection | Done, found working APIs |
| `GET /debug/cpp-goto?query=X` | Probe Strategy A + D + C, extract name/file/line | **Done, all strategies confirmed** |

Remove all debug endpoints once the real `/symbols` service is built and verified.

---

## Concrete Next Steps

### Step 1: Build SymbolSearchService

Create `Services/SymbolSearchService.cs` using the confirmed APIs:

```csharp
// Core dependencies (resolved once in constructor via reflection)
CppGlobalSymbolCache cache;       // solution.GetComponent<CppGlobalSymbolCache>()
CppSymbolNameCache nameCache;     // cache.SymbolNameCache property

// Primary lookup
CppList<ICppSymbol> symbols = nameCache.GetSymbolsByShortName("AActor");

// For each symbol, extract:
string name = symbol.Name.ToString();                    // CppQualifiedName.ToString()
string file = symbol.ContainingFile.FullPath;            // CppFileLocation.FullPath
int line = symbol.LocateDocumentRange(solution)          // DocumentRange
               .StartOffset.ToDocumentCoords().Line + 1;
string kind = symbol.GetType().Name;                     // "CppClassSymbol", etc.
```

Filter/sort results: `CppClassSymbol` before `CppFwdClassSymbol`. Deduplicate by (name, file, line).

### Step 2: Build SymbolsHandler + Models

- `Handlers/SymbolsHandler.cs` for `GET /symbols`, `GET /symbols/declaration`, `GET /symbols/inheritors`
- `Models/SymbolModels.cs` for response DTOs
- Wire into `InspectionHttpServer2.cs` and `FathomMcpServer.cs`

### Step 3: Test inheritors

Try `CppSymbolNameCache.GetDerivedClasses("AActor")` via a debug call or directly in the service. If it returns results, wire it into `/symbols/inheritors`.

### Step 4: Research C++ find usages

Explore C++-specific alternatives to `IFinder.FindReferences()`:
- Search for `CppFindUsages`, `CppReferenceSearcher`, or similar types in the C++ assemblies
- Check if `UEAssetUsagesSearcher.GetFindUsagesResults()` works for pure C++ references (not just Blueprint)
- `CppWordIndex.GetFilesContainingWord()` as a rough fallback (text matches, not semantic references)

### Step 5: Remove debug endpoints

Once `/symbols` is verified working, remove `DebugSymbolHandler.cs`.

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

**Access a sub-component via property:**
```csharp
// CppSymbolNameCache is NOT a standalone component. Access via CppGlobalSymbolCache.
var nameCacheProp = cache.GetType().GetProperty("SymbolNameCache");
var nameCache = nameCacheProp.GetValue(cache);
```

**Invoke a method on a closed-source object:**
```csharp
var getByName = nameCache.GetType().GetMethod("GetSymbolsByShortName",
    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
var result = getByName.Invoke(nameCache, new object[] { "AActor" });
```

**Handle overloaded static methods (avoid AmbiguousMatchException):**
```csharp
// Do NOT use GetMethod("GetShortName") when multiple overloads exist.
// Instead, iterate and pick the right one:
var methods = gotoUtilType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name == "GetShortName");
foreach (var m in methods)
{
    if (m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(symbol.GetType()))
    {
        var name = m.Invoke(null, new[] { symbol }) as string;
        break;
    }
}
```

**Extract data from ICppSymbol (confirmed working pattern):**
```csharp
// Name
var nameProp = symbol.GetType().GetProperty("Name");
var qualName = nameProp.GetValue(symbol);   // CppQualifiedName
var shortName = qualName.ToString();        // "AActor"

// File path
var cfProp = symbol.GetType().GetProperty("ContainingFile");
var fileLocation = cfProp.GetValue(symbol); // CppFileLocation
var fpProp = fileLocation.GetType().GetProperty("FullPath");
var filePath = fpProp.GetValue(fileLocation) as string;

// Line number
var locateMethod = symbol.GetType().GetMethod("LocateDocumentRange");
var docRange = (DocumentRange)locateMethod.Invoke(symbol, new object[] { solution });
var line = (int)docRange.StartOffset.ToDocumentCoords().Line + 1;
```

**Existing examples in codebase:**
- `Services/BlueprintQueryService.cs` -- reflection-based access to `UE4AssetsCache`
- `Services/ReflectionService.cs` -- component resolution via reflection
- `Services/CppStructureWalker.cs` -- reading C++ PSI properties via reflection
