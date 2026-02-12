using System.Collections.Generic;

namespace ReSharperPlugin.Fathom.Models;

public class FilesResponse
{
    public string Solution { get; set; }
    public int FileCount { get; set; }
    public List<FileEntry> Files { get; set; }
}

public class FileEntry
{
    public string Path { get; set; }
    public string Ext { get; set; }
    public string Language { get; set; }
}
