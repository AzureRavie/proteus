using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace Proteus;

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

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.SavePluginConfig(this);

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
