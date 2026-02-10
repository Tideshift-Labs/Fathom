using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using ReSharperPlugin.CoRider.Models;
using MemberInfo = ReSharperPlugin.CoRider.Models.MemberInfo;
using TypeInfo = ReSharperPlugin.CoRider.Models.TypeInfo;

namespace ReSharperPlugin.CoRider.Services;

/// <summary>
/// Walks C++ PSI trees using tree node type names for categorization.
///
/// C++ PSI nodes (ClassSpecifier, Declarator, etc.) live in JetBrains.ReSharper.Psi.Cpp.Tree
/// and their declared elements (CppResolveEntityDeclaredElement) implement ICppDeclaredElement
/// but NOT generic PSI interfaces like ITypeElement or IFunction. We therefore categorize by
/// tree node type name (ClassSpecifier = type, Declarator = member/function) and use reflection
/// to extract type information from C++-specific interfaces.
/// </summary>
public static class CppStructureWalker
{
    public static void Walk(IFile cppFile, IPsiSourceFile sourceFile, FileStructure result,
        List<string> debugDiagnostics)
    {
        result.Language = "C++";

        // Phase 1: Categorize tree nodes by their runtime type name
        var classSpecifiers = new List<KeyValuePair<ITreeNode, IDeclaredElement>>();
        var declarators = new List<KeyValuePair<ITreeNode, IDeclaredElement>>();
        var importNodes = new List<ITreeNode>();

        foreach (var node in cppFile.Descendants())
        {
            var typeName = node.GetType().Name;

            switch (typeName)
            {
                case "ImportDirective":
                    importNodes.Add(node);
                    break;

                case "ClassSpecifier":
                {
                    var element = TryGetDeclaredElement(node);
                    if (element != null)
                        classSpecifiers.Add(
                            new KeyValuePair<ITreeNode, IDeclaredElement>(node, element));
                    break;
                }

                case "FwdClassSpecifier":
                    break; // Skip forward declarations

                case "Declarator":
                case "InitDeclarator":
                {
                    var element = TryGetDeclaredElement(node);
                    if (element != null && element.ShortName != "<anonymous>")
                        declarators.Add(
                            new KeyValuePair<ITreeNode, IDeclaredElement>(node, element));
                    break;
                }
            }
        }

        if (debugDiagnostics != null)
            debugDiagnostics.Add("Phase1: " + classSpecifiers.Count + " classes, " +
                declarators.Count + " declarators, " + importNodes.Count + " imports");

        // Phase 2: Build TypeInfo from ClassSpecifier nodes
        var typeNodeMap = new Dictionary<ITreeNode, TypeInfo>();
        var nsMap = new Dictionary<string, NamespaceInfo>(StringComparer.Ordinal);
        var globalTypes = new List<TypeInfo>();

        foreach (var kvp in classSpecifiers)
        {
            var node = kvp.Key;
            var element = kvp.Value;

            var typeInfo = new TypeInfo
            {
                Name = element.ShortName,
                Line = GetLine(node, sourceFile),
                Kind = DetermineTypeKind(node),
            };

            TryExtractBaseTypes(node, typeInfo);
            TryExtractAccess(node, element, typeInfo);
            typeInfo.Annotations = TryExtractUeAnnotations(node);

            typeNodeMap[node] = typeInfo;

            var nsName = GetNamespaceQualifiedName(element);
            if (string.IsNullOrEmpty(nsName))
            {
                globalTypes.Add(typeInfo);
            }
            else
            {
                if (!nsMap.TryGetValue(nsName, out var nsInfo))
                {
                    nsInfo = new NamespaceInfo { Name = nsName, Types = new List<TypeInfo>() };
                    nsMap[nsName] = nsInfo;
                }
                nsInfo.Types.Add(typeInfo);
            }
        }

        // Phase 3: Assign declarators as members of their containing ClassSpecifier,
        // or as free functions/globals if outside any class.
        var globalFunctions = new List<MemberInfo>();

        foreach (var kvp in declarators)
        {
            var node = kvp.Key;
            var element = kvp.Value;

            // Skip parameter declarators (nested inside another Declarator)
            if (IsParameterDeclarator(node)) continue;

            // Skip local variables (nested inside a compound statement / function body)
            if (IsLocalDeclarator(node)) continue;

            var containingClassNode = FindAncestorByTypeName(node, "ClassSpecifier");

            if (containingClassNode != null &&
                typeNodeMap.TryGetValue(containingClassNode, out var parentType))
            {
                // Member of a class/struct
                var memberInfo = BuildMemberInfo(node, element, parentType.Name, sourceFile);

                parentType.Members ??= new List<MemberInfo>();
                parentType.Members.Add(memberInfo);
            }
            else
            {
                // Free function or global variable
                var memberInfo = BuildMemberInfo(node, element, null, sourceFile);
                if (memberInfo.Kind == "method")
                    memberInfo.Kind = "function";

                // Qualify out-of-line method definitions (e.g. OnPossess -> AFEPlayerController::OnPossess)
                var containingTypeName = TryGetContainingTypeName(node, element);
                if (containingTypeName != null)
                {
                    memberInfo.ContainingType = containingTypeName;
                    memberInfo.Name = containingTypeName + "::" + element.ShortName;
                }

                var funcNs = GetNamespaceQualifiedName(element);
                if (!string.IsNullOrEmpty(funcNs))
                {
                    if (!nsMap.TryGetValue(funcNs, out var nsInfo))
                    {
                        nsInfo = new NamespaceInfo { Name = funcNs };
                        nsMap[funcNs] = nsInfo;
                    }
                    nsInfo.FreeFunctions ??= new List<MemberInfo>();
                    nsInfo.FreeFunctions.Add(memberInfo);
                }
                else
                {
                    globalFunctions.Add(memberInfo);
                }
            }
        }

        // Phase 4: Extract includes from ImportDirective nodes
        ExtractIncludes(importNodes, result, debugDiagnostics);

        // Assemble result
        if (nsMap.Count > 0)
            result.Namespaces = nsMap.Values.ToList();
        if (globalTypes.Count > 0)
            result.Types = globalTypes;
        if (globalFunctions.Count > 0)
            result.FreeFunctions = globalFunctions;
    }

    // ── Helpers: element extraction ─────────────────────────────────────

    private static IDeclaredElement TryGetDeclaredElement(ITreeNode node)
    {
        try
        {
            var prop = node.GetType().GetProperty("DeclaredElement",
                BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return null;
            return prop.GetValue(node) as IDeclaredElement;
        }
        catch
        {
            return null;
        }
    }

    // ── Helpers: tree structure ──────────────────────────────────────────

    /// <summary>
    /// A Declarator is a parameter if it is nested inside another Declarator
    /// (i.e. inside a function's parameter list) before reaching a ClassSpecifier or file root.
    /// </summary>
    private static bool IsParameterDeclarator(ITreeNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            var name = parent.GetType().Name;
            if (name == "ClassSpecifier" || name == "CppFile" || name == "TranslationUnit")
                return false;
            if (name == "Declarator" || name == "InitDeclarator")
                return true;
            parent = parent.Parent;
        }
        return false;
    }

    /// <summary>
    /// A Declarator is local if it is nested inside a compound statement (function body,
    /// loop, lambda, or block scope). Top-level function/global declarations never have
    /// a compound statement ancestor.
    /// </summary>
    private static bool IsLocalDeclarator(ITreeNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            var name = parent.GetType().Name;
            if (name == "CppFile" || name == "TranslationUnit")
                return false;
            if (name is "CompoundStatement" or "CppChameleonCompoundStatement")
                return true;
            // Range-based for and if-init declarators sit directly under their
            // statement node, outside any CompoundStatement, but are still local.
            if (name is "RangeBasedForStatement" or "ForStatement"
                or "IfStatement" or "LambdaDeclarator")
                return true;
            parent = parent.Parent;
        }
        return false;
    }

    private static ITreeNode FindAncestorByTypeName(ITreeNode node, string ancestorTypeName)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            if (parent.GetType().Name == ancestorTypeName)
                return parent;
            parent = parent.Parent;
        }
        return null;
    }

    // ── Helpers: type info ──────────────────────────────────────────────

    /// <summary>
    /// Determine class vs struct vs union from the ClassSpecifier node text.
    /// </summary>
    private static string DetermineTypeKind(ITreeNode classSpecifierNode)
    {
        try
        {
            var text = classSpecifierNode.GetText();
            if (text != null)
            {
                var trimmed = text.TrimStart();
                if (trimmed.StartsWith("struct")) return "struct";
                if (trimmed.StartsWith("union")) return "union";
            }
        }
        catch { }
        return "class";
    }

    /// <summary>
    /// Extract base types from the class header text (before the first '{').
    /// Looks for ": public BaseClass" pattern.
    /// </summary>
    private static void TryExtractBaseTypes(ITreeNode classNode, TypeInfo info)
    {
        try
        {
            var text = classNode.GetText();
            if (text == null) return;

            var braceIdx = text.IndexOf('{');
            if (braceIdx < 0) return;

            var header = text.Substring(0, braceIdx);

            // Find inheritance colon (not part of ::)
            var colonIdx = -1;
            for (var i = 0; i < header.Length; i++)
            {
                if (header[i] != ':') continue;
                if (i > 0 && header[i - 1] == ':') continue;
                if (i < header.Length - 1 && header[i + 1] == ':') continue;
                colonIdx = i;
                break;
            }
            if (colonIdx < 0) return;

            var baseClause = header.Substring(colonIdx + 1).Trim();
            var parts = baseClause.Split(',');

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                // Remove access specifier prefix
                foreach (var prefix in new[] { "public ", "protected ", "private ", "virtual " })
                {
                    while (trimmed.StartsWith(prefix))
                        trimmed = trimmed.Substring(prefix.Length).Trim();
                }

                if (string.IsNullOrEmpty(trimmed)) continue;

                if (info.BaseType == null)
                    info.BaseType = trimmed;
                else
                {
                    info.Interfaces ??= new List<string>();
                    info.Interfaces.Add(trimmed);
                }
            }
        }
        catch { }
    }

    // ── Helpers: member info ────────────────────────────────────────────

    /// <summary>
    /// Build member info from a Declarator node. Uses node text to determine
    /// whether this is a method (contains '(') or a field.
    /// </summary>
    private static MemberInfo BuildMemberInfo(ITreeNode node, IDeclaredElement element,
        string containingTypeName, IPsiSourceFile sourceFile)
    {
        var isFunction = NodeTextContainsParenthesis(node);

        var info = new MemberInfo
        {
            Name = element.ShortName,
            Line = GetLine(node, sourceFile),
        };

        if (isFunction)
        {
            // Constructor / destructor detection
            if (containingTypeName != null && element.ShortName == containingTypeName)
                info.Kind = "constructor";
            else if (element.ShortName.StartsWith("~"))
                info.Kind = "destructor";
            else
                info.Kind = "method";

            TryExtractReturnType(element, node, info);
            ExtractParametersFromTree(node, sourceFile, info);
        }
        else
        {
            info.Kind = "field";
            TryExtractFieldType(element, node, info);
        }

        TryExtractAccess(node, element, info);
        TryExtractModifiers(element, info);
        info.Annotations = TryExtractUeAnnotations(node);

        return info;
    }

    /// <summary>
    /// Check if the Declarator node text contains '(' indicating a function signature.
    /// </summary>
    private static bool NodeTextContainsParenthesis(ITreeNode node)
    {
        try
        {
            var text = node.GetText();
            if (text != null && text.IndexOf('(') >= 0)
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Extract parameters by finding nested Declarator children of a function Declarator.
    /// </summary>
    private static void ExtractParametersFromTree(ITreeNode functionDeclaratorNode,
        IPsiSourceFile sourceFile, MemberInfo info)
    {
        var parameters = new List<Models.ParameterInfo>();

        foreach (var child in functionDeclaratorNode.Descendants())
        {
            var childTypeName = child.GetType().Name;
            if (childTypeName != "Declarator" && childTypeName != "InitDeclarator") continue;

            var paramElement = TryGetDeclaredElement(child);
            if (paramElement == null || paramElement.ShortName == "<anonymous>") continue;

            var paramInfo = new Models.ParameterInfo
            {
                Name = paramElement.ShortName,
            };

            TryExtractParamType(paramElement, child, paramInfo);

            // Check for default value (InitDeclarator typically has an initializer)
            if (childTypeName == "InitDeclarator")
                paramInfo.HasDefault = true;

            parameters.Add(paramInfo);
        }

        if (parameters.Count > 0)
            info.Parameters = parameters;
    }

    // ── Helpers: type extraction via reflection ─────────────────────────

    /// <summary>
    /// Get a presentable type string from ICppTypedDeclaredElement.Type via reflection.
    /// Tries multiple strategies: GetPresentableName, GetLongPresentableText, ToString.
    /// </summary>
    private static string GetTypePresentation(IDeclaredElement element)
    {
        try
        {
            var typeProp = element.GetType().GetProperty("Type",
                BindingFlags.Public | BindingFlags.Instance);
            if (typeProp == null) return null;

            var typeObj = typeProp.GetValue(element);
            if (typeObj == null) return null;

            var typeObjType = typeObj.GetType();
            var methods = typeObjType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            // Strategy 1: GetPresentableName with a single parameter (PsiLanguageType)
            foreach (var method in methods)
            {
                if (method.Name != "GetPresentableName") continue;
                var parms = method.GetParameters();
                if (parms.Length == 1)
                {
                    var result = method.Invoke(typeObj, new object[] { null })?.ToString();
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }

            // Strategy 2: GetLongPresentableText (no parameters)
            foreach (var method in methods)
            {
                if (method.Name != "GetLongPresentableText") continue;
                var parms = method.GetParameters();
                if (parms.Length == 0)
                {
                    var result = method.Invoke(typeObj, null)?.ToString();
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }

            // Strategy 3: GetPresentableName with no parameters
            foreach (var method in methods)
            {
                if (method.Name != "GetPresentableName") continue;
                var parms = method.GetParameters();
                if (parms.Length == 0)
                {
                    var result = method.Invoke(typeObj, null)?.ToString();
                    if (!string.IsNullOrEmpty(result))
                        return result;
                }
            }

            // Strategy 4: ToString
            var str = typeObj.ToString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        catch
        {
            return null;
        }
    }

    private static void TryExtractReturnType(IDeclaredElement element, ITreeNode node,
        MemberInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
        {
            info.ReturnType = typeStr;
            return;
        }

        // Text-based fallback: extract return type from declaration text
        var textType = ExtractTypeFromDeclarationText(node);
        if (textType != null)
            info.ReturnType = textType;
    }

    private static void TryExtractFieldType(IDeclaredElement element, ITreeNode node,
        MemberInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
        {
            info.Type = typeStr;
            return;
        }

        var textType = ExtractTypeFromDeclarationText(node);
        if (textType != null)
            info.Type = textType;
    }

    private static void TryExtractParamType(IDeclaredElement element, ITreeNode paramNode,
        Models.ParameterInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
        {
            info.Type = typeStr;
            return;
        }

        var textType = ExtractTypeFromDeclarationText(paramNode);
        if (textType != null)
            info.Type = textType;
    }

    // ── Helpers: text-based type extraction ──────────────────────────────

    private static readonly HashSet<string> CppStorageSpecifiers = new(StringComparer.Ordinal)
    {
        "static", "virtual", "inline", "explicit", "constexpr", "consteval", "constinit",
        "extern", "mutable", "friend", "register", "thread_local",
        "FORCEINLINE"
    };

    /// <summary>
    /// Extract the type from the declaration text preceding a declarator node.
    /// For "virtual void OnPossess(...)" where the declarator is "OnPossess(...)",
    /// this returns "void" after stripping storage-class specifiers.
    /// </summary>
    private static string ExtractTypeFromDeclarationText(ITreeNode declaratorNode)
    {
        try
        {
            var parent = declaratorNode.Parent;
            if (parent == null) return null;

            var parentText = parent.GetText();
            if (parentText == null) return null;

            // Use tree offsets to find the text preceding the declarator within its parent
            var nodeOffset = declaratorNode.GetTreeStartOffset().Offset
                             - parent.GetTreeStartOffset().Offset;
            if (nodeOffset > 0 && nodeOffset <= parentText.Length)
            {
                var precedingText = parentText.Substring(0, nodeOffset);
                var cleaned = CleanTypeString(precedingText);
                if (cleaned != null)
                {
                    // If the declarator text starts with pointer/reference operators,
                    // they belong to the type (C++ grammar puts * and & on the declarator)
                    var declText = declaratorNode.GetText();
                    if (declText != null)
                    {
                        var prefix = "";
                        for (var i = 0; i < declText.Length; i++)
                        {
                            if (declText[i] == '*' || declText[i] == '&')
                                prefix += declText[i];
                            else if (declText[i] != ' ')
                                break;
                        }

                        if (prefix.Length > 0 && !cleaned.EndsWith("*") && !cleaned.EndsWith("&"))
                            cleaned += prefix;
                    }

                    return cleaned;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Clean a raw type string by stripping C++ storage-class specifiers, API export macros,
    /// and extraneous whitespace.
    /// </summary>
    private static string CleanTypeString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var result = raw.Trim().TrimEnd(';').Trim();

        // Strip leading storage-class specifiers, API macros, and UE macro invocations
        while (true)
        {
            var changed = false;
            foreach (var spec in CppStorageSpecifiers)
            {
                if (!result.StartsWith(spec)) continue;
                if (result.Length > spec.Length && !char.IsWhiteSpace(result[spec.Length])) continue;
                result = result.Length == spec.Length ? "" : result.Substring(spec.Length).TrimStart();
                changed = true;
                break;
            }

            // Strip API export macros (all-uppercase ending in _API, e.g. UDEMY_CUI_API)
            if (!changed && result.Length > 4)
            {
                var spaceIdx = result.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var token = result.Substring(0, spaceIdx);
                    if (token.EndsWith("_API") && token == token.ToUpperInvariant())
                    {
                        result = result.Substring(spaceIdx).TrimStart();
                        changed = true;
                    }
                }
            }

            // Strip UE macro invocations (UPROPERTY(...), UFUNCTION(...), etc.)
            // These leak into the type text when preceding a field/method declaration.
            if (!changed)
            {
                foreach (var macroName in UeMacroNames)
                {
                    if (!result.StartsWith(macroName + "(")) continue;
                    var depth = 0;
                    for (var i = macroName.Length; i < result.Length; i++)
                    {
                        if (result[i] == '(') depth++;
                        else if (result[i] == ')')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                result = result.Substring(i + 1).TrimStart();
                                changed = true;
                                break;
                            }
                        }
                    }

                    if (changed) break;
                }
            }

            if (!changed) break;
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ── Helpers: UE macro annotations ──────────────────────────────────

    private static readonly string[] UeMacroNames =
    {
        "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UPROPERTY", "UFUNCTION"
    };

    /// <summary>
    /// Extract UE macro annotations (UCLASS, UPROPERTY, etc.) by scanning the PSI tree
    /// near the given node. Two strategies:
    /// 1) Walk preceding siblings of the node and its parents (finds UCLASS/USTRUCT
    ///    which are separate statement nodes before the class declaration).
    /// 2) Search preceding text within parent nodes using LastIndexOf (finds
    ///    UPROPERTY/UFUNCTION embedded in the parent declaration text).
    /// </summary>
    private static List<string> TryExtractUeAnnotations(ITreeNode node)
    {
        try
        {
            // Strategy 1: Scan preceding siblings of the node and its parent chain.
            var current = node;
            for (var depth = 0; depth < 4 && current != null; depth++)
            {
                var sibling = current.PrevSibling;
                for (var sibCount = 0; sibCount < 10 && sibling != null; sibCount++)
                {
                    var sibText = sibling.GetText();
                    if (sibText == null || string.IsNullOrWhiteSpace(sibText))
                    {
                        sibling = sibling.PrevSibling;
                        continue;
                    }

                    var trimmed = sibText.Trim();
                    var annotations = ExtractMacrosFromText(trimmed);
                    if (annotations != null)
                        return annotations;

                    // Stop at substantive non-macro content
                    break;
                }

                current = current.Parent;
            }

            // Strategy 2: Search text preceding the node within parent/grandparent nodes.
            // Uses LastIndexOf to find the nearest (most relevant) macro.
            var candidate = node.Parent;
            for (var level = 0; level < 3 && candidate != null; level++)
            {
                var candidateText = candidate.GetText();
                if (candidateText == null)
                {
                    candidate = candidate.Parent;
                    continue;
                }

                var nodeOffset = node.GetTreeStartOffset().Offset
                                 - candidate.GetTreeStartOffset().Offset;
                if (nodeOffset <= 0)
                {
                    candidate = candidate.Parent;
                    continue;
                }

                var precedingText = candidateText.Substring(0,
                    Math.Min(nodeOffset, candidateText.Length));

                List<string> annotations = null;
                foreach (var macroName in UeMacroNames)
                {
                    // Use LastIndexOf to find the nearest macro before the node
                    var searchTarget = macroName + "(";
                    var idx = precedingText.LastIndexOf(searchTarget, StringComparison.Ordinal);
                    if (idx < 0) continue;

                    if (idx > 0 && (char.IsLetterOrDigit(precedingText[idx - 1])
                                    || precedingText[idx - 1] == '_'))
                        continue;

                    var macroCall = ExtractBalancedMacroCall(precedingText, idx, macroName.Length);
                    if (macroCall != null)
                    {
                        annotations ??= new List<string>();
                        annotations.Add(macroCall);
                    }
                }

                if (annotations != null)
                    return annotations;

                candidate = candidate.Parent;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Scan a text block for UE macro invocations and return all found.
    /// </summary>
    private static List<string> ExtractMacrosFromText(string text)
    {
        List<string> annotations = null;
        foreach (var macroName in UeMacroNames)
        {
            var idx = text.IndexOf(macroName + "(", StringComparison.Ordinal);
            if (idx < 0) continue;
            if (idx > 0 && (char.IsLetterOrDigit(text[idx - 1]) || text[idx - 1] == '_'))
                continue;

            var macroCall = ExtractBalancedMacroCall(text, idx, macroName.Length);
            if (macroCall != null)
            {
                annotations ??= new List<string>();
                annotations.Add(macroCall);
            }
        }

        return annotations;
    }

    /// <summary>
    /// Extract a macro call with balanced parentheses starting at the given index.
    /// Returns null if parentheses are unbalanced.
    /// </summary>
    private static string ExtractBalancedMacroCall(string text, int macroStart, int nameLength)
    {
        var parenStart = macroStart + nameLength;
        if (parenStart >= text.Length || text[parenStart] != '(') return null;

        var depth = 0;
        for (var i = parenStart; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(macroStart, i + 1 - macroStart);
            }
        }

        return null; // unbalanced
    }

    // ── Helpers: access and modifiers ───────────────────────────────────

    private static void TryExtractAccess(ITreeNode node, IDeclaredElement element, TypeInfo info)
    {
        var access = GetAccess(node, element);
        if (access != null) info.Access = access;
    }

    private static void TryExtractAccess(ITreeNode node, IDeclaredElement element, MemberInfo info)
    {
        var access = GetAccess(node, element);
        if (access != null) info.Access = access;
    }

    private static string GetAccess(ITreeNode node, IDeclaredElement element)
    {
        // Check node
        if (node is IAccessRightsOwner nodeAccess)
            return GetAccessString(nodeAccess.GetAccessRights());

        // Check element
        if (element is IAccessRightsOwner elemAccess)
            return GetAccessString(elemAccess.GetAccessRights());

        // Reflection fallback
        try
        {
            var method = element.GetType().GetMethod("GetAccessRights",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null)
            {
                var result = method.Invoke(element, null);
                if (result is AccessRights ar)
                    return GetAccessString(ar);
            }
        }
        catch { }

        return null;
    }

    private static void TryExtractModifiers(IDeclaredElement element, MemberInfo info)
    {
        if (element is IModifiersOwner modOwner)
        {
            if (modOwner.IsStatic) info.IsStatic = true;
            if (modOwner.IsVirtual) info.IsVirtual = true;
            if (modOwner.IsAbstract) info.IsAbstract = true;
            if (modOwner.IsOverride) info.IsOverride = true;
        }

        // Reflection fallback for C++ elements
        try
        {
            if (info.IsStatic != true && TryGetBool(element, "IsStatic") == true)
                info.IsStatic = true;
            if (info.IsVirtual != true && TryGetBool(element, "IsVirtual") == true)
                info.IsVirtual = true;
        }
        catch { }
    }

    private static bool? TryGetBool(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(obj);
        }
        catch { }
        return null;
    }

    // ── Helpers: qualified names ────────────────────────────────────────

    /// <summary>
    /// Try to find the containing type name for an out-of-line definition.
    /// Strategy A: reflection on the C++ declared element.
    /// Strategy B: parse "ClassName::MethodName" from the node text.
    /// </summary>
    private static string TryGetContainingTypeName(ITreeNode node, IDeclaredElement element)
    {
        // Strategy A: reflection
        try
        {
            foreach (var methodName in new[] { "GetContainingType", "GetClassByMember" })
            {
                var method = element.GetType().GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method == null) continue;
                var result = method.Invoke(element, null);
                if (result == null) continue;
                var shortNameProp = result.GetType().GetProperty("ShortName");
                if (shortNameProp != null)
                {
                    var name = shortNameProp.GetValue(result)?.ToString();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
        }
        catch { }

        // Strategy B: text fallback - look for ClassName::MethodName in declarator text
        try
        {
            var text = node.GetText();
            if (text == null) return null;

            var shortName = element.ShortName;
            var pattern = "::" + shortName;
            var idx = text.IndexOf(pattern, StringComparison.Ordinal);
            if (idx <= 0) return null;

            // Walk backwards from the :: to find the class name
            var end = idx;
            var start = end - 1;
            while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] == '_'))
                start--;
            start++;

            if (start >= end) return null;
            var className = text.Substring(start, end - start);
            return string.IsNullOrEmpty(className) ? null : className;
        }
        catch { }

        return null;
    }

    // ── Helpers: namespace, line, access string ─────────────────────────

    private static string GetNamespaceQualifiedName(IDeclaredElement element)
    {
        try
        {
            if (element is ITypeElement typeElement)
                return typeElement.GetContainingNamespace()?.QualifiedName ?? "";

            if (element is ITypeMember typeMember)
            {
                var containingType = typeMember.GetContainingType();
                if (containingType != null)
                    return containingType.GetContainingNamespace()?.QualifiedName ?? "";
            }

            // Reflection fallback for C++ elements
            var method = element.GetType().GetMethod("GetContainingNamespace",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method != null)
            {
                var ns = method.Invoke(element, null);
                if (ns != null)
                {
                    var qnProp = ns.GetType().GetProperty("QualifiedName");
                    return qnProp?.GetValue(ns)?.ToString() ?? "";
                }
            }
        }
        catch { }
        return "";
    }

    private static void ExtractIncludes(List<ITreeNode> importNodes, FileStructure result,
        List<string> debugDiagnostics)
    {
        if (importNodes.Count == 0) return;

        var includeList = new List<string>();
        foreach (var node in importNodes)
        {
            string path = null;

            foreach (var propName in new[] { "Path", "FileName", "IncludePath", "Name" })
            {
                var prop = node.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(node)?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        path = val;
                        if (debugDiagnostics != null)
                            debugDiagnostics.Add("ImportDirective." + propName + " = '" + val + "'");
                        break;
                    }
                }
            }

            if (path == null)
            {
                try
                {
                    var text = node.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        path = text.Trim();
                        if (debugDiagnostics != null)
                            debugDiagnostics.Add("ImportDirective.GetText() = '" + path + "'");
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(path))
                includeList.Add(path);
        }

        if (includeList.Count > 0)
            result.Includes = includeList;
    }

    private static int? GetLine(ITreeNode node, IPsiSourceFile sourceFile)
    {
        try
        {
            var doc = sourceFile.Document;
            if (doc == null) return null;
            var range = node.GetDocumentRange();
            if (!range.IsValid()) return null;
            var offset = range.StartOffset.Offset;
            if (offset < 0 || offset > doc.GetTextLength()) return null;
            return (int)new DocumentOffset(doc, offset).ToDocumentCoords().Line + 1;
        }
        catch
        {
            return null;
        }
    }

    private static string GetAccessString(AccessRights access)
    {
        switch (access)
        {
            case AccessRights.PUBLIC: return "public";
            case AccessRights.PRIVATE: return "private";
            case AccessRights.PROTECTED: return "protected";
            case AccessRights.INTERNAL: return "internal";
            default: return null;
        }
    }
}
