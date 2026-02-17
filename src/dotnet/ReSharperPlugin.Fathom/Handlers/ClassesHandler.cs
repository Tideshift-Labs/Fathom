using System.Net;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class ClassesHandler : IRequestHandler
{
    private readonly ClassIndexService _classIndex;
    private readonly ServerConfiguration _config;

    public ClassesHandler(ClassIndexService classIndex, ServerConfiguration config)
    {
        _classIndex = classIndex;
        _config = config;
    }

    public bool CanHandle(string path) => path == "/classes";

    public void Handle(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var search = ctx.Request.QueryString["search"];
        var baseClass = ctx.Request.QueryString["base"];
        var classes = _classIndex.BuildClassIndex(search, baseClass);

        if (format == "json")
        {
            var response = new ClassesResponse { Classes = classes };
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8",
                Json.Serialize(response));
        }
        else
        {
            var isMcp = ctx.Request.QueryString["source"] == "mcp";
            var markdown = ClassesMarkdownFormatter.Format(classes, _config.Port, includeLinks: !isMcp);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }
}
