// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetsCache
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.I18n;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.Diagnostics;
using JetBrains.Diagnostics.StringInterpolation;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Features.Diagnostics;
using JetBrains.ReSharper.Feature.Services.Cpp.Options;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader;
using JetBrains.ReSharper.Feature.Services.DeferredCaches;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Cpp.Caches;
using JetBrains.Util;
using JetBrains.Util.Caches;
using JetBrains.Util.Maths;
using JetBrains.Util.PersistentMap;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

#nullable enable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset;

/// <summary>
/// Cache for UE asset files (.uasset, .umap).
/// <br />
/// NB: in our codebase the terms "asset" and "blueprint" are used interchangeably most of the time.
/// <para />
/// This component should be used as an entry point for all blueprint-reading activities
/// - e.g. in <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher" />.
/// Use <see cref="M:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetsCache.GetUEAssetFileAccessor(JetBrains.ReSharper.Psi.IPsiSourceFile)" /> to read data from an asset.
/// <para />
/// The cache stores:
/// <ul>
/// <li> <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetData" /> for each asset; </li>
/// <li> Word index for assets </li>
/// <li> Inheritance tree for all blueprint classes in assets; </li>
/// <li> Exceptions thrown during asset parsing.</li>
/// </ul>
/// For each exception, the corresponding <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetExceptionDiagnostic" /> is shown in Problems View.
/// Diagnostic is removed when the file is updated.
/// <br />
/// Parsing exception will be thrown, e.g., if we do not support the new blueprint file version
/// - please keep an eye on ObjectVersion.h in UE sources (ue5-main branch) and add support for new versions.
/// The list of currently supported versions can be found in <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.Properties.UEObjectUE5Version" />.
/// <para />
/// The list of asset files for the cache is provided by <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetAdditionalFilesModuleFactory" />.
/// </summary>
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetFileAccessor" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetExceptionDiagnostic" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetAdditionalFilesModuleFactory" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.DummyUnrealAssetTrigramIndexBuilder" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintDaemonStageBase" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher" />
[PsiComponent(Instantiation.DemandAnyThreadSafe)]
public class UE4AssetsCache : DeferredCacheWithCustomLockBase<
#nullable disable
UE4AssetData>
{
  [NotNull]
  private readonly ISolution mySolution;
  [NotNull]
  private readonly OneToListMap<string, DerivedBlueprintClass> myBaseTypesToInheritors;
  [NotNull]
  private readonly UE4AssetFilesByWordCache myFilesByWordHash;
  [NotNull]
  private readonly Dictionary<IPsiSourceFile, UE4AssetsCache.DiagnosticContainer> myDiagnostics = new Dictionary<IPsiSourceFile, UE4AssetsCache.DiagnosticContainer>();
  [NotNull]
  private readonly Dictionary<int, List<OWORD>> myAddedMergedData = new Dictionary<int, List<OWORD>>();
  [NotNull]
  private readonly Dictionary<int, List<OWORD>> myRemovedMergedData = new Dictionary<int, List<OWORD>>();
  private static uint MAX_LOG_ERROR_COUNT = 100;
  private static uint MAX_LOG_ERROR_PER_FILE_COUNT = 5;
  private uint myErrorCount;
  private readonly bool myIsCacheEnabled;

  protected override string Version => "7:net252-uinterface-support";

  public UE4AssetsCache(
    Lifetime lifetime,
    [NotNull] ISolution solution,
    [NotNull] IPersistentIndexManager persistentIndexManager,
    [NotNull] Lazy<CppIdentifierIntern> lazyIdentifierIntern,
    [NotNull] ILogger logger)
    : base(lifetime, persistentIndexManager, (IUnsafeMarshaller<UE4AssetData>) new UE4UnsafeMarshallerAdaptor<UE4AssetData>(lazyIdentifierIntern, (IUE4Marshaller<UE4AssetData>) UE4AssetDataMarshaller.Instance), logger)
  {
    this.myFilesByWordHash = new UE4AssetFilesByWordCache(persistentIndexManager, lifetime, "UE4AssetsFilesByWordCache", logger);
    this.mySolution = solution;
    this.myIsCacheEnabled = UnrealBlueprintsSupportStatusProvider.IsBlueprintsSupportEnabled(solution);
    this.myBaseTypesToInheritors = new OneToListMap<string, DerivedBlueprintClass>();
    this.Map.Cache = (IDictionaryBasedCache<IPsiSourceFile, UE4AssetData>) new DirectMappedCache<IPsiSourceFile, UE4AssetData>(100);
  }

  public override bool IsApplicable(IPsiSourceFile sourceFile)
  {
    return this.myIsCacheEnabled && sourceFile.LanguageType.Is<UnrealAssetFileType>();
  }

  public override object Build(IPsiSourceFile sourceFile)
  {
    if (!this.IsApplicable(sourceFile))
      return (object) null;
    this.DropDiagnostics(sourceFile);
    UE4AssetData ue4AssetData;
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    return this.GetUEAssetFileAccessor(sourceFile).TryGetValue<UE4AssetData>(UE4AssetsCache.\u003C\u003EO.\u003C0\u003E__FromLinker ?? (UE4AssetsCache.\u003C\u003EO.\u003C0\u003E__FromLinker = new Func<UELinker, UE4AssetData>(UE4AssetData.FromLinker)), out ue4AssetData) ? (object) ue4AssetData : (object) null;
  }

  [NotNull]
  public UEAssetFileAccessor GetUEAssetFileAccessor([NotNull] IPsiSourceFile sourceFile)
  {
    return new UEAssetFileAccessor(sourceFile, this);
  }

  protected override void InvalidateData()
  {
    lock (this.Lock)
    {
      foreach (UE4AssetsCache.DiagnosticContainer diagnosticContainer in this.myDiagnostics.Values)
        diagnosticContainer.LifetimeDefinition.Terminate();
      this.myDiagnostics.Clear();
      this.myBaseTypesToInheritors.Clear();
      this.myFilesByWordHash.Clear();
    }
  }

  protected override void MergeData([NotNull] IPsiSourceFile assetFile, [CanBeNull] UE4AssetData assetData)
  {
    if (assetData == null || !assetFile.IsValid())
      return;
    foreach (UE4AssetData.BlueprintClassObject blueprintClass in assetData.BlueprintClasses)
    {
      DerivedBlueprintClass derivedBlueprintClass = new DerivedBlueprintClass(blueprintClass.Index, blueprintClass.ObjectName, assetFile);
      this.myBaseTypesToInheritors.Add(blueprintClass.SuperClassName, derivedBlueprintClass);
      foreach (string key in blueprintClass.Interfaces)
        this.myBaseTypesToInheritors.Add(key, derivedBlueprintClass);
    }
    foreach (int wordHash in assetData.WordHashes)
      this.myFilesByWordHash.Add(wordHash, this.myPersistentIndexManager[assetFile]);
  }

  protected override void DropData([NotNull] IPsiSourceFile assetFile, UE4AssetData assetData)
  {
    if (assetData == null)
      return;
    foreach (UE4AssetData.BlueprintClassObject blueprintClass in assetData.BlueprintClasses)
    {
      DerivedBlueprintClass derivedBlueprintClass = new DerivedBlueprintClass(blueprintClass.Index, blueprintClass.ObjectName, assetFile);
      this.myBaseTypesToInheritors.Remove(blueprintClass.SuperClassName, derivedBlueprintClass);
      foreach (string key in blueprintClass.Interfaces)
        this.myBaseTypesToInheritors.Remove(key, derivedBlueprintClass);
    }
    foreach (int wordHash in assetData.WordHashes)
      this.myFilesByWordHash.Remove(wordHash, this.myPersistentIndexManager[assetFile]);
  }

  public override void FlushMergeData()
  {
    using (Interruption.Current.Add((IInterruptionSource) LifetimeInterruptionSource.Create(this.myLifetime)))
      this.myFilesByWordHash.Flush();
  }

  [NotNull]
  public ICollection<DerivedBlueprintClass> GetDerivedBlueprintClasses([NotNull] string baseClassName)
  {
    lock (this.Lock)
      return (ICollection<DerivedBlueprintClass>) this.myBaseTypesToInheritors[baseClassName].ToList<DerivedBlueprintClass>();
  }

  internal static int GetWordCode(string word) => CppWordIndexUtil.GetWordCode(word);

  public bool CanContainWord(string word)
  {
    return this.myFilesByWordHash.Count(UE4AssetsCache.GetWordCode(word)) > 0;
  }

  public bool CanContainWord(IPsiSourceFile assetFile, string word)
  {
    return this.myFilesByWordHash.Contains(UE4AssetsCache.GetWordCode(word), this.myPersistentIndexManager[assetFile]);
  }

  public bool CanContainAnyWord(IPsiSourceFile assetFile, IEnumerable<string> words)
  {
    OWORD persistentIndex = this.myPersistentIndexManager[assetFile];
    return words.Any<string>((Func<string, bool>) (word => this.myFilesByWordHash.Contains(UE4AssetsCache.GetWordCode(word), persistentIndex)));
  }

  public IEnumerable<IPsiSourceFile> GetAssetFilesContainingWord([NotNull] string name)
  {
    return (IEnumerable<IPsiSourceFile>) this.myFilesByWordHash.Get(UE4AssetsCache.GetWordCode(name)).Select<OWORD, IPsiSourceFile>((Func<OWORD, IPsiSourceFile>) (id => this.myPersistentIndexManager[id])).Where<IPsiSourceFile>((Func<IPsiSourceFile, bool>) (file => file != null && file.IsValid())).ToList<IPsiSourceFile>();
  }

  public void CollectAssetFilesContainingWord([NotNull] string name, HashSet<IPsiSourceFile> result)
  {
    foreach (OWORD id in this.myFilesByWordHash.Get(UE4AssetsCache.GetWordCode(name)))
    {
      IPsiSourceFile psiSourceFile = this.myPersistentIndexManager[id];
      if (psiSourceFile != null && psiSourceFile.IsValid())
        result.Add(psiSourceFile);
    }
  }

  public void CollectAssetFilesContainingAllWords(
    [NotNull] IList<string> words,
    HashSet<IPsiSourceFile> result)
  {
    if (words.Count == 0)
      return;
    HashSet<OWORD> owordSet = new HashSet<OWORD>((IEnumerable<OWORD>) this.myFilesByWordHash.Get(UE4AssetsCache.GetWordCode(words[0])));
    for (int index = 1; index < words.Count; ++index)
      owordSet.IntersectWith((IEnumerable<OWORD>) this.myFilesByWordHash.Get(UE4AssetsCache.GetWordCode(words[index])));
    foreach (OWORD id in owordSet)
    {
      IPsiSourceFile psiSourceFile = this.myPersistentIndexManager[id];
      if (psiSourceFile != null && psiSourceFile.IsValid())
        result.Add(psiSourceFile);
    }
  }

  private void DropDiagnostics([NotNull] IPsiSourceFile sourceFile)
  {
    lock (this.Lock)
    {
      UE4AssetsCache.DiagnosticContainer diagnosticContainer;
      if (!this.myDiagnostics.TryGetValue(sourceFile, out diagnosticContainer))
        return;
      diagnosticContainer.LifetimeDefinition.Terminate();
      this.myDiagnostics.Remove(sourceFile);
    }
  }

  public void AddDiagnostic([NotNull] IPsiSourceFile sourceFile, [NotNull] Exception exception)
  {
    if (ILoggerEx.IsTraceEnabled(this.Logger))
      this.Logger.Trace(exception);
    if (!this.mySolution.GetSettingsStore((ISettingsStore) null).GetValue<CppUnrealEngineSettingsKey, bool>((Expression<Func<CppUnrealEngineSettingsKey, bool>>) (key => key.ShowBlueprintsInProblemsView)))
      return;
    lock (this.Lock)
    {
      UE4AssetsCache.DiagnosticContainer diagnosticContainer;
      if (!this.myDiagnostics.TryGetValue(sourceFile, out diagnosticContainer))
      {
        diagnosticContainer = new UE4AssetsCache.DiagnosticContainer();
        this.myDiagnostics.Add(sourceFile, diagnosticContainer);
      }
      if ((long) diagnosticContainer.Diagnostics.Count < (long) UE4AssetsCache.MAX_LOG_ERROR_PER_FILE_COUNT && this.myErrorCount < UE4AssetsCache.MAX_LOG_ERROR_COUNT)
      {
        ++this.myErrorCount;
        ILogger logger1 = this.Logger;
        ILogger logger2 = logger1;
        bool isEnabled;
        JetLogErrorInterpolatedStringHandler interpolatedStringHandler = new JetLogErrorInterpolatedStringHandler(21, 2, (ILog) logger1, out isEnabled);
        if (isEnabled)
        {
          interpolatedStringHandler.AppendLiteral("Asset file error ");
          interpolatedStringHandler.AppendFormatted(exception.Message.NotNull<string>("exception.Message").NON_LOCALIZABLE());
          interpolatedStringHandler.AppendLiteral(" at ");
          interpolatedStringHandler.AppendFormatted<VirtualFileSystemPath>(sourceFile.GetLocation());
        }
        ref JetLogErrorInterpolatedStringHandler local = ref interpolatedStringHandler;
        logger2.Error(ref local);
      }
      UEAssetExceptionDiagnostic exceptionDiagnostic = new UEAssetExceptionDiagnostic(sourceFile, exception, sourceFile.GetAggregatedTimestamp());
      diagnosticContainer.Diagnostics.Add(exceptionDiagnostic);
      // ISSUE: object of a compiler-generated type is created
      this.mySolution.GetComponent<IDiagnosticCollector>().Collect(diagnosticContainer.LifetimeDefinition.Lifetime, (IReadOnlyCollection<IDiagnostic>) new \u003C\u003Ez__ReadOnlySingleElementList<IDiagnostic>((IDiagnostic) exceptionDiagnostic));
    }
  }

  public bool HasErrors([NotNull] IPsiSourceFile sourceFile)
  {
    UE4AssetsCache.DiagnosticContainer diagnosticContainer;
    return this.myDiagnostics.TryGetValue(sourceFile, out diagnosticContainer) && !diagnosticContainer.Diagnostics.IsNullOrEmpty<UEAssetExceptionDiagnostic>();
  }

  private class DiagnosticContainer
  {
    public readonly LifetimeDefinition LifetimeDefinition = new LifetimeDefinition();
    public readonly HashSet<UEAssetExceptionDiagnostic> Diagnostics = new HashSet<UEAssetExceptionDiagnostic>(JetBrains.Util.Collections.EqualityComparer.Create<UEAssetExceptionDiagnostic>((Func<UEAssetExceptionDiagnostic, UEAssetExceptionDiagnostic, bool>) ((x, y) => x.AggregatedTimeStamp == y.AggregatedTimeStamp), (Func<UEAssetExceptionDiagnostic, int>) (x => x.AggregatedTimeStamp.GetHashCode())));
  }
}
