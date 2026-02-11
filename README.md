# CoRider (Rider Plugin)

A Rider/ReSharper plugin that exposes IDE intelligence (code inspections, Blueprint hierarchies, asset queries) via a local HTTP API. Designed to bridge the gap between your IDE and external tools or LLMs.

## What is CoRider?

CoRider transforms Rider's deep understanding of your codebase into a format external tools can consume. Instead of just seeing errors in your editor, you can query your entire solution for inspections, find Blueprint derivation chains, and audit asset metadata via simple HTTP requests.

### Core Capabilities

- **Solution-Wide Inspections**: Query ReSharper's analysis for any file in the solution, not just the ones currently open.
- **Unreal Engine Intelligence**: Find derived Blueprints and inspect asset summaries (requires companion UE plugin).
- **Tool Friendly**: Native support for Markdown and JSON output formats.
- **Headless Refresh**: Automatically triggers Unreal Engine commandlets to keep asset data fresh.

## HTTP API Quick Reference

Starts automatically on port `19876` when a solution opens. See the [full API reference](docs/api_reference.md) for parameters, response formats, and examples.

**Source Code**

| Endpoint | Description |
|----------|-------------|
| `GET /files` | List all source files in the solution |
| `GET /classes` | List game C++ classes with headers/sources and base class |
| `GET /describe_code` | Structural description of source files |
| `GET /inspect` | Run code inspection on files |

**Unreal Engine** (some require live UE editor with companion plugin)

| Endpoint | Description |
|----------|-------------|
| `GET /blueprints` | Find Blueprints derived from a C++ class |
| `GET /bp` | Blueprint composite: audit + dependencies + referencers |
| `GET /blueprint-audit` | Get Blueprint audit data |
| `GET /blueprint-audit/refresh` | Trigger background audit refresh |
| `GET /blueprint-audit/status` | Check audit refresh status |
| `GET /uassets` | Fuzzy search for UAssets by name |
| `GET /uassets/show` | Asset detail: metadata, disk size, tags, ref counts |
| `GET /asset-refs/dependencies` | Asset dependencies |
| `GET /asset-refs/referencers` | Asset referencers |
| `GET /asset-refs/status` | UE editor connection status |

**Diagnostics**

| Endpoint | Description |
|----------|-------------|
| `GET /health` | Server and solution health check |
| `GET /ue-project` | UE project detection diagnostics |
| `GET /debug-psi-tree` | Raw PSI tree dump for a source file |

## Getting Started

### Prerequisites

- **Rider**: 2024.3+
- **JDK 17+** (for building the plugin)
- **Windows** (current primary support)

### First-Time Setup

Initialize the required build tools (downloads `nuget.exe` and `vswhere.exe`):

```powershell
.\scripts\setup.ps1
```

### Building and Running

```powershell
.\gradlew.bat :compileDotNet    # Compile the backend
.\gradlew.bat :runIde           # Launch sandbox Rider with the plugin
```

Once Rider is running with your project open, verify the server is active:
```powershell
curl http://localhost:19876/health
```

## Documentation

- **[API Reference](docs/api_reference.md)**: Full endpoint documentation with parameters, response formats, and status codes.
- **[Technical Overview](docs/technical_overview.md)**: Deep dive into architecture, APIs, and design decisions.
- **[Learnings & Troubleshooting](docs/LEARNINGS.md)**: Hard-won lessons and ReSharper SDK quirks.
- **[ReSharper SDK Notes](docs/Resharper-SDK-API-Notes.md)**: Specific API details for future contributors.
- **[Unreal Companion Doc](docs/ue-companion-plugin.md)**: Details on the integration with Unreal Engine.

## TODOs

- [ ] **Recursive C++ Discovery**: Improve `/blueprints` to walk C++ class hierarchies.
- [ ] **UCLASS Reflection**: Add `/uclass` endpoint for parsing `UPROPERTY`/`UFUNCTION` macros.
- [ ] **Notification Balloons**: Show UI feedback in Rider when the server starts or fails.
- [ ] **C# Inspection Fix**: Investigate why `.cs` files sometimes report 0 issues.
- [x] **Blueprint Audit Boot Check**: `BlueprintAuditService.RunBlueprintAuditCommandlet` crashes with "Cannot start process because a file name has not been provided" when the UE engine path is not yet resolved. Likely a race condition on first-time project open where Rider is still indexing. Guard the commandlet launch against a null/empty `CommandletExePath`.
- [ ] **Status Bar Icon**: Show a status bar icon when the server is running. It should be used to indicate any issues or actions the user needs to take (eg: UE plugin not found, UE engine path not configured, etc).
- [ ] **Access Log**: Allow the dev to view the log via the bottom (status) bar icon. We'd log all important stuff to a corider specific access log. Proposal md doc needed first in `proposals` dir
- [ ] Describe Code should also include comments that are assigned to functions or properties

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
