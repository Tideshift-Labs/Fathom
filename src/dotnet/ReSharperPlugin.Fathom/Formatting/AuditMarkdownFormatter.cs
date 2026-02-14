using System.Linq;
using System.Text;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Formatting;

public static class AuditMarkdownFormatter
{
    public static string FormatAuditResult(BlueprintAuditResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Asset Audit");
        sb.AppendLine();
        sb.AppendLine("**Status:** Fresh (all data up-to-date)");
        sb.Append("**Total Assets:** ").AppendLine(result.TotalCount.ToString());
        if (result.Blueprints != null)
            sb.Append("**Blueprints:** ").AppendLine(result.Blueprints.Count.ToString());
        if (result.DataTableCount > 0)
            sb.Append("**DataTables:** ").AppendLine(result.DataTableCount.ToString());
        if (result.DataAssetCount > 0)
            sb.Append("**DataAssets:** ").AppendLine(result.DataAssetCount.ToString());
        if (result.ErrorCount > 0)
            sb.Append("**Errors:** ").AppendLine(result.ErrorCount.ToString());
        sb.AppendLine();

        if (result.Blueprints != null && result.Blueprints.Count > 0)
        {
            sb.AppendLine("## Blueprints");
            foreach (var e in result.Blueprints.OrderBy(b => b.Name))
            {
                FormatEntryLine(sb, e);
            }
            sb.AppendLine();
        }

        if (result.DataTables != null && result.DataTables.Count > 0)
        {
            sb.AppendLine("## DataTables");
            foreach (var e in result.DataTables.OrderBy(b => b.Name))
            {
                FormatEntryLine(sb, e);
            }
            sb.AppendLine();
        }

        if (result.DataAssets != null && result.DataAssets.Count > 0)
        {
            sb.AppendLine("## DataAssets");
            foreach (var e in result.DataAssets.OrderBy(b => b.Name))
            {
                FormatEntryLine(sb, e);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void FormatEntryLine(StringBuilder sb, BlueprintAuditEntry e)
    {
        sb.Append("- **").Append(e.Name ?? "(unknown)").Append("**");
        if (!string.IsNullOrEmpty(e.Path))
            sb.Append(" - ").Append(e.Path);
        if (e.Error != null)
            sb.Append(" *(error: ").Append(e.Error).Append(")*");
        sb.AppendLine();
    }

    public static string FormatStale(BlueprintAuditResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Blueprint Audit - STALE DATA");
        sb.AppendLine();
        sb.AppendLine("**Status:** Data is stale and cannot be returned.");
        sb.AppendLine();
        sb.Append("**Stale Blueprints:** ").Append(result.StaleCount).Append(" of ").AppendLine(result.TotalCount.ToString());
        sb.AppendLine();
        sb.AppendLine("## Stale Examples (first 10)");
        if (result.StaleExamples != null)
        {
            foreach (var e in result.StaleExamples)
            {
                sb.Append("- ").AppendLine(e.Name ?? e.Path ?? "(unknown)");
            }
        }
        sb.AppendLine();
        sb.AppendLine("**Action:** Call `/blueprint-audit/refresh` to update audit data.");

        return sb.ToString();
    }

    public static string FormatNotReady(string message)
    {
        return "# Blueprint Audit Not Ready\n\n" +
               message + "\n\n" +
               "**Action:** Call `/blueprint-audit/refresh` to generate audit data.";
    }

    public static string FormatCommandletMissing()
    {
        return "# Blueprint Audit - Plugin Not Installed\n\n" +
               BlueprintAuditConstants.CommandletMissingMessage.Replace("\n", "\n\n");
    }

    public static string FormatAuditStatus(BlueprintAuditStatus status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Blueprint Audit Status");
        sb.AppendLine();
        sb.Append("**In Progress:** ").AppendLine(status.InProgress ? "Yes" : "No");
        if (status.CommandletMissing)
        {
            sb.AppendLine("**Commandlet Missing:** Yes");
            sb.AppendLine();
            sb.AppendLine("> **WARNING:** The BlueprintAudit commandlet is not installed.");
            sb.AppendLine("> Install the FathomUELink plugin in your UE project.");
            sb.AppendLine();
        }
        sb.Append("**Boot Check Completed:** ").AppendLine(status.BootCheckCompleted ? "Yes" : "No");
        if (status.BootCheckResult != null)
            sb.Append("**Boot Check Result:** ").AppendLine(status.BootCheckResult);
        if (status.LastRefresh != null)
            sb.Append("**Last Refresh:** ").AppendLine(status.LastRefresh);
        if (status.LastExitCode.HasValue)
            sb.Append("**Last Exit Code:** ").AppendLine(status.LastExitCode.Value.ToString());
        if (status.Error != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Last Error");
            sb.AppendLine("```");
            sb.AppendLine(status.Error);
            sb.AppendLine("```");
        }
        if (status.Output != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Last Output (truncated)");
            sb.AppendLine("```");
            sb.AppendLine(status.Output);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }
}

internal static class BlueprintAuditConstants
{
    public const string CommandletMissingMessage =
        "The BlueprintAudit commandlet is not installed.\n\n" +
        "To fix this, install the FathomUELink plugin in your UE project:\n" +
        "1. Clone https://github.com/Tideshift-Labs/Fathom-UnrealEngine\n" +
        "2. Copy or symlink it to your project's Plugins/ folder\n" +
        "3. Rebuild your UE project\n" +
        "4. Restart Rider and try again";
}
