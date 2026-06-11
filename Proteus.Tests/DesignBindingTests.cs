using System;
using System.Collections.Generic;
using System.Text.Json;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Proteus.Services;
using Xunit;

namespace Proteus.Tests;

/// <summary>
/// Tests for the design-binding pieces that are pure / serialization-only:
/// the gear-match heuristic predicate, the binding store round-trip, and the
/// in-memory color-override resolution. No Dalamud / game data required.
/// </summary>
public class DesignBindingTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // Build a design JObject: each tuple is an equipment slot with an item id and an Apply flag.
    private static JObject Design(params (string slot, ulong item, bool apply)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item, apply) in slots)
            eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = apply };
        return new JObject { ["Equipment"] = eq };
    }

    // Build a player-state JObject: every slot is "applied" (full applied state).
    private static JObject State(params (string slot, ulong item)[] slots)
    {
        var eq = new JObject();
        foreach (var (slot, item) in slots)
            eq[slot] = new JObject { ["ItemId"] = item, ["Apply"] = true };
        return new JObject { ["Equipment"] = eq };
    }

    // ── GearMatches ──────────────────────────────────────────────────────────

    [Fact]
    public void GearMatches_AllAppliedSlotsEqual_AndEnoughSlots_IsTrue()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3), ("Legs", 99));
        Assert.True(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_WeaponSlotsAreIgnored()
    {
        // The applied design matches the outfit in every armor slot but the drawn weapon differs
        // (job/gearset/sheathe state) — must still match, because MainHand/OffHand are excluded.
        var design = Design(("MainHand", 8654, true), ("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("MainHand", 16060), ("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.True(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_OneAppliedItemDiffers_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 7)); // hands differ
        Assert.False(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_FewerThanMinimumAppliedSlots_IsFalse()
    {
        // Only two applied slots — below MinGearSlots — even though they match.
        var design = Design(("Head", 1, true), ("Body", 2, true));
        var state  = State(("Head", 1), ("Body", 2));
        Assert.False(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_UnappliedSlotsAreIgnored()
    {
        // Legs is present but Apply=false with a mismatching item — must be ignored.
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true), ("Legs", 555, false));
        var state  = State(("Head", 1), ("Body", 2), ("Hands", 3), ("Legs", 1));
        Assert.True(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_StateMissingAppliedSlot_IsFalse()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        var state  = State(("Head", 1), ("Body", 2)); // no Hands in state
        Assert.False(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_MetaEntriesWithoutItemId_AreIgnored()
    {
        var design = Design(("Head", 1, true), ("Body", 2, true), ("Hands", 3, true));
        // Add a meta entry (no ItemId), like Hat/Visor — should be skipped, not crash.
        ((JObject)design["Equipment"]!)["Hat"] = new JObject { ["Show"] = true, ["Apply"] = true };
        var state = State(("Head", 1), ("Body", 2), ("Hands", 3));
        Assert.True(DesignBindingService.GearMatches(design, state));
    }

    [Fact]
    public void GearMatches_MissingEquipmentObject_IsFalse()
    {
        Assert.False(DesignBindingService.GearMatches(new JObject(), State(("Head", 1))));
        Assert.False(DesignBindingService.GearMatches(Design(("Head", 1, true)), new JObject()));
    }

    // ── IsApplySignal ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(StateChangeType.Design)]
    [InlineData(StateChangeType.Reapply)] // automation apply + revert surface here
    [InlineData(StateChangeType.Reset)]   // manual revert
    public void IsApplySignal_StateWideChanges_AreSignals(StateChangeType type)
        => Assert.True(DesignBindingService.IsApplySignal(type));

    [Theory]
    [InlineData(StateChangeType.Equip)]
    [InlineData(StateChangeType.Customize)]
    [InlineData(StateChangeType.Stains)]
    [InlineData(StateChangeType.MaterialValue)]
    public void IsApplySignal_IndividualTweaks_AreNotSignals(StateChangeType type)
        => Assert.False(DesignBindingService.IsApplySignal(type));

    // ── Store round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void BindingStore_RoundTrips_ThroughJson()
    {
        var id = Guid.NewGuid();
        var store = new DesignBindingStore();
        store.Bindings[id] = new DesignBinding
        {
            DesignId    = id,
            DesignName  = "Outfit A",
            CapturedUtc = new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc),
            Mods =
            [
                new ProteusModBinding
                {
                    ModDirectory = "SomeMod",
                    Enabled      = true,
                    Priority     = 5,
                    Options      = new() { ["Length"] = ["Thigh-high"] },
                    Colors       = new OverlayColorOverride
                    {
                        Top = [ new ColorTableRowPreset { Row = 16, SubRowA = new() { Diffuse = "#FF0000", Opacity = -20 } } ],
                        Options = new()
                        {
                            ["Length"] = new()
                            {
                                ["Thigh-high"] = [ new ColorTableRowPreset { Row = 3, SubRowB = new() { Emissive = 0.5f } } ],
                            },
                        },
                    },
                },
            ],
        };

        var back = JsonSerializer.Deserialize<DesignBindingStore>(JsonSerializer.Serialize(store, JsonOpts), JsonOpts);

        Assert.NotNull(back);
        Assert.True(back!.Bindings.ContainsKey(id));
        var mod = back.Bindings[id].Mods[0];
        Assert.Equal("SomeMod", mod.ModDirectory);
        Assert.True(mod.Enabled);
        Assert.Equal(5, mod.Priority);
        Assert.Equal(["Thigh-high"], mod.Options["Length"]);
        Assert.Equal("#FF0000", mod.Colors.Top![0].SubRowA!.Diffuse);
        Assert.Equal(-20, mod.Colors.Top![0].SubRowA!.Opacity);
        Assert.Equal(0.5f, mod.Colors.Options!["Length"]["Thigh-high"][0].SubRowB!.Emissive);
    }

    // ── OverlayColorOverride.Resolve ─────────────────────────────────────────────

    [Fact]
    public void Resolve_PrefersMatchingOption_FallsBackToTop()
    {
        var top    = new List<ColorTableRowPreset> { new() { Row = 16 } };
        var optRows = new List<ColorTableRowPreset> { new() { Row = 3 } };
        var ovr = new OverlayColorOverride
        {
            Top = top,
            Options = new() { ["Length"] = new() { ["Thigh-high"] = optRows } },
        };

        Assert.Same(optRows, ovr.Resolve("Length", "Thigh-high")); // exact option
        Assert.Same(top, ovr.Resolve("Length", "Ankle"));          // option not stored → top
        Assert.Same(top, ovr.Resolve(null, null));                 // no option context → top
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingStored()
    {
        var ovr = new OverlayColorOverride();
        Assert.Null(ovr.Resolve(null, null));
        Assert.Null(ovr.Resolve("g", "o"));
    }

    // ── ApplyManualModEdit ───────────────────────────────────────────────────────

    private static DesignBinding BindingWith(string modDir, bool enabled, int priority) =>
        new() { Mods = [ new ProteusModBinding { ModDirectory = modDir, Enabled = enabled, Priority = priority } ] };

    [Fact]
    public void ApplyManualModEdit_TogglingEnabled_UpdatesBinding()
    {
        var b = BindingWith("Stockings", enabled: true, priority: 0);
        Assert.True(DesignBindingService.ApplyManualModEdit(b, "Stockings", enabled: false, priority: null));
        Assert.False(b.Mods[0].Enabled);
    }

    [Fact]
    public void ApplyManualModEdit_IsCaseInsensitiveOnModDir_AndEditsPriority()
    {
        var b = BindingWith("Stockings", enabled: true, priority: 0);
        Assert.True(DesignBindingService.ApplyManualModEdit(b, "STOCKINGS", enabled: null, priority: 7));
        Assert.Equal(7, b.Mods[0].Priority);
        Assert.True(b.Mods[0].Enabled); // untouched when only priority is supplied
    }

    [Fact]
    public void ApplyManualModEdit_NoChangeOrUnknownMod_ReturnsFalse()
    {
        var b = BindingWith("Stockings", enabled: true, priority: 5);
        // Same values → no change.
        Assert.False(DesignBindingService.ApplyManualModEdit(b, "Stockings", enabled: true, priority: 5));
        // Mod not part of the binding → no change.
        Assert.False(DesignBindingService.ApplyManualModEdit(b, "OtherMod", enabled: false, priority: 1));
    }

    // ── PickMostRecent ─────────────────────────────────────────────────────────

    [Fact]
    public void PickMostRecent_ReturnsBindingWithLatestCapturedUtc()
    {
        var older  = Guid.NewGuid();
        var newer  = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var bindings = new Dictionary<Guid, DesignBinding>
        {
            [older]  = new() { DesignId = older,  CapturedUtc = new(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc) },
            [newer]  = new() { DesignId = newer,  CapturedUtc = new(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc) },
            [newest] = new() { DesignId = newest, CapturedUtc = new(2026, 5, 28, 19, 51, 0, DateTimeKind.Utc) },
        };
        Assert.Equal(newest, DesignBindingService.PickMostRecent(new[] { older, newer, newest }, bindings));
        Assert.Equal(newer,  DesignBindingService.PickMostRecent(new[] { older, newer },         bindings));
        Assert.Equal(older,  DesignBindingService.PickMostRecent(new[] { older },                bindings));
    }

    [Fact]
    public void PickMostRecent_MissingBindingTreatedAsOldest()
    {
        // An ID present in `matches` but missing from the store can occur transiently if a binding
        // is removed between match-collection and pick. Such IDs must not be preferred.
        var present = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var bindings = new Dictionary<Guid, DesignBinding>
        {
            [present] = new() { DesignId = present, CapturedUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };
        Assert.Equal(present, DesignBindingService.PickMostRecent(new[] { missing, present }, bindings));
    }
}
