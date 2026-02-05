namespace ReSharperPlugin.RiderActionExplorer.Models;

/// <summary>
/// Information about the Unreal Engine project detected in the current solution.
/// </summary>
public class UeProjectInfo
{
        /// <summary>
        /// True if this solution contains a UE project.
        /// </summary>
        public bool IsUnrealProject { get; set; }

        /// <summary>
        /// Full path to the .uproject file (e.g., "E:\UE\Projects\MyGame\MyGame.uproject").
        /// </summary>
        public string UProjectPath { get; set; }

        /// <summary>
        /// Full path to the Engine directory (e.g., "D:\UE\Engines\UE_5.7\Engine").
        /// </summary>
        public string EnginePath { get; set; }

        /// <summary>
        /// Engine version string (e.g., "5.7.1").
        /// </summary>
        public string EngineVersion { get; set; }

        /// <summary>
        /// Full path to UnrealEditor-Cmd.exe for running commandlets.
        /// </summary>
        public string CommandletExePath { get; set; }

        /// <summary>
        /// Error message if detection failed.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Directory containing the .uproject file.
        /// Convenience property derived from UProjectPath.
        /// </summary>
        public string ProjectDirectory =>
            string.IsNullOrEmpty(UProjectPath)
                ? null
                : System.IO.Path.GetDirectoryName(UProjectPath);
    }
