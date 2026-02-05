using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using JetBrains.ReSharper.Psi;
using ReSharperPlugin.RiderActionExplorer.Models;

namespace ReSharperPlugin.RiderActionExplorer.Services;

public class PsiSyncService
{
    private readonly ServerConfiguration _config;

    public PsiSyncService(ServerConfiguration config)
    {
        _config = config;
    }

    public PsiSyncResult WaitForPsiSync(IPsiSourceFile sourceFile)
    {
        var diskPath = sourceFile.GetLocation().FullPath;
        var sw = Stopwatch.StartNew();

        string diskContent;
        try
        {
            diskContent = NormalizeLineEndings(File.ReadAllText(diskPath));
        }
        catch (Exception ex)
        {
            return new PsiSyncResult
            {
                Status = "disk_read_error",
                WaitedMs = 0,
                Message = ex.GetType().Name + ": " + ex.Message
            };
        }

        var timeoutMs = _config.PsiSyncTimeoutMs;
        var pollIntervalMs = _config.PsiSyncPollIntervalMs;
        var attempts = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            attempts++;
            try
            {
                var doc = sourceFile.Document;
                if (doc != null)
                {
                    var docContent = NormalizeLineEndings(doc.GetText().ToString());
                    if (docContent == diskContent)
                    {
                        return new PsiSyncResult
                        {
                            Status = attempts == 1 ? "synced" : "synced_after_wait",
                            WaitedMs = (int)sw.ElapsedMilliseconds,
                            Attempts = attempts
                        };
                    }
                }
            }
            catch
            {
                // Document may be in a transitional state, keep polling
            }

            Thread.Sleep(pollIntervalMs);
        }

        return new PsiSyncResult
        {
            Status = "timeout",
            WaitedMs = (int)sw.ElapsedMilliseconds,
            Attempts = attempts,
            Message = "PSI document did not match disk content within " + timeoutMs + "ms"
        };
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }
}
