using System;
using System.Collections.Generic;
using System.IO;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Services;

public class ClassIndexService
{
    private readonly ISolution _solution;
    private readonly FileIndexService _fileIndex;
    private readonly CodeStructureService _codeStructure;

    private static readonly HashSet<string> CppExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "h", "hpp", "cpp", "cc", "cxx" };

    private static readonly HashSet<string> HeaderExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "h", "hpp" };

    public ClassIndexService(ISolution solution, FileIndexService fileIndex,
        CodeStructureService codeStructure)
    {
        _solution = solution;
        _fileIndex = fileIndex;
        _codeStructure = codeStructure;
    }

    /// <summary>
    /// Build a list of game C++ classes from headers under Source/ (excluding Plugins/).
    /// </summary>
    /// <param name="search">Optional case-insensitive substring filter on class name.</param>
    /// <param name="baseClass">Optional exact match filter on base class name.</param>
    public List<ClassEntry> BuildClassIndex(string search = null, string baseClass = null)
    {
        var solutionDir = _solution.SolutionDirectory;
        var fileIndex = _fileIndex.BuildFileIndex();

        // Step 1: Filter to game C++ files and group by filename stem
        var stemGroups = new Dictionary<string, StemGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in fileIndex)
        {
            var normKey = kvp.Key; // already lowercased by FileIndexService

            if (!normKey.StartsWith("source/")) continue;
            if (normKey.Contains("/plugins/")) continue;

            var location = kvp.Value.GetLocation();
            var ext = location.ExtensionNoDot.ToLowerInvariant();
            if (!CppExtensions.Contains(ext)) continue;

            var relPath = location.MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(relPath);

            if (!stemGroups.TryGetValue(stem, out var group))
            {
                group = new StemGroup();
                stemGroups[stem] = group;
            }

            if (HeaderExtensions.Contains(ext))
            {
                group.HeaderPath = relPath;
                group.HeaderFile = kvp.Value;
            }
            else
            {
                group.SourcePath = relPath;
            }
        }

        // Step 2: For each group with a header, extract class info
        var hasSearch = !string.IsNullOrEmpty(search);
        var hasBase = !string.IsNullOrEmpty(baseClass);
        var classes = new List<ClassEntry>();

        foreach (var kvp in stemGroups)
        {
            var group = kvp.Value;
            if (group.HeaderFile == null) continue;

            var structure = _codeStructure.DescribeFile(
                group.HeaderFile, group.HeaderPath, false, null);

            var allTypes = CollectTypes(structure);

            foreach (var type in allTypes)
            {
                if (type.Kind != "class") continue;
                if (string.IsNullOrEmpty(type.Name)) continue;

                if (hasSearch &&
                    type.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (hasBase &&
                    !string.Equals(type.BaseType, baseClass, StringComparison.OrdinalIgnoreCase))
                    continue;

                classes.Add(new ClassEntry
                {
                    Name = type.Name,
                    Base = type.BaseType,
                    Header = group.HeaderPath,
                    Source = group.SourcePath
                });
            }
        }

        classes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return classes;
    }

    private static List<TypeInfo> CollectTypes(FileStructure structure)
    {
        var types = new List<TypeInfo>();

        if (structure.Types != null)
            types.AddRange(structure.Types);

        if (structure.Namespaces != null)
        {
            foreach (var ns in structure.Namespaces)
            {
                if (ns.Types != null)
                    types.AddRange(ns.Types);
            }
        }

        return types;
    }

    private class StemGroup
    {
        public string HeaderPath;
        public string SourcePath;
        public IPsiSourceFile HeaderFile;
    }
}
