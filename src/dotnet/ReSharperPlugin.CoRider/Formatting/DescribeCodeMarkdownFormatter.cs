using System.Collections.Generic;
using System.Text;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Formatting;

public static class DescribeCodeMarkdownFormatter
{
    public static string Format(List<FileStructure> files, int totalMs, bool debug,
        List<string> debugDiagnostics)
    {
        var sb = new StringBuilder();

        foreach (var file in files)
        {
            var displayPath = file.ResolvedPath ?? file.RequestedPath;
            sb.Append("## ").AppendLine(displayPath);

            if (file.Error != null)
            {
                sb.Append("**Error:** ").AppendLine(file.Error);
                sb.AppendLine();
                continue;
            }

            if (file.Language != null)
                sb.Append("Language: ").AppendLine(file.Language);

            // Includes
            if (file.Includes != null && file.Includes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Includes");
                foreach (var inc in file.Includes)
                    sb.Append("- `").Append(inc).AppendLine("`");
            }

            // Namespaces
            if (file.Namespaces != null)
            {
                foreach (var ns in file.Namespaces)
                    FormatNamespace(sb, ns, 3);
            }

            // Top-level types (outside namespaces)
            if (file.Types != null)
            {
                foreach (var type in file.Types)
                    FormatType(sb, type, 3);
            }

            // Free functions (outside namespaces)
            if (file.FreeFunctions != null && file.FreeFunctions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Free Functions");
                foreach (var func in file.FreeFunctions)
                    FormatMember(sb, func, "  ");
            }

            sb.AppendLine();
        }

        if (debug)
        {
            sb.AppendLine("---");
            sb.Append(files.Count).Append(" file(s) in ").Append(totalMs).AppendLine("ms");
            if (debugDiagnostics != null && debugDiagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Debug Diagnostics");
                foreach (var d in debugDiagnostics)
                    sb.Append("- ").AppendLine(d);
            }
        }

        return sb.ToString();
    }

    private static void FormatNamespace(StringBuilder sb, NamespaceInfo ns, int headingLevel)
    {
        sb.AppendLine();
        sb.Append('#', headingLevel).Append(' ').Append("namespace ").AppendLine(ns.Name);

        if (ns.Types != null)
        {
            foreach (var type in ns.Types)
                FormatType(sb, type, headingLevel + 1);
        }

        if (ns.FreeFunctions != null && ns.FreeFunctions.Count > 0)
        {
            sb.AppendLine();
            sb.Append('#', headingLevel + 1).AppendLine(" Free Functions");
            foreach (var func in ns.FreeFunctions)
                FormatMember(sb, func, "  ");
        }

        if (ns.Namespaces != null)
        {
            foreach (var nested in ns.Namespaces)
                FormatNamespace(sb, nested, headingLevel + 1);
        }
    }

    private static void FormatType(StringBuilder sb, TypeInfo type, int headingLevel)
    {
        sb.AppendLine();
        // Cap heading level at 6 (markdown max)
        var level = headingLevel > 6 ? 6 : headingLevel;
        sb.Append('#', level).Append(' ');

        // Access + modifiers + kind + name
        if (type.Access != null)
            sb.Append(type.Access).Append(' ');
        if (type.IsAbstract == true) sb.Append("abstract ");
        if (type.IsSealed == true) sb.Append("sealed ");
        if (type.IsStatic == true) sb.Append("static ");
        sb.Append(type.Kind ?? "type").Append(' ');
        sb.Append(type.Name);

        if (type.TypeParameters != null && type.TypeParameters.Count > 0)
            sb.Append('<').Append(string.Join(", ", type.TypeParameters)).Append('>');

        if (type.Line != null)
            sb.Append(" (line ").Append(type.Line).Append(')');

        sb.AppendLine();

        // Inheritance
        if (type.BaseType != null)
            sb.Append("Extends: `").Append(type.BaseType).AppendLine("`");
        if (type.Interfaces != null && type.Interfaces.Count > 0)
            sb.Append("Implements: ").AppendLine(string.Join(", ", WrapBackticks(type.Interfaces)));

        // Members
        if (type.Members != null && type.Members.Count > 0)
        {
            sb.AppendLine();
            foreach (var member in type.Members)
                FormatMember(sb, member, "");
        }

        // Nested types
        if (type.NestedTypes != null)
        {
            foreach (var nested in type.NestedTypes)
                FormatType(sb, nested, headingLevel + 1);
        }
    }

    private static void FormatMember(StringBuilder sb, MemberInfo member, string indent)
    {
        sb.Append(indent).Append("- ");

        // Access
        if (member.Access != null)
            sb.Append(member.Access).Append(' ');

        // Modifier flags
        if (member.IsStatic == true) sb.Append("static ");
        if (member.IsAbstract == true) sb.Append("abstract ");
        if (member.IsVirtual == true) sb.Append("virtual ");
        if (member.IsOverride == true) sb.Append("override ");
        if (member.IsAsync == true) sb.Append("async ");
        if (member.IsReadonly == true) sb.Append("readonly ");

        // Return type / field type
        if (member.ReturnType != null)
            sb.Append('`').Append(member.ReturnType).Append("` ");
        else if (member.Type != null && member.Kind != "property")
            sb.Append('`').Append(member.Type).Append("` ");

        // Name
        sb.Append("**").Append(member.Name).Append("**");

        // Parameters (for methods, constructors, functions)
        if (member.Parameters != null)
        {
            sb.Append('(');
            for (var i = 0; i < member.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var p = member.Parameters[i];
                if (p.Modifier != null)
                    sb.Append(p.Modifier).Append(' ');
                if (p.Type != null)
                    sb.Append(p.Type).Append(' ');
                sb.Append(p.Name);
                if (p.HasDefault == true)
                    sb.Append(" = ...");
                if (p.IsParams == true)
                    sb.Append(" [params]");
            }
            sb.Append(')');
        }

        // Property type + accessors
        if (member.Kind == "property" && member.Type != null)
        {
            sb.Append(" : `").Append(member.Type).Append('`');
            if (member.HasGetter == true || member.HasSetter == true)
            {
                sb.Append(" {");
                if (member.HasGetter == true) sb.Append(" get;");
                if (member.HasSetter == true) sb.Append(" set;");
                sb.Append(" }");
            }
        }

        // Kind label + line
        sb.Append(" *[").Append(member.Kind ?? "member");
        if (member.Line != null)
            sb.Append(", line ").Append(member.Line);
        sb.Append("]*");

        sb.AppendLine();
    }

    private static IEnumerable<string> WrapBackticks(List<string> items)
    {
        foreach (var item in items)
            yield return "`" + item + "`";
    }
}
