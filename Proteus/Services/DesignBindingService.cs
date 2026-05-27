using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Interop;

namespace Proteus.Services;

// ── Persisted model (design_bindings.json) ──────────────────────────────────

public class DesignBindingStore
{
    public int Version { get; set; } = 1;
    public Dictionary<Guid, DesignBinding> Bindings { get; set; } = new();
}

public class DesignBinding
{
    public Guid DesignId { get; set; }
    public string? DesignName { get; set; }
    public DateTime CapturedUtc { get; set; }
    public List<ProteusModBinding> Mods { get; set; } = new();
}

/// <summary>Captured state of one Proteus overlay mod at the moment a design was saved.</summary>
public class ProteusModBinding
{
    public string ModDirectory { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Priority { get; set; }

    /// <summary>Penumbra option group → selected option names.</summary>
    public Dictionary<string, List<string>> Options { get; set; } = new();

    /// <summary>Effective colors at capture time (in-memory override on restore; never written to metadata.json).</summary>
    public OverlayColorOverride Colors { get; set; } = new();
}

/// <summary>
/// Binds the current Proteus state to a Glamourer design (keyed by GUID) on save, and restores it
/// on apply. Observer-only: Proteus never applies designs. Restore writes Penumbra enable/priority/
/// options but applies colors as a non-destructive in-memory override (metadata.json is untouched).
/// Apply detection is heuristic — a unique gear match against the player's current state.
/// </summary>
public class DesignBindingService : IDisposable
{
    // A design must apply at least this many equipment slots to be a heuristic candidate; gearless
    // designs never match (safe abstain). "Most designs save everything including gear" so real
    // outfits apply ~10+.
    private const int MinGearSlots = 3;
    private const int RestoreSuppressMs = 2000;

    private readonly PenumbraBridge penumbra;
    private readonly GlamourerBridge glamourer;
    private readonly SidecarDiscoveryService discovery;
    private readonly CompositorService compositor;
    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string storePath;
    private readonly object gate = new();
    private DesignBindingStore store = new();

    // All of the below are touched only on the framework thread (watcher callbacks marshal first).
    private Dictionary<string, OverlayColorOverride>? activeOverride;
    private Guid? activeDesignId;
    private long suppressUntilTick;
    private readonly Dictionary<Guid, JObject?> designCache = new();

    public DesignBindingService(
        PenumbraBridge penumbra, GlamourerBridge glamourer, SidecarDiscoveryService discovery,
        CompositorService compositor, Configuration config, IDalamudPluginInterface pluginInterface,
        IFramework framework, IPluginLog log)
    {
        this.penumbra   = penumbra;
        this.glamourer  = glamourer;
        this.discovery  = discovery;
        this.compositor = compositor;
        this.config     = config;
        this.framework  = framework;
        this.log        = log;

        storePath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "design_bindings.json");
        Load();

        glamourer.LocalPlayerStateChangedTyped += OnGlamourerStateChangedTyped;
    }

    public void Dispose()
    {
        glamourer.LocalPlayerStateChangedTyped -= OnGlamourerStateChangedTyped;
    }

    // ── UI / accessors ─────────────────────────────────────────────────────────

    public Guid? ActiveDesignId { get { lock (gate) return activeDesignId; } }

    public IReadOnlyList<DesignBinding> Bindings
    {
        get { lock (gate) return store.Bindings.Values.OrderByDescending(b => b.CapturedUtc).ToList(); }
    }

    public bool HasBinding(Guid id) { lock (gate) return store.Bindings.ContainsKey(id); }

    public void RemoveBinding(Guid id)
    {
        lock (gate)
        {
            if (!store.Bindings.Remove(id)) return;
            designCache.Remove(id);
            if (activeDesignId == id) { activeDesignId = null; activeOverride = null; }
            Save();
        }
        compositor.SetActiveColorOverride(null);
    }

    // ── Capture (called by the design-file watcher; any thread) ─────────────────

    /// <summary>Called when a design's {guid}.json is written. Marshals to the framework thread.</summary>
    public void OnDesignSaved(Guid designId)
    {
        if (!config.DesignBindingEnabled) return;
        framework.RunOnFrameworkThread(() => Capture(designId));
    }

    private void Capture(Guid designId)
    {
        try
        {
            var collId = penumbra.GetPlayerCollectionId();
            if (collId == null)
            {
                log.Debug("[Proteus] Skipping design capture for {0}: no player collection.", designId);
                return;
            }

            var name = glamourer.GetDesigns().TryGetValue(designId, out var n) ? n : null;

            var mods = new List<ProteusModBinding>();
            foreach (var e in discovery.DiscoverAll())
            {
                var settings = penumbra.GetModSettings(collId.Value, e.ModDirectory);
                var options  = settings?.Options is { } o
                    ? o.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
                    : new Dictionary<string, List<string>>();

                mods.Add(new ProteusModBinding
                {
                    ModDirectory = e.ModDirectory,
                    Enabled      = e.Enabled,
                    Priority     = e.Priority,
                    Options      = options,
                    Colors       = CaptureColors(e),
                });
            }

            lock (gate)
            {
                store.Bindings[designId] = new DesignBinding
                {
                    DesignId    = designId,
                    DesignName  = name,
                    CapturedUtc = DateTime.UtcNow,
                    Mods        = mods,
                };
                designCache.Remove(designId); // design content changed → drop cached gear
                Save();
            }

            log.Information("[Proteus] Captured Proteus state for design {0} ({1} mods).", name ?? designId.ToString(), mods.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to capture design binding for {0}", designId);
        }
    }

    // Capture the *effective* colors (what the compositor is currently using): the live override for
    // this mod if a design is active, else the mod's metadata. Captures all options so the binding is
    // self-contained; the right one is selected at composite time via OverlayColorOverride.Resolve.
    private OverlayColorOverride CaptureColors(OverlayEntry e)
    {
        OverlayColorOverride? active = null;
        lock (gate) activeOverride?.TryGetValue(e.ModDirectory, out active);

        var result = new OverlayColorOverride
        {
            Top = CloneRows(active?.Top ?? e.Metadata.ColorTableRows),
        };

        if (e.Metadata.OptionGroups is { } groups)
        {
            var opts = new Dictionary<string, Dictionary<string, List<ColorTableRowPreset>>>();
            foreach (var g in groups)
            foreach (var o in g.Options)
            {
                List<ColorTableRowPreset>? rows = null;
                if (active?.Options != null
                    && active.Options.TryGetValue(g.PenumbraGroupName, out var d)
                    && d.TryGetValue(o.Name, out var r))
                    rows = r;
                rows ??= o.ColorTableRows;

                var cloned = CloneRows(rows);
                if (cloned == null) continue;
                if (!opts.TryGetValue(g.PenumbraGroupName, out var inner))
                    opts[g.PenumbraGroupName] = inner = new();
                inner[o.Name] = cloned;
            }
            if (opts.Count > 0) result.Options = opts;
        }

        return result;
    }

    // ── Restore / clear (framework thread) ──────────────────────────────────────

    public void Restore(Guid designId)
    {
        DesignBinding? b;
        lock (gate) store.Bindings.TryGetValue(designId, out b);
        if (b == null) return;

        var present = discovery.DiscoverAll()
            .Select(e => e.ModDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collId = penumbra.GetPlayerCollectionId();

        lock (gate)
        {
            suppressUntilTick = Environment.TickCount64 + RestoreSuppressMs;
            activeDesignId    = designId;
            activeOverride    = b.Mods.ToDictionary(m => m.ModDirectory, m => m.Colors, StringComparer.OrdinalIgnoreCase);
        }
        compositor.SetActiveColorOverride(activeOverride);

        if (collId != null)
        {
            foreach (var m in b.Mods)
            {
                if (!present.Contains(m.ModDirectory)) continue; // mod no longer installed — skip
                penumbra.SetModEnabled(collId.Value, m.ModDirectory, m.Enabled);
                penumbra.SetModPriority(collId.Value, m.ModDirectory, m.Priority);
                foreach (var (group, sel) in m.Options)
                    penumbra.SetModOption(collId.Value, m.ModDirectory, group, sel);
            }
        }

        compositor.TriggerRecomposite($"design-restore:{designId}");
        log.Information("[Proteus] Restored Proteus state for design {0}.", b.DesignName ?? designId.ToString());
    }

    /// <summary>Drop the active color override (revert to metadata colors) and recomposite.</summary>
    public void ClearColorOverride()
    {
        lock (gate) { activeDesignId = null; activeOverride = null; }
        compositor.SetActiveColorOverride(null);
        compositor.TriggerRecomposite("design-override-clear");
    }

    // ── Heuristic apply detection (framework thread) ────────────────────────────

    private void OnGlamourerStateChangedTyped(StateChangeType type)
    {
        if (type != StateChangeType.Design) return;
        if (Environment.TickCount64 < suppressUntilTick) return; // our own restore echo

        Guid[] candidateIds;
        lock (gate) candidateIds = store.Bindings.Keys.ToArray();
        if (candidateIds.Length == 0) return; // nothing bound → leave composite as-is

        var state = glamourer.GetObjectState(0);
        if (state == null) return; // can't read state → abstain

        var matches = new List<Guid>();
        foreach (var id in candidateIds)
        {
            var design = GetDesignCached(id);
            if (design != null && GearMatches(design, state))
                matches.Add(id);
        }

        if (matches.Count == 1)
        {
            if (activeDesignId == matches[0]) return; // already applied
            Restore(matches[0]);
        }
        else if (matches.Count == 0)
        {
            // An unbound/unrecognized design was applied → revert to base colors.
            if (activeDesignId != null) ClearColorOverride();
        }
        // matches.Count >= 2 → ambiguous → abstain (leave current override in place)
    }

    private JObject? GetDesignCached(Guid id)
    {
        lock (gate)
            if (designCache.TryGetValue(id, out var cached))
                return cached;

        var design = glamourer.GetDesign(id);
        lock (gate) designCache[id] = design;
        return design;
    }

    // A design matches the state when every equipment slot it applies has the same ItemId as the
    // player's current state, and it applies a meaningful number of slots. Stains/meta are ignored.
    internal static bool GearMatches(JObject design, JObject state)
    {
        if (design["Equipment"] is not JObject dEquip || state["Equipment"] is not JObject sEquip)
            return false;

        int applied = 0;
        foreach (var prop in dEquip.Properties())
        {
            if (prop.Value is not JObject slot) continue;
            var itemTok = slot["ItemId"];
            if (itemTok == null) continue;                          // skip meta entries (Hat/Visor/Weapon/VieraEars)
            if (slot["Apply"]?.ToObject<bool>() != true) continue;  // slot not applied by the design

            if (sEquip[prop.Name] is not JObject sSlot || sSlot["ItemId"] is not { } sItem)
                return false;                                       // state lacks the slot
            if (itemTok.ToObject<ulong>() != sItem.ToObject<ulong>())
                return false;                                       // different item → not this design
            applied++;
        }

        return applied >= MinGearSlots;
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (File.Exists(storePath))
                store = JsonSerializer.Deserialize<DesignBindingStore>(File.ReadAllText(storePath), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to load design bindings; starting empty.");
            store = new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(storePath, JsonSerializer.Serialize(store, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Proteus] Failed to save design bindings.");
        }
    }

    private static List<ColorTableRowPreset>? CloneRows(List<ColorTableRowPreset>? rows)
        => rows == null ? null : JsonSerializer.Deserialize<List<ColorTableRowPreset>>(JsonSerializer.Serialize(rows));
}
