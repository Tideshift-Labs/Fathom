using System;
using System.Collections.Generic;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Services;

public class CodeStructureService
{
    private readonly ISolution _solution;

    public CodeStructureService(ISolution solution)
    {
        _solution = solution;
    }

    public FileStructure DescribeFile(IPsiSourceFile sourceFile, string resolvedPath,
        bool debug, List<string> debugDiagnostics)
    {
        var result = new FileStructure { ResolvedPath = resolvedPath };

        try
        {
            ReadLockCookie.Execute(() =>
            {
                // Find the IProjectFile for this source file so we can get the PSI tree
                var projectFile = FindProjectFile(sourceFile);
                if (projectFile == null)
                {
                    result.Error = "Could not find project file for source file";
                    return;
                }

                var primaryFile = projectFile.GetPrimaryPsiFile();
                if (primaryFile == null)
                {
                    result.Error = "No PSI tree available for this file";
                    return;
                }

                if (primaryFile is ICSharpFile csFile)
                {
                    CSharpStructureWalker.Walk(csFile, sourceFile, result);
                }
                else
                {
                    // Check if this is a C++ file by language name or PSI file type name
                    var langName = primaryFile.Language?.Name;
                    var fileTypeName = primaryFile.GetType().Name;

                    var isCpp = langName != null &&
                        (langName.IndexOf("CPP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         langName.IndexOf("C++", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         langName.Equals("MIXED_CPP_HEADER", StringComparison.OrdinalIgnoreCase));

                    // Also check the type name as a fallback
                    if (!isCpp)
                    {
                        isCpp = fileTypeName.IndexOf("Cpp", StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    // Also check file extension as last resort
                    if (!isCpp)
                    {
                        var ext = sourceFile.GetLocation().ExtensionNoDot.ToLowerInvariant();
                        isCpp = ext == "cpp" || ext == "h" || ext == "hpp" ||
                                ext == "cxx" || ext == "cc" || ext == "hxx" ||
                                ext == "hh" || ext == "inl";
                    }

                    if (isCpp)
                    {
                        CppStructureWalker.Walk(primaryFile, sourceFile, result, debugDiagnostics);
                    }
                    else
                    {
                        result.Language = langName ?? "unknown";
                        result.Error = "Language '" + result.Language +
                            "' is not yet supported by /describe_code. PSI file type: " + fileTypeName;
                    }
                }

                if (debug && debugDiagnostics != null)
                {
                    debugDiagnostics.Add("File '" + resolvedPath + "': language=" +
                        primaryFile.Language?.Name + ", psiFileType=" + primaryFile.GetType().Name);
                }
            });
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }

        // Null out empty collections so they're omitted from JSON
        NullIfEmpty(result);
        return result;
    }

    private IProjectFile FindProjectFile(IPsiSourceFile sourceFile)
    {
        foreach (var project in _solution.GetAllProjects())
        {
            foreach (var pf in project.GetAllProjectFiles())
            {
                var sf = pf.ToSourceFile();
                if (sf != null && sf.Equals(sourceFile))
                    return pf;
            }
        }
        return null;
    }

    private static void NullIfEmpty(FileStructure fs)
    {
        if (fs.Namespaces is { Count: 0 }) fs.Namespaces = null;
        if (fs.Types is { Count: 0 }) fs.Types = null;
        if (fs.FreeFunctions is { Count: 0 }) fs.FreeFunctions = null;
        if (fs.Includes is { Count: 0 }) fs.Includes = null;
    }
}
