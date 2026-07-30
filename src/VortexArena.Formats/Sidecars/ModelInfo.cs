namespace XonoticGodot.Formats.Sidecars;

/// <summary>
/// A player model's <c>.txt</c> model-info sidecar (Xonotic <c>get_model_parameters</c>, read by
/// <c>qcsrc/common/util.qc</c>). The file is named <c>&lt;model&gt;_&lt;skin&gt;.txt</c> (e.g.
/// <c>erebus.iqm_0.txt</c>) and carries the per-skin display metadata plus the SKELETAL parameters the
/// upper/lower-body split + view-pitch aim need: which bone divides the torso from the legs
/// (<see cref="BoneUpperBody"/>), whether the upper body needs re-anchoring (<see cref="FixBone"/>), the
/// weapon attachment bone, and the spine/head aim bones with their per-bone blend weights
/// (<see cref="AimBones"/>). Bone names are kept RAW (they can contain spaces, e.g. <c>bip01 r hand</c>) —
/// the consumer resolves them to bone indices against the model's unsanitized joint names.
/// </summary>
public sealed class ModelInfo
{
    // --- display metadata (the menu/scoreboard read these; not needed for posing) ---
    public string Name = "";
    public string Species = "";
    public string Sex = "";
    public string Description = "";
    public bool Hidden;

    // --- skeletal parameters (player_skeleton.qc) ---
    /// <summary>The split bone: it and its descendants are the UPPER body (QC <c>bone_upperbody</c>). "" = no split.</summary>
    public string BoneUpperBody = "";

    /// <summary>The weapon attachment bone (QC <c>bone_weapon</c>); falls back to "weapon"/"tag_weapon" otherwise.</summary>
    public string BoneWeapon = "";

    /// <summary>Re-anchor the upper body after the split (QC <c>fixbone</c>).</summary>
    public bool FixBone;

    /// <summary>The view-pitch aim bones in declared order (QC <c>bone_aimN &lt;weight&gt; &lt;name&gt;</c>): each
    /// spine/head bone bends by <c>weight * v_angle.x</c>, so the bends compound down the chain.</summary>
    public List<(float Weight, string Bone)> AimBones { get; } = new();
}

/// <summary>
/// Parser for the line-oriented player model-info sidecar: <c>key rest-of-line</c> per line. Mirrors the
/// permissive QC <c>get_model_parameters</c> token reader (unknown keys ignored; values can contain spaces).
/// </summary>
public static class ModelInfoParser
{
    public static ModelInfo Parse(string text)
    {
        var info = new ModelInfo();
        if (string.IsNullOrEmpty(text))
            return info;

        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '/')
                continue;

            // key = first whitespace-delimited token; value = the remainder (verbatim, may contain spaces).
            int sp = IndexOfWhitespace(line);
            string key = sp < 0 ? line : line.Substring(0, sp);
            string value = sp < 0 ? "" : line.Substring(sp + 1).Trim();

            switch (key)
            {
                case "name": info.Name = value; break;
                case "species": info.Species = value; break;
                case "sex": info.Sex = value; break;
                case "description": info.Description = value; break;
                case "hidden": info.Hidden = ParseBool(value); break;
                case "bone_upperbody": info.BoneUpperBody = value; break;
                case "bone_weapon": info.BoneWeapon = value; break;
                case "fixbone": info.FixBone = ParseBool(value); break;
                default:
                    if (key.StartsWith("bone_aim", System.StringComparison.Ordinal))
                    {
                        // bone_aimN <weight> <bone name...> — weight is the first sub-token, name the rest.
                        int sp2 = IndexOfWhitespace(value);
                        if (sp2 < 0) break; // malformed: no name
                        float weight = ParseFloat(value.Substring(0, sp2));
                        string bone = value.Substring(sp2 + 1).Trim();
                        if (bone.Length > 0)
                            info.AimBones.Add((weight, bone));
                    }
                    break;
            }
        }
        return info;
    }

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] == ' ' || s[i] == '\t')
                return i;
        return -1;
    }

    private static bool ParseBool(string s)
        => s.Length > 0 && s != "0" && !s.Equals("false", System.StringComparison.OrdinalIgnoreCase);

    private static float ParseFloat(string s)
        => float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f;
}
