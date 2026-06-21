using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

/// <summary>Which sibling body materials Proteus synthesizes for a mod's overlays.</summary>
public enum SiblingSynthesisMode
{
    /// <summary>No sibling synthesis at all (neither gen3 nor vanilla).</summary>
    Off = 0,
    /// <summary>gen3 (_b.mtrl) and bibo (_bibo) bake only — the legacy default; no vanilla.</summary>
    BiboGen3Only = 1,
    /// <summary>gen3 (_b.mtrl), bibo (_bibo.mtrl) bake plus vanilla (gen2 _a.mtrl) generation.</summary>
    AllBodies = 2,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool PluginEnabled { get; set; } = true;

    public bool DisableAutoRedraw { get; set; } = false;

    public int ManagedModPriority { get; set; } = 999;

    /// <summary>
    /// How strongly to suppress skin-tone tinting on opaque overlay pixels (0–1), by fading the
    /// normal map's skin-color-influence channel under the overlay. 1 = overlays keep their authored
    /// color on any skin tone (but those pixels read slightly shinier, since the channel also softens
    /// the skin's specular/subsurface response). 0 = disabled — overlays are tinted by skin tone as
    /// the game normally does, and Proteus no longer rewrites the normal for diffuse-only overlays.
    /// </summary>
    public float SkinColorSuppression { get; set; } = 1f;

    /// <summary>When true, saving a Glamourer design auto-captures the current Proteus state bound to it.</summary>
    public bool DesignBindingEnabled { get; set; } = true;

    /// <summary>Optional explicit path to Glamourer's designs directory; null = derive from the config dir.</summary>
    public string? GlamourerDesignDirOverride { get; set; } = null;

    /// <summary>Per-mod sibling-synthesis mode, keyed by Penumbra mod directory.
    /// Absent = BiboGen3Only (default, = legacy behavior: gen3 bake, no vanilla).</summary>
    public Dictionary<string, SiblingSynthesisMode> SiblingSynthesis { get; set; } = new();

    /// <summary>Sibling-synthesis mode for a mod, applying the absent-default.</summary>
    public SiblingSynthesisMode SiblingModeFor(string modDir) =>
        SiblingSynthesis.TryGetValue(modDir, out var m) ? m : SiblingSynthesisMode.BiboGen3Only;

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
