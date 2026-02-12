# CoRider

![Unreal Engine](https://img.shields.io/badge/Unreal%20Engine-5.4+-313131?style=flat&logo=unrealengine&logoColor=white)
![Rider](https://img.shields.io/badge/Rider-2025.3+-087CFA?style=flat&logo=rider&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Win64-0078D6?style=flat&logo=windows&logoColor=white)
![MCP](https://img.shields.io/badge/MCP-Tools-8A2BE2?style=flat)
![License](https://img.shields.io/badge/License-MIT-green?style=flat)

CoRider is a free, open-source plugin that helps AI coding assistants better understand your Unreal Engine 5 C++ projects. Whether you use Claude, Gemini, Codex, or any other LLM, CoRider gives them the right project context at the right time, reducing hallucinations and increasing your productivity.

## Why CoRider?

LLMs working on UE5 C++ projects frequently hallucinate class names, miss Blueprint relationships, and produce code that doesn't match your project's actual structure. They can only see the files you feed them, and they have no way to query your project as a whole.

CoRider solves this by exposing your project's full picture through MCP tools that any compatible AI assistant can call on demand.

## What It Does

- **Solution-Wide Code Analysis**: Your LLM can query diagnostics and inspections for any file in the solution, not just the ones currently open.
- **Blueprint and Asset Discovery**: Find which Blueprints derive from a given C++ class, and view asset summaries alongside your code.
- **Automatic Data Freshness**: Asset data is kept up to date so your LLM always works with the current state of the project.
- **Structured Output**: Results come back in Markdown or JSON, formatted for easy consumption by both humans and LLMs.

## Philosophy

CoRider is not here to automate everything. It exists to reduce the friction of using LLMs with UE5, especially for C++ development. It intentionally does not provide Blueprint or uasset editing capabilities. Instead it focuses on helping your LLM agents better understand the full codebase, both C++ and Blueprints.

## Getting Started

Install the latest CoRider plugin from the [JetBrains Marketplace](https://plugins.jetbrains.com/plugin/com.jetbrains.rider.plugins.corider) or manually by downloading it from the [GitHub releases page](https://github.com/kvirani/CoRider/releases).

### Requirements

- **Rider** 2025.3+
- **Unreal Engine** 5.4+
- **Windows** (current primary support)

### Companion UE Plugin

Blueprint and asset features require the [CoRider Unreal Engine](https://github.com/kvirani/CoRider-UnrealEngine) companion plugin installed in your game project. See its README for installation instructions.

## Limitations

- Windows only
- Unreal Engine only (Unity support is planned)
- The UE companion plugin must be installed in the Game project, not the Engine

## Documentation

- **[API Reference](docs/api_reference.md)**: Endpoint documentation with parameters, response formats, and status codes.
- **[Technical Overview](docs/technical_overview.md)**: Architecture and design decisions.
- **[Unreal Companion Doc](docs/ue-companion-plugin.md)**: Details on the UE companion plugin integration.
- **[Release Process](docs/release.md)**: Versioning, bump script, and release workflow.

### For Contributors

- **[Learnings & Troubleshooting](docs/LEARNINGS.md)**: Hard-won lessons and SDK quirks.
- **[SDK API Notes](docs/Resharper-SDK-API-Notes.md)**: API details for contributors.

## Building From Source

### First-Time Setup

Initialize the required build tools:

```powershell
.\scripts\setup.ps1
```

### Building and Running

```powershell
.\gradlew.bat :compileDotNet    # Compile the backend
.\gradlew.bat :runIde           # Launch sandbox with the plugin
```

Once running with your project open, verify the plugin is active:
```powershell
Invoke-RestMethod http://localhost:19876/health
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
