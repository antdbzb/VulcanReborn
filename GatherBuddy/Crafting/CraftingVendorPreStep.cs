using System;
using System.Linq;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using GrandCompanyRank = Lumina.Excel.Sheets.GrandCompanyRank;

namespace GatherBuddy.Crafting;

/// <summary>
/// Runs after any retainer withdrawal and before gathering: builds a temporary vendor buy list from the
/// plan's still-missing raw materials and works through it in rounds, then hands control back to the gather stage.
/// Each round buys everything currently affordable; if Grand Company purchases are still short on seals and seal
/// farming is enabled, it farms seals (AutoDuty + Deliveroo) up to just below the seal cap, then buys again. Buying
/// first keeps seals from capping out when the total needed exceeds what a character can hold.
/// </summary>
public static class CraftingVendorPreStep
{
    public enum BeginResult
    {
        NotNeeded,
        Started,
        Failed,
    }

    private enum Last
    {
        None,
        Buy,
        Farm,
    }

    private const int  MaxRounds        = 30;
    private const uint SealCapMargin    = 4000;
    private const uint DefaultSealCap   = 90000;

    private static Guid?   _tempListId;
    private static Action? _continuation;
    private static bool    _farmingSeals;
    private static bool    _buying;
    private static bool    _farmFailed;
    private static string  _planName = string.Empty;
    private static Last    _last;
    private static int     _rounds;
    private static int     _stagnantRounds;
    private static long    _pendingAtLastBuy;

    public static bool IsActive
        => _tempListId.HasValue;

    /// <summary>One-line description of the vendor step, or null when idle (seal farming reports its own status).</summary>
    public static string? Status
    {
        get
        {
            if (!_tempListId.HasValue || _farmingSeals)
                return null;

            if (_buying)
            {
                var text = GatherBuddy.VendorBuyListManager.StatusText;
                return string.IsNullOrWhiteSpace(text) ? "Buying materials from vendors" : $"Buying materials: {text}";
            }

            return "Planning vendor purchases";
        }
    }

    public static BeginResult Begin(CraftingExecutionPlan plan, Action continueToGather)
    {
        if (IsActive)
            return BeginResult.Failed;

        var manager = GatherBuddy.VendorBuyListManager;
        var requests = plan.MaterialsView
            .Where(kv => kv.Value > 0
                && manager.CanAddSupportedItem(kv.Key)
                && VendorBuyListManager.GetCurrentInventoryAndArmoryCount(kv.Key) < kv.Value)
            .Select(kv => new VendorBuyListManager.VendorTargetRequest(kv.Key, (uint)kv.Value))
            .ToList();
        if (requests.Count == 0)
            return BeginResult.NotNeeded;

        if (manager.IsBusy)
        {
            Communicator.PrintError("[VulcanReborn] Vendor automation is busy; stopping the crafting list.");
            return BeginResult.Failed;
        }

        if (!VNavmesh.Enabled)
        {
            Communicator.PrintError("[VulcanReborn] Auto Vendor Purchase requires the vnavmesh plugin to be loaded; stopping the crafting list.");
            return BeginResult.Failed;
        }

        var tempList = manager.CreateList($"Vulcan Auto - {plan.ListName}", setActive: false);
        if (manager.TrySetTargets(tempList.Id, requests, selectList: false, openWindow: false, announce: false) == 0)
        {
            manager.DeleteList(tempList.Id);
            return BeginResult.NotNeeded;
        }

        _planName         = plan.ListName;
        _tempListId       = tempList.Id;
        _continuation     = continueToGather;
        _farmingSeals     = false;
        _buying           = false;
        _farmFailed       = false;
        _last             = Last.None;
        _rounds           = 0;
        _stagnantRounds   = 0;
        _pendingAtLastBuy = long.MaxValue;

        var result = Advance();
        if (result != BeginResult.Started)
        {
            _tempListId   = null;
            _continuation = null;
            manager.DeleteList(tempList.Id);
        }

        return result;
    }

    /// <summary>Decides and starts the next action (buy or farm) for the temporary list.</summary>
    private static BeginResult Advance()
    {
        var manager = GatherBuddy.VendorBuyListManager;
        var list    = _tempListId.HasValue
            ? manager.Lists.FirstOrDefault(l => l.Id == _tempListId.Value)
            : null;
        if (list == null)
            return BeginResult.NotNeeded;

        var pending = PendingQuantity(manager, list);
        if (pending == 0)
            return BeginResult.NotNeeded;

        if (++_rounds > MaxRounds)
        {
            GatherBuddy.Log.Warning("[CraftingVendorPreStep] Round limit reached; stopping the vendor step.");
            return BeginResult.NotNeeded;
        }

        if (_last == Last.Buy)
        {
            _stagnantRounds = pending >= _pendingAtLastBuy ? _stagnantRounds + 1 : 0;
            if (_stagnantRounds >= 2)
            {
                GatherBuddy.Log.Warning("[CraftingVendorPreStep] Purchases are not making progress; stopping the vendor step.");
                return BeginResult.NotNeeded;
            }
        }

        var farmWanted = false;
        uint currency  = 0;
        uint farmTarget = 0;
        if (!_farmFailed
         && GatherBuddy.Config.VulcanAutoDutyConfig.Enabled
         && TryGetSealShortfall(manager, list, out currency, out var need))
        {
            var cap     = GetSealCap();
            farmTarget  = Math.Min(need, cap > SealCapMargin ? cap - SealCapMargin : cap);
            farmWanted  = farmTarget > SealFarmStep.GetSeals(currency);
        }

        var canBuy = HasAffordablePending(manager, list);
        if (farmWanted && !(canBuy && _last != Last.Buy))
        {
            GatherBuddy.Log.Information($"[CraftingVendorPreStep] Farming seals: have {SealFarmStep.GetSeals(currency)}, target {farmTarget} (cap {GetSealCap()}).");
            if (SealFarmStep.TryBegin(currency, farmTarget, OnSealFarmFinished))
            {
                _farmingSeals = true;
                _last         = Last.Farm;
                return BeginResult.Started;
            }

            Communicator.PrintError("[VulcanReborn] Seal farming could not start; buying what you can afford.");
            _farmFailed = true;
            return canBuy && _last != Last.Buy ? StartBuy(manager, list.Id, pending) : BeginResult.NotNeeded;
        }

        if (canBuy && _last != Last.Buy)
            return StartBuy(manager, list.Id, pending);

        return BeginResult.NotNeeded;
    }

    private static BeginResult StartBuy(VendorBuyListManager manager, Guid listId, long pending)
    {
        VendorBuyListManager.StartResult result;
        try
        {
            result = manager.Start(listId);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[CraftingVendorPreStep] Vendor run failed to start: {e}");
            manager.Stop();
            Communicator.PrintError("[VulcanReborn] Automatic vendor purchase failed to start; stopping the crafting list.");
            return BeginResult.Failed;
        }

        switch (result)
        {
            case VendorBuyListManager.StartResult.Started:
            case VendorBuyListManager.StartResult.WaitingForPreviousInteraction:
                GatherBuddy.Log.Information($"[CraftingVendorPreStep] Started vendor purchase for '{_planName}' ({pending} items pending).");
                _buying           = true;
                _last             = Last.Buy;
                _pendingAtLastBuy = pending;
                return BeginResult.Started;
            case VendorBuyListManager.StartResult.Empty:
            case VendorBuyListManager.StartResult.NoPendingEntries:
                return BeginResult.NotNeeded;
            default:
                Communicator.PrintError($"[VulcanReborn] Could not start the automatic vendor purchase ({result}); stopping the crafting list.");
                return BeginResult.Failed;
        }
    }

    private static long PendingQuantity(VendorBuyListManager manager, VendorBuyListDefinition list)
        => list.Entries.Where(e => e.Enabled).Sum(e => (long)manager.GetRemainingQuantity(e));

    /// <summary>True when at least one pending entry can be bought with what the player holds right now.</summary>
    private static bool HasAffordablePending(VendorBuyListManager manager, VendorBuyListDefinition list)
        => list.Entries.Any(e => e.Enabled
            && manager.GetRemainingQuantity(e) > 0
            && (e.ShopType != VendorShopType.GrandCompanySeals || SealFarmStep.GetSeals(e.CurrencyItemId) >= e.Cost));

    /// <summary>Total seals the pending Grand Company entries cost, if that exceeds what the player holds.</summary>
    private static bool TryGetSealShortfall(VendorBuyListManager manager, VendorBuyListDefinition list, out uint currencyItemId, out uint target)
    {
        currencyItemId = 0;
        target         = 0;
        var perCurrency = list.Entries
            .Where(e => e.ShopType == VendorShopType.GrandCompanySeals && e.Enabled)
            .GroupBy(e => e.CurrencyItemId)
            .Select(g => (Currency: g.Key, Need: g.Sum(e => (long)manager.GetRemainingQuantity(e) * e.Cost)));

        foreach (var (currency, need) in perCurrency)
        {
            if (currency == 0 || need <= 0 || need <= SealFarmStep.GetSeals(currency))
                continue;

            currencyItemId = currency;
            target         = (uint)Math.Min(need, uint.MaxValue);
            return true;
        }

        return false;
    }

    /// <summary>The most Grand Company seals the character can hold at their current rank.</summary>
    private static unsafe uint GetSealCap()
    {
        try
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            var sheet       = Dalamud.GameData.GetExcelSheet<GrandCompanyRank>();
            if (playerState != null && sheet != null && sheet.TryGetRow(playerState->GetGrandCompanyRank(), out var rank) && rank.MaxSeals > 0)
                return rank.MaxSeals;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"[CraftingVendorPreStep] Could not read the seal cap: {e.Message}");
        }

        return DefaultSealCap;
    }

    private static void OnSealFarmFinished(bool success)
    {
        if (!_tempListId.HasValue)
            return;

        _farmingSeals = false;
        if (!success)
        {
            _farmFailed = true;
            Communicator.PrintError("[VulcanReborn] Seal farming did not reach its target; buying what you can afford.");
        }

        Resume();
    }

    /// <summary>Continues after a buy or farm step finished: start the next one or wrap up.</summary>
    private static void Resume()
    {
        var listId = _tempListId!.Value;
        switch (Advance())
        {
            case BeginResult.Started:
                return;
            case BeginResult.NotNeeded:
                Finish(listId, runContinuation: true);
                return;
            default:
                Finish(listId, runContinuation: false);
                CraftingGatherBridge.StopQueue();
                return;
        }
    }

    private static void Finish(Guid listId, bool runContinuation)
    {
        var continuation = _continuation;
        _tempListId   = null;
        _continuation = null;
        _farmingSeals = false;
        _buying       = false;
        var manager = GatherBuddy.VendorBuyListManager;
        if (!manager.DeleteList(listId))
            GatherBuddy.Log.Warning("[CraftingVendorPreStep] Could not delete the temporary vendor list.");

        if (runContinuation)
            continuation?.Invoke();
    }

    /// <summary>Aborts an in-progress vendor step (used when the crafting queue is stopped).</summary>
    public static void Cancel()
    {
        if (!_tempListId.HasValue)
            return;

        var listId = _tempListId.Value;
        _tempListId   = null;
        _continuation = null;
        _buying       = false;
        if (_farmingSeals)
        {
            _farmingSeals = false;
            SealFarmStep.Cancel();
        }

        var manager = GatherBuddy.VendorBuyListManager;
        manager.Stop();
        if (!manager.DeleteList(listId))
            GatherBuddy.Log.Debug("[CraftingVendorPreStep] Temporary vendor list will be left for manual cleanup (manager still busy).");
    }

    public static void Update()
    {
        if (!_tempListId.HasValue || _farmingSeals || !_buying)
            return;

        var manager = GatherBuddy.VendorBuyListManager;
        if (manager.IsBusy)
            return;

        _buying = false;
        if (manager.LastRunStopped)
        {
            var listId = _tempListId.Value;
            Finish(listId, runContinuation: false);
            Communicator.Print("[VulcanReborn] Vendor purchase stopped; stopping the crafting list.");
            CraftingGatherBridge.StopQueue();
            return;
        }

        Resume();
    }
}
