using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class InspectHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly FileIndexService _fileIndex;
    private readonly InspectionService _inspection;

    public InspectHandler(ISolution solution, FileIndexService fileIndex, InspectionService inspection)
    {
        _solution = solution;
        _fileIndex = fileIndex;
        _inspection = inspection;
    }

    public bool CanHandle(string path) => path == "/inspect";

    public void Handle(HttpListenerContext ctx)
    {
        var fileParams = ctx.Request.QueryString.GetValues("file");
        if (fileParams == null || fileParams.Length == 0)
        {
            HttpHelpers.Respond(ctx, 400, "text/plain",
                "Missing 'file' query parameter.\nUsage: /inspect?file=Source/Foo.cpp&file=Source/Bar.cpp");
            return;
        }

        var debug = HttpHelpers.IsDebug(ctx);
        var format = HttpHelpers.GetFormat(ctx);

        var solutionDir = _solution.SolutionDirectory;
        var fileIndex = _fileIndex.BuildFileIndex();
        var requestSw = Stopwatch.StartNew();

        var workItems = new List<(FileInspectionResult result, IPsiSourceFile source)>();
        var notFoundResults = new List<FileInspectionResult>();

        foreach (var fileParam in fileParams)
        {
            var key = FileIndexService.NormalizePath(fileParam);
            var result = new FileInspectionResult { RequestedPath = fileParam };

            IPsiSourceFile sourceFile;
            if (!fileIndex.TryGetValue(key, out sourceFile))
            {
                result.Error = "File not found in solution";
                notFoundResults.Add(result);
                continue;
            }

            result.ResolvedPath = sourceFile.GetLocation()
                .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
            workItems.Add((result, sourceFile));
        }

        // If ALL files are not found, fail early
        if (workItems.Count == 0)
        {
            var earlyTotalMs = (int)requestSw.ElapsedMilliseconds;
            var results = notFoundResults;
            RespondResults(ctx, format, debug, solutionDir.FullPath.Replace('\\', '/'),
                results, earlyTotalMs);
            return;
        }

        // Run inspections (PSI sync + daemon)
        _inspection.RunInspections(workItems);

        // Combine results in original request order
        var resultsByPath = workItems.Select(w => w.result)
            .Concat(notFoundResults)
            .ToDictionary(r => r.RequestedPath);
        var allResults = fileParams.Select(fp => resultsByPath[fp]).ToList();

        var totalMs = (int)requestSw.ElapsedMilliseconds;
        RespondResults(ctx, format, debug, solutionDir.FullPath.Replace('\\', '/'),
            allResults, totalMs);
    }

    private static void RespondResults(HttpListenerContext ctx, string format, bool debug,
        string solutionPath, List<FileInspectionResult> results, int totalMs)
    {
        if (format == "json")
        {
            var totalIssues = results.Sum(r => r.Issues.Count);
            var response = new InspectResponse
            {
                Solution = solutionPath,
                Files = results,
                TotalIssues = totalIssues,
                TotalFiles = results.Count,
                Debug = debug ? new InspectDebugInfo { TotalMs = totalMs } : null
            };
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(response));
        }
        else
        {
            var markdown = InspectMarkdownFormatter.Format(results, totalMs, debug);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }
}
