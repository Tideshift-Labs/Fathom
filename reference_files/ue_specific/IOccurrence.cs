// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Occurrences.IOccurrence
// Assembly: JetBrains.ReSharper.Feature.Services, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 4C92A54E-3E1D-4A2A-83F7-BA80E44C71B3
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.xml

using JetBrains.Annotations;
using JetBrains.ProjectModel;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Occurrences;

public interface IOccurrence : INavigatable
{
  [CanBeNull]
  ISolution GetSolution();

  OccurrenceType OccurrenceType { get; }

  bool IsValid { get; }

  string DumpToString();

  OccurrencePresentationOptions PresentationOptions { get; set; }
}
