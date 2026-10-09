using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherBuddy.Config;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private sealed record DungeonOption(uint TerritoryTypeId, string Name, int Level);

    private List<DungeonOption>? _autodutyDungeons;
    private readonly Dictionary<uint, bool> _autodutyHasPath = new();
    private string _autodutyDungeonFilter = string.Empty;
    private bool   _autodutyOnlyWithPath  = true;

    private void DrawAutodutyTab()
    {
        using var tabItem = ImRaii.TabItem("Autoduty##autodutyTab");
        if (!tabItem.Success)
            return;

        var config = GatherBuddy.Config.VulcanAutoDutyConfig;

        ImGui.TextColored(ImGuiColors.DalamudYellow, "Grand Company Seal Farming");
        ImGui.TextWrapped("When a crafting list's Auto Vendor Purchase needs more Grand Company seals than you have, "
          + "Vulcan runs the dungeon below with AutoDuty, turns the loot in with Deliveroo, and repeats until it has enough. "
          + "Then it buys the items.");
        ImGui.Spacing();

        DrawAutodutyRequirements();
        ImGui.Separator();

        var enabled = config.Enabled;
        if (ImGui.Checkbox("Farm seals when purchases need more than I have", ref enabled))
        {
            config.Enabled = enabled;
            GatherBuddy.Config.Save();
        }

        ImGui.Spacing();
        DrawAutodutyGearsetCombo(config);
        DrawAutodutyDungeonCombo(config);

        var maxLoops = config.MaxLoops;
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(100f));
        if (ImGui.InputInt("Max dungeon runs##autodutyLoops", ref maxLoops, 0, 0))
        {
            config.MaxLoops = Math.Clamp(maxLoops, 1, 99);
            GatherBuddy.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Safety limit: stop farming after this many runs even if the seal target hasn't been reached.");

        ImGui.Spacing();
        ImGui.Separator();
        DrawAutodutyTestControls(config);
    }

    private static void DrawAutodutyRequirements()
    {
        static void Row(string name, bool loaded)
        {
            ImGui.TextColored(loaded ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed, loaded ? "●" : "○");
            ImGui.SameLine();
            ImGui.Text(loaded ? $"{name} loaded" : $"{name} not loaded");
        }

        Row("AutoDuty", AutoDutyIpc.Loaded);
        Row("Deliveroo", DeliverooIpc.Loaded);
        Row("vnavmesh", VNavmesh.Enabled);
    }

    private unsafe void DrawAutodutyGearsetCombo(VulcanAutoDutyConfig config)
    {
        var options = new List<(int Index, string Label)> { (-1, "Use current job") };
        var module  = RaptureGearsetModule.Instance();
        var jobs    = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (module != null)
        {
            for (var i = 0; i < 100; i++)
            {
                var entry = module->Entries[i];
                if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                    continue;

                // Crafters (8-15) and gatherers (16-18) can't run dungeons.
                if (entry.ClassJob == 0 || entry.ClassJob is >= 8 and <= 18)
                    continue;

                var abbreviation = jobs != null && jobs.TryGetRow(entry.ClassJob, out var job)
                    ? job.Abbreviation.ExtractText()
                    : entry.ClassJob.ToString();
                options.Add((i, $"{entry.NameString} ({abbreviation})##gs{i}"));
            }
        }

        var selected = options.FirstOrDefault(o => o.Index == config.GearsetIndex);
        var preview  = selected.Label != null
            ? selected.Label.Split("##")[0]
            : config.GearsetIndex >= 0 ? $"Gearset {config.GearsetIndex + 1} (missing)" : "Use current job";

        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(280f));
        using (var combo = ImRaii.Combo("Combat gearset##autodutyGearset", preview))
        {
            if (combo.Success)
            {
                foreach (var (index, label) in options)
                {
                    if (ImGui.Selectable(label, index == config.GearsetIndex))
                    {
                        config.GearsetIndex = index;
                        GatherBuddy.Config.Save();
                    }
                }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Equipped before each dungeon run. Only combat gearsets are listed.");
    }

    private void DrawAutodutyDungeonCombo(VulcanAutoDutyConfig config)
    {
        _autodutyDungeons ??= BuildDungeonOptions();

        var dungeons = _autodutyDungeons;
        if (_autodutyOnlyWithPath && AutoDutyIpc.Loaded)
            dungeons = dungeons.Where(HasAutoDutyPath).ToList();

        var current = _autodutyDungeons.FirstOrDefault(d => d.TerritoryTypeId == config.TerritoryTypeId);
        var preview = current != null
            ? $"Lv{current.Level} {current.Name}"
            : "Select a dungeon...";

        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(380f));
        using (var combo = ImRaii.Combo("Dungeon##autodutyDungeon", preview))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##autodutyFilter", "Filter...", ref _autodutyDungeonFilter, 64);
                foreach (var dungeon in dungeons)
                {
                    if (_autodutyDungeonFilter.Length > 0
                     && !dungeon.Name.Contains(_autodutyDungeonFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (ImGui.Selectable($"Lv{dungeon.Level} {dungeon.Name}##dg{dungeon.TerritoryTypeId}", dungeon.TerritoryTypeId == config.TerritoryTypeId))
                    {
                        config.TerritoryTypeId = dungeon.TerritoryTypeId;
                        GatherBuddy.Config.Save();
                    }
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("Only with AutoDuty path##autodutyOnlyPath", ref _autodutyOnlyWithPath) && _autodutyOnlyWithPath)
            _autodutyHasPath.Clear();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only list dungeons AutoDuty can run. Requires AutoDuty to be loaded.");

        if (config.TerritoryTypeId != 0 && AutoDutyIpc.Loaded && current != null && !HasAutoDutyPath(current))
            ImGui.TextColored(ImGuiColors.DalamudOrange, "AutoDuty has no path for this dungeon.");
    }

    private bool HasAutoDutyPath(DungeonOption dungeon)
    {
        if (!_autodutyHasPath.TryGetValue(dungeon.TerritoryTypeId, out var hasPath))
        {
            hasPath = AutoDutyIpc.ContentHasPath(dungeon.TerritoryTypeId);
            _autodutyHasPath[dungeon.TerritoryTypeId] = hasPath;
        }

        return hasPath;
    }

    private static List<DungeonOption> BuildDungeonOptions()
    {
        var sheet = Dalamud.GameData.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return [];

        // ContentType 2 = Dungeons.
        return sheet
            .Where(row => row.ContentType.RowId == 2 && row.TerritoryType.RowId != 0)
            .Select(row => new DungeonOption(row.TerritoryType.RowId, row.Name.ExtractText(), row.ClassJobLevelRequired))
            .Where(d => d.Name.Length > 0)
            .GroupBy(d => d.TerritoryTypeId)
            .Select(g => g.First())
            .OrderByDescending(d => d.Level)
            .ThenBy(d => d.Name)
            .ToList();
    }

    private void DrawAutodutyTestControls(VulcanAutoDutyConfig config)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Test");

        var currency = VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId();
        var seals    = currency != 0 ? SealFarmStep.GetSeals(currency) : 0u;
        ImGui.Text(currency != 0 ? $"Current seals: {seals:N0}" : "No Grand Company detected.");

        if (SealFarmStep.IsActive)
        {
            if (ImGui.Button("Stop farming##autodutyStop"))
                SealFarmStep.Cancel();
            return;
        }

        var configured = SealFarmStep.IsConfigured(out var problem);
        using (ImRaii.Disabled(!configured || currency == 0))
        {
            if (ImGui.Button("Test: run one dungeon + turn in##autodutyTest"))
                SealFarmStep.TryBegin(currency, seals + 1, ok => GatherBuddy.Log.Information($"[AutodutyTab] Test run finished (success={ok})."));
        }

        if (!configured)
            ImGui.TextColored(ImGuiColors.DalamudOrange, problem);
    }
}
