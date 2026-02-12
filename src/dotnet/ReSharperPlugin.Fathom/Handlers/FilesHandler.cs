using System.Collections.Generic;
using System.Linq;
using System.Net;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class FilesHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly FileIndexService _fileIndex;

    public FilesHandler(ISolution solution, FileIndexService fileIndex)
    {
        _solution = solution;
        _fileIndex = fileIndex;
    }

    public bool CanHandle(string path) => path == "/files";

    public void Handle(HttpListenerContext ctx)
    {
        var solutionDir = _solution.SolutionDirectory;
        var fileIndex = _fileIndex.BuildFileIndex();

        var fileEntries = new List<FileEntry>();
        foreach (var kvp in fileIndex.OrderBy(x => x.Key))
        {
            var relPath = kvp.Value.GetLocation()
                .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
            var ext = kvp.Value.GetLocation().ExtensionNoDot.ToLowerInvariant();
            var lang = kvp.Value.LanguageType?.Name ?? "unknown";
            fileEntries.Add(new FileEntry
            {
                Path = relPath,
                Ext = ext,
                Language = lang
            });
        }

        var response = new FilesResponse
        {
            Solution = solutionDir.FullPath.Replace('\\', '/'),
            FileCount = fileEntries.Count,
            Files = fileEntries
        };

        HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(response));
    }
}
