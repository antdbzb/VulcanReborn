using System;

namespace GatherBuddy.Crafting;

/// <summary>Short randomized pauses before discrete actions (job swaps, teleports, mounting...) so automation looks less instant.</summary>
public static class HumanDelay
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Call every tick before performing an action. The first call starts a random delay and returns false;
    /// once it has elapsed the call returns true exactly once and resets <paramref name="readyAt"/> for next time.
    /// </summary>
    public static bool Passed(ref DateTime? readyAt)
    {
        var config = GatherBuddy.Config.VulcanHumanizeConfig;
        if (!config.Enabled)
        {
            readyAt = null;
            return true;
        }

        var now = DateTime.UtcNow;
        if (readyAt == null)
        {
            var min = Math.Max(0f, config.MinSeconds);
            var max = Math.Max(min, config.MaxSeconds);
            readyAt = now.AddSeconds(min + Rng.NextDouble() * (max - min));
            return false;
        }

        if (now < readyAt.Value)
            return false;

        readyAt = null;
        return true;
    }
}
