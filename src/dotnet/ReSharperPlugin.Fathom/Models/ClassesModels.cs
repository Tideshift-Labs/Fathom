using System.Collections.Generic;

namespace ReSharperPlugin.Fathom.Models;

public class ClassEntry
{
    public string Name { get; set; }
    public string Base { get; set; }
    public string Header { get; set; }
    public string Source { get; set; }
}

public class ClassesResponse
{
    public List<ClassEntry> Classes { get; set; }
}
