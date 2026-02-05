// =============================================================================
// FullInspectionTestComponent.cs — C#-ONLY SUCCESS (Disabled)
// =============================================================================
// GOAL PROGRESS: PARTIAL — Proved RunLocalInspections works for C#, not C++.
//
// What it does:
//   Auto-runs on solution load. Collects ALL source files across ALL projects,
//   then runs CollectInspectionResults.RunLocalInspections() on the entire batch.
//   Writes results to desktop as resharper-full-inspections-dump.txt.
//
// How it works:
//   1. Waits 10s for solution indexing to settle
//   2. Collects all IPsiSourceFile from all projects (no filtering)
//   3. Creates CollectInspectionResults with a nested lifetime
//   4. Calls RunLocalInspections with the full file stack
//   5. Formats IssuePointer results with severity, path, line, and message
//
// Why it doesn't help for C++:
//   RunLocalInspections only triggers ReSharper daemon stages (C# analyzers).
//   C++ files get callbacks but with 0 issues. This is because the internal
//   InspectionDaemon lacks FileImages.DisableCheckThread().
//
// Value: Proved that RunLocalInspections works for C# (88 issues found on a
//   C# project without SWEA). Also established the correct lifetime management
//   pattern — terminating the lifetime too early causes silent 0-issue results.
//
// Superseded by: CppInspectionExperiment.cs (for diagnostics),
//   InspectCodeDaemonExperiment.cs (for the working C++ solution)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.Application.Progress;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.SolutionAnalysis;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;

namespace ReSharperPlugin.RiderActionExplorer
{
    // Disabled: superseded by CppInspectionExperiment
    // [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class FullInspectionTestComponent
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<FullInspectionTestComponent>();

        public FullInspectionTestComponent(Lifetime lifetime, ISolution solution)
        {
            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-full-inspections-dump.txt");

            // Phase 1: write immediately to prove the component loaded
            try
            {
                File.WriteAllText(outputPath,
                    $"=== FullInspectionTestComponent loaded ===\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Solution: {solution.SolutionDirectory}\n" +
                    $"Waiting for SWEA to complete...\n");
                Log.Verbose("FullInspectionTestComponent: wrote initial marker file");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FullInspectionTestComponent: failed to write marker file");
            }

            // Phase 2: run local daemon analysis on a background thread
            var thread = new Thread(() => RunLocalAnalysis(lifetime, solution, outputPath))
            {
                IsBackground = true,
                Name = "FullInspectionTestComponent"
            };
            thread.Start();
        }

        private static void RunLocalAnalysis(Lifetime lifetime, ISolution solution, string outputPath)
        {
            // Give the solution a moment to finish loading
            Thread.Sleep(10000);
            if (lifetime.IsNotAlive) return;

            try
            {
                Log.Verbose("FullInspectionTestComponent: starting local daemon analysis...");
                var allSourceFiles = CollectAllSourceFiles(solution);

                File.WriteAllText(outputPath,
                    $"=== FullInspectionTestComponent running ===\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Files to analyze: {allSourceFiles.Count}\n" +
                    $"Running local daemon per-file analysis...\n");

                var issueLines = new List<string>();
                var filesProcessed = 0;
                var callbackCount = 0;

                // Use the parent lifetime directly - don't nest and terminate early
                var lifetimeDef = lifetime.CreateNested();
                var progress = new ProgressIndicator(lifetimeDef.Lifetime);
                var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
                var files = new Stack<IPsiSourceFile>(allSourceFiles);

                Log.Verbose($"FullInspectionTestComponent: calling RunLocalInspections with {files.Count} files...");

                runner.RunLocalInspections(files, (file, issues) =>
                {
                    callbackCount++;
                    filesProcessed++;
                    var issueCount = issues?.Count ?? 0;
                    Log.Verbose($"FullInspectionTestComponent: callback #{callbackCount} for {file.Name}, {issueCount} issues");

                    if (issues != null)
                    {
                        foreach (var issue in issues)
                            issueLines.Add(FormatIssue(issue, file, solution));
                    }
                }, null);

                Log.Verbose($"FullInspectionTestComponent: RunLocalInspections returned. " +
                            $"Callbacks fired: {callbackCount}, files remaining in stack: {files.Count}");

                // If RunLocalInspections is async and returned immediately with no callbacks,
                // wait and check periodically
                if (callbackCount == 0)
                {
                    Log.Verbose("FullInspectionTestComponent: no callbacks fired, RunLocalInspections may be async. Waiting...");
                    for (var i = 0; i < 60; i++)
                    {
                        Thread.Sleep(5000);
                        if (lifetime.IsNotAlive) return;
                        if (callbackCount > 0)
                        {
                            Log.Verbose($"FullInspectionTestComponent: callbacks started firing after wait. Count: {callbackCount}");
                            // Wait a bit more for it to finish
                            Thread.Sleep(30000);
                            break;
                        }
                    }
                }

                lifetimeDef.Terminate();

                issueLines.RemoveAll(l => l == null);

                // Write diagnostic info alongside results
                var diagnostics = $"Diagnostics: callbacks={callbackCount}, filesProcessed={filesProcessed}, " +
                                  $"issueLines={issueLines.Count}, stackRemaining={files.Count}";
                Log.Verbose($"FullInspectionTestComponent: {diagnostics}");

                WriteResults(solution, issueLines, $"Local daemon (per-file analysis) | {diagnostics}",
                    allSourceFiles.Count, outputPath);
                Log.Verbose($"FullInspectionTestComponent: wrote {issueLines.Count} issues");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FullInspectionTestComponent analysis failed");
                try
                {
                    File.WriteAllText(outputPath,
                        $"=== FullInspectionTestComponent FAILED ===\n" +
                        $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Error: {ex.Message}\n\n{ex.StackTrace}\n");
                }
                catch { /* ignore */ }
            }
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

            return $"[{severity}] {filePath}:{line} - {issue.Message}";
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

        private static void WriteResults(ISolution solution, List<string> issueLines, string mode, int totalFiles, string outputPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Full Code Inspection Results (All Solution Files) ===");
            sb.AppendLine($"Scanned at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Solution: {solution.SolutionDirectory}");
            sb.AppendLine($"Mode: {mode}");
            sb.AppendLine($"Total files in scope: {totalFiles}");
            sb.AppendLine();

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

            File.WriteAllText(outputPath, sb.ToString());
        }
    }
}
