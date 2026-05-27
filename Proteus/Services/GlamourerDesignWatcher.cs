using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Proteus.Services;

/// <summary>
/// Watches Glamourer's designs directory and notifies <see cref="DesignBindingService"/> when a
/// design is saved. Purely passive: it never opens design file contents (the GUID comes from the
/// filename) and never writes to the directory, so it cannot lock or corrupt Glamourer's files.
/// Per-GUID debounced because Glamourer autosaves on every edit.
/// </summary>
public sealed class GlamourerDesignWatcher : IDisposable
{
    private const int DebounceMs = 400;

    private readonly DesignBindingService bindingService;
    private readonly IPluginLog log;
    private readonly FileSystemWatcher? watcher;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> debounce = new();

    public GlamourerDesignWatcher(DesignBindingService bindingService, string? designsDir, IPluginLog log)
    {
        this.bindingService = bindingService;
        this.log            = log;

        if (string.IsNullOrEmpty(designsDir) || !Directory.Exists(designsDir))
        {
            log.Warning("[Proteus] Glamourer designs directory not found ({0}); design auto-binding disabled.",
                designsDir ?? "null");
            return;
        }

        try
        {
            watcher = new FileSystemWatcher(designsDir, "*.json")
            {
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error   += OnError;
            watcher.EnableRaisingEvents = true;
            log.Information("[Proteus] Watching Glamourer designs at {0}", designsDir);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to watch Glamourer designs dir; auto-binding disabled.");
            watcher = null;
        }
    }

    private void OnChanged(object _, FileSystemEventArgs e) => Schedule(e.FullPath);
    private void OnRenamed(object _, RenamedEventArgs e)    => Schedule(e.FullPath);
    private void OnError(object _, ErrorEventArgs e)        => log.Warning(e.GetException(), "[Proteus] Design watcher error.");

    private void Schedule(string fullPath)
    {
        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(fullPath), out var id))
            return; // not a {guid}.json design file

        var cts = new CancellationTokenSource();
        debounce.AddOrUpdate(id, cts, (_, prev) => { prev.Cancel(); prev.Dispose(); return cts; });
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DebounceMs, token); }
            catch (OperationCanceledException) { return; }
            debounce.TryRemove(id, out _);
            bindingService.OnDesignSaved(id); // marshals to the framework thread internally
        }, token);
    }

    public void Dispose()
    {
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnChanged;
            watcher.Changed -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error   -= OnError;
            watcher.Dispose();
        }
        foreach (var cts in debounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        debounce.Clear();
    }
}
