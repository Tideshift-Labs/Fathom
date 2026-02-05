// =============================================================================
// RunInspectionsAction.cs — DEAD END (Active but not useful)
// =============================================================================
// GOAL PROGRESS: NO — Only reads existing markup from already-open files.
//
// What it does:
//   An IExecutableAction (Ctrl+Alt+Shift+I) that reads document markup
//   (highlighting results) from all files that have an active IDocumentMarkup
//   model. Writes results to desktop as resharper-inspections-dump.txt.
//
// How it works:
//   1. Gets IDocumentMarkupManager from Shell
//   2. Iterates all project files, calls TryGetMarkupModel(document)
//   3. For files with markup, enumerates highlighters and extracts tooltip text
//   4. Filters out Usage markers, keeps Error/Warning/Suggestion/Hint
//
// Why it doesn't help:
//   IDocumentMarkupManager.TryGetMarkupModel() only returns a model for
//   documents that have active editor sessions (open files). Files not open
//   in the editor have no markup model, so they're silently skipped.
//   This is fundamentally the same constraint as IDaemon.ForceReHighlight —
//   it requires open editor tabs, which conflicts with our requirement to
//   inspect files without UI interaction.
//
// Value: First experiment. Proved that reading existing markup only works for
//   open files and that we need a different approach entirely.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.Actions;
using JetBrains.Application.UI.ActionsRevised.Menu;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.TextControl.DocumentMarkup;
using JetBrains.Util;

namespace ReSharperPlugin.RiderActionExplorer
{
#pragma warning disable CS0612
    [Action("ActionExplorer.RunInspections", "Run Inspections to File",
        Id = 1730,
        IdeaShortcuts = new[] { "Control+Alt+Shift+I" })]
#pragma warning restore CS0612
    public class RunInspectionsAction : IExecutableAction
    {
        public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate)
        {
            return context.GetData(ProjectModelDataConstants.SOLUTION) != null;
        }

        public void Execute(IDataContext context, DelegateExecute nextExecute)
        {
            var solution = context.GetData(ProjectModelDataConstants.SOLUTION);
            if (solution == null) return;

            var sb = new StringBuilder();
            sb.AppendLine("=== Code Inspection Results ===");
            sb.AppendLine($"Scanned at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            var issueCount = 0;
            var issueLines = new List<string>();
            var filesScanned = 0;

            var markupManager = Shell.Instance.GetComponent<IDocumentMarkupManager>();

            foreach (var project in solution.GetAllProjects())
            {
                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    var sourceFile = projectFile.ToSourceFile();
                    if (sourceFile == null) continue;

                    var document = sourceFile.Document;
                    if (document == null) continue;

                    var markup = markupManager.TryGetMarkupModel(document);
                    if (markup == null) continue;

                    filesScanned++;

                    try
                    {
                        foreach (var highlighter in markup.GetHighlightersEnumerable(
                            JetBrains.Util.dataStructures.OnWriteLockRequestedBehavior.MATERIALIZE_ENUMERABLE,
                            null))
                        {
                            var errorStripeAttrs = highlighter.ErrorStripeAttributes;
                            if (errorStripeAttrs == null) continue;

                            var markerKind = errorStripeAttrs.MarkerKind;
                            if (markerKind == ErrorStripeMarkerKind.Usage) continue;

                            var tooltip = highlighter.TryGetTooltip(HighlighterTooltipKind.ErrorStripe);
                            var message = tooltip?.ToString();
                            if (string.IsNullOrEmpty(message)) continue;

                            var line = 0;
                            try
                            {
                                if (highlighter is JetBrains.TextControl.Data.IRangeable rangeable
                                    && rangeable.Range.IsValid)
                                {
                                    var docOffset = new DocumentOffset(document, rangeable.Range.StartOffset);
                                    line = (int)docOffset.ToDocumentCoords().Line + 1;
                                }
                            }
                            catch
                            {
                                // ignore range conversion errors
                            }

                            var filePath = sourceFile.GetLocation()
                                .MakeRelativeTo(solution.SolutionDirectory);
                            var severityTag = MapMarkerKind(markerKind);

                            issueLines.Add($"[{severityTag}] {filePath}:{line} - {message}");
                            issueCount++;
                        }
                    }
                    catch
                    {
                        // skip files with markup enumeration errors
                    }
                }
            }

            sb.AppendLine($"Total issues found: {issueCount}");
            sb.AppendLine($"Files scanned: {filesScanned}");
            sb.AppendLine();

            foreach (var issueLine in issueLines.OrderBy(l => l))
            {
                sb.AppendLine(issueLine);
            }

            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-inspections-dump.txt");

            File.WriteAllText(outputPath, sb.ToString());

            MessageBox.ShowInfo($"Found {issueCount} issues in {filesScanned} files.\nResults written to:\n{outputPath}");
        }

        private static string MapMarkerKind(ErrorStripeMarkerKind kind)
        {
            switch (kind)
            {
                case ErrorStripeMarkerKind.Error: return "ERROR";
                case ErrorStripeMarkerKind.Warning: return "WARNING";
                case ErrorStripeMarkerKind.Suggestion: return "SUGGESTION";
                case ErrorStripeMarkerKind.Info: return "HINT";
                default: return kind.ToString().ToUpperInvariant();
            }
        }
    }
}
