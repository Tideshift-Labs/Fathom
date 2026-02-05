// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintDaemonStageProcessBase`1
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Cpp.Daemon;
using JetBrains.ReSharper.Feature.Services.Cpp.Options;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Cpp.Symbols;
using JetBrains.ReSharper.Psi.Cpp.Tree;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using JetBrains.Util.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

public abstract class UnrealBlueprintDaemonStageProcessBase<TDaemonStage> : 
  IRecursiveElementProcessor<IHighlightingConsumer>,
  IDaemonStageProcess
  where TDaemonStage : UnrealBlueprintDaemonStageBase
{
  [NotNull]
  protected readonly CppFile myFile;
  [NotNull]
  private readonly ILogger myLogger;
  [NotNull]
  protected readonly ISolution mySolution;
  protected readonly string myModuleName;
  [NotNull]
  protected readonly TDaemonStage myOwner;
  [NotNull]
  protected readonly OccurrenceFactory myOccurrenceFactory;
  protected bool IsAssetCacheReady;
  protected ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> myAssetAccessorCache;
  protected readonly IUnrealBlueprintHighlightingProvider myHighlightingProvider;
  protected int MaxOccurrences;

  protected UnrealBlueprintDaemonStageProcessBase(
    [NotNull] IDaemonProcess daemonProcess,
    [NotNull] CppFile file,
    [NotNull] ILogger logger,
    string moduleName,
    [NotNull] OccurrenceFactory occurrenceFactory,
    [NotNull] TDaemonStage owner)
  {
    this.myFile = file;
    this.myLogger = logger;
    this.myOwner = owner;
    this.myOccurrenceFactory = occurrenceFactory;
    this.DaemonProcess = daemonProcess;
    this.mySolution = this.DaemonProcess.Solution;
    this.myModuleName = moduleName;
    this.myHighlightingProvider = this.mySolution.TryGetComponent<IUnrealBlueprintHighlightingProvider>();
  }

  protected virtual bool IsEnabledInSettings()
  {
    return UnrealBlueprintsSupportStatusProvider.IsBlueprintsSupportEnabled(this.mySolution) && this.myHighlightingProvider != null;
  }

  public abstract bool InteriorShouldBeProcessed(ITreeNode element, IHighlightingConsumer context);

  public bool IsProcessingFinished(IHighlightingConsumer context) => false;

  public void ProcessBeforeInterior(ITreeNode element, [NotNull] IHighlightingConsumer context)
  {
    Interruption.Current.CheckAndThrow();
    try
    {
      if (!(element is IGenericSymbolNode genericSymbolNode) || !(genericSymbolNode is ICppNamedNode) || !(genericSymbolNode.GetGenericSymbol() is ICppParserSymbol genericSymbol))
        return;
      this.ProcessSymbol(genericSymbol, context);
    }
    catch (OperationCanceledException ex)
    {
      throw;
    }
    catch (Exception ex)
    {
      this.myLogger.Error(ex);
    }
  }

  protected abstract void ProcessSymbol([NotNull] ICppParserSymbol symbol, [NotNull] IHighlightingConsumer context);

  public void ProcessAfterInterior(ITreeNode element, IHighlightingConsumer context)
  {
  }

  public void Execute(Action<DaemonStageResult> committer)
  {
    if (!this.myOwner.Detector.IsUnrealSolution.Value || this.myHighlightingProvider == null || !this.IsEnabledInSettings() || this.DaemonProcess.GetStageProcess<CppSlowDaemonStageProcess>() == null)
      return;
    IPsiSourceFile sourceFile = this.DaemonProcess.SourceFile;
    if (sourceFile.Properties.IsNonUserFile)
      return;
    this.MaxOccurrences = this.DaemonProcess.ContextBoundSettingsStore.GetValue<CppUnrealEngineSettingsKey, int>((Expression<Func<CppUnrealEngineSettingsKey, int>>) (key => key.MaximumBlueprintOccurrencesInCodeVision));
    if (this.MaxOccurrences <= 0)
      this.MaxOccurrences = int.MaxValue;
    DeferredCacheController component = this.mySolution.GetComponent<DeferredCacheController>();
    this.IsAssetCacheReady = component.CompletedOnce.Value && !component.HasDirtyFiles();
    this.myAssetAccessorCache = new ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor>();
    try
    {
      using (this.myLogger.StopwatchCookie(this.GetType().Name + ".DoExecute", sourceFile.Name))
        this.DoExecute(committer, sourceFile);
    }
    finally
    {
      this.myAssetAccessorCache = (ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor>) null;
    }
  }

  protected virtual void DoExecute([NotNull] Action<DaemonStageResult> committer, [NotNull] IPsiSourceFile sourceFile)
  {
    FilteringHighlightingConsumer context = new FilteringHighlightingConsumer(sourceFile, (IFile) this.myFile, this.DaemonProcess.ContextBoundSettingsStore);
    IFile primaryPsiFile = sourceFile.GetPrimaryPsiFile();
    if (primaryPsiFile != null)
      primaryPsiFile.ProcessDescendants<IHighlightingConsumer>((IRecursiveElementProcessor<IHighlightingConsumer>) this, (IHighlightingConsumer) context);
    committer(new DaemonStageResult(context.CollectHighlightings()));
  }

  public IDaemonProcess DaemonProcess { get; }
}
