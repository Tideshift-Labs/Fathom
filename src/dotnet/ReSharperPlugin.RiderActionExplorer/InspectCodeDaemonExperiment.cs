// =============================================================================
// InspectCodeDaemonExperiment.cs — THE BREAKTHROUGH (Active)
// =============================================================================
// GOAL PROGRESS: YES — This is the proven path forward for C++ inspection.
//
// What it does:
//   Uses the public InspectCodeDaemon class (from JetBrains.ReSharper.Daemon.
//   SolutionAnalysis.InspectCode) to run code inspections on C++ files WITHOUT
//   requiring them to be open in the editor. This is the same class used by
//   inspectcode.exe (JetBrains' command-line inspection tool).
//
// How it works:
//   1. Collects user C++ source files under the solution directory
//   2. For each file, creates an InspectCodeDaemon(issueClasses, file, fileImages)
//   3. Calls daemon.DoHighlighting(DaemonProcessKind.OTHER, callback)
//   4. The callback receives IIssue objects with severity, message, and offset
//
// Why it works (when RunLocalInspections doesn't):
//   InspectCodeDaemon wraps DoHighlighting() in FileImages.DisableCheckThread()
//   and CompilationContextCookie.GetOrCreate(). The DisableCheckThread() call is
//   the critical difference that allows C++ daemon stages to execute from
//   background threads. The private InspectionDaemon used by RunLocalInspections
//   lacks this wrapper, causing 0 callbacks for C++ files.
//
// Results:
//   - 34 real C++ issues found across 19 files (Clang-Tidy, symbol resolution, etc.)
//   - No open editor tabs required
//   - No SWEA dependency
//
// Known issue:
//   .h files may throw OperationCanceledException, likely from the
//   UnrealHeaderToolDaemonProcess stage which runs an external UBT process.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JetBrains.Application.Parts;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.SolutionAnalysis;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Issues;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using FileImages = JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages;

namespace ReSharperPlugin.RiderActionExplorer
{
    // Disabled: auto-run replaced by InspectionHttpServer
    // [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class InspectCodeDaemonExperiment
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<InspectCodeDaemonExperiment>();

        public InspectCodeDaemonExperiment(Lifetime lifetime, ISolution solution)
        {
            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-inspectcodedaemon-experiment.txt");

            try
            {
                File.WriteAllText(outputPath,
                    $"=== InspectCodeDaemonExperiment loaded ===\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Solution: {solution.SolutionDirectory}\n");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InspectCodeDaemonExperiment: failed to write marker");
            }

            var thread = new Thread(() => Run(lifetime, solution, outputPath))
            {
                IsBackground = true,
                Name = "InspectCodeDaemonExperiment"
            };
            thread.Start();
        }

        private static void Run(Lifetime lifetime, ISolution solution, string outputPath)
        {
            // Wait for indexing to settle
            Thread.Sleep(30000);
            if (lifetime.IsNotAlive) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== InspectCodeDaemonExperiment ===");
                sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Solution: {solution.SolutionDirectory}");
                sb.AppendLine();

                // Collect user C++ files under solution directory
                var solutionDir = solution.SolutionDirectory;
                var cppExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "cpp", "h", "hpp", "cc", "cxx", "hxx", "inl" };
                var userCppFiles = new List<IPsiSourceFile>();

                foreach (var project in solution.GetAllProjects())
                {
                    foreach (var projectFile in project.GetAllProjectFiles())
                    {
                        var sourceFile = projectFile.ToSourceFile();
                        if (sourceFile == null) continue;
                        var path = sourceFile.GetLocation();
                        if (!path.StartsWith(solutionDir)) continue;
                        var ext = path.ExtensionNoDot.ToLowerInvariant();
                        if (cppExtensions.Contains(ext))
                            userCppFiles.Add(sourceFile);
                    }
                }

                sb.AppendLine($"User C++ files found: {userCppFiles.Count}");
                sb.AppendLine();

                // Get required components
                IssueClasses issueClasses;
                FileImages fileImages;

                try
                {
                    issueClasses = solution.GetComponent<IssueClasses>();
                    sb.AppendLine("IssueClasses: obtained");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"IssueClasses: FAILED - {ex.Message}");
                    File.WriteAllText(outputPath, sb.ToString());
                    return;
                }

                try
                {
                    fileImages = FileImages.GetInstance(solution);
                    sb.AppendLine("FileImages: obtained");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"FileImages: FAILED - {ex.Message}");
                    File.WriteAllText(outputPath, sb.ToString());
                    return;
                }

                sb.AppendLine();
                sb.AppendLine("== Running InspectCodeDaemon on user C++ files ==");
                sb.AppendLine();

                // Run InspectCodeDaemon on each file (limit to first 20 for experiment)
                var allIssues = new List<string>();
                var filesProcessed = 0;
                var filesWithErrors = 0;
                var startTime = DateTime.Now;

                foreach (var file in userCppFiles.Take(20))
                {
                    if (lifetime.IsNotAlive) break;

                    var relPath = file.GetLocation().MakeRelativeTo(solutionDir);
                    try
                    {
                        var fileIssues = new List<string>();
                        var daemon = new InspectCodeDaemon(issueClasses, file, fileImages);
                        daemon.DoHighlighting(DaemonProcessKind.OTHER, issue =>
                        {
                            var severity = issue.GetSeverity().ToString().ToUpperInvariant();
                            var message = issue.Message;
                            var line = 0;

                            try
                            {
                                var doc = file.Document;
                                if (doc != null && issue.Range.HasValue)
                                {
                                    var offset = issue.Range.Value.StartOffset;
                                    if (offset >= 0 && offset <= doc.GetTextLength())
                                        line = (int)new DocumentOffset(doc, offset).ToDocumentCoords().Line + 1;
                                }
                            }
                            catch { /* ignore offset errors */ }

                            fileIssues.Add($"[{severity}] {relPath}:{line} - {message}");
                        });

                        filesProcessed++;
                        allIssues.AddRange(fileIssues);
                        sb.AppendLine($"  {relPath}: {fileIssues.Count} issues");
                    }
                    catch (Exception ex)
                    {
                        filesWithErrors++;
                        sb.AppendLine($"  {relPath}: ERROR - {ex.GetType().Name}: {ex.Message}");
                    }
                }

                var elapsed = DateTime.Now - startTime;
                sb.AppendLine();
                sb.AppendLine($"Completed in {elapsed.TotalSeconds:F1}s");
                sb.AppendLine($"Files processed: {filesProcessed}");
                sb.AppendLine($"Files with errors: {filesWithErrors}");
                sb.AppendLine($"Total issues: {allIssues.Count}");
                sb.AppendLine();

                foreach (var issue in allIssues.OrderBy(i => i))
                    sb.AppendLine(issue);

                File.WriteAllText(outputPath, sb.ToString());
                Log.Warn("InspectCodeDaemonExperiment: completed, wrote results");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InspectCodeDaemonExperiment failed");
                try
                {
                    File.WriteAllText(outputPath,
                        $"=== InspectCodeDaemonExperiment FAILED ===\n" +
                        $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Error: {ex.Message}\n\n{ex.StackTrace}\n");
                }
                catch { /* ignore */ }
            }
        }
    }
}
