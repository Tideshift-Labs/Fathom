#!/usr/bin/env node
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import fs from "fs";
import path from "path";

// Configuration
const DEFAULT_PORT = 19876;

/**
 * Discovers the CoRider port by looking for .corider-server.json
 * in the current directory or its parents.
 */
function discoverPort() {
  let curr = process.cwd();
  while (curr) {
    const configPath = path.join(curr, ".corider-server.json");
    if (fs.existsSync(configPath)) {
      try {
        const config = JSON.parse(fs.readFileSync(configPath, "utf-8"));
        if (config.port) return config.port;
      } catch (e) {
        // Ignore parse errors
      }
    }
    const parent = path.dirname(curr);
    if (parent === curr) break;
    curr = parent;
  }
  return DEFAULT_PORT;
}

/**
 * Helper to fetch text from the local API
 */
async function fetchText(endpoint) {
  const port = discoverPort();
  const apiBase = `http://localhost:${port}`;
  try {
    const response = await fetch(`${apiBase}${endpoint}`);
    if (!response.ok) {
      throw new Error(`CoRider API Error: ${response.status} ${response.statusText} (on port ${port})`);
    }
    return await response.text();
  } catch (error) {
    if (error.cause?.code === "ECONNREFUSED") {
      return `Error: Could not connect to CoRider on port ${port}. Is Rider open with this solution loaded?`;
    }
    throw error;
  }
}

/**
 * Define the server
 */
const server = new Server(
  {
    name: "corider-mcp",
    version: "1.0.0",
  },
  {
    capabilities: {
      tools: {},
    },
  }
);

/**
 * List available tools
 */
server.setRequestHandler(ListToolsRequestSchema, async () => {
  return {
    tools: [
      {
        name: "list_solution_files",
        description: "List all source files in the currently open Rider solution.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "inspect_code",
        description: "Run ReSharper code inspections on a specific file or files. Returns issues like errors, warnings, and suggestions.",
        inputSchema: {
          type: "object",
          properties: {
            file: {
              type: "string",
              description: "Relative path to the file to inspect (e.g., 'Source/MyActor.cpp').",
            },
          },
          required: ["file"],
        },
      },
      {
        name: "check_server_health",
        description: "Check if the CoRider plugin is running and responsive.",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
      {
        name: "find_derived_blueprints",
        description: "Find Unreal Engine Blueprint classes that derive from a specific C++ class or Blueprint.",
        inputSchema: {
          type: "object",
          properties: {
            className: {
              type: "string",
              description: "The name of the base class (e.g., 'MyActor', 'BP_Player').",
            },
          },
          required: ["className"],
        },
      },
      {
        name: "get_blueprint_audit_status",
        description: "Check the status of the background Blueprint audit (Unreal Engine projects only).",
        inputSchema: {
          type: "object",
          properties: {},
        },
      },
    ],
  };
});

/**
 * Handle tool execution
 */
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  try {
    switch (name) {
      case "list_solution_files": {
        const text = await fetchText("/files");
        return {
          content: [{ type: "text", text }],
        };
      }

      case "inspect_code": {
        // Support inspecting multiple files if needed, but tool def assumes one for simplicity
        const path = args.file;
        const text = await fetchText(`/inspect?file=${encodeURIComponent(path)}`);
        return {
          content: [{ type: "text", text }],
        };
      }

      case "check_server_health": {
        const text = await fetchText("/health");
        return {
          content: [{ type: "text", text }],
        };
      }

      case "find_derived_blueprints": {
        const text = await fetchText(`/blueprints?class=${encodeURIComponent(args.className)}`);
        return {
          content: [{ type: "text", text }],
        };
      }

      case "get_blueprint_audit_status": {
        const text = await fetchText("/blueprint-audit/status");
        return {
          content: [{ type: "text", text }],
        };
      }

      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  } catch (error) {
    return {
      content: [
        {
          type: "text",
          text: `Error executing tool '${name}': ${error.message}`,
        },
      ],
      isError: true,
    };
  }
});

// Start the server
const transport = new StdioServerTransport();
await server.connect(transport);
