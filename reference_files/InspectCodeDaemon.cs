// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode.InspectCodeDaemon
// Assembly: JetBrains.ReSharper.SolutionAnalysis, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 7A6C3549-A05E-4BA2-BFCA-F1A750255DB2
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.SolutionAnalysis.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.SolutionAnalysis.xml

using JetBrains.DocumentModel;
using JetBrains.ReSharper.Daemon.Impl;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using System;
using System.Collections.Generic;

#nullable enable
namespace JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode;

public class InspectCodeDaemon(
  IssueClasses issueClasses,
  IPsiSourceFile sourceFile,
  JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages fileImages) : 
  DaemonProcessBaseImpl(sourceFile)
{
  public void DoHighlighting(DaemonProcessKind daemonProcessKind, Action<IIssue> issueConsumer)
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
    using (fileImages.DisableCheckThread())
    {
      using (CompilationContextCookie.GetOrCreate(this.SourceFile.ResolveContext))
      {
        this.DoHighlighting(daemonProcessKind, committer);
        IssueData[] array = issueClasses.BuildIssues((IReadOnlyCollection<HighlightingInfo>) highlightings ?? EmptyList<HighlightingInfo>.Collection, this.SourceFile, this.ContextBoundSettingsStore);
        lock (issueConsumer)
        {
          for (int index = 0; index < array.Length; ++index)
            issueConsumer((IIssue) new IssuePointer(array, index, this.SourceFile.Ptr(), this.ContextBoundSettingsStore, this.Solution));
        }
      }
    }
  }

  public override bool InterruptFlag => false;

  public override bool IsRangeInvalidated(DocumentRange range) => true;

  public override bool FullRehighlightingRequired => true;
}
