// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Daemon.DaemonProcessBase
// Assembly: JetBrains.ReSharper.Feature.Services, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 4C92A54E-3E1D-4A2A-83F7-BA80E44C71B3
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.ContentModel;
using JetBrains.Application.I18n;
using JetBrains.Application.Notifications;
using JetBrains.Application.Settings;
using JetBrains.Application.Settings.Implementation;
using JetBrains.Application.Threading.Tasks;
using JetBrains.Collections;
using JetBrains.Diagnostics;
using JetBrains.Diagnostics.StringInterpolation;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Documents;
using JetBrains.ReSharper.Feature.Services.FeaturesStatistics;
using JetBrains.ReSharper.Feature.Services.Resources;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Dependencies;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.ReSharper.Psi.SourceGenerators;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Util;
using JetBrains.Util.Collections;
using JetBrains.Util.DataStructures.Collections;
using JetBrains.Util.Logging;
using JetBrains.Util.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

#nullable enable
namespace JetBrains.ReSharper.Feature.Services.Daemon;

public abstract class DaemonProcessBase : IDaemonProcess
{
  protected static readonly 
  #nullable disable
  StartupMeasurer.GroupId StartupMeasureGroupId = new StartupMeasurer.GroupId(nameof (DaemonProcessBase));
  [NotNull]
  private readonly ILogger myLogger = Logger.GetLogger(typeof (DaemonProcessBase));
  [NotNull]
  private readonly IDaemonThread myDaemonThread;
  [NotNull]
  private readonly IHighlightingOverlapResolver myOverlapResolver;
  [NotNull]
  private readonly HighlightingSettingsManager myHighlightingSettingsManager;
  [NotNull]
  private readonly DependencyStore myDependencyStore;
  [NotNull]
  private readonly IDocumentInfoCache myDocumentInfoCache;
  [NotNull]
  private readonly PerformanceUserNotifications myPerformanceNotifications;
  [NotNull]
  private readonly DaemonProcessBase.NoInterruptWrapper myFastContentLockNotificationInterrupt;
  [NotNull]
  protected readonly IDaemonEnablePolicy EnablePolicy;
  [NotNull]
  private readonly DaemonPerformanceCollector myPerformanceCollector;
  [NotNull]
  private readonly Dictionary<Type, IDaemonStageProcess[]> myProcessTypeToProcess = new Dictionary<Type, IDaemonStageProcess[]>();
  [NotNull]
  protected readonly IDaemonStagesManager myDaemonStagesManager;
  [NotNull]
  protected IContextBoundSettingsStore myContextBoundSettingsStore;
  private static readonly JetBrains.Util.DataStructures.Specialized.ObjectPool<PooledList<IDaemonStage>> ourStageListPool = PooledList<IDaemonStage>.CreatePool();
  private static readonly JetBrains.Util.DataStructures.Specialized.ObjectPool<PooledDictionary<IDaemonStage, bool>> ourStagesDictionaryPool = PooledDictionary<IDaemonStage, bool>.CreatePool(ReferenceEqualityComparer<IDaemonStage>.Default);
  private static readonly JetBrains.Util.DataStructures.Specialized.ObjectPool<PooledHashSet<IDaemonStage>> ourStagesHashSetPool = PooledHashSet<IDaemonStage>.CreatePool(ReferenceEqualityComparer<IDaemonStage>.Default);

  public IPsiSourceFile SourceFile { get; }

  public ISolution Solution { get; }

  public IPsiModule PsiModule { get; }

  public IDocument Document { get; }

  public UserDataHolder CustomData { get; } = new UserDataHolder();

  public DaemonProfilingCollector.Bag ProfilingBag { get; private set; } = DaemonProfilingCollector.Bag.Zero;

  protected DaemonProcessBase(
    [NotNull] IPsiSourceFile sourceFile,
    [CanBeNull] IDocument document = null,
    [CanBeNull] IContextBoundSettingsStore contextBoundSettingsStore = null)
  {
    this.SourceFile = sourceFile;
    this.Solution = sourceFile.GetSolution();
    this.PsiModule = sourceFile.PsiModule.NotNull<IPsiModule>("sourceFile.PsiModule");
    this.Document = document ?? GetSourceFileDocument(this.SourceFile);
    this.myDaemonStagesManager = this.Solution.GetComponent<IDaemonStagesManager>();
    this.Document = document ?? this.SourceFile.GetDocumentForHighlighters();
    this.myDaemonThread = this.Solution.GetComponent<IDaemonThread>();
    this.myOverlapResolver = this.CreateOverlapResolver();
    this.myHighlightingSettingsManager = this.Solution.GetComponent<HighlightingSettingsManager>();
    this.myDependencyStore = this.Solution.GetPsiServices().DependencyStore;
    this.myDocumentInfoCache = this.Solution.GetComponent<IDocumentInfoCache>();
    this.myPerformanceNotifications = this.Solution.GetComponent<PerformanceUserNotifications>();
    this.myFastContentLockNotificationInterrupt = new DaemonProcessBase.NoInterruptWrapper(this.Solution.Locks.ContentModelLocks.Interruption);
    this.EnablePolicy = this.Solution.GetComponent<IDaemonEnablePolicy>();
    this.myPerformanceCollector = this.Solution.GetComponent<DaemonPerformanceCollector>();
    this.myContextBoundSettingsStore = contextBoundSettingsStore ?? this.BindSettings();

    [NotNull]
    static IDocument GetSourceFileDocument([NotNull] IPsiSourceFile sourceFile)
    {
      return sourceFile is ISourceGeneratorOutputFile generatorOutputFile ? generatorOutputFile.AssociatedEmbeddedSourceDocument ?? generatorOutputFile.AssociatedEditorDocument ?? sourceFile.Document : sourceFile.Document;
    }
  }

  [NotNull]
  protected virtual IHighlightingOverlapResolver CreateOverlapResolver()
  {
    return this.Solution.GetComponent<HighlightingOverlapResolverFactory>().Create(this.Document, this.SourceFile);
  }

  [NotNull]
  protected IContextBoundSettingsStore BindSettings()
  {
    return (IContextBoundSettingsStore) new LazyContextBoundSettingsStore((Func<IContextBoundSettingsStore>) (() => !this.myHighlightingSettingsManager.GetReadSettingsFromFileLevel(this.SourceFile) ? this.SourceFile.GetSettingsStore(this.Solution) : this.SourceFile.GetSettingsStoreWithEditorConfig(this.Solution)));
  }

  public TDaemonStageProcess GetStageProcess<TDaemonStageProcess>() where TDaemonStageProcess : class, IDaemonStageProcess
  {
    lock (this.myProcessTypeToProcess)
    {
      IDaemonStageProcess[] valueOrDefault = this.myProcessTypeToProcess.GetValueOrDefault<Type, IDaemonStageProcess[]>(typeof (TDaemonStageProcess));
      return valueOrDefault != null && valueOrDefault.Length >= 1 ? (TDaemonStageProcess) valueOrDefault[0] : default (TDaemonStageProcess);
    }
  }

  public IEnumerable<TDaemonStageProcess> GetStageProcesses<TDaemonStageProcess>() where TDaemonStageProcess : class, IDaemonStageProcess
  {
    lock (this.myProcessTypeToProcess)
    {
      IDaemonStageProcess[] valueOrDefault = this.myProcessTypeToProcess.GetValueOrDefault<Type, IDaemonStageProcess[]>(typeof (TDaemonStageProcess));
      return valueOrDefault == null ? (IEnumerable<TDaemonStageProcess>) EmptyList<TDaemonStageProcess>.Instance : Enumerable.Cast<TDaemonStageProcess>(valueOrDefault);
    }
  }

  public IReadOnlyCollection<TDaemonStageProcess> GetCompletedStageProcesses<TDaemonStageProcess>() where TDaemonStageProcess : class, IDaemonStageProcess
  {
    lock (this.myProcessTypeToProcess)
    {
      LocalList<TDaemonStageProcess> localList = new LocalList<TDaemonStageProcess>();
      foreach (IDaemonStageProcess[] daemonStageProcessArray in this.myProcessTypeToProcess.Values)
      {
        foreach (IDaemonStageProcess daemonStageProcess1 in daemonStageProcessArray)
        {
          if (daemonStageProcess1 is TDaemonStageProcess daemonStageProcess2)
            localList.Add(daemonStageProcess2);
        }
      }
      return (IReadOnlyCollection<TDaemonStageProcess>) localList.ReadOnlyList();
    }
  }

  public ICollection<IDaemonStageProcess> GetCompletedStageProcesses()
  {
    lock (this.myProcessTypeToProcess)
    {
      List<IDaemonStageProcess> completedStageProcesses = new List<IDaemonStageProcess>();
      foreach (IDaemonStageProcess[] collection in this.myProcessTypeToProcess.Values)
        completedStageProcesses.AddRange((IEnumerable<IDaemonStageProcess>) collection);
      return (ICollection<IDaemonStageProcess>) completedStageProcesses;
    }
  }

  public IContextBoundSettingsStore ContextBoundSettingsStore => this.myContextBoundSettingsStore;

  public abstract bool IsRangeInvalidated(DocumentRange range);

  public abstract bool FullRehighlightingRequired { get; }

  public virtual bool InterruptFlag => false;

  public virtual DocumentRange VisibleRange => DocumentRange.InvalidRange;

  protected virtual bool RunStagesInParallel => true;

  protected virtual bool AnalyzeMembersInParallel => true;

  public bool IsSingleThreadedDaemon => !this.RunStagesInParallel && !this.AnalyzeMembersInParallel;

  /// <summary>
  /// Aggregated timestamp of <see cref="T:JetBrains.ReSharper.Psi.IPsiSourceFile" /> for which daemon was completed
  /// </summary>
  public long LastRunTimestamp { get; private set; } = -1;

  public ITaskBarrier CreateTaskBarrierForStageExecution()
  {
    return this.myDaemonThread.CreateTaskBarrier((IDaemonProcess) this, !this.AnalyzeMembersInParallel);
  }

  [CanBeNull]
  public virtual IMarkupModelOverlapResolver GetMarkupOverlapResolver()
  {
    return (IMarkupModelOverlapResolver) null;
  }

  public virtual void ClearOverlapResolver(IHighlightingOverlapResolver.Layer layerMask)
  {
    lock (this.myOverlapResolver)
      this.myOverlapResolver.Clear(layerMask);
  }

  private static IHighlightingOverlapResolver.Layer AdjustLayerFromProcess(
    IHighlightingOverlapResolver.Layer layer,
    IDaemonStageProcess process)
  {
    if (process is IDaemonStageProcessWithPsiFile processWithPsiFile)
    {
      IFile file = processWithPsiFile.File;
      if (file != null && file.IsInjected())
        return IHighlightingOverlapResolver.Layer.Injected;
    }
    return layer;
  }

  private static IHighlightingOverlapResolver.Layer DaemonProcessKindToLayer(
    DaemonProcessKind kind,
    bool forCleanUp = false)
  {
    if (kind == DaemonProcessKind.GLOBAL_WARNINGS)
      return IHighlightingOverlapResolver.Layer.GlobalAnalysis;
    if (forCleanUp)
      return IHighlightingOverlapResolver.Layer.All;
    return kind == DaemonProcessKind.EXTERNAL_DAEMON ? IHighlightingOverlapResolver.Layer.External : IHighlightingOverlapResolver.Layer.Normal;
  }

  protected void DoHighlighting(
    DaemonProcessKind processKind,
    [CanBeNull] Action<DaemonProcessBase.DaemonCommitContext> committer,
    [CanBeNull, InstantHandle] Action onFastStagesCompleted = null)
  {
    this.DoHighlighting(processKind, committer, this.ContextBoundSettingsStore, onFastStagesCompleted);
  }

  protected void DoHighlighting(
    DaemonProcessKind processKind,
    [CanBeNull] Action<DaemonProcessBase.DaemonCommitContext> committer,
    [NotNull] IContextBoundSettingsStore settingsStore,
    [CanBeNull, InstantHandle] Action onFastStagesCompleted = null)
  {
    if (processKind == DaemonProcessKind.SOLUTION_ANALYSIS)
      ContentModelFork.AssertForked("SWA must always run in Content Model Fork for side-effects isolation");
    int documentLength = this.myDocumentInfoCache.GetDocumentLength(this.SourceFile);
    int textLength = this.Document.GetTextLength();
    if (documentLength != textLength)
    {
      if (this.ShouldNotifySwea(this.SourceFile))
        this.AnalysisCompleted(this.SourceFile, this, (DependencySet) null, false, processKind);
      this.EnablePolicy.Isolate(this.SourceFile);
      this.Solution.GetComponent<PsiCachesRepairService>().ForceUpdateFile(this.SourceFile, "DaemonProcessBase.DoHighlighting: inconsistent text length");
      ILogger logger1 = this.myLogger;
      ILogger logger2 = logger1;
      bool isEnabled;
      JetLogVerboseInterpolatedStringHandler interpolatedStringHandler = new JetLogVerboseInterpolatedStringHandler(96 /*0x60*/, 3, (ILog) logger1, out isEnabled);
      if (isEnabled)
      {
        interpolatedStringHandler.AppendLiteral("Inconsistent caches for file ");
        interpolatedStringHandler.AppendFormatted(this.SourceFile.Name);
        interpolatedStringHandler.AppendLiteral(": cached=");
        interpolatedStringHandler.AppendFormatted<int>(documentLength);
        interpolatedStringHandler.AppendLiteral(" ");
        interpolatedStringHandler.AppendLiteral("actual=");
        interpolatedStringHandler.AppendFormatted<int>(textLength);
        interpolatedStringHandler.AppendLiteral(". Please, describe how this file could be changed.");
      }
      ref JetLogVerboseInterpolatedStringHandler local = ref interpolatedStringHandler;
      logger2.Verbose(ref local);
    }
    else
    {
      using (this.myPerformanceNotifications.WithPerformanceNotificationCookie(TimeSpan.FromMilliseconds(1000.0), Strings.DaemonProcess__Text.Format((object) this.GetType().Name.NON_LOCALIZABLE()), Strings.AnalysisOf__Text.Format((object) this.SourceFile)))
      {
        using (CompilationContextCookie.GetOrCreate(this.SourceFile.ResolveContext))
        {
          DaemonProcessBase.StageResultForSWEACollector sweaResultCollector = (DaemonProcessBase.StageResultForSWEACollector) null;
          try
          {
            ILogger logger3 = this.myLogger;
            ILogger logger4 = logger3;
            bool isEnabled1;
            JetLogVerboseInterpolatedStringHandler interpolatedStringHandler1 = new JetLogVerboseInterpolatedStringHandler(50, 3, (ILog) logger3, out isEnabled1);
            if (isEnabled1)
            {
              interpolatedStringHandler1.AppendLiteral("[Daemon] Daemon process ");
              interpolatedStringHandler1.AppendFormatted<int>(this.GetHashCode());
              interpolatedStringHandler1.AppendLiteral(" started ");
              interpolatedStringHandler1.AppendLiteral("on file ");
              interpolatedStringHandler1.AppendFormatted<PsiSourceFileEx.PersistentIdForLogging>(this.SourceFile.GetPersistentIdForLogging());
              interpolatedStringHandler1.AppendLiteral(", kind = ");
              interpolatedStringHandler1.AppendFormatted<DaemonProcessKind>(processKind);
            }
            ref JetLogVerboseInterpolatedStringHandler local1 = ref interpolatedStringHandler1;
            logger4.Verbose(ref local1);
            DaemonHighlightingStatistics highlightingStatistics = processKind != DaemonProcessKind.VISIBLE_DOCUMENT || !this.FullRehighlightingRequired ? (DaemonHighlightingStatistics) null : new DaemonHighlightingStatistics(this.Solution, this.SourceFile, this.Document, this.myPerformanceCollector);
            using (Interruption.Current.AddLegacy((Func<bool>) (() => this.InterruptFlag)))
            {
              using (Interruption.Current.Add((IInterruptionSource) this.myFastContentLockNotificationInterrupt))
              {
                Interruption.Current.CheckAndThrow();
                DependencyStore.IDependencySetHandle handle = (DependencyStore.IDependencySetHandle) null;
                using (ReadLockCookie.Create("Src\\Daemon\\Impl\\DaemonProcessBase.cs", nameof (DoHighlighting)))
                {
                  try
                  {
                    Interruption.Current.CheckAndThrow();
                    this.ProfilingBag = DaemonProfilingCollector.CreateBagForFileAnalysis(this.SourceFile, processKind, this.FullRehighlightingRequired);
                    bool rehighlightingRequired = this.FullRehighlightingRequired;
                    bool analysisSupported = this.ShouldNotifySwea(this.SourceFile);
                    sweaResultCollector = analysisSupported ? new DaemonProcessBase.StageResultForSWEACollector() : (DaemonProcessBase.StageResultForSWEACollector) null;
                    bool flag = rehighlightingRequired & analysisSupported && processKind == DaemonProcessKind.SOLUTION_ANALYSIS;
                    if (flag)
                      handle = this.myDependencyStore.CreateDependencySet(this.SourceFile);
                    ILogger logger5 = this.myLogger;
                    ILogger logger6 = logger5;
                    bool isEnabled2;
                    JetLogTraceInterpolatedStringHandler interpolatedStringHandler2 = new JetLogTraceInterpolatedStringHandler(72, 4, (ILog) logger5, out isEnabled2);
                    if (isEnabled2)
                    {
                      interpolatedStringHandler2.AppendLiteral("[Daemon] Daemon process '");
                      interpolatedStringHandler2.AppendFormatted<IPsiSourceFile>(this.SourceFile);
                      interpolatedStringHandler2.AppendLiteral("' , ");
                      interpolatedStringHandler2.AppendLiteral("fullRehighlight=");
                      interpolatedStringHandler2.AppendFormatted<bool>(rehighlightingRequired);
                      interpolatedStringHandler2.AppendLiteral(", ");
                      interpolatedStringHandler2.AppendLiteral("notifySwea=");
                      interpolatedStringHandler2.AppendFormatted<bool>(analysisSupported);
                      interpolatedStringHandler2.AppendLiteral(", ");
                      interpolatedStringHandler2.AppendLiteral("collectDeps=");
                      interpolatedStringHandler2.AppendFormatted<bool>(flag);
                    }
                    ref JetLogTraceInterpolatedStringHandler local2 = ref interpolatedStringHandler2;
                    logger6.Trace(ref local2);
                    this.ClearOverlapResolver(DaemonProcessBase.DaemonProcessKindToLayer(processKind, true));
                    Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> stageResultCommitter = sweaResultCollector != null ? sweaResultCollector.WrapCommitter(committer) : DaemonProcessBase.CreateTransitWrapper(committer);
                    ContentModelFork.ThreadTransitionCookie threadTransitionCookie = ContentModelFork.CaptureCurrentForkForThreadTransition();
                    DaemonProcessBase.StagesToRun run = this.PrepareStagesToRun(processKind);
                    bool sync = !this.RunStagesInParallel;
                    if (run.ImmediateStages.Count > 0)
                    {
                      using (ITaskBarrier processTaskBarrier = this.myDaemonThread.CreateInterProcessTaskBarrier(sync))
                        this.ScheduleStages(processTaskBarrier, run.ImmediateStages, run.ImmediateStagesBefore, processKind, stageResultCommitter, settingsStore, threadTransitionCookie, highlightingStatistics);
                      if (onFastStagesCompleted != null && processKind != DaemonProcessKind.GLOBAL_WARNINGS)
                        onFastStagesCompleted();
                    }
                    highlightingStatistics?.RegisterNormalStagesDone();
                    if (run.LongRunningStages.Count > 0)
                    {
                      Interruption.Current.CheckAndThrow();
                      using (ITaskBarrier processTaskBarrier = this.myDaemonThread.CreateInterProcessTaskBarrier(sync))
                        this.ScheduleStages(processTaskBarrier, run.LongRunningStages, run.LongRunningStagesBefore, processKind, stageResultCommitter, settingsStore, threadTransitionCookie);
                    }
                    if (run.LastStages.Count > 0)
                    {
                      Interruption.Current.CheckAndThrow();
                      using (ITaskBarrier processTaskBarrier = this.myDaemonThread.CreateInterProcessTaskBarrier(sync))
                      {
                        foreach (IDaemonStage lastStage1 in run.LastStages)
                        {
                          IDaemonStage lastStage = lastStage1;
                          processTaskBarrier.EnqueueJob((Action) (() => this.RunStage(lastStage, processKind, stageResultCommitter, settingsStore, threadTransitionCookie)), "Src\\Daemon\\Impl\\DaemonProcessBase.cs", nameof (DoHighlighting));
                        }
                      }
                    }
                    if (sweaResultCollector != null)
                      this.NotifySolutionAnalysis(sweaResultCollector, processKind, settingsStore);
                    if (rehighlightingRequired)
                    {
                      DependencySet dependencies = (DependencySet) null;
                      if (flag)
                      {
                        dependencies = this.myDependencyStore.ReleaseDependencySet(handle);
                        handle = (DependencyStore.IDependencySetHandle) null;
                      }
                      this.AnalysisCompleted(this.SourceFile, this, dependencies, analysisSupported, processKind);
                    }
                    else if (analysisSupported)
                      this.FilePartlyReanalyzed(this.SourceFile, this, processKind);
                    else
                      this.AnalysisCompleted(this.SourceFile, this, (DependencySet) null, false, processKind);
                  }
                  finally
                  {
                    if (handle != null)
                    {
                      try
                      {
                        this.myDependencyStore.ReleaseDependencySet(handle);
                      }
                      catch (Exception ex)
                      {
                        this.myLogger.LogException(ex);
                      }
                    }
                    this.ProfilingBag = DaemonProfilingCollector.Bag.Zero;
                  }
                }
              }
            }
            ILogger logger7 = this.myLogger;
            ILogger logger8 = logger7;
            bool isEnabled3;
            JetLogVerboseInterpolatedStringHandler interpolatedStringHandler3 = new JetLogVerboseInterpolatedStringHandler(43, 2, (ILog) logger7, out isEnabled3);
            if (isEnabled3)
            {
              interpolatedStringHandler3.AppendLiteral("[Daemon] Daemon process ");
              interpolatedStringHandler3.AppendFormatted<int>(this.GetHashCode());
              interpolatedStringHandler3.AppendLiteral(" finished on file ");
              interpolatedStringHandler3.AppendFormatted<PsiSourceFileEx.PersistentIdForLogging>(this.SourceFile.GetPersistentIdForLogging());
              interpolatedStringHandler3.AppendLiteral(" ");
            }
            ref JetLogVerboseInterpolatedStringHandler local3 = ref interpolatedStringHandler3;
            logger8.Verbose(ref local3);
          }
          catch (Exception ex) when (ex.IsOperationCanceled())
          {
            if (sweaResultCollector != null)
              this.NotifySolutionAnalysis(sweaResultCollector, processKind, settingsStore);
            ILogger logger9 = this.myLogger;
            ILogger logger10 = logger9;
            bool isEnabled;
            JetLogVerboseInterpolatedStringHandler interpolatedStringHandler = new JetLogVerboseInterpolatedStringHandler(36, 1, (ILog) logger9, out isEnabled);
            if (isEnabled)
            {
              interpolatedStringHandler.AppendLiteral("[Daemon] Daemon process ");
              interpolatedStringHandler.AppendFormatted<int>(this.GetHashCode());
              interpolatedStringHandler.AppendLiteral(" interrupted");
            }
            ref JetLogVerboseInterpolatedStringHandler local = ref interpolatedStringHandler;
            logger10.Verbose(ref local);
            throw;
          }
          catch (Exception ex)
          {
            this.EnablePolicy.Isolate(this.SourceFile);
            ILogger logger11 = this.myLogger;
            ILogger logger12 = logger11;
            bool isEnabled;
            JetLogVerboseInterpolatedStringHandler interpolatedStringHandler = new JetLogVerboseInterpolatedStringHandler(48 /*0x30*/, 1, (ILog) logger11, out isEnabled);
            if (isEnabled)
            {
              interpolatedStringHandler.AppendLiteral("[Daemon] Daemon process ");
              interpolatedStringHandler.AppendFormatted<int>(this.GetHashCode());
              interpolatedStringHandler.AppendLiteral(" finished with exception");
            }
            ref JetLogVerboseInterpolatedStringHandler local = ref interpolatedStringHandler;
            logger12.Verbose(ref local);
            throw;
          }
          finally
          {
            lock (this.myProcessTypeToProcess)
              this.myProcessTypeToProcess.Clear();
          }
          this.LastRunTimestamp = this.SourceFile.GetAggregatedTimestamp();
        }
      }
    }
  }

  private void ScheduleStages(
    [NotNull] ITaskBarrier taskBarrier,
    [NotNull] HashSet<IDaemonStage> stagesToRun,
    [NotNull] OneToSetMap<IDaemonStage, IDaemonStage> stagesBefore,
    DaemonProcessKind processKind,
    [CanBeNull] Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> stageResultCommitter,
    [NotNull] IContextBoundSettingsStore settingsStore,
    ContentModelFork.ThreadTransitionCookie threadTransitionCookie,
    [CanBeNull] DaemonHighlightingStatistics highlightingStatistics = null)
  {
    if (taskBarrier.IsSync)
    {
      foreach (IDaemonStage stage in (IEnumerable<IDaemonStage>) this.myDaemonStagesManager.AllRegisteredStagesSorted)
      {
        if (stagesToRun.Contains(stage))
          this.RunStage(stage, processKind, stageResultCommitter, settingsStore, threadTransitionCookie, highlightingStatistics);
      }
    }
    else
    {
      CountingSet<IDaemonStage> dependencyCount = new CountingSet<IDaemonStage>(stagesToRun.Count, ReferenceEqualityComparer<IDaemonStage>.Default);
      foreach (IDaemonStage key in stagesToRun)
      {
        int count = stagesBefore[key].Count;
        if (count > 0)
          dependencyCount.Add(key, count);
      }
      foreach (IDaemonStage key in stagesToRun)
      {
        if (!stagesBefore.ContainsKey(key))
        {
          IDaemonStage stageToRun = key;
          taskBarrier.EnqueueJob((Action) (() => Run(stageToRun)), "Src\\Daemon\\Impl\\DaemonProcessBase.cs", nameof (ScheduleStages));
        }
      }
    }
    DaemonProcessBase daemonProcessBase;
    DaemonProcessKind processKind1;
    Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> stageResultCommitter1;
    IContextBoundSettingsStore settingsStore1;
    ContentModelFork.ThreadTransitionCookie threadTransitionCookie1;
    DaemonHighlightingStatistics highlightingStatistics1;
    CountingSet<IDaemonStage> dependencyCount1;
    ITaskBarrier taskBarrier1;

    void Run([NotNull] IDaemonStage stage)
    {
      daemonProcessBase.RunStage(stage, processKind1, stageResultCommitter1, settingsStore1, threadTransitionCookie1, highlightingStatistics1);
      using (PooledList<IDaemonStage> pooledList = DaemonProcessBase.ourStageListPool.Allocate())
      {
        foreach (IDaemonStage daemonStage in daemonProcessBase.myDaemonStagesManager.GetAfterStagesFor(stage))
        {
          int num;
          lock (dependencyCount1)
            num = dependencyCount1.Remove(daemonStage);
          if (num == 0)
            pooledList.Add(daemonStage);
        }
        foreach (IDaemonStage daemonStage in (List<IDaemonStage>) pooledList)
        {
          IDaemonStage afterStage = daemonStage;
          taskBarrier1.EnqueueJob((Action) (() => Run(afterStage)), "Src\\Daemon\\Impl\\DaemonProcessBase.cs", nameof (ScheduleStages));
        }
      }
    }
  }

  /// <summary>
  /// Dynamically filter stages to run, for example by the highlighting types that stages can produce.
  /// Note that the stage will run anyway if some other stage is dependent on it and this method
  /// returns 'true' for this other stage.
  /// </summary>
  protected virtual bool ShouldRunStage([NotNull] IDaemonStage stage) => true;

  private void NotifySolutionAnalysis(
    [NotNull] DaemonProcessBase.StageResultForSWEACollector sweaResultCollector,
    DaemonProcessKind processKind,
    IContextBoundSettingsStore settingsStore)
  {
    foreach (KeyValuePair<(IDaemonStage Stage, byte Layer), DaemonProcessBase.StageResultForSWEA> pair in sweaResultCollector)
    {
      (IDaemonStage Stage, byte Layer) key;
      DaemonProcessBase.StageResultForSWEA stageResultForSwea1;
      pair.Deconstruct<(IDaemonStage, byte), DaemonProcessBase.StageResultForSWEA>(out key, out stageResultForSwea1);
      (IDaemonStage Stage, byte Layer) tuple = key;
      DaemonProcessBase.StageResultForSWEA stageResultForSwea2 = stageResultForSwea1;
      this.AnalysisStageCompleted(this.SourceFile, tuple.Stage, tuple.Layer, stageResultForSwea2.Highlightings, stageResultForSwea2.FullRehighlight, stageResultForSwea2.RehighlightedRanges, processKind, settingsStore);
    }
  }

  [NotNull]
  private DaemonProcessBase.StagesToRun PrepareStagesToRun(DaemonProcessKind processKind)
  {
    HashSet<IDaemonStage> daemonStageSet1 = new HashSet<IDaemonStage>(ReferenceEqualityComparer<IDaemonStage>.Default);
    HashSet<IDaemonStage> stagesToRun = new HashSet<IDaemonStage>(ReferenceEqualityComparer<IDaemonStage>.Default);
    HashSet<IDaemonStage> daemonStageSet2 = new HashSet<IDaemonStage>(ReferenceEqualityComparer<IDaemonStage>.Default);
    bool isInInternalMode = JetBrains.ReSharper.Resources.Shell.Shell.Instance.IsInInternalMode;
    bool flag1 = processKind == DaemonProcessKind.GLOBAL_WARNINGS;
    using (PooledDictionary<IDaemonStage, bool> stages = DaemonProcessBase.ourStagesDictionaryPool.Allocate())
    {
      bool flag2 = true;
      foreach (IDaemonStage daemonStage in (IEnumerable<IDaemonStage>) this.myDaemonStagesManager.AllRegisteredStagesSorted)
      {
        DaemonStageAttribute stageAttribute = this.myDaemonStagesManager.GetStageAttribute(daemonStage);
        if (stageAttribute != null && (isInInternalMode || !stageAttribute.InternalMode) && flag1 == stageAttribute.GlobalAnalysisStage)
        {
          bool flag3 = this.ShouldRunStage(daemonStage);
          flag2 &= flag3;
          stages[daemonStage] = flag3;
        }
      }
      if (!flag2)
        EnablesDeactivatedStagesRequiredByStagesBeforeDependencies((Dictionary<IDaemonStage, bool>) stages);
      foreach (KeyValuePair<IDaemonStage, bool> pair in (Dictionary<IDaemonStage, bool>) stages)
      {
        IDaemonStage key;
        bool flag4;
        pair.Deconstruct<IDaemonStage, bool>(out key, out flag4);
        IDaemonStage stage = key;
        if (flag4)
        {
          DaemonStageAttribute daemonStageAttribute = this.myDaemonStagesManager.GetStageAttribute(stage).NotNull<DaemonStageAttribute>("myDaemonStagesManager.GetStageAttribute(stage)");
          if (daemonStageAttribute.LastStage)
            daemonStageSet2.Add(stage);
          else if (daemonStageAttribute.LongRunningStage)
            stagesToRun.Add(stage);
          else
            daemonStageSet1.Add(stage);
        }
      }
      OneToSetMap<IDaemonStage, IDaemonStage> oneToSetMap1 = CollectBeforeStages(daemonStageSet1, (HashSet<IDaemonStage>) null);
      OneToSetMap<IDaemonStage, IDaemonStage> oneToSetMap2 = CollectBeforeStages(stagesToRun, daemonStageSet1);
      return new DaemonProcessBase.StagesToRun()
      {
        ImmediateStages = daemonStageSet1,
        ImmediateStagesBefore = oneToSetMap1,
        LongRunningStages = stagesToRun,
        LongRunningStagesBefore = oneToSetMap2,
        LastStages = daemonStageSet2
      };
    }

    [NotNull]
    OneToSetMap<IDaemonStage, IDaemonStage> CollectBeforeStages(
      [NotNull] HashSet<IDaemonStage> stagesToRun,
      [CanBeNull] HashSet<IDaemonStage> completedStages)
    {
      OneToSetMap<IDaemonStage, IDaemonStage> oneToSetMap = new OneToSetMap<IDaemonStage, IDaemonStage>((IEqualityComparer<IDaemonStage>) SystemObjectEqualityComparer<IDaemonStage>.Instance);
      foreach (IDaemonStage daemonStage1 in stagesToRun)
      {
        foreach (IDaemonStage daemonStage2 in this.myDaemonStagesManager.GetBeforeStagesFor(daemonStage1))
        {
          if ((completedStages == null || !completedStages.Contains(daemonStage2)) && stagesToRun.Contains(daemonStage2))
            oneToSetMap.Add(daemonStage1, daemonStage2);
        }
      }
      return oneToSetMap;
    }

    void EnablesDeactivatedStagesRequiredByStagesBeforeDependencies(
      [NotNull] Dictionary<IDaemonStage, bool> stages)
    {
      using (PooledHashSet<IDaemonStage> pooledHashSet = DaemonProcessBase.ourStagesHashSetPool.Allocate())
      {
        foreach (KeyValuePair<IDaemonStage, bool> stage1 in stages)
        {
          IDaemonStage key1;
          bool flag1;
          stage1.Deconstruct<IDaemonStage, bool>(out key1, out flag1);
          IDaemonStage stage2 = key1;
          if (flag1)
          {
            foreach (IDaemonStage key2 in this.myDaemonStagesManager.GetTransitiveBeforeOnlyStagesFor(stage2))
            {
              bool flag2;
              if (stages.TryGetValue(key2, out flag2) && !flag2)
                pooledHashSet.Add(key2);
            }
          }
        }
        foreach (IDaemonStage key in (HashSet<IDaemonStage>) pooledHashSet)
          stages[key] = true;
      }
    }
  }

  [CanBeNull]
  private static Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> CreateTransitWrapper(
    [CanBeNull] Action<DaemonProcessBase.DaemonCommitContext> committer)
  {
    return committer == null ? (Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext>) null : (Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext>) ((_, context) => committer(context));
  }

  private void RunStage(
    [NotNull] IDaemonStage stage,
    DaemonProcessKind processKind,
    [CanBeNull] Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> committer,
    [NotNull] IContextBoundSettingsStore settingsStore,
    ContentModelFork.ThreadTransitionCookie threadTransitionCookie,
    [CanBeNull] DaemonHighlightingStatistics highlightingStatistics = null)
  {
    using (CompilationContextCookie.GetOrCreate(this.SourceFile.ResolveContext))
    {
      using (ContentModelFork.SetupForkForCurrentChildReadThread(threadTransitionCookie))
      {
        using (DaemonUtil.SetCurrentRunningStageForAsserts(stage))
        {
          using (StartupMeasurer.LocalScopeCookie(stage.GetType().Name, this.GetType(), DaemonProcessBase.StartupMeasureGroupId, category: nameof (RunStage)))
          {
            using (this.ProfilingBag.MeasureTime((object) stage))
            {
              if (this.InterruptFlag)
                throw new OperationCanceledException();
              LocalStopwatch localStopwatch = LocalStopwatch.StartNew();
              Type stageId = stage.GetType();
              IDaemonStageProcess[] daemonStageProcessArray = stage.CreateProcess((IDaemonProcess) this, settingsStore, processKind).AsArray<IDaemonStageProcess>();
              if (daemonStageProcessArray.Length != 0)
              {
                try
                {
                  foreach (IDaemonStageProcess daemonStageProcess1 in daemonStageProcessArray)
                  {
                    IDaemonStageProcess daemonStageProcess = daemonStageProcess1;
                    Stopwatch stageProcessStopWatch = Stopwatch.StartNew();
                    daemonStageProcess.Execute((Action<DaemonStageResult>) (result =>
                    {
                      if (committer == null)
                        return;
                      committer(stage, this.CreateCommitContext(stageId, DaemonProcessBase.AdjustLayerFromProcess(DaemonProcessBase.DaemonProcessKindToLayer(processKind), daemonStageProcess), settingsStore, result));
                      highlightingStatistics?.RegisterProcessCommitted(stageProcessStopWatch.ElapsedTicks);
                    }));
                  }
                }
                catch (Exception ex) when (!ex.IsOperationCanceled())
                {
                  this.ProfilingBag.ReportException((object) stage);
                  Logger.LogException(ex);
                }
                if (processKind != DaemonProcessKind.GLOBAL_WARNINGS)
                {
                  lock (this.myProcessTypeToProcess.NotNull<Dictionary<Type, IDaemonStageProcess[]>>("myProcessTypeToProcess"))
                  {
                    if (daemonStageProcessArray[0] is MultiFileDaemonStageProcess daemonStageProcess)
                    {
                      IDaemonStageProcess[] array = daemonStageProcess.Processes.ToArray<IDaemonStageProcess>();
                      this.myProcessTypeToProcess.Add(array[0].GetType(), array);
                    }
                    else
                      this.myProcessTypeToProcess.Add(daemonStageProcessArray[0].GetType(), daemonStageProcessArray);
                  }
                }
                highlightingStatistics?.RegisterStageCompleted(localStopwatch.ElapsedTicks);
              }
              else
              {
                if (committer == null)
                  return;
                Type[] stagesOverridenBy = this.myDaemonStagesManager.GetStagesOverridenBy(stageId);
                if (stage is IMultiLayerDaemonStage layerDaemonStage)
                {
                  IReadOnlyList<byte> usedLayers = layerDaemonStage.UsedLayers;
                  if (usedLayers != null)
                  {
                    using (IEnumerator<byte> enumerator = usedLayers.GetEnumerator())
                    {
                      while (enumerator.MoveNext())
                      {
                        byte current = enumerator.Current;
                        committer(stage, new DaemonProcessBase.DaemonCommitContext(this.Document, stageId, current, stagesOverridenBy));
                      }
                      return;
                    }
                  }
                }
                committer(stage, new DaemonProcessBase.DaemonCommitContext(this.Document, stageId, (byte) 0, stagesOverridenBy));
              }
            }
          }
        }
      }
    }
  }

  protected abstract void FilePartlyReanalyzed(
    IPsiSourceFile sourceFile,
    DaemonProcessBase daemonProcessBase,
    DaemonProcessKind processKind);

  protected abstract void AnalysisCompleted(
    IPsiSourceFile sourceFile,
    [NotNull] DaemonProcessBase daemonProcessBase,
    DependencySet dependencies,
    bool analysisSupported,
    DaemonProcessKind processKind);

  protected abstract void AnalysisStageCompleted(
    IPsiSourceFile sourceFile,
    IDaemonStage stage,
    byte layer,
    List<HighlightingInfo> stageHighlightings,
    bool stageFullRehighlight,
    List<DocumentRange> stageRanges,
    DaemonProcessKind processKind,
    IContextBoundSettingsStore settingsStore);

  protected abstract bool ShouldNotifySwea(IPsiSourceFile sourceFile);

  [Pure]
  [NotNull]
  private DaemonProcessBase.DaemonCommitContext CreateCommitContext(
    [NotNull] Type stageId,
    IHighlightingOverlapResolver.Layer layer,
    [NotNull] IContextBoundSettingsStore settingsStore,
    [CanBeNull] DaemonStageResult stageResult)
  {
    Type[] stagesOverridenBy = this.myDaemonStagesManager.GetStagesOverridenBy(stageId);
    if (stageResult == null)
      return new DaemonProcessBase.DaemonCommitContext(this.Document, stageId, (byte) 0, stagesOverridenBy);
    IReadOnlyCollection<HighlightingInfo> overlappedPreviousHighlightingInfos;
    IReadOnlyCollection<HighlightingInfo> restoredPreviousHighlightingInfos;
    if (stageResult.Highlightings.Count > 0)
    {
      lock (this.myOverlapResolver)
      {
        DocumentRange invalidationRange = stageResult.FullyRehighlighted ? DocumentRange.InvalidRange : stageResult.RehighlightedRange;
        this.myOverlapResolver.ResolveOverlappedPreviousHighlightings((IReadOnlyCollection<HighlightingInfo>) stageResult.Highlightings, stageResult.FullyRehighlighted, invalidationRange, layer, settingsStore, (Func<bool>) (() => this.InterruptFlag), out overlappedPreviousHighlightingInfos, out restoredPreviousHighlightingInfos);
      }
    }
    else
    {
      overlappedPreviousHighlightingInfos = (IReadOnlyCollection<HighlightingInfo>) EmptyList<HighlightingInfo>.Instance;
      restoredPreviousHighlightingInfos = (IReadOnlyCollection<HighlightingInfo>) EmptyList<HighlightingInfo>.Instance;
    }
    return new DaemonProcessBase.DaemonCommitContext(this.Document, stageId, stageResult, overlappedPreviousHighlightingInfos, restoredPreviousHighlightingInfos, stagesOverridenBy, stageResult.OnCommitted);
  }

  private class StagesToRun
  {
    [NotNull]
    public required HashSet<IDaemonStage> ImmediateStages { get; init; }

    [NotNull]
    public required OneToSetMap<IDaemonStage, IDaemonStage> ImmediateStagesBefore { get; init; }

    [NotNull]
    public required HashSet<IDaemonStage> LongRunningStages { get; init; }

    [NotNull]
    public required OneToSetMap<IDaemonStage, IDaemonStage> LongRunningStagesBefore { get; init; }

    [NotNull]
    public required HashSet<IDaemonStage> LastStages { get; init; }
  }

  protected sealed class DaemonCommitContext : IDaemonCommitContext
  {
    [NotNull]
    public readonly IDocument Document;
    public readonly (Type StageType, byte Layer) StageId;
    public readonly bool FullRehighlight;
    public readonly DocumentRange RehighlightedRange;
    [NotNull]
    public readonly IReadOnlyList<HighlightingInfo> HighlightingsToAdd;
    [NotNull]
    public readonly IReadOnlyCollection<HighlightingInfo> OverlappedPreviousHighlightings;
    [NotNull]
    public readonly IReadOnlyCollection<HighlightingInfo> RestoredPreviousHighlightings;

    [CanBeNull]
    public Action OnCommitted { get; }

    public List<(Type StageType, byte Layer)> OverridenStage { get; }

    internal DaemonCommitContext(
      [NotNull] IDocument document,
      [NotNull] Type stageId,
      byte layer,
      [NotNull] Type[] overridenStage)
    {
      this.OverridenStage = overridenStage.ToList<Type, (Type, byte)>((Func<Type, (Type, byte)>) (t => (t, layer)));
      this.Document = document;
      this.StageId = (stageId, layer);
      this.RehighlightedRange = DocumentRange.InvalidRange;
      this.FullRehighlight = true;
      this.OverlappedPreviousHighlightings = (IReadOnlyCollection<HighlightingInfo>) EmptyList<HighlightingInfo>.Instance;
      this.RestoredPreviousHighlightings = (IReadOnlyCollection<HighlightingInfo>) EmptyList<HighlightingInfo>.Instance;
      this.HighlightingsToAdd = (IReadOnlyList<HighlightingInfo>) EmptyList<HighlightingInfo>.Instance;
    }

    internal DaemonCommitContext(
      [NotNull] DaemonProcessBase.DaemonCommitContext other,
      [NotNull] Func<HighlightingInfo, bool> filter)
    {
      this.Document = other.Document;
      this.OverridenStage = other.OverridenStage;
      this.StageId = other.StageId;
      this.HighlightingsToAdd = (IReadOnlyList<HighlightingInfo>) other.HighlightingsToAdd.Where<HighlightingInfo>(filter).ToList<HighlightingInfo>();
      this.RehighlightedRange = other.RehighlightedRange;
      this.FullRehighlight = other.FullRehighlight;
      this.OverlappedPreviousHighlightings = other.OverlappedPreviousHighlightings;
      this.RestoredPreviousHighlightings = other.RestoredPreviousHighlightings;
      this.OnCommitted = other.OnCommitted;
    }

    internal DaemonCommitContext(
      [NotNull] IDocument document,
      [NotNull] Type stageId,
      [NotNull] DaemonStageResult result,
      [NotNull] IReadOnlyCollection<HighlightingInfo> overlappedPreviousHighlightings,
      [NotNull] IReadOnlyCollection<HighlightingInfo> restoredPreviousHighlightings,
      [NotNull] Type[] overridenStage,
      [CanBeNull] Action onCommitted)
    {
      this.OnCommitted = onCommitted;
      this.Document = document;
      this.OverridenStage = ((IEnumerable<Type>) overridenStage).Select<Type, (Type, byte)>((Func<Type, (Type, byte)>) (t => (t, result.Layer))).ToList<(Type, byte)>();
      this.StageId = (stageId, result.Layer);
      this.HighlightingsToAdd = result.Highlightings;
      this.RehighlightedRange = result.RehighlightedRange;
      this.FullRehighlight = result.FullyRehighlighted;
      this.OverlappedPreviousHighlightings = overlappedPreviousHighlightings;
      this.RestoredPreviousHighlightings = restoredPreviousHighlightings;
    }

    (Type StageType, byte Layer) IDaemonCommitContext.StageId => this.StageId;

    bool IDaemonCommitContext.FullRehighlight => this.FullRehighlight;

    DocumentRange IDaemonCommitContext.RehighlightedRange => this.RehighlightedRange;

    IReadOnlyList<HighlightingInfo> IDaemonCommitContext.HighlightingsToAdd
    {
      get => this.HighlightingsToAdd;
    }

    public override string ToString()
    {
      return $"{this.StageId} ({(this.FullRehighlight ? "full" : "incremental " + this.RehighlightedRange.TextRange.ToString())}, to add = {this.HighlightingsToAdd.Count}, doc = {this.Document.GetShortDocumentMoniker()})";
    }

    [NotNull]
    public DaemonProcessBase.DaemonCommitContext WithFilter([NotNull] Func<HighlightingInfo, bool> filter)
    {
      return new DaemonProcessBase.DaemonCommitContext(this, filter);
    }
  }

  private class StageResultForSWEA
  {
    public readonly List<HighlightingInfo> Highlightings;
    public readonly List<DocumentRange> RehighlightedRanges;
    public bool FullRehighlight;

    internal StageResultForSWEA(
      [NotNull] DaemonProcessBase.DaemonCommitContext daemonCommitContext)
    {
      this.Highlightings = daemonCommitContext.HighlightingsToAdd.ToList<HighlightingInfo>();
      this.RehighlightedRanges = new List<DocumentRange>(1)
      {
        daemonCommitContext.RehighlightedRange
      };
      this.FullRehighlight = daemonCommitContext.FullRehighlight;
    }

    public void Merge(
      [NotNull] DaemonProcessBase.DaemonCommitContext daemonCommitContext)
    {
      this.Highlightings.AddRange((IEnumerable<HighlightingInfo>) daemonCommitContext.HighlightingsToAdd);
      this.RehighlightedRanges.Add(daemonCommitContext.RehighlightedRange);
      this.FullRehighlight |= daemonCommitContext.FullRehighlight;
    }
  }

  private class StageResultForSWEACollector : 
    IEnumerable<KeyValuePair<(IDaemonStage Stage, byte Layer), DaemonProcessBase.StageResultForSWEA>>,
    IEnumerable
  {
    private readonly Dictionary<(IDaemonStage Stage, byte Layer), DaemonProcessBase.StageResultForSWEA> myPerStageResults = new Dictionary<(IDaemonStage, byte), DaemonProcessBase.StageResultForSWEA>();

    [NotNull]
    public Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext> WrapCommitter(
      [CanBeNull] Action<DaemonProcessBase.DaemonCommitContext> committer)
    {
      return (Action<IDaemonStage, DaemonProcessBase.DaemonCommitContext>) ((stage, context) =>
      {
        Action<DaemonProcessBase.DaemonCommitContext> action = committer;
        if (action != null)
          action(context);
        byte layer = context.StageId.Layer;
        lock (this.myPerStageResults)
        {
          DaemonProcessBase.StageResultForSWEA stageResultForSwea;
          if (this.myPerStageResults.TryGetValue((stage, layer), out stageResultForSwea))
            stageResultForSwea.Merge(context);
          else
            this.myPerStageResults.Add((stage, layer), new DaemonProcessBase.StageResultForSWEA(context));
        }
      });
    }

    public IEnumerator<KeyValuePair<(IDaemonStage Stage, byte Layer), DaemonProcessBase.StageResultForSWEA>> GetEnumerator()
    {
      return (IEnumerator<KeyValuePair<(IDaemonStage, byte), DaemonProcessBase.StageResultForSWEA>>) this.myPerStageResults.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => (IEnumerator) this.GetEnumerator();
  }

  /// A wrapper around <see cref="T:JetBrains.Application.IInterruptionSource" />
  ///  which don't issue real interrupts, only forcing the current
  ///             interruption handler to recheck the status of other interrupts in the list by switching state to the dirty.
  /// A wrapper around <see cref="T:JetBrains.Application.IInterruptionSource" />
  ///  which don't issue real interrupts, only forcing the current
  ///             interruption handler to recheck the status of other interrupts in the list by switching state to the dirty.
  private class NoInterruptWrapper(IInterruptionSource impl) : IInterruptionSource
  {
    public bool IsPolling => impl.IsPolling;

    public void Subscribe(Interruption.InterruptionHandler handler) => impl.Subscribe(handler);

    public void Unsubscribe(Interruption.InterruptionHandler handler) => impl.Unsubscribe(handler);

    public bool CheckInterrupt() => false;
  }
}
