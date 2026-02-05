// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintHighlightingProviderBase
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application.Settings;
using JetBrains.Diagnostics;
using JetBrains.Diagnostics.StringInterpolation;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Cpp.Options;
using JetBrains.ReSharper.Feature.Services.Cpp.Resources;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util.Logging;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

[HighlightingSource]
public abstract class UnrealBlueprintHighlightingProviderBase : IUnrealBlueprintHighlightingProvider
{
  [NotNull]
  protected static string GetTooltipText(string text, bool isComplete)
  {
    return !isComplete ? Strings.ResultsMayBeIncompleteWhileUpdatingAssetIndex : text;
  }

  public abstract IHighlighting CreateClassHighlighting(
    IDeclaredElement declaredElement,
    Func<IEnumerable<UnrealAssetOccurence>> occurrencesCalculator,
    in DocumentRange documentRange,
    IContextBoundSettingsStore store,
    bool isComplete);

  public abstract IHighlighting CreatePropertyHighlighting(
    IDeclaredElement declaredElement,
    Func<IEnumerable<IUnrealOccurence>> occurrencesCalculator,
    in DocumentRange documentRange,
    IContextBoundSettingsStore store,
    bool isComplete);

  public abstract IHighlighting CreateFunctionImplementationsHighlighting(
    IDeclaredElement declaredElement,
    Func<IEnumerable<UnrealAssetOccurence>> occurrencesCalculator,
    in DocumentRange documentRange,
    IContextBoundSettingsStore store,
    bool isComplete);

  public abstract IHighlighting CreateFunctionUsagesHighlighting(
    IDeclaredElement declaredElement,
    Func<IEnumerable<UnrealAssetOccurence>> occurrencesCalculator,
    in DocumentRange documentRange,
    IContextBoundSettingsStore store,
    bool isComplete);

  public virtual bool IsClassProviderEnabled(IContextBoundSettingsStore store)
  {
    return this.IsProviderEnabled(store, (Expression<Func<CppUnrealEngineSettingsKey, bool>>) (key => key.EnableBlueprintClassHighlightings));
  }

  public bool IsFunctionsProviderEnabled(IContextBoundSettingsStore store, bool isCacheReady)
  {
    if (!this.IsProviderEnabled(store, (Expression<Func<CppUnrealEngineSettingsKey, bool>>) (key => key.EnableBlueprintFunctionHighlightings)))
      return false;
    if (this.IsFunctionsImplementationsHighlightingEnabled(store) || this.IsFunctionsUsagesHighlightingEnabled(store) || isCacheReady && this.IsFunctionNotImplementedHighlightingEnabled(store))
      return true;
    return isCacheReady && this.IsFunctionUnusedHighlightingEnabled(store);
  }

  public virtual bool IsPropertiesProviderEnabled(IContextBoundSettingsStore store)
  {
    return this.IsProviderEnabled(store, (Expression<Func<CppUnrealEngineSettingsKey, bool>>) (key => key.EnableBlueprintPropertyHighlightings));
  }

  public abstract bool IsFunctionsImplementationsHighlightingEnabled(
    IContextBoundSettingsStore store);

  public abstract bool IsFunctionsUsagesHighlightingEnabled(IContextBoundSettingsStore store);

  public bool IsFunctionNotImplementedHighlightingEnabled(IContextBoundSettingsStore store)
  {
    return UnrealBlueprintHighlightingProviderBase.IsInspectionEnabled(store, "CppUEBlueprintImplementableEventNotImplemented");
  }

  public bool IsFunctionUnusedHighlightingEnabled(IContextBoundSettingsStore store)
  {
    return UnrealBlueprintHighlightingProviderBase.IsInspectionEnabled(store, "CppUEBlueprintCallableFunctionUnused");
  }

  private static bool IsInspectionEnabled([NotNull] IContextBoundSettingsStore store, [NotNull] string highlightingID)
  {
    SettingsIndexedEntry indexedEntry = store.Schema.GetIndexedEntry<HighlightingSettings, string, Severity>(HighlightingSettingsAccessor.InspectionSeverities);
    object obj = store.GetIndexedValue(indexedEntry, (object) highlightingID, (IDictionary<SettingsKey, object>) null);
    if (obj == null)
    {
      obj = (object) JetBrains.ReSharper.Resources.Shell.Shell.Instance.GetComponent<IHighlightingSettingsManager>().TryGetSeverityItem(highlightingID)?.DefaultSeverity;
      if (obj == null)
      {
        ILog logger1 = (ILog) Logger.GetLogger<UnrealBlueprintHighlightingProviderBase>();
        ILog logger2 = logger1;
        bool isEnabled;
        JetLogErrorInterpolatedStringHandler interpolatedStringHandler = new JetLogErrorInterpolatedStringHandler(27, 1, logger1, out isEnabled);
        if (isEnabled)
        {
          interpolatedStringHandler.AppendLiteral("Failed to get severity for ");
          interpolatedStringHandler.AppendFormatted(highlightingID);
        }
        ref JetLogErrorInterpolatedStringHandler local = ref interpolatedStringHandler;
        logger2.Error(ref local);
        return false;
      }
    }
    return (Severity) obj > Severity.DO_NOT_SHOW;
  }

  private bool IsProviderEnabled(
    [NotNull] IContextBoundSettingsStore store,
    [NotNull] Expression<Func<CppUnrealEngineSettingsKey, bool>> getSettingValue)
  {
    return store.GetValue<CppUnrealEngineSettingsKey, bool>(getSettingValue);
  }
}
