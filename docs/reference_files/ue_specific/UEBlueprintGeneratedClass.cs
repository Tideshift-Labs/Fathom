// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.UEBlueprintGeneratedClass
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.Properties;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader.Util;
using JetBrains.Util;
using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Reader;

public class UEBlueprintGeneratedClass : IUEObject
{
  public List<string> Interfaces = new List<string>();

  private void ReadField([NotNull] EndiannessAwareBinaryReader reader, [NotNull] UELinker linker)
  {
    reader.ReadName(linker);
    reader.ReadInt32();
    if (reader.ReadInt32() == 0)
      return;
    reader.ReadArray<bool>((Func<EndiannessAwareBinaryReader, bool>) (r =>
    {
      r.ReadName(linker);
      r.ReadUEString();
      return true;
    }));
  }

  private void ReadProperty([NotNull] EndiannessAwareBinaryReader reader, [NotNull] UELinker linker)
  {
    this.ReadField(reader, linker);
    reader.ReadInt32();
    reader.ReadInt32();
    reader.ReadInt64();
    int num1 = (int) reader.ReadInt16();
    reader.ReadName(linker);
    int num2 = (int) reader.ReadByte();
  }

  private void ReadObjectProperty([NotNull] EndiannessAwareBinaryReader reader, [NotNull] UELinker linker)
  {
    this.ReadProperty(reader, linker);
    reader.ReadInt32();
  }

  private bool ReadProperties([NotNull] EndiannessAwareBinaryReader reader, [NotNull] UELinker linker)
  {
    string str = reader.ReadName(linker).Present(linker);
    if (str != null)
    {
      switch (str.Length)
      {
        case 5:
          if (str == "Field")
          {
            this.ReadField(reader, linker);
            goto label_61;
          }
          goto label_60;
        case 8:
          if (str == "Property")
            goto label_50;
          goto label_60;
        case 11:
          switch (str[1])
          {
            case 'a':
              if (str == "MapProperty")
              {
                this.ReadProperty(reader, linker);
                this.ReadProperties(reader, linker);
                this.ReadProperties(reader, linker);
                goto label_61;
              }
              goto label_60;
            case 'e':
              if (str == "SetProperty")
                goto label_53;
              goto label_60;
            case 'n':
              if (str == "IntProperty")
                goto label_50;
              goto label_60;
            case 't':
              if (str == "StrProperty")
                goto label_50;
              goto label_60;
            default:
              goto label_60;
          }
        case 12:
          switch (str[0])
          {
            case 'B':
              switch (str)
              {
                case "ByteProperty":
                  this.ReadProperty(reader, linker);
                  reader.ReadInt32();
                  goto label_61;
                case "BoolProperty":
                  this.ReadProperty(reader, linker);
                  int num1 = (int) reader.ReadByte();
                  int num2 = (int) reader.ReadByte();
                  int num3 = (int) reader.ReadByte();
                  int num4 = (int) reader.ReadByte();
                  int num5 = (int) reader.ReadByte();
                  int num6 = (int) reader.ReadByte();
                  goto label_61;
                default:
                  goto label_60;
              }
            case 'E':
              if (str == "EnumProperty")
              {
                this.ReadProperty(reader, linker);
                reader.ReadInt32();
                this.ReadProperties(reader, linker);
                goto label_61;
              }
              goto label_60;
            case 'I':
              if (str == "Int8Property")
                goto label_50;
              goto label_60;
            case 'N':
              if (str == "NameProperty")
                goto label_50;
              goto label_60;
            case 'T':
              if (str == "TextProperty")
                goto label_50;
              goto label_60;
            default:
              goto label_60;
          }
        case 13:
          switch (str[4])
          {
            case '4':
              if (str == "Int64Property")
                goto label_50;
              goto label_60;
            case '6':
              if (str == "Int16Property")
                goto label_50;
              goto label_60;
            case 's':
              if (str == "ClassProperty")
                goto label_47;
              goto label_60;
            case 't':
              if (str == "FloatProperty")
                goto label_50;
              goto label_60;
            case 'y':
              if (str == "ArrayProperty")
                goto label_53;
              goto label_60;
            default:
              goto label_60;
          }
        case 14:
          switch (str[4])
          {
            case '1':
              if (str == "UInt16Property")
                goto label_50;
              goto label_60;
            case '3':
              if (str == "UInt32Property")
                goto label_50;
              goto label_60;
            case '6':
              if (str == "UInt64Property")
                goto label_50;
              goto label_60;
            case 'c':
              switch (str)
              {
                case "ObjectProperty":
                  break;
                case "StructProperty":
                  this.ReadProperty(reader, linker);
                  reader.ReadInt32();
                  goto label_61;
                default:
                  goto label_60;
              }
              break;
            case 'l':
              if (str == "DoubleProperty")
                goto label_50;
              goto label_60;
            default:
              goto label_60;
          }
        case 15:
          switch (str[0])
          {
            case 'A':
              if (str == "AnsiStrProperty")
                goto label_50;
              goto label_60;
            case 'U':
              if (str == "Utf8StrProperty")
                goto label_50;
              goto label_60;
            default:
              goto label_60;
          }
        case 16 /*0x10*/:
          switch (str[0])
          {
            case 'D':
              if (str == "DelegateProperty")
                goto label_58;
              goto label_60;
            case 'O':
              if (str == "OptionalProperty")
              {
                this.ReadProperty(reader, linker);
                reader.ReadName(linker);
                goto label_61;
              }
              goto label_60;
            default:
              goto label_60;
          }
        case 17:
          switch (str[0])
          {
            case 'F':
              if (str == "FieldPathProperty")
              {
                this.ReadProperty(reader, linker);
                reader.ReadInt32();
                goto label_61;
              }
              goto label_60;
            case 'I':
              if (str == "InterfaceProperty")
              {
                this.ReadProperty(reader, linker);
                reader.ReadInt32();
                goto label_61;
              }
              goto label_60;
            case 'S':
              if (str == "SoftClassProperty")
                goto label_47;
              goto label_60;
            default:
              goto label_60;
          }
        case 18:
          switch (str[0])
          {
            case 'L':
              if (str == "LazyObjectProperty")
                goto label_50;
              goto label_60;
            case 'S':
              if (str == "SoftObjectProperty")
                break;
              goto label_60;
            case 'W':
              if (str == "WeakObjectProperty")
                goto label_50;
              goto label_60;
            default:
              goto label_60;
          }
          break;
        case 25:
          if (str == "MulticastDelegateProperty")
            goto label_58;
          goto label_60;
        case 31 /*0x1F*/:
          switch (str[9])
          {
            case 'I':
              if (str == "MulticastInlineDelegateProperty")
                goto label_58;
              goto label_60;
            case 'S':
              if (str == "MulticastSparseDelegateProperty")
                goto label_58;
              goto label_60;
            default:
              goto label_60;
          }
        case 32 /*0x20*/:
          if (str == "LargeWorldCoordinatesRealPropert")
            goto label_50;
          goto label_60;
        default:
          goto label_60;
      }
      this.ReadObjectProperty(reader, linker);
      goto label_61;
label_47:
      this.ReadObjectProperty(reader, linker);
      reader.ReadInt32();
      goto label_61;
label_50:
      this.ReadProperty(reader, linker);
      goto label_61;
label_53:
      this.ReadProperty(reader, linker);
      this.ReadProperties(reader, linker);
      goto label_61;
label_58:
      this.ReadProperty(reader, linker);
      reader.ReadInt32();
label_61:
      return true;
    }
label_60:
    throw new NotImplementedException("Unknown property type: " + str);
  }

  public void Load([NotNull] EndiannessAwareBinaryReader reader, [NotNull] UELinker linker)
  {
    reader.ReadInt32();
    if (linker.Summary.CustomVersionContainer.FindVersion(UEDevObjectVersions.FFrameworkObjectVersion) < 29)
    {
      reader.ReadInt32();
      reader.ReadInt32();
    }
    else
      reader.ReadArray<int>((Func<EndiannessAwareBinaryReader, int>) (r => r.ReadInt32()));
    UEPackageFileSummary summary = linker.Summary;
    if (summary.CustomVersionContainer.FindVersion(UEDevObjectVersions.FCoreObjectVersion) >= 4)
      reader.ReadArray<bool>((Func<EndiannessAwareBinaryReader, bool>) (r => this.ReadProperties(r, linker)));
    reader.ReadInt32();
    reader.ReadInt32();
    reader.ReadArray<bool>((Func<EndiannessAwareBinaryReader, bool>) (r =>
    {
      r.ReadName(linker);
      r.ReadInt32();
      return true;
    }));
    reader.ReadInt32();
    reader.ReadInt32();
    reader.ReadName(linker);
    reader.ReadInt32();
    summary = linker.Summary;
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    // ISSUE: reference to a compiler-generated field
    this.Interfaces = (summary.FileVersionUE >= 361 ? (IEnumerable<UEBlueprintGeneratedClass.SerializedInterfaceReference>) reader.ReadArray<UEBlueprintGeneratedClass.SerializedInterfaceReference>(UEBlueprintGeneratedClass.\u003C\u003EO.\u003C0\u003E__Read ?? (UEBlueprintGeneratedClass.\u003C\u003EO.\u003C0\u003E__Read = new Func<EndiannessAwareBinaryReader, UEBlueprintGeneratedClass.SerializedInterfaceReference>(UEBlueprintGeneratedClass.SerializedInterfaceReference.Read))) : (IEnumerable<UEBlueprintGeneratedClass.SerializedInterfaceReference>) reader.ReadArray<UEBlueprintGeneratedClass.SerializedInterfaceReference>(UEBlueprintGeneratedClass.\u003C\u003EO.\u003C0\u003E__Read ?? (UEBlueprintGeneratedClass.\u003C\u003EO.\u003C0\u003E__Read = new Func<EndiannessAwareBinaryReader, UEBlueprintGeneratedClass.SerializedInterfaceReference>(UEBlueprintGeneratedClass.SerializedInterfaceReference.Read)))).Select<UEBlueprintGeneratedClass.SerializedInterfaceReference, string>((Func<UEBlueprintGeneratedClass.SerializedInterfaceReference, string>) (i =>
    {
      UEObjectResource reference = new UEPackageIndex(i.Class, linker).Reference;
      return reference == null ? "" : reference.ObjectName.Present(linker);
    })).ToList<string>();
  }

  private enum FCoreObjectVersion
  {
    BeforeCustomVersionWasAdded = 0,
    MaterialInputNativeSerialize = 1,
    EnumProperties = 2,
    SkeletalMaterialEditorDataStripping = 3,
    FProperties = 4,
    LatestVersion = 4,
    VersionPlusOne = 5,
  }

  private enum FFrameworkObjectVersion
  {
    BeforeCustomVersionWasAdded = 0,
    UseBodySetupCollisionProfile = 1,
    AnimBlueprintSubgraphFix = 2,
    MeshSocketScaleUtilization = 3,
    ExplicitAttachmentRules = 4,
    MoveCompressedAnimDataToTheDDC = 5,
    FixNonTransactionalPins = 6,
    SmartNameRefactor = 7,
    AddSourceReferenceSkeletonToRig = 8,
    ConstraintInstanceBehaviorParameters = 9,
    PoseAssetSupportPerBoneMask = 10, // 0x0000000A
    PhysAssetUseSkeletalBodySetup = 11, // 0x0000000B
    RemoveSoundWaveCompressionName = 12, // 0x0000000C
    AddInternalClothingGraphicalSkinning = 13, // 0x0000000D
    WheelOffsetIsFromWheel = 14, // 0x0000000E
    MoveCurveTypesToSkeleton = 15, // 0x0000000F
    CacheDestructibleOverlaps = 16, // 0x00000010
    GeometryCacheMissingMaterials = 17, // 0x00000011
    LODsUseResolutionIndependentScreenSize = 18, // 0x00000012
    BlendSpacePostLoadSnapToGrid = 19, // 0x00000013
    SupportBlendSpaceRateScale = 20, // 0x00000014
    LODHysteresisUseResolutionIndependentScreenSize = 21, // 0x00000015
    ChangeAudioComponentOverrideSubtitlePriorityDefault = 22, // 0x00000016
    HardSoundReferences = 23, // 0x00000017
    EnforceConstInAnimBlueprintFunctionGraphs = 24, // 0x00000018
    InputKeySelectorTextStyle = 25, // 0x00000019
    EdGraphPinContainerType = 26, // 0x0000001A
    ChangeAssetPinsToString = 27, // 0x0000001B
    LocalVariablesBlueprintVisible = 28, // 0x0000001C
    RemoveUField_Next = 29, // 0x0000001D
    UserDefinedStructsBlueprintVisible = 30, // 0x0000001E
    PinsStoreFName = 31, // 0x0000001F
    UserDefinedStructsStoreDefaultInstance = 32, // 0x00000020
    FunctionTerminatorNodesUseMemberReference = 33, // 0x00000021
    EditableEventsUseConstRefParameters = 34, // 0x00000022
    BlueprintGeneratedClassIsAlwaysAuthoritative = 35, // 0x00000023
    EnforceBlueprintFunctionVisibility = 36, // 0x00000024
    LatestVersion = 37, // 0x00000025
    StoringUCSSerializationIndex = 37, // 0x00000025
    VersionPlusOne = 38, // 0x00000026
  }

  private struct SerializedInterfaceReference
  {
    public int Class;

    public SerializedInterfaceReference() => this.Class = 0;

    public static UEBlueprintGeneratedClass.SerializedInterfaceReference Read(
      [NotNull] EndiannessAwareBinaryReader reader)
    {
      UEBlueprintGeneratedClass.SerializedInterfaceReference interfaceReference;
      interfaceReference.Class = reader.ReadInt32();
      reader.ReadInt32();
      reader.ReadInt32();
      return interfaceReference;
    }
  }
}
