using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.Progress;
using JetBrains.Application.UI.Actions;
using JetBrains.Application.UI.ActionsRevised.Menu;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.DataContext;
using JetBrains.ReSharper.Daemon;
using JetBrains.ReSharper.Daemon.SolutionAnalysis;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;

namespace ReSharperPlugin.RiderActionExplorer
{
#pragma warning disable CS0612
    [Action("ActionExplorer.RunFullInspections", "Run Full Inspections (All Files) to File",
        Id = 1732,
        IdeaShortcuts = new[] { "Control+Alt+Shift+W" })]
#pragma warning restore CS0612
    public class RunFullInspectionsAction : IExecutableAction
    {
        public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate)
        {
            return context.GetData(ProjectModelDataConstants.SOLUTION) != null;
        }

        public void Execute(IDataContext context, DelegateExecute nextExecute)
        {
            var solution = context.GetData(ProjectModelDataConstants.SOLUTION);
            if (solution == null) return;

            var allSourceFiles = CollectAllSourceFiles(solution);
            var issueLines = new List<string>();
            string modeUsed;

            var manager = solution.GetComponent<SolutionAnalysisManager>();
            var config = solution.GetComponent<SolutionAnalysisConfiguration>();

            if (!config.Paused.Value && config.CompletedOnceAfterStart.Value)
            {
                modeUsed = "SWEA (cached solution-wide analysis)";
                CollectFromSwea(solution, manager, allSourceFiles, issueLines);
            }
            else
            {
                modeUsed = "Local daemon (per-file analysis)";
                CollectFromLocalDaemon(solution, allSourceFiles, issueLines);
            }

            WriteResults(solution, issueLines, modeUsed, allSourceFiles.Count);
        }

        private static List<IPsiSourceFile> CollectAllSourceFiles(ISolution solution)
        {
            var files = new List<IPsiSourceFile>();
            foreach (var project in solution.GetAllProjects())
            {
                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    var sourceFile = projectFile.ToSourceFile();
                    if (sourceFile != null)
                        files.Add(sourceFile);
                }
            }
            return files;
        }

        private static void CollectFromSwea(
            ISolution solution,
            SolutionAnalysisManager manager,
            List<IPsiSourceFile> sourceFiles,
            List<string> issueLines)
        {
            using (ReadLockCookie.Create())
            {
                var issueSet = manager.IssueSet;

                foreach (var sourceFile in sourceFiles)
                {
                    CollectInspectionResults.CollectIssuesFromSolutionAnalysis(
                        manager, issueSet, sourceFile,
                        issue => issueLines.Add(FormatIssue(issue, sourceFile, solution)));
                }
            }
        }

        private static void CollectFromLocalDaemon(
            ISolution solution,
            List<IPsiSourceFile> sourceFiles,
            List<string> issueLines)
        {
            var solutionLifetime = solution.GetSolutionLifetimes().UntilSolutionCloseLifetime;
            var lifetimeDef = solutionLifetime.CreateNested();

            try
            {
                var progress = new ProgressIndicator(lifetimeDef.Lifetime);
                var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
                var files = new Stack<IPsiSourceFile>(sourceFiles);

                runner.RunLocalInspections(files, (file, issues) =>
                {
                    foreach (var issue in issues)
                        issueLines.Add(FormatIssue(issue, file, solution));
                }, null);
            }
            finally
            {
                lifetimeDef.Terminate();
            }
        }

        private static string FormatIssue(IssuePointer issue, IPsiSourceFile sourceFile, ISolution solution)
        {
            if (!issue.IsValid) return null;

            var severity = MapSeverity(issue.GetSeverity());
            var filePath = sourceFile.GetLocation().MakeRelativeTo(solution.SolutionDirectory);
            var line = 0;

            try
            {
                var document = sourceFile.Document;
                if (document != null && issue.Range.HasValue)
                {
                    var offset = issue.Range.Value.StartOffset;
                    if (offset >= 0 && offset <= document.GetTextLength())
                    {
                        var docOffset = new DocumentOffset(document, offset);
                        line = (int)docOffset.ToDocumentCoords().Line + 1;
                    }
                }
                else if (document != null)
                {
                    var navOffset = issue.NavigationOffset;
                    if (navOffset >= 0 && navOffset <= document.GetTextLength())
                    {
                        var docOffset = new DocumentOffset(document, navOffset);
                        line = (int)docOffset.ToDocumentCoords().Line + 1;
                    }
                }
            }
            catch
            {
                // ignore offset conversion errors
            }

            var message = issue.Message;
            return $"[{severity}] {filePath}:{line} - {message}";
        }

        private static string MapSeverity(Severity severity)
        {
            switch (severity)
            {
                case Severity.ERROR: return "ERROR";
                case Severity.WARNING: return "WARNING";
                case Severity.SUGGESTION: return "SUGGESTION";
                case Severity.HINT: return "HINT";
                case Severity.INFO: return "INFO";
                default: return severity.ToString().ToUpperInvariant();
            }
        }

        private static void WriteResults(
            ISolution solution,
            List<string> issueLines,
            string modeUsed,
            int totalFiles)
        {
            // Remove null entries from invalid issues
            issueLines.RemoveAll(l => l == null);

            var sb = new StringBuilder();
            sb.AppendLine("=== Full Code Inspection Results (All Solution Files) ===");
            sb.AppendLine($"Scanned at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Solution: {solution.SolutionDirectory}");
            sb.AppendLine($"Mode: {modeUsed}");
            sb.AppendLine($"Total files in scope: {totalFiles}");
            sb.AppendLine();

            // Count by severity
            var errorCount = issueLines.Count(l => l.StartsWith("[ERROR]"));
            var warningCount = issueLines.Count(l => l.StartsWith("[WARNING]"));
            var suggestionCount = issueLines.Count(l => l.StartsWith("[SUGGESTION]"));
            var hintCount = issueLines.Count(l => l.StartsWith("[HINT]"));
            var infoCount = issueLines.Count(l => l.StartsWith("[INFO]"));

            sb.AppendLine($"Total issues: {issueLines.Count}");
            sb.AppendLine($"  Errors:      {errorCount}");
            sb.AppendLine($"  Warnings:    {warningCount}");
            sb.AppendLine($"  Suggestions: {suggestionCount}");
            sb.AppendLine($"  Hints:       {hintCount}");
            sb.AppendLine($"  Info:        {infoCount}");
            sb.AppendLine();

            foreach (var issueLine in issueLines.OrderBy(l => l))
            {
                sb.AppendLine(issueLine);
            }

            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-full-inspections-dump.txt");

            File.WriteAllText(outputPath, sb.ToString());

            MessageBox.ShowInfo(
                $"Found {issueLines.Count} issues across {totalFiles} files.\n" +
                $"Mode: {modeUsed}\n" +
                $"Results written to:\n{outputPath}");
        }
    }
}
