using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using Aetheryte = GatherBuddy.Classes.Aetheryte;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace GatherBuddy.Crafting;

/// <summary>
/// Runs after the vendor step and before gathering: for each still-missing monster-drop material, travels to a
/// spawn area, targets matching mobs and lets Wrath Combo's auto-rotation kill them until enough have dropped.
/// </summary>
public static class CraftingMobFarmStep
{
    public enum BeginResult
    {
        NotNeeded,
        Started,
        Failed,
    }

    private enum Phase
    {
        Idle,
        LoadingData,
        Preparing,
        Teleporting,
        WaitingForZone,
        Traveling,
        Hunting,
    }

    private sealed record Job(uint ItemId, int Needed, List<Candidate> Candidates);

    private sealed record Candidate(uint BNpcNameId, string MobName, MobDropClusterInfo Cluster);

    private static readonly TimeSpan TickInterval         = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PrepareTimeout       = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TeleportTimeout      = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoMobTimeout         = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CandidateTimeout     = TimeSpan.FromMinutes(30);
    private const           float    EngageRange          = 40f;
    private const           float    ArrivalRange         = 8f;
    private const           float    MountDistance        = 60f;
    private const           float    AttackRange          = 3f;

    private static Phase          _phase = Phase.Idle;
    private static CraftingExecutionPlan? _plan;
    private static System.Action?        _continuation;
    private static string         _listName = string.Empty;
    private static int            _combatGearset;
    private static List<Job>      _jobs = [];
    private static int            _jobIndex;
    private static int            _candidateIndex;
    private static Vector3        _destination;
    private static bool           _hasDestination;
    private static WrathComboIpc? _wrath;
    private static DateTime       _nextTick;
    private static DateTime       _phaseStart;
    private static DateTime       _candidateStart;
    private static DateTime       _lastNoMobSeen;
    private static DateTime       _lastMoveCommand;
    private static DateTime       _lastDiagnostic;
    private static DateTime?      _gearsetDelay;
    private static DateTime?      _teleportDelay;
    private static DateTime?      _mountDelay;
    private static DateTime?      _dismountDelay;
    private static DateTime       _lastMountCommand;
    private static bool           _gearsetRequested;

    public static bool IsActive
        => _phase != Phase.Idle;

    private static readonly Dictionary<uint, string> ItemNames = new();

    /// <summary>One-line description of what mob farming is doing right now, or null when idle.</summary>
    public static string? Status
    {
        get
        {
            if (_phase == Phase.Idle)
                return null;
            if (_phase == Phase.LoadingData)
                return "Hunting monsters: loading drop data";
            if (_jobIndex >= _jobs.Count)
                return "Hunting monsters: finishing up";

            var job       = _jobs[_jobIndex];
            var candidate = job.Candidates[Math.Min(_candidateIndex, job.Candidates.Count - 1)];
            var have      = VendorBuyListManager.GetCurrentInventoryAndArmoryCount(job.ItemId);
            var stage = _phase switch
            {
                Phase.Preparing                       => "switching to the combat gearset",
                Phase.Teleporting or Phase.WaitingForZone => "teleporting",
                Phase.Traveling                       => "traveling to the area",
                Phase.Hunting                         => "fighting",
                _                                     => "working",
            };
            return $"Hunting {candidate.MobName} for {GetItemName(job.ItemId)} ({have} / {job.Needed}), "
                 + $"area {_candidateIndex + 1} of {job.Candidates.Count}: {stage}";
        }
    }

    private static string GetItemName(uint itemId)
    {
        if (ItemNames.TryGetValue(itemId, out var name))
            return name;

        var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        name = sheet != null && sheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : $"item {itemId}";
        ItemNames[itemId] = name;
        return name;
    }

    public static BeginResult Begin(CraftingExecutionPlan plan, System.Action continueToGather)
    {
        if (IsActive)
            return BeginResult.Failed;

        _plan         = plan;
        _continuation = continueToGather;
        MobDropInfoCache.EnsureInitializeStarted();
        if (!MobDropInfoCache.IsInitialized)
        {
            GatherBuddy.Log.Debug("[CraftingMobFarmStep] Waiting for mob drop data to load.");
            SetPhase(Phase.LoadingData);
            return BeginResult.Started;
        }

        return Setup();
    }

    private static BeginResult Setup()
    {
        var plan = _plan!;
        var jobs = BuildJobs(plan);
        if (jobs.Count == 0)
        {
            GatherBuddy.Log.Debug("[CraftingMobFarmStep] No monster-drop materials need farming.");
            return BeginResult.NotNeeded;
        }

        if (!WrathComboIpc.IsReady())
        {
            Communicator.PrintError("[VulcanReborn] Auto Mob Farming requires Wrath Combo to be loaded with IPC enabled; stopping the crafting list.");
            return BeginResult.Failed;
        }

        if (!VNavmesh.Enabled)
        {
            Communicator.PrintError("[VulcanReborn] Auto Mob Farming requires the vnavmesh plugin to be loaded; stopping the crafting list.");
            return BeginResult.Failed;
        }

        // The lease is registered later, once we're on the combat job and in position: Wrath drops all leases
        // when it suspends (e.g. around job changes), and a dropped lease is never valid again.
        _wrath = new WrathComboIpc();

        _jobs           = jobs;
        _jobIndex       = 0;
        _candidateIndex = 0;
        _listName      = plan.ListName;
        _combatGearset  = plan.MobFarmGearset;
        _gearsetRequested = false;
        _hasDestination = false;
        SetPhase(Phase.Preparing);
        _candidateStart = DateTime.UtcNow;
        var summary = string.Join(", ", jobs.Select(j => $"{j.ItemId}x{j.Needed}"));
        GatherBuddy.Log.Information($"[CraftingMobFarmStep] Starting mob farming for '{plan.ListName}': {summary}");
        Communicator.Print($"[VulcanReborn] Farming monster drops for '{plan.ListName}'.");
        return BeginResult.Started;
    }

    public static void Cancel()
    {
        if (!IsActive)
            return;

        GatherBuddy.Log.Information("[CraftingMobFarmStep] Cancelled.");
        Cleanup();
    }

    public static void Update()
    {
        if (!IsActive)
            return;

        var now = DateTime.UtcNow;
        if (now < _nextTick)
            return;
        _nextTick = now + TickInterval;

        try
        {
            if (_phase == Phase.LoadingData)
            {
                UpdateLoadingData();
                return;
            }

            if (!Player.Available)
                return;

            if (Player.Object.IsDead)
            {
                Fail("You were defeated; stopping the crafting list.");
                return;
            }

            if (Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
                return;

            if (_jobIndex >= _jobs.Count)
            {
                Finish();
                return;
            }

            if (IsJobSatisfied(_jobs[_jobIndex]))
            {
                GatherBuddy.Log.Information($"[CraftingMobFarmStep] Collected enough of item {_jobs[_jobIndex].ItemId}.");
                NextJob();
                return;
            }

            switch (_phase)
            {
                case Phase.Preparing:      UpdatePreparing();      break;
                case Phase.Teleporting:    UpdateTeleporting();    break;
                case Phase.WaitingForZone: UpdateWaitingForZone(); break;
                case Phase.Traveling:      UpdateTraveling();      break;
                case Phase.Hunting:        UpdateHunting();        break;
            }
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[CraftingMobFarmStep] Error in Update: {e}");
            Fail("Mob farming hit an error; stopping the crafting list.");
        }
    }

    private static void UpdateLoadingData()
    {
        var timedOut = DateTime.UtcNow - _phaseStart > TimeSpan.FromSeconds(60);
        if (!MobDropInfoCache.IsInitialized && !timedOut)
            return;

        var continuation = _continuation;
        _phase = Phase.Idle;
        var result = timedOut ? BeginResult.NotNeeded : Setup();
        if (timedOut)
            GatherBuddy.Log.Warning("[CraftingMobFarmStep] Mob drop data did not load in time; skipping mob farming.");

        switch (result)
        {
            case BeginResult.NotNeeded:
                _continuation = null;
                continuation?.Invoke();
                break;
            case BeginResult.Failed:
                _continuation = null;
                CraftingGatherBridge.StopQueue();
                break;
        }
    }

    private static List<Job> BuildJobs(CraftingExecutionPlan plan)
    {
        var jobs = new List<Job>();
        foreach (var (itemId, needed) in plan.MaterialsView)
        {
            GatherBuddy.Log.Debug(
                $"[CraftingMobFarmStep] Material {itemId}: need {needed}, have {VendorBuyListManager.GetCurrentInventoryAndArmoryCount(itemId)}, "
              + $"mobDrop={MobDropInfoCache.IsKnownDropItem(itemId)}, gatherable={GatherBuddy.GameData.Gatherables.ContainsKey(itemId)}");
            if (needed <= 0
             || VendorBuyListManager.GetCurrentInventoryAndArmoryCount(itemId) >= needed
             || !MobDropInfoCache.IsKnownDropItem(itemId)
             || GatherBuddy.GameData.Gatherables.ContainsKey(itemId)
             || GatherBuddy.GameData.Fishes.ContainsKey(itemId))
                continue;

            if (plan.AutoVendorPurchase && GatherBuddy.VendorBuyListManager.CanAddSupportedItem(itemId))
                continue;

            var candidates = MobDropInfoCache.GetDropInfoForItem(itemId).Mobs
                .SelectMany(mob => mob.Zones.SelectMany(zone => zone.Clusters
                    .Where(cluster => cluster.HasCoordinates)
                    .Select(cluster => new Candidate(mob.BNpcNameId, mob.MobName, cluster))))
                .GroupBy(c => c.Cluster.TerritoryTypeId)
                // Keep each zone's areas together so we never bounce between zones: the current zone first, then the
                // zones with the most spawn points.
                .OrderBy(g => g.Key == Player.Territory ? 0 : 1)
                .ThenByDescending(g => g.Max(c => c.Cluster.SpawnPointCount))
                .SelectMany(g => g.OrderByDescending(c => c.Cluster.SpawnPointCount))
                .ToList();
            if (candidates.Count == 0)
            {
                GatherBuddy.Log.Warning($"[CraftingMobFarmStep] No spawn data for item {itemId}; skipping.");
                continue;
            }

            jobs.Add(new Job(itemId, needed, candidates));
        }

        return jobs;
    }

    private static bool IsJobSatisfied(Job job)
        => VendorBuyListManager.GetCurrentInventoryAndArmoryCount(job.ItemId) >= job.Needed;

    private static Candidate CurrentCandidate
        => _jobs[_jobIndex].Candidates[_candidateIndex];

    private static void SetPhase(Phase phase)
    {
        _phase      = phase;
        _phaseStart = DateTime.UtcNow;
    }

    private static void NextJob()
    {
        StopMovement();
        _jobIndex++;
        _candidateIndex = 0;
        _teleportCount  = 0;
        _hasDestination = false;
        _candidateStart = DateTime.UtcNow;
        SetPhase(Phase.Traveling);
    }

    private static void NextCandidate(string reason)
    {
        StopMovement();
        var job = _jobs[_jobIndex];
        GatherBuddy.Log.Information($"[CraftingMobFarmStep] Leaving {CurrentCandidate.MobName} area ({reason}).");
        _candidateIndex++;
        _hasDestination = false;
        _candidateStart = DateTime.UtcNow;
        if (_candidateIndex >= job.Candidates.Count)
        {
            Communicator.PrintError($"[VulcanReborn] Could not farm item {job.ItemId}; no more spawn areas to try.");
            NextJob();
            return;
        }

        SetPhase(Phase.Traveling);
    }

    private static unsafe void UpdatePreparing()
    {
        if (Player.Job is >= 8 and <= 18)
        {
            if (_combatGearset <= 0)
            {
                Fail("Auto Mob Farming needs a combat job: switch to one, or set a combat gearset on the list. Stopping the crafting list.");
                return;
            }

            if (!_gearsetRequested)
            {
                if (!HumanDelay.Passed(ref _gearsetDelay))
                    return;

                _gearsetRequested = true;
                var module = RaptureGearsetModule.Instance();
                if (module == null || !module->IsValidGearset(_combatGearset - 1))
                {
                    Fail($"Combat gearset {_combatGearset} is not valid; stopping the crafting list.");
                    return;
                }

                module->EquipGearset(_combatGearset - 1);
            }

            if (DateTime.UtcNow - _phaseStart > PrepareTimeout)
                Fail("Timed out switching to the combat gearset; stopping the crafting list.");
            return;
        }

        SetPhase(Phase.Traveling);
    }

    private static void UpdateTeleporting()
    {
        var candidate = CurrentCandidate;
        var aetheryte = ResolveAetheryte(candidate.Cluster);
        if (aetheryte == null)
        {
            NextCandidate("no aetheryte available");
            return;
        }

        if (Dalamud.Conditions[ConditionFlag.InCombat] || Dalamud.Conditions[ConditionFlag.Casting])
            return;

        if (!HumanDelay.Passed(ref _teleportDelay))
            return;

        if (++_teleportCount > MaxTeleportsPerItem)
        {
            Communicator.PrintError($"[VulcanReborn] Teleported {MaxTeleportsPerItem} times looking for item {_jobs[_jobIndex].ItemId} without success; giving up on it.");
            NextJob();
            return;
        }

        Executor.TeleportToAetheryte(aetheryte);
        SetPhase(Phase.WaitingForZone);
    }

    private static void UpdateWaitingForZone()
    {
        if (Player.Territory == CurrentCandidate.Cluster.TerritoryTypeId && Player.Interactable)
        {
            SetPhase(Phase.Traveling);
            return;
        }

        if (DateTime.UtcNow - _phaseStart > TeleportTimeout)
            NextCandidate("teleport timed out");
    }

    private static void UpdateTraveling()
    {
        var candidate = CurrentCandidate;
        if (Player.Territory != candidate.Cluster.TerritoryTypeId)
        {
            SetPhase(Phase.Teleporting);
            return;
        }

        if (DateTime.UtcNow - _candidateStart > CandidateTimeout)
        {
            NextCandidate("time limit");
            return;
        }

        if (FindMob(candidate.BNpcNameId) is { } mob && Vector3.Distance(Player.Position, mob.Position) <= EngageRange)
        {
            _lastNoMobSeen = DateTime.UtcNow;
            SetPhase(Phase.Hunting);
            return;
        }

        if (!_hasDestination)
        {
            // Right after a zone change vnavmesh is still loading the navmesh, so a missing floor point isn't final yet.
            var navReady = false;
            try { navReady = VNavmesh.Nav.IsReady(); } catch { /* not ready */ }
            if (!navReady)
            {
                if (DateTime.UtcNow - _phaseStart > TimeSpan.FromSeconds(45))
                    NextCandidate("navmesh did not load");
                return;
            }

            if (!TryResolveDestination(candidate.Cluster, out _destination))
            {
                NextCandidate("could not resolve a destination");
                return;
            }
        }

        _hasDestination = true;
        var distance = Vector3.Distance(Player.Position, _destination);
        if (distance <= ArrivalRange)
        {
            StopMovement();
            var (total, fate, busy) = CountMobs(candidate.BNpcNameId);
            var waited = DateTime.UtcNow - _phaseStart;
            if (DateTime.UtcNow - _lastDiagnostic > TimeSpan.FromSeconds(10))
            {
                _lastDiagnostic = DateTime.UtcNow;
                GatherBuddy.Log.Information(
                    $"[CraftingMobFarmStep] Arrived at {candidate.MobName} area; nearby with that name: {total} (FATE: {fate}, fighting others: {busy}).");
            }

            if (total > 0 && fate == total && waited > TimeSpan.FromSeconds(5))
                NextCandidate("only FATE monsters here");
            else if (waited > NoMobTimeout)
                NextCandidate("no mobs found");
            return;
        }

        TryMount(distance);
        IssueMove(_destination);
    }

    private static void UpdateHunting()
    {
        var candidate = CurrentCandidate;
        var player    = Player.Object!;
        var now       = DateTime.UtcNow;

        if (Dalamud.Conditions[ConditionFlag.Mounted])
        {
            StopMovement();
            if (now - _lastMountCommand > TimeSpan.FromSeconds(1.5) && HumanDelay.Passed(ref _dismountDelay))
            {
                _lastMountCommand = now;
                Dismount();
            }

            return;
        }

        if (_wrath != null && !_wrath.Maintain() && _wrath.Failures >= 10)
        {
            Fail("Wrath Combo keeps refusing control (is its IPC enabled?); stopping the crafting list.");
            return;
        }

        // Anything already attacking us takes priority, whether or not it's the mob we're hunting.
        // Otherwise we'd stand idle holding aggro from unrelated mobs.
        var current = Dalamud.Targets.Target as IBattleNpc;
        var valid   = current is { IsDead: false, CurrentHp: > 0 }
                   && (IsAggressor(current, player)
                    || (current.NameId == candidate.BNpcNameId && !IsFateMob(current) && FindAggressor() == null));
        if (!valid)
        {
            var next = FindAggressor() ?? FindMob(candidate.BNpcNameId);
            if (next == null)
            {
                if (Dalamud.Conditions[ConditionFlag.InCombat])
                    return;
                if (now - _lastNoMobSeen > TimeSpan.FromSeconds(NoMobTimeout.TotalSeconds * 0.5))
                {
                    SetPhase(Phase.Traveling);
                    _hasDestination = false;
                }

                return;
            }

            Dalamud.Targets.Target = next;
            current = next;
        }

        _lastNoMobSeen = now;
        var distance = Vector3.Distance(player.Position, current!.Position);

        if (current.GameObjectId != _targetId)
        {
            _targetId    = current.GameObjectId;
            _targetSince = now;
        }

        // Wrath doesn't start the fight itself, so open with the job's basic attack. It takes over once in combat.
        // With a Wrath lease, Wrath fights once combat starts and we only need to open. Without one (Wrath refused or
        // blacklisted us), keep using the basic attack ourselves so fights still finish.
        var wrathDriving = _wrath is { HasLease: true };
        var wantAttack = distance <= AttackRange + 1f
                      && (wrathDriving
                              ? !Dalamud.Conditions[ConditionFlag.InCombat]
                             && now - _targetSince > TimeSpan.FromSeconds(2.5)
                             && now - _lastPull > TimeSpan.FromSeconds(2)
                              : now - _targetSince > TimeSpan.FromSeconds(0.5)
                             && now - _lastPull > TimeSpan.FromSeconds(1.3));
        if (wantAttack)
        {
            _lastPull = now;
            PullWithOpener(current);
        }

        if (now - _lastDiagnostic > TimeSpan.FromSeconds(5))
        {
            _lastDiagnostic = now;
            var inCombat = Dalamud.Conditions[ConditionFlag.InCombat];
            GatherBuddy.Log.Information(
                $"[CraftingMobFarmStep] Hunting: target='{current.Name}' (nameId={current.NameId}, hp={current.CurrentHp}) dist={distance:F1} "
              + $"inCombat={inCombat} job={Player.Job} | Wrath: {_wrath?.Describe()}");
            if (_wrath != null && _wrath.HasLease && !_wrath.IsJobReady())
                _wrath.RequestJobReady();
        }

        if (distance > AttackRange + 0.5f)
        {
            if (now - _lastMoveCommand > TimeSpan.FromSeconds(1))
            {
                _lastMoveCommand = now;
                VNavmesh.SimpleMove.PathfindAndMoveCloseTo(current.Position, false, AttackRange);
            }
        }
        else if (VNavmesh.Path.IsRunning())
        {
            VNavmesh.Path.Stop();
        }
    }

    private static DateTime _lastPull;
    private static DateTime _targetSince;
    private static ulong    _targetId;
    private static uint     _openerJob;
    private static uint     _openerAction;

    private static unsafe void PullWithOpener(IBattleNpc target)
    {
        var action = ResolveOpener();
        if (action == 0)
            return;

        var manager = ActionManager.Instance();
        if (manager == null)
            return;

        var used = manager->UseAction(ActionType.Action, action, target.GameObjectId);
        GatherBuddy.Log.Debug($"[CraftingMobFarmStep] Attacking '{target.Name}' with action {action}: {used}");
    }

    /// <summary>The job's lowest-level offensive action, preferring instant ones: a generic pull that works on any combat job.</summary>
    private static uint ResolveOpener()
    {
        var job = Player.Job;
        if (_openerJob == job)
            return _openerAction;

        _openerJob    = job;
        _openerAction = 0;
        var classJobs = Dalamud.GameData.GetExcelSheet<ClassJob>();
        var actions   = Dalamud.GameData.GetExcelSheet<LuminaAction>();
        if (classJobs == null || actions == null || !classJobs.TryGetRow(job, out var classJob))
            return 0;

        var owners = new HashSet<uint> { job, classJob.ClassJobParent.RowId };
        var best = actions
            .Where(a => a.IsPlayerAction
                     && !a.IsPvP
                     && a.CanTargetHostile
                     && a.ClassJobLevel > 0
                     && owners.Contains(a.ClassJob.RowId)
                     && a.ActionCategory.RowId is 2 or 3)
            .OrderBy(a => a.Cast100ms == 0 ? 0 : 1)
            .ThenBy(a => a.ClassJobLevel)
            .ThenBy(a => a.RowId)
            .FirstOrDefault();
        _openerAction = best.RowId;
        GatherBuddy.Log.Information($"[CraftingMobFarmStep] Opener for job {job}: {(_openerAction == 0 ? "none found" : $"{best.Name} ({_openerAction})")}");
        return _openerAction;
    }

    private const int   MaxTeleportsPerItem = 4;
    private static int  _teleportCount;

    /// <summary>Living mobs with this name currently loaded: total, FATE-flagged, and busy with someone else.</summary>
    private static (int Total, int Fate, int Busy) CountMobs(uint nameId)
    {
        var player = Player.Object;
        int total = 0, fate = 0, busy = 0;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj is not IBattleNpc npc || npc.NameId != nameId || npc.IsDead || npc.CurrentHp == 0)
                continue;

            total++;
            if (IsFateMob(npc))
                fate++;
            else if (player != null && npc.StatusFlags.HasFlag(StatusFlags.InCombat) && npc.TargetObjectId != player.GameObjectId)
                busy++;
        }

        return (total, fate, busy);
    }

    private static unsafe bool IsFateMob(IBattleNpc npc)
    {
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)npc.Address;
        return chara != null && chara->FateId != 0;
    }

    private const float AggressorRange = 30f;

    private static bool IsAggressor(IBattleNpc npc, IGameObject player)
        => !npc.IsDead
        && npc.CurrentHp > 0
        && npc.StatusFlags.HasFlag(StatusFlags.InCombat)
        && npc.TargetObjectId == player.GameObjectId;

    /// <summary>Nearest living mob that is currently attacking the player, regardless of its name.</summary>
    private static IBattleNpc? FindAggressor()
    {
        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? best = null;
        var bestDistance = AggressorRange;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj is not IBattleNpc npc || !npc.IsTargetable || !IsAggressor(npc, player))
                continue;

            var distance = Vector3.Distance(player.Position, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best         = npc;
            }
        }

        return best;
    }

    private static IBattleNpc? FindMob(uint nameId)
    {
        var player = Player.Object;
        if (player == null)
            return null;

        IBattleNpc? best = null;
        var bestDistance = float.MaxValue;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj is not IBattleNpc npc || npc.NameId != nameId || npc.IsDead || !npc.IsTargetable || npc.CurrentHp == 0)
                continue;

            // FATE monsters (e.g. the boss) share names with the regular ones but are far tougher.
            if (IsFateMob(npc))
                continue;

            if (npc.StatusFlags.HasFlag(StatusFlags.InCombat) && npc.TargetObjectId != player.GameObjectId)
                continue;

            var distance = Vector3.Distance(player.Position, npc.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best         = npc;
            }
        }

        return best;
    }

    private static bool TryResolveDestination(MobDropClusterInfo cluster, out Vector3 destination)
    {
        destination = default;
        var mapSheet = Dalamud.GameData.GetExcelSheet<Map>();
        if (mapSheet == null || !mapSheet.TryGetRow(cluster.MapRowId, out var map) || map.SizeFactor == 0)
            return false;

        var x = MapToWorld(cluster.MapX, map.SizeFactor, map.OffsetX);
        var z = MapToWorld(cluster.MapY, map.SizeFactor, map.OffsetY);
        var probe = new Vector3(x, Player.Position.Y, z);
        var floor = VNavmesh.Query.Mesh.NearestPoint(probe, 20f, 300f)
                 ?? VNavmesh.Query.Mesh.NearestPoint(probe, 60f, 300f)
                 ?? VNavmesh.Query.Mesh.PointOnFloor(probe, false, 60f);
        if (floor == null)
        {
            GatherBuddy.Log.Debug($"[CraftingMobFarmStep] No navmesh point near ({x:F1}, {z:F1}).");
            return false;
        }

        destination = floor.Value;
        return true;
    }

    private static float MapToWorld(float mapCoord, uint sizeFactor, int offset)
    {
        const double factor = 0.019999999552965164d;
        return (float)((mapCoord - 1.0d - (factor * offset) - (2048.0d / sizeFactor)) / factor);
    }

    private static Aetheryte? ResolveAetheryte(MobDropClusterInfo cluster)
    {
        if (cluster.TerritoryTypeId == 0)
            return null;

        if (!GatherBuddy.GameData.Territories.TryGetValue(cluster.TerritoryTypeId, out var territory))
        {
            var sheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
            if (sheet == null || !sheet.TryGetRow(cluster.TerritoryTypeId, out var row))
                return null;

            territory = GatherBuddy.GameData.FindOrAddTerritory(row);
        }

        if (territory == null || territory.Aetherytes.Count == 0)
            return null;

        var mapX = (int)MathF.Round(cluster.MapX * 100f);
        var mapY = (int)MathF.Round(cluster.MapY * 100f);
        return territory.Aetherytes
            .OrderBy(a => a.WorldDistance(territory.Id, mapX, mapY))
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }

    private static void TryMount(float distance)
    {
        if (distance < MountDistance
         || Dalamud.Conditions[ConditionFlag.Mounted]
         || Dalamud.Conditions[ConditionFlag.InCombat]
         || Dalamud.Conditions[ConditionFlag.Casting]
         || DateTime.UtcNow - _lastMountCommand < TimeSpan.FromSeconds(3))
            return;

        if (!HumanDelay.Passed(ref _mountDelay))
            return;

        _lastMountCommand = DateTime.UtcNow;
        unsafe
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9); // Mount Roulette
        }
    }

    private static unsafe void Dismount()
        => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);

    private static void IssueMove(Vector3 destination)
    {
        if (Dalamud.Conditions[ConditionFlag.Casting] || DateTime.UtcNow - _lastMoveCommand < TimeSpan.FromSeconds(2))
            return;
        if (VNavmesh.Path.IsRunning() || VNavmesh.SimpleMove.PathfindInProgress())
            return;

        _lastMoveCommand = DateTime.UtcNow;
        VNavmesh.SimpleMove.PathfindAndMoveTo(destination, false);
    }

    private static void StopMovement()
    {
        try
        {
            if (VNavmesh.Enabled && VNavmesh.Path.IsRunning())
                VNavmesh.Path.Stop();
        }
        catch
        {
            // vnavmesh may have been unloaded.
        }
    }

    private static void Finish()
    {
        var continuation = _continuation;
        Cleanup();
        Communicator.Print($"[VulcanReborn] Monster farming finished for '{_listName}'.");
        continuation?.Invoke();
    }

    private static void Fail(string message)
    {
        Communicator.PrintError($"[VulcanReborn] {message}");
        Cleanup();
        CraftingGatherBridge.StopQueue();
    }

    private static void Cleanup()
    {
        StopMovement();
        if (_wrath != null)
        {
            _wrath.SetAutoRotation(false);
            _wrath.Release();
            _wrath = null;
        }

        _phase        = Phase.Idle;
        _continuation = null;
        _jobs         = [];
        _hasDestination = false;
    }
}
