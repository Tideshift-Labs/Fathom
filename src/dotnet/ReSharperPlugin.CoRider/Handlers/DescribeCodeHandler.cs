using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Models;
using ReSharperPlugin.CoRider.Serialization;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider.Handlers;

public class DescribeCodeHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly FileIndexService _fileIndex;
    private readonly CodeStructureService _codeStructure;

    public DescribeCodeHandler(ISolution solution, FileIndexService fileIndex,
        CodeStructureService codeStructure)
    {
        _solution = solution;
        _fileIndex = fileIndex;
        _codeStructure = codeStructure;
    }

    public bool CanHandle(string path) => path == "/describe_code";

    public void Handle(HttpListenerContext ctx)
    {
        var fileParams = ctx.Request.QueryString.GetValues("file");
        if (fileParams == null || fileParams.Length == 0)
        {
            HttpHelpers.Respond(ctx, 400, "text/plain",
                "Missing 'file' query parameter.\n" +
                "Usage: /describe_code?file=Source/Foo.cs&file=Source/Bar.h\n" +
                "Add &format=json for JSON output (default is markdown).");
            return;
        }

        var debug = HttpHelpers.IsDebug(ctx);
        var format = HttpHelpers.GetFormat(ctx);
        var solutionDir = _solution.SolutionDirectory;
        var fileIndex = _fileIndex.BuildFileIndex();
        var requestSw = Stopwatch.StartNew();
        var debugDiagnostics = debug ? new List<string>() : null;

        var results = new List<FileStructure>();

        foreach (var fileParam in fileParams)
        {
            var key = FileIndexService.NormalizePath(fileParam);
            IPsiSourceFile sourceFile;

            if (!fileIndex.TryGetValue(key, out sourceFile))
            {
                results.Add(new FileStructure
                {
                    RequestedPath = fileParam,
                    Error = "File not found in solution"
                });
                continue;
            }

            var resolvedPath = sourceFile.GetLocation()
                .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
            var structure = _codeStructure.DescribeFile(sourceFile, resolvedPath, debug, debugDiagnostics);
            structure.RequestedPath = fileParam;
            results.Add(structure);
        }

        var totalMs = (int)requestSw.ElapsedMilliseconds;

        if (format == "json")
        {
            var response = new DescribeCodeResponse
            {
                Solution = solutionDir.FullPath.Replace('\\', '/'),
                Files = results,
                TotalFiles = results.Count,
                Debug = debug ? new DescribeCodeDebugInfo
                {
                    TotalMs = totalMs,
                    Diagnostics = debugDiagnostics is { Count: > 0 } ? debugDiagnostics : null
                } : null
            };
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(response));
        }
        else
        {
            var markdown = DescribeCodeMarkdownFormatter.Format(results, totalMs, debug, debugDiagnostics);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }
}
