using System.Text;
using ReSharperPlugin.RiderActionExplorer.Models;

namespace ReSharperPlugin.RiderActionExplorer.Formatting;

public static class BlueprintMarkdownFormatter
{
    public static string Format(BlueprintQueryResult result, bool debug)
    {
        var sb = new StringBuilder();
        sb.Append("# Blueprints derived from ").AppendLine(result.ClassName);
        sb.AppendLine();
        sb.Append("**Cache status:** ").AppendLine(result.CacheStatus);
        sb.Append("**Total descendants:** ").AppendLine(result.TotalCount.ToString());
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
            for (var i = 0; i < result.Blueprints.Count; i++)
            {
                var bp = result.Blueprints[i];
                sb.Append(i + 1).Append(". ").Append(bp.Name);
                if (!string.IsNullOrEmpty(bp.FilePath))
                    sb.Append(" — ").Append(bp.FilePath);
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
