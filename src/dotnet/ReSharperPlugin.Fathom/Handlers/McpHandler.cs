using System.IO;
using System.Net;
using System.Text;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Mcp;

namespace ReSharperPlugin.Fathom.Handlers
{
    /// <summary>
    /// Handles MCP (Model Context Protocol) Streamable HTTP requests on POST /mcp.
    /// Bridges HttpListenerContext to the FathomMcpServer JSON-RPC handler.
    /// </summary>
    public class McpHandler : IRequestHandler
    {
        private readonly FathomMcpServer _mcp;

        public McpHandler(FathomMcpServer mcp)
        {
            _mcp = mcp;
        }

        public bool CanHandle(string path) => path == "/mcp";

        public void Handle(HttpListenerContext ctx)
        {
            // CORS headers for browser-based MCP clients
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Accept, Mcp-Session-Id");
            ctx.Response.AddHeader("Access-Control-Expose-Headers", "Mcp-Session-Id");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
                return;
            }

            if (ctx.Request.HttpMethod != "POST")
            {
                HttpHelpers.Respond(ctx, 405, "text/plain", "Method not allowed. Use POST for MCP JSON-RPC requests.");
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            var response = _mcp.HandleJsonRpc(body);

            if (response == null)
            {
                // JSON-RPC notification (no id), acknowledge without body
                ctx.Response.StatusCode = 202;
                ctx.Response.OutputStream.Close();
                return;
            }

            var buffer = Encoding.UTF8.GetBytes(response);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
