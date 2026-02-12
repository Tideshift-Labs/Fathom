using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Formatting;

public static class InspectMarkdownFormatter
{
    public static string Format(List<FileInspectionResult> results, int totalMs, bool debug)
    {
        var sb = new StringBuilder();
        var totalIssues = results.Sum(r => r.Issues.Count);

        foreach (var r in results)
        {
            var displayPath = r.ResolvedPath ?? r.RequestedPath;

            if (r.Error != null)
            {
                sb.Append("## ").Append(displayPath).AppendLine(" (error)");
                sb.Append("**Error:** ").AppendLine(r.Error);
            }
            else if (r.Issues.Count == 0)
            {
                sb.Append("## ").Append(displayPath).AppendLine(" (0 issues)");
            }
            else
            {
                sb.Append("## ").Append(displayPath)
                    .Append(" (").Append(r.Issues.Count)
                    .Append(r.Issues.Count == 1 ? " issue)" : " issues)")
                    .AppendLine();
                for (var i = 0; i < r.Issues.Count; i++)
                {
                    var issue = r.Issues[i];
                    sb.Append(i + 1).Append(". ")
                        .Append(issue.Message)
                        .Append(" [").Append(issue.Severity).Append("]")
                        .Append(" (line ").Append(issue.Line).Append(")")
                        .AppendLine();
                }
            }

            if (debug && r.SyncResult != null)
            {
                sb.Append("> Debug: PSI ").Append(r.SyncResult.Status);
                if (r.SyncResult.WaitedMs > 0)
                    sb.Append(" (").Append(r.SyncResult.WaitedMs).Append("ms, ")
                        .Append(r.SyncResult.Attempts).Append(" attempts)");
                sb.Append(" | Inspection: ").Append(r.InspectionMs).Append("ms");
                if (r.Retries > 0)
                    sb.Append(" | Retries: ").Append(r.Retries);
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (debug)
        {
            sb.Append("---").AppendLine();
            sb.Append("Total: ").Append(totalIssues).Append(" issues across ")
                .Append(results.Count).Append(" files in ")
                .Append(totalMs).Append("ms").AppendLine();
        }

        return sb.ToString();
    }
}
