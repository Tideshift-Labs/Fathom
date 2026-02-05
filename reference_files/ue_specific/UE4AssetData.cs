// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.UE4AssetData
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.Entities.Properties;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset;

public class UE4AssetData
{
  [NotNull]
  public readonly UE4AssetData.BlueprintClassObject[] BlueprintClasses;
  [NotNull]
  public readonly UE4AssetData.K2GraphNodeObject[] K2VariableSets;
  [NotNull]
  public readonly UE4AssetData.OtherAssetObject[] OtherClasses;
  [NotNull]
  public readonly int[] WordHashes;

  public UE4AssetData(
    [NotNull] UE4AssetData.BlueprintClassObject[] blueprintClasses,
    [NotNull] UE4AssetData.K2GraphNodeObject[] k2VariableSets,
    [NotNull] UE4AssetData.OtherAssetObject[] otherClasses,
    [NotNull] int[] wordHashes)
  {
    this.BlueprintClasses = blueprintClasses;
    this.K2VariableSets = k2VariableSets;
    this.OtherClasses = otherClasses;
    this.WordHashes = wordHashes;
  }

  [NotNull]
  public static UE4AssetData FromLinker([NotNull] UELinker linker)
  {
    List<UE4AssetData.BlueprintClassObject> blueprintClassObjectList = new List<UE4AssetData.BlueprintClassObject>();
    List<UE4AssetData.OtherAssetObject> otherAssetObjectList = new List<UE4AssetData.OtherAssetObject>();
    List<UE4AssetData.K2GraphNodeObject> k2GraphNodeObjectList = new List<UE4AssetData.K2GraphNodeObject>();
    for (int index = 0; index < linker.ExportMap.Length; ++index)
    {
      UEObjectExport objectExport = linker.ExportMap[index];
      UEPackageIndex uePackageIndex = objectExport.ClassIndex;
      UEObjectResource reference1 = uePackageIndex.Reference;
      if (reference1 != null)
      {
        if (objectExport.IsBlueprintGeneratedClass())
        {
          uePackageIndex = objectExport.SuperIndex;
          if (uePackageIndex.Exists())
          {
            string[] interfaces = objectExport.GetObject<UEBlueprintGeneratedClass>()?.Interfaces.ToArray() ?? Array.Empty<string>();
            uePackageIndex = objectExport.SuperIndex;
            UEObjectResource reference2 = uePackageIndex.Reference;
            blueprintClassObjectList.Add(new UE4AssetData.BlueprintClassObject(index, objectExport.ObjectStringName, reference1.ObjectStringName, reference2.ObjectStringName, interfaces));
            continue;
          }
        }
        UE4AssetData.K2GraphNodeObject.Kind? kind = GetKind();
        if (!kind.HasValue)
        {
          otherAssetObjectList.Add(new UE4AssetData.OtherAssetObject(index, reference1.ObjectStringName));
        }
        else
        {
          UE4AssetData.K2GraphNodeObject.Kind objectKind = kind.Value;
          string str;
          switch (objectKind)
          {
            case UE4AssetData.K2GraphNodeObject.Kind.VariableGet:
              str = "VariableReference";
              break;
            case UE4AssetData.K2GraphNodeObject.Kind.VariableSet:
              str = "VariableReference";
              break;
            case UE4AssetData.K2GraphNodeObject.Kind.FunctionCall:
              str = "FunctionReference";
              break;
            case UE4AssetData.K2GraphNodeObject.Kind.AddDelegate:
              str = "DelegateReference";
              break;
            case UE4AssetData.K2GraphNodeObject.Kind.ClearDelegate:
              str = "DelegateReference";
              break;
            case UE4AssetData.K2GraphNodeObject.Kind.CallDelegate:
              str = "DelegateReference";
              break;
            default:
              throw new ArgumentOutOfRangeException("kind");
          }
          string key = str;
          if ((objectExport.GetTaggedPropertiesImpl(linker).TryGetValue<string, IUEProperty>(key) is UEPropertiesBasedStructProperty basedStructProperty ? basedStructProperty.Properties.TryGetValue<string, IUEProperty>("MemberName") : (IUEProperty) null) is UENameProperty ueNameProperty)
            k2GraphNodeObjectList.Add(new UE4AssetData.K2GraphNodeObject(index, objectKind, ueNameProperty.Value));
        }
      }

      UE4AssetData.K2GraphNodeObject.Kind? GetKind()
      {
        if (objectExport.IsK2Node_VariableSet())
          return new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.VariableSet);
        if (objectExport.IsK2Node_VariableGet())
          return new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.VariableGet);
        if (objectExport.IsK2Node_CallFunction())
          return new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.FunctionCall);
        if (objectExport.IsK2Node_AddOrAssetDelegate())
          return new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.AddDelegate);
        if (objectExport.IsK2Node_ClearDelegate())
          return new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.ClearDelegate);
        return objectExport.IsK2Node_CallDelegate() ? new UE4AssetData.K2GraphNodeObject.Kind?(UE4AssetData.K2GraphNodeObject.Kind.CallDelegate) : new UE4AssetData.K2GraphNodeObject.Kind?();
      }
    }
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    int[] array = ((IEnumerable<string>) linker.NameMap).Select<string, int>(UE4AssetData.\u003C\u003EO.\u003C0\u003E__GetWordCode ?? (UE4AssetData.\u003C\u003EO.\u003C0\u003E__GetWordCode = new Func<string, int>(UE4AssetsCache.GetWordCode))).ToArray<int>();
    return new UE4AssetData(blueprintClassObjectList.ToArray(), k2GraphNodeObjectList.ToArray(), otherAssetObjectList.ToArray(), array);
  }

  public override bool Equals(object obj)
  {
    return obj is UE4AssetData ue4AssetData && ArrayUtil.StructuralEquals<UE4AssetData.BlueprintClassObject>(this.BlueprintClasses, ue4AssetData.BlueprintClasses) && ArrayUtil.StructuralEquals<UE4AssetData.OtherAssetObject>(this.OtherClasses, ue4AssetData.OtherClasses) && ArrayUtil.StructuralEquals<UE4AssetData.K2GraphNodeObject>(this.K2VariableSets, ue4AssetData.K2VariableSets) && ArrayUtil.StructuralEquals<int>(this.WordHashes, ue4AssetData.WordHashes);
  }

  public override int GetHashCode()
  {
    return ((ArrayUtil.GetHashCode<UE4AssetData.BlueprintClassObject>(this.BlueprintClasses) * 397 ^ ArrayUtil.GetHashCode<UE4AssetData.OtherAssetObject>(this.OtherClasses)) * 397 ^ ArrayUtil.GetHashCode<UE4AssetData.K2GraphNodeObject>(this.K2VariableSets)) * 397 ^ ArrayUtil.GetHashCode<int>(this.WordHashes);
  }

  public readonly struct BlueprintClassObject
  {
    public readonly int Index;
    public readonly string ObjectName;
    public readonly string ClassName;
    public readonly string SuperClassName;
    public readonly string[] Interfaces;

    public BlueprintClassObject(
      int index,
      string objectName,
      string className,
      string superClassName,
      string[] interfaces)
    {
      this.Interfaces = Array.Empty<string>();
      this.Index = index;
      this.ObjectName = objectName;
      this.ClassName = className;
      this.SuperClassName = superClassName;
      this.Interfaces = interfaces;
    }
  }

  public readonly struct OtherAssetObject(int index, string className)
  {
    public readonly int Index = index;
    public readonly string ClassName = className;
  }

  public readonly struct K2GraphNodeObject(
    int index,
    UE4AssetData.K2GraphNodeObject.Kind objectKind,
    string memberName)
  {
    public readonly int Index = index;
    public readonly UE4AssetData.K2GraphNodeObject.Kind ObjectKind = objectKind;
    public readonly string MemberName = memberName;

    public enum Kind
    {
      VariableGet,
      VariableSet,
      FunctionCall,
      AddDelegate,
      ClearDelegate,
      CallDelegate,
    }
  }
}
