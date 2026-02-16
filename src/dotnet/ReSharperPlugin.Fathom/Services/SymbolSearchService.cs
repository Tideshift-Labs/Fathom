using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Services;

public class SymbolSearchService
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<SymbolSearchService>();

    private readonly ISolution _solution;

    // Lazy init state: 0 = not tried, 1 = resolved, -1 = failed
    private int _initState;
    private object _symbolNameCache;
    private MethodInfo _getSymbolsByShortName;

    // Lazily cached per-symbol-type reflection handles
    private PropertyInfo _nameProp;
    private PropertyInfo _containingFileProp;
    private PropertyInfo _parentProp;
    private MethodInfo _locateDocumentRange;
    private PropertyInfo _locationProp;
    private PropertyInfo _fullPathProp;

    public SymbolSearchService(ISolution solution)
    {
        _solution = solution;
    }

    private bool EnsureInitialized()
    {
        var state = Volatile.Read(ref _initState);
        if (state == 1) return true;
        if (state == -1) return false;

        // Try to initialize (only one thread wins the race, others see the result)
        if (Interlocked.CompareExchange(ref _initState, -1, 0) != 0)
        {
            // Another thread got here first, read their result
            return Volatile.Read(ref _initState) == 1;
        }

        try
        {
            // Step 1: Find and resolve CppGlobalSymbolCache
            var cppGlobalCacheType = FindTypeByName("CppGlobalSymbolCache", "JetBrains.ReSharper.Psi.Cpp");
            if (cppGlobalCacheType == null)
            {
                Log.Verbose("SymbolSearchService: CppGlobalSymbolCache type not found, C++ symbols unavailable");
                return false;
            }

            var cppGlobalCache = ResolveComponent(cppGlobalCacheType);
            if (cppGlobalCache == null)
            {
                Log.Verbose("SymbolSearchService: could not resolve CppGlobalSymbolCache component");
                // Reset to 0 so we retry next request (cache may not be ready yet)
                Volatile.Write(ref _initState, 0);
                return false;
            }

            // Step 2: Get CppSymbolNameCache
            var symbolNameCacheType = FindTypeByName("CppSymbolNameCache", "JetBrains.ReSharper.Psi.Cpp")
                                      ?? FindTypeByName("CppSymbolNameCache", "JetBrains.ReSharper.Feature.Services.Cpp");

            object symbolNameCache = null;
            if (symbolNameCacheType != null)
                symbolNameCache = ResolveComponent(symbolNameCacheType);

            // Fallback: search CppGlobalSymbolCache properties for SymbolNameCache
            if (symbolNameCache == null)
            {
                foreach (var prop in cppGlobalCache.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if ((symbolNameCacheType != null && symbolNameCacheType.IsAssignableFrom(prop.PropertyType)) ||
                        prop.PropertyType.Name.Contains("SymbolNameCache"))
                    {
                        try
                        {
                            symbolNameCache = prop.GetValue(cppGlobalCache);
                            if (symbolNameCache != null) break;
                        }
                        catch { /* skip */ }
                    }
                }

                // Also try parameterless methods
                if (symbolNameCache == null)
                {
                    foreach (var m in cppGlobalCache.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.GetParameters().Length != 0) continue;
                        if ((symbolNameCacheType != null && !symbolNameCacheType.IsAssignableFrom(m.ReturnType)) &&
                            !m.ReturnType.Name.Contains("SymbolNameCache")) continue;
                        try
                        {
                            symbolNameCache = m.Invoke(cppGlobalCache, null);
                            if (symbolNameCache != null) break;
                        }
                        catch { /* skip */ }
                    }
                }
            }

            if (symbolNameCache == null)
            {
                Log.Verbose("SymbolSearchService: could not resolve CppSymbolNameCache");
                // Reset to 0 so we retry (might not be ready)
                Volatile.Write(ref _initState, 0);
                return false;
            }

            // Step 3: Cache GetSymbolsByShortName method
            var method = symbolNameCache.GetType().GetMethod("GetSymbolsByShortName",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);

            if (method == null)
            {
                Log.Verbose("SymbolSearchService: GetSymbolsByShortName method not found on CppSymbolNameCache");
                return false; // Permanent failure, leave _initState as -1
            }

            _symbolNameCache = symbolNameCache;
            _getSymbolsByShortName = method;
            Volatile.Write(ref _initState, 1);
            Log.Verbose("SymbolSearchService: initialized, C++ symbol lookup available");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("SymbolSearchService: initialization failed: " + ex.Message);
            // Reset to 0 so we retry on transient failures
            Volatile.Write(ref _initState, 0);
            return false;
        }
    }

    public SymbolSearchResponse SearchByName(string query, string kind, string scope, int limit)
    {
        var response = new SymbolSearchResponse
        {
            Query = query,
            Results = new List<SymbolResult>(),
            TotalMatches = 0,
            Truncated = false
        };

        if (!EnsureInitialized() || string.IsNullOrWhiteSpace(query))
            return response;

        var solutionDir = _solution.SolutionDirectory?.FullPath;

        ReadLockCookie.Execute(() =>
        {
            try
            {
                var rawSymbols = _getSymbolsByShortName.Invoke(_symbolNameCache, new object[] { query });
                var symbols = EnumerateCollection(rawSymbols);
                if (symbols == null || symbols.Count == 0)
                    return;

                var seen = new HashSet<string>();
                var results = new List<(SymbolResult result, int sortOrder)>();

                foreach (var sym in symbols)
                {
                    var info = ExtractSymbolInfo(sym);
                    if (info.name == null) continue;

                    var mappedKind = MapKind(info.typeName, sym);

                    // Kind filter
                    if (!string.IsNullOrEmpty(kind) && !kind.Equals("all", StringComparison.OrdinalIgnoreCase)
                        && !mappedKind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Scope filter: "all" (default) = include everything; "user" = only files under solution directory
                    if (scope != null && scope.Equals("user", StringComparison.OrdinalIgnoreCase))
                    {
                        if (solutionDir != null && info.file != null &&
                            !info.file.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Deduplicate by (name, file, line)
                    var dedupeKey = $"{info.name}|{info.file}|{info.line}";
                    if (!seen.Add(dedupeKey))
                        continue;

                    var sortOrder = mappedKind == "forward_declaration" ? 1 : 0;
                    results.Add((new SymbolResult
                    {
                        Name = info.name,
                        Kind = mappedKind,
                        File = info.file,
                        Line = info.line,
                        SymbolType = info.typeName
                    }, sortOrder));
                }

                // Sort: definitions first, forward declarations last
                results.Sort((a, b) => a.sortOrder.CompareTo(b.sortOrder));

                response.TotalMatches = results.Count;
                response.Truncated = results.Count > limit;
                response.Results = results.Take(limit).Select(r => r.result).ToList();
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log.Warn($"SymbolSearchService.SearchByName failed for '{query}': {inner.GetType().Name}: {inner.Message}");
            }
        });

        return response;
    }

    public DeclarationResponse GetDeclaration(string symbol, string containingType, string kind, int contextLines)
    {
        var response = new DeclarationResponse
        {
            Symbol = symbol,
            Declarations = new List<SymbolDeclaration>(),
            ForwardDeclarations = 0
        };

        if (!EnsureInitialized() || string.IsNullOrWhiteSpace(symbol))
            return response;

        ReadLockCookie.Execute(() =>
        {
            try
            {
                var rawSymbols = _getSymbolsByShortName.Invoke(_symbolNameCache, new object[] { symbol });
                var symbols = EnumerateCollection(rawSymbols);
                if (symbols == null || symbols.Count == 0)
                    return;

                var seen = new HashSet<string>();

                foreach (var sym in symbols)
                {
                    var info = ExtractSymbolInfo(sym);
                    if (info.name == null) continue;

                    var mappedKind = MapKind(info.typeName, sym);

                    // Count forward declarations but don't include them
                    if (mappedKind == "forward_declaration")
                    {
                        response.ForwardDeclarations++;
                        continue;
                    }

                    // Kind filter
                    if (!string.IsNullOrEmpty(kind) && !kind.Equals("all", StringComparison.OrdinalIgnoreCase)
                        && !mappedKind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // ContainingType filter
                    if (!string.IsNullOrEmpty(containingType))
                    {
                        var parentName = ExtractParentName(sym);
                        if (parentName == null || !parentName.Equals(containingType, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Deduplicate
                    var dedupeKey = $"{info.name}|{info.file}|{info.line}";
                    if (!seen.Add(dedupeKey))
                        continue;

                    var snippet = info.file != null && info.line > 0
                        ? ReadSnippet(info.file, info.line, contextLines)
                        : null;

                    var parentForResult = ExtractParentName(sym);

                    response.Declarations.Add(new SymbolDeclaration
                    {
                        Name = info.name,
                        Kind = mappedKind,
                        File = info.file,
                        Line = info.line,
                        ContainingType = parentForResult,
                        Snippet = snippet
                    });
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log.Warn($"SymbolSearchService.GetDeclaration failed for '{symbol}': {inner.GetType().Name}: {inner.Message}");
            }
        });

        return response;
    }

    private (string name, string typeName, string file, int line) ExtractSymbolInfo(object symbol)
    {
        string name = null;
        var typeName = symbol.GetType().Name;
        string file = null;
        var line = 0;

        // Extract name via Name property (CppQualifiedName -> ToString())
        try
        {
            var nameProp = _nameProp ?? symbol.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProp != null)
            {
                Interlocked.CompareExchange(ref _nameProp, nameProp, null);
                var nameObj = nameProp.GetValue(symbol);
                if (nameObj != null)
                {
                    // Try ShortName property first for cleaner output
                    var shortNameProp = nameObj.GetType().GetProperty("ShortName");
                    if (shortNameProp != null)
                        name = shortNameProp.GetValue(nameObj) as string;

                    if (name == null)
                    {
                        var nameStr = nameObj.ToString();
                        if (!string.IsNullOrEmpty(nameStr) && nameStr != nameObj.GetType().Name)
                            name = nameStr;
                    }
                }
            }
        }
        catch { /* skip */ }

        // Fallback: ShortName property directly on the symbol
        if (name == null)
        {
            try
            {
                var prop = symbol.GetType().GetProperty("ShortName", BindingFlags.Public | BindingFlags.Instance);
                name = prop?.GetValue(symbol) as string;
            }
            catch { /* skip */ }
        }

        // Extract file from ContainingFile property
        try
        {
            var cfProp = _containingFileProp ?? symbol.GetType().GetProperty("ContainingFile", BindingFlags.Public | BindingFlags.Instance);
            if (cfProp != null)
            {
                Interlocked.CompareExchange(ref _containingFileProp, cfProp, null);
                var fileLocation = cfProp.GetValue(symbol);
                if (fileLocation != null)
                    file = ExtractPathFromFileLocation(fileLocation);
            }
        }
        catch { /* skip */ }

        // Extract line from LocateDocumentRange(ISolution)
        try
        {
            var locMethod = _locateDocumentRange ?? symbol.GetType().GetMethod("LocateDocumentRange", BindingFlags.Public | BindingFlags.Instance);
            if (locMethod != null)
            {
                Interlocked.CompareExchange(ref _locateDocumentRange, locMethod, null);
                var ps = locMethod.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_solution.GetType()))
                {
                    var docRange = locMethod.Invoke(symbol, new object[] { _solution });
                    if (docRange is DocumentRange range && range.IsValid())
                    {
                        var doc = range.Document;
                        if (doc != null)
                        {
                            if (file == null)
                            {
                                try
                                {
                                    var monikerProp = doc.GetType().GetProperty("Moniker");
                                    var moniker = monikerProp?.GetValue(doc) as string;
                                    if (!string.IsNullOrEmpty(moniker))
                                        file = moniker;
                                }
                                catch { /* skip */ }
                            }

                            var startOffset = range.StartOffset;
                            if (startOffset.Offset >= 0 && startOffset.Offset <= doc.GetTextLength())
                            {
                                line = (int)startOffset.ToDocumentCoords().Line + 1; // 0-based, add 1
                            }
                        }
                    }
                }
            }
        }
        catch { /* skip */ }

        return (name, typeName, file, line);
    }

    private string ExtractPathFromFileLocation(object fileLocation)
    {
        var locType = fileLocation.GetType();

        // Try .Location property -> .FullPath
        try
        {
            var locProp = _locationProp ?? locType.GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
            if (locProp != null)
            {
                Interlocked.CompareExchange(ref _locationProp, locProp, null);
                var loc = locProp.GetValue(fileLocation);
                if (loc != null)
                {
                    var fpProp = _fullPathProp ?? loc.GetType().GetProperty("FullPath");
                    if (fpProp != null)
                    {
                        Interlocked.CompareExchange(ref _fullPathProp, fpProp, null);
                        var path = fpProp.GetValue(loc) as string;
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                    var str = loc.ToString();
                    if (!string.IsNullOrEmpty(str) && str != loc.GetType().Name)
                        return str;
                }
            }
        }
        catch { /* skip */ }

        // Try .SourceFile (IPsiSourceFile)
        try
        {
            var sfProp = locType.GetProperty("SourceFile", BindingFlags.Public | BindingFlags.Instance);
            if (sfProp != null)
            {
                var sf = sfProp.GetValue(fileLocation);
                if (sf is IPsiSourceFile psiSf)
                    return psiSf.GetLocation().FullPath;
            }
        }
        catch { /* skip */ }

        // Try .File -> .FullPath
        try
        {
            var fileProp = locType.GetProperty("File", BindingFlags.Public | BindingFlags.Instance);
            if (fileProp != null)
            {
                var f = fileProp.GetValue(fileLocation);
                if (f != null)
                {
                    var fpProp = f.GetType().GetProperty("FullPath");
                    if (fpProp != null)
                    {
                        var path = fpProp.GetValue(f) as string;
                        if (!string.IsNullOrEmpty(path))
                            return path;
                    }
                }
            }
        }
        catch { /* skip */ }

        // Fallback: ToString
        var fallback = fileLocation.ToString();
        if (!string.IsNullOrEmpty(fallback) && fallback != fileLocation.GetType().Name)
            return fallback;

        return null;
    }

    private string ExtractParentName(object symbol)
    {
        try
        {
            var parentProp = _parentProp ?? symbol.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
            if (parentProp != null)
            {
                Interlocked.CompareExchange(ref _parentProp, parentProp, null);
                var parent = parentProp.GetValue(symbol);
                if (parent != null)
                {
                    var nameProp = parent.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        var nameObj = nameProp.GetValue(parent);
                        if (nameObj != null)
                        {
                            var nameStr = nameObj.ToString();
                            if (!string.IsNullOrEmpty(nameStr) && nameStr != nameObj.GetType().Name)
                                return nameStr;
                        }
                    }
                }
            }
        }
        catch { /* skip */ }

        return null;
    }

    /// <summary>
    /// Enumerates items from a collection that may not implement IEnumerable
    /// (e.g. JetBrains CppList). Tries IEnumerable first, then Count + indexer via reflection.
    /// </summary>
    private static List<object> EnumerateCollection(object collection)
    {
        if (collection == null) return null;

        // Strategy 1: standard IEnumerable
        if (collection is System.Collections.IEnumerable enumerable)
        {
            var items = new List<object>();
            foreach (var item in enumerable)
                items.Add(item);
            return items;
        }

        // Strategy 2: Count + indexer (CppList<T> pattern)
        var collType = collection.GetType();
        var countProp = collType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProp != null && countProp.PropertyType == typeof(int))
        {
            // Find indexer: get_Item(int)
            var indexerMethod = collType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int) }, null);
            if (indexerMethod != null)
            {
                var count = (int)countProp.GetValue(collection);
                var items = new List<object>(count);
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        items.Add(indexerMethod.Invoke(collection, new object[] { i }));
                    }
                    catch { /* skip bad entries */ }
                }
                return items;
            }
        }

        // Strategy 3: GetEnumerator() duck typing
        var getEnumerator = collType.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        if (getEnumerator != null)
        {
            try
            {
                var enumerator = getEnumerator.Invoke(collection, null);
                if (enumerator != null)
                {
                    var enumType = enumerator.GetType();
                    var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);

                    if (moveNext != null && currentProp != null)
                    {
                        var items = new List<object>();
                        while ((bool)moveNext.Invoke(enumerator, null))
                            items.Add(currentProp.GetValue(enumerator));
                        return items;
                    }
                }
            }
            catch { /* skip */ }
        }

        Log.Verbose($"SymbolSearchService: cannot enumerate {collType.Name}, no IEnumerable/Count/GetEnumerator found");
        return null;
    }

    private static string MapKind(string typeName, object symbol)
    {
        return typeName switch
        {
            "CppClassSymbol" => "class",
            "CppFwdClassSymbol" => "forward_declaration",
            "CppFunctionSymbol" => "function",
            "CppSimpleDeclaratorSymbol" => IsFunction(symbol) ? "function" : "variable",
            "CppNamespaceSymbol" => "namespace",
            "CppEnumSymbol" => "enum",
            _ => "symbol"
        };
    }

    private static MethodInfo _isFunctionMethod;

    private static bool IsFunction(object symbol)
    {
        try
        {
            var method = _isFunctionMethod ?? symbol.GetType().GetMethod("IsFunction",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(bool))
            {
                Interlocked.CompareExchange(ref _isFunctionMethod, method, null);
                return (bool)method.Invoke(symbol, null);
            }
        }
        catch { /* skip */ }
        return false;
    }

    private static string ReadSnippet(string filePath, int centerLine, int contextLines)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            var lines = File.ReadAllLines(filePath);
            var startLine = Math.Max(0, centerLine - 1 - contextLines);
            var endLine = Math.Min(lines.Length - 1, centerLine - 1 + contextLines);

            var snippetLines = new List<string>();
            for (var i = startLine; i <= endLine; i++)
                snippetLines.Add(lines[i]);

            return string.Join("\n", snippetLines);
        }
        catch (Exception ex)
        {
            Log.Verbose($"ReadSnippet failed for {filePath}:{centerLine}: {ex.Message}");
            return null;
        }
    }

    private Type FindTypeByName(string typeName, string preferredAssemblySubstring)
    {
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

    private object ResolveComponent(Type componentType)
    {
        MethodInfo getComponentMethod = null;
        foreach (var iface in _solution.GetType().GetInterfaces())
        {
            getComponentMethod = iface.GetMethod("GetComponent",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                break;
            getComponentMethod = null;
        }

        if (getComponentMethod == null)
        {
            for (var type = _solution.GetType(); type != null; type = type.BaseType)
            {
                getComponentMethod = type.GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                    break;
                getComponentMethod = null;
            }
        }

        if (getComponentMethod != null)
        {
            try
            {
                var gm = getComponentMethod.MakeGenericMethod(componentType);
                return gm.Invoke(_solution, null);
            }
            catch { /* component not registered */ }
        }

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
                            try
                            {
                                var gm = m.MakeGenericMethod(componentType);
                                return gm.Invoke(null, new object[] { _solution });
                            }
                            catch { /* skip */ }
                        }
                    }
                }
            }
            catch { /* skip */ }
        }

        return null;
    }
}
