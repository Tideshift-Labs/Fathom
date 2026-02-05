// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search.IUnrealOccurence
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.ReSharper.Feature.Services.Occurrences;
using JetBrains.ReSharper.Feature.Services.Occurrences.OccurrenceInformation;
using JetBrains.UI.Icons;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;

public interface IUnrealOccurence : IOccurrence, INavigatable
{
  UnrealFindResult FindResult { get; }

  OccurrenceMergeContext MergeContext { get; }

  JetBrains.UI.RichText.RichText GetDisplayText();

  JetBrains.UI.RichText.RichText GetRelatedFilePresentation();

  IconId GetIcon();

  string GetRelatedFolderPresentation();

  OccurrenceKind GetOccurrenceKind();
}
