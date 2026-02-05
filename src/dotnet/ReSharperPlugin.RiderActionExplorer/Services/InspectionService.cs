using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.SolutionAnalysis;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Issues;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using ReSharperPlugin.RiderActionExplorer.Models;
using FileImages = JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages;

namespace ReSharperPlugin.RiderActionExplorer.Services;

public class InspectionService
{
    private readonly ISolution _solution;
    private readonly PsiSyncService _psiSync;
    private readonly ServerConfiguration _config;

    public InspectionService(ISolution solution, PsiSyncService psiSync, ServerConfiguration config)
    {
        _solution = solution;
        _psiSync = psiSync;
        _config = config;
    }

    public List<FileInspectionResult> RunInspections(
        List<(FileInspectionResult result, IPsiSourceFile source)> workItems)
    {
        IssueClasses issueClasses;
        FileImages fileImages;
        issueClasses = _solution.GetComponent<IssueClasses>();
        fileImages = FileImages.GetInstance(_solution);

        // Step A: Wait for PSI sync on all files in parallel
        Parallel.ForEach(workItems, item =>
        {
            item.result.SyncResult = _psiSync.WaitForPsiSync(item.source);
            if (item.result.SyncResult.Status == "timeout")
                item.result.Error = "PSI sync timeout: document does not match disk content after " +
                                    item.result.SyncResult.WaitedMs + "ms";
            else if (item.result.SyncResult.Status == "disk_read_error")
                item.result.Error = "Cannot read file from disk: " + item.result.SyncResult.Message;
        });

        // Step B: Run inspections in parallel with retry on OperationCanceledException
        var inspectableItems = workItems.Where(item => item.result.Error == null).ToList();
        var maxRetries = _config.MaxInspectionRetries;
        var retryDelayMs = _config.RetryDelayMs;
        Parallel.ForEach(inspectableItems, item =>
        {
            var result = item.result;
            var sourceFile = item.source;

            var inspectSw = Stopwatch.StartNew();
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                result.Retries = attempt - 1;
                result.Issues.Clear();
                result.Error = null;

                try
                {
                    var daemon = new InspectCodeDaemon(issueClasses, sourceFile, fileImages);
                    daemon.DoHighlighting(DaemonProcessKind.OTHER, issue =>
                    {
                        var severity = issue.GetSeverity().ToString().ToUpperInvariant();
                        var message = issue.Message ?? "";
                        var line = 0;

                        try
                        {
                            var doc = sourceFile.Document;
                            if (doc != null && issue.Range.HasValue)
                            {
                                var offset = issue.Range.Value.StartOffset;
                                if (offset >= 0 && offset <= doc.GetTextLength())
                                    line = (int)new DocumentOffset(doc, offset)
                                        .ToDocumentCoords().Line + 1;
                            }
                        }
                        catch { /* ignore offset errors */ }

                        result.Issues.Add(new InspectionIssue
                        {
                            Severity = severity,
                            Line = line,
                            Message = message
                        });
                    });

                    break;
                }
                catch (OperationCanceledException) when (attempt < maxRetries)
                {
                    Thread.Sleep(retryDelayMs);
                }
                catch (Exception ex)
                {
                    result.Error = ex.GetType().Name + ": " + ex.Message;
                    break;
                }
            }
            result.InspectionMs = (int)inspectSw.ElapsedMilliseconds;
        });

        return workItems.Select(w => w.result).ToList();
    }
}
