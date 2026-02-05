// =============================================================================
// ActionDumperComponent.cs — UTILITY (Active)
// =============================================================================
// GOAL PROGRESS: N/A — Diagnostic utility, not related to inspection.
//
// What it does:
//   A [ShellComponent] that automatically dumps all registered actions at shell
//   startup. Same output as DumpActionsAction.cs but runs without user trigger.
//
// How it works:
//   Constructor receives IActionManager, iterates all action defs, writes to
//   resharper-actions-dump.txt on the desktop.
//
// Value: Proved that [ShellComponent] auto-runs at startup. Useful for
//   debugging action registration issues without relying on keyboard shortcuts.
// =============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application;
using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.Application.UI.Actions.ActionManager;
using JetBrains.Lifetimes;

namespace ReSharperPlugin.RiderActionExplorer
{
    // Disabled: auto-run replaced by InspectionHttpServer
    // [ShellComponent]
    public class ActionDumperComponent
    {
        public ActionDumperComponent(Lifetime lifetime, IActionManager actionManager)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== All Registered Actions ===");
            sb.AppendLine($"Dumped at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var actionDefs = actionManager.Defs.GetAllActionDefs()
                .OrderBy(def => def.ActionId)
                .ToList();

            sb.AppendLine($"Total actions found: {actionDefs.Count}");
            sb.AppendLine();

            foreach (var def in actionDefs)
            {
                sb.AppendLine($"  ID: {def.ActionId}");
                sb.AppendLine($"  Text: {def.Text}");
                sb.AppendLine($"  Description: {def.Description}");
                sb.AppendLine();
            }

            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-actions-dump.txt");

            File.WriteAllText(outputPath, sb.ToString());
        }
    }
}
