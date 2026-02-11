# CoRider (Rider Plugin)

If you are using Jetbrains Rider along with LLMs for UE5 ([Unreal Engine 5](https://www.unrealengine.com/en-US)) C++ development, you should consider this Rider plugin.

CoRider is an open-source and completely free tool to enhance your LLMs performance for C++ development when working with UE5. 

The plugin leverages Rider/ReSharper as well as UE5 to expose more context, details using MCP tools and skills. Whether you use Claude, Gemini, Codex or any other LLM, CoRider provides an MCP server to feed them with the right information.

It's best used with Claude CLI or Desktop for developers working on UE5 C++ projects using [Jetbrains Rider](https://www.jetbrains.com/rider/). 

## Core Philosophy

Unlike other GenAI / LLM coding tools in game development, CoRider is not here to automate everything. It exists to reduce the friction of using LLMs with UE5, especially for C++ development. It does not try to edit blueprints or other uassets for you. It will understand them better and therefore make less mistakes when working on your C++ code. 

### Core Capabilities

- **Solution-Wide Inspections**: Query ReSharper's analysis for any file in the solution, not just the ones currently open.
- **Unreal Engine Intelligence**: Find derived Blueprints and inspect asset summaries (requires companion UE plugin).
- **Tool Friendly**: Native support for Markdown and JSON output formats.
- **Headless Refresh**: Automatically triggers Unreal Engine commandlets to keep asset data fresh.

## Getting Started

Install the latest CoRider plugin from JetBrains Marketplace (coming soon) or from the [releases page](https://github.com/kvirani/CoRider/releases).

### Prerequisites
- **Rider**: 2025.3+
- **Windows** (current primary support)

## Documentation

- **[API Reference](docs/api_reference.md)**: Full endpoint documentation with parameters, response formats, and status codes.
- **[Technical Overview](docs/technical_overview.md)**: Deep dive into architecture, APIs, and design decisions.
- **[Learnings & Troubleshooting](docs/LEARNINGS.md)**: Hard-won lessons and ReSharper SDK quirks.
- **[ReSharper SDK Notes](docs/Resharper-SDK-API-Notes.md)**: Specific API details for future contributors.
- **[Unreal Companion Doc](docs/ue-companion-plugin.md)**: Details on the integration with Unreal Engine.

## Building From Source

If you wish to contribute or for some other reason, build the plugin from source, this section will help you with that. 

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


## TODOs

- [ ] **Recursive C++ Discovery**: Improve `/blueprints` to walk C++ class hierarchies.
- [ ] **UCLASS Reflection**: Add `/uclass` endpoint for parsing `UPROPERTY`/`UFUNCTION` macros.
- [ ] **Notification Balloons**: Show UI feedback in Rider when the server starts or fails.
- [ ] **C# Inspection Fix**: Investigate why `.cs` files sometimes report 0 issues.
- [ ] **Describe Code** action should also include comments that are assigned to functions or properties

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
