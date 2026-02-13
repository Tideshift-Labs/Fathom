using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

/// <summary>
/// Discovers and proxies HTTP requests to the UE editor's asset reference server.
/// The UE server writes a marker file (Saved/Fathom/.fathom-ue-server.json) containing
/// the port and PID. This service reads that file to find the server, validates
/// the PID is alive, and forwards requests.
/// </summary>
public class AssetRefProxyService
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<AssetRefProxyService>();

    private readonly UeProjectService _ueProject;
    private readonly HttpClient _httpClient;

    private int _cachedPort;
    private int _cachedPid;
    private string _markerPath;

    public AssetRefProxyService(UeProjectService ueProject)
    {
        _ueProject = ueProject;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Returns true if the UE editor's asset ref server appears reachable:
    /// marker file exists and the PID is alive.
    /// </summary>
    public bool IsAvailable()
    {
        return TryReadMarker(out _, out _);
    }

    /// <summary>
    /// Returns a status object describing the current connection state.
    /// </summary>
    public AssetRefStatus GetStatus()
    {
        if (!_ueProject.IsUnrealProject())
        {
            return new AssetRefStatus
            {
                Connected = false,
                Message = "Not an Unreal Engine project"
            };
        }

        if (!TryReadMarker(out var port, out var pid))
        {
            return new AssetRefStatus
            {
                Connected = false,
                Message = "UE editor is not running. Asset reference queries require a live editor connection."
            };
        }

        return new AssetRefStatus
        {
            Connected = true,
            Port = port,
            Pid = pid,
            Message = $"Connected to UE editor (port {port}, PID {pid})"
        };
    }

    /// <summary>
    /// Proxy a GET request to the UE editor server. Returns the response body
    /// or null on failure. Throws on HTTP errors.
    /// </summary>
    public string ProxyGet(string path)
    {
        if (!TryReadMarker(out var port, out _))
        {
            return null;
        }

        try
        {
            // Sync-over-async is safe here: callers run on thread pool threads
            // with no SynchronizationContext, and net472 has no sync HttpClient API.
            var url = $"http://localhost:{port}/{path.TrimStart('/')}";
#pragma warning disable VSTHRD002
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"AssetRefProxy: UE server returned {(int)response.StatusCode} for {path}");
            }

            return body;
        }
        catch (Exception ex)
        {
            Log.Warn($"AssetRefProxy: Failed to reach UE server on port {port}: {ex.Message}");
            // Invalidate cached port so next call re-reads marker
            _cachedPort = 0;
            _cachedPid = 0;
            return null;
        }
    }

    /// <summary>
    /// Proxy a GET request and also return the HTTP status code.
    /// </summary>
    public (string Body, int StatusCode) ProxyGetWithStatus(string path)
    {
        if (!TryReadMarker(out var port, out _))
        {
            return (null, 0);
        }

        try
        {
            var url = $"http://localhost:{port}/{path.TrimStart('/')}";
#pragma warning disable VSTHRD002 // Safe: runs on thread pool thread, no SynchronizationContext
            var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            return (body, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Warn($"AssetRefProxy: Failed to reach UE server on port {port}: {ex.Message}");
            _cachedPort = 0;
            _cachedPid = 0;
            return (null, 0);
        }
    }

    private bool TryReadMarker(out int port, out int pid)
    {
        port = 0;
        pid = 0;

        // Use cached values if PID is still alive
        if (_cachedPort > 0 && _cachedPid > 0 && IsProcessAlive(_cachedPid))
        {
            port = _cachedPort;
            pid = _cachedPid;
            return true;
        }

        var markerPath = GetMarkerPath();
        if (string.IsNullOrEmpty(markerPath) || !File.Exists(markerPath))
        {
            _cachedPort = 0;
            _cachedPid = 0;
            return false;
        }

        try
        {
            var json = File.ReadAllText(markerPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("port", out var portEl))
                port = portEl.GetInt32();
            if (root.TryGetProperty("pid", out var pidEl))
                pid = pidEl.GetInt32();
        }
        catch (Exception ex)
        {
            Log.Warn($"AssetRefProxy: Failed to read marker file: {ex.Message}");
            return false;
        }

        if (port <= 0 || pid <= 0)
        {
            return false;
        }

        if (!IsProcessAlive(pid))
        {
            Log.Info($"AssetRefProxy: Marker file PID {pid} is not running, treating as unavailable");
            _cachedPort = 0;
            _cachedPid = 0;
            return false;
        }

        _cachedPort = port;
        _cachedPid = pid;
        return true;
    }

    private string GetMarkerPath()
    {
        if (_markerPath != null)
        {
            return _markerPath;
        }

        try
        {
            var info = _ueProject.GetUeProjectInfo();
            if (!string.IsNullOrEmpty(info.UProjectPath))
            {
                var projectDir = Path.GetDirectoryName(info.UProjectPath);
                if (!string.IsNullOrEmpty(projectDir))
                {
                    _markerPath = Path.Combine(projectDir, "Saved", "Fathom", ".fathom-ue-server.json");
                    return _markerPath;
                }
            }
        }
        catch
        {
            // Fall through
        }

        _markerPath = "";
        return _markerPath;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

public class AssetRefStatus
{
    public bool Connected { get; set; }
    public int Port { get; set; }
    public int Pid { get; set; }
    public string Message { get; set; }
}
