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
    [ShellComponent]
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
