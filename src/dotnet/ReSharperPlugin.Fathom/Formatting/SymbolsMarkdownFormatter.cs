using System.IO;
using System.Text;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Formatting;

public static class SymbolsMarkdownFormatter
{
    public static string FormatSearch(SymbolSearchResponse response)
    {
        var sb = new StringBuilder();

        sb.Append("# Symbol search: `").Append(response.Query).AppendLine("`");
        sb.AppendLine();

        if (response.Results.Count == 0)
        {
            sb.AppendLine("No symbols found.");
            return sb.ToString();
        }

        sb.Append("Found **").Append(response.TotalMatches).Append("** matches");
        if (response.Truncated)
            sb.Append(" (showing first ").Append(response.Results.Count).Append(')');
        sb.AppendLine();
        sb.AppendLine();

        sb.AppendLine("| Name | Kind | File | Line |");
        sb.AppendLine("|------|------|------|------|");

        foreach (var r in response.Results)
        {
            var displayFile = TruncatePath(r.File, 60);
            sb.Append("| `").Append(r.Name).Append("` | ").Append(r.Kind)
              .Append(" | ").Append(displayFile)
              .Append(" | ").Append(r.Line > 0 ? r.Line.ToString() : "?")
              .AppendLine(" |");
        }

        return sb.ToString();
    }

    public static string FormatDeclaration(DeclarationResponse response)
    {
        var sb = new StringBuilder();

        sb.Append("# Declaration: `").Append(response.Symbol).AppendLine("`");
        sb.AppendLine();

        if (response.Declarations.Count == 0)
        {
            sb.AppendLine("No declarations found.");
            if (response.ForwardDeclarations > 0)
                sb.Append("(").Append(response.ForwardDeclarations).AppendLine(" forward declarations skipped)");
            return sb.ToString();
        }

        sb.Append("**").Append(response.Declarations.Count).Append("** declaration(s)");
        if (response.ForwardDeclarations > 0)
            sb.Append(", ").Append(response.ForwardDeclarations).Append(" forward declaration(s) skipped");
        sb.AppendLine();
        sb.AppendLine();

        foreach (var decl in response.Declarations)
        {
            sb.Append("## ").Append(decl.Name);
            if (decl.ContainingType != null)
                sb.Append(" (in ").Append(decl.ContainingType).Append(')');
            sb.AppendLine();

            sb.Append("**Kind:** ").AppendLine(decl.Kind);
            sb.Append("**File:** ").Append(decl.File).Append(':').AppendLine(decl.Line.ToString());
            sb.AppendLine();

            if (decl.Snippet != null)
            {
                var ext = decl.File != null ? Path.GetExtension(decl.File).TrimStart('.') : "cpp";
                if (string.IsNullOrEmpty(ext)) ext = "cpp";

                sb.Append("```").AppendLine(ext);
                sb.AppendLine(decl.Snippet);
                sb.AppendLine("```");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string TruncatePath(string path, int maxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength) return path ?? "";
        return "..." + path.Substring(path.Length - maxLength + 3);
    }
}
