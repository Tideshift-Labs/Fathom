// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.Application.Progress;
using JetBrains.Diagnostics;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.Entities.Properties;
using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Cpp.UE4;
using JetBrains.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;

/// <summary>
/// Searches usages of C++ classes/functions/properties in UE asset files.
/// Used e.g. in <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UE4AssetUsagesDomainSpecificSearcher" />
/// and <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.UnrealBlueprintDaemonStageBase" />.
/// Search targets should be created with <see cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UE4SearchUtil" /> in order to account for inheritors and Core Redirects.
/// <para />
/// Main methods:
/// <ul>
/// <li> <see cref="M:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher.GetFindUsagesResults(JetBrains.ReSharper.Psi.IPsiSourceFile,JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.IUE4SearchTarget,System.Boolean,System.Collections.Concurrent.ConcurrentDictionary{JetBrains.ReSharper.Psi.IPsiSourceFile,JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetFileAccessor})" /> </li>
/// <li> <see cref="M:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher.GetGoToInheritorsResults(System.Collections.Generic.IList{JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.IUE4SearchTarget},JetBrains.Application.Progress.IProgressIndicator,System.Collections.Concurrent.ConcurrentDictionary{JetBrains.ReSharper.Psi.IPsiSourceFile,JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetFileAccessor})" /> </li>
/// <li> <see cref="M:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UEAssetUsagesSearcher.FindPossibleReadWriteResults(System.Collections.Generic.IList{JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.IUE4SearchTarget},System.Collections.Concurrent.ConcurrentDictionary{JetBrains.ReSharper.Psi.IPsiSourceFile,JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UEAssetFileAccessor},System.Boolean)" /> </li>
/// </ul>
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetsCache" />
/// <seealso cref="T:JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Config.Search.UEIniUsagesSearcher" />
/// </summary>
[SolutionComponent(Instantiation.DemandAnyThreadSafe)]
public class UEAssetUsagesSearcher
{
  [NotNull]
  private readonly ISolution mySolution;
  [NotNull]
  private readonly UE4AssetsCache myAssetsCache;
  [NotNull]
  private readonly ILogger myLogger;
  private const bool CheckInFiles = true;
  [NotNull]
  private static readonly string[] ourAnimGraphRootNodeFunctions = new string[3]
  {
    "InitialUpdateFunction",
    "BecomeRelevantFunction",
    "UpdateFunction"
  };

  public UEAssetUsagesSearcher([NotNull] ISolution solution, [NotNull] UE4AssetsCache assetsCache, [NotNull] ILogger logger)
  {
    this.mySolution = solution;
    this.myAssetsCache = assetsCache;
    this.myLogger = logger;
  }

  public IEnumerable<UnrealAssetFindResult> GetFindUsagesResults(
    [NotNull] IPsiSourceFile sourceFile,
    [NotNull] IUE4SearchTarget searchTarget,
    bool searchReadOccurrences,
    ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> cache = null)
  {
    if (!UnrealBlueprintsSupportStatusProvider.IsBlueprintsSupportEnabled(this.mySolution))
      return (IEnumerable<UnrealAssetFindResult>) EmptyList<UnrealAssetFindResult>.Instance;
    LogWithLevel? nullable = this.myLogger.WhenTrace();
    ref LogWithLevel? local1 = ref nullable;
    if (local1.HasValue)
      local1.GetValueOrDefault().Log($"Running 'Find Usages' for search target {searchTarget} in source file {sourceFile}");
    Interruption.Current.CheckAndThrow();
    UE4AssetData ue4AssetData = this.myAssetsCache.TryGetValue(sourceFile);
    if (ue4AssetData == null)
      return (IEnumerable<UnrealAssetFindResult>) EmptyList<UnrealAssetFindResult>.Instance;
    switch (searchTarget)
    {
      case UE4SearchFieldTarget searchFieldTarget2:
        if (!AssetContainsWord(searchFieldTarget2.FieldName))
          break;
        goto default;
      case UE4SearchFunctionTarget searchFunctionTarget2:
        if (AssetContainsWord(searchFunctionTarget2.FunctionName))
          goto default;
        break;
      default:
        UEAssetUsagesSearcher.CachedAssetAccessor accessor = new UEAssetUsagesSearcher.CachedAssetAccessor(sourceFile, this.myAssetsCache, cache);
        if (!accessor.IsValid)
          return (IEnumerable<UnrealAssetFindResult>) EmptyList<UnrealAssetFindResult>.Instance;
        List<UnrealAssetFindResult> results = new List<UnrealAssetFindResult>();
        switch (searchTarget)
        {
          case UE4SearchClassTarget searchClassTarget:
            foreach (UE4AssetData.BlueprintClassObject blueprintClass in ue4AssetData.BlueprintClasses)
            {
              this.ProcessPossibleClassInstance(sourceFile, accessor, blueprintClass.Index, blueprintClass.ClassName, searchClassTarget, results);
              this.ProcessPossibleInheritor(sourceFile, accessor, blueprintClass.Index, blueprintClass.SuperClassName, searchClassTarget, results);
            }
            foreach (UE4AssetData.OtherAssetObject otherClass in ue4AssetData.OtherClasses)
              this.ProcessPossibleClassInstance(sourceFile, accessor, otherClass.Index, otherClass.ClassName, searchClassTarget, results);
            break;
          case UE4SearchFieldTarget searchFieldTarget1:
            HashSet<string> hashSet = UE4SearchUtil.GetDerivedBlueprintClasses(searchFieldTarget1.ClassName, this.myAssetsCache).SelectMany<DerivedBlueprintClass, string>((Func<DerivedBlueprintClass, IEnumerable<string>>) (c =>
            {
              UEObjectExport ueObjectExport = accessor.GetObject(c.Index);
              if (ueObjectExport != (UEObjectExport) null && ueObjectExport.IsBlueprintGeneratedClass() && ueObjectExport.ObjectStringName.EndsWith("_C"))
              {
                string[] items = new string[2];
                string objectStringName = ueObjectExport.ObjectStringName;
                int length = "_C".Length;
                items[0] = objectStringName.Substring(0, objectStringName.Length - length);
                items[1] = c.Name;
                // ISSUE: object of a compiler-generated type is created
                return (IEnumerable<string>) new \u003C\u003Ez__ReadOnlyArray<string>(items);
              }
              return (IEnumerable<string>) new string[1]
              {
                c.Name
              };
            })).ToHashSet<string>();
            foreach (UE4AssetData.OtherAssetObject otherClass in ue4AssetData.OtherClasses)
              this.ProcessPossibleFieldWrite(accessor, hashSet, otherClass.Index, otherClass.ClassName, searchFieldTarget1, results);
            foreach (UE4AssetData.K2GraphNodeObject k2VariableSet in ue4AssetData.K2VariableSets)
            {
              if (k2VariableSet.MemberName == searchFieldTarget1.FieldName)
              {
                UEObjectExport ueObjectExport = accessor.GetObject(k2VariableSet.Index);
                if (!(ueObjectExport == (UEObjectExport) null))
                {
                  this.ProcessPossibleSetVariableNode(accessor, hashSet, k2VariableSet.Index, ueObjectExport, searchFieldTarget1, results);
                  if (searchReadOccurrences)
                  {
                    this.ProcessPossibleGetVariableNode(accessor, hashSet, k2VariableSet.Index, ueObjectExport, searchFieldTarget1, results);
                    this.ProcessPossibleAddDelegateNode(accessor, hashSet, k2VariableSet.Index, ueObjectExport, searchFieldTarget1, results);
                    this.ProcessPossibleClearDelegateNode(accessor, hashSet, k2VariableSet.Index, ueObjectExport, searchFieldTarget1, results);
                    this.ProcessPossibleCallDelegateNode(accessor, hashSet, k2VariableSet.Index, ueObjectExport, searchFieldTarget1, results);
                  }
                }
              }
            }
            break;
          default:
            UE4SearchFunctionTarget searchFunctionTarget1 = searchTarget as UE4SearchFunctionTarget;
            if (searchFunctionTarget1 != null & searchReadOccurrences)
            {
              foreach (UE4AssetData.K2GraphNodeObject k2VariableSet in ue4AssetData.K2VariableSets)
              {
                if (k2VariableSet.ObjectKind == UE4AssetData.K2GraphNodeObject.Kind.FunctionCall && k2VariableSet.MemberName == searchFunctionTarget1.FunctionName)
                {
                  UEObjectExport possibleFunctionCallNode = accessor.GetObject(k2VariableSet.Index);
                  if (!(possibleFunctionCallNode == (UEObjectExport) null))
                    this.ProcessPossibleCallFunctionNode(accessor, k2VariableSet.Index, possibleFunctionCallNode, searchFunctionTarget1, results);
                }
              }
              foreach (UE4AssetData.OtherAssetObject otherClass in ue4AssetData.OtherClasses)
                this.ProcessPossibleCallInAnimGraphNodeRoot(accessor, otherClass, searchFunctionTarget1, results);
              break;
            }
            break;
        }
        return (IEnumerable<UnrealAssetFindResult>) results;
    }
    nullable = this.myLogger.WhenTrace();
    ref LogWithLevel? local2 = ref nullable;
    if (local2.HasValue)
      local2.GetValueOrDefault().Log($"Find Usages' for {searchTarget}: source file {sourceFile} does not contain member name");
    return (IEnumerable<UnrealAssetFindResult>) EmptyList<UnrealAssetFindResult>.Instance;

    bool AssetContainsWord(string name) => this.myAssetsCache.CanContainWord(name);
  }

  private void ProcessPossibleCallInAnimGraphNodeRoot(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    UE4AssetData.OtherAssetObject otherClass,
    [NotNull] UE4SearchFunctionTarget searchFunctionTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (otherClass.ClassName != "AnimGraphNode_Root")
      return;
    UEObjectExport objectExport = accessor.GetObject(otherClass.Index);
    if (objectExport == (UEObjectExport) null)
      return;
    IDictionary<string, IUEProperty> taggedProperties = accessor.GetTaggedProperties(objectExport);
    foreach (string rootNodeFunction in UEAssetUsagesSearcher.ourAnimGraphRootNodeFunctions)
    {
      if (taggedProperties.TryGetValue<string, IUEProperty>(rootNodeFunction) is UEPropertiesBasedStructProperty basedStructProperty && basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") is UENameProperty ueNameProperty && ueNameProperty.Value == searchFunctionTarget.FunctionName)
        results.Add((UnrealAssetFindResult) new UnrealAssetFindFunctionInAnimRootNodeResult(accessor.AssetPath, this.mySolution, searchFunctionTarget.FunctionName, otherClass.Index, objectExport.ObjectStringName, rootNodeFunction, accessor.GetGuidProperty(objectExport)));
    }
  }

  private void ProcessPossibleSetVariableNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int nodeIndex,
    [NotNull] UEObjectExport possibleSetVariableNode,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleSetVariableNode.IsK2Node_VariableSet() || !((accessor.GetTaggedProperties(possibleSetVariableNode).TryGetValue<string, IUEProperty>("VariableReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFieldTarget.FieldName)
      return;
    UEPackageIndex outerIndex = possibleSetVariableNode.OuterIndex;
    UEObjectResource reference = outerIndex.Reference;
    UEObjectResource ueObjectResource;
    if (reference == null)
    {
      ueObjectResource = (UEObjectResource) null;
    }
    else
    {
      outerIndex = reference.OuterIndex;
      ueObjectResource = outerIndex.Reference;
    }
    if (ueObjectResource as UEObjectExport == (UEObjectExport) null)
    {
      LogWithLevel? nullable = this.myLogger.WhenTrace();
      ref LogWithLevel? local = ref nullable;
      if (!local.HasValue)
        return;
      local.GetValueOrDefault().Log($"Event graph node '{possibleSetVariableNode}' doesn't contain linkage to global blueprint export");
    }
    else
    {
      string nodeName = this.VerifyAndGetNodeName((ICollection<string>) allDerivedClassNames, possibleSetVariableNode, (IUE4SearchMemberTarget) searchFieldTarget);
      if (nodeName == null)
        return;
      results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, nodeName, nodeIndex, possibleSetVariableNode.ObjectStringName, OccurrenceKind.Write, accessor.GetGuidProperty(possibleSetVariableNode), (IUEProperty) null));
    }
  }

  private void ProcessPossibleCallFunctionNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    int nodeIndex,
    [NotNull] UEObjectExport possibleFunctionCallNode,
    [NotNull] UE4SearchFunctionTarget searchFunctionTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleFunctionCallNode.IsK2Node_CallFunction() || !((accessor.GetTaggedProperties(possibleFunctionCallNode).TryGetValue<string, IUEProperty>("FunctionReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFunctionTarget.FunctionName)
      return;
    if (basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberParent") is UEObjectProperty ueObjectProperty)
    {
      UEObjectResource ueObjectResource = ueObjectProperty.Value;
      string name = VirtualFileSystemPath.Parse(ueObjectResource.OuterIndex.Reference.ObjectStringName, InteractionContext.SolutionContext).Name;
      if (searchFunctionTarget.ClassName == ueObjectResource.ObjectStringName)
      {
        int num = searchFunctionTarget.ModuleName == name ? 1 : 0;
        results.Add((UnrealAssetFindResult) new UnrealAssetFindFunctionResult(accessor.AssetPath, this.mySolution, searchFunctionTarget.FunctionName, OccurrenceKind.Invocation, nodeIndex, possibleFunctionCallNode.ObjectStringName, accessor.GetGuidProperty(possibleFunctionCallNode)));
        return;
      }
    }
    results.Add((UnrealAssetFindResult) new UnrealAssetFindFunctionResult(accessor.AssetPath, this.mySolution, searchFunctionTarget.FunctionName, OccurrenceKind.Invocation, nodeIndex, possibleFunctionCallNode.ObjectStringName, accessor.GetGuidProperty(possibleFunctionCallNode)));
  }

  private void ProcessPossibleGetVariableNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int nodeIndex,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleNode.IsK2Node_VariableGet() || !((accessor.GetTaggedProperties(possibleNode).TryGetValue<string, IUEProperty>("VariableReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFieldTarget.FieldName)
      return;
    string nodeName = this.VerifyAndGetNodeName((ICollection<string>) allDerivedClassNames, possibleNode, (IUE4SearchMemberTarget) searchFieldTarget);
    if (nodeName == null)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, nodeName, nodeIndex, possibleNode.ObjectStringName, OccurrenceKind.Read, accessor.GetGuidProperty(possibleNode), (IUEProperty) null));
  }

  private void ProcessPossibleAddDelegateNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int nodeIndex,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleNode.IsK2Node_AddOrAssetDelegate() || !UEAssetUsagesSearcher.VerifyDelegate(accessor, possibleNode, allDerivedClassNames, (IUE4SearchMemberTarget) searchFieldTarget))
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, possibleNode.ObjectStringName, nodeIndex, possibleNode.ObjectStringName, UnrealCustomDelegateOccurrenceKinds.DelegateBinding, accessor.GetGuidProperty(possibleNode), (IUEProperty) null));
  }

  private void ProcessPossibleClearDelegateNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int nodeIndex,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleNode.IsK2Node_ClearDelegate() || !((accessor.GetTaggedProperties(possibleNode).TryGetValue<string, IUEProperty>("DelegateReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFieldTarget.FieldName)
      return;
    string nodeName = this.VerifyAndGetNodeName((ICollection<string>) allDerivedClassNames, possibleNode, (IUE4SearchMemberTarget) searchFieldTarget);
    if (nodeName == null)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, nodeName, nodeIndex, possibleNode.ObjectStringName, UnrealCustomDelegateOccurrenceKinds.DelegateUnbinding, accessor.GetGuidProperty(possibleNode), (IUEProperty) null));
  }

  private void ProcessPossibleCallDelegateNode(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int nodeIndex,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!possibleNode.IsK2Node_CallDelegate() || !((accessor.GetTaggedProperties(possibleNode).TryGetValue<string, IUEProperty>("DelegateReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFieldTarget.FieldName)
      return;
    string nodeName = this.VerifyAndGetNodeName((ICollection<string>) allDerivedClassNames, possibleNode, (IUE4SearchMemberTarget) searchFieldTarget);
    if (nodeName == null)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, nodeName, nodeIndex, possibleNode.ObjectStringName, UnrealCustomDelegateOccurrenceKinds.DelegateCall, accessor.GetGuidProperty(possibleNode), (IUEProperty) null));
  }

  private static bool VerifyDelegate(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] HashSet<string> allDerivedClassNames,
    [NotNull] IUE4SearchMemberTarget searchFieldTarget)
  {
    if (!((accessor.GetTaggedProperties(possibleNode).TryGetValue<string, IUEProperty>("DelegateReference") is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty) || ueNameProperty.Value != searchFieldTarget.MemberName || !(basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberParent") is UEObjectProperty ueObjectProperty))
      return false;
    UEObjectImport ueObjectImport = (UEObjectImport) ueObjectProperty.Value;
    string name = VirtualFileSystemPath.Parse(ueObjectImport.OuterIndex.Reference.ObjectStringName, InteractionContext.SolutionContext).Name;
    return (searchFieldTarget.ClassName == ueObjectImport.ObjectStringName || allDerivedClassNames.Contains(ueObjectImport.ObjectStringName)) && searchFieldTarget.ModuleName == name;
  }

  [CanBeNull]
  private string VerifyAndGetNodeName(
    [NotNull] ICollection<string> allDerivedClassNames,
    [NotNull] UEObjectExport possibleNode,
    [NotNull] IUE4SearchMemberTarget searchFieldTarget)
  {
    UEPackageIndex outerIndex = possibleNode.OuterIndex;
    UEObjectResource reference1 = outerIndex.Reference;
    UEObjectResource ueObjectResource;
    if (reference1 == null)
    {
      ueObjectResource = (UEObjectResource) null;
    }
    else
    {
      outerIndex = reference1.OuterIndex;
      ueObjectResource = outerIndex.Reference;
    }
    UEObjectExport ueObjectExport1 = ueObjectResource as UEObjectExport;
    if (ueObjectExport1 == (UEObjectExport) null)
    {
      LogWithLevel? nullable = this.myLogger.WhenTrace();
      ref LogWithLevel? local = ref nullable;
      if (local.HasValue)
        local.GetValueOrDefault().Log($"Event graph node '{possibleNode}' doesn't contain linkage to global blueprint export");
      return (string) null;
    }
    string objectStringName = ueObjectExport1.ObjectStringName;
    if (objectStringName != searchFieldTarget.ClassName && !allDerivedClassNames.Contains(objectStringName))
      return (string) null;
    UEObjectExport ueObjectExport2 = ueObjectExport1;
    outerIndex = possibleNode.OuterIndex;
    UEObjectResource reference2 = outerIndex.Reference;
    return $"{ueObjectExport2}.{reference2}";
  }

  private void ProcessPossibleFieldWrite(
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    [NotNull] HashSet<string> allDerivedClassNames,
    int classIndex,
    [NotNull] string className,
    [NotNull] UE4SearchFieldTarget searchFieldTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (!allDerivedClassNames.Contains(className) && className != searchFieldTarget.ClassName)
      return;
    UEObjectExport objectExport = accessor.GetObject(classIndex);
    if (objectExport?.ClassIndex.Reference?.ObjectStringName != className)
      return;
    IUEProperty property = accessor.GetTaggedProperties(objectExport).TryGetValue<string, IUEProperty>(searchFieldTarget.FieldName);
    if (property == null)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindPropertyResult(accessor.AssetPath, this.mySolution, objectExport.ObjectStringName, classIndex, className, OccurrenceKind.Write, accessor.GetGuidProperty(objectExport), property));
  }

  private void ProcessPossibleInheritor(
    [NotNull] IPsiSourceFile sourceFile,
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    int childClassIndex,
    [NotNull] string className,
    [NotNull] UE4SearchClassTarget searchClassTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (className != searchClassTarget.ClassName)
      return;
    UEObjectExport ueObjectExport = accessor.GetObject(childClassIndex);
    if (ueObjectExport == (UEObjectExport) null || !ueObjectExport.IsBlueprintGeneratedClass() || !ueObjectExport.SuperIndex.Exists() || ueObjectExport.SuperIndex.Reference?.ObjectStringName != className)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindClassResult(sourceFile.GetLocation(), this.mySolution, ueObjectExport.SuperIndex.Index, className, OccurrenceKind.ExtendedType));
  }

  private void ProcessPossibleClassInstance(
    [NotNull] IPsiSourceFile sourceFile,
    [NotNull] UEAssetUsagesSearcher.CachedAssetAccessor accessor,
    int classIndex,
    [NotNull] string className,
    [NotNull] UE4SearchClassTarget searchClassTarget,
    [NotNull] List<UnrealAssetFindResult> results)
  {
    if (className != searchClassTarget.ClassName || accessor.GetObject(classIndex)?.ClassIndex.Reference?.ObjectStringName != className)
      return;
    results.Add((UnrealAssetFindResult) new UnrealAssetFindClassResult(sourceFile.GetLocation(), this.mySolution, classIndex, className, OccurrenceKind.NewInstanceCreation));
  }

  [NotNull]
  [ItemNotNull]
  public IEnumerable<UnrealFindResult> FindPossibleReadWriteResults(
    [NotNull, ItemNotNull] IList<IUE4SearchTarget> searchTargets,
    [CanBeNull] ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> cache,
    bool searchReadOccurrences)
  {
    if (UnrealBlueprintsSupportStatusProvider.IsBlueprintsSupportEnabled(this.mySolution) && !searchTargets.IsEmpty<IUE4SearchTarget>())
    {
      Interruption.Current.CheckAndThrow();
      LogWithLevel? nullable = this.myLogger.WhenTrace();
      ref LogWithLevel? local = ref nullable;
      if (local.HasValue)
        local.GetValueOrDefault().Log($"Running 'Find Usages' for search targets [{string.Join<IUE4SearchTarget>(", ", (IEnumerable<IUE4SearchTarget>) searchTargets)}]");
      IEnumerable<string> source;
      if (searchTargets.All<IUE4SearchTarget>((Func<IUE4SearchTarget, bool>) (target => target is UE4SearchFieldTarget)))
        source = searchTargets.OfType<UE4SearchFieldTarget>().Select<UE4SearchFieldTarget, string>((Func<UE4SearchFieldTarget, string>) (target => target.FieldName));
      else if (searchTargets.All<IUE4SearchTarget>((Func<IUE4SearchTarget, bool>) (target => target is UE4SearchFunctionTarget)))
        source = searchTargets.OfType<UE4SearchFunctionTarget>().Select<UE4SearchFunctionTarget, string>((Func<UE4SearchFunctionTarget, string>) (target => target.FunctionName));
      else if (searchTargets.All<IUE4SearchTarget>((Func<IUE4SearchTarget, bool>) (target => target is UE4SearchClassTarget)))
      {
        source = searchTargets.Select<IUE4SearchTarget, string>((Func<IUE4SearchTarget, string>) (target => target.ClassName));
      }
      else
      {
        Assertion.Fail($"searchTargets contains different types: [{string.Join<IUE4SearchTarget>(", ", (IEnumerable<IUE4SearchTarget>) searchTargets)}]");
        yield break;
      }
      foreach (IPsiSourceFile psiSourceFile in source.SelectMany<string, IPsiSourceFile>((Func<string, IEnumerable<IPsiSourceFile>>) (name => this.myAssetsCache.GetAssetFilesContainingWord(name))).Distinct<IPsiSourceFile>())
      {
        IPsiSourceFile file = psiSourceFile;
        IEnumerator<UnrealAssetFindResult> enumerator = searchTargets.SelectMany<IUE4SearchTarget, UnrealAssetFindResult>((Func<IUE4SearchTarget, IEnumerable<UnrealAssetFindResult>>) (searchTarget => this.GetFindUsagesResults(file, searchTarget, searchReadOccurrences, cache))).Distinct<UnrealAssetFindResult>().GetEnumerator();
        while (enumerator.MoveNext())
          yield return (UnrealFindResult) enumerator.Current;
        enumerator = (IEnumerator<UnrealAssetFindResult>) null;
      }
    }
  }

  private IEnumerable<UnrealAssetFindFunctionResult> ToFunctionFindResults(
    UE4SearchFunctionTarget searchFunctionTarget,
    DerivedBlueprintClass blueprintClass,
    ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> cache)
  {
    IEnumerable<IPsiSourceFile> filesContainingWord = this.myAssetsCache.GetAssetFilesContainingWord(searchFunctionTarget.FunctionName);
    if (filesContainingWord != null && filesContainingWord.Contains<IPsiSourceFile>(blueprintClass.ContainingFile))
    {
      UEAssetUsagesSearcher.CachedAssetAccessor accessor = new UEAssetUsagesSearcher.CachedAssetAccessor(blueprintClass.ContainingFile, this.myAssetsCache, cache);
      if (accessor.IsValid)
      {
        bool isFunctionOverrideFound = false;
        UEObjectExport objectExport = accessor.GetObject(blueprintClass.Index);
        if (!(objectExport == (UEObjectExport) null))
        {
          UEPackageIndex[] uePackageIndexArray = objectExport.Dependencies;
          int exportObjectIndex;
          for (exportObjectIndex = 0; exportObjectIndex < uePackageIndexArray.Length; ++exportObjectIndex)
          {
            UEPackageIndex uePackageIndex = uePackageIndexArray[exportObjectIndex];
            UEObjectExport reference = uePackageIndex.Reference as UEObjectExport;
            if (!(reference == (UEObjectExport) null) && reference.IsFunction() && reference.ObjectStringName == searchFunctionTarget.FunctionName)
            {
              isFunctionOverrideFound = true;
              yield return new UnrealAssetFindFunctionResult(reference.Linker.AssetPath, this.mySolution, searchFunctionTarget.FunctionName, OccurrenceKind.Write, uePackageIndex.Index, searchFunctionTarget.FunctionName, accessor.GetGuidProperty(reference));
            }
          }
          uePackageIndexArray = (UEPackageIndex[]) null;
          int fileVersionUe = objectExport.Linker.Summary.FileVersionUE;
          bool flag = fileVersionUe >= 1000;
          if (!flag && fileVersionUe == 522)
            flag = this.mySolution.GetComponent<ICppUE4SolutionDetector>().UnrealContext.Value.Version.IsUE5();
          if (flag && !isFunctionOverrideFound)
          {
            UEObjectExport[] exportMap = objectExport.Linker.ExportMap;
            for (exportObjectIndex = 0; exportObjectIndex < exportMap.Length; ++exportObjectIndex)
            {
              UEObjectExport objectExport1 = exportMap[exportObjectIndex];
              if (!isFunctionOverrideFound)
              {
                IUEProperty ueProperty1;
                IUEProperty ueProperty2;
                if (objectExport1.IsK2Node_Event() && accessor.GetTaggedProperties(objectExport1).TryGetValue("EventReference", out ueProperty1) && ueProperty1 is UEPropertiesBasedStructProperty basedStructProperty && basedStructProperty.Properties.TryGetValue("MemberName", out ueProperty2) && ueProperty2 is UENameProperty ueNameProperty && ueNameProperty.Value == searchFunctionTarget.FunctionName)
                {
                  isFunctionOverrideFound = true;
                  yield return new UnrealAssetFindFunctionResult(objectExport.Linker.AssetPath, this.mySolution, searchFunctionTarget.FunctionName, OccurrenceKind.Write, exportObjectIndex, objectExport1.ObjectStringName, accessor.GetGuidProperty(objectExport1));
                }
              }
              else
                break;
            }
            exportMap = (UEObjectExport[]) null;
          }
        }
      }
    }
  }

  [NotNull]
  public IEnumerable<UnrealAssetFindResult> GetGoToInheritorsResults(
    [NotNull] IList<IUE4SearchTarget> searchTargets,
    IProgressIndicator pi = null,
    ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> cache = null)
  {
    HashSet<UnrealAssetFindResult> unrealAssetFindResultSet = new HashSet<UnrealAssetFindResult>();
    if (UnrealBlueprintsSupportStatusProvider.IsBlueprintsSupportEnabled(this.mySolution))
    {
      LogWithLevel? nullable = this.myLogger.WhenTrace();
      ref LogWithLevel? local = ref nullable;
      if (local.HasValue)
        local.GetValueOrDefault().Log($"Running 'Go to Inheritors' for search targets [{string.Join<IUE4SearchTarget>(", ", (IEnumerable<IUE4SearchTarget>) searchTargets)}]");
      IEnumerator<DerivedBlueprintClass> enumerator1 = UE4SearchUtil.GetDerivedBlueprintClasses(searchTargets.OfType<UE4SearchClassTarget>().Select<UE4SearchClassTarget, string>((Func<UE4SearchClassTarget, string>) (t => t.ClassName)), this.myAssetsCache).GetEnumerator();
      while (enumerator1.MoveNext())
      {
        DerivedBlueprintClass current = enumerator1.Current;
        IProgressIndicator progressIndicator = pi;
        if ((progressIndicator != null ? (progressIndicator.IsCanceled ? 1 : 0) : 0) == 0)
        {
          Interruption.Current.CheckAndThrow();
          yield return (UnrealAssetFindResult) new UnrealAssetFindClassResult(current.ContainingFile.GetLocation(), this.mySolution, current.Index, current.Name, OccurrenceKind.ExtendedType);
        }
        else
          break;
      }
      enumerator1 = (IEnumerator<DerivedBlueprintClass>) null;
      foreach (UE4SearchFunctionTarget searchFunctionTarget in searchTargets.OfType<UE4SearchFunctionTarget>())
      {
        foreach (DerivedBlueprintClass derivedBlueprintClass in UE4SearchUtil.GetDerivedBlueprintClasses(searchFunctionTarget.ClassName, this.myAssetsCache))
        {
          IProgressIndicator progressIndicator = pi;
          if ((progressIndicator != null ? (progressIndicator.IsCanceled ? 1 : 0) : 0) == 0)
          {
            Interruption.Current.CheckAndThrow();
            IEnumerator<UnrealAssetFindFunctionResult> enumerator2 = this.ToFunctionFindResults(searchFunctionTarget, derivedBlueprintClass, cache).GetEnumerator();
            while (enumerator2.MoveNext())
              yield return (UnrealAssetFindResult) enumerator2.Current;
            enumerator2 = (IEnumerator<UnrealAssetFindFunctionResult>) null;
          }
          else
            break;
        }
      }
    }
  }

  private class CachedAssetAccessor
  {
    [NotNull]
    private readonly UEAssetFileAccessor myAccessor;

    public bool IsValid => this.myAccessor.IsValid();

    public CachedAssetAccessor(
      [NotNull] IPsiSourceFile assetSourceFile,
      [NotNull] UE4AssetsCache globalCache,
      [CanBeNull] ConcurrentDictionary<IPsiSourceFile, UEAssetFileAccessor> accessorCache)
    {
      this.myAccessor = accessorCache == null ? globalCache.GetUEAssetFileAccessor(assetSourceFile) : accessorCache.GetOrAdd(assetSourceFile, new Func<IPsiSourceFile, UEAssetFileAccessor>(globalCache.GetUEAssetFileAccessor)).NotNull<UEAssetFileAccessor>("accessorCache.GetOrAdd(assetSourceFile, globalCache.GetUEAssetFileAccessor)");
    }

    [CanBeNull]
    public UEObjectExport GetObject(int index)
    {
      UEObjectExport ueObjectExport;
      return !this.myAccessor.TryGetValue<UEObjectExport>((Func<UELinker, UEObjectExport>) (linker => linker.ExportMap[index]), out ueObjectExport) ? (UEObjectExport) null : ueObjectExport;
    }

    [NotNull]
    public IDictionary<string, IUEProperty> GetTaggedProperties([NotNull] UEObjectExport objectExport)
    {
      return objectExport.GetTaggedProperties(this.myAccessor);
    }

    [CanBeNull]
    public string GetGuidProperty([NotNull] UEObjectExport objectExport)
    {
      IUEProperty ueProperty1;
      return (this.GetTaggedProperties(objectExport).TryGetValue("Guid", out ueProperty1) || this.GetTaggedProperties(objectExport).TryGetValue("NodeGuid", out ueProperty1)) && ueProperty1 is UEProperty<UEGuid> ueProperty2 ? ueProperty2.ValuePresentation : (string) null;
    }

    [NotNull]
    public VirtualFileSystemPath AssetPath => this.myAccessor.AssetPath;
  }
}
