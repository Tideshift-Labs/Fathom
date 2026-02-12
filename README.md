# CoRider (Rider Plugin)

![Unreal Engine](https://img.shields.io/badge/Unreal%20Engine-5.4+-313131?style=flat&logo=unrealengine&logoColor=white)
![Rider](https://img.shields.io/badge/Rider-2025.3+-087CFA?style=flat&logo=rider&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Win64-0078D6?style=flat&logo=windows&logoColor=white)
![MCP](https://img.shields.io/badge/MCP-Tools-8A2BE2?style=flat)
![License](https://img.shields.io/badge/License-MIT-green?style=flat)

CoRider is an open-source (MIT Licensed) and completely free tool to enhance your LLM's performance for C++ development when working with UE5 C++ projects using [Jetbrains Rider](https://www.jetbrains.com/rider/).

The plugin leverages Rider/ReSharper as well as UE5's internal APIs to expose more context and details using MCP tools and skills. Whether you use Claude, Gemini, Codex or any other LLM, CoRider feeds them with the right information at the right time, reducing hallucinations and crashes, and increasing your productivity.

## Core Philosophy

Unlike other GenAI / LLM coding tools in game development, CoRider is not here to automate everything. It exists to reduce the friction of using LLMs with UE5, especially for C++ development. It intentionally does not provide blueprint / uasset editing capabilities. Instead it focuses on helping your LLM agents to better understand the full codebase (C++ _and_ blueprints). 

### Core Capabilities

- **Solution-Wide Inspections**: Query ReSharper's analysis for any file in the solution, not just the ones currently open.
- **Unreal Engine Intelligence**: Find derived Blueprints and inspect asset summaries (requires companion UE plugin).
- **Tool Friendly**: Native support for Markdown and JSON output formats.
- **Headless Refresh**: Automatically triggers Unreal Engine commandlets to keep asset data fresh.

## Limitations

- Windows only
- For Rider 2025.3+ only
- For UE5 5.4+ only 
- Unreal Engine only (Support for Unity is planned)
- UE5 Plugin must be installed in the Game (not in Engine)

## Getting Started

Install the latest CoRider plugin from ~~JetBrains Marketplace (coming soon) or~~ our manually by downloading it from the [GitHub releases page](https://github.com/kvirani/CoRider/releases).

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

If you wish to contribute or build the plugin from source for any other reason, this section will help.

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
Invoke-RestMethod http://localhost:19876/health
```


## TODOs

- [ ] **Recursive C++ Discovery**: Improve `/blueprints` to walk C++ class hierarchies.
- [ ] **UCLASS Reflection**: Add `/uclass` endpoint for parsing `UPROPERTY`/`UFUNCTION` macros.
- [ ] **Notification Balloons**: Show UI feedback in Rider when the server starts or fails.
- [ ] **C# Inspection Fix**: Investigate why `.cs` files sometimes report 0 issues.
- [ ] **Describe Code** action should also include comments that are assigned to functions or properties

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
