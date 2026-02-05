// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintClassesDaemonStageProcess
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Cpp.Language;
using JetBrains.ReSharper.Psi.Cpp.Symbols;
using JetBrains.ReSharper.Psi.Cpp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

[HighlightingSource(HighlightingTypes = new Type[] {typeof (IUnrealBlueprintHighlightingProvider)})]
public class UnrealBlueprintClassesDaemonStageProcess(
  [NotNull] 
  #nullable disable
  IDaemonProcess daemonProcess,
  [NotNull] CppFile file,
  [NotNull] ILogger logger,
  string moduleName,
  [NotNull] OccurrenceFactory occurrenceFactory,
  [NotNull] UnrealBlueprintClassesDaemonStage owner) : 
  UnrealBlueprintDaemonStageProcessBase<UnrealBlueprintClassesDaemonStage>(daemonProcess, file, logger, moduleName, occurrenceFactory, owner)
{
  protected override bool IsEnabledInSettings()
  {
    return base.IsEnabledInSettings() && this.myHighlightingProvider.IsClassProviderEnabled(this.DaemonProcess.ContextBoundSettingsStore);
  }

  public override bool InteriorShouldBeProcessed(ITreeNode element, IHighlightingConsumer context)
  {
    return !(element is IClassOrEnumSpecifier);
  }

  protected override void ProcessSymbol(ICppParserSymbol symbol, IHighlightingConsumer context)
  {
    if (symbol is CppFwdClassSymbol)
      return;
    ICppClassResolveEntity cppClassResolveEntity = this.myFile.FileSymbolsCache?.TryFindResolveEntityBySymbol((ICppSymbol) symbol) as ICppClassResolveEntity;
    if (cppClassResolveEntity == null || !UE4Util.IsLooksLikeUClass(cppClassResolveEntity))
      return;
    Func<IEnumerable<UnrealAssetOccurence>> occurrencesCalculator = (Func<IEnumerable<UnrealAssetOccurence>>) (() => this.myOwner.Searcher.GetGoToInheritorsResults(UE4SearchUtil.BuildUESearchTargets(cppClassResolveEntity, this.mySolution, this.myModuleName, true), cache: this.myAssetAccessorCache).SelectNotNull<UnrealAssetFindResult, UnrealAssetOccurence>((Func<UnrealAssetFindResult, UnrealAssetOccurence>) (result => new UnrealAssetOccurence(result))).Take<UnrealAssetOccurence>(this.MaxOccurrences));
    DocumentRange documentRange = symbol.LocateDocumentRange(this.mySolution);
    IHighlighting classHighlighting = this.myHighlightingProvider.CreateClassHighlighting((IDeclaredElement) new CppParserSymbolDeclaredElement(this.mySolution.GetPsiServices(), symbol), occurrencesCalculator, in documentRange, this.DaemonProcess.ContextBoundSettingsStore, this.IsAssetCacheReady);
    context.AddHighlighting(classHighlighting);
  }
}
