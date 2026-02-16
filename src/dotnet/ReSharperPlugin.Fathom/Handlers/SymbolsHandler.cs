using System;
using System.Net;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class SymbolsHandler : IRequestHandler
{
    private readonly SymbolSearchService _symbolSearch;
    private readonly ServerConfiguration _config;

    public SymbolsHandler(SymbolSearchService symbolSearch, ServerConfiguration config)
    {
        _symbolSearch = symbolSearch;
        _config = config;
    }

    public bool CanHandle(string path) =>
        path == "/symbols" || path == "/symbols/declaration" || path == "/symbols/inheritors";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
        var format = HttpHelpers.GetFormat(ctx);

        if (path == "/symbols")
            HandleSearch(ctx, format);
        else if (path == "/symbols/declaration")
            HandleDeclaration(ctx, format);
        else if (path == "/symbols/inheritors")
            HandleInheritors(ctx, format);
    }

    private void HandleSearch(HttpListenerContext ctx, string format)
    {
        var query = ctx.Request.QueryString["query"];
        if (string.IsNullOrWhiteSpace(query))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: query");
            return;
        }

        var kind = ctx.Request.QueryString["kind"];
        var scope = ctx.Request.QueryString["scope"];
        var limitStr = ctx.Request.QueryString["limit"];
        var limit = 50;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = parsedLimit;

        var response = _symbolSearch.SearchByName(query, kind, scope, limit);

        if (format == "json")
        {
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8",
                Json.Serialize(response));
        }
        else
        {
            var markdown = SymbolsMarkdownFormatter.FormatSearch(response);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }

    private void HandleDeclaration(HttpListenerContext ctx, string format)
    {
        var symbol = ctx.Request.QueryString["symbol"];
        if (string.IsNullOrWhiteSpace(symbol))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: symbol");
            return;
        }

        var containingType = ctx.Request.QueryString["containingType"];
        var kind = ctx.Request.QueryString["kind"];
        var contextLinesStr = ctx.Request.QueryString["context_lines"];
        var contextLines = 4;
        if (int.TryParse(contextLinesStr, out var parsedContext) && parsedContext >= 0)
            contextLines = parsedContext;

        var response = _symbolSearch.GetDeclaration(symbol, containingType, kind, contextLines);

        if (format == "json")
        {
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8",
                Json.Serialize(response));
        }
        else
        {
            var markdown = SymbolsMarkdownFormatter.FormatDeclaration(response);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }

    private void HandleInheritors(HttpListenerContext ctx, string format)
    {
        var symbol = ctx.Request.QueryString["symbol"];
        if (string.IsNullOrWhiteSpace(symbol))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain", "Missing required parameter: symbol");
            return;
        }

        var scope = ctx.Request.QueryString["scope"];
        var limitStr = ctx.Request.QueryString["limit"];
        var limit = 100;
        if (int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            limit = parsedLimit;

        var response = _symbolSearch.FindInheritors(symbol, scope, limit);

        if (format == "json")
        {
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8",
                Json.Serialize(response));
        }
        else
        {
            var markdown = SymbolsMarkdownFormatter.FormatInheritors(response);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }
}
