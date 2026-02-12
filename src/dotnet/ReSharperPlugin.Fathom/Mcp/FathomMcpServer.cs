using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.Fathom.Serialization;

namespace ReSharperPlugin.Fathom.Mcp
{
    /// <summary>
    /// Implements the MCP (Model Context Protocol) JSON-RPC handler.
    /// Translates MCP tool calls into internal HTTP requests to Fathom REST endpoints.
    /// No external SDK needed; the protocol is simple JSON-RPC 2.0 over HTTP.
    /// </summary>
    public class FathomMcpServer
    {
        private const string ProtocolVersion = "2025-03-26";
        private const string ServerName = "fathom";
        private const string ServerVersion = "1.0.0";

        private readonly int _port;

        private static readonly ToolDef[] Tools =
        {
            // Source Code
            new ToolDef("list_solution_files",
                "List all source files in the solution.",
                "/files"),

            new ToolDef("list_cpp_classes",
                "List game C++ classes with header/source pairs and base class. Optional: search (name substring), base (filter by base class).",
                "/classes",
                new ToolParam("search", "string", "Name substring filter"),
                new ToolParam("base", "string", "Filter by base class (e.g. ACharacter)")),

            new ToolDef("describe_code",
                "Structural description of source file(s): functions, classes, members.",
                "/describe_code",
                new ToolParam("file", "string", "Relative file path (e.g. Source/MyActor.cpp)", required: true)),

            new ToolDef("inspect_code",
                "Run ReSharper code inspection on file(s). Returns issues with severity, location, and description.",
                "/inspect",
                new ToolParam("file", "string", "Relative file path (e.g. Source/MyActor.cpp)", required: true)),

            // Blueprints / UE5
            new ToolDef("find_derived_blueprints",
                "[UE5] List Blueprint classes deriving from a C++ class.",
                "/blueprints",
                new ToolParam("className", "string", "C++ class name (e.g. ACharacter)", required: true, queryParam: "class")),

            new ToolDef("get_blueprint_info",
                "[UE5] Blueprint composite info: audit data, dependencies, and referencers.",
                "/bp",
                new ToolParam("file", "string", "Blueprint package path (e.g. /Game/Blueprints/BP_Player)", required: true)),

            new ToolDef("get_blueprint_audit",
                "[UE5] Get cached Blueprint audit data. Returns 409 if stale, 503 if not ready.",
                "/blueprint-audit"),

            new ToolDef("refresh_blueprint_audit",
                "[UE5] Trigger background refresh of Blueprint audit data.",
                "/blueprint-audit/refresh"),

            new ToolDef("get_audit_status",
                "[UE5] Check status of Blueprint audit refresh.",
                "/blueprint-audit/status"),

            // Asset search / detail
            new ToolDef("search_assets",
                "[UE5] Search or browse UAssets. Provide a search term and/or filters. Search uses plain name substrings (space-separated, all must match; no wildcards or regex). To browse without searching, pass class and/or pathPrefix only. Requires live UE editor.",
                "/uassets",
                new ToolParam("search", "string", "Plain name substrings, space-separated, all must match (no wildcards/regex)"),
                new ToolParam("class", "string", "Filter by asset class (e.g. WidgetBlueprint)"),
                new ToolParam("pathPrefix", "string", "Path prefix filter (default: /Game; set empty to search all)"),
                new ToolParam("limit", "integer", "Maximum number of results")),

            new ToolDef("show_asset",
                "[UE5] Asset detail: registry metadata, disk size, tags, dependency/referencer counts. Requires live UE editor.",
                "/uassets/show",
                new ToolParam("package", "string", "Full package path (e.g. /Game/UI/WBP_MainMenu)", required: true)),

            new ToolDef("get_asset_dependencies",
                "[UE5] List what a given asset depends on. Requires live UE editor.",
                "/asset-refs/dependencies",
                new ToolParam("asset", "string", "Asset package path (e.g. /Game/UI/WBP_MainMenu)", required: true)),

            new ToolDef("get_asset_referencers",
                "[UE5] List what depends on a given asset. Requires live UE editor.",
                "/asset-refs/referencers",
                new ToolParam("asset", "string", "Asset package path (e.g. /Game/UI/WBP_MainMenu)", required: true)),

            // Diagnostics
            new ToolDef("get_ue_project_info",
                "UE project detection info and engine path.",
                "/ue-project"),

            new ToolDef("get_editor_status",
                "[UE5] UE editor connection status.",
                "/asset-refs/status"),
        };

        public FathomMcpServer(int port)
        {
            _port = port;
        }

        /// <summary>
        /// Process a JSON-RPC request body and return a JSON-RPC response string.
        /// Returns null for notifications (messages without an id).
        /// </summary>
        public string HandleJsonRpc(string requestBody)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(requestBody);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return JsonRpcError(null, -32700, "Parse error: " + ex.Message);
            }

            string method = null;
            if (root.TryGetProperty("method", out var methodEl))
                method = methodEl.GetString();

            if (method == null)
                return JsonRpcError(null, -32600, "Invalid request: missing method");

            // Notifications have no id and expect no response
            string idRaw = null;
            if (root.TryGetProperty("id", out var idEl))
                idRaw = idEl.GetRawText();

            if (idRaw == null)
                return null;

            try
            {
                object result;
                switch (method)
                {
                    case "initialize":
                        result = HandleInitialize();
                        break;
                    case "tools/list":
                        result = HandleToolsList();
                        break;
                    case "tools/call":
                        var paramsEl = root.TryGetProperty("params", out var p) ? p : default;
                        result = HandleToolsCall(paramsEl);
                        break;
                    case "ping":
                        result = new { };
                        break;
                    default:
                        return JsonRpcError(idRaw, -32601, "Method not found: " + method);
                }

                var resultJson = Json.Serialize(result);
                return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + resultJson + "}";
            }
            catch (Exception ex)
            {
                return JsonRpcError(idRaw, -32603, ex.Message);
            }
        }

        private static object HandleInitialize()
        {
            return new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = ServerName,
                    version = ServerVersion
                }
            };
        }

        private static object HandleToolsList()
        {
            var tools = new List<object>();
            foreach (var t in Tools)
            {
                var properties = new Dictionary<string, object>();
                var required = new List<string>();

                if (t.Params != null)
                {
                    foreach (var p in t.Params)
                    {
                        properties[p.Name] = new { type = p.Type, description = p.Description };
                        if (p.Required)
                            required.Add(p.Name);
                    }
                }

                tools.Add(new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = new
                    {
                        type = "object",
                        properties,
                        required = required.Count > 0 ? required.ToArray() : null
                    }
                });
            }

            return new { tools };
        }

        private object HandleToolsCall(JsonElement paramsEl)
        {
            string toolName = null;
            var argsEl = default(JsonElement);

            if (paramsEl.ValueKind != JsonValueKind.Undefined)
            {
                if (paramsEl.TryGetProperty("name", out var nameEl))
                    toolName = nameEl.GetString();
                paramsEl.TryGetProperty("arguments", out argsEl);
            }

            if (toolName == null)
                throw new ArgumentException("Missing tool name in params");

            var toolDef = Array.Find(Tools, t => t.Name == toolName);
            if (toolDef == null)
                throw new ArgumentException("Unknown tool: " + toolName);

            var url = BuildInternalUrl(toolDef, argsEl);

            string responseText;
            try
            {
                responseText = InternalHttpGet(url);
            }
            catch (Exception ex)
            {
                return new
                {
                    content = new[] { new { type = "text", text = "Error calling Fathom endpoint: " + ex.Message } },
                    isError = true
                };
            }

            return new
            {
                content = new[] { new { type = "text", text = responseText } }
            };
        }

        private string BuildInternalUrl(ToolDef tool, JsonElement argsEl)
        {
            var sb = new StringBuilder();
            sb.Append("http://localhost:").Append(_port).Append(tool.Endpoint);

            var firstParam = true;
            if (tool.Params != null && argsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in tool.Params)
                {
                    if (!argsEl.TryGetProperty(p.Name, out var val) || val.ValueKind == JsonValueKind.Null)
                        continue;

                    var queryKey = p.QueryParam ?? p.Name;
                    var valStr = val.ValueKind == JsonValueKind.String
                        ? val.GetString()
                        : val.GetRawText();

                    sb.Append(firstParam ? '?' : '&')
                      .Append(Uri.EscapeDataString(queryKey))
                      .Append('=')
                      .Append(Uri.EscapeDataString(valStr ?? ""));
                    firstParam = false;
                }
            }

            return sb.ToString();
        }

        private static string InternalHttpGet(string url)
        {
            using (var client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                try
                {
                    return client.DownloadString(url);
                }
                catch (WebException wex) when (wex.Response is HttpWebResponse httpResp)
                {
                    using (var reader = new StreamReader(httpResp.GetResponseStream(), Encoding.UTF8))
                    {
                        var body = reader.ReadToEnd();
                        return "HTTP " + (int)httpResp.StatusCode + ": " + body;
                    }
                }
            }
        }

        private static string JsonRpcError(string idRaw, int code, string message)
        {
            var escapedMessage = Json.Escape(message);
            var idPart = idRaw ?? "null";
            return "{\"jsonrpc\":\"2.0\",\"id\":" + idPart + ",\"error\":{\"code\":" + code + ",\"message\":\"" + escapedMessage + "\"}}";
        }

        private class ToolDef
        {
            public readonly string Name;
            public readonly string Description;
            public readonly string Endpoint;
            public readonly ToolParam[] Params;

            public ToolDef(string name, string description, string endpoint, params ToolParam[] parameters)
            {
                Name = name;
                Description = description;
                Endpoint = endpoint;
                Params = parameters.Length > 0 ? parameters : null;
            }
        }

        private class ToolParam
        {
            public readonly string Name;
            public readonly string Type;
            public readonly string Description;
            public readonly bool Required;
            public readonly string QueryParam;

            public ToolParam(string name, string type, string description,
                bool required = false, string queryParam = null)
            {
                Name = name;
                Type = type;
                Description = description;
                Required = required;
                QueryParam = queryParam;
            }
        }
    }
}
