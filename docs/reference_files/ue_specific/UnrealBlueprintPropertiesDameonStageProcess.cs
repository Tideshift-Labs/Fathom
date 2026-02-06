// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintPropertiesDaemonStageProcess
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Cpp.Language;
using JetBrains.ReSharper.Psi.Cpp.Symbols;
using JetBrains.ReSharper.Psi.Cpp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

[HighlightingSource(HighlightingTypes = new Type[] {typeof (IUnrealBlueprintHighlightingProvider)})]
public class UnrealBlueprintPropertiesDaemonStageProcess(
  [NotNull] 
  #nullable disable
  IDaemonProcess daemonProcess,
  [NotNull] CppFile file,
  [NotNull] ILogger logger,
  string moduleName,
  [NotNull] OccurrenceFactory occurrenceFactory,
  [NotNull] UnrealBlueprintPropertiesDaemonStage owner) : 
  UnrealBlueprintDaemonStageProcessBase<UnrealBlueprintPropertiesDaemonStage>(daemonProcess, file, logger, moduleName, occurrenceFactory, owner)
{
  protected override bool IsEnabledInSettings()
  {
    return base.IsEnabledInSettings() && this.myHighlightingProvider.NotNull<IUnrealBlueprintHighlightingProvider>("myHighlightingProvider").IsPropertiesProviderEnabled(this.DaemonProcess.ContextBoundSettingsStore);
  }

  public override bool InteriorShouldBeProcessed(ITreeNode element, IHighlightingConsumer context)
  {
    if (!(element is IClassOrEnumSpecifier classOrEnumSpecifier))
      return true;
    return classOrEnumSpecifier is ClassSpecifier classSpecifier && UE4Util.IsLooksLikeUClass(classSpecifier.GetClassResolveEntity());
  }

  protected override void ProcessSymbol(ICppParserSymbol symbol, IHighlightingConsumer context)
  {
    if (!(this.myFile.FileSymbolsCache?.TryFindResolveEntityBySymbol((ICppSymbol) symbol) is ICppVariableDeclaratorResolveEntity resolveEntityBySymbol) || !UE4Util.IsLooksLikeModifiableUProperty(resolveEntityBySymbol) && !UE4Util.IsConfigUProperty(resolveEntityBySymbol))
      return;
    List<IUE4SearchTarget> targets = UE4SearchUtil.BuildUESearchTargets(resolveEntityBySymbol, this.mySolution, this.myModuleName).ToList<IUE4SearchTarget>();
    VirtualFileSystemPath location = this.myFile.GetSourceFile().GetLocation();
    DocumentRange documentRange = symbol.LocateDocumentRange(this.mySolution);
    IHighlighting propertyHighlighting = this.myHighlightingProvider.CreatePropertyHighlighting((IDeclaredElement) new CppParserSymbolDeclaredElement(this.mySolution.GetPsiServices(), symbol), new Func<IEnumerable<IUnrealOccurence>>(OccurrencesCalculator), in documentRange, this.DaemonProcess.ContextBoundSettingsStore, this.IsAssetCacheReady);
    context.AddHighlighting(propertyHighlighting);

    IEnumerable<IUnrealOccurence> OccurrencesCalculator()
    {
      return this.myOwner.Searcher.FindPossibleReadWriteResults((IList<IUE4SearchTarget>) targets, this.myAssetAccessorCache, true).Concat<UnrealFindResult>((IEnumerable<UnrealFindResult>) this.myOwner.IniSearcher.FindUPropertyWriteUsages(location, targets.OfType<UE4SearchFieldTarget>())).SelectNotNull<UnrealFindResult, IUnrealOccurence>((Func<UnrealFindResult, IUnrealOccurence>) (result => (this.myOccurrenceFactory.MakeOccurrence((FindResult) result) as IUnrealOccurence).NotNull<IUnrealOccurence>("occurrence as IUnrealOccurence != null"))).Take<IUnrealOccurence>(this.MaxOccurrences);
    }
  }
}
