// =============================================================================
// CppInspectionExperiment.cs — DEAD END (Disabled)
// =============================================================================
// GOAL PROGRESS: NO — RunLocalInspections does not work for C++ files.
//
// What it does:
//   Comprehensive diagnostic experiment that inventories all solution projects
//   and files, filters to user C++/C# source files, inspects C++ PSI properties,
//   then runs CollectInspectionResults.RunLocalInspections() on both C++ and C#
//   files separately.
//
// How it works:
//   1. Phase 1: Inventories all projects and files with extension breakdowns
//   2. Phase 2: Filters to user files under solution directory (~231 C++ files)
//   3. Phase 3: Dumps C++ PSI diagnostics (LanguageType, ShouldBuildPsi, etc.)
//   4. Phase 4: Runs RunLocalInspections on C++ files
//   5. Phase 5: Runs RunLocalInspections on C# files
//
// Why it doesn't help:
//   RunLocalInspections uses a private InspectionDaemon that goes through
//   DaemonProcessBase.DoHighlighting() but does NOT wrap in
//   FileImages.DisableCheckThread(). C++ daemon stages silently produce 0
//   results. C# works fine (88 issues on a C# project), but C++ gets 0 callbacks.
//
// Value: Proved that C++ PSI is healthy (ShouldBuildPsi=true, documents present)
//   and that the problem is specifically in how RunLocalInspections invokes the
//   daemon, not in the PSI model itself.
//
// Superseded by: InspectCodeDaemonExperiment.cs
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
    // Disabled: superseded by InspectCodeDaemonExperiment
    // [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class CppInspectionExperiment
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<CppInspectionExperiment>();

        public CppInspectionExperiment(Lifetime lifetime, ISolution solution)
        {
            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "resharper-cpp-experiment.txt");

            try
            {
                File.WriteAllText(outputPath,
                    $"=== CppInspectionExperiment loaded ===\n" +
                    $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Solution: {solution.SolutionDirectory}\n");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CppInspectionExperiment: failed to write marker");
            }

            var thread = new Thread(() => Run(lifetime, solution, outputPath))
            {
                IsBackground = true,
                Name = "CppInspectionExperiment"
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
                sb.AppendLine("=== CppInspectionExperiment ===");
                sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Solution: {solution.SolutionDirectory}");
                sb.AppendLine();

                // ── Phase 1: Inventory all projects and files ──
                sb.AppendLine("== Phase 1: Project & File Inventory ==");
                sb.AppendLine();

                var allFiles = new List<IPsiSourceFile>();
                var filesByProject = new Dictionary<string, List<IPsiSourceFile>>();
                var projectTypes = new Dictionary<string, string>();

                foreach (var project in solution.GetAllProjects())
                {
                    var projectName = project.Name;
                    var projectKind = project.ProjectProperties?.ProjectKind.ToString() ?? "Unknown";
                    var projectGuid = project.Guid.ToString();
                    projectTypes[projectName] = projectKind;

                    var projectFiles = new List<IPsiSourceFile>();
                    foreach (var projectFile in project.GetAllProjectFiles())
                    {
                        var sourceFile = projectFile.ToSourceFile();
                        if (sourceFile != null)
                            projectFiles.Add(sourceFile);
                    }

                    filesByProject[projectName] = projectFiles;
                    allFiles.AddRange(projectFiles);
                }

                sb.AppendLine($"Total projects: {filesByProject.Count}");
                sb.AppendLine($"Total source files: {allFiles.Count}");
                sb.AppendLine();

                // List projects with file counts and types
                foreach (var kvp in filesByProject.OrderByDescending(x => x.Value.Count).Take(30))
                {
                    var extBreakdown = kvp.Value
                        .GroupBy(f => f.GetLocation().ExtensionNoDot.ToLowerInvariant())
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Key}={g.Count()}")
                        .ToArray();
                    string projectType;
                    if (!projectTypes.TryGetValue(kvp.Key, out projectType)) projectType = "?";
                    sb.AppendLine($"  [{projectType}] {kvp.Key}: " +
                                  $"{kvp.Value.Count} files ({string.Join(", ", extBreakdown.Take(8))})");
                }

                if (filesByProject.Count > 30)
                    sb.AppendLine($"  ... and {filesByProject.Count - 30} more projects");
                sb.AppendLine();

                // Global extension breakdown
                var globalExtBreakdown = allFiles
                    .GroupBy(f => f.GetLocation().ExtensionNoDot.ToLowerInvariant())
                    .OrderByDescending(g => g.Count())
                    .ToList();

                sb.AppendLine("File type breakdown (all projects):");
                foreach (var g in globalExtBreakdown.Take(20))
                    sb.AppendLine($"  .{g.Key}: {g.Count()}");
                sb.AppendLine();

                // ── Phase 2: Filter to user source files only ──
                sb.AppendLine("== Phase 2: Filtering to User Source Files ==");
                sb.AppendLine();

                var solutionDir = solution.SolutionDirectory;
                var userCppFiles = new List<IPsiSourceFile>();
                var userCsFiles = new List<IPsiSourceFile>();
                var cppExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "cpp", "h", "hpp", "cc", "cxx", "hxx", "inl" };

                foreach (var file in allFiles)
                {
                    var path = file.GetLocation();
                    var ext = path.ExtensionNoDot.ToLowerInvariant();

                    // Skip files outside the solution directory (engine, third-party)
                    if (!path.StartsWith(solutionDir))
                        continue;

                    if (cppExtensions.Contains(ext))
                        userCppFiles.Add(file);
                    else if (ext == "cs")
                        userCsFiles.Add(file);
                }

                sb.AppendLine($"User C++ files (under solution dir): {userCppFiles.Count}");
                foreach (var f in userCppFiles.Take(50))
                    sb.AppendLine($"  {f.GetLocation().MakeRelativeTo(solutionDir)}");
                if (userCppFiles.Count > 50)
                    sb.AppendLine($"  ... and {userCppFiles.Count - 50} more");
                sb.AppendLine();

                sb.AppendLine($"User C# files (under solution dir): {userCsFiles.Count}");
                foreach (var f in userCsFiles.Take(20))
                    sb.AppendLine($"  {f.GetLocation().MakeRelativeTo(solutionDir)}");
                sb.AppendLine();

                // ── Phase 3: Inspect C++ file properties ──
                sb.AppendLine("== Phase 3: C++ File PSI Diagnostics ==");
                sb.AppendLine();

                foreach (var file in userCppFiles.Take(10))
                {
                    var relPath = file.GetLocation().MakeRelativeTo(solutionDir);
                    sb.AppendLine($"  File: {relPath}");
                    sb.AppendLine($"    LanguageType: {file.LanguageType}");
                    sb.AppendLine($"    PsiModule: {file.PsiModule?.Name ?? "NULL"}");
                    sb.AppendLine($"    IsValid: {file.IsValid()}");
                    sb.AppendLine($"    Document: {(file.Document != null ? "present" : "NULL")}");
                    if (file.Document != null)
                        sb.AppendLine($"    DocLength: {file.Document.GetTextLength()}");

                    try
                    {
                        var properties = file.Properties;
                        sb.AppendLine($"    ShouldBuildPsi: {properties.ShouldBuildPsi}");
                        sb.AppendLine($"    IsNonUserFile: {properties.IsNonUserFile}");
                        sb.AppendLine($"    ProvidesCodeModel: {properties.ProvidesCodeModel}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"    Properties error: {ex.Message}");
                    }

                    sb.AppendLine();
                }

                // ── Phase 4: Run inspections on user C++ files only ──
                sb.AppendLine("== Phase 4: RunLocalInspections on User C++ Files ==");
                sb.AppendLine();

                if (userCppFiles.Count > 0)
                {
                    var cppIssues = RunInspectionsOn(lifetime, solution, userCppFiles, sb);
                    sb.AppendLine($"C++ issues found: {cppIssues.Count}");
                    foreach (var issue in cppIssues.Take(50))
                        sb.AppendLine($"  {issue}");
                    if (cppIssues.Count > 50)
                        sb.AppendLine($"  ... and {cppIssues.Count - 50} more");
                }
                else
                {
                    sb.AppendLine("No user C++ files found to inspect.");
                }
                sb.AppendLine();

                // ── Phase 5: Run inspections on user C# files only ──
                sb.AppendLine("== Phase 5: RunLocalInspections on User C# Files ==");
                sb.AppendLine();

                if (userCsFiles.Count > 0)
                {
                    var csIssues = RunInspectionsOn(lifetime, solution, userCsFiles, sb);
                    sb.AppendLine($"C# issues found: {csIssues.Count}");
                    foreach (var issue in csIssues.Take(50))
                        sb.AppendLine($"  {issue}");
                    if (csIssues.Count > 50)
                        sb.AppendLine($"  ... and {csIssues.Count - 50} more");
                }
                else
                {
                    sb.AppendLine("No user C# files found to inspect.");
                }

                File.WriteAllText(outputPath, sb.ToString());
                Log.Warn("CppInspectionExperiment: completed, wrote results");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CppInspectionExperiment failed");
                try
                {
                    File.WriteAllText(outputPath,
                        $"=== CppInspectionExperiment FAILED ===\n" +
                        $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"Error: {ex.Message}\n\n{ex.StackTrace}\n");
                }
                catch { /* ignore */ }
            }
        }

        private static List<string> RunInspectionsOn(
            Lifetime lifetime, ISolution solution,
            List<IPsiSourceFile> files, StringBuilder diagLog)
        {
            var issueLines = new List<string>();
            var callbackCount = 0;
            var solutionDir = solution.SolutionDirectory;

            var lifetimeDef = lifetime.CreateNested();
            var progress = new ProgressIndicator(lifetimeDef.Lifetime);
            var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
            var stack = new Stack<IPsiSourceFile>(files);

            diagLog.AppendLine($"  Running on {files.Count} files...");
            var startTime = DateTime.Now;

            runner.RunLocalInspections(stack, (file, issues) =>
            {
                callbackCount++;
                if (issues != null)
                {
                    foreach (var issue in issues)
                    {
                        if (!issue.IsValid) continue;
                        var severity = issue.GetSeverity().ToString().ToUpperInvariant();
                        var filePath = file.GetLocation().MakeRelativeTo(solutionDir);
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
                        catch { /* ignore */ }

                        issueLines.Add($"[{severity}] {filePath}:{line} - {issue.Message}");
                    }
                }
            }, null);

            var elapsed = DateTime.Now - startTime;
            diagLog.AppendLine($"  Completed in {elapsed.TotalSeconds:F1}s, callbacks={callbackCount}");

            lifetimeDef.Terminate();
            return issueLines;
        }
    }
}
