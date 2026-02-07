# CoRider MCP Bridge

This is a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that allows LLMs (like Claude, Gemini, or Cursor) to interact directly with your Rider instance via the CoRider plugin.

## Features

Exposes the following tools to your AI assistant:
- `list_solution_files`: See every file in your current solution.
- `inspect_code`: Run real-time ReSharper inspections on any file.
- `find_derived_blueprints`: (UE5) Find Blueprints inheriting from a class.
- `get_blueprint_audit_status`: (UE5) Check status of Blueprint analysis.

## Prerequisites

- [Node.js](https://nodejs.org/) (v18+)
- [CoRider Rider Plugin](../README.md) installed and running in Rider.

## Installation

1. Open a terminal in this directory:
   ```powershell
   cd CoRider/mcp-server
   npm install
   ```

## Configuration

Add the following to your AI client's configuration file (e.g., `%APPDATA%\Claude\claude_desktop_config.json` for Claude Desktop):

```json
{
  "mcpServers": {
    "corider": {
      "command": "node",
      "args": ["C:\path	o\CoRider\mcp-server\index.js"]
    }
  }
}
```
*Note: Ensure you use absolute paths and double-backslashes on Windows.*

## Development

To test the bridge manually (it uses Stdio):
```powershell
node index.js
```
The process will stay open and wait for JSON-RPC input.
