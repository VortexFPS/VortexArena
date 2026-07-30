// The resource mutator-hook surface — the C# successor to the MUTATOR_CALLHOOK points in
// qcsrc/server/sv_resources.qc (GetResourceLimit, SetResource, GiveResource, ResourceAmountChanged,
// ResourceWasted). QC fires these so mutators can override per-resource caps, forbid changes, or react to
// gains/waste. The port keeps the resource math in Resources.cs and exposes these typed hook chains here
// (kept in the Items folder so the resource subsystem owns them); mutators subscribe via Hook()/Unhook().

using XonoticGodot.Common.Framework;

namespace XonoticGodot.Common.Gameplay;

/// <summary>
/// The resource hook chains (QC sv_resources.qc MUTATOR_CALLHOOK points). Empty by default — a handler is
/// added only by an active mutator. <see cref="Resources"/> calls them on the set/give/limit paths.
/// </summary>
public static class ResourceHooks
{
    /// <summary>QC GetResourceLimit hook: a mutator may override a resource's cap. Set <see cref="GetResourceLimitArgs.Limit"/>.</summary>
    public struct GetResourceLimitArgs { public Entity Entity; public ResourceType Resource; public float Limit; }
    public static readonly HookChain<GetResourceLimitArgs> GetResourceLimit = new();

    /// <summary>QC SetResource hook: a mutator may forbid (return true), rewrite the amount, or rewrite the resource type (M_ARGV(8) entity read-back in sv_resources.qc:94).</summary>
    public struct SetResourceArgs { public Entity Entity; public ResourceType Resource; public float Amount; public bool Forbid; }
    public static readonly HookChain<SetResourceArgs> SetResource = new();

    /// <summary>QC GiveResource hook: a mutator may forbid (return true), rewrite the amount, or rewrite the resource type (resistance/vampire buffs).</summary>
    public struct GiveResourceArgs { public Entity Receiver; public ResourceType Resource; public float Amount; public bool Forbid; }
    public static readonly HookChain<GiveResourceArgs> GiveResource = new();

    /// <summary>QC GiveResourceWithLimit hook (sv_resources.qc:165): a mutator may forbid or rewrite the amount/limit before the trim.</summary>
    public struct GiveResourceWithLimitArgs { public Entity Receiver; public ResourceType Resource; public float Amount; public float Limit; public bool Forbid; }
    public static readonly HookChain<GiveResourceWithLimitArgs> GiveResourceWithLimit = new();

    /// <summary>QC TakeResource hook (sv_resources.qc:191): a mutator may forbid or rewrite the resource drain.</summary>
    public struct TakeResourceArgs { public Entity Receiver; public ResourceType Resource; public float Amount; public bool Forbid; }
    public static readonly HookChain<TakeResourceArgs> TakeResource = new();

    /// <summary>QC TakeResourceWithLimit hook (sv_resources.qc:211): a mutator may forbid or rewrite the amount/limit before the clamp.</summary>
    public struct TakeResourceWithLimitArgs { public Entity Receiver; public ResourceType Resource; public float Amount; public float Limit; public bool Forbid; }
    public static readonly HookChain<TakeResourceWithLimitArgs> TakeResourceWithLimit = new();

    /// <summary>QC ResourceAmountChanged hook: fired after a resource value actually changes (HUD/score reactions).</summary>
    public struct ResourceChangedArgs { public Entity Entity; public ResourceType Resource; public float Amount; }
    public static readonly HookChain<ResourceChangedArgs> ResourceAmountChanged = new();

    /// <summary>QC ResourceWasted hook: fired when a give exceeds the cap and the excess is dropped.</summary>
    public struct ResourceWastedArgs { public Entity Entity; public ResourceType Resource; public float Wasted; }
    public static readonly HookChain<ResourceWastedArgs> ResourceWasted = new();

    // --- thin call helpers used by Resources.cs (mirroring MUTATOR_CALLHOOK + M_ARGV read-back) ---

    /// <summary>Run the GetResourceLimit hook, returning the (possibly overridden) limit.</summary>
    internal static float CallGetResourceLimit(Entity e, ResourceType res, float limit)
    {
        if (GetResourceLimit.Count == 0) return limit;
        var args = new GetResourceLimitArgs { Entity = e, Resource = res, Limit = limit };
        GetResourceLimit.Call(ref args);
        return args.Limit;
    }

    /// <summary>
    /// Run the SetResource hook; returns true if a mutator forbids the change.
    /// Both <paramref name="amount"/> and <paramref name="res"/> are written back — Base sv_resources.qc:94 reads
    /// both M_ARGV(9,float) and M_ARGV(8,entity) after the hook so a mutator may rewrite the resource TYPE as
    /// well as the amount.
    /// </summary>
    internal static bool CallSetResource(Entity e, ref ResourceType res, ref float amount)
    {
        if (SetResource.Count == 0) return false;
        var args = new SetResourceArgs { Entity = e, Resource = res, Amount = amount, Forbid = false };
        SetResource.Call(ref args);
        res    = args.Resource;
        amount = args.Amount;
        return args.Forbid;
    }

    /// <summary>
    /// Run the GiveResource hook (QC sv_resources.qc:121). Returns true if a mutator forbids the give;
    /// otherwise writes back the (maybe rewritten) resource type and amount — Base reads back both
    /// M_ARGV(8,entity) and M_ARGV(9,float) after the forbid check (sv_resources.qc:126-127).
    /// </summary>
    internal static bool CallGiveResource(Entity e, ref ResourceType res, ref float amount)
    {
        if (GiveResource.Count == 0) return false;
        var args = new GiveResourceArgs { Receiver = e, Resource = res, Amount = amount, Forbid = false };
        GiveResource.Call(ref args);
        res    = args.Resource;
        amount = args.Amount;
        return args.Forbid;
    }

    /// <summary>
    /// Run the GiveResourceWithLimit hook (QC sv_resources.qc:165). Returns true if forbidden; updates amount and
    /// limit via out parameters so the caller can apply them after the hook.
    /// </summary>
    internal static bool CallGiveResourceWithLimit(Entity e, ref ResourceType res, ref float amount, ref float limit)
    {
        if (GiveResourceWithLimit.Count == 0) return false;
        var args = new GiveResourceWithLimitArgs { Receiver = e, Resource = res, Amount = amount, Limit = limit, Forbid = false };
        GiveResourceWithLimit.Call(ref args);
        res    = args.Resource;
        amount = args.Amount;
        limit  = args.Limit;
        return args.Forbid;
    }

    /// <summary>
    /// Run the TakeResource hook (QC sv_resources.qc:191); returns true if forbidden, otherwise writes back
    /// the (maybe rewritten) resource type and amount (Base reads back M_ARGV(8,entity)+M_ARGV(9,float)).
    /// </summary>
    internal static bool CallTakeResource(Entity e, ref ResourceType res, ref float amount)
    {
        if (TakeResource.Count == 0) return false;
        var args = new TakeResourceArgs { Receiver = e, Resource = res, Amount = amount, Forbid = false };
        TakeResource.Call(ref args);
        res    = args.Resource;
        amount = args.Amount;
        return args.Forbid;
    }

    /// <summary>
    /// Run the TakeResourceWithLimit hook (QC sv_resources.qc:211). Returns true if forbidden; updates amount and
    /// limit via out parameters so the caller can apply them after the hook (mirrors CallGiveResourceWithLimit).
    /// </summary>
    internal static bool CallTakeResourceWithLimit(Entity e, ref ResourceType res, ref float amount, ref float limit)
    {
        if (TakeResourceWithLimit.Count == 0) return false;
        var args = new TakeResourceWithLimitArgs { Receiver = e, Resource = res, Amount = amount, Limit = limit, Forbid = false };
        TakeResourceWithLimit.Call(ref args);
        res    = args.Resource;
        amount = args.Amount;
        limit  = args.Limit;
        return args.Forbid;
    }

    internal static void CallResourceAmountChanged(Entity e, ResourceType res, float amount)
    {
        if (ResourceAmountChanged.Count == 0) return;
        var args = new ResourceChangedArgs { Entity = e, Resource = res, Amount = amount };
        ResourceAmountChanged.Call(ref args);
    }

    internal static void CallResourceWasted(Entity e, ResourceType res, float wasted)
    {
        if (wasted <= 0f || ResourceWasted.Count == 0) return;
        var args = new ResourceWastedArgs { Entity = e, Resource = res, Wasted = wasted };
        ResourceWasted.Call(ref args);
    }
}
