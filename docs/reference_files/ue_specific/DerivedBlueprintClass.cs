// Decompiled with JetBrains decompiler
// Type: JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset.DerivedBlueprintClass
// Assembly: JetBrains.ReSharper.Feature.Services.Cpp, Version=777.0.0.0, Culture=neutral, PublicKeyToken=1010a0d8d6380325
// MVID: 6D919497-FB1A-4BF7-A478-25434533C5C0
// Assembly location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
// XML documentation location: C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.xml

using JetBrains.ReSharper.Psi;

#nullable disable
namespace JetBrains.ReSharper.Feature.Services.Cpp.UE4.UEAsset;

public readonly struct DerivedBlueprintClass(int index, string name, IPsiSourceFile containingFile)
{
  public readonly int Index = index;
  public readonly string Name = name;
  public readonly IPsiSourceFile ContainingFile = containingFile;
}
