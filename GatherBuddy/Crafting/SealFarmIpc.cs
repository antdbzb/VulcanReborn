using System;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

/// <summary>Thin wrapper around AutoDuty's IPC (internal plugin name "AutoDuty").</summary>
internal static class AutoDutyIpc
{
    private const string Prefix = "AutoDuty";

    public static bool Loaded
        => IPCSubscriber.IsReady("AutoDuty");

    public static bool Run(uint territoryType, int loops)
    {
        try
        {
            Dalamud.PluginInterface.GetIpcSubscriber<uint, int, bool, object>($"{Prefix}.Run").InvokeAction(territoryType, loops, false);
            return true;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[AutoDutyIpc] Run failed: {e.Message}");
            return false;
        }
    }

    public static void Stop()
    {
        try
        {
            Dalamud.PluginInterface.GetIpcSubscriber<object>($"{Prefix}.Stop").InvokeAction();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Debug($"[AutoDutyIpc] Stop failed: {e.Message}");
        }
    }

    public static bool IsStopped()
        => Query($"{Prefix}.IsStopped", true);

    public static bool IsNavigating()
        => Query($"{Prefix}.IsNavigating", false);

    public static bool IsLooping()
        => Query($"{Prefix}.IsLooping", false);

    public static bool ContentHasPath(uint territoryType)
    {
        try
        {
            return Dalamud.PluginInterface.GetIpcSubscriber<uint, bool>($"{Prefix}.ContentHasPath").InvokeFunc(territoryType);
        }
        catch
        {
            return false;
        }
    }

    private static bool Query(string name, bool fallback)
    {
        try
        {
            return Dalamud.PluginInterface.GetIpcSubscriber<bool>(name).InvokeFunc();
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>Thin wrapper around Deliveroo's IPC and slash command (internal plugin name "Deliveroo").</summary>
internal static class DeliverooIpc
{
    public static bool Loaded
        => IPCSubscriber.IsReady("Deliveroo");

    public static bool IsTurnInRunning()
    {
        try
        {
            return Dalamud.PluginInterface.GetIpcSubscriber<bool>("Deliveroo.IsTurnInRunning").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts a Grand Company turn-in (the same as typing "/deliveroo enable").</summary>
    public static bool StartTurnIn()
        => Dalamud.Commands.ProcessCommand("/deliveroo enable");

    public static void StopTurnIn()
        => Dalamud.Commands.ProcessCommand("/deliveroo disable");
}
