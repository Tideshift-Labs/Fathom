// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UnrealAssetOccurence
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application.Threading;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.IDE;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Feature.Services.Occurrences.OccurrenceInformation;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.Rider.Model.Notifications;
using JetBrains.UI.Icons;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;

public class UnrealAssetOccurence : IUnrealOccurence, IOccurrence, INavigatable
{
  private OccurrenceMergeContext myMergeContext;

  public UnrealAssetOccurence([NotNull] UnrealAssetFindResult assetFindResult)
  {
    this.AssetFindResult = assetFindResult;
    this.PresentationOptions = OccurrencePresentationOptions.DefaultOptions;
    this.IconId = this.AssetFindResult.AssetFile.ExtensionWithDot == ".umap" ? UnrealBinaryFileTypesThemedIcons.Umap.Id : UnrealBinaryFileTypesThemedIcons.Uasset.Id;
  }

  [NotNull]
  public UnrealAssetFindResult AssetFindResult { get; }

  [NotNull]
  public VirtualFileSystemPath AssetFile => this.AssetFindResult.AssetFile;

  public IconId IconId { get; }

  public bool Navigate(
    ISolution solution,
    PopupWindowContextSource windowContext,
    bool transferFocus,
    TabOptions tabOptions = TabOptions.Default)
  {
    List<IUnrealEngineNavigationProvider> list = solution.GetComponents<IUnrealEngineNavigationProvider>().ToList<IUnrealEngineNavigationProvider>();
    bool flag1 = !list.IsNullOrEmpty<IUnrealEngineNavigationProvider>();
    if (!flag1)
    {
      NotificationsModel notificationModel = solution.TryGetComponent<NotificationsModel>();
      if (notificationModel == null)
        return false;
      IShellLocks locks = solution.Locks;
      NotificationModel notificationEntry = new NotificationModel(solution.GetRdProjectId(), "Unreal asset navigation", $"Unable to navigate to {this.AssetFindResult.AssetFile.Name}. Please ensure that you have plugin 'UnrealLink' installed.", true, RdNotificationEntryType.WARN, new List<NotificationHyperlink>());
      OuterLifetime solutionCloseLifetime = (OuterLifetime) solution.GetSolutionLifetimes().UntilSolutionCloseLifetime;
      Action action = (Action) (() => notificationModel.Notification(notificationEntry));
      locks.Queue(solutionCloseLifetime, "DotNetCoreMessage", action, "Src\\UE4\\UEAsset\\Search\\UnrealAssetOccurence.cs", nameof (Navigate));
    }
    string Guid = "";
    if (this.AssetFindResult is UnrealAssetFindPropertyResult assetFindResult1)
      Guid = assetFindResult1.Guid;
    if (this.AssetFindResult is UnrealAssetFindFunctionResult assetFindResult2)
      Guid = assetFindResult2.Guid;
    bool flag2 = list.Any<IUnrealEngineNavigationProvider>((Func<IUnrealEngineNavigationProvider, bool>) (provider => provider.Navigate(this.AssetFindResult.AssetFile, Guid)));
    if (!flag2)
    {
      NotificationsModel notificationModel = solution.TryGetComponent<NotificationsModel>();
      if (notificationModel == null)
        return false;
      IShellLocks locks = solution.Locks;
      NotificationModel notificationEntry = new NotificationModel(solution.GetRdProjectId(), "Unreal asset navigation", $"Unable to navigate to {this.AssetFindResult.AssetFile.Name}. Please ensure that connection to Unreal Editor is established", true, RdNotificationEntryType.WARN, new List<NotificationHyperlink>());
      OuterLifetime solutionCloseLifetime = (OuterLifetime) solution.GetSolutionLifetimes().UntilSolutionCloseLifetime;
      Action action = (Action) (() => notificationModel.Notification(notificationEntry));
      locks.Queue(solutionCloseLifetime, "DotNetCoreMessage", action, "Src\\UE4\\UEAsset\\Search\\UnrealAssetOccurence.cs", nameof (Navigate));
    }
    return flag1 & flag2;
  }

  public ISolution GetSolution() => this.AssetFindResult.Solution;

  public OccurrenceType OccurrenceType => OccurrenceType.Occurrence;

  public bool IsValid => true;

  public string DumpToString()
  {
    return "C++ class inheritor inside blueprint " + this.AssetFindResult.AssetFile.Name;
  }

  public OccurrencePresentationOptions PresentationOptions { get; set; }

  public override string ToString() => this.AssetFindResult.ToString();

  public OccurrenceKind GetOccurrenceKind() => this.AssetFindResult.OccurrenceKind;

  public OccurrenceMergeContext MergeContext
  {
    get => this.myMergeContext ?? (this.myMergeContext = new OccurrenceMergeContext((object) this));
  }

  public UnrealFindResult FindResult => (UnrealFindResult) this.AssetFindResult;

  public string ObjectName => this.AssetFindResult.ObjectName;

  public JetBrains.UI.RichText.RichText GetDisplayText() => this.AssetFindResult.GetDisplayText();

  public JetBrains.UI.RichText.RichText GetRelatedFilePresentation()
  {
    return this.AssetFindResult.GetRelatedFilePresentation();
  }

  public IconId GetIcon()
  {
    return OccurrencePresentationUtil.GetOccurrenceKindImage((IOccurrence) this, this.GetSolution()).Icon;
  }

  public string GetRelatedFolderPresentation()
  {
    return UE4Util.GetFolderPresentation(this.GetSolution(), this.AssetFile);
  }
}
