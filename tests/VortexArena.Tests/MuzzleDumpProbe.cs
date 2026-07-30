using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Md3;
using Xunit;
using Xunit.Abstractions;

namespace XonoticGodot.Tests;

/// <summary>
/// DUMP / DIAGNOSTIC PROBE (task B): quantifies the "first-person shot fires LOW" hypothesis by loading the REAL
/// shipped weapon models and printing, per weapon, (i) the port's CURRENT single-bone extraction (the live
/// <c>NetGame.PrecacheWeaponModels</c> + <c>AssetLoader.LoadMuzzleOffset(v, h)</c> + <c>MuzzleTag</c> path), vs
/// (ii) the would-be base-faithful muzzle = (h_ rig weapon-attach REST matrix) applied to (v_ model shot-tag
/// LOCAL pos), and (iii) the delta. No-ops when the asset checkout is absent. Writes to a temp file + test output.
///
/// FINDING (2026-06-08): the "missing weapon-attach-transform" hypothesis is DISPROVEN by the real model data.
/// The v_ visual models carry NO tags at all (v_rl.md3 / v_arc.md3 header num_tags == 0; v_crylink.md3 is actually
/// a 1-bone INTERQUAKEMODEL despite the .md3 extension, with no shot tag). So in QC CL_WeaponEntity_SetModel the
/// `v_shot_idx` is 0 (all.qc:369) and `movedir` is taken from the ELSE branch (all.qc:413-417) = the h_ RIG's own
/// `tag_shot` bone — gettaginfo(this=h_rig, "shot") — which is the bone's full model-space rest position. That is
/// EXACTLY what the port already computes (MuzzleTag.Extract on the h_ rig). There is no v_-shot-through-attach
/// composition to add, because the v_ models have no shot tag. (ii) cannot even be computed (v_shotLocal == none).
/// Hence port-current (i) == QC movedir for every weapon; the shot-origin code is already faithful here.
///
/// Run: dotnet test tests/XonoticGodot.Tests --filter MuzzleDumpProbe -l "console;verbosity=detailed"
/// </summary>
[Collection("GlobalState")]
public class MuzzleDumpProbe
{
    private const string Weapons = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir\models\weapons";
    private const string ReportPath = @"C:\Users\Bryan\AppData\Local\Temp\muzzledump.txt";

    // The attach socket the v_ model rides on, in QC resolution order (setattachment ... "weapon").
    private static readonly string[] AttachBoneNames = { "weapon", "tag_weapon", "tag_handle" };
    private static readonly string[] ShotTagNames = { "shot", "tag_shot" };

    private readonly ITestOutputHelper _out;
    public MuzzleDumpProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_RealWeapon_Muzzle_Composition()
    {
        if (!Directory.Exists(Weapons)) { _out.WriteLine("weapons dir missing — skipped"); return; }

        var sb = new StringBuilder();
        DumpWeapon(sb, "Devastator (rocket launcher)", "v_rl.md3", "h_rl.iqm");
        DumpWeapon(sb, "Arc", "v_arc.md3", "h_arc.iqm");
        DumpWeapon(sb, "Crylink", "v_crylink.md3", "h_crylink.iqm");
        DumpWeapon(sb, "Blaster (laser)", "v_laser.md3", "h_laser.iqm");
        DumpWeapon(sb, "Shotgun", "v_shotgun.md3", "h_shotgun.iqm");
        DumpWeapon(sb, "Vortex (nex)", "v_nex.md3", "h_nex.iqm");

        string report = sb.ToString();
        File.WriteAllText(ReportPath, report);
        _out.WriteLine(report);

        // Pin the finding so a future asset/parser change can't silently re-open the question: the v_ visual
        // models carry NO shot tag, so QC takes the h_-rig own-shot-tag branch and the port's single-bone
        // extraction is already faithful. (Guarded by File.Exists inside the helpers; only asserts when present.)
        AssertVHasNoShotTag("v_rl.md3");
        AssertVHasNoShotTag("v_arc.md3");
        AssertVHasNoShotTag("v_crylink.md3");
        AssertHHasShotTag("h_rl.iqm");
        AssertHHasShotTag("h_arc.iqm");
        AssertHHasShotTag("h_crylink.iqm");
    }

    private static void AssertVHasNoShotTag(string vFile)
    {
        string p = Path.Combine(Weapons, vFile);
        if (!File.Exists(p)) return;
        Assert.True(VShotLocal(File.ReadAllBytes(p)) is null,
            $"{vFile}: v_ visual model unexpectedly has a shot tag — the muzzle path assumption changed");
    }

    private static void AssertHHasShotTag(string hFile)
    {
        string p = Path.Combine(Weapons, hFile);
        if (!File.Exists(p)) return;
        Assert.True(MuzzleTagExtractAny(File.ReadAllBytes(p)) is not null,
            $"{hFile}: h_ rig has no shot tag — the per-weapon muzzle would be silently inert");
    }

    /// <summary>The v_ model's own shot-tag local position (magic-dispatched), or null if it has none.</summary>
    private static Vector3? VShotLocal(byte[] bytes)
    {
        string magic = Magic(bytes);
        if (magic.StartsWith("IDP3", StringComparison.Ordinal))
        {
            Md3Data v = Md3Reader.Read(bytes);
            foreach (string n in ShotTagNames)
                if (v.TagsByName.TryGetValue(n, out Md3Tag? st) && st.Transforms.Length > 0) return st.Transforms[0].Origin;
            return null;
        }
        return MuzzleTagExtractAny(bytes); // IQM/DPM-with-.md3-extension: same single-bone resolution
    }

    private void DumpWeapon(StringBuilder sb, string label, string vFile, string hFile)
    {
        sb.AppendLine("============================================================");
        sb.AppendLine($"WEAPON: {label}   (v={vFile}, h={hFile})");
        sb.AppendLine("============================================================");

        string vPath = Path.Combine(Weapons, vFile);
        string hPath = Path.Combine(Weapons, hFile);
        if (!File.Exists(vPath) || !File.Exists(hPath))
        {
            sb.AppendLine($"  MISSING FILE (v exists={File.Exists(vPath)}, h exists={File.Exists(hPath)})");
            sb.AppendLine();
            return;
        }

        byte[] vBytes = File.ReadAllBytes(vPath);
        byte[] hBytes = File.ReadAllBytes(hPath);
        string vMagic = Magic(vBytes);
        sb.AppendLine($"  v magic = {vMagic.Split('\0')[0]}");
        sb.AppendLine($"  h magic = {Magic(hBytes).Split('\0')[0]}");

        // ---- v_ model: all tags/bones + shot tag frame-0 local origin (dispatch by MAGIC, ext lies) ----
        // NOTE: v_crylink.md3 et al. are actually INTERQUAKEMODEL despite the .md3 extension.
        Vector3? vShotLocal = null;
        if (vMagic.StartsWith("IDP3", StringComparison.Ordinal))
        {
            Md3Data v = Md3Reader.Read(vBytes);
            sb.AppendLine($"  -- v_ MD3 tags ({v.Tags.Count}): ");
            foreach (Md3Tag t in v.Tags)
            {
                Vector3 o = t.Transforms.Length > 0 ? t.Transforms[0].Origin : Vector3.Zero;
                sb.AppendLine($"       tag '{t.Name}'  frame0.origin = {Fmt(o)}");
            }
            foreach (string name in ShotTagNames)
                if (v.TagsByName.TryGetValue(name, out Md3Tag? st) && st.Transforms.Length > 0)
                { vShotLocal = st.Transforms[0].Origin; break; }
        }
        else if (vMagic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal))
        {
            IqmData v = IqmReader.Read(vBytes);
            sb.AppendLine($"  -- v_ IQM joints ({v.Joints.Length}) [extension lies: this v_ is IQM]:");
            for (int i = 0; i < v.Joints.Length; i++)
                sb.AppendLine($"       joint[{i,2}] '{v.Joints[i].Name}' parent={v.Joints[i].Parent}");
            foreach (string sn in ShotTagNames)
            {
                int idx = FindIqmJoint(v, sn);
                if (idx >= 0) { vShotLocal = IqmBoneWorldRest(v, idx).Translation; break; }
            }
        }
        else if (vMagic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal))
        {
            DpmData v = DpmReader.Read(vBytes);
            DpmFrame bind = v.Frames.Length > 0 ? v.Frames[0] : null!;
            sb.AppendLine($"  -- v_ DPM bones ({v.Bones.Length}) [extension lies: this v_ is DPM]:");
            for (int i = 0; i < v.Bones.Length; i++)
                sb.AppendLine($"       bone[{i,2}] '{v.Bones[i].Name}' parent={v.Bones[i].Parent}");
            foreach (string sn in ShotTagNames)
            {
                int idx = FindDpmBone(v, sn);
                if (idx >= 0 && bind is not null) { vShotLocal = DpmBoneWorldRest(v, bind, idx).Translation; break; }
            }
        }
        sb.AppendLine($"     => v_ shot-tag LOCAL origin = {(vShotLocal.HasValue ? Fmt(vShotLocal.Value) : "<none>")}");

        // ---- h_ rig: bones, attach-bone rest matrix, own shot bone ------------------------------------
        Matrix4x4 attachRest = Matrix4x4.Identity;
        string attachBoneUsed = "<none>";
        bool hasAttach = false;
        Vector3? hOwnShotLocal = null;
        string hMagic = Magic(hBytes);

        if (hMagic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal))
        {
            DpmData h = DpmReader.Read(hBytes);
            DpmFrame bind = h.Frames.Length > 0 ? h.Frames[0] : null!;
            sb.AppendLine($"  -- h_ DPM bones ({h.Bones.Length}), bind frame='{(bind?.Name ?? "<none>")}':");
            for (int i = 0; i < h.Bones.Length; i++)
                sb.AppendLine($"       bone[{i,2}] '{h.Bones[i].Name}' parent={h.Bones[i].Parent} flags={h.Bones[i].Flags}");

            foreach (string bn in AttachBoneNames)
            {
                int idx = FindDpmBone(h, bn);
                if (idx >= 0 && bind is not null)
                {
                    attachRest = DpmBoneWorldRest(h, bind, idx);
                    attachBoneUsed = bn; hasAttach = true; break;
                }
            }
            foreach (string sn in ShotTagNames)
            {
                int idx = FindDpmBone(h, sn);
                if (idx >= 0 && bind is not null) { hOwnShotLocal = DpmBoneWorldRest(h, bind, idx).Translation; break; }
            }
        }
        else if (hMagic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal))
        {
            IqmData h = IqmReader.Read(hBytes);
            sb.AppendLine($"  -- h_ IQM joints ({h.Joints.Length}):");
            for (int i = 0; i < h.Joints.Length; i++)
                sb.AppendLine($"       joint[{i,2}] '{h.Joints[i].Name}' parent={h.Joints[i].Parent}");

            foreach (string bn in AttachBoneNames)
            {
                int idx = FindIqmJoint(h, bn);
                if (idx >= 0) { attachRest = IqmBoneWorldRest(h, idx); attachBoneUsed = bn; hasAttach = true; break; }
            }
            foreach (string sn in ShotTagNames)
            {
                int idx = FindIqmJoint(h, sn);
                if (idx >= 0) { hOwnShotLocal = IqmBoneWorldRest(h, idx).Translation; break; }
            }
        }
        else
        {
            sb.AppendLine($"  -- h_ UNKNOWN MAGIC: {hMagic}");
        }

        sb.AppendLine($"     attach bone used = '{attachBoneUsed}' (found={hasAttach})");
        if (hasAttach)
        {
            sb.AppendLine($"     attach REST translation = {Fmt(attachRest.Translation)}");
            sb.AppendLine($"     attach REST basis rows:");
            sb.AppendLine($"        row M1* (img of +X) = {Fmt(new Vector3(attachRest.M11, attachRest.M12, attachRest.M13))}");
            sb.AppendLine($"        row M2* (img of +Y) = {Fmt(new Vector3(attachRest.M21, attachRest.M22, attachRest.M23))}");
            sb.AppendLine($"        row M3* (img of +Z) = {Fmt(new Vector3(attachRest.M31, attachRest.M32, attachRest.M33))}");
        }
        sb.AppendLine($"     h_ own shot tag LOCAL = {(hOwnShotLocal.HasValue ? Fmt(hOwnShotLocal.Value) : "<none>")}");

        // ---- (i) PORT-CURRENT: what MuzzleTag produces today via the h_-first-then-v_ AssetLoader path -
        // NetGame.PrecacheWeaponModels reads h_<name> first; only falls back to v_ if h_ has no shot tag.
        Vector3? portCurrent;
        string portSource;
        Vector3? hExtract = MuzzleTagExtractAny(hBytes);
        if (hExtract.HasValue) { portCurrent = hExtract; portSource = $"h_ ({hMagic.Split('\0')[0]}) single-bone MuzzleTag.Extract"; }
        else
        {
            Vector3? vExtract = MuzzleTagExtractAny(vBytes);
            portCurrent = vExtract; portSource = $"v_ ({vMagic.Split('\0')[0]}) single-bone MuzzleTag.Extract (h_ had no shot tag)";
        }

        // ---- (ii) BASE-FAITHFUL: weapon-attach REST ∘ v_ shot-tag LOCAL -------------------------------
        Vector3? baseFaithful = null;
        if (vShotLocal.HasValue && hasAttach)
            baseFaithful = Vector3.Transform(vShotLocal.Value, attachRest);
        else if (vShotLocal.HasValue)
            baseFaithful = vShotLocal; // no attach bone -> identity attach (QC would still attach, but degrade gracefully)

        sb.AppendLine();
        sb.AppendLine($"  (i)   PORT-CURRENT  = {(portCurrent.HasValue ? Fmt(portCurrent.Value) : "<none>")}   [source: {portSource}]");
        sb.AppendLine($"  (ii)  BASE-FAITHFUL = {(baseFaithful.HasValue ? Fmt(baseFaithful.Value) : "<none>")}   [attach('{attachBoneUsed}') REST  o  v_shotLocal]");
        if (portCurrent.HasValue && baseFaithful.HasValue)
        {
            Vector3 d = baseFaithful.Value - portCurrent.Value;
            sb.AppendLine($"  (iii) DELTA (ii - i) = {Fmt(d)}");
            sb.AppendLine($"        -> dZ = {d.Z.ToString("F3", CultureInfo.InvariantCulture)}  (positive Z = base-faithful is HIGHER; the 'fires low' fix amount)");
            sb.AppendLine($"        -> dX = {d.X.ToString("F3", CultureInfo.InvariantCulture)}  (positive X = base-faithful is MORE FORWARD)");
        }
        sb.AppendLine();
    }

    // ----- composition helpers (mirror MuzzleTag's private chain; full rest matrix, not just translation) -

    private static Matrix4x4 IqmBoneWorldRest(IqmData iqm, int idx)
    {
        IqmJoint j = iqm.Joints[idx];
        Matrix4x4 local = Trs(j.Translate, j.Rotate, j.Scale);
        return j.Parent >= 0 ? local * IqmBoneWorldRest(iqm, j.Parent) : local;
    }

    private static Matrix4x4 DpmBoneWorldRest(DpmData dpm, DpmFrame bind, int idx)
    {
        Matrix4x4 local = bind.BonePoses[idx].ToMatrix();
        int parent = dpm.Bones[idx].Parent;
        return parent >= 0 ? local * DpmBoneWorldRest(dpm, bind, parent) : local;
    }

    private static Matrix4x4 Trs(Vector3 t, Quaternion r, Vector3 s)
        => Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

    private static int FindIqmJoint(IqmData iqm, string name)
    {
        for (int i = 0; i < iqm.Joints.Length; i++)
            if (string.Equals(iqm.Joints[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static int FindDpmBone(DpmData dpm, string name)
    {
        for (int i = 0; i < dpm.Bones.Length; i++)
            if (string.Equals(dpm.Bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static Vector3? MuzzleTagExtractAny(byte[] bytes)
    {
        string magic = Magic(bytes);
        if (magic.StartsWith("INTERQUAKEMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(IqmReader.Read(bytes));
        if (magic.StartsWith("DARKPLACESMODEL", StringComparison.Ordinal)) return MuzzleTag.Extract(DpmReader.Read(bytes));
        if (magic.StartsWith("IDP3", StringComparison.Ordinal)) return MuzzleTag.Extract(Md3Reader.Read(bytes));
        return null;
    }

    private static string Magic(byte[] bytes)
        => System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(16, bytes.Length));

    private static string Fmt(Vector3 v)
        => $"({v.X.ToString("F3", CultureInfo.InvariantCulture)}, {v.Y.ToString("F3", CultureInfo.InvariantCulture)}, {v.Z.ToString("F3", CultureInfo.InvariantCulture)})";
}
