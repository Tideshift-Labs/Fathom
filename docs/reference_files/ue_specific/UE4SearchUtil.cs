// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.UE4SearchUtil
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.Collections;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Config;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Cpp.Caches;
using JetBrains.ReSharper.Psi.Cpp.Language;
using JetBrains.ReSharper.Psi.Cpp.Symbols;
using JetBrains.ReSharper.Psi.Cpp.UE4;
using JetBrains.ReSharper.Psi.Cpp.Util;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;

public static class UE4SearchUtil
{
  [NotNull]
  public static IEnumerable<string> GetDerivedBlueprintTypeNames(
    string baseTypeName,
    [NotNull] UE4AssetsCache assetsCache)
  {
    return UE4SearchUtil.GetDerivedBlueprintClasses(ToEnumerator(), assetsCache).Select<DerivedBlueprintClass, string>((Func<DerivedBlueprintClass, string>) (c => c.Name));

    IEnumerable<string> ToEnumerator()
    {
      yield return baseTypeName;
    }
  }

  [NotNull]
  public static IEnumerable<DerivedBlueprintClass> GetDerivedBlueprintClasses(
    string baseTypeName,
    [NotNull] UE4AssetsCache assetsCache)
  {
    return UE4SearchUtil.GetDerivedBlueprintClasses(ToEnumerator(), assetsCache);

    IEnumerable<string> ToEnumerator()
    {
      yield return baseTypeName;
    }
  }

  public static IEnumerable<DerivedBlueprintClass> GetDerivedBlueprintClasses(
    IEnumerable<string> baseTypeNames,
    [NotNull] UE4AssetsCache assetsCache,
    bool uniqueNames = false)
  {
    Queue<string> queue = new Queue<string>(baseTypeNames);
    HashSet<string> classNames = new HashSet<string>();
    while (!queue.IsEmpty<string>())
    {
      foreach (DerivedBlueprintClass derivedBlueprintClass in (IEnumerable<DerivedBlueprintClass>) assetsCache.GetDerivedBlueprintClasses(queue.Dequeue()))
      {
        int num = classNames.Add(derivedBlueprintClass.Name) ? 1 : 0;
        if (num != 0)
          queue.Enqueue(derivedBlueprintClass.Name);
        if (num != 0 || !uniqueNames)
          yield return derivedBlueprintClass;
      }
    }
  }

  public static bool IsApplicableForUnrealSpecificSearch(IDeclaredElement element)
  {
    return element is CppLinkageEntityDeclaredElement || element is CppResolveEntityDeclaredElement;
  }

  [NotNull]
  public static Func<string, string, string, string, bool, IUE4SearchMemberTarget> MemberTargetCreator(
    bool isEnum)
  {
    return isEnum ? (Func<string, string, string, string, bool, IUE4SearchMemberTarget>) ((clss, prop, module, _, redirect) => (IUE4SearchMemberTarget) new UE4SearchEnumValueTarget(clss, prop, module, redirect)) : (Func<string, string, string, string, bool, IUE4SearchMemberTarget>) ((clss, prop, module, config, redirect) => (IUE4SearchMemberTarget) new UE4SearchFieldTarget(clss, prop, module, config, redirect));
  }

  /// <summary>
  /// Builds search targets for search into Unreal Engine files (e.g. .uasset or .ini) according to Core Redirects and class inheritance
  /// </summary>
  [NotNull]
  public static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] IDeclaredElement declaredElement,
    bool withAllInheritors = false)
  {
    ISolution solution = declaredElement.GetSolution();
    string moduleName = UE4SearchUtil.GetModuleName(declaredElement);
    if (moduleName == null)
      return EmptyList<IUE4SearchTarget>.InstanceList;
    switch (declaredElement)
    {
      case CppResolveEntityDeclaredElement entityDeclaredElement1:
        IList<IUE4SearchTarget> ue4SearchTargetList1;
        switch (entityDeclaredElement1.GetResolveEntity())
        {
          case ICppClassResolveEntity classResolveEntity:
            ue4SearchTargetList1 = UE4SearchUtil.BuildUESearchTargets(classResolveEntity, solution, moduleName, withAllInheritors);
            break;
          case ICppVariableDeclaratorResolveEntity resolveEntity:
            ue4SearchTargetList1 = UE4SearchUtil.BuildUESearchTargets(resolveEntity, solution, moduleName);
            break;
          case ICppFunctionDeclaratorResolveEntity functionDeclaratorResolveEntity:
            ue4SearchTargetList1 = UE4SearchUtil.BuildUESearchTargets(functionDeclaratorResolveEntity, solution, moduleName);
            break;
          default:
            ue4SearchTargetList1 = EmptyList<IUE4SearchTarget>.InstanceList;
            break;
        }
        return ue4SearchTargetList1;
      case CppLinkageEntityDeclaredElement entityDeclaredElement2:
        IList<IUE4SearchTarget> ue4SearchTargetList2;
        switch (entityDeclaredElement2.GetLinkageEntity())
        {
          case CppClassLinkageEntity cppClassLinkageEntity:
            ue4SearchTargetList2 = UE4SearchUtil.BuildUESearchTargets(cppClassLinkageEntity, solution, moduleName);
            break;
          case CppDeclaratorLinkageEntity cppDeclaratorLinkageEntity:
            ue4SearchTargetList2 = UE4SearchUtil.BuildUESearchTargets(cppDeclaratorLinkageEntity, solution, moduleName);
            break;
          default:
            ue4SearchTargetList2 = EmptyList<IUE4SearchTarget>.InstanceList;
            break;
        }
        return ue4SearchTargetList2;
      default:
        return EmptyList<IUE4SearchTarget>.InstanceList;
    }
  }

  [NotNull]
  public static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] ICppClassResolveEntity classResolveEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName,
    bool withAllInheritors)
  {
    if (!CppUE4Util.IsUEType(classResolveEntity))
      return (IList<IUE4SearchTarget>) EmptyList<IUE4SearchTarget>.Instance;
    return withAllInheritors ? (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetClassAndAllInheritorsTargets(classResolveEntity, solution, moduleName, false).Select<(string, string, string, bool), UE4SearchClassTarget>((Func<(string, string, string, bool), UE4SearchClassTarget>) (t => UE4SearchClassTarget.FromClassName(t.className, t.moduleName, t.isCoreRedirect)))).ToList<IUE4SearchTarget>() : (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetTypeNameWithPossibleNameRedirects(classResolveEntity.Name.ToString(), solution, moduleName).Select<(string, bool), UE4SearchClassTarget>((Func<(string, bool), UE4SearchClassTarget>) (t => new UE4SearchClassTarget(t.name, moduleName, t.isCoreRedirect)))).AsList<IUE4SearchTarget>();
  }

  [NotNull]
  public static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] ICppVariableDeclaratorResolveEntity resolveEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    string qualifiedIdValue = resolveEntity.Name.GetQualifiedIdValue();
    if (qualifiedIdValue == null || !CppUE4Util.IsUProperty((ICppDeclaratorResolveEntity) resolveEntity))
      return EmptyList<IUE4SearchTarget>.InstanceList;
    ICppClassResolveEntity classByMember = resolveEntity.GetClassByMember();
    if (classByMember == null || !UE4Util.IsLooksLikeUClass(classByMember) && !CppUE4Util.IsUStruct(classByMember))
      return EmptyList<IUE4SearchTarget>.InstanceList;
    bool calculateConfigName = UE4Util.IsConfigUProperty(resolveEntity);
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    return (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetMemberTargetsWithRedirects<IUE4SearchMemberTarget>((ICollection<(string, string, string, bool)>) UE4SearchUtil.GetClassAndAllInheritorsTargets(classByMember, solution, moduleName, calculateConfigName), qualifiedIdValue, solution, UE4SearchUtil.MemberTargetCreator(false), UE4SearchUtil.\u003C\u003EO.\u003C0\u003E__GetPossiblePropertyNameRedirects ?? (UE4SearchUtil.\u003C\u003EO.\u003C0\u003E__GetPossiblePropertyNameRedirects = new Func<string, string, ISolution, string, IEnumerable<string>>(UE4SearchUtil.GetPossiblePropertyNameRedirects)))).AsList<IUE4SearchTarget>();
  }

  [NotNull]
  public static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] ICppFunctionDeclaratorResolveEntity functionDeclaratorResolveEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    functionDeclaratorResolveEntity = (ICppFunctionDeclaratorResolveEntity) functionDeclaratorResolveEntity.FindGroupedFunction();
    if (functionDeclaratorResolveEntity == null || !CppUE4Util.IsUFunction(functionDeclaratorResolveEntity))
      return EmptyList<IUE4SearchTarget>.InstanceList;
    ICppClassResolveEntity classByMember = functionDeclaratorResolveEntity.GetClassByMember();
    return classByMember == null || !UE4Util.IsLooksLikeUClass(classByMember) ? EmptyList<IUE4SearchTarget>.InstanceList : (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetFunctionTargetsWithRedirects((ICollection<(string, string, string, bool)>) UE4SearchUtil.GetClassAndAllInheritorsTargets(classByMember, solution, moduleName, false), functionDeclaratorResolveEntity.Name.ToString(), solution)).AsList<IUE4SearchTarget>();
  }

  [NotNull]
  private static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] CppClassLinkageEntity cppClassLinkageEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    string qualifiedIdValue = cppClassLinkageEntity.Name.GetQualifiedIdValue();
    if (qualifiedIdValue == null)
      return EmptyList<IUE4SearchTarget>.InstanceList;
    CppSymbolNameCache symbolNameCache = solution.GetComponent<CppGlobalSymbolCache>().SymbolNameCache;
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    return !UE4Util.GetGlobalClassSymbols(qualifiedIdValue, symbolNameCache).Where<ICppClassSymbol>(UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType ?? (UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType = new Func<ICppClassSymbol, bool>(CppUE4Util.IsUEType))).Any<ICppClassSymbol>() ? EmptyList<IUE4SearchTarget>.InstanceList : (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetTypeNameWithPossibleNameRedirects(qualifiedIdValue, solution, moduleName).Select<(string, bool), UE4SearchClassTarget>((Func<(string, bool), UE4SearchClassTarget>) (t => new UE4SearchClassTarget(t.name, moduleName, t.isCoreRedirect)))).AsList<IUE4SearchTarget>();
  }

  private static IList<IUE4SearchTarget> BuildUESearchTargets(
    [NotNull] CppDeclaratorLinkageEntity cppDeclaratorLinkageEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    CppGlobalSymbolCache component = solution.GetComponent<CppGlobalSymbolCache>();
    CppSymbolNameCache nameCache = component.SymbolNameCache;
    if (!(cppDeclaratorLinkageEntity.GetClassByMember() is CppClassLinkageEntity classByMember))
      return EmptyList<IUE4SearchTarget>.InstanceList;
    string propertyName = cppDeclaratorLinkageEntity.Name.GetQualifiedIdValue();
    if (propertyName == null)
      return EmptyList<IUE4SearchTarget>.InstanceList;
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    ICppClassSymbol sym = UE4Util.GetGlobalClassSymbols(classByMember.Name.GetQualifiedIdValue(), nameCache).Where<ICppClassSymbol>(UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType ?? (UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType = new Func<ICppClassSymbol, bool>(CppUE4Util.IsUEType))).Where<ICppClassSymbol>((Func<ICppClassSymbol, bool>) (classSymbol =>
    {
      if (CppUE4Util.IsUEnum(classSymbol))
        return true;
      ICppDeclaratorSymbol upropertySymbol = UE4Util.GetUPropertySymbol(classSymbol, propertyName, nameCache);
      if (upropertySymbol == null)
        return false;
      return CppUE4Util.IsUProperty(upropertySymbol) || CppUE4Util.IsUFunction(upropertySymbol);
    })).FirstOrDefault<ICppClassSymbol>();
    if (sym == null)
      return EmptyList<IUE4SearchTarget>.InstanceList;
    string configName = UE4SearchUtil.GetConfigName((ICppLinkageEntity) classByMember, (ICppParserSymbol) sym, component.LinkageCache, nameCache, new Dictionary<ICppLinkageEntity, string>(), new HashSet<ICppLinkageEntity>(), 0, 100, true);
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    return (IList<IUE4SearchTarget>) ((IEnumerable<IUE4SearchTarget>) UE4SearchUtil.GetMemberTargetsWithRedirects<IUE4SearchMemberTarget>((ICollection<(string, string, string, bool)>) UE4SearchUtil.GetTypeNameWithPossibleNameRedirects(classByMember.Name.ToString(), solution, moduleName).Select<(string, bool), (string, string, string, bool)>((Func<(string, bool), (string, string, string, bool)>) (t => (t.name, configName, moduleName, t.isCoreRedirect))).ToList<(string, string, string, bool)>(), propertyName, solution, UE4SearchUtil.MemberTargetCreator(CppUE4Util.IsUEnum(sym)), UE4SearchUtil.\u003C\u003EO.\u003C0\u003E__GetPossiblePropertyNameRedirects ?? (UE4SearchUtil.\u003C\u003EO.\u003C0\u003E__GetPossiblePropertyNameRedirects = new Func<string, string, ISolution, string, IEnumerable<string>>(UE4SearchUtil.GetPossiblePropertyNameRedirects)))).AsList<IUE4SearchTarget>();
  }

  [NotNull]
  [ItemNotNull]
  private static IEnumerable<UE4SearchFunctionTarget> GetFunctionTargetsWithRedirects(
    [NotNull] ICollection<(string className, string configName, string moduleName, bool isCoreRedirect)> ownerClassNameRedirects,
    [NotNull] string functionName,
    [NotNull] ISolution solution)
  {
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    return UE4SearchUtil.GetMemberTargetsWithRedirects<UE4SearchFunctionTarget>(ownerClassNameRedirects, functionName, solution, (Func<string, string, string, string, bool, UE4SearchFunctionTarget>) ((clazz, function, module, config, isCoreRedirect) => new UE4SearchFunctionTarget(clazz, function, module, isCoreRedirect)), UE4SearchUtil.\u003C\u003EO.\u003C2\u003E__GetPossibleFunctionNameRedirects ?? (UE4SearchUtil.\u003C\u003EO.\u003C2\u003E__GetPossibleFunctionNameRedirects = new Func<string, string, ISolution, string, IEnumerable<string>>(UE4SearchUtil.GetPossibleFunctionNameRedirects)));
  }

  [NotNull]
  [ItemNotNull]
  private static IEnumerable<T> GetMemberTargetsWithRedirects<T>(
    [NotNull] ICollection<(string className, string configName, string moduleName, bool isCoreRedirect)> ownerClassNameRedirects,
    [NotNull] string targetMemberName,
    [NotNull] ISolution solution,
    [NotNull] Func<string, string, string, string, bool, T> createTarget,
    [NotNull] Func<string, string, ISolution, string, IEnumerable<string>> getPossibleMemberNameRedirects)
    where T : IUE4SearchMemberTarget
  {
    HashSet<T> targetsWithRedirects = new HashSet<T>();
    Queue<(string, bool)> collection = new Queue<(string, bool)>();
    collection.Enqueue((targetMemberName, false));
    HashSet<string> stringSet = new HashSet<string>();
    stringSet.Add(targetMemberName);
    while (!collection.IsEmpty<(string, bool)>())
    {
      (string, bool) valueTuple = collection.Dequeue();
      foreach ((string className, string configName, string moduleName, bool isCoreRedirect) in (IEnumerable<(string className, string configName, string moduleName, bool isCoreRedirect)>) ownerClassNameRedirects)
      {
        targetsWithRedirects.Add(createTarget(className, valueTuple.Item1, moduleName, configName, isCoreRedirect || valueTuple.Item2));
        foreach (string str in getPossibleMemberNameRedirects(className, valueTuple.Item1, solution, moduleName))
        {
          if (stringSet.Add(str))
            collection.Enqueue((str, true));
        }
      }
    }
    return (IEnumerable<T>) targetsWithRedirects;
  }

  [NotNull]
  private static IEnumerable<(string name, bool isCoreRedirect)> GetTypeNameWithPossibleNameRedirects(
    [NotNull] string typeName,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    string unrealEntityName = UnrealPrefixes.GetEntityNameForCoreRedirects(typeName);
    yield return (unrealEntityName, false);
    foreach (string typeRedirectName in solution.GetComponent<UEIniInformationProvider>().GetOldTypeRedirectNames(moduleName, unrealEntityName))
      yield return (typeRedirectName, true);
  }

  [NotNull]
  private static IEnumerable<string> GetPossiblePropertyNameRedirects(
    [NotNull] string typeName,
    [NotNull] string propertyName,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    string forCoreRedirects = UnrealPrefixes.GetEntityNameForCoreRedirects(typeName);
    foreach (string propertyRedirectName in solution.GetComponent<UEIniInformationProvider>().GetOldPropertyRedirectNames(moduleName, forCoreRedirects, propertyName))
      yield return propertyRedirectName;
  }

  [NotNull]
  private static IEnumerable<string> GetPossibleFunctionNameRedirects(
    [NotNull] string typeName,
    [NotNull] string functionName,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName)
  {
    string forCoreRedirects = UnrealPrefixes.GetEntityNameForCoreRedirects(typeName);
    foreach (string functionRedirectName in solution.GetComponent<UEIniInformationProvider>().GetOldFunctionRedirectNames(moduleName, forCoreRedirects, functionName))
      yield return functionRedirectName;
  }

  [NotNull]
  private static List<(string className, string configName, string moduleName, bool isCoreRedirect)> GetClassAndAllInheritorsTargets(
    [NotNull] ICppClassResolveEntity classResolveEntity,
    [NotNull] ISolution solution,
    [CanBeNull] string moduleName,
    bool calculateConfigName)
  {
    string configName = calculateConfigName ? UE4Util.GetConfigNameFromBases(classResolveEntity) : (string) null;
    List<(string, string, string, bool)> result = new List<(string, string, string, bool)>(UE4SearchUtil.GetTypeNameWithPossibleNameRedirects(classResolveEntity.Name.ToString(), solution, moduleName).Select<(string, bool), (string, string, string, bool)>((Func<(string, bool), (string, string, string, bool)>) (t => (t.name, configName, moduleName, t.isCoreRedirect))));
    CppGlobalSymbolCache component = solution.GetComponent<CppGlobalSymbolCache>();
    CppLinkageEntityCache linkageCache = component.LinkageCache;
    ICppLinkageEntity currentEntity = CppLinkageEntityFactory.FromResolveEntity((ICppResolveEntity) classResolveEntity);
    UE4SearchUtil.FillTargetsWithDerivedElements(solution, currentEntity, calculateConfigName, configName, linkageCache, component.SymbolNameCache, new HashSet<ICppLinkageEntity>(), result);
    return result;
  }

  private static void FillTargetsWithDerivedElements(
    [NotNull] ISolution solution,
    [NotNull] ICppLinkageEntity currentEntity,
    bool calculateConfigName,
    string currentConfig,
    [NotNull] CppLinkageEntityCache cppLinkageEntityCache,
    [NotNull] CppSymbolNameCache symbolNameCache,
    [NotNull] HashSet<ICppLinkageEntity> visited,
    List<(string className, string configName, string moduleName, bool isCoreRedirect)> result)
  {
    if (!visited.Add(currentEntity))
      return;
    foreach (KeyValuePair<ICppLinkageEntity, SearchTargetRole> derivedLinkageEntity in (IEnumerable<KeyValuePair<ICppLinkageEntity, SearchTargetRole>>) CppInheritanceUtil.FindDirectDerivedLinkageEntities(cppLinkageEntityCache, currentEntity))
    {
      ICppLinkageEntity key;
      derivedLinkageEntity.Deconstruct<ICppLinkageEntity, SearchTargetRole>(out key, out SearchTargetRole _);
      ICppLinkageEntity currentEntity1 = key;
      CppQualifiedId? nullable = currentEntity1.Name.AsQualifiedId();
      if (nullable.HasValue)
      {
        CppQualifiedId valueOrDefault = nullable.GetValueOrDefault();
        foreach (ICppClassSymbol cent in UE4Util.GetGlobalClassSymbols(valueOrDefault.Name, symbolNameCache).Where<ICppClassSymbol>((Func<ICppClassSymbol, bool>) (sym => CppUE4Util.IsUClass(sym) || CppUE4Util.IsUStruct(sym))))
        {
          string inheritorModuleName = UE4SearchUtil.GetModuleName(cent.ContainingFile.Location, solution);
          if (inheritorModuleName != null)
          {
            if (calculateConfigName)
            {
              string configName = CppUE4Util.GetConfigName(cent);
              if (configName != null)
                currentConfig = configName;
            }
            result.AddRange(UE4SearchUtil.GetTypeNameWithPossibleNameRedirects(valueOrDefault.Name, solution, inheritorModuleName).Select<(string, bool), (string, string, string, bool)>((Func<(string, bool), (string, string, string, bool)>) (t => (t.name, currentConfig, inheritorModuleName, t.isCoreRedirect))));
          }
        }
        UE4SearchUtil.FillTargetsWithDerivedElements(solution, currentEntity1, calculateConfigName, currentConfig, cppLinkageEntityCache, symbolNameCache, visited, result);
      }
    }
  }

  private static string GetConfigName(
    [NotNull] ICppLinkageEntity currentEntity,
    ICppParserSymbol cppParserSymbol,
    [NotNull] CppLinkageEntityCache cache,
    CppSymbolNameCache cppSymbolNameCache,
    [NotNull] Dictionary<ICppLinkageEntity, string> configCache,
    [NotNull] HashSet<ICppLinkageEntity> visited,
    int curDepth,
    int maxDepth,
    bool calculateConfigName)
  {
    if (curDepth == maxDepth)
      return (string) null;
    if (!calculateConfigName)
      return (string) null;
    string configName1 = !(cppParserSymbol is ICppClassSymbol cent) ? (string) null : CppUE4Util.GetConfigName(cent);
    if (configName1 != null)
    {
      configCache[currentEntity] = configName1;
      return configName1;
    }
    visited.Add(currentEntity);
    foreach (ICppLinkageEntity cppLinkageEntity in (IEnumerable<ICppLinkageEntity>) cache.FindBases(currentEntity))
    {
      if (!visited.Contains(cppLinkageEntity))
      {
        string configName2;
        if (configCache.TryGetValue(cppLinkageEntity, out configName2))
        {
          if (configName2 != null)
            return configName2;
        }
        else
        {
          CppQualifiedId? nullable = cppLinkageEntity.Name.AsQualifiedId();
          if (nullable.HasValue)
          {
            // ISSUE: reference to a compiler-generated field
            // ISSUE: reference to a compiler-generated field
            // ISSUE: reference to a compiler-generated field
            // ISSUE: reference to a compiler-generated field
            ICppClassSymbol cppClassSymbol = UE4Util.GetGlobalClassSymbols(nullable.GetValueOrDefault().Name, cppSymbolNameCache).Where<ICppClassSymbol>(UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType ?? (UE4SearchUtil.\u003C\u003EO.\u003C1\u003E__IsUEType = new Func<ICppClassSymbol, bool>(CppUE4Util.IsUEType))).OrderBy<ICppClassSymbol, string>(UE4SearchUtil.\u003C\u003EO.\u003C3\u003E__GetConfigName ?? (UE4SearchUtil.\u003C\u003EO.\u003C3\u003E__GetConfigName = new Func<ICppClassSymbol, string>(CppUE4Util.GetConfigName))).FirstOrDefault<ICppClassSymbol>();
            if (cppClassSymbol != null)
            {
              string configName3 = UE4SearchUtil.GetConfigName(cppLinkageEntity, (ICppParserSymbol) cppClassSymbol, cache, cppSymbolNameCache, configCache, visited, curDepth + 1, maxDepth, true);
              if (configName3 != null)
              {
                configCache[cppLinkageEntity] = configName3;
                return configName3;
              }
            }
          }
        }
      }
    }
    configCache[currentEntity] = (string) null;
    return (string) null;
  }

  [CanBeNull]
  public static string GetModuleName([NotNull] IDeclaredElement declaredElement)
  {
    IPsiSourceFile[] array = declaredElement.GetSourceFiles().ToArray();
    if (array.Length == 0)
      return (string) null;
    return array.Length == 1 ? UE4SearchUtil.GetModuleName(array[0].GetLocation(), declaredElement.GetSolution()) : UE4SearchUtil.GetModuleName(((IEnumerable<IPsiSourceFile>) array).Select<IPsiSourceFile, VirtualFileSystemPath>((Func<IPsiSourceFile, VirtualFileSystemPath>) (t => t.GetLocation())).OrderBy<VirtualFileSystemPath, int>((Func<VirtualFileSystemPath, int>) (t => t.FullPath.GetPlatformIndependentHashCode())).First<VirtualFileSystemPath>(), declaredElement.GetSolution());
  }

  [CanBeNull]
  private static string GetModuleName(VirtualFileSystemPath path, [NotNull] ISolution solution)
  {
    ICppUE4ModuleNamesProvider component1 = solution.GetComponent<ICppUE4ModuleNamesProvider>();
    ICppUE4ProjectPropertiesProvider component2 = solution.GetComponent<ICppUE4ProjectPropertiesProvider>();
    VirtualFileSystemPath location = path;
    ICppUE4ProjectPropertiesProvider projectPropertiesProvider = component2;
    return component1.GetModuleNameForLocation(location, projectPropertiesProvider);
  }
}
