using System.Collections.Generic;
using System.Text;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Formatting;

public static class ClassesMarkdownFormatter
{
    public static string Format(List<ClassEntry> classes, int port)
    {
        var sb = new StringBuilder();

        foreach (var cls in classes)
        {
            sb.Append("# ").AppendLine(cls.Name);

            if (cls.Base != null)
                sb.Append("extends ").AppendLine(cls.Base);

            sb.AppendLine();

            if (cls.Source != null)
                sb.Append("cpp: ").AppendLine(cls.Source);
            if (cls.Header != null)
                sb.Append("h: ").AppendLine(cls.Header);

            sb.AppendLine();

            var fileParams = BuildFileParams(cls.Header, cls.Source);

            sb.Append("[describe](http://localhost:").Append(port)
                .Append("/describe_code").Append(fileParams).AppendLine(")");
            sb.Append("[inspect](http://localhost:").Append(port)
                .Append("/inspect").Append(fileParams).AppendLine(")");
            sb.Append("[blueprints](http://localhost:").Append(port)
                .Append("/blueprints?class=").Append(cls.Name).AppendLine(")");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildFileParams(string header, string source)
    {
        var sb = new StringBuilder();
        var first = true;

        if (header != null)
        {
            sb.Append("?file=").Append(header);
            first = false;
        }

        if (source != null)
        {
            sb.Append(first ? "?file=" : "&file=").Append(source);
        }

        return sb.ToString();
    }
}
