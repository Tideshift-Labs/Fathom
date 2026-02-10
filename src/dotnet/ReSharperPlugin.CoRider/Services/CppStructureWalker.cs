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

            TryExtractReturnType(element, info);
            ExtractParametersFromTree(node, sourceFile, info);
        }
        else
        {
            info.Kind = "field";
            TryExtractFieldType(element, info);
        }

        TryExtractAccess(node, element, info);
        TryExtractModifiers(element, info);

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

            TryExtractParamType(paramElement, paramInfo);

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

            // Try GetPresentableName with a single parameter (PsiLanguageType)
            foreach (var method in typeObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
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

            // Fallback: ToString
            var str = typeObj.ToString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        catch
        {
            return null;
        }
    }

    private static void TryExtractReturnType(IDeclaredElement element, MemberInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
            info.ReturnType = typeStr;
    }

    private static void TryExtractFieldType(IDeclaredElement element, MemberInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
            info.Type = typeStr;
    }

    private static void TryExtractParamType(IDeclaredElement element, Models.ParameterInfo info)
    {
        var typeStr = GetTypePresentation(element);
        if (typeStr != null)
            info.Type = typeStr;
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
