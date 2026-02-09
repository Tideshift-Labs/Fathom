# ReSharper SDK 2025.3.2 API Notes

API reference learned from exploring the NuGet package XML docs and IL disassembly for the JetBrains ReSharper/Rider SDK.

## Package Structure

The `JetBrains.ReSharper.SDK` (v2025.3.2) is a meta-package. Actual assemblies live in separate NuGet packages:

| Package | Contains |
|---------|----------|
| `jetbrains.psi.features.core` | `JetBrains.ReSharper.Feature.Services.dll` - daemon, highlightings, refactorings |
| `jetbrains.psi.features.src` | `JetBrains.ReSharper.Daemon.dll`, `JetBrains.ReSharper.Daemon.CSharp.dll` |
| `jetbrains.platform.core.text` | `JetBrains.Platform.TextControl.dll`, `JetBrains.Platform.Text.Protocol.dll` - document markup, highlighters |
| `jetbrains.platform.core.shell` | `JetBrains.Platform.Core.dll`, `JetBrains.Platform.Util.dll` - core types, data structures |

SDK version `2025.3.2` maps to internal build version `253.0.20260129.45253`.

---

## IDaemon (`JetBrains.ReSharper.Feature.Services.Daemon`)

The main daemon interface. Does **NOT** have a `GetHighlightings()` method.

### Methods
- `ForceReHighlight(IDocument)` - force re-highlight a document
- `Invalidate()` - invalidate all daemon data
- `Invalidate(string)` - invalidate by key
- `Invalidate(IDocument)` - invalidate for a document
- `Invalidate(string, IDocument)` - invalidate by key + document
- `StateWithDescription(IDocument)` - get daemon state for a document

### Properties
- `DaemonStateChanged` - event signal for daemon state changes

---

## HighlightingInfo (`JetBrains.ReSharper.Feature.Services.Daemon`)

Used internally in `DaemonStageResult` to pass highlighting results from daemon stages. Members are NOT well-documented in the public XML docs - treat as internal.

### Used in
- `DaemonStageResult(IReadOnlyList<HighlightingInfo>)`
- `DaemonStageResult(IReadOnlyList<HighlightingInfo>, byte)`
- `DaemonStageResult(IReadOnlyList<HighlightingInfo>, DocumentRange)`

---

## IHighlighting (`JetBrains.ReSharper.Feature.Services.Daemon`)

Interface for semantic highlighting data.

### Known Properties/Methods
- `IsValid()` - check if the highlighting is still valid
- `ToolTip` - tooltip text
- `ErrorStripeToolTip` - error stripe tooltip text

---

## Severity Enum (`JetBrains.ReSharper.Feature.Services.Daemon`)

```csharp
enum Severity { INFO, HINT, SUGGESTION, WARNING, ERROR }
```

---

## SolutionAnalysisMode Enum (`JetBrains.ReSharper.Feature.Services.Daemon`)

```csharp
enum SolutionAnalysisMode {
    LocalInspection,
    GlobalInspection,
    LocalAndGlobalInspection,
    LocalInspectionExcludedFromSolutionAnalysisResults
}
```

---

## SolutionAnalysisService (`JetBrains.ReSharper.Daemon`)

Type exists but has NO documented public methods in XML docs. Likely internal or inherited.

---

## SolutionAnalysisConfiguration (`JetBrains.ReSharper.Daemon`)

### Properties
- `PausedByUser` - whether user paused SWEA
- `Paused` - whether SWEA is paused
- `CompletedOnceAfterStart` - whether analysis completed at least once

### Methods
- `Pause(Lifetime, string)` - pause analysis with a reason

---

## Document Markup API (the way to READ daemon results in a plugin)

This is the primary public API for reading daemon/inspection results from a plugin.

### IDocumentMarkupManager (`JetBrains.TextControl.DocumentMarkup`)

Shell-level component. Get via `Shell.Instance.GetComponent<IDocumentMarkupManager>()`.

#### Methods
- `GetMarkupModel(IDocument)` - get markup model for a document
- `TryGetMarkupModel(IDocument)` - get markup model or null
- `GetMarkupModelAndKeepAlive(Lifetime, IDocument)` - get markup and keep alive
- `AdviseMarkupEvents(Lifetime, IDocument, IDocumentMarkupEvents)` - subscribe to markup changes

### IDocumentMarkup (`JetBrains.TextControl.DocumentMarkup`)

Per-document markup model containing all highlighters.

#### Properties
- `Document` - the document this markup belongs to

#### Methods
- `GetHighlightersEnumerable(OnWriteLockRequestedBehavior, Func<IHighlighter, bool>)` - enumerate highlighters with optional filter
- `GetHighlightersEnumerable(string key, OnWriteLockRequestedBehavior, Func<IHighlighter, bool>)` - enumerate with key filter
- `GetHighlightersOver(TextRange, OnWriteLockRequestedBehavior, Func<IHighlighter, bool>)` - highlighters in a range
- `AddHighlighterCustom(...)` - add a custom highlighter
- `AddHighlighterRegistered(...)` - add a registered highlighter
- `RemoveHighlighter(IHighlighter)` / `RemoveHighlighterAsync(IHighlighter)`
- `RemoveHighlighters(string key)` / `RemoveHighlightersAsync(string key)`
- `RemoveAllHighlighters()`
- `BatchChangeCookie(string)` - batch changes

#### Extension Methods (DocumentMarkupEx)
- `GetFilteredHighlighters(IDocumentMarkup, Func<IHighlighter, bool>)`
- `GetFilteredHighlightersOver(IDocumentMarkup, TextRange, Func<IHighlighter, bool>)`

### IHighlighter (`JetBrains.TextControl.DocumentMarkup`)

Individual highlighter on a document. Implementations (e.g. `HighlighterOnRangeMarker`) also implement `IRangeable`.

#### Properties (from interface + explicit implementations)
- `Attributes` - `HighlighterAttributes`
- `AttributeId` - string identifier for the highlighting kind
- `ErrorStripeAttributes` - nullable `ErrorStripeAttributes` (severity info)
- `GutterMarkType` - gutter mark info
- `AdornmentDataModel` - adornment data
- `Key` - string key identifying the highlighter group
- `Layer` - `HighlighterLayer`
- `UserData` - `UserDataWrapper` for custom data

#### Methods
- `TryGetTooltip(HighlighterTooltipKind)` - get tooltip (returns object with `.ToString()`)

### IRangeable (`JetBrains.TextControl.Data`)

Interface for range information. `IHighlighter` implementations also implement this.

#### Properties
- `Document` - the document
- `IsValid` - whether the range is still valid
- `Range` - `TextRange` (start/end offsets)

**Usage**: Cast `IHighlighter` to `IRangeable` to get range:
```csharp
if (highlighter is JetBrains.TextControl.Data.IRangeable rangeable && rangeable.Range.IsValid)
{
    int startOffset = rangeable.Range.StartOffset;
}
```

### ErrorStripeAttributes (`JetBrains.TextControl.DocumentMarkup`)

Attached to highlighters that appear on the error stripe.

#### Properties
- `MarkerKind` - `ErrorStripeMarkerKind` enum
- `ErrorStripeColorHighlighterAttributeId` - attribute ID for color

### ErrorStripeMarkerKind Enum (`JetBrains.TextControl.DocumentMarkup`)

```csharp
enum ErrorStripeMarkerKind { Info, Suggestion, Warning, Error, Usage }
```

Severity mapping:

| MarkerKind | Display |
|------------|---------|
| Error | [ERROR] |
| Warning | [WARNING] |
| Suggestion | [SUGGESTION] |
| Info | [HINT] |
| Usage | (skip - find usages highlight) |

### HighlighterTooltipKind Enum (`JetBrains.TextControl.DocumentMarkup`)

- `ErrorStripe` - tooltip for error stripe marker

### OnWriteLockRequestedBehavior Enum (`JetBrains.Util.dataStructures`)

**IMPORTANT**: Note the lowercase `d` in `dataStructures` - this is the actual namespace casing.

Found via IL disassembly of `JetBrains.Platform.Core.dll`:

```csharp
// JetBrains.Util.dataStructures.OnWriteLockRequestedBehavior
enum OnWriteLockRequestedBehavior
{
    THROW_OCE = 0,                                   // Throw OperationCanceledException
    MATERIALIZE_ENUMERABLE = 1,                       // Materialize the enumerable into a list first
    IGNORE_WRITE_LOCK_AND_CONTINUE_ENUMERATION = 2    // Ignore write lock, continue enumerating
}
```

Usage:
```csharp
markup.GetHighlightersEnumerable(
    JetBrains.Util.dataStructures.OnWriteLockRequestedBehavior.MATERIALIZE_ENUMERABLE,
    null);
```

---

## Document Offset / Coordinates API

### DocumentOffset (`JetBrains.DocumentModel`)

Use `DocumentOffset` instead of raw offset integers. `IDocument.GetCoordsByOffset(int)` is **obsolete**.

```csharp
// Correct (modern API):
var docOffset = new DocumentOffset(document, offset);
var coords = docOffset.ToDocumentCoords();
int line = (int)coords.Line + 1;  // Line is 0-based

// Obsolete (avoid):
// document.GetCoordsByOffset(offset)
```

---

## RD Protocol Threading (`JetBrains.Rd`)

All RD model interactions (`Advise`, `Fire`, property read/write) must execute on the protocol's scheduler thread ("Shell Rd Dispatcher"). Components marked `ContainerAsyncAnyThreadSafe` are constructed on pool threads and will throw `LoggerException` ("Illegal scheduler") if they touch the model directly.

### Key types

| Type | Namespace | Notes |
|---|---|---|
| `IScheduler` | `JetBrains.Collections.Viewable` | Protocol thread scheduler |
| `RdExtBase` | `JetBrains.Rd.Base` | Base class for generated models. `Proto` property is **protected** |
| `TryGetProto()` | `JetBrains.Rd.Base` (extension) | **Public** way to access `IProtocol` from any `IRdBindable` |
| `IProtocol.Scheduler` | `JetBrains.Rd` | The `IScheduler` for queuing RD-thread work |

### Pattern

```csharp
using JetBrains.Collections.Viewable; // IScheduler
using JetBrains.Rd.Base;              // TryGetProto() extension

var protocolSolution = solution.GetProtocolSolution();
IScheduler scheduler = protocolSolution.TryGetProto()?.Scheduler;

// All RD operations go through Queue():
scheduler?.Queue(() =>
{
    var model = protocolSolution.GetCoRiderModel();
    model.Port.Advise(lifetime, newPort => { /* ... */ });
    model.ServerStatus.Fire(new ServerStatus(true, port, "ok"));
});
```

### `IScheduler.Queue(Action)`

Enqueues the action to run on the protocol dispatcher thread. Safe to call from any thread. If the scheduler is already on the correct thread, the action runs synchronously.

---

## Action Registration Pattern

```csharp
#pragma warning disable CS0612 // ActionAttribute(string, string) is obsolete in 2025.3
[Action("UniqueActionId", "Display Text",
    Id = 1234,  // unique numeric ID
    IdeaShortcuts = new[] { "Control+Alt+Shift+X" })]
#pragma warning restore CS0612
public class MyAction : IExecutableAction
{
    public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate)
    {
        // Return true if action is available
        return context.GetData(ProjectModelDataConstants.SOLUTION) != null;
    }

    public void Execute(IDataContext context, DelegateExecute nextExecute)
    {
        var solution = context.GetData(ProjectModelDataConstants.SOLUTION);
        // ... action logic
        MessageBox.ShowInfo("Done!");
    }
}
```

Key types:
- `IDataContext` (`JetBrains.Application.DataContext`)
- `ActionPresentation` / `DelegateUpdate` / `DelegateExecute` (`JetBrains.Application.UI.Actions`)
- `ProjectModelDataConstants.SOLUTION` (`JetBrains.ProjectModel.DataContext`) - get ISolution from context
- `Shell.Instance.GetComponent<T>()` (`JetBrains.ReSharper.Resources.Shell`) - resolve shell components
- `solution.GetComponent<T>()` - resolve solution components
- `MessageBox.ShowInfo(string)` (`JetBrains.Util`) - show info dialog

---

## Component Registration

### Shell Component (runs at startup)
```csharp
[ShellComponent]
public class MyComponent
{
    public MyComponent(Lifetime lifetime, IActionManager actionManager)
    {
        // Runs on shell initialization
    }
}
```

### Solution Component (per-solution lifetime)
```csharp
[SolutionComponent]
public class MySolutionComponent
{
    public MySolutionComponent(ISolution solution)
    {
        // Runs when solution opens
    }
}
```

---

## PSI / Source File Iteration

```csharp
foreach (var project in solution.GetAllProjects())
{
    foreach (var projectFile in project.GetAllProjectFiles())
    {
        IPsiSourceFile sourceFile = projectFile.ToSourceFile();
        if (sourceFile == null) continue;

        IDocument document = sourceFile.Document;
        FileSystemPath location = sourceFile.GetLocation();
        FileSystemPath relative = location.MakeRelativeTo(solution.SolutionDirectory);
    }
}
```

---

## Zone System

```csharp
[ZoneDefinition]
public interface IMyPluginZone : IZone, IRequire<ICodeEditingZone> { }
```

Zones control feature availability. `ICodeEditingZone` (`JetBrains.ReSharper.Feature.Services`) is required for code editing features.

---

## Key Namespaces Reference

| Namespace | Contains |
|-----------|----------|
| `JetBrains.ReSharper.Feature.Services.Daemon` | IDaemon, IHighlighting, Severity, HighlightingInfo, DaemonStageResult |
| `JetBrains.ReSharper.Daemon` | SolutionAnalysisService, SolutionAnalysisConfiguration, DaemonImpl |
| `JetBrains.TextControl.DocumentMarkup` | IDocumentMarkupManager, IDocumentMarkup, IHighlighter, ErrorStripeMarkerKind |
| `JetBrains.TextControl.Data` | IRangeable |
| `JetBrains.Util.dataStructures` | OnWriteLockRequestedBehavior (**lowercase d**) |
| `JetBrains.DocumentModel` | IDocument, DocumentRange, DocumentOffset |
| `JetBrains.ProjectModel` | ISolution, IProject, IProjectFile |
| `JetBrains.ProjectModel.DataContext` | ProjectModelDataConstants |
| `JetBrains.ReSharper.Psi` | IPsiSourceFile, IPsiModules |
| `JetBrains.Application.UI.Actions` | IExecutableAction, ActionPresentation |
| `JetBrains.Application.UI.ActionsRevised.Menu` | Action attribute |
| `JetBrains.ReSharper.Resources.Shell` | Shell |
| `JetBrains.Lifetimes` | Lifetime |
| `JetBrains.Application.BuildScript.Application.Zones` | ZoneDefinition, IZone, IRequire |

---

## NuGet Package Cache Locations

Packages for this SDK live at `C:\Users\<user>\.nuget\packages\`:
- XML docs are in `<package>\<version>\DotFiles\<Assembly>.xml`
- DLLs are in `<package>\<version>\DotFiles\<Assembly>.dll`
- Use `ildasm` for types not documented in XML (enum values, internal members)
