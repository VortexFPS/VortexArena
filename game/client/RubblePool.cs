using System;
using VortexArena.Common.Services;

namespace VortexArena.Game.Client;

/// <summary>Reusable slots for client rubble, ordered by spawn time across render batches.</summary>
internal sealed class RubblePool<T> where T : struct
{
    internal const int Capacity = 1024;

    private T[] _items = Array.Empty<T>();
    private ulong[] _order = Array.Empty<ulong>();
    private ulong _sequence;
    private int _count;

    public int Length => _items.Length;
    public bool IsEmpty => _count == 0;
    public ref T this[int slot] => ref _items[slot];
    public bool IsActive(int slot) => _order[slot] != 0;

    public static int ReadLimit(ICvarService? cvars, string name, int fallback)
    {
        float value = cvars?.Has(name) == true ? cvars.GetFloat(name) : fallback;
        return float.IsNaN(value) ? 0 : (int)Math.Clamp(value, 0, Capacity);
    }

    public bool TrySpawn(int limit, out int slot)
    {
        slot = -1;
        if (limit == 0)
        {
            if (_count != 0)
                Array.Clear(_order);
            _count = 0;
            return false;
        }

        // Upstream appends before trimming, so reserve one place for the incoming item.
        while (_count >= limit)
        {
            int oldest = -1;
            ulong first = ulong.MaxValue;
            for (int i = 0; i < _order.Length; i++)
            {
                ulong order = _order[i];
                if (order == 0 || order >= first)
                    continue;
                oldest = i;
                first = order;
            }
            Remove(oldest);
            slot = oldest;
        }

        if (slot < 0)
        {
            slot = Array.IndexOf(_order, 0UL);
            if (slot < 0)
            {
                slot = _items.Length;
                int capacity = Math.Min(Capacity, Math.Max(16, slot * 2));
                Array.Resize(ref _items, capacity);
                Array.Resize(ref _order, capacity);
            }
        }
        _order[slot] = ++_sequence;
        _count++;
        return true;
    }

    public void Remove(int slot)
    {
        _order[slot] = 0;
        _count--;
    }
}
