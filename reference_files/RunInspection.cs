// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection
// Assembly: JetBrains.ReSharper.SolutionAnalysis, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 7A6C3549-A05E-4BA2-BFCA-F1A750255DB2
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.SolutionAnalysis.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.SolutionAnalysis.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.ContextNotifications;
using JetBrains.Application.I18n;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.Application.UI.BindableLinq.Collections;
using JetBrains.Application.UI.Controls.TreeView;
using JetBrains.Application.UI.Progress;
using JetBrains.DataFlow;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.ErrorsView;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Resources;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.InspectThis;
using JetBrains.ReSharper.Feature.Services.Navigation.ContextNavigation;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.GeneratedCode;
using JetBrains.ReSharper.Psi.Pointers;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection;

public static class RunInspection
{
  public static void Execute(
  #nullable disable
  IList<IProjectModelElement> scope, IssueTypeGroup issuesToShow = null)
  {
    JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection.ExecuteInBackground(scope[0].GetSolution(), scope, issuesToShow);
  }

  private static void ExecuteInBackground(
    ISolution solution,
    IList<IProjectModelElement> scope,
    IssueTypeGroup issuesToShow = null,
    IRunInspectionResultDescriptor descriptor = null)
  {
    Dictionary<IPsiSourceFile, ProjectModelElementEnvoy> fileToEnvoy = new Dictionary<IPsiSourceFile, ProjectModelElementEnvoy>();
    Stack<IPsiSourceFile> analyze = JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection.CollectFilesToAnalyze(solution, (IEnumerable<IProjectModelElement>) scope);
    LifetimeDefinition calculationLifetime = Lifetime.Define(solution.GetSolutionLifetimes().UntilSolutionCloseLifetime);
    string str1;
    if (scope.Count != 1)
      str1 = Strings.Inspecting_Text;
    else
      str1 = Strings.Inspecting__Text.Format((object) scope[0].Name.NON_LOCALIZABLE());
    string str2 = str1;
    if (descriptor == null)
    {
      descriptor = solution.GetComponent<IInspectionResultDescriptorProvider>().CreateDescriptor(solution, scope, issuesToShow);
      solution.Locks.ExecuteOrQueue((OuterLifetime) solution.GetSolutionLifetimes().UntilSolutionCloseLifetime, "Update inspection result", (Action) (() => solution.GetComponent<IInspectionWindowRegistrar>().ShowContent((IInspectionResultDescriptor) descriptor)), "Src\\RunInspection\\RunInspection.cs", nameof (ExecuteInBackground));
    }
    else
      JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection.ClearDescriptor(solution, descriptor);
    Lifetime lifetime = calculationLifetime.Lifetime;
    lifetime.OnTermination((Action) (() =>
    {
      using (new SyncLockCookie(descriptor.RwLock.WriteLock, "Src\\RunInspection\\RunInspection.cs", nameof (ExecuteInBackground)))
      {
        if (!descriptor.Issues.IsEmpty<object>())
          return;
        descriptor.Issues.Add((object) new NoIssuesFoundFakeNode(Strings.NoIssuesFound_Caption));
      }
    }));
    TreeViewController controller = descriptor as TreeViewController;
    IProgressIndicator progress;
    if (controller != null)
    {
      ProgressIndicator progressIndicator = new ProgressIndicator(descriptor.LifetimeDefinition.Lifetime);
      lifetime = calculationLifetime.Lifetime;
      lifetime.OnTermination((Action) (() => progressIndicator.Cancel()));
      controller.IsBusy.SetValue<bool>(calculationLifetime.Lifetime, true);
      lifetime = calculationLifetime.Lifetime;
      lifetime.OnTermination((Action) (() => controller.Progress.Value = new double?()));
      progressIndicator.Fraction.FlowInto<double, double?>(calculationLifetime.Lifetime, controller.Progress, (Func<double, double?>) (d => d != 0.0 ? new double?(d * 100.0) : new double?()));
      progress = (IProgressIndicator) progressIndicator;
    }
    else
    {
      SearchRequestNotificationModel model = new SearchRequestNotificationModel(calculationLifetime);
      progress = (IProgressIndicator) model.ProgressIndicator;
      solution.GetComponent<IContextNotificationHost>().Create((ContextNotificationModel) model);
    }
    progress.TaskName = str2;
    ICollection<object> occurrences = descriptor.Issues;
    OneToListMap<SourceFilePtr, IssuePointer> issuesToUpdate = descriptor.IssuesToUpdate;
    descriptor.RefreshAction = (Action) (() =>
    {
      if (calculationLifetime.Lifetime.IsAlive)
        calculationLifetime.Terminate();
      JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection.ExecuteInBackground(solution, scope, issuesToShow, descriptor);
    });
    descriptor.StopAction = (Action) (() =>
    {
      if (!calculationLifetime.Lifetime.IsAlive)
        return;
      calculationLifetime.Terminate();
    });
    descriptor.SetCalculationLifetime(calculationLifetime.Lifetime);
    lifetime = descriptor.LifetimeDefinition.Lifetime;
    lifetime.AddDispose((IDisposable) calculationLifetime);
    CollectInspectionResults inspectionResults = new CollectInspectionResults(solution, calculationLifetime, progress);
    if (JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection.RunInspection.ShouldUseSwa(solution, analyze, issuesToShow))
    {
      ITaskExecutor freeThreaded = JetBrains.ReSharper.Resources.Shell.Shell.Instance.GetComponent<UITaskExecutor>().FreeThreaded;
      inspectionResults.RunGlobalInspections(freeThreaded, analyze, new Action<List<IssuePointer>>(OnConsume), issuesToShow);
    }
    else
      inspectionResults.RunLocalInspections(analyze, (Action<IPsiSourceFile, List<IssuePointer>>) ((_, issues) => OnConsume(issues)), issuesToShow);

    void OnConsume(List<IssuePointer> issues)
    {
      Lifetime lifetime;
      while (!descriptor.RwLock.WriteLock.TryAcquire(100, "Src\\RunInspection\\RunInspection.cs", nameof (ExecuteInBackground)))
      {
        lifetime = calculationLifetime.Lifetime;
        if (lifetime.IsNotAlive)
          return;
        InterruptableActivityCookie.CheckAndThrow(progress);
      }
      try
      {
        foreach (IssuePointer issue in issues)
        {
          IPsiSourceFile file = issue.File.File;
          ProjectModelElementEnvoy fileEnvoy;
          if (!fileToEnvoy.TryGetValue(file, out fileEnvoy))
          {
            fileEnvoy = new ProjectModelElementEnvoy((IProjectModelElement) file.ToProjectFile(), file.PsiModule.TargetFrameworkId);
            fileToEnvoy.Add(file, fileEnvoy);
          }
          lifetime = calculationLifetime.Lifetime;
          if (!lifetime.IsNotAlive)
          {
            occurrences.Add((object) IssueOccurrence.Create((IIssue) issue, fileEnvoy));
            issuesToUpdate.Add(issue.File, issue);
          }
        }
      }
      finally
      {
        descriptor.RwLock.WriteLock.Release();
      }
    }
  }

  private static bool ShouldUseSwa(
    [NotNull] ISolution solution,
    [NotNull] Stack<IPsiSourceFile> scope,
    [CanBeNull] IssueTypeGroup issuesToShow = null)
  {
    SolutionAnalysisConfiguration component = solution.GetComponent<SolutionAnalysisConfiguration>();
    return component.Enabled.Value && component.Loaded.Value && (issuesToShow == null || !issuesToShow.IssueTypes.All<IIssueType>((Func<IIssueType, bool>) (x => x.SolutionAnalysisMode == SolutionAnalysisMode.LocalInspectionExcludedFromSolutionAnalysisResults))) && (component.WarningsMode.Value != SweaWarningsMode.DoNotShowAndDoNotRun && component.Completed.Value || issuesToShow == null || scope.Count != 1 || issuesToShow.IssueTypes.Any<IIssueType>((Func<IIssueType, bool>) (issueType =>
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
    })));
  }

  private static void ClearDescriptor(ISolution solution, IRunInspectionResultDescriptor descriptor)
  {
    using (solution.Locks.UsingWriteLock("Src\\RunInspection\\RunInspection.cs", nameof (ClearDescriptor)))
    {
      descriptor.RwLock.WriteLock.Acquire("Src\\RunInspection\\RunInspection.cs", nameof (ClearDescriptor));
      try
      {
        descriptor.Issues.Clear();
        descriptor.IssuesToUpdate.Clear();
      }
      finally
      {
        descriptor.RwLock.WriteLock.Release();
      }
    }
  }

  public static bool IsAvailable(IList<IProjectModelElement> scope)
  {
    if (scope.Count == 0)
      return false;
    CollectFilesVisitor collectFilesVisitor = new CollectFilesVisitor(DaemonExcludedFilesManager.GetInstance(scope[0].GetSolution()), true);
    foreach (IProjectModelElement projectModelElement in (IEnumerable<IProjectModelElement>) scope)
    {
      projectModelElement.Accept((ProjectVisitor) collectFilesVisitor);
      if (collectFilesVisitor.Files.Count > 0)
        return true;
    }
    return false;
  }

  private static Stack<IPsiSourceFile> CollectFilesToAnalyze(
    ISolution solution,
    IEnumerable<IProjectModelElement> scope)
  {
    CollectFilesVisitor collectFilesVisitor = new CollectFilesVisitor(DaemonExcludedFilesManager.GetInstance(solution), false);
    foreach (IProjectModelElement projectModelElement in scope)
      projectModelElement.Accept((ProjectVisitor) collectFilesVisitor);
    return new Stack<IPsiSourceFile>(collectFilesVisitor.Files.SelectNotNull<IProjectFile, IPsiSourceFile>((Func<IProjectFile, IPsiSourceFile>) (pf => pf.ToSourceFile())));
  }
}
