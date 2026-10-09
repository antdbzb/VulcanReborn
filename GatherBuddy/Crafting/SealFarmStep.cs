using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

/// <summary>
/// Farms Grand Company seals: equips the chosen gearset, runs the chosen dungeon through AutoDuty, turns the loot in
/// with Deliveroo, and repeats until the seal target is reached (or the loop cap / a failure stops it).
/// </summary>
public static class SealFarmStep
{
    private enum Phase
    {
        Idle,
        Preparing,
        StartingDuty,
        RunningDuty,
        WalkingToOfficer,
        TurnIn,
    }

    private static readonly TimeSpan TickInterval       = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GearsetTimeout     = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DutyStartTimeout   = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DutyTimeout        = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DutyEndGrace       = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TurnInStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TurnInTimeout      = TimeSpan.FromMinutes(10);

    private static Phase           _phase = Phase.Idle;
    private static uint            _currencyItemId;
    private static uint            _targetSeals;
    private static Action<bool>?   _onFinished;
    private static int             _loops;
    private static DateTime        _nextTick;
    private static DateTime        _phaseStart;
    private static DateTime        _idleSince;
    private static bool            _gearsetRequested;
    private static bool            _sawDutyStart;
    private static bool            _sawTurnIn;
    private static DateTime?       _delay;

    public static bool IsActive
        => _phase != Phase.Idle;

    public static uint GetSeals(uint currencyItemId)
        => VendorCurrencyAvailabilityResolver.Resolve(VendorCurrencyGroup.GrandCompanySeals, currencyItemId, "Grand Company Seals").AvailableAmount;

    /// <summary>Whether the user has set everything up needed to farm; <paramref name="problem"/> explains what's missing.</summary>
    public static bool IsConfigured(out string problem)
    {
        var config = GatherBuddy.Config.VulcanAutoDutyConfig;
        if (config.TerritoryTypeId == 0)
            problem = "No dungeon is selected on the Autoduty tab.";
        else if (!AutoDutyIpc.Loaded)
            problem = "AutoDuty is not loaded.";
        else if (!DeliverooIpc.Loaded)
            problem = "Deliveroo is not loaded.";
        else if (!AutoDutyIpc.ContentHasPath(config.TerritoryTypeId))
            problem = "AutoDuty has no path for the selected dungeon.";
        else
            problem = string.Empty;

        return problem.Length == 0;
    }

    /// <summary>Starts the loop. <paramref name="onFinished"/> receives true when the seal target was reached.</summary>
    public static bool TryBegin(uint currencyItemId, uint targetSeals, Action<bool> onFinished)
    {
        if (IsActive)
            return false;

        if (!IsConfigured(out var problem))
        {
            Communicator.PrintError($"[VulcanReborn] Cannot farm seals: {problem}");
            return false;
        }

        _currencyItemId   = currencyItemId;
        _targetSeals      = targetSeals;
        _onFinished       = onFinished;
        _loops            = 0;
        _gearsetRequested = false;
        _turnInRequested  = false;
        _delay            = null;
        SetPhase(Phase.Preparing);
        Communicator.Print($"[VulcanReborn] Farming Grand Company seals ({GetSeals(currencyItemId):N0} / {targetSeals:N0}).");
        GatherBuddy.Log.Information($"[SealFarmStep] Started: have {GetSeals(currencyItemId)}, need {targetSeals}.");
        return true;
    }

    public static void Cancel()
    {
        if (!IsActive)
            return;

        GatherBuddy.Log.Information("[SealFarmStep] Cancelled.");
        if (_phase == Phase.RunningDuty || _phase == Phase.StartingDuty)
            AutoDutyIpc.Stop();
        if (_phase == Phase.WalkingToOfficer)
        {
            if (_autoDutyGoto)
                AutoDutyIpc.Stop();
            else
                GatherBuddy.VendorNavigator.Stop();
        }
        if (_phase == Phase.TurnIn && DeliverooIpc.IsTurnInRunning())
            DeliverooIpc.StopTurnIn();

        _phase      = Phase.Idle;
        _onFinished = null;
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
            if (!Player.Available || Dalamud.Conditions[ConditionFlag.BetweenAreas] || Dalamud.Conditions[ConditionFlag.BetweenAreas51])
                return;

            switch (_phase)
            {
                case Phase.Preparing:    UpdatePreparing();    break;
                case Phase.StartingDuty: UpdateStartingDuty(); break;
                case Phase.RunningDuty:  UpdateRunningDuty();  break;
                case Phase.WalkingToOfficer: UpdateWalkingToOfficer(); break;
                case Phase.TurnIn:       UpdateTurnIn();       break;
            }
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[SealFarmStep] Error in Update: {e}");
            Finish(false, "Seal farming hit an error.");
        }
    }

    private static void SetPhase(Phase phase)
    {
        _phase      = phase;
        _phaseStart = DateTime.UtcNow;
    }

    private static unsafe void UpdatePreparing()
    {
        if (Functions.BoundByDuty())
            return;

        var gearset = GatherBuddy.Config.VulcanAutoDutyConfig.GearsetIndex;
        if (gearset >= 0)
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(gearset))
            {
                Finish(false, "The selected combat gearset no longer exists.");
                return;
            }

            var wantedJob = module->Entries[gearset].ClassJob;
            if (Player.Job != wantedJob)
            {
                if (!_gearsetRequested)
                {
                    if (!HumanDelay.Passed(ref _delay))
                        return;

                    _gearsetRequested = true;
                    module->EquipGearset(gearset);
                }

                if (DateTime.UtcNow - _phaseStart > GearsetTimeout)
                    Finish(false, "Timed out switching to the combat gearset.");
                return;
            }
        }

        _gearsetRequested = false;
        SetPhase(Phase.StartingDuty);
    }

    private static void UpdateStartingDuty()
    {
        if (Dalamud.Conditions[ConditionFlag.InCombat] || !HumanDelay.Passed(ref _delay))
            return;

        var territory = GatherBuddy.Config.VulcanAutoDutyConfig.TerritoryTypeId;
        GatherBuddy.Log.Information($"[SealFarmStep] Starting AutoDuty run for territory {territory} (loop {_loops + 1}).");
        if (!AutoDutyIpc.Run(territory, 1))
        {
            Finish(false, "AutoDuty refused to start the run.");
            return;
        }

        _sawDutyStart = false;
        _idleSince    = DateTime.MinValue;
        SetPhase(Phase.RunningDuty);
    }

    private static void UpdateRunningDuty()
    {
        var now      = DateTime.UtcNow;
        var inDuty   = Functions.BoundByDuty();
        var running  = !AutoDutyIpc.IsStopped() || AutoDutyIpc.IsNavigating() || AutoDutyIpc.IsLooping() || inDuty;
        if (running)
        {
            _sawDutyStart = true;
            _idleSince    = DateTime.MinValue;
        }

        if (!_sawDutyStart)
        {
            if (now - _phaseStart > DutyStartTimeout)
                Finish(false, "AutoDuty did not start the run.");
            return;
        }

        if (now - _phaseStart > DutyTimeout)
        {
            AutoDutyIpc.Stop();
            Finish(false, "The dungeon run took too long.");
            return;
        }

        if (running)
            return;

        // AutoDuty is idle and we're out of the duty; give it a moment in case it is between steps.
        if (_idleSince == DateTime.MinValue)
            _idleSince = now;
        if (now - _idleSince < DutyEndGrace)
            return;

        GatherBuddy.Log.Information("[SealFarmStep] Dungeon finished; heading to the Personnel Officer to turn in loot.");
        _sawTurnIn       = false;
        _turnInRequested = false;
        _navStarted      = false;
        _autoDutyGoto    = false;
        _officer         = null;
        SetPhase(Phase.WalkingToOfficer);
    }

    private static bool                _navStarted;
    private static VendorNpcLocation?  _officer;
    private static readonly TimeSpan   WalkTimeout = TimeSpan.FromMinutes(5);

    private static bool                    _autoDutyGoto;
    private static bool                    _sawMovement;
    private static System.Numerics.Vector3 _lastWalkPos;
    private static uint                    _walkStartTerritory;
    private static DateTime                _lastMoved;
    private static readonly TimeSpan       GotoIdleFallback = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan       GotoSettle       = TimeSpan.FromSeconds(4);

    private static void UpdateWalkingToOfficer()
    {
        if (Functions.BoundByDuty())
            return;

        var now = DateTime.UtcNow;
        if (!_navStarted)
        {
            if (!HumanDelay.Passed(ref _delay))
                return;

            _navStarted         = true;
            _phaseStart         = now;
            _sawMovement        = false;
            _lastWalkPos        = Player.Position;
            _walkStartTerritory = Player.Territory;
            _lastMoved          = now;

            // AutoDuty can walk to the Grand Company supply officer itself ("Goto > GCSupply" in its window).
            if (AutoDutyIpc.Loaded)
            {
                _autoDutyGoto = true;
                GatherBuddy.Log.Information("[SealFarmStep] Asking AutoDuty to go to the GC supply officer.");
                Dalamud.Commands.ProcessCommand("/autoduty goto gcsupply");
            }
            else
            {
                StartOwnWalker();
            }

            return;
        }

        if (_autoDutyGoto)
            UpdateAutoDutyGoto(now);
        else
            UpdateOwnWalker(now);
    }

    private static void UpdateAutoDutyGoto(DateTime now)
    {
        var position  = Player.Position;
        var territory = Player.Territory;
        if (territory != _walkStartTerritory || System.Numerics.Vector3.Distance(position, _lastWalkPos) > 0.5f)
        {
            _sawMovement        = true;
            _lastMoved          = now;
            _lastWalkPos        = position;
            _walkStartTerritory = territory; // remember the new zone so a zone change only counts once
        }

        // Arrived = the Personnel Officer is loaded and within interaction range, and we've stopped moving.
        var officer      = FindOfficerObject();
        var distance     = officer == null ? float.MaxValue : System.Numerics.Vector3.Distance(position, officer.Position);
        var headquarters = GetHeadquartersTerritory();
        if (now - _lastWalkLog > TimeSpan.FromSeconds(5))
        {
            _lastWalkLog = now;
            GatherBuddy.Log.Information(
                $"[SealFarmStep] Waiting for arrival: territory={territory} (HQ {headquarters}), officer={(officer == null ? "not loaded" : $"{distance:F1}m")}, "
              + $"moving={(now - _lastMoved < GotoSettle)}, sawMovement={_sawMovement}.");
        }

        if (officer != null
         && distance <= OfficerRange
         && now - _lastMoved > TimeSpan.FromSeconds(1.5)
         && !Dalamud.Conditions[ConditionFlag.BetweenAreas]
         && (headquarters == 0 || territory == headquarters))
        {
            GatherBuddy.Log.Information($"[SealFarmStep] Reached the Personnel Officer ({distance:F1}m away).");
            SetPhase(Phase.TurnIn);
            return;
        }

        if (!_sawMovement && now - _phaseStart > GotoIdleFallback)
        {
            GatherBuddy.Log.Warning("[SealFarmStep] AutoDuty's goto did nothing; walking there myself.");
            _autoDutyGoto = false;
            StartOwnWalker();
            return;
        }

        if (now - _phaseStart > WalkTimeout)
            Finish(false, "Timed out going to the Grand Company supply officer.");
    }

    private const float    OfficerRange = 7f;
    private static DateTime _lastWalkLog;

    private static IGameObject? FindOfficerObject()
    {
        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind == ObjectKind.EventNpc
             && obj.Name.TextValue.Contains("Personnel Officer", StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }

    private static void StartOwnWalker()
    {
        _officer = ResolveOfficer();
        if (_officer == null)
        {
            GatherBuddy.Log.Warning("[SealFarmStep] Could not locate the Personnel Officer; trying the turn-in from here.");
            SetPhase(Phase.TurnIn);
            return;
        }

        GatherBuddy.Log.Information($"[SealFarmStep] Walking to {_officer.NpcName} (territory {_officer.TerritoryId}).");
        GatherBuddy.VendorNavigator.StartNavigation(_officer);
        _phaseStart = DateTime.UtcNow;
    }

    private static unsafe uint GetHeadquartersTerritory()
    {
        var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return playerState == null
            ? 0u
            : playerState->GrandCompany switch
            {
                1 => 128u, // Maelstrom: Limsa Lominsa Upper Decks
                2 => 132u, // Twin Adder: New Gridania
                3 => 130u, // Immortal Flames: Ul'dah - Steps of Nald
                _ => 0u,
            };
    }

    private static void UpdateOwnWalker(DateTime now)
    {
        var navigator = GatherBuddy.VendorNavigator;
        if (navigator.IsReadyToPurchase)
        {
            navigator.Stop();
            GatherBuddy.Log.Information("[SealFarmStep] Reached the Personnel Officer.");
            SetPhase(Phase.TurnIn);
            return;
        }

        if (navigator.IsFailed || (!navigator.IsActive && now - _phaseStart > TimeSpan.FromSeconds(5)) || now - _phaseStart > WalkTimeout)
        {
            navigator.Stop();
            Finish(false, "Could not walk to the Grand Company Personnel Officer.");
        }
    }

    /// <summary>The Personnel Officer in the player's Grand Company headquarters, from the game's level data.</summary>
    private static unsafe VendorNpcLocation? ResolveOfficer()
    {
        var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        var headquarters = playerState == null
            ? 0u
            : playerState->GrandCompany switch
            {
                1 => 128u, // Maelstrom: Limsa Lominsa Upper Decks
                2 => 132u, // Twin Adder: New Gridania
                3 => 130u, // Immortal Flames: Ul'dah - Steps of Nald
                _ => 0u,
            };
        if (headquarters == 0)
            return null;

        var residents = Dalamud.GameData.GetExcelSheet<ENpcResident>();
        var levels    = Dalamud.GameData.GetExcelSheet<Level>();
        if (residents == null || levels == null)
            return null;

        var names = residents
            .Where(r => string.Equals(r.Singular.ExtractText(), "Personnel Officer", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(r => r.RowId, r => r.Singular.ExtractText());
        foreach (var level in levels.Where(l => l.Object.RowId > 1000000 && l.Territory.RowId == headquarters))
        {
            if (!names.TryGetValue(level.Object.RowId, out var name))
                continue;

            var mapRowId = level.Map.RowId != 0 ? level.Map.RowId : level.Territory.ValueNullable?.Map.RowId ?? 0;
            return new VendorNpcLocation(level.Object.RowId, name, headquarters, mapRowId,
                new System.Numerics.Vector3(level.X, level.Y, level.Z), VendorNpcLocationSource.Level);
        }

        return null;
    }

    private static bool     _turnInRequested;
    private static int      _enableAttempts;
    private static DateTime _lastEnableSent;

    private static void UpdateTurnIn()
    {
        var now = DateTime.UtcNow;
        if (Functions.BoundByDuty())
            return;

        if (!_turnInRequested)
        {
            if (!DeliverooIpc.Loaded)
            {
                Finish(false, "Deliveroo is not loaded.");
                return;
            }

            if (!HumanDelay.Passed(ref _delay))
                return;

            _turnInRequested = true;
            _enableAttempts  = 1;
            _phaseStart      = now;
            _lastEnableSent  = now;
            GatherBuddy.Log.Information($"[SealFarmStep] Sent '/deliveroo enable' -> handled={DeliverooIpc.StartTurnIn()} (running={DeliverooIpc.IsTurnInRunning()}).");
            return;
        }

        var running = DeliverooIpc.IsTurnInRunning();
        if (running)
        {
            _sawTurnIn = true;
            _idleSince = DateTime.MinValue;
            if (now - _phaseStart > TurnInTimeout)
            {
                DeliverooIpc.StopTurnIn();
                Finish(false, "The Deliveroo turn-in took too long.");
            }

            return;
        }

        if (!_sawTurnIn)
        {
            // Deliveroo hasn't reported running yet: resend the command a few times before giving up.
            if (now - _lastEnableSent > TimeSpan.FromSeconds(8) && _enableAttempts < 3)
            {
                _enableAttempts++;
                _lastEnableSent = now;
                GatherBuddy.Log.Information($"[SealFarmStep] Deliveroo not running; resending '/deliveroo enable' (attempt {_enableAttempts}) -> handled={DeliverooIpc.StartTurnIn()}.");
                return;
            }

            if (now - _lastEnableSent < TimeSpan.FromSeconds(8) || _enableAttempts < 3)
                return;

            // Never started: stop here instead of assuming there was nothing to turn in and running another dungeon.
            _turnInRequested = false;
            Finish(false, "Deliveroo did not start the turn-in (is it at the Personnel Officer with its 'Handle GC turn ins' option available?).");
            return;
        }
        else
        {
            if (_idleSince == DateTime.MinValue)
                _idleSince = now;
            if (now - _idleSince < TimeSpan.FromSeconds(3))
                return;
        }

        _turnInRequested = false;
        CheckSeals();
    }

    private static void CheckSeals()
    {
        var seals = GetSeals(_currencyItemId);
        _loops++;
        GatherBuddy.Log.Information($"[SealFarmStep] After run {_loops}: {seals} / {_targetSeals} seals.");
        if (seals >= _targetSeals)
        {
            Finish(true, null);
            return;
        }

        var max = Math.Max(1, GatherBuddy.Config.VulcanAutoDutyConfig.MaxLoops);
        if (_loops >= max)
        {
            Finish(false, $"Reached the run limit ({max}) with {seals:N0} / {_targetSeals:N0} seals.");
            return;
        }

        SetPhase(Phase.Preparing);
    }

    private static void Finish(bool success, string? error)
    {
        var callback = _onFinished;
        _onFinished       = null;
        _phase            = Phase.Idle;
        _turnInRequested  = false;
        if (error != null)
            Communicator.PrintError($"[VulcanReborn] {error}");
        else if (success)
            Communicator.Print("[VulcanReborn] Seal target reached.");

        callback?.Invoke(success);
    }
}
