using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Application.Progress;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Formatting;

namespace ReSharperPlugin.Fathom.Handlers;

/// <summary>
/// Temporary debug endpoints to test whether ReSharper SDK symbol APIs work for C++.
///
/// GET /debug/symbol-scope?query=AActor
///   Tests ISymbolScope.GetElementsByShortName() across all PSI modules.
///
/// GET /debug/find-refs?query=AActor
///   Tests IFinder.FindReferences() on the first element found by symbol-scope.
///
/// GET /debug/declarations?query=AActor
///   Tests IDeclaredElement.GetDeclarations() on the first element found.
///
/// These endpoints are for experimentation only and will be removed once we know
/// which APIs work for C++ and which need reflection fallbacks.
/// </summary>
public class DebugSymbolHandler : IRequestHandler
{
    private readonly ISolution _solution;

    public DebugSymbolHandler(ISolution solution)
    {
        _solution = solution;
    }

    public bool CanHandle(string path) =>
        path == "/debug/symbol-scope" ||
        path == "/debug/find-refs" ||
        path == "/debug/declarations" ||
        path == "/debug/symbol-diag" ||
        path == "/debug/cpp-cache";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

        try
        {
            switch (path)
            {
                case "/debug/symbol-scope":
                    HandleSymbolScope(ctx);
                    break;
                case "/debug/find-refs":
                    HandleFindRefs(ctx);
                    break;
                case "/debug/declarations":
                    HandleDeclarations(ctx);
                    break;
                case "/debug/symbol-diag":
                    HandleSymbolDiag(ctx);
                    break;
                case "/debug/cpp-cache":
                    HandleCppCache(ctx);
                    break;
            }
        }
        catch (Exception ex)
        {
            HttpHelpers.Respond(ctx, 500, "text/plain; charset=utf-8",
                $"Exception: {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Tests ISymbolScope.GetElementsByShortName() across all PSI modules.
    /// Reports: which modules exist, how many elements each returns, element details.
    /// </summary>
    private void HandleSymbolScope(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString["query"];
        if (string.IsNullOrWhiteSpace(query))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: query");
            return;
        }

        var scope = ctx.Request.QueryString["scope"] ?? "all";
        var sb = new StringBuilder();
        sb.AppendLine($"# Symbol Scope Experiment: `{query}`");
        sb.AppendLine();

        var sw = Stopwatch.StartNew();
        ReadLockCookie.Execute(() =>
        {
            var psiServices = _solution.GetPsiServices();
            var psiModules = psiServices.Modules;

            // Collect all modules
            var allModules = psiModules.GetModules().ToList();
            sb.AppendLine($"## PSI Modules: {allModules.Count} total");
            sb.AppendLine();

            // Group by type for overview
            var modulesByType = allModules
                .GroupBy(m => m.GetType().Name)
                .OrderByDescending(g => g.Count());
            foreach (var group in modulesByType)
            {
                sb.AppendLine($"- `{group.Key}`: {group.Count()}");
            }
            sb.AppendLine();

            // Search across all modules
            var allElements = new List<(IDeclaredElement element, string moduleName, string moduleType)>();
            var modulesSearched = 0;
            var modulesWithResults = 0;
            var errors = new List<string>();

            foreach (var module in allModules)
            {
                // Optional: filter to solution-local modules only
                if (scope == "user")
                {
                    var modulePath = TryGetModulePath(module);
                    if (modulePath != null && !modulePath.StartsWith(_solution.SolutionDirectory.FullPath,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                modulesSearched++;
                try
                {
                    var symbolScope = psiServices.Symbols.GetSymbolScope(
                        module, caseSensitive: true, withReferences: true);

                    var elements = symbolScope.GetElementsByShortName(query);
                    if (elements != null && elements.Any())
                    {
                        modulesWithResults++;
                        foreach (var element in elements)
                        {
                            allElements.Add((element, module.DisplayName, module.GetType().Name));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"`{module.DisplayName}` ({module.GetType().Name}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            sw.Stop();

            sb.AppendLine($"## Results");
            sb.AppendLine();
            sb.AppendLine($"- Query: `{query}`");
            sb.AppendLine($"- Scope: `{scope}`");
            sb.AppendLine($"- Modules searched: {modulesSearched}");
            sb.AppendLine($"- Modules with results: {modulesWithResults}");
            sb.AppendLine($"- Total elements found: {allElements.Count}");
            sb.AppendLine($"- Time: {sw.ElapsedMilliseconds}ms");
            sb.AppendLine();

            if (allElements.Count > 0)
            {
                sb.AppendLine($"## Elements ({Math.Min(allElements.Count, 50)} shown)");
                sb.AppendLine();
                sb.AppendLine("| # | ShortName | Kind | CLR Type | Module | ModuleType |");
                sb.AppendLine("|---|-----------|------|----------|--------|------------|");

                var shown = 0;
                foreach (var (element, moduleName, moduleType) in allElements)
                {
                    if (shown++ >= 50) break;

                    var kind = GetElementKind(element);
                    var clrType = element.GetType().Name;
                    var shortName = element.ShortName ?? "(null)";

                    sb.AppendLine($"| {shown} | `{shortName}` | {kind} | `{clrType}` | {Truncate(moduleName, 40)} | `{moduleType}` |");
                }
                sb.AppendLine();

                // Show declaration locations for first 10 elements
                sb.AppendLine("## Declaration Locations (first 10)");
                sb.AppendLine();
                shown = 0;
                foreach (var (element, moduleName, _) in allElements)
                {
                    if (shown++ >= 10) break;
                    try
                    {
                        var declarations = element.GetDeclarations();
                        if (!declarations.Any())
                        {
                            sb.AppendLine($"- `{element.ShortName}` (from {Truncate(moduleName, 30)}): **0 declarations**");
                            continue;
                        }
                        foreach (var decl in declarations)
                        {
                            var sourceFile = decl.GetSourceFile();
                            var filePath = sourceFile?.GetLocation().FullPath ?? "(no file)";
                            var line = GetLineNumber(sourceFile, decl);
                            var relativePath = TryMakeRelative(filePath);
                            sb.AppendLine($"- `{element.ShortName}`: `{relativePath}:{line}`");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"- `{element.ShortName}`: GetDeclarations() threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## Errors ({errors.Count})");
                sb.AppendLine();
                foreach (var error in errors.Take(20))
                    sb.AppendLine($"- {error}");
            }

            // Also try case-insensitive if no results
            if (allElements.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Retry: case-insensitive");
                sb.AppendLine();

                var ciResults = new List<string>();
                foreach (var module in allModules.Take(20)) // limit to avoid timeout
                {
                    try
                    {
                        var ciScope = psiServices.Symbols.GetSymbolScope(
                            module, caseSensitive: false, withReferences: true);
                        var ciElements = ciScope.GetElementsByShortName(query);
                        if (ciElements != null && ciElements.Any())
                        {
                            foreach (var e in ciElements)
                                ciResults.Add($"`{e.ShortName}` ({e.GetType().Name}) in `{module.DisplayName}`");
                        }
                    }
                    catch { /* skip */ }
                }

                if (ciResults.Count > 0)
                {
                    sb.AppendLine($"Found {ciResults.Count} case-insensitive matches:");
                    foreach (var r in ciResults.Take(20))
                        sb.AppendLine($"- {r}");
                }
                else
                {
                    sb.AppendLine("No case-insensitive matches either.");
                }
            }
        });

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// Tests IFinder.FindReferences() on the first element returned by symbol scope lookup.
    /// </summary>
    private void HandleFindRefs(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString["query"];
        if (string.IsNullOrWhiteSpace(query))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: query");
            return;
        }

        var limitStr = ctx.Request.QueryString["limit"];
        var limit = 30;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = Math.Min(parsedLimit, 200);

        var sb = new StringBuilder();
        sb.AppendLine($"# Find References Experiment: `{query}`");
        sb.AppendLine();

        var sw = Stopwatch.StartNew();
        var responded = false;

        ReadLockCookie.Execute(() =>
        {
            var psiServices = _solution.GetPsiServices();

            // Step 1: Find the element via symbol scope
            sb.AppendLine("## Step 1: Find element via symbol scope");
            sb.AppendLine();

            var targetElement = FindFirstElement(psiServices, query, out var targetModule);

            if (targetElement == null)
            {
                sb.AppendLine($"No element found for `{query}`. Cannot test FindReferences.");
                sw.Stop();
                sb.AppendLine($"\nTime: {sw.ElapsedMilliseconds}ms");
                HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
                responded = true;
                return;
            }

            sb.AppendLine($"- Element: `{targetElement.ShortName}`");
            sb.AppendLine($"- CLR Type: `{targetElement.GetType().Name}`");
            sb.AppendLine($"- Module: `{targetModule}`");
            sb.AppendLine($"- Kind: {GetElementKind(targetElement)}");
            sb.AppendLine();

            // Step 2: Try IFinder.FindReferences
            sb.AppendLine("## Step 2: FindReferences");
            sb.AppendLine();

            try
            {
                var searchDomainFactory = _solution.GetComponent<SearchDomainFactory>();
                var searchDomain = searchDomainFactory.CreateSearchDomain(_solution, false);
                var finder = psiServices.Finder;

                var references = new List<(string file, int line, string context)>();
                var refCount = 0;
                var truncated = false;

                finder.FindReferences(
                    targetElement,
                    searchDomain,
                    new FindResultConsumer(result =>
                    {
                        refCount++;
                        if (references.Count >= limit)
                        {
                            truncated = true;
                            return FindExecution.Continue;
                        }

                        if (result is FindResultReference refResult)
                        {
                            try
                            {
                                var treeNode = refResult.Reference.GetTreeNode();
                                var sourceFile = treeNode?.GetSourceFile();
                                var filePath = sourceFile?.GetLocation().FullPath ?? "(unknown)";
                                var relativePath = TryMakeRelative(filePath);
                                var line = 0;

                                // Get line number from tree node's offset
                                if (treeNode != null && sourceFile?.Document != null)
                                {
                                    var offset = treeNode.GetTreeStartOffset().Offset;
                                    var doc = sourceFile.Document;
                                    if (offset >= 0 && offset <= doc.GetTextLength())
                                        line = (int)new DocumentOffset(doc, offset)
                                            .ToDocumentCoords().Line + 1;
                                }

                                // Get context line
                                var contextLine = TryGetContextLine(sourceFile?.Document, line);

                                references.Add((relativePath, line, contextLine));
                            }
                            catch (Exception ex)
                            {
                                references.Add(("(error)", 0, ex.GetType().Name + ": " + ex.Message));
                            }
                        }
                        else
                        {
                            references.Add(("(non-reference result)", 0, result.GetType().Name));
                        }

                        return FindExecution.Continue;
                    }),
                    NullProgressIndicator.Create());

                sw.Stop();

                sb.AppendLine($"- Total references found: {refCount}");
                sb.AppendLine($"- Shown: {references.Count}");
                sb.AppendLine($"- Truncated: {truncated}");
                sb.AppendLine($"- Time: {sw.ElapsedMilliseconds}ms");
                sb.AppendLine();

                if (references.Count > 0)
                {
                    sb.AppendLine("| # | File | Line | Context |");
                    sb.AppendLine("|---|------|------|---------|");
                    for (var i = 0; i < references.Count; i++)
                    {
                        var (file, line, context) = references[i];
                        var escapedContext = context?.Replace("|", "\\|") ?? "";
                        sb.AppendLine($"| {i + 1} | `{file}` | {line} | `{Truncate(escapedContext.Trim(), 80)}` |");
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                sb.AppendLine($"**FindReferences threw:** `{ex.GetType().Name}`: {ex.Message}");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(ex.StackTrace);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine($"Time: {sw.ElapsedMilliseconds}ms");
            }
        });

        if (!responded)
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// Tests IDeclaredElement.GetDeclarations() in detail for all elements matching the query.
    /// Shows file paths, line numbers, declaration node types, and containing types.
    /// </summary>
    private void HandleDeclarations(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString["query"];
        if (string.IsNullOrWhiteSpace(query))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: query");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Declarations Experiment: `{query}`");
        sb.AppendLine();

        var sw = Stopwatch.StartNew();
        var responded = false;

        ReadLockCookie.Execute(() =>
        {
            var psiServices = _solution.GetPsiServices();

            // Find all elements with this name across all modules (deduplicate by reference equality)
            var seen = new HashSet<IDeclaredElement>(ReferenceEqualityComparer<IDeclaredElement>.Instance);
            var elements = new List<(IDeclaredElement element, string moduleName)>();

            foreach (var module in psiServices.Modules.GetModules())
            {
                try
                {
                    var symbolScope = psiServices.Symbols.GetSymbolScope(
                        module, caseSensitive: true, withReferences: true);
                    var found = symbolScope.GetElementsByShortName(query);
                    if (found == null) continue;

                    foreach (var elem in found)
                    {
                        if (seen.Add(elem))
                            elements.Add((elem, module.DisplayName));
                    }
                }
                catch { /* skip modules that throw */ }
            }

            sw.Stop();

            sb.AppendLine($"- Unique elements found: {elements.Count}");
            sb.AppendLine($"- Search time: {sw.ElapsedMilliseconds}ms");
            sb.AppendLine();

            if (elements.Count == 0)
            {
                sb.AppendLine("No elements found.");
                HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
                responded = true;
                return;
            }

            foreach (var (element, moduleName) in elements.Take(20))
            {
                sb.AppendLine($"### `{element.ShortName}` ({GetElementKind(element)})");
                sb.AppendLine();
                sb.AppendLine($"- CLR Type: `{element.GetType().Name}`");
                sb.AppendLine($"- Module: `{Truncate(moduleName, 60)}`");

                // Try to get containing type info
                try
                {
                    var containingType = TryGetContainingTypeName(element);
                    if (containingType != null)
                        sb.AppendLine($"- Containing type: `{containingType}`");
                }
                catch { /* skip */ }

                // Check known interfaces
                sb.AppendLine($"- Is ITypeElement: {element is ITypeElement}");
                sb.AppendLine($"- Is ITypeMember: {element is ITypeMember}");
                sb.AppendLine($"- Is IFunction: {element is IFunction}");
                sb.AppendLine($"- Is IClrDeclaredElement: {element is IClrDeclaredElement}");

                // Implemented interfaces (to understand C++ element types)
                var interfaces = element.GetType().GetInterfaces()
                    .Where(i => i.Name.StartsWith("I") &&
                                (i.Name.Contains("Declared") || i.Name.Contains("Type") ||
                                 i.Name.Contains("Member") || i.Name.Contains("Function") ||
                                 i.Name.Contains("Cpp") || i.Name.Contains("Class")))
                    .Select(i => i.Name)
                    .OrderBy(n => n)
                    .Take(15);
                sb.AppendLine($"- Key interfaces: {string.Join(", ", interfaces.Select(n => $"`{n}`"))}");
                sb.AppendLine();

                try
                {
                    var declarations = element.GetDeclarations();
                    sb.AppendLine($"**Declarations: {declarations.Count}**");
                    sb.AppendLine();

                    if (declarations.Count > 0)
                    {
                        sb.AppendLine("| # | File | Line | Node Type | Name Range |");
                        sb.AppendLine("|---|------|------|-----------|------------|");

                        for (var i = 0; i < Math.Min(declarations.Count, 20); i++)
                        {
                            var decl = declarations[i];
                            var sourceFile = decl.GetSourceFile();
                            var filePath = sourceFile?.GetLocation().FullPath ?? "(no file)";
                            var relativePath = TryMakeRelative(filePath);
                            var line = GetLineNumber(sourceFile, decl);
                            var nodeType = decl.GetType().Name;
                            var nameRange = "(unknown)";
                            try
                            {
                                var nr = decl.GetNameRange();
                                nameRange = $"{nr.StartOffset}-{nr.EndOffset}";
                            }
                            catch { /* skip */ }

                            sb.AppendLine($"| {i + 1} | `{relativePath}` | {line} | `{nodeType}` | {nameRange} |");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"**GetDeclarations() threw:** `{ex.GetType().Name}`: {ex.Message}");
                }
                sb.AppendLine();
            }
        });

        if (!responded)
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// Deep diagnostic: probes multiple angles to find how C++ symbols are indexed.
    ///
    /// GET /debug/symbol-diag?query=AActor
    ///
    /// Tests:
    /// 1. GetAllShortNames() on sample modules (are ANY names indexed?)
    /// 2. IPsiServices.Symbols available APIs (what methods exist?)
    /// 3. Walking a known C++ source file's PSI tree to get an IDeclaredElement,
    ///    then testing IFinder.FindReferences on that element directly
    /// 4. Solution-level component scan for C++ caches/indices
    /// </summary>
    private void HandleSymbolDiag(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString["query"] ?? "AActor";
        var sb = new StringBuilder();
        sb.AppendLine($"# Symbol Diagnostic: `{query}`");
        sb.AppendLine();

        ReadLockCookie.Execute(() =>
        {
            var psiServices = _solution.GetPsiServices();
            var allModules = psiServices.Modules.GetModules().ToList();

            // --- Section 1: Check if ANY names exist in symbol scopes ---
            sb.AppendLine("## 1. GetAllShortNames() sample");
            sb.AppendLine();
            sb.AppendLine("Checking if symbol scopes contain ANY names at all:");
            sb.AppendLine();

            // Sample one module of each type
            var sampledTypes = new HashSet<string>();
            foreach (var module in allModules)
            {
                var moduleType = module.GetType().Name;
                if (!sampledTypes.Add(moduleType)) continue;

                try
                {
                    var symbolScope = psiServices.Symbols.GetSymbolScope(
                        module, caseSensitive: true, withReferences: true);
                    var allNames = symbolScope.GetAllShortNames();
                    var nameCount = allNames.Count();
                    var sample = allNames.Take(5).ToList();
                    sb.AppendLine($"- `{moduleType}` ({module.DisplayName}): **{nameCount} names**");
                    if (sample.Count > 0)
                        sb.AppendLine($"  Sample: {string.Join(", ", sample.Select(n => $"`{n}`"))}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"- `{moduleType}`: threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            sb.AppendLine();

            // --- Section 2: Try ProjectPsiModule modules with C++ files ---
            sb.AppendLine("## 2. ProjectPsiModule details");
            sb.AppendLine();
            sb.AppendLine("Checking C++ project modules specifically:");
            sb.AppendLine();

            var projectModules = allModules
                .Where(m => m.GetType().Name == "ProjectPsiModule")
                .Take(10);

            foreach (var module in projectModules)
            {
                try
                {
                    var symbolScope = psiServices.Symbols.GetSymbolScope(
                        module, caseSensitive: true, withReferences: true);
                    var nameCount = symbolScope.GetAllShortNames().Count();

                    // Check module's source files
                    var sourceFiles = module.SourceFiles.ToList();
                    var cppFiles = sourceFiles.Where(f =>
                    {
                        var ext = f.GetLocation().ExtensionNoDot.ToLowerInvariant();
                        return ext == "cpp" || ext == "h" || ext == "hpp";
                    }).ToList();

                    sb.AppendLine($"- `{Truncate(module.DisplayName, 50)}`: {nameCount} names, " +
                                  $"{sourceFiles.Count} source files ({cppFiles.Count} C++)");

                    // If this module has C++ files and 0 names, that confirms the gap
                    if (cppFiles.Count > 0 && nameCount == 0)
                        sb.AppendLine($"  **GAP CONFIRMED: {cppFiles.Count} C++ files but 0 indexed names**");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"- `{module.DisplayName}`: threw {ex.GetType().Name}");
                }
            }
            sb.AppendLine();

            // --- Section 3: Walk a C++ file's PSI tree, get an element, test FindReferences ---
            sb.AppendLine("## 3. PSI tree walk + FindReferences on known element");
            sb.AppendLine();

            IDeclaredElement foundElement = null;
            IPsiSourceFile foundInFile = null;
            var solutionDir = _solution.SolutionDirectory;

            // Find a C++ source file under the solution directory
            foreach (var project in _solution.GetAllProjects())
            {
                if (foundElement != null) break;
                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    if (foundElement != null) break;
                    var sourceFile = projectFile.ToSourceFile();
                    if (sourceFile == null) continue;

                    var path = sourceFile.GetLocation();
                    if (!path.StartsWith(solutionDir)) continue;
                    var ext = path.ExtensionNoDot.ToLowerInvariant();
                    if (ext != "h" && ext != "cpp") continue;

                    // Walk the PSI tree looking for a class declaration
                    var primaryFile = projectFile.GetPrimaryPsiFile();
                    if (primaryFile == null) continue;

                    foreach (var node in primaryFile.Descendants())
                    {
                        if (foundElement != null) break;
                        var typeName = node.GetType().Name;
                        if (typeName != "ClassSpecifier") continue;

                        // Try to get DeclaredElement via reflection (same as CppStructureWalker)
                        try
                        {
                            var prop = node.GetType().GetProperty("DeclaredElement",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (prop == null) continue;
                            var elem = prop.GetValue(node) as IDeclaredElement;
                            if (elem == null) continue;

                            foundElement = elem;
                            foundInFile = sourceFile;

                            sb.AppendLine($"Found element from PSI tree walk:");
                            sb.AppendLine($"- Name: `{elem.ShortName}`");
                            sb.AppendLine($"- CLR Type: `{elem.GetType().Name}`");
                            sb.AppendLine($"- File: `{path.MakeRelativeTo(solutionDir)}`");
                            sb.AppendLine($"- Is IDeclaredElement: true");
                            sb.AppendLine($"- Is ITypeElement: {elem is ITypeElement}");

                            // Show interfaces
                            var ifaces = elem.GetType().GetInterfaces()
                                .Select(i => i.Name)
                                .Where(n => n.Contains("Cpp") || n.Contains("Declared") ||
                                            n.Contains("Type") || n.Contains("Class") ||
                                            n.Contains("Member"))
                                .OrderBy(n => n)
                                .Take(15);
                            sb.AppendLine($"- Interfaces: {string.Join(", ", ifaces.Select(n => $"`{n}`"))}");
                            sb.AppendLine();

                            // Test GetDeclarations
                            try
                            {
                                var decls = elem.GetDeclarations();
                                sb.AppendLine($"GetDeclarations(): **{decls.Count} declarations**");
                                foreach (var decl in decls.Take(5))
                                {
                                    var sf = decl.GetSourceFile();
                                    var fp = sf?.GetLocation().FullPath ?? "(no file)";
                                    sb.AppendLine($"  - `{TryMakeRelative(fp)}`");
                                }
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"GetDeclarations(): threw {ex.GetType().Name}: {ex.Message}");
                            }
                            sb.AppendLine();
                        }
                        catch { /* skip */ }
                    }
                }
            }

            if (foundElement == null)
            {
                sb.AppendLine("No C++ class found in user files via PSI tree walk.");
                sb.AppendLine();
            }
            else
            {
                // Now try FindReferences on this PSI-obtained element
                sb.AppendLine($"### FindReferences on `{foundElement.ShortName}` (from PSI tree)");
                sb.AppendLine();

                try
                {
                    var searchDomainFactory = _solution.GetComponent<SearchDomainFactory>();
                    var searchDomain = searchDomainFactory.CreateSearchDomain(_solution, false);
                    var finder = psiServices.Finder;

                    var refs = new List<string>();
                    var refCount = 0;
                    var sw = Stopwatch.StartNew();

                    finder.FindReferences(
                        foundElement,
                        searchDomain,
                        new FindResultConsumer(result =>
                        {
                            refCount++;
                            if (refs.Count >= 20) return FindExecution.Continue;

                            if (result is FindResultReference refResult)
                            {
                                try
                                {
                                    var treeNode = refResult.Reference.GetTreeNode();
                                    var sf = treeNode?.GetSourceFile();
                                    var fp = sf?.GetLocation().FullPath ?? "(unknown)";
                                    var line = 0;
                                    if (treeNode != null && sf?.Document != null)
                                    {
                                        var offset = treeNode.GetTreeStartOffset().Offset;
                                        if (offset >= 0 && offset <= sf.Document.GetTextLength())
                                            line = (int)new DocumentOffset(sf.Document, offset)
                                                .ToDocumentCoords().Line + 1;
                                    }
                                    refs.Add($"`{TryMakeRelative(fp)}:{line}`");
                                }
                                catch (Exception ex)
                                {
                                    refs.Add($"(error: {ex.GetType().Name})");
                                }
                            }
                            else
                            {
                                refs.Add($"(non-ref: {result.GetType().Name})");
                            }
                            return FindExecution.Continue;
                        }),
                        NullProgressIndicator.Create());

                    sw.Stop();
                    sb.AppendLine($"- References found: **{refCount}**");
                    sb.AppendLine($"- Time: {sw.ElapsedMilliseconds}ms");
                    foreach (var r in refs)
                        sb.AppendLine($"  - {r}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"FindReferences threw: `{ex.GetType().Name}`: {ex.Message}");
                    sb.AppendLine("```");
                    sb.AppendLine(ex.StackTrace);
                    sb.AppendLine("```");
                }
                sb.AppendLine();

                // Also try: can we find this element via symbol scope by its own name?
                sb.AppendLine($"### Reverse check: search symbol scope for `{foundElement.ShortName}`");
                sb.AppendLine();
                var reverseFound = false;
                foreach (var module in allModules)
                {
                    try
                    {
                        var symbolScope = psiServices.Symbols.GetSymbolScope(
                            module, caseSensitive: true, withReferences: true);
                        var elements = symbolScope.GetElementsByShortName(foundElement.ShortName);
                        if (elements != null && elements.Any())
                        {
                            sb.AppendLine($"- Found in `{module.GetType().Name}` ({module.DisplayName}):");
                            foreach (var e in elements.Take(5))
                                sb.AppendLine($"  - `{e.ShortName}` ({e.GetType().Name}), same object: {ReferenceEquals(e, foundElement)}");
                            reverseFound = true;
                        }
                    }
                    catch { /* skip */ }
                }
                if (!reverseFound)
                    sb.AppendLine("**NOT FOUND via symbol scope** (confirms ISymbolScope gap for C++)");
                sb.AppendLine();
            }

            // --- Section 4: Scan solution components for C++ caches ---
            sb.AppendLine("## 4. Solution components with 'Symbol' or 'Cache' or 'Index' in name");
            sb.AppendLine();

            try
            {
                // Use reflection to find components that might provide symbol lookup
                var container = _solution.GetType();
                var methods = container.GetMethods(System.Reflection.BindingFlags.Public |
                                                   System.Reflection.BindingFlags.Instance);

                // Look for GetComponent<T> and try to enumerate container contents
                // Instead, scan loaded assemblies for types that might be relevant
                var relevantTypes = new List<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.FullName.Contains("JetBrains")) continue;
                    if (!asm.FullName.Contains("Cpp") && !asm.FullName.Contains("Psi")) continue;

                    try
                    {
                        foreach (var type in asm.GetTypes())
                        {
                            if (!type.IsPublic) continue;
                            var name = type.Name;
                            if ((name.Contains("SymbolTable") || name.Contains("SymbolScope") ||
                                 name.Contains("SymbolCache") || name.Contains("DeclarationCache") ||
                                 name.Contains("WordIndex") || name.Contains("CppIndex") ||
                                 name.Contains("CppCache") || name.Contains("CppSymbol") ||
                                 name.Contains("GotoSymbol") || name.Contains("GotoClass") ||
                                 name.Contains("NavigateToSymbol")) &&
                                !name.Contains("Test"))
                            {
                                relevantTypes.Add($"`{type.FullName}` ({asm.GetName().Name})");
                            }
                        }
                    }
                    catch { /* skip assemblies that can't be reflected */ }
                }

                if (relevantTypes.Count > 0)
                {
                    sb.AppendLine($"Found {relevantTypes.Count} potentially relevant types:");
                    foreach (var t in relevantTypes.OrderBy(t => t).Take(40))
                        sb.AppendLine($"- {t}");
                }
                else
                {
                    sb.AppendLine("No relevant types found in loaded Cpp/Psi assemblies.");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Assembly scan threw: {ex.GetType().Name}: {ex.Message}");
            }
            sb.AppendLine();

            // --- Section 5: Try IPsiServices.Symbols API surface ---
            sb.AppendLine("## 5. IPsiServices.Symbols API surface");
            sb.AppendLine();

            try
            {
                var symbolsObj = psiServices.Symbols;
                var symbolsType = symbolsObj.GetType();
                var publicMethods = symbolsType.GetMethods(System.Reflection.BindingFlags.Public |
                                                           System.Reflection.BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName) // exclude property getters/setters
                    .Select(m => $"`{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})`")
                    .OrderBy(m => m);

                sb.AppendLine($"Type: `{symbolsType.FullName}`");
                sb.AppendLine();
                foreach (var m in publicMethods)
                    sb.AppendLine($"- {m}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Threw: {ex.GetType().Name}: {ex.Message}");
            }
        });

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    /// <summary>
    /// Probes CppGlobalSymbolCache, CppWordIndex, and CppGotoSymbolUtil via reflection.
    ///
    /// GET /debug/cpp-cache?query=AActor
    /// </summary>
    private void HandleCppCache(HttpListenerContext ctx)
    {
        var query = ctx.Request.QueryString["query"] ?? "AActor";
        var sb = new StringBuilder();
        sb.AppendLine($"# C++ Cache Probe: `{query}`");
        sb.AppendLine();

        // --- Section 1: Resolve CppGlobalSymbolCache ---
        sb.AppendLine("## 1. CppGlobalSymbolCache");
        sb.AppendLine();

        object cppGlobalCache = null;
        Type cppGlobalCacheType = null;
        try
        {
            cppGlobalCacheType = FindTypeByName("CppGlobalSymbolCache", "JetBrains.ReSharper.Psi.Cpp");
            if (cppGlobalCacheType != null)
            {
                sb.AppendLine($"Type found: `{cppGlobalCacheType.FullName}`");
                sb.AppendLine();

                // Try to resolve as a component
                cppGlobalCache = TryResolveComponent(cppGlobalCacheType);
                if (cppGlobalCache != null)
                {
                    sb.AppendLine("**Resolved as component!**");
                    sb.AppendLine();
                    DumpPublicApi(cppGlobalCache.GetType(), sb);

                    // Try to find a lookup method
                    TryInvokeSymbolLookup(cppGlobalCache, query, sb);
                }
                else
                {
                    sb.AppendLine("Could not resolve as component. Trying static instance...");

                    // Try GetInstance pattern
                    var getInstance = cppGlobalCacheType.GetMethod("GetInstance",
                        BindingFlags.Public | BindingFlags.Static);
                    if (getInstance != null)
                    {
                        sb.AppendLine($"Found static `GetInstance()`, invoking...");
                        try
                        {
                            var ps = getInstance.GetParameters();
                            if (ps.Length == 0)
                                cppGlobalCache = getInstance.Invoke(null, null);
                            else if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_solution.GetType()))
                                cppGlobalCache = getInstance.Invoke(null, new object[] { _solution });

                            if (cppGlobalCache != null)
                            {
                                sb.AppendLine("**Got instance via GetInstance!**");
                                DumpPublicApi(cppGlobalCache.GetType(), sb);
                                TryInvokeSymbolLookup(cppGlobalCache, query, sb);
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"GetInstance threw: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                        }
                    }
                    else
                    {
                        sb.AppendLine("No `GetInstance()` method found.");
                        DumpPublicApi(cppGlobalCacheType, sb);
                    }
                }
            }
            else
            {
                sb.AppendLine("Type `CppGlobalSymbolCache` not found in loaded assemblies.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        // --- Section 2: CppWordIndex ---
        sb.AppendLine("## 2. CppWordIndex");
        sb.AppendLine();

        try
        {
            var cppWordIndexType = FindTypeByName("CppWordIndex", "JetBrains.ReSharper.Feature.Services.Cpp");
            if (cppWordIndexType != null)
            {
                sb.AppendLine($"Type found: `{cppWordIndexType.FullName}`");

                var wordIndex = TryResolveComponent(cppWordIndexType);
                if (wordIndex != null)
                {
                    sb.AppendLine("**Resolved as component!**");
                    sb.AppendLine();
                    DumpPublicApi(wordIndex.GetType(), sb);

                    // Try to search the word index
                    TryWordIndexLookup(wordIndex, query, sb);
                }
                else
                {
                    sb.AppendLine("Could not resolve as component.");
                    DumpPublicApi(cppWordIndexType, sb);
                }
            }
            else
            {
                sb.AppendLine("Type `CppWordIndex` not found.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        // --- Section 3: CppGotoSymbolUtil ---
        sb.AppendLine("## 3. CppGotoSymbolUtil");
        sb.AppendLine();

        try
        {
            var gotoUtilType = FindTypeByName("CppGotoSymbolUtil", "JetBrains.ReSharper.Feature.Services.Cpp");
            if (gotoUtilType != null)
            {
                sb.AppendLine($"Type found: `{gotoUtilType.FullName}`");
                sb.AppendLine();
                DumpPublicApi(gotoUtilType, sb, includeStatic: true);
            }
            else
            {
                sb.AppendLine("Type `CppGotoSymbolUtil` not found.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        // --- Section 4: CppGotoClassMemberProvider ---
        sb.AppendLine("## 4. CppGotoClassMemberProvider");
        sb.AppendLine();

        try
        {
            var gotoProviderType = FindTypeByName("CppGotoClassMemberProvider",
                "JetBrains.ReSharper.Feature.Services.Cpp");
            if (gotoProviderType != null)
            {
                sb.AppendLine($"Type found: `{gotoProviderType.FullName}`");
                sb.AppendLine();

                var provider = TryResolveComponent(gotoProviderType);
                if (provider != null)
                {
                    sb.AppendLine("**Resolved as component!**");
                    sb.AppendLine();
                    DumpPublicApi(provider.GetType(), sb);
                }
                else
                {
                    sb.AppendLine("Could not resolve as component.");
                    DumpPublicApi(gotoProviderType, sb);
                }
            }
            else
            {
                sb.AppendLine("Type not found.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();

        // --- Section 5: CppFileSymbolTable ---
        sb.AppendLine("## 5. CppFileSymbolTable (per-file)");
        sb.AppendLine();

        try
        {
            var fstType = FindTypeByName("CppFileSymbolTable", "JetBrains.ReSharper.Psi.Cpp");
            if (fstType != null)
            {
                sb.AppendLine($"Type found: `{fstType.FullName}`");
                sb.AppendLine();
                DumpPublicApi(fstType, sb);

                // Check if CppGlobalSymbolCache has a method to get a file's symbol table
                if (cppGlobalCache != null)
                {
                    var methods = cppGlobalCache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.ReturnType == fstType ||
                                    m.Name.Contains("SymbolTable") ||
                                    m.Name.Contains("FileSymbol"))
                        .Select(m => FormatMethod(m));
                    sb.AppendLine("Methods on CppGlobalSymbolCache returning/related to file symbol tables:");
                    foreach (var m in methods)
                        sb.AppendLine($"  - {m}");
                }
            }
            else
            {
                sb.AppendLine("Type not found.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error: {ex.GetType().Name}: {ex.Message}");
        }

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    private Type FindTypeByName(string typeName, string preferredAssemblySubstring)
    {
        // Search preferred assembly first
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.FullName.Contains(preferredAssemblySubstring)) continue;
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == typeName) return t;
                }
            }
            catch { /* skip */ }
        }

        // Fallback: search all JetBrains assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!asm.FullName.Contains("JetBrains")) continue;
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == typeName) return t;
                }
            }
            catch { /* skip */ }
        }

        return null;
    }

    private object TryResolveComponent(Type componentType)
    {
        // Try solution.GetComponent<T>()
        try
        {
            var getComp = typeof(ISolution).GetMethod("GetComponent");
            if (getComp == null)
            {
                // Search extension methods
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.FullName.Contains("JetBrains")) continue;
                    try
                    {
                        foreach (var t in asm.GetExportedTypes())
                        {
                            if (!t.IsAbstract || !t.IsSealed) continue;
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if (m.Name != "GetComponent" || !m.IsGenericMethodDefinition) continue;
                                var ps = m.GetParameters();
                                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_solution.GetType()))
                                {
                                    var gm = m.MakeGenericMethod(componentType);
                                    return gm.Invoke(null, new object[] { _solution });
                                }
                            }
                        }
                    }
                    catch { /* skip */ }
                }
            }
            else
            {
                var gm = getComp.MakeGenericMethod(componentType);
                return gm.Invoke(_solution, null);
            }
        }
        catch { /* component not registered */ }

        return null;
    }

    private void DumpPublicApi(Type type, StringBuilder sb, bool includeStatic = false)
    {
        sb.AppendLine($"### Public API of `{type.Name}`");
        sb.AppendLine();

        var flags = BindingFlags.Public | BindingFlags.Instance;
        if (includeStatic) flags |= BindingFlags.Static;

        var methods = type.GetMethods(flags)
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
            .Select(m => FormatMethod(m))
            .OrderBy(m => m)
            .Take(40);

        foreach (var m in methods)
            sb.AppendLine($"- {m}");

        var props = type.GetProperties(flags)
            .Where(p => p.DeclaringType != typeof(object))
            .Select(p => $"`{p.Name}` : `{p.PropertyType.Name}`")
            .OrderBy(p => p)
            .Take(20);

        if (props.Any())
        {
            sb.AppendLine();
            sb.AppendLine("Properties:");
            foreach (var p in props)
                sb.AppendLine($"- {p}");
        }
        sb.AppendLine();
    }

    private static string FormatMethod(MethodInfo m)
    {
        var parms = string.Join(", ", m.GetParameters()
            .Select(p => $"{p.ParameterType.Name} {p.Name}"));
        var staticTag = m.IsStatic ? "static " : "";
        return $"`{staticTag}{m.ReturnType.Name} {m.Name}({parms})`";
    }

    private void TryInvokeSymbolLookup(object cache, string query, StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine($"### Attempting symbol lookup for `{query}`");
        sb.AppendLine();

        var cacheType = cache.GetType();

        // Try various method name patterns
        var methodNames = new[]
        {
            "GetSymbolsByName", "FindSymbolsByName", "GetSymbol", "FindSymbol",
            "GetSymbolsByShortName", "LookupSymbol", "GetDeclaredElements",
            "FindDeclaredElements", "GetElements", "Find", "Lookup",
            "GetDeclarationsByName", "GetNamedElements", "GetByName"
        };

        foreach (var methodName in methodNames)
        {
            var methods = cacheType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            foreach (var method in methods)
            {
                sb.AppendLine($"Trying `{FormatMethod(method)}`...");
                try
                {
                    var ps = method.GetParameters();
                    object result = null;

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        result = method.Invoke(cache, new object[] { query });
                    else
                        continue;

                    if (result == null)
                    {
                        sb.AppendLine("  Result: null");
                        continue;
                    }

                    // Try to enumerate the result
                    if (result is System.Collections.IEnumerable enumerable)
                    {
                        var items = new List<string>();
                        foreach (var item in enumerable)
                        {
                            if (items.Count >= 10) break;
                            items.Add($"`{item}` ({item.GetType().Name})");
                        }
                        sb.AppendLine($"  Result: {items.Count} items");
                        foreach (var item in items)
                            sb.AppendLine($"    - {item}");
                    }
                    else
                    {
                        sb.AppendLine($"  Result: `{result}` ({result.GetType().Name})");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    sb.AppendLine($"  Threw: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        // Also try: enumerate all methods that take a string parameter and return something
        sb.AppendLine();
        sb.AppendLine("### All methods taking a string parameter:");
        sb.AppendLine();
        var stringMethods = cacheType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName &&
                        m.GetParameters().Length >= 1 &&
                        m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .Select(m => FormatMethod(m))
            .OrderBy(m => m)
            .Take(20);
        foreach (var m in stringMethods)
            sb.AppendLine($"- {m}");
    }

    private void TryWordIndexLookup(object wordIndex, string query, StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine($"### Word Index lookup for `{query}`");
        sb.AppendLine();

        var indexType = wordIndex.GetType();

        // Try HasWord, GetFilesContainingWord, etc.
        var methodNames = new[]
        {
            "HasWord", "GetFilesContainingWord", "GetFiles",
            "FindWord", "Search", "GetSourceFiles", "ContainsWord"
        };

        foreach (var methodName in methodNames)
        {
            var methods = indexType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            foreach (var method in methods)
            {
                sb.AppendLine($"Trying `{FormatMethod(method)}`...");
                try
                {
                    var ps = method.GetParameters();
                    object result = null;

                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        result = method.Invoke(wordIndex, new object[] { query });
                    else
                        continue;

                    if (result == null)
                    {
                        sb.AppendLine("  Result: null");
                        continue;
                    }

                    if (result is bool b)
                    {
                        sb.AppendLine($"  Result: **{b}**");
                    }
                    else if (result is System.Collections.IEnumerable enumerable)
                    {
                        var items = new List<string>();
                        foreach (var item in enumerable)
                        {
                            if (items.Count >= 10) break;
                            if (item is IPsiSourceFile sf)
                                items.Add($"`{TryMakeRelative(sf.GetLocation().FullPath)}`");
                            else
                                items.Add($"`{item}` ({item.GetType().Name})");
                        }
                        sb.AppendLine($"  Result: {items.Count}+ items");
                        foreach (var item in items)
                            sb.AppendLine($"    - {item}");
                    }
                    else
                    {
                        sb.AppendLine($"  Result: `{result}` ({result.GetType().Name})");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    sb.AppendLine($"  Threw: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }
    }

    // --- Shared helpers ---

    /// <summary>
    /// Searches all PSI modules for the first element matching the query by short name.
    /// Prefers elements that have at least one declaration.
    /// </summary>
    private IDeclaredElement FindFirstElement(IPsiServices psiServices, string query, out string moduleName)
    {
        moduleName = null;
        IDeclaredElement fallback = null;
        string fallbackModule = null;

        foreach (var module in psiServices.Modules.GetModules())
        {
            try
            {
                var symbolScope = psiServices.Symbols.GetSymbolScope(
                    module, caseSensitive: true, withReferences: true);
                var elements = symbolScope.GetElementsByShortName(query);
                if (elements == null || !elements.Any()) continue;

                // Prefer elements with declarations
                foreach (var elem in elements)
                {
                    try
                    {
                        var decls = elem.GetDeclarations();
                        if (decls.Count > 0)
                        {
                            moduleName = module.DisplayName;
                            return elem;
                        }
                    }
                    catch { /* skip elements whose declarations throw */ }
                }

                // Keep first found as fallback
                if (fallback == null)
                {
                    fallback = elements.First();
                    fallbackModule = module.DisplayName;
                }
            }
            catch { /* skip modules that throw */ }
        }

        moduleName = fallbackModule;
        return fallback;
    }

    private static string GetElementKind(IDeclaredElement element)
    {
        if (element is ITypeElement)
        {
            if (element is IClass) return "class";
            if (element is IStruct) return "struct";
            if (element is IInterface) return "interface";
            if (element is IEnum) return "enum";
            if (element is IDelegate) return "delegate";
            return "type";
        }
        if (element is IFunction) return "function";
        if (element is IProperty) return "property";
        if (element is IField) return "field";
        if (element is IEvent) return "event";
        if (element is INamespace) return "namespace";
        if (element is ITypeParameter) return "type_parameter";

        // For C++ elements that don't implement standard interfaces,
        // try to infer from the CLR type name
        var typeName = element.GetType().Name;
        if (typeName.Contains("Class") || typeName.Contains("Struct")) return "type (C++ inferred)";
        if (typeName.Contains("Function") || typeName.Contains("Method")) return "function (C++ inferred)";
        if (typeName.Contains("Field") || typeName.Contains("Variable")) return "field (C++ inferred)";
        if (typeName.Contains("Enum")) return "enum (C++ inferred)";
        if (typeName.Contains("Namespace")) return "namespace (C++ inferred)";

        return $"unknown ({typeName})";
    }

    private static string TryGetContainingTypeName(IDeclaredElement element)
    {
        if (element is ITypeMember member)
            return member.GetContainingType()?.ShortName;

        // Try reflection for C++ elements
        try
        {
            var prop = element.GetType().GetProperty("ContainingType",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null)
            {
                var container = prop.GetValue(element);
                if (container is IDeclaredElement containerElem)
                    return containerElem.ShortName;
            }
        }
        catch { /* skip */ }

        return null;
    }

    private int GetLineNumber(IPsiSourceFile sourceFile, IDeclaration decl)
    {
        try
        {
            var doc = sourceFile?.Document;
            if (doc == null) return 0;

            var nameRange = decl.GetNameRange();
            var offset = nameRange.StartOffset.Offset;
            if (offset >= 0 && offset <= doc.GetTextLength())
                return (int)new DocumentOffset(doc, offset).ToDocumentCoords().Line + 1;
        }
        catch { /* ignore */ }
        return 0;
    }

    private string TryMakeRelative(string fullPath)
    {
        var solutionDir = _solution.SolutionDirectory.FullPath;
        if (fullPath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = fullPath.Substring(solutionDir.Length).TrimStart('\\', '/');
            return rel.Replace('\\', '/');
        }
        return fullPath;
    }

    private static string TryGetModulePath(IPsiModule module)
    {
        try
        {
            var project = module.ContainingProjectModule as IProject;
            return project?.ProjectFileLocation?.FullPath;
        }
        catch { return null; }
    }

    private static string TryGetContextLine(IDocument document, int lineNumber)
    {
        if (document == null || lineNumber <= 0) return "";
        try
        {
            var text = document.GetText();
            var lines = text.Split('\n');
            if (lineNumber <= lines.Length)
                return lines[lineNumber - 1].TrimEnd('\r');
        }
        catch { /* ignore */ }
        return "";
    }

    private static string Truncate(string s, int maxLength)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s;
        return s.Substring(0, maxLength) + "...";
    }

    /// <summary>Reference equality comparer for deduplicating IDeclaredElement instances.</summary>
    private class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new();
        public bool Equals(T x, T y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
