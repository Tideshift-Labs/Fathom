using System.Collections.Generic;

namespace ReSharperPlugin.Fathom.Models;

public class SymbolResult
{
    public string Name { get; set; }
    public string Kind { get; set; }
    public string File { get; set; }
    public int Line { get; set; }
    public string SymbolType { get; set; }
}

public class SymbolDeclaration
{
    public string Name { get; set; }
    public string Kind { get; set; }
    public string File { get; set; }
    public int Line { get; set; }
    public string ContainingType { get; set; }
    public string Snippet { get; set; }
}

public class SymbolSearchResponse
{
    public string Query { get; set; }
    public List<SymbolResult> Results { get; set; }
    public int TotalMatches { get; set; }
    public bool Truncated { get; set; }
}

public class DeclarationResponse
{
    public string Symbol { get; set; }
    public List<SymbolDeclaration> Declarations { get; set; }
    public int ForwardDeclarations { get; set; }
}

public class InheritorsResponse
{
    public string Symbol { get; set; }
    public List<SymbolResult> CppInheritors { get; set; }
    public List<BlueprintInheritor> BlueprintInheritors { get; set; }
    public int TotalCpp { get; set; }
    public bool Truncated { get; set; }
}

public class BlueprintInheritor
{
    public string Name { get; set; }
    public string AssetPath { get; set; }
}
