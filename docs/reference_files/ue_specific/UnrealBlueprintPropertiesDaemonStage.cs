// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintPropertiesDaemonStage
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application.Parts;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Config.Search;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Psi.Cpp.Tree;
using JetBrains.ReSharper.Psi.Cpp.UE4;
using JetBrains.ReSharper.Psi.Cpp.Util;
using JetBrains.Util;
using System;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

[DaemonStage(Instantiation.DemandAnyThreadUnsafe, StagesBefore = new Type[] {typeof (UnrealBlueprintClassesDaemonStage)}, HighlightingTypes = new Type[] {typeof (UnrealBlueprintPropertiesDaemonStageProcess)})]
public class UnrealBlueprintPropertiesDaemonStage : UnrealBlueprintDaemonStageBase
{
  [NotNull]
  public UEIniUsagesSearcher IniSearcher { get; }

  public UnrealBlueprintPropertiesDaemonStage(
    ElementProblemAnalyzerRegistrar elementProblemAnalyzerRegistrar,
    [NotNull] ILogger logger,
    [NotNull] ICppUE4ModuleNamesProvider moduleNamesProvider,
    [NotNull] ICppUE4ProjectPropertiesProvider projectPropertiesProvider,
    [NotNull] UEAssetUsagesSearcher searcher,
    [NotNull] UEIniUsagesSearcher iniSearcher,
    [NotNull] ICppUE4SolutionDetector detector,
    [NotNull] OccurrenceFactory occurrenceFactory)
    : base(elementProblemAnalyzerRegistrar, logger, moduleNamesProvider, projectPropertiesProvider, searcher, detector, occurrenceFactory)
  {
    this.IniSearcher = iniSearcher;
  }

  protected override IDaemonStageProcess CreateProcess(
    IDaemonProcess process,
    CppFile file,
    string moduleName)
  {
    return (IDaemonStageProcess) new UnrealBlueprintPropertiesDaemonStageProcess(process, file, this.Logger, moduleName, this.OccurrenceFactory, this);
  }
}
