namespace ReSharperPlugin.Fathom.Models;

/// <summary>
/// Canonical list of asset types Fathom can audit.
/// Update this when adding new asset type support (mirrors EAuditAssetType in C++).
/// </summary>
public static class AuditCapabilities
{
    public static readonly AuditedAssetType[] SupportedTypes =
    {
        new AuditedAssetType("Blueprint",
            "Blueprints, Widget Blueprints, and Animation Blueprints (event graph only, not the anim graph)"),
        new AuditedAssetType("DataTable",
            "Data Tables with row struct definitions and row data"),
        new AuditedAssetType("DataAsset",
            "Data Assets derived from UDataAsset"),
        new AuditedAssetType("UserDefinedStruct",
            "User-created struct definitions"),
        new AuditedAssetType("ControlRig",
            "Control Rig Blueprints for animation rigging"),
        new AuditedAssetType("Material",
            "Materials and Material Instances (properties, parameters, expression graph)"),
        new AuditedAssetType("BehaviorTree",
            "Behavior Trees with blackboard keys, tree structure, decorators, services, and task properties"),
    };
}

public class AuditedAssetType
{
    public string Name { get; }
    public string Description { get; }

    public AuditedAssetType(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Full project status returned by the /ue-project endpoint.
/// Combines UE project detection, Fathom service state, and audit stats.
/// </summary>
public class ProjectStatusInfo
{
    // UE project detection
    public bool IsUnrealProject { get; set; }
    public string UProjectPath { get; set; }
    public string ProjectDirectory { get; set; }
    public string EnginePath { get; set; }
    public string EngineVersion { get; set; }
    public string CommandletExePath { get; set; }
    public string UnrealBuildToolDllPath { get; set; }
    public string EditorTargetName { get; set; }
    public string Error { get; set; }

    // Fathom versions
    public string FathomVersion { get; set; }
    public string FathomUELinkVersion { get; set; }

    // Fathom server
    public string McpEndpoint { get; set; }

    // FathomUELink editor connection
    public UeLinkStatus UeLink { get; set; }

    // Audit info
    public AuditInfo Audit { get; set; }
}

public class UeLinkStatus
{
    public bool Connected { get; set; }
    public int Port { get; set; }
    public int Pid { get; set; }
    public string Status { get; set; }
}

public class AuditInfo
{
    public string AuditDirectory { get; set; }
    public int SchemaVersion { get; set; }
    public AuditedAssetType[] SupportedAssetTypes { get; set; }
    public int TotalAudited { get; set; }
    public int BlueprintCount { get; set; }
    public int DataTableCount { get; set; }
    public int DataAssetCount { get; set; }
    public int StructureCount { get; set; }
    public int ControlRigCount { get; set; }
    public int MaterialCount { get; set; }
    public int BehaviorTreeCount { get; set; }
    public int StaleCount { get; set; }
    public int ErrorCount { get; set; }
}
