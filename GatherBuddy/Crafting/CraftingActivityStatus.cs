namespace GatherBuddy.Crafting;

/// <summary>What the automation steps that run alongside the crafting queue are doing, for the status window.</summary>
public static class CraftingActivityStatus
{
    /// <summary>The most specific active step's description, or null when none is running.</summary>
    public static string? Current
        => SealFarmStep.Status
        ?? CraftingMobFarmStep.Status
        ?? CraftingVendorPreStep.Status;
}
