namespace XonoticGodot.Common.Framework;

/// <summary>Ordering bucket for hook handlers (QC CBC_ORDER_FIRST/ANY/LAST).</summary>
public enum HookOrder
{
    First = 0,
    Any = 1,
    Last = 2,
}

/// <summary>
/// A handler in a <see cref="HookChain{TArgs}"/>. Args are passed by <c>ref</c> so handlers can read
/// inputs and write outputs — replacing QuakeC's global MUTATOR_ARGV slots with real in/out params
/// (planning/decisions/ADR-0003, specs/entity-model.md). Returns true to signal "handled".
/// </summary>
public delegate bool HookHandler<TArgs>(ref TArgs args) where TArgs : struct;

/// <summary>
/// A typed, ordered callback chain — the C# successor to QuakeC's MUTATOR_HOOKABLE/CALLHOOK bus.
/// Mutators subscribe on enable and unsubscribe on disable.
/// </summary>
public sealed class HookChain<TArgs> where TArgs : struct
{
    private readonly List<(HookOrder order, HookHandler<TArgs> cb)> _handlers = new();

    public void Add(HookHandler<TArgs> cb, HookOrder order = HookOrder.Any)
    {
        _handlers.Add((order, cb));
        // stable sort keeps registration order within a bucket
        _handlers.Sort(static (a, b) => a.order.CompareTo(b.order));
    }

    public void Remove(HookHandler<TArgs> cb) => _handlers.RemoveAll(h => h.cb == cb);

    /// <summary>Drop every handler. Used to reset global hook state between match re-inits / isolated tests.</summary>
    public void Clear() => _handlers.Clear();

    /// <summary>Run every handler; returns true if any returned true. Earlier handlers' writes are visible to later ones.</summary>
    public bool Call(ref TArgs args)
    {
        bool handled = false;
        // index loop avoids allocating an enumerator on the hot path
        var list = _handlers;
        for (int i = 0; i < list.Count; i++)
            handled |= list[i].cb(ref args);
        return handled;
    }

    public int Count => _handlers.Count;
}
