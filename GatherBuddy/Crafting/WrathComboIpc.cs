using System;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>Thin lease-based wrapper around Wrath Combo's auto-rotation IPC.</summary>
internal sealed class WrathComboIpc
{
    private const string Prefix = "WrathCombo";

    private Guid?    _lease;
    private bool     _readyRequested;
    private bool     _enableSent;
    private bool     _loggedFallback;
    private DateTime _lastCheck   = DateTime.MinValue;
    private DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>Consecutive failures to hold a lease and enable auto-rotation.</summary>
    public int Failures { get; private set; }

    public static bool Loaded
        => IPCSubscriber.IsReady("WrathCombo");

    public bool HasLease
        => _lease.HasValue;

    public static bool IsReady()
    {
        try
        {
            return Loaded && Dalamud.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.IPCReady").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool TryAcquire()
    {
        if (_lease.HasValue)
            return true;

        try
        {
            _lease = Dalamud.PluginInterface
                .GetIpcSubscriber<string, string, Guid?>($"{Prefix}.RegisterForLease")
                .InvokeFunc(GatherBuddy.InternalName, "Vulcan Reborn");
            _readyRequested = false;
            _enableSent     = false;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[WrathComboIpc] RegisterForLease failed: {e.Message}");
            _lease = null;
        }

        return _lease.HasValue;
    }

    private static bool IsAutoRotationOn()
    {
        try
        {
            return Dalamud.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.GetAutoRotationState").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Turns auto-rotation on or off under our lease. Only a failed state change counts as a dead lease.</summary>
    public bool SetAutoRotation(bool enabled)
    {
        if (!_lease.HasValue)
            return false;

        try
        {
            // SetResult: Okay = 0, OkayWorking = 1; higher values are errors (e.g. InvalidLease).
            var state = Dalamud.PluginInterface
                .GetIpcSubscriber<Guid, bool, int>($"{Prefix}.SetAutoRotationState")
                .InvokeFunc(_lease.Value, enabled);
            if (state > 1)
            {
                GatherBuddy.Log.Information($"[WrathComboIpc] SetAutoRotationState({enabled}) -> {state}; releasing lease.");
                Release();
                return false;
            }

            if (enabled)
                GatherBuddy.Log.Information($"[WrathComboIpc] SetAutoRotationState(True) -> {state}");

            if (enabled && !_readyRequested)
            {
                // Best effort, once per lease, and only when the job isn't already ready (it usually is): a second
                // lease-controlled setting appears to trip a duplicate-key bug in Wrath's own PvE tab.
                _readyRequested = true;
                if (IsJobReady())
                    return true;

                var ready = Dalamud.PluginInterface
                    .GetIpcSubscriber<Guid, int>($"{Prefix}.SetCurrentJobAutoRotationReady")
                    .InvokeFunc(_lease.Value);
                GatherBuddy.Log.Information($"[WrathComboIpc] SetCurrentJobAutoRotationReady -> {ready}");
            }

            return true;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[WrathComboIpc] SetAutoRotation failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Keeps auto-rotation enabled: registers a lease if we have none (or Wrath dropped it), and re-enables
    /// rotation only when Wrath reports it as off. Throttled; returns true while it is on under a valid lease.
    /// </summary>
    public bool Maintain()
    {
        var now = DateTime.UtcNow;
        if (!_lease.HasValue)
        {
            if (now - _lastAttempt < TimeSpan.FromSeconds(2))
                return false;

            _lastAttempt = now;
            if (!TryAcquire())
            {
                // Wrath may refuse leases (e.g. it blacklisted us). If the user already has auto-rotation switched on,
                // that still works as a global toggle, so don't treat the refusal as fatal.
                if (IsAutoRotationOn())
                {
                    if (!_loggedFallback)
                    {
                        _loggedFallback = true;
                        GatherBuddy.Log.Warning("[WrathComboIpc] No lease available (blacklisted or refused); relying on Wrath's own auto-rotation setting.");
                    }

                    Failures = 0;
                    return true;
                }

                Failures++;
                return false;
            }

            _lastCheck = DateTime.MinValue;
        }

        if (now - _lastCheck < TimeSpan.FromSeconds(1.5))
            return true;

        _lastCheck = now;
        // Wrath's global auto-rotation switch may already be on from the user's own settings, which says nothing about
        // our lease: always send the enable once per lease, and only use the state check for upkeep after that.
        if (_enableSent && IsAutoRotationOn())
        {
            Failures = 0;
            return true;
        }

        if (SetAutoRotation(true))
        {
            _enableSent = true;
            Failures     = 0;
            return true;
        }

        Failures++;
        return false;
    }

    /// <summary>One-line snapshot of what Wrath reports, for troubleshooting.</summary>
    public string Describe()
    {
        try
        {
            var on    = Dalamud.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.GetAutoRotationState").InvokeFunc();
            var ready = Dalamud.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.IsCurrentJobAutoRotationReady").InvokeFunc();
            return $"autoRotation={on}, jobReady={ready}, lease={_lease.HasValue}";
        }
        catch (Exception e)
        {
            return $"query failed: {e.Message}";
        }
    }

    /// <summary>Asks Wrath to ready the current job again (e.g. when it reports the job as not ready).</summary>
    public void RequestJobReady()
    {
        if (!_lease.HasValue)
            return;

        try
        {
            var ready = Dalamud.PluginInterface
                .GetIpcSubscriber<Guid, int>($"{Prefix}.SetCurrentJobAutoRotationReady")
                .InvokeFunc(_lease.Value);
            GatherBuddy.Log.Information($"[WrathComboIpc] SetCurrentJobAutoRotationReady (retry) -> {ready}");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[WrathComboIpc] RequestJobReady failed: {e.Message}");
        }
    }

    public bool IsJobReady()
    {
        try
        {
            return Dalamud.PluginInterface.GetIpcSubscriber<bool>($"{Prefix}.IsCurrentJobAutoRotationReady").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public void Release()
    {
        if (!_lease.HasValue)
            return;

        var lease = _lease.Value;
        _lease      = null;
        _enableSent = false;
        try
        {
            Dalamud.PluginInterface.GetIpcSubscriber<Guid, object>($"{Prefix}.ReleaseControl").InvokeAction(lease);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"[WrathComboIpc] ReleaseControl failed: {e.Message}");
        }
    }
}
