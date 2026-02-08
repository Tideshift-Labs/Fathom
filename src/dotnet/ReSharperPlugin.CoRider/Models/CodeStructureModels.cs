using System.Collections.Generic;

namespace ReSharperPlugin.CoRider.Models;

public class DescribeCodeResponse
{
    public string Solution { get; set; }
    public List<FileStructure> Files { get; set; }
    public int TotalFiles { get; set; }
    public DescribeCodeDebugInfo Debug { get; set; }
}

public class DescribeCodeDebugInfo
{
    public int TotalMs { get; set; }
    public List<string> Diagnostics { get; set; }
}

public class FileStructure
{
    public string RequestedPath { get; set; }
    public string ResolvedPath { get; set; }
    public string Language { get; set; }
    public string Error { get; set; }
    public List<NamespaceInfo> Namespaces { get; set; }
    public List<TypeInfo> Types { get; set; }
    public List<MemberInfo> FreeFunctions { get; set; }
    public List<string> Includes { get; set; }
}

public class NamespaceInfo
{
    public string Name { get; set; }
    public List<TypeInfo> Types { get; set; }
    public List<MemberInfo> FreeFunctions { get; set; }
    public List<NamespaceInfo> Namespaces { get; set; }
}

public class TypeInfo
{
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Access { get; set; }
    public string BaseType { get; set; }
    public List<string> Interfaces { get; set; }
    public List<string> TypeParameters { get; set; }
    public List<MemberInfo> Members { get; set; }
    public List<TypeInfo> NestedTypes { get; set; }
    public int? Line { get; set; }

    public bool? IsAbstract { get; set; }
    public bool? IsSealed { get; set; }
    public bool? IsStatic { get; set; }
}

public class MemberInfo
{
    public string Kind { get; set; }
    public string Name { get; set; }
    public string Access { get; set; }
    public string ReturnType { get; set; }
    public string Type { get; set; }
    public List<ParameterInfo> Parameters { get; set; }
    public int? Line { get; set; }

    public bool? IsStatic { get; set; }
    public bool? IsVirtual { get; set; }
    public bool? IsAbstract { get; set; }
    public bool? IsOverride { get; set; }
    public bool? IsAsync { get; set; }
    public bool? IsReadonly { get; set; }
    public bool? HasGetter { get; set; }
    public bool? HasSetter { get; set; }
}

public class ParameterInfo
{
    public string Name { get; set; }
    public string Type { get; set; }
    public bool? IsParams { get; set; }
    public bool? HasDefault { get; set; }
    public string Modifier { get; set; }
}
