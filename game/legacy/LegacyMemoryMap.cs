// Developer aid for the review scripts' "mem" line: where the process's committed memory is, read from the
// operating system rather than from any one allocator's own counter. The managed heap, Godot's static memory
// and the renderer's texture counter each account for their own; what is left over (the C heap's free lists,
// a graphics driver's own copies, staging buffers) shows up only here. Windows only; elsewhere it says nothing.
// Only under VORTEX_LEGACY_MEMMAP: walking the heaps takes tens of milliseconds.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VortexArena.Game.Legacy;

internal static class LegacyMemoryMap
{
    public static readonly bool Enabled = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VORTEX_LEGACY_MEMMAP"));

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapSummaryData
    {
        public uint Size;
        public nuint Allocated, Committed, Reserved, MaxReserve;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint address, out MemoryBasicInformation info, nint length);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessHeaps(uint count, [Out] nint[] heaps);

    [DllImport("kernel32.dll")]
    private static extern bool HeapSummary(nint heap, uint flags, ref HeapSummaryData summary);

    private const uint MemCommit = 0x1000, MemImage = 0x1000000, MemMapped = 0x40000, MemPrivate = 0x20000;

    /// <summary>A few lines: committed bytes by kind, private allocations by size class, each heap, the largest allocations.</summary>
    public static string Report()
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return "";
        const double mb = 1024.0 * 1024.0;
        try
        {
            long image = 0, mapped = 0, priv = 0;
            Dictionary<nint, long> allocations = new();
            Dictionary<nint, uint> protections = new();
            Dictionary<nint, long> reserved = new();
            Dictionary<nint, nint> firstCommitted = new();
            nint address = 0;
            int size = Marshal.SizeOf<MemoryBasicInformation>();
            while (VirtualQuery(address, out MemoryBasicInformation info, size) != 0)
            {
                long region = info.RegionSize;
                if (region <= 0) break;
                if (info.State != 0x10000 && info.Type == MemPrivate)
                {
                    reserved.TryGetValue(info.AllocationBase, out long all);
                    reserved[info.AllocationBase] = all + region;
                    if (info.State == MemCommit && (info.Protect & 0x104) == 0x04 && !firstCommitted.ContainsKey(info.AllocationBase)) firstCommitted[info.AllocationBase] = info.BaseAddress;
                }
                if (info.State == MemCommit)
                {
                    if (info.Type == MemImage) image += region;
                    else if (info.Type == MemMapped) mapped += region;
                    else if (info.Type == MemPrivate)
                    {
                        priv += region;
                        allocations.TryGetValue(info.AllocationBase, out long sum);
                        allocations[info.AllocationBase] = sum + region;
                        protections.TryGetValue(info.AllocationBase, out uint flags);
                        protections[info.AllocationBase] = flags | info.Protect;
                    }
                }
                long next = info.BaseAddress + region;
                if (next <= address) break;
                address = (nint)next;
            }
            // An allocation with write-combined pages is a mapping of video memory (the graphics driver's): it is
            // counted in the process's private bytes and not in its working set. Everything else is system memory.
            long[] bucketBytes = new long[6];
            int[] bucketCount = new int[6];
            string[] bucketName = { "<1MB", "1-4MB", "4-16MB", "16-64MB", "64-256MB", ">=256MB" };
            List<long> sizes = new(), video = new();
            foreach (KeyValuePair<nint, long> allocation in allocations)
                ((protections[allocation.Key] & 0x400) != 0 ? video : sizes).Add(allocation.Value);
            sizes.Sort((a, b) => b.CompareTo(a));
            video.Sort((a, b) => b.CompareTo(a));
            long system = 0, videoBytes = 0;
            foreach (long bytes in video) videoBytes += bytes;
            foreach (long bytes in sizes)
            {
                int bucket = bytes < 1 << 20 ? 0 : bytes < 4 << 20 ? 1 : bytes < 16 << 20 ? 2 : bytes < 64 << 20 ? 3 : bytes < 256 << 20 ? 4 : 5;
                bucketBytes[bucket] += bytes;
                bucketCount[bucket]++;
                system += bytes;
            }
            StringBuilder text = new();
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            text.Append(CultureInfo.InvariantCulture, $"memmap: gc heap {gc.HeapSizeBytes / mb:0} MB fragmented {gc.FragmentedBytes / mb:0} MB committed {gc.TotalCommittedBytes / mb:0} MB, generations (size/fragmentation MB):");
            foreach (GCGenerationInfo generation in gc.GenerationInfo) text.Append(CultureInfo.InvariantCulture, $" {generation.SizeAfterBytes / mb:0}/{generation.FragmentationAfterBytes / mb:0}");
            text.Append(CultureInfo.InvariantCulture, $"; committed image {image / mb:0} MB, mapped {mapped / mb:0} MB, private {priv / mb:0} MB = video mappings {videoBytes / mb:0} MB x{video.Count} (largest:");
            for (int i = 0; i < Math.Min(5, video.Count); i++) text.Append(CultureInfo.InvariantCulture, $" {video[i] / mb:0}");
            text.Append(CultureInfo.InvariantCulture, $") + system {system / mb:0} MB x{sizes.Count}:");
            for (int i = 0; i < 6; i++) text.Append(CultureInfo.InvariantCulture, $" {bucketName[i]} {bucketBytes[i] / mb:0} MB x{bucketCount[i]}");
            text.Append("; largest (MB):");
            for (int i = 0; i < Math.Min(14, sizes.Count); i++) text.Append(CultureInfo.InvariantCulture, $" {sizes[i] / mb:0}");

            // The three largest system allocations: reserved size and the first bytes, to tell whose they are.
            List<KeyValuePair<nint, long>> biggest = new();
            foreach (KeyValuePair<nint, long> allocation in allocations)
                if ((protections[allocation.Key] & 0x400) == 0) biggest.Add(allocation);
            biggest.Sort((a, b) => b.Value.CompareTo(a.Value));
            text.Append("; biggest:");
            for (int i = 0; i < Math.Min(3, biggest.Count); i++)
            {
                nint at = biggest[i].Key;
                text.Append(CultureInfo.InvariantCulture, $" [{biggest[i].Value / mb:0} MB of {reserved.GetValueOrDefault(at) / mb:0} reserved at 0x{(long)at:X} \"");
                if (firstCommitted.TryGetValue(at, out nint first))
                {
                    byte[] sample = new byte[48];
                    try { Marshal.Copy(first, sample, 0, sample.Length); } catch (AccessViolationException) { }
                    foreach (byte b in sample) text.Append(b is >= 32 and < 127 ? (char)b : '.');
                }
                text.Append("\"]");
            }

            nint[] heaps = new nint[256];
            uint count = GetProcessHeaps((uint)heaps.Length, heaps);
            long heapAllocated = 0, heapCommitted = 0;
            List<(long Committed, long Allocated)> each = new();
            for (int i = 0; i < Math.Min(count, (uint)heaps.Length); i++)
            {
                HeapSummaryData summary = new() { Size = (uint)Marshal.SizeOf<HeapSummaryData>() };
                if (!HeapSummary(heaps[i], 0, ref summary)) continue;
                heapAllocated += (long)summary.Allocated;
                heapCommitted += (long)summary.Committed;
                each.Add(((long)summary.Committed, (long)summary.Allocated));
            }
            each.Sort((a, b) => b.Committed.CompareTo(a.Committed));
            text.Append(CultureInfo.InvariantCulture, $"; heaps {count}: allocated {heapAllocated / mb:0} MB, committed {heapCommitted / mb:0} MB; largest heaps (committed/allocated MB):");
            for (int i = 0; i < Math.Min(4, each.Count); i++) text.Append(CultureInfo.InvariantCulture, $" {each[i].Committed / mb:0}/{each[i].Allocated / mb:0}");
            return text.ToString();
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            return "memmap: not available (" + e.GetType().Name + ")";
        }
    }
}
