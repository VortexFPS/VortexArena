using System;
using System.Collections.Generic;
using System.Linq;
using VortexArena.Net;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// Every flag in the networked entity/field masks must own a distinct bit.
///
/// This exists because the failure is invisible everywhere else. Two enum members sharing a bit is legal
/// C#, compiles without a warning, and produces no wrong value locally — the corruption only appears on
/// the wire, between a server and a remote client, where one field's update silently sets the other's.
/// Nothing in the rest of the suite would notice.
///
/// It is also not hypothetical. Merging <c>feature/playermodel-lean</c> onto main in 2026-07 produced
/// <c>Lean</c> and <c>ColormapOverride</c> both at <c>1 &lt;&lt; 26</c>: main had added
/// <c>ColormapOverride</c> at bit 26 while the branch's <c>Lean</c> sat there, having ALREADY been moved
/// once when main's <c>Colors</c> took bit 25. A long-lived branch touching a shared bitfield collides
/// every time the field grows on both sides, and the merge is exactly where nobody is looking at bit
/// numbers.
/// </summary>
public class NetFieldBitTests
{
    private readonly ITestOutputHelper _out;
    public NetFieldBitTests(ITestOutputHelper o) => _out = o;

    public static TheoryData<Type> MaskEnums() => new() { typeof(EntityField), typeof(NetEntityFlags) };

    [Theory]
    [MemberData(nameof(MaskEnums))]
    public void Every_Flag_Owns_A_Distinct_Bit(Type maskEnum)
    {
        // Names paired with their values, single-bit members only. A composite/alias member (a mask of
        // several bits, or an explicit 0 "None") is legitimate and must not be mistaken for a collision.
        var singleBit = Enum.GetNames(maskEnum)
            .Select(n => (Name: n, Value: Convert.ToUInt64(Enum.Parse(maskEnum, n))))
            .Where(x => x.Value != 0 && (x.Value & (x.Value - 1)) == 0)
            .ToList();

        Assert.True(singleBit.Count > 0,
            $"{maskEnum.Name} exposed no single-bit members — the reflection here stopped matching the "
            + "enum's shape, so this test verified nothing.");

        var collisions = singleBit
            .GroupBy(x => x.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"bit {BitOperations_Log2(g.Key)}: {string.Join(" + ", g.Select(x => x.Name))}")
            .ToList();

        Assert.True(collisions.Count == 0,
            $"{maskEnum.Name} has flags sharing a bit:\n  {string.Join("\n  ", collisions)}\n"
            + "Two fields on one wire bit corrupt each other on remote clients only — it compiles, it runs "
            + "locally, and no other test sees it. Move the newer flag to the next free bit.");

        ulong highest = singleBit.Max(x => x.Value);
        _out.WriteLine($"{maskEnum.Name}: {singleBit.Count} flags, highest bit {BitOperations_Log2(highest)}");

        // The mask is serialised as its underlying type; running off the end would silently drop the flag.
        int width = System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(maskEnum)) * 8;
        Assert.True(BitOperations_Log2(highest) < width,
            $"{maskEnum.Name}'s highest bit ({BitOperations_Log2(highest)}) does not fit its "
            + $"{width}-bit underlying type — widen the enum before adding another flag.");
    }

    private static int BitOperations_Log2(ulong v)
    {
        int n = 0;
        while ((v >>= 1) != 0) n++;
        return n;
    }
}
