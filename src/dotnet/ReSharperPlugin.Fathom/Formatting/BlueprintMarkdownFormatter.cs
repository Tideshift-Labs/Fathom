using System.Text;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Formatting;

public static class BlueprintMarkdownFormatter
{
    public static string Format(BlueprintQueryResult result, bool debug)
    {
        var sb = new StringBuilder();
        sb.Append("# Blueprints derived from ").AppendLine(result.ClassName);
        sb.AppendLine();
        sb.Append("Cache status: ").AppendLine(result.CacheStatus);
        sb.Append("Total descendants: ").AppendLine(result.TotalCount.ToString());
        sb.AppendLine();

        if (!result.CacheReady)
        {
            sb.AppendLine("> **Warning:** Cache is still building. Results may be incomplete.");
            sb.AppendLine();
        }

        if (result.Blueprints.Count == 0)
        {
            sb.AppendLine("No derived Blueprint classes found.");
        }
        else
        {
            sb.AppendLine("## Derived Blueprint Classes");
            sb.AppendLine();
            foreach (var bp in result.Blueprints)
            {
                sb.Append("### ").AppendLine(bp.Name);
                if (!string.IsNullOrEmpty(bp.FilePath))
                    sb.Append("Location: ").AppendLine(bp.FilePath);
                if (!string.IsNullOrEmpty(bp.PackagePath))
                    sb.Append("Package Path: ").AppendLine(bp.PackagePath);
                if (!string.IsNullOrEmpty(bp.MoreInfoUrl))
                    sb.Append("More Info: ").AppendLine(bp.MoreInfoUrl);
                sb.AppendLine();
            }
        }

        if (debug && result.DebugInfo != null)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("## Debug Info");
            sb.AppendLine("```");
            sb.AppendLine(result.DebugInfo);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }
}
