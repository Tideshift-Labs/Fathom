// =============================================================================
// DumpActionsAction.cs — UTILITY (Active)
// =============================================================================
// GOAL PROGRESS: N/A — Diagnostic utility, not related to inspection.
//
// What it does:
//   An IExecutableAction (Ctrl+Alt+Shift+D) that dumps all registered
//   ReSharper/Rider backend actions to a text file on the desktop.
//
// How it works:
//   Gets IActionManager from Shell, iterates Defs.GetAllActionDefs(),
//   writes ID/Text/Description for each action to resharper-actions-dump.txt.
//
// Value: Helped verify that our plugin actions were registered in the backend.
//   Also useful for discovering available action IDs.
// =============================================================================

using System;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.Actions;
using JetBrains.Application.UI.Actions.ActionManager;
using JetBrains.Application.UI.ActionsRevised.Menu;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;

namespace ReSharperPlugin.RiderActionExplorer
{
#pragma warning disable CS0612 // ActionAttribute(string, string) is obsolete in 2025.3
    [Action("ActionExplorer.DumpActions", "Dump All Registered Actions",
        Id = 1729,
        IdeaShortcuts = new[] { "Control+Alt+Shift+D" })]
#pragma warning restore CS0612
    public class DumpActionsAction : IExecutableAction
    {
        public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate)
        {
            return true;
        }

        public void Execute(IDataContext context, DelegateExecute nextExecute)
        {
            var actionManager = Shell.Instance.GetComponent<IActionManager>();
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

            MessageBox.ShowInfo($"Dumped {actionDefs.Count} actions to:\n{outputPath}");
        }
    }
}
