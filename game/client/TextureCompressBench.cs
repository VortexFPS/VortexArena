using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using VortexArena.Common.Config;

namespace VortexArena.Game.Client;

/// <summary>
/// <c>texcompress_bench</c> — measure what this port's texture block encoder actually costs and what it
/// actually produces, on real game textures, in this process (C10).
///
/// <para><b>Why it exists.</b> BC7 dominates a cold load here — ~110 s of a 119 s cold stormkeep at
/// <c>gl_texturecompression 2</c> — and the obvious reaction is "get a faster encoder". That reaction needs a
/// number to argue with, because none of the published comparisons provide one: the fast encoders (bc7e, bc7f,
/// ispc_texcomp) are benchmarked against each other, never against CVTT as Godot configures it, and the one
/// open-source GPU port measured slower than CPU on Vulkan. This is the missing measurement.</para>
///
/// <para><b>What it found (2026-08-06, RTX 3080 box, 24 logical cores).</b> Godot's encoder is not the
/// problem. Over 50.33 Mpixel of real textures: <b>5,484 ms wall, 114.5 CPU-seconds, 53.71 dB PSNR</b> — about
/// 9.2 Mpixel/s and 2.27 CPU-seconds per megapixel. For comparison, BCnEncoder.NET (a managed BC1-7 encoder
/// with real quality tiers, benchmarked here and then removed) needed <b>74,841 ms and 1,618 CPU-seconds at
/// its FASTEST setting for 47.90 dB</b> — 13.6x the time and 5.8 dB worse. Its best-quality mode was 60x
/// slower and still worse than CVTT.</para>
///
/// <para>So a cold BC7 load is slow because of the VOLUME — 287 textures, ~1.5 Mpixel each — not because the
/// encoder is bad. The levers worth pulling are the ones that stop the work happening on a player's machine
/// at all (ship the cache, encode in the background), not an encoder swap. Re-run this before believing any
/// future claim that some other encoder would help.</para>
///
/// <para><b>Nothing here runs during play.</b> It is a console command and a measurement tool.</para>
/// </summary>
public static class TextureCompressBench
{
    /// <summary>The match's asset system; set by <c>NetGame</c>. Falls back to the menu's shared loader.</summary>
    public static Loaders.AssetSystem? Assets { get; set; }

    public static void Register(ConfigInterpreter interp, Action<string> print)
    {
        interp.RegisterCommand("texcompress_bench", args => Run(args, print),
            "Measure the BC7 encoder on N real textures (default 12): texcompress_bench [count] [mode]");
    }

    private static void Run(IReadOnlyList<string> args, Action<string> print)
    {
        // The match's loader when there is one, else the menu's process-lifetime shared loader — so this can
        // be run from a `+texcompress_bench` boot line as well as from the console mid-match.
        Loaders.AssetSystem? assets = Assets ?? Menu.MenuState.SharedAssets?.Assets;
        if (assets is null)
        {
            print("texcompress_bench: no asset system yet (try again once a map is loaded).");
            return;
        }

        int want = 12;
        if (args.Count > 1 && int.TryParse(args[1], out int n) && n > 0)
            want = n;
        // Optional second argument: which encoder, matching gl_texturecompression (1 = S3TC, 2 = BC7).
        Image.CompressMode mode = Image.CompressMode.Bptc;
        string modeName = "BC7/BPTC (CVTT)";
        if (args.Count > 2 && args[2] == "1")
        {
            mode = Image.CompressMode.S3Tc;
            modeName = "S3TC/DXT (etcpak)";
        }

        // Real game textures, biggest-first: a sample full of 64x64 icons would measure per-call overhead
        // rather than encoding.
        List<(string Path, Image Img)> sample = CollectSample(assets, want, print);
        if (sample.Count == 0)
        {
            print("texcompress_bench: found no uncompressed textures to test.");
            return;
        }

        long pixels = 0;
        foreach ((_, Image img) in sample)
            pixels += (long)img.GetWidth() * img.GetHeight();
        double mpx = pixels / 1_000_000.0;
        print($"texcompress_bench: {sample.Count} textures, {mpx:0.00} Mpixel (level 0 only), "
            + $"{modeName}, {System.Environment.ProcessorCount} logical cores");

        var copies = new List<Image>(sample.Count);
        foreach ((_, Image img) in sample)
            copies.Add(Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8, img.GetData()));

        TimeSpan cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        long t0 = Stopwatch.GetTimestamp();
        long bytes = 0;
        int failed = 0;
        foreach (Image c in copies)
        {
            if (c.Compress(mode, Image.CompressSource.Generic) != Error.Ok) { failed++; continue; }
            bytes += c.GetData().Length;
        }
        double wall = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;

        print($"  {wall:0} ms wall, {cpu:0.0} CPU-seconds, {bytes / 1048576.0:0.0} MB out, "
            + $"{Psnr(sample, copies):0.00} dB PSNR"
            + (failed > 0 ? $", {failed} FAILED" : ""));
        print($"  = {mpx / (wall / 1000.0):0.0} Mpixel/s wall, {cpu / Math.Max(mpx, 0.001):0.00} CPU-seconds per Mpixel, "
            + $"{cpu / Math.Max(wall / 1000.0, 0.001):0.0} cores busy");

        foreach (Image c in copies)
            c.Dispose();
        foreach ((_, Image img) in sample)
            img.Dispose();
    }

    /// <summary>
    /// The N largest textures the mounted content resolves to, decoded and normalised to RGBA8 with no
    /// mipmaps. Pre-compressed sources are skipped — they cannot be re-encoded meaningfully, and they are
    /// exactly the ones a warm cache skips anyway.
    /// </summary>
    private static List<(string, Image)> CollectSample(Loaders.AssetSystem assets, int want, Action<string> print)
    {
        var found = new List<(string Path, Image Img, long Px)>();

        void TryAdd(string vpath)
        {
            if (found.Count > 4000)
                return;
            try
            {
                byte[] bytes = assets.Vfs.ReadBytes(vpath);
                Image? img = vpath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                    ? Loaders.TgaDecoder.Decode(bytes)
                    : LoadPng(bytes);
                if (img is null || img.IsEmpty() || img.IsCompressed())
                    return;
                if (img.GetWidth() < 64 || img.GetHeight() < 64)
                    return;
                found.Add((vpath, img, (long)img.GetWidth() * img.GetHeight()));
            }
            catch { /* a texture that will not decode is not this tool's problem */ }
        }

        foreach (string vpath in assets.Vfs.Find("textures/", "tga")) TryAdd(vpath);
        foreach (string vpath in assets.Vfs.Find("textures/", "png")) TryAdd(vpath);
        foreach (string vpath in assets.Vfs.Find("models/", "tga")) TryAdd(vpath);
        foreach (string vpath in assets.Vfs.Find("models/", "png")) TryAdd(vpath);

        found.Sort((a, b) => b.Px.CompareTo(a.Px));
        var outp = new List<(string, Image)>();
        for (int i = 0; i < found.Count; i++)
        {
            if (i < want)
            {
                Image img = found[i].Img;
                if (img.GetFormat() != Image.Format.Rgba8)
                    img.Convert(Image.Format.Rgba8);
                outp.Add((found[i].Path, img));
            }
            else
            {
                found[i].Img.Dispose();
            }
        }
        if (outp.Count < want)
            print($"texcompress_bench: only {outp.Count} suitable textures found (asked for {want}).");
        return outp;
    }

    private static Image? LoadPng(byte[] bytes)
    {
        var img = new Image();
        return img.LoadPngFromBuffer(bytes) == Error.Ok ? img : null;
    }

    /// <summary>
    /// Mean PSNR of the compressed images against the originals, in dB — the standard quality measure for a
    /// lossy block codec, and the half of the comparison that stops "faster" from being reported as "better".
    /// RGB only: alpha handling differs between formats and would muddy a colour comparison.
    /// </summary>
    private static double Psnr(List<(string Path, Image Img)> originals, List<Image> compressed)
    {
        double total = 0;
        int counted = 0;
        for (int i = 0; i < originals.Count && i < compressed.Count; i++)
        {
            Image a = originals[i].Img;
            using var b = Image.CreateFromData(compressed[i].GetWidth(), compressed[i].GetHeight(),
                false, compressed[i].GetFormat(), compressed[i].GetData());
            if (b.IsCompressed() && b.Decompress() != Error.Ok)
                continue;
            if (b.GetFormat() != Image.Format.Rgba8)
                b.Convert(Image.Format.Rgba8);

            byte[] pa = a.GetData(), pb = b.GetData();
            int len = Math.Min(pa.Length, pb.Length);
            if (len == 0)
                continue;
            double sse = 0;
            for (int p = 0; p + 3 < len; p += 4)
                for (int ch = 0; ch < 3; ch++)
                {
                    double d = pa[p + ch] - pb[p + ch];
                    sse += d * d;
                }
            double mse = sse / (len / 4.0 * 3.0);
            total += mse <= 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 / mse);
            counted++;
        }
        return counted == 0 ? 0 : total / counted;
    }
}
