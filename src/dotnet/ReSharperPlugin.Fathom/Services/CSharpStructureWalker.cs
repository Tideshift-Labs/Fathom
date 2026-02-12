using System.Collections.Generic;
using System.Linq;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using ReSharperPlugin.Fathom.Models;
using MemberInfo = ReSharperPlugin.Fathom.Models.MemberInfo;
using ParameterInfo = ReSharperPlugin.Fathom.Models.ParameterInfo;

namespace ReSharperPlugin.Fathom.Services;

public static class CSharpStructureWalker
{
    public static void Walk(ICSharpFile csFile, IPsiSourceFile sourceFile, FileStructure result)
    {
        result.Language = "C#";

        // Walk namespace declarations
        foreach (var nsDecl in csFile.NamespaceDeclarations)
        {
            var nsInfo = WalkNamespace(nsDecl, sourceFile);
            if (nsInfo != null)
            {
                result.Namespaces ??= new List<NamespaceInfo>();
                result.Namespaces.Add(nsInfo);
            }
        }

        // Walk top-level type declarations (outside namespaces)
        foreach (var typeDecl in csFile.TypeDeclarations)
        {
            var typeInfo = WalkType(typeDecl, sourceFile);
            if (typeInfo != null)
            {
                result.Types ??= new List<TypeInfo>();
                result.Types.Add(typeInfo);
            }
        }
    }

    private static NamespaceInfo WalkNamespace(ICSharpNamespaceDeclaration nsDecl, IPsiSourceFile sourceFile)
    {
        var info = new NamespaceInfo { Name = nsDecl.DeclaredName };

        // Nested namespaces
        foreach (var nested in nsDecl.NamespaceDeclarations)
        {
            var nestedInfo = WalkNamespace(nested, sourceFile);
            if (nestedInfo != null)
            {
                info.Namespaces ??= new List<NamespaceInfo>();
                info.Namespaces.Add(nestedInfo);
            }
        }

        // Type declarations inside this namespace
        foreach (var typeDecl in nsDecl.TypeDeclarations)
        {
            var typeInfo = WalkType(typeDecl, sourceFile);
            if (typeInfo != null)
            {
                info.Types ??= new List<TypeInfo>();
                info.Types.Add(typeInfo);
            }
        }

        return info;
    }

    private static TypeInfo WalkType(ITypeDeclaration typeDecl, IPsiSourceFile sourceFile)
    {
        var info = new TypeInfo
        {
            Name = typeDecl.DeclaredName,
            Line = GetLine(typeDecl, sourceFile),
            Access = GetAccessFromNode(typeDecl),
        };

        // Determine kind
        switch (typeDecl)
        {
            case IClassDeclaration:
                info.Kind = "class";
                break;
            case IStructDeclaration:
                info.Kind = "struct";
                break;
            case IInterfaceDeclaration:
                info.Kind = "interface";
                break;
            case IEnumDeclaration:
                info.Kind = "enum";
                break;
            default:
                info.Kind = "type";
                break;
        }

        // Base types and interfaces
        var declaredElement = typeDecl.DeclaredElement as ITypeElement;
        if (declaredElement != null)
        {
            foreach (var superType in declaredElement.GetSuperTypes())
            {
                var typeName = superType.GetPresentableName(CSharpLanguage.Instance);
                var resolvedType = superType.Resolve().DeclaredElement as ITypeElement;
                if (resolvedType is IInterface)
                {
                    info.Interfaces ??= new List<string>();
                    info.Interfaces.Add(typeName);
                }
                else if (typeName != "object" && typeName != "ValueType")
                {
                    info.BaseType = typeName;
                }
            }

            // Modifiers from declared element
            if (declaredElement is IModifiersOwner modOwner)
            {
                if (modOwner.IsAbstract) info.IsAbstract = true;
                if (modOwner.IsSealed) info.IsSealed = true;
                if (modOwner.IsStatic) info.IsStatic = true;
            }
        }

        // Type parameters (from declared element, not declaration)
        if (declaredElement is ITypeParametersOwner tpOwner && tpOwner.TypeParameters.Count > 0)
        {
            info.TypeParameters = tpOwner.TypeParameters
                .Select(tp => tp.ShortName).ToList();
        }

        // Members
        foreach (var memberDecl in typeDecl.MemberDeclarations)
        {
            var memberInfo = WalkMember(memberDecl, sourceFile);
            if (memberInfo != null)
            {
                info.Members ??= new List<MemberInfo>();
                info.Members.Add(memberInfo);
            }
        }

        // Nested types
        foreach (var nestedTypeDecl in typeDecl.NestedTypeDeclarations)
        {
            var nestedInfo = WalkType(nestedTypeDecl, sourceFile);
            if (nestedInfo != null)
            {
                info.NestedTypes ??= new List<TypeInfo>();
                info.NestedTypes.Add(nestedInfo);
            }
        }

        return info;
    }

    private static MemberInfo WalkMember(IDeclaration memberDecl, IPsiSourceFile sourceFile)
    {
        var info = new MemberInfo
        {
            Name = memberDecl.DeclaredName,
            Line = GetLine(memberDecl, sourceFile),
        };

        switch (memberDecl)
        {
            case IConstructorDeclaration ctorDecl:
                info.Kind = "constructor";
                info.Access = GetAccessFromNode(ctorDecl);
                info.Parameters = WalkParameters(ctorDecl.ParameterDeclarations);
                if (ctorDecl.IsStatic) info.IsStatic = true;
                break;

            case IDestructorDeclaration:
                info.Kind = "destructor";
                break;

            case IMethodDeclaration methodDecl:
                info.Kind = "method";
                info.Access = GetAccessFromNode(methodDecl);
                info.ReturnType = methodDecl.Type?.GetPresentableName(CSharpLanguage.Instance);
                info.Parameters = WalkParameters(methodDecl.ParameterDeclarations);
                SetMethodModifiers(info, methodDecl.DeclaredElement);
                if (methodDecl.IsAsync) info.IsAsync = true;
                // Type parameters on method
                if (methodDecl.DeclaredElement is ITypeParametersOwner methodTpOwner &&
                    methodTpOwner.TypeParameters.Count > 0)
                {
                    var typeParams = methodTpOwner.TypeParameters
                        .Select(tp => tp.ShortName).ToList();
                    info.Name += "<" + string.Join(", ", typeParams) + ">";
                }
                break;

            case IPropertyDeclaration propDecl:
                info.Kind = "property";
                info.Access = GetAccessFromNode(propDecl);
                info.Type = propDecl.Type?.GetPresentableName(CSharpLanguage.Instance);
                SetPropertyAccessors(info, propDecl);
                SetMemberModifiers(info, propDecl.DeclaredElement);
                break;

            case IIndexerDeclaration indexerDecl:
                info.Kind = "property";
                info.Name = "this[]";
                info.Access = GetAccessFromNode(indexerDecl);
                info.Type = indexerDecl.Type?.GetPresentableName(CSharpLanguage.Instance);
                info.Parameters = WalkParameters(indexerDecl.ParameterDeclarations);
                break;

            case IFieldDeclaration fieldDecl:
                info.Kind = "field";
                info.Access = GetAccessFromNode(fieldDecl);
                info.Type = fieldDecl.Type?.GetPresentableName(CSharpLanguage.Instance);
                SetMemberModifiers(info, fieldDecl.DeclaredElement);
                if (fieldDecl.IsReadonly) info.IsReadonly = true;
                break;

            case IEventDeclaration eventDecl:
                info.Kind = "event";
                info.Access = GetAccessFromNode(eventDecl);
                info.Type = eventDecl.Type?.GetPresentableName(CSharpLanguage.Instance);
                break;

            default:
                info.Kind = "member";
                break;
        }

        return info;
    }

    private static List<ParameterInfo> WalkParameters(
        TreeNodeCollection<ICSharpParameterDeclaration> paramDecls)
    {
        if (paramDecls.Count == 0) return null;
        return paramDecls.Select(p =>
        {
            var pi = new ParameterInfo
            {
                Name = p.DeclaredName,
                Type = p.Type?.GetPresentableName(CSharpLanguage.Instance),
            };
            if (p.IsParams) pi.IsParams = true;
            // Default value: check via the declared element
            var paramElement = p.DeclaredElement;
            if (paramElement != null && paramElement.IsOptional) pi.HasDefault = true;
            var kind = paramElement?.Kind;
            if (kind == ParameterKind.REFERENCE) pi.Modifier = "ref";
            else if (kind == ParameterKind.OUTPUT) pi.Modifier = "out";
            else if (kind == ParameterKind.INPUT) pi.Modifier = "in";
            return pi;
        }).ToList();
    }

    private static void SetMethodModifiers(MemberInfo info, IDeclaredElement element)
    {
        if (element is IModifiersOwner modOwner)
        {
            if (modOwner.IsStatic) info.IsStatic = true;
            if (modOwner.IsVirtual) info.IsVirtual = true;
            if (modOwner.IsAbstract) info.IsAbstract = true;
            if (modOwner.IsOverride) info.IsOverride = true;
        }
    }

    private static void SetMemberModifiers(MemberInfo info, IDeclaredElement element)
    {
        if (element is IModifiersOwner modOwner)
        {
            if (modOwner.IsStatic) info.IsStatic = true;
            if (modOwner.IsVirtual) info.IsVirtual = true;
            if (modOwner.IsAbstract) info.IsAbstract = true;
            if (modOwner.IsOverride) info.IsOverride = true;
        }
    }

    private static void SetPropertyAccessors(MemberInfo info, IPropertyDeclaration propDecl)
    {
        var accessors = propDecl.AccessorDeclarations;
        bool hasGetter = false, hasSetter = false;
        foreach (var accessor in accessors)
        {
            if (accessor.Kind == AccessorKind.GETTER) hasGetter = true;
            if (accessor.Kind == AccessorKind.SETTER) hasSetter = true;
        }
        // Auto-properties without explicit accessors still have get/set
        if (!hasGetter && !hasSetter)
        {
            hasGetter = true;
            hasSetter = true;
        }
        if (hasGetter) info.HasGetter = true;
        if (hasSetter) info.HasSetter = true;
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

    private static string GetAccessFromNode(ITreeNode node)
    {
        if (node is IAccessRightsOwner accessOwner)
            return GetAccessString(accessOwner.GetAccessRights());
        return null;
    }

    private static string GetAccessString(AccessRights access)
    {
        switch (access)
        {
            case AccessRights.PUBLIC: return "public";
            case AccessRights.PRIVATE: return "private";
            case AccessRights.PROTECTED: return "protected";
            case AccessRights.INTERNAL: return "internal";
            case AccessRights.PROTECTED_OR_INTERNAL: return "protected internal";
            case AccessRights.PROTECTED_AND_INTERNAL: return "private protected";
            default: return null;
        }
    }
}
