using System;
using System.Net;
using System.Reflection;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

/// <summary>
/// Debug endpoint that dumps the PSI tree structure for a file.
/// Usage: /debug_psi_tree?file=Source/MyFile.h&maxdepth=8&maxtext=120
/// </summary>
public class DebugPsiTreeHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly FileIndexService _fileIndex;

    public DebugPsiTreeHandler(ISolution solution, FileIndexService fileIndex)
    {
        _solution = solution;
        _fileIndex = fileIndex;
    }

    public bool CanHandle(string path) => path == "/debug_psi_tree";

    public void Handle(HttpListenerContext ctx)
    {
        var fileParam = ctx.Request.QueryString["file"];
        if (string.IsNullOrEmpty(fileParam))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain",
                "Missing 'file' query parameter.\n" +
                "Usage: /debug_psi_tree?file=Source/Foo.h&maxdepth=8&maxtext=120");
            return;
        }

        var maxDepth = 8;
        var maxText = 100;
        if (int.TryParse(ctx.Request.QueryString["maxdepth"], out var md) && md > 0)
            maxDepth = md;
        if (int.TryParse(ctx.Request.QueryString["maxtext"], out var mt) && mt > 0)
            maxText = mt;

        var key = FileIndexService.NormalizePath(fileParam);
        var fileIndex = _fileIndex.BuildFileIndex();

        if (!fileIndex.TryGetValue(key, out var sourceFile))
        {
            HttpHelpers.Respond(ctx, 404, "text/plain", "File not found in solution: " + fileParam);
            return;
        }

        var sb = new StringBuilder();

        try
        {
            ReadLockCookie.Execute(() =>
            {
                IProjectFile projectFile = null;
                foreach (var project in _solution.GetAllProjects())
                {
                    foreach (var pf in project.GetAllProjectFiles())
                    {
                        var sf = pf.ToSourceFile();
                        if (sf != null && sf.Equals(sourceFile))
                        {
                            projectFile = pf;
                            break;
                        }
                    }
                    if (projectFile != null) break;
                }

                if (projectFile == null)
                {
                    sb.AppendLine("ERROR: Could not find project file");
                    return;
                }

                var primaryFile = projectFile.GetPrimaryPsiFile();
                if (primaryFile == null)
                {
                    sb.AppendLine("ERROR: No PSI tree available");
                    return;
                }

                sb.Append("# PSI Tree: ").AppendLine(fileParam);
                sb.Append("Language: ").AppendLine(primaryFile.Language?.Name ?? "unknown");
                sb.Append("RootType: ").AppendLine(primaryFile.GetType().FullName);
                sb.Append("MaxDepth: ").Append(maxDepth).Append(", MaxText: ").AppendLine(maxText.ToString());
                sb.AppendLine();
                sb.AppendLine("```");

                WalkNode(primaryFile, sourceFile, sb, 0, maxDepth, maxText);

                sb.AppendLine("```");
            });
        }
        catch (Exception ex)
        {
            sb.Append("ERROR: ").Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
        }

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    private static void WalkNode(ITreeNode node, IPsiSourceFile sourceFile, StringBuilder sb,
        int depth, int maxDepth, int maxText)
    {
        var indent = new string(' ', depth * 2);
        var typeName = node.GetType().Name;

        // Basic info: type name
        sb.Append(indent).Append(typeName);

        // Offset info
        try
        {
            var offset = node.GetTreeStartOffset().Offset;
            sb.Append(" [off=").Append(offset);

            // Line number
            var doc = sourceFile.Document;
            if (doc != null)
            {
                var range = node.GetDocumentRange();
                if (range.IsValid())
                {
                    var docOffset = range.StartOffset.Offset;
                    if (docOffset >= 0 && docOffset <= doc.GetTextLength())
                    {
                        var line = (int)new DocumentOffset(doc, docOffset).ToDocumentCoords().Line + 1;
                        sb.Append(", line=").Append(line);
                    }
                }
            }

            sb.Append(']');
        }
        catch
        {
            sb.Append(" [off=?]");
        }

        // DeclaredElement info
        try
        {
            var prop = node.GetType().GetProperty("DeclaredElement",
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var elem = prop.GetValue(node) as IDeclaredElement;
                if (elem != null)
                {
                    sb.Append(" decl=").Append(elem.ShortName);
                    sb.Append(" (").Append(elem.GetType().Name).Append(')');
                }
            }
        }
        catch { }

        // PrevSibling type (useful for understanding sibling walk)
        try
        {
            var prev = node.PrevSibling;
            if (prev != null)
            {
                var prevName = prev.GetType().Name;
                // Skip whitespace-like siblings to show the meaningful one
                var prevPrev = prev;
                var steps = 0;
                while (prevPrev != null && steps < 5)
                {
                    var text = prevPrev.GetText();
                    if (text != null && !string.IsNullOrWhiteSpace(text))
                    {
                        prevName = prevPrev.GetType().Name;
                        break;
                    }
                    prevPrev = prevPrev.PrevSibling;
                    steps++;
                }
                sb.Append(" prev=").Append(prevName);
            }
        }
        catch { }

        // Node text (truncated)
        try
        {
            var text = node.GetText();
            if (text != null)
            {
                // Show length
                sb.Append(" len=").Append(text.Length);

                // Show truncated text on same line if short, or first line if multiline
                var displayText = text;
                var newlineIdx = displayText.IndexOf('\n');
                if (newlineIdx >= 0 && newlineIdx < maxText)
                    displayText = displayText.Substring(0, newlineIdx) + "...";
                if (displayText.Length > maxText)
                    displayText = displayText.Substring(0, maxText) + "...";
                displayText = displayText.Replace("\r", "").Replace("\n", "\\n");

                sb.Append(" text=|").Append(displayText).Append('|');
            }
        }
        catch { }

        sb.AppendLine();

        // Recurse into children (with depth limit)
        if (depth >= maxDepth)
        {
            // Count children to indicate there's more
            var childCount = 0;
            foreach (var _ in node.Children())
                childCount++;
            if (childCount > 0)
                sb.Append(indent).Append("  ... (").Append(childCount).AppendLine(" children, depth limit)");
            return;
        }

        foreach (var child in node.Children())
        {
            WalkNode(child, sourceFile, sb, depth + 1, maxDepth, maxText);
        }
    }
}
