using System;
using System.Collections.Generic;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

public class FileIndexService
{
    private readonly ISolution _solution;

    public FileIndexService(ISolution solution)
    {
        _solution = solution;
    }

    public Dictionary<string, IPsiSourceFile> BuildFileIndex()
    {
        return ReadLockCookie.Execute(() =>
        {
            var solutionDir = _solution.SolutionDirectory;
            var index = new Dictionary<string, IPsiSourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in _solution.GetAllProjects())
            {
                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    var sourceFile = projectFile.ToSourceFile();
                    if (sourceFile == null) continue;

                    var path = sourceFile.GetLocation();
                    if (!path.StartsWith(solutionDir)) continue;

                    var key = NormalizePath(path.MakeRelativeTo(solutionDir).ToString());
                    if (!index.ContainsKey(key))
                        index[key] = sourceFile;
                }
            }
            return index;
        });
    }

    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
