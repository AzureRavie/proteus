using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Proteus.Interop;

public class GlamourerBridge : IDisposable
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly IDalamudPluginInterface pluginInterface;

    private readonly EventSubscriber<nint, StateChangeType>? stateChangedSub;
    private readonly GetDesignList getDesignList;
    private readonly GetDesignJObject getDesignJObject;
    private readonly GetState getState;

    public bool IsAvailable { get; private set; }

    /// <summary>Fired when Glamourer applies a design, resets, or reapplies state on the local player.</summary>
    public event Action? LocalPlayerStateChanged;

    /// <summary>
    /// Like <see cref="LocalPlayerStateChanged"/> but carries the change type so consumers can
    /// react specifically to design applications (used by the design-binding heuristic).
    /// </summary>
    public event Action<StateChangeType>? LocalPlayerStateChangedTyped;

    /// <summary>
    /// Fired when the local player's character customization changes in a way that may affect
    /// which race/body materials are active (Model or EntireCustomize), without necessarily
    /// changing the mod set. Consumers should recomposite unconditionally.
    /// </summary>
    public event Action? LocalPlayerCustomizationChanged;

    public GlamourerBridge(IDalamudPluginInterface pluginInterface, IObjectTable objectTable, IPluginLog log)
    {
        this.log             = log;
        this.objectTable     = objectTable;
        this.pluginInterface = pluginInterface;

        // FuncSubscriber construction only creates the call gate (safe even if Glamourer is absent);
        // the Invoke() calls below are individually guarded.
        getDesignList    = new GetDesignList(pluginInterface);
        getDesignJObject = new GetDesignJObject(pluginInterface);
        getState         = new GetState(pluginInterface);

        try
        {
            stateChangedSub = StateChangedWithType.Subscriber(pluginInterface, OnStateChanged);
            IsAvailable = true;
            log.Information("[Proteus] Glamourer IPC subscribed.");
        }
        catch (Exception ex)
        {
            log.Warning("[Proteus] Glamourer IPC unavailable — Glamourer design changes won't auto-trigger recomposite. {0}", ex.Message);
        }
    }

    /// <summary>
    /// Glamourer's on-disk designs directory. Glamourer stores designs as {guid}.json under its own
    /// plugin config dir, which is a sibling of Proteus's. Returns null if it can't be determined.
    /// </summary>
    public string? DesignsDirectory
    {
        get
        {
            try
            {
                var parent = pluginInterface.ConfigDirectory.Parent;
                return parent == null ? null : Path.Combine(parent.FullName, "Glamourer", "designs");
            }
            catch { return null; }
        }
    }

    /// <summary>Glamourer's design list (GUID → display name); empty on failure.</summary>
    public Dictionary<Guid, string> GetDesigns()
    {
        try { return getDesignList.Invoke() ?? new(); }
        catch (Exception ex) { log.Warning("[Proteus] GetDesignList failed: {0}", ex.Message); return new(); }
    }

    /// <summary>The serialized data for a single design (includes equipment + apply flags), or null on failure.</summary>
    public JObject? GetDesign(Guid id)
    {
        try { return getDesignJObject.Invoke(id); }
        catch (Exception ex) { log.Warning("[Proteus] GetDesignJObject failed for {0}: {1}", id, ex.Message); return null; }
    }

    /// <summary>The current applied state of an object (default: local player, index 0), or null on failure.</summary>
    public JObject? GetObjectState(int objectIndex = 0)
    {
        try
        {
            var (ec, data) = getState.Invoke(objectIndex);
            return ec == GlamourerApiEc.Success ? data : null;
        }
        catch (Exception ex) { log.Warning("[Proteus] GetState failed: {0}", ex.Message); return null; }
    }

    private void OnStateChanged(nint address, StateChangeType changeType)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address != address) return;

        // Model/EntireCustomize can change race/body without touching mod settings.
        // Fire the customization event so the compositor recomposites unconditionally.
        if (changeType is StateChangeType.Model or StateChangeType.EntireCustomize)
        {
            LocalPlayerCustomizationChanged?.Invoke();
            return;
        }

        // Only care about state-wide changes that can affect which mods are active.
        if (changeType is not (StateChangeType.Design or StateChangeType.Reset or StateChangeType.Reapply))
            return;

        // Typed first so the design-binding heuristic can set its color override before the
        // compositor's (debounced) recomposite reads it.
        LocalPlayerStateChangedTyped?.Invoke(changeType);
        LocalPlayerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        stateChangedSub?.Dispose();
    }
}
