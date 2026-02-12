using System;
using System.Net;
using System.Text;
using ReSharperPlugin.Fathom.Serialization;

namespace ReSharperPlugin.Fathom.Formatting;

public static class HttpHelpers
{
    public static void RespondWithFormat(HttpListenerContext ctx, string format, int statusCode,
        string markdownBody, object jsonBody)
    {
        if (format == "json")
            Respond(ctx, statusCode, "application/json; charset=utf-8", Json.Serialize(jsonBody));
        else
            Respond(ctx, statusCode, "text/markdown; charset=utf-8", markdownBody);
    }

    public static void Respond(HttpListenerContext ctx, int statusCode, string contentType, string body)
    {
        var buffer = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = buffer.Length;
        ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
        ctx.Response.OutputStream.Close();
    }

    public static string GetFormat(HttpListenerContext ctx)
    {
        var val = ctx.Request.QueryString["format"];
        if (val != null && val.Equals("json", StringComparison.OrdinalIgnoreCase))
            return "json";
        return "md";
    }

    public static bool IsDebug(HttpListenerContext ctx)
    {
        var val = ctx.Request.QueryString["debug"];
        return val != null &&
               (val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                val.Equals("1", StringComparison.OrdinalIgnoreCase));
    }

    public static string TruncateForJson(string s, int maxLength)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s;
        return s.Substring(0, maxLength) + "... [truncated]";
    }
}
