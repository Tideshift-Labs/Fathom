// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon.IUnrealAssetHighlighting
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.Annotations;
using JetBrains.DocumentModel;
using JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Search;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Navigation.Descriptors;
using JetBrains.ReSharper.Psi;
using System;
using System.Collections.Generic;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.Daemon;

public interface IUnrealAssetHighlighting : IHighlighting
{
  DocumentRange Range { get; }

  IDeclaredElement DeclaredElement { get; }

  [NotNull]
  Func<IEnumerable<IUnrealOccurence>> OccurrencesCalculator { get; }

  [NotNull]
  SearchDescriptor GetSearchDescriptor();

  [NotNull]
  IUnrealBlueprintOccurrencesPopupMenuProvider OccurrencesPopupMenuProvider { get; }
}
