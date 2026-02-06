// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Daemon.SolutionAnalysis.CollectInspectionResults
// Assembly: JetBrains.ReSharper.SolutionAnalysis, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: D3FF7F3F-9DA4-4480-A0E6-46DCBFE2FB82
// Assembly location: C:\Users\kvira\.nuget\packages\jetbrains.psi.features.src\253.0.20260129.45253\DotFiles\JetBrains.ReSharper.SolutionAnalysis.dll
// XML documentation location: C:\Users\kvira\.nuget\packages\jetbrains.psi.features.src\253.0.20260129.45253\DotFiles\JetBrains.ReSharper.SolutionAnalysis.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.I18n;
using JetBrains.Application.Progress;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.Application.Threading.Tasks;
using JetBrains.Diagnostics;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.Impl;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Issues;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Resources;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection;
using JetBrains.ReSharper.Daemon.UsageChecking;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Resources.Shell;
using JetBrains.Threading;
using JetBrains.Util;
using JetBrains.Util.DataStructures.Collections;
using JetBrains.Util.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

#nullable disable
namespace JetBrains.ReSharper.Daemon.SolutionAnalysis;

public class CollectInspectionResults
{
  private readonly ISolution mySolution;
  private readonly LifetimeDefinition myLifetimeDefinition;
  private readonly IProgressIndicator myProgress;

  public CollectInspectionResults(
    ISolution solution,
    LifetimeDefinition lifetimeDefinition,
    IProgressIndicator progress)
  {
    this.mySolution = solution;
    this.myLifetimeDefinition = lifetimeDefinition;
    this.myProgress = progress;
  }

  public void RunGlobalInspections(
    [NotNull] ITaskExecutor taskExecutor,
    [NotNull] Stack<IPsiSourceFile> filesToAnalyze,
    [NotNull] Action<List<IssuePointer>> consumer,
    [CanBeNull] IssueTypeGroup issueTypeGroup = null)
  {
    DaemonBase instance = DaemonBase.GetInstance(this.mySolution);
    SolutionAnalysisManager solutionAnalysisManager = SolutionAnalysisManager.GetInstance(this.mySolution).NotNull<SolutionAnalysisManager>("SolutionAnalysisManager.GetInstance(mySolution)");
    SolutionAnalysisConfiguration sweaConf = this.mySolution.GetComponent<SolutionAnalysisConfiguration>();
    using (instance.Suspend())
    {
      using (WriteLockCookie.Create("Src\\CollectInspectionResults.cs", nameof (RunGlobalInspections)))
        ;
      instance.InterruptFlag = false;
      SolutionAnalysisService sweaService = SolutionAnalysisService.GetInstance(this.mySolution);
      DaemonEnablePolicy component = this.mySolution.GetComponent<DaemonEnablePolicy>();
      SweaWarningsMode sweaWarningsMode = sweaConf.WarningsMode.Value;
      List<IPsiSourceFile> filesToAnalyze1 = new List<IPsiSourceFile>();
      List<IPsiSourceFile> collectIssuesFromSolutionAnalysisFiles = new List<IPsiSourceFile>();
      List<IPsiSourceFile> filesToAnalyze2 = new List<IPsiSourceFile>();
      using (ReadLockCookie.Create("Src\\CollectInspectionResults.cs", nameof (RunGlobalInspections)))
      {
        foreach (IPsiSourceFile sourceFile in filesToAnalyze)
        {
          if ((sourceFile != null ? sourceFile.ToProjectFile() : (IProjectFile) null) != null)
          {
            if (CollectInspectionResults.IsFileForGlobalAnalysis(sourceFile))
              filesToAnalyze1.Add(sourceFile);
            if (component.IsSwaEnabled(sourceFile) && sweaWarningsMode != SweaWarningsMode.DoNotShowAndDoNotRun)
              collectIssuesFromSolutionAnalysisFiles.Add(sourceFile);
            else
              filesToAnalyze2.Add(sourceFile);
          }
        }
      }
      if (!taskExecutor.ExecuteTask(Strings.WaitingForSolutionWideAnalysis_Text, TaskCancelable.Yes, (Action<IProgressIndicator>) (progress2 =>
      {
        using (ReadLockCookie.Create("Src\\CollectInspectionResults.cs", nameof (RunGlobalInspections)))
        {
          progress2.Start(10);
          using (IProgressIndicator subProgress = progress2.CreateSubProgress(8.0))
            CollectInspectionResults.EnsureSweaIsCompleted(this.mySolution, sweaConf, sweaService, subProgress);
          IIssueSet issueSet = solutionAnalysisManager.IssueSet;
          ((HighlightingResultsMap) issueSet).SyncUpdate();
          using (IProgressIndicator subProgress = progress2.CreateSubProgress(2.0))
            consumer(this.CollectIssuesFromSolutionInspectionResults((ICollection<IPsiSourceFile>) collectIssuesFromSolutionAnalysisFiles, solutionAnalysisManager, issueSet, issueTypeGroup, subProgress));
        }
      })))
      {
        this.myLifetimeDefinition.Terminate();
      }
      else
      {
        IGlobalUsageChecker globalUsageChecker = sweaService.UsageChecker;
        this.myProgress.Start(filesToAnalyze1.Count + filesToAnalyze2.Count);
        List<CollectInspectionResults.AnalyzerTask> analyzerTasks = new List<CollectInspectionResults.AnalyzerTask>();
        if (issueTypeGroup == null || issueTypeGroup.IssueTypes.Any<IIssueType>((Func<IIssueType, bool>) (issueType =>
        {
          bool flag;
          switch (issueType.SolutionAnalysisMode)
          {
            case SolutionAnalysisMode.GlobalInspection:
            case SolutionAnalysisMode.LocalAndGlobalInspection:
              flag = true;
              break;
            default:
              flag = false;
              break;
          }
          return flag;
        })))
        {
          CollectInspectionResults.AnalyzerTask analyzerTask = new CollectInspectionResults.AnalyzerTask((IReadOnlyCollection<IPsiSourceFile>) filesToAnalyze1, (Action<IPsiSourceFile>) (psiSourceFile =>
          {
            List<IssuePointer> consumer1 = new List<IssuePointer>();
            CollectInspectionResults.RunGlobalWarningsDaemon(this.mySolution, this.myProgress, psiSourceFile, globalUsageChecker, CollectInspectionResults.ConsumeIssuesWithFiltering(consumer1, issueTypeGroup), issueTypeGroup, sweaConf, sweaService);
            consumer(consumer1);
          }), false);
          analyzerTasks.Add(analyzerTask);
        }
        if (filesToAnalyze2.Count > 0)
        {
          CollectInspectionResults.AnalyzerTask analyzerTask = new CollectInspectionResults.AnalyzerTask((IReadOnlyCollection<IPsiSourceFile>) filesToAnalyze2, (Action<IPsiSourceFile>) (psiSourceFile =>
          {
            List<IssuePointer> consumer2 = new List<IssuePointer>();
            CollectInspectionResults.RunLocalDaemon(this.myProgress, psiSourceFile, CollectInspectionResults.ConsumeIssuesWithFiltering(consumer2, issueTypeGroup), issueTypeGroup);
            consumer(consumer2);
          }), true);
          analyzerTasks.Add(analyzerTask);
        }
        this.RunInspectionTask((IReadOnlyList<CollectInspectionResults.AnalyzerTask>) analyzerTasks);
      }
    }
  }

  private static void EnsureSweaIsCompleted(
    [NotNull] ISolution solution,
    [NotNull] SolutionAnalysisConfiguration sweaConf,
    [NotNull] SolutionAnalysisService sweaService,
    [NotNull] IProgressIndicator progress)
  {
    if (sweaConf.Completed.Value)
      return;
    using (JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages.GetInstance(solution).DisableCheckThread())
    {
      foreach (IPsiSourceFile sourceFile in sweaService.GetFilesToAnalyze((IInterruptable) new InterruptableOnProgress(progress)).WithProgress<IPsiSourceFile>(progress, Strings.WaitingForSolutionWideAnalysisToComplete_Text, throwOnCancel: true))
      {
        progress.CurrentItemText = Strings.Analyzing__Text.Format((object) sourceFile.Name.NON_LOCALIZABLE());
        using (CompilationContextCookie.GetOrCreate(sourceFile.ResolveContext))
          sweaService.AnalyzeInvisibleFile(sourceFile);
      }
      sweaService.AllFilesAnalyzed();
    }
  }

  public void RunLocalInspections(
    Stack<IPsiSourceFile> filesToAnalyze,
    Action<IPsiSourceFile, List<IssuePointer>> consumer,
    IssueTypeGroup issueTypeGroup = null,
    IContextBoundSettingsStore settingsStore = null)
  {
    this.myProgress.Start(filesToAnalyze.Count);
    this.RunInspectionTask(FixedList.Of<CollectInspectionResults.AnalyzerTask>(new CollectInspectionResults.AnalyzerTask((IReadOnlyCollection<IPsiSourceFile>) filesToAnalyze, new Action<IPsiSourceFile>(Analyzer), true)));

    void Analyzer(IPsiSourceFile file)
    {
      Lifetime lifetime = this.myLifetimeDefinition.Lifetime;
      using (LifetimeDefinition.ExecuteIfAliveCookie executeIfAliveCookie = lifetime.UsingExecuteIfAlive())
      {
        if (!executeIfAliveCookie.Succeed)
          lifetime.ThrowIfNotAlive();
        List<IssuePointer> consumer = new List<IssuePointer>();
        CollectInspectionResults.RunLocalDaemon(this.myProgress, file, CollectInspectionResults.ConsumeIssuesWithFiltering(consumer, issueTypeGroup), issueTypeGroup, settingsStore);
        consumer(file, consumer);
      }
    }
  }

  private void RunInspectionTask(
    [NotNull] IReadOnlyList<CollectInspectionResults.AnalyzerTask> analyzerTasks)
  {
    if (analyzerTasks.Count == 0)
    {
      this.myProgress.Stop();
      this.myLifetimeDefinition.Terminate();
    }
    else
    {
      CollectInspectionResults.AnalyzerTask analyzerTask = analyzerTasks[0];
      IReadOnlyList<CollectInspectionResults.AnalyzerTask> remainingTasks = analyzerTasks.Skip<CollectInspectionResults.AnalyzerTask>(1).ToIReadOnlyList<CollectInspectionResults.AnalyzerTask>();
      AsyncCommitService instance = AsyncCommitService.GetInstance(this.mySolution);
      CollectInspectionResults.RunInspectionCommitClient client = new CollectInspectionResults.RunInspectionCommitClient(this.mySolution, instance, this.myLifetimeDefinition, analyzerTask.FilesToAnalyze, this.myProgress, (Action<IPsiSourceFile>) (psiSourceFile => analyzerTask.Consumer(psiSourceFile)), (Action) (() => this.RunInspectionTask(remainingTasks)), analyzerTask.RunInParallel);
      if (this.mySolution.Locks.Dispatcher.IsAsyncBehaviorProhibited)
      {
        this.mySolution.GetPsiServices().Files.CommitAllDocuments();
        client.BeforeCommit()();
      }
      else
        instance.RequestCommit((IAsyncCommitClient) client);
    }
  }

  public static bool IsFileForGlobalAnalysis([CanBeNull] IPsiSourceFile sourceFile)
  {
    if (sourceFile == null)
      return false;
    foreach (PsiLanguageType language in sourceFile.GetLanguages())
    {
      if (UsageCheckingServiceManager.Instance.GetService(language).GetUnusedDeclarationsSupported())
        return true;
    }
    return false;
  }

  private List<IssuePointer> CollectIssuesFromSolutionInspectionResults(
    [NotNull] ICollection<IPsiSourceFile> files,
    [NotNull] SolutionAnalysisManager solutionAnalysisManager,
    [NotNull] IIssueSet issueSet,
    [CanBeNull] IssueTypeGroup issueTypeGroup,
    [NotNull] IProgressIndicator progress)
  {
    this.mySolution.Locks.AssertReadAccessAllowed();
    List<IssuePointer> consumer = new List<IssuePointer>();
    Action<IssuePointer> issueConsumer = CollectInspectionResults.ConsumeIssuesWithFiltering(consumer, issueTypeGroup);
    foreach (IPsiSourceFile sourceFile in files.WithProgress<IPsiSourceFile>(progress, "Collecting Solution Wide Analysis results".NON_LOCALIZABLE(), throwOnCancel: true))
    {
      if (sourceFile.IsValid())
      {
        progress.CurrentItemText = Strings.Processing__Text.Format((object) sourceFile.Name.NON_LOCALIZABLE());
        using (CompilationContextCookie.GetOrCreate(sourceFile.ResolveContext))
          CollectInspectionResults.CollectIssuesFromSolutionAnalysis(solutionAnalysisManager, issueSet, sourceFile, issueConsumer, issueTypeGroup);
      }
    }
    return consumer;
  }

  public static void CollectIssuesFromSolutionAnalysis(
    [NotNull] SolutionAnalysisManager manager,
    [NotNull] IIssueSet issueSet,
    [NotNull] IPsiSourceFile sourceFile,
    [NotNull] Action<IssuePointer> issueConsumer,
    [CanBeNull] IssueTypeGroup issueTypeGroup = null)
  {
    manager.ReanalyzeIfSettingsChanged(sourceFile);
    IIssueGroup allIssues = issueSet.AllIssues;
    allIssues.RunOperationWithLock((Action) (() =>
    {
      IReadOnlyList<IssuePointer> issues = allIssues.GetIssues(sourceFile.Ptr());
      IssueData[] array = new IssueData[Math.Min(issues.Count, 64 /*0x40*/)];
      int index = 0;
      foreach (IssuePointer issuePointer in (IEnumerable<IssuePointer>) issues)
      {
        if (issueTypeGroup == null || issueTypeGroup.Contains(issuePointer.IssueType))
        {
          if (index >= array.Length)
          {
            array = new IssueData[64 /*0x40*/];
            index = 0;
          }
          array[index] = issuePointer.Array[issuePointer.Index];
          issueConsumer(new IssuePointer(array, index, issuePointer.File, issuePointer.GetSeverity()));
          ++index;
        }
      }
    }));
  }

  public static void RunGlobalWarningsDaemon(
    ISolution solution,
    IProgressIndicator progress,
    IPsiSourceFile sourceFile,
    IGlobalUsageChecker usageChecker,
    Action<IssuePointer> issueConsumer,
    IssueTypeGroup issueTypeGroup = null,
    SolutionAnalysisConfiguration sweaConf = null,
    SolutionAnalysisService sweaService = null)
  {
    if (sourceFile == null)
      return;
    if (sweaService != null && sweaConf != null)
      CollectInspectionResults.EnsureSweaIsCompleted(solution, sweaConf, sweaService, NullProgressIndicator.Create());
    CollectInspectionResults.InspectionDaemon inspectionDaemon = new CollectInspectionResults.InspectionDaemon(sourceFile, DaemonProcessKind.GLOBAL_WARNINGS, issueConsumer, (Func<bool>) (() => progress.IsCanceled), issueTypeGroup);
    inspectionDaemon.CustomData.PutData<IGlobalUsageChecker>(SolutionAnalysisService.UsageCheckerInDaemonProcessKey, usageChecker);
    inspectionDaemon.DoHighlighting();
  }

  private static void RunLocalDaemon(
    IProgressIndicator progress,
    IPsiSourceFile sourceFile,
    Action<IssuePointer> issueConsumer,
    IssueTypeGroup issueTypeGroup = null,
    IContextBoundSettingsStore settingsStore = null)
  {
    if (sourceFile == null)
      return;
    using (CompilationContextCookie.GetOrCreate(sourceFile.GetResolveContext()))
      new CollectInspectionResults.InspectionDaemon(sourceFile, DaemonProcessKind.OTHER, issueConsumer, (Func<bool>) (() => progress.IsCanceled), issueTypeGroup, settingsStore).DoHighlighting();
  }

  [NotNull]
  [Pure]
  private static Action<IssuePointer> ConsumeIssuesWithFiltering(
    [NotNull] List<IssuePointer> consumer,
    [CanBeNull] IssueTypeGroup issueTypeGroup)
  {
    return (Action<IssuePointer>) (issue =>
    {
      if (issue.GetSeverity() == Severity.INFO || issueTypeGroup != null && !issueTypeGroup.Contains(issue.IssueType))
        return;
      consumer.Add(issue);
    });
  }

  private sealed class AnalyzerTask
  {
    [NotNull]
    public readonly IReadOnlyCollection<IPsiSourceFile> FilesToAnalyze;
    [NotNull]
    public readonly Action<IPsiSourceFile> Consumer;
    public readonly bool RunInParallel;

    public AnalyzerTask(
      [NotNull] IReadOnlyCollection<IPsiSourceFile> filesToAnalyze,
      [NotNull] Action<IPsiSourceFile> consumer,
      bool runInParallel)
    {
      this.FilesToAnalyze = filesToAnalyze;
      this.Consumer = consumer;
      this.RunInParallel = runInParallel;
    }
  }

  public class RunInspectionCommitClient : IAsyncCommitClient
  {
    private readonly ISolution mySolution;
    private readonly AsyncCommitService myService;
    private readonly LifetimeDefinition myLifetimeDefinition;
    private readonly HashSet<IPsiSourceFile> myFilesToAnalyze;
    private readonly IProgressIndicator myProgress;
    private readonly Action<IPsiSourceFile> myConsumer;
    private readonly Action myContinuation;
    private readonly bool myRunInParallel;

    public RunInspectionCommitClient(
      ISolution solution,
      AsyncCommitService service,
      LifetimeDefinition lifetimeDefinition,
      IReadOnlyCollection<IPsiSourceFile> filesToAnalyze,
      IProgressIndicator progress,
      Action<IPsiSourceFile> consumer,
      Action continuation,
      bool runInParallel)
    {
      this.mySolution = solution;
      this.myService = service;
      this.myLifetimeDefinition = lifetimeDefinition;
      this.myFilesToAnalyze = new HashSet<IPsiSourceFile>((IEnumerable<IPsiSourceFile>) filesToAnalyze);
      this.myProgress = progress;
      this.myConsumer = consumer;
      this.myContinuation = continuation;
      this.myRunInParallel = runInParallel;
    }

    [NotNull]
    public Action BeforeCommit()
    {
      return (Action) (() =>
      {
        Lifetime lifetime = this.myLifetimeDefinition.Lifetime;
        InterruptionSet interruptionSet = new InterruptionSet(ProgressIndicatorInterruptionSource.Create(this.myProgress));
        InterruptableReadActivityThe activity = new InterruptableReadActivityThe(lifetime, this.mySolution.Locks, interruptionSet, "Src\\CollectInspectionResults.cs", nameof (BeforeCommit))
        {
          FuncRun = this.myRunInParallel ? new Action(this.FunRunInParallel) : new Action(this.FuncRun),
          FuncCancelled = (Action) (() => this.myService.RequestCommit((IAsyncCommitClient) this)),
          FuncCompleted = (Action) (() => this.myContinuation())
        };
        this.mySolution.Locks.Dispatcher.ExecuteAsyncIfAllowedOrSync(lifetime, (Action<Lifetime>) (_ => activity.DoStart()), (Action) (() =>
        {
          activity.FuncRun();
          activity.FuncCompleted();
        }));
      });
    }

    private void FunRunInParallel()
    {
      using (LifetimeDefinition lifetimeDefinition = new LifetimeDefinition())
      {
        Interruption.Current.Add(lifetimeDefinition.Lifetime, ProgressIndicatorInterruptionSource.Create(this.myProgress));
        using (TaskBarrier taskBarrier = this.mySolution.Locks.Tasks.CreateBarrier(lifetimeDefinition.Lifetime, options: TaskCreationOptions.LongRunning))
        {
          ConcurrentQueue<IPsiSourceFile> filesQueue = new ConcurrentQueue<IPsiSourceFile>((IEnumerable<IPsiSourceFile>) this.myFilesToAnalyze);
          uint num = Math.Max(ProcessorUtil.GetProcessorCountWithAffinityMask() - 1U, 2U);
          for (int index = 0; (long) index < (long) num; ++index)
            taskBarrier.EnqueueJob(new Action(RunJob), "Src\\CollectInspectionResults.cs", nameof (FunRunInParallel));

          void RunJob()
          {
            IPsiSourceFile result;
            do
              ;
            while (filesQueue.TryDequeue(out result) && !result.IsValid());
            if (result == null)
              return;
            this.RunConsumer(result);
            taskBarrier.EnqueueJob(new Action(RunJob), "Src\\CollectInspectionResults.cs", nameof (FunRunInParallel));
          }
        }
      }
    }

    private void FuncRun()
    {
      while (!this.myFilesToAnalyze.IsEmpty<IPsiSourceFile>())
      {
        Interruption.Current.CheckAndThrow();
        if (this.myProgress.IsCanceled)
          break;
        IPsiSourceFile file = this.myFilesToAnalyze.First<IPsiSourceFile>();
        if (file.IsValid())
        {
          this.myProgress.CurrentItemText = Strings.AnalyzingFile__Text.Format((object) file.Name.NON_LOCALIZABLE());
          this.RunConsumer(file);
        }
      }
    }

    private void RunConsumer(IPsiSourceFile file)
    {
      try
      {
        this.myConsumer(file);
      }
      catch (OperationCanceledException ex)
      {
        throw;
      }
      catch (Exception ex)
      {
        Logger.LogException(ex);
        lock (this.myFilesToAnalyze)
          this.myFilesToAnalyze.Remove(file);
        this.myProgress.Advance();
        return;
      }
      lock (this.myFilesToAnalyze)
        this.myFilesToAnalyze.Remove(file);
      this.myProgress.Advance();
    }

    public void OnInterrupt() => this.myService.RequestCommit((IAsyncCommitClient) this);
  }

  private class InspectionDaemon : DaemonProcessBaseImpl
  {
    private readonly DaemonProcessKind myDaemonProcessKind;
    private readonly Action<IssuePointer> myIssueConsumer;
    private readonly Func<bool> myIsInterrupted;
    [CanBeNull]
    private readonly HashSet<Type> myHighlightingTypes;

    public InspectionDaemon(
      [NotNull] IPsiSourceFile sourceFile,
      DaemonProcessKind daemonProcessKind,
      [NotNull] Action<IssuePointer> issueConsumer,
      [NotNull] Func<bool> isInterrupted,
      [CanBeNull] IssueTypeGroup issueTypeGroup,
      [CanBeNull] IContextBoundSettingsStore settingsStore = null)
      : base(sourceFile, settingsStore: settingsStore)
    {
      this.myIsInterrupted = isInterrupted;
      this.myDaemonProcessKind = daemonProcessKind;
      this.myIssueConsumer = issueConsumer;
      IPsiServices psiServices = sourceFile.GetPsiServices();
      if (issueTypeGroup == null)
        return;
      this.myHighlightingTypes = new HashSet<Type>();
      foreach (IIssueType issueType in issueTypeGroup.IssueTypes)
      {
        if (issueType is ConfigurableIssueTypeWithHighlightingType highlightingType)
        {
          this.myHighlightingTypes.Add(highlightingType.HighlightingType);
        }
        else
        {
          this.myHighlightingTypes = (HashSet<Type>) null;
          break;
        }
      }
      if (this.myHighlightingTypes == null)
        return;
      this.CustomData.PutData<ISet<Type>>(DaemonCustomDataConstants.SpecificHighlightingTypesKey, (ISet<Type>) this.myHighlightingTypes);
      Predicate<IElementProblemAnalyzer> highlightingTypes = DaemonUtil.GetAnalyzersFilterByHighlightingTypes(psiServices.GetComponent<ElementProblemAnalyzerRegistrar>(), (IReadOnlyCollection<Type>) this.myHighlightingTypes);
      this.CustomData.PutData<Predicate<IElementProblemAnalyzer>>(DaemonCustomDataConstants.AnalyzersFilterKey, highlightingTypes);
    }

    protected override bool ShouldRunStage(IDaemonStage stage)
    {
      return this.myHighlightingTypes == null || stage.CanProduceAnyHighlightingOfType(this.myDaemonStagesManager, (IReadOnlyCollection<Type>) this.myHighlightingTypes);
    }

    public void DoHighlighting()
    {
      List<HighlightingInfo> highlightings = (List<HighlightingInfo>) null;
      Action<DaemonProcessBase.DaemonCommitContext> committer = (Action<DaemonProcessBase.DaemonCommitContext>) null;
      if (this.SourceFile.ToProjectFile() != null)
      {
        highlightings = new List<HighlightingInfo>();
        committer = (Action<DaemonProcessBase.DaemonCommitContext>) (context =>
        {
          lock (highlightings)
            highlightings.AddRange((IEnumerable<HighlightingInfo>) context.HighlightingsToAdd);
        });
      }
      this.DoHighlighting(this.myDaemonProcessKind, committer);
      IssueData[] array = this.Solution.GetComponent<IssueClasses>().BuildIssues((IReadOnlyCollection<HighlightingInfo>) highlightings ?? EmptyList<HighlightingInfo>.Collection, this.SourceFile, this.ContextBoundSettingsStore);
      lock (this.myIssueConsumer)
      {
        for (int index = 0; index < array.Length; ++index)
          this.myIssueConsumer(new IssuePointer(array, index, this.SourceFile.Ptr(), this.ContextBoundSettingsStore, this.Solution));
      }
    }

    public override bool InterruptFlag => this.myIsInterrupted();

    public override bool IsRangeInvalidated(DocumentRange range) => true;

    public override bool FullRehighlightingRequired => true;
  }
}
