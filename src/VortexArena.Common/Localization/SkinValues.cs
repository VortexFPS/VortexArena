// Port of qcsrc/menu/skin.qh (the SKINVECTOR/SKINFLOAT/SKINSTRING X-macro + Skin_ApplySetting) and
// qcsrc/menu/skin-customizables.inc (the authoritative Generic-default schema — ~190 keys), plus the
// skinvalues.txt line parse from qcsrc/menu/menu.qc m_init_delayed (menu.qc:192-201).
//
// skin.qh is included twice: once to DECLARE every key as a global `SKIN<name>` seeded with its shipped default
// (the Generic/default skin baked into the binary), and once to build `Skin_ApplySetting(key,value)` whose
// `switch(key)` case label is the BARE key (no SKIN prefix) and which writes via stov (vectors) / stof (floats)
// / strzone (strings). `skinvalues.txt` only OVERRIDES the keys it lists, on top of the schema baseline.
//
// This is the Godot-free table (ADR-0008): MenuSkin.cs consumes it (every SKIN* the port reads is resolved here)
// and the unit tests exercise ApplySetting/Load headlessly. The value-substring + bare-key-case + //-and-empty-
// ignore + unknown-key-log semantics are faithful to Base; see Load() and ApplySetting().
using System;
using System.Collections.Generic;
using System.Globalization;
using XonoticGodot.Common.Diagnostics;

namespace XonoticGodot.Common.Localization;

/// <summary>A Godot-free 3-component skin value (an <c>SKINVECTOR</c> — usually an RGB colour or a 2-D size).
/// Kept here so the schema has no Godot dependency; <c>MenuSkin</c> converts it to a Godot Color/Vector2.</summary>
public readonly record struct SkinVec(float X, float Y, float Z)
{
    public static readonly SkinVec Zero = new(0, 0, 0);
}

/// <summary>
/// The Xonotic menu-skin value table — the C# successor to the <c>SKIN*</c> globals from <c>skin.qh</c> +
/// <c>skin-customizables.inc</c>. Each instance starts at the shipped Generic defaults; <see cref="Load"/> (or
/// <see cref="ApplySetting"/>) overlays a <c>skinvalues.txt</c> on top. Typed accessors
/// (<see cref="Float"/>/<see cref="Vector"/>/<see cref="Str"/>) read the resolved value.
/// </summary>
public sealed class SkinValues
{
    private readonly Dictionary<string, float> _floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkinVec> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    // Keys that a loaded skinvalues.txt has OVERRIDDEN (vs the seeded Generic default). Lets a consumer
    // distinguish "the skin file set this" from "this is still the schema baseline" — used by MenuSkin to keep
    // its luma-tuned fallback when no file is loaded (headless), while honoring a loaded skin's overrides.
    private readonly HashSet<string> _overridden = new(StringComparer.Ordinal);

    /// <summary>Build a table seeded with the full Generic-default schema (skin-customizables.inc:36-282).</summary>
    public SkinValues()
    {
        SeedDefaults();
    }

    // ---- typed accessors (the SKIN<name> globals; key is the BARE name, no SKIN prefix) -------------------

    /// <summary>Resolved float value for <paramref name="key"/> (bare key, e.g. <c>"FONTSIZE_NORMAL"</c>);
    /// <paramref name="fallback"/> if the key isn't a known float.</summary>
    public float Float(string key, float fallback = 0f)
        => _floats.TryGetValue(key, out float v) ? v : fallback;

    /// <summary>Resolved vector value for <paramref name="key"/>; <paramref name="fallback"/> if unknown.</summary>
    public SkinVec Vector(string key, SkinVec fallback = default)
        => _vectors.TryGetValue(key, out SkinVec v) ? v : fallback;

    /// <summary>Resolved string value for <paramref name="key"/>; <paramref name="fallback"/> if unknown.</summary>
    public string Str(string key, string fallback = "")
        => _strings.TryGetValue(key, out string? v) ? v : fallback;

    /// <summary>True when <paramref name="key"/> is a known float slot.</summary>
    public bool HasFloat(string key) => _floats.ContainsKey(key);
    /// <summary>True when <paramref name="key"/> is a known vector slot.</summary>
    public bool HasVector(string key) => _vectors.ContainsKey(key);
    /// <summary>True when <paramref name="key"/> is a known string slot.</summary>
    public bool HasString(string key) => _strings.ContainsKey(key);

    /// <summary>True when a loaded skin file has overridden <paramref name="key"/> (vs the seeded default).</summary>
    public bool IsOverridden(string key) => _overridden.Contains(key);

    // ---- the loader (skinvalues.txt) ----------------------------------------------------------------------

    /// <summary>
    /// Overlay a <c>skinvalues.txt</c> on top of the schema defaults — a faithful port of the parse loop in
    /// <c>m_init_delayed</c> (menu.qc:192-201):
    /// <list type="bullet">
    ///   <item>skip lines beginning <c>"title "</c> (6 chars) and <c>"author "</c> (7 chars) — those belong to
    ///         skinlist.qc.</item>
    ///   <item><c>tokenize_console</c> the line; if it has fewer than 2 tokens, skip.</item>
    ///   <item>KEY = first token; VALUE = the raw substring from the START of token 1 to the END of the LAST
    ///         token (so multi-word vectors <c>1 0.7 0.7</c> survive verbatim — whitespace between key and value
    ///         is the delimiter only, and quotes are NOT stripped).</item>
    ///   <item>apply via <see cref="ApplySetting"/>.</item>
    /// </list>
    /// </summary>
    public void Load(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string s = raw;
            // QC: substring(s,0,6)=="title " / substring(s,0,7)=="author " (handled by skinlist.qc).
            if (s.StartsWith("title ", StringComparison.Ordinal)) continue;
            if (s.StartsWith("author ", StringComparison.Ordinal)) continue;

            // tokenize_console + the value-substring: KEY is token 0; VALUE is the verbatim text from where
            // token 1 starts to where the last token ends. We compute those spans over the ORIGINAL line so the
            // value keeps its internal spacing (e.g. "1 0.7 0.7") exactly as Base's argv_start/argv_end do.
            if (!SplitKeyValue(s, out string key, out string value))
                continue; // < 2 tokens
            ApplySetting(key, value);
        }
    }

    /// <summary>
    /// Apply one parsed setting — the C# successor to <c>Skin_ApplySetting(key, _value)</c> (skin.qh:16-28). The
    /// <paramref name="key"/> is the BARE key (no <c>SKIN</c> prefix), matched against the schema: a vector slot
    /// parses the value with <c>stov</c>, a float slot with <c>stof</c>, a string slot stores it raw. The special
    /// keys <c>""</c> and <c>"//"</c> are ignored (skin.qh:22); an unknown key is logged (QC
    /// <c>LOG_TRACE("Invalid key in skin file: ", key)</c>) and dropped.
    /// </summary>
    public void ApplySetting(string key, string value)
    {
        if (key.Length == 0 || key == "//")
            return; // case "": break; case "//": break;

        if (_vectors.ContainsKey(key))
        {
            _vectors[key] = Stov(value);
            _overridden.Add(key);
        }
        else if (_floats.ContainsKey(key))
        {
            _floats[key] = Stof(value);
            _overridden.Add(key);
        }
        else if (_strings.ContainsKey(key))
        {
            _strings[key] = value; // strzone(_value): stored raw, no quote stripping
            _overridden.Add(key);
        }
        else
        {
            Log.Trace("Invalid key in skin file: " + key); // default: LOG_TRACE("Invalid key in skin file: ", key)
        }
    }

    // ---- parsing helpers (stof/stov + the value-substring tokenizer) -------------------------------------

    /// <summary>
    /// Split a skinvalues.txt line into (key, value) using the Base value-substring semantics: the key is the
    /// first whitespace-delimited token; the value is the verbatim remainder from the first non-whitespace char
    /// AFTER the key to the last non-whitespace char on the line. Returns false when the line has fewer than two
    /// tokens (no value). Quotes in the value are preserved. (Public so it's unit-testable.)
    /// </summary>
    public static bool SplitKeyValue(string line, out string key, out string value)
    {
        key = "";
        value = "";

        int i = 0, n = line.Length;
        // skip leading whitespace
        while (i < n && char.IsWhiteSpace(line[i])) i++;
        if (i >= n) return false;
        int keyStart = i;
        while (i < n && !char.IsWhiteSpace(line[i])) i++;
        key = line.Substring(keyStart, i - keyStart);

        // skip whitespace between key and value
        while (i < n && char.IsWhiteSpace(line[i])) i++;
        if (i >= n) return false; // no second token
        int valStart = i;
        // value runs to the last non-whitespace char (argv_end_index(-1)); trim trailing whitespace only.
        // [A2-review F9] menu.qc uses tokenize_console (COM_ParseToken_Console), which STOPS at an inline `//`
        // line comment, so for `KEY 12 // note` the value is "12" (not "12 // note"). Mirror that: cap the
        // value at an unquoted `//` before trimming. (No shipped skin uses inline comments, but a hand-edited
        // skin with DP's convention would otherwise zero the value through stof/stov.)
        int valEnd = n;
        int comment = line.IndexOf("//", valStart, System.StringComparison.Ordinal);
        if (comment >= 0) valEnd = comment;
        while (valEnd > valStart && char.IsWhiteSpace(line[valEnd - 1])) valEnd--;
        value = line.Substring(valStart, valEnd - valStart);
        return value.Length > 0;
    }

    /// <summary>QC <c>stof</c>: parse a float, invariant culture, 0 on failure (the engine returns 0 for junk).</summary>
    public static float Stof(string s)
        => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    /// <summary>
    /// QC <c>stov</c>: parse a vector from <c>'r g b'</c> (single-quoted) OR bare <c>r g b</c>. The skinvalues.txt
    /// file stores vectors bare (the Perl generator prints <c>%-31s %s</c> with no quotes), but a manually edited
    /// file may quote them, so both forms are accepted. Missing components default to 0. (Public so it's testable.)
    /// </summary>
    public static SkinVec Stov(string s)
    {
        // strip a single pair of surrounding single quotes (the QC '...' vector literal); bare values pass through.
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'')
            t = t.Substring(1, t.Length - 2);
        string[] p = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float x = p.Length > 0 ? ParseComp(p[0]) : 0f;
        float y = p.Length > 1 ? ParseComp(p[1]) : 0f;
        float z = p.Length > 2 ? ParseComp(p[2]) : 0f;
        return new SkinVec(x, y, z);
    }

    private static float ParseComp(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

    // ---- the Generic-default schema (skin-customizables.inc:36-282) --------------------------------------

    private void SeedDefaults()
    {
        // font sizes (used for everything)
        F("FONTSIZE_NORMAL", 12);
        F("HEIGHT_NORMAL", 1.5f);
        F("FONTSIZE_TITLE", 16);
        F("HEIGHT_TITLE", 1.5f);
        F("HEIGHT_ZOOMEDTITLE", -1);

        // tooltips
        S("GFX_TOOLTIP", "tooltip");
        V("MARGIN_TOOLTIP", 5, 5, 0);
        V("BORDER_TOOLTIP", 1, 1, 0);
        V("AVOID_TOOLTIP", 8, 8, 0);
        F("WIDTH_TOOLTIP", 0.3f);
        F("FONTSIZE_TOOLTIP", 12);
        F("ALPHA_TOOLTIP", 0.7f);
        V("COLOR_TOOLTIP", 1, 1, 1);

        // the individual dialog background colors
        V("COLOR_DIALOG_FIRSTRUN", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_MULTIPLAYER", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_SETTINGS", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_TEAMSELECT", 1, 1, 1);
        V("COLOR_DIALOG_SANDBOXTOOLS", 1, 1, 1);
        V("COLOR_DIALOG_QUIT", 1, 0, 0);
        V("COLOR_DIALOG_ADVANCED", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_MUTATORS", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_MAPINFO", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_MEDIA", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_USERBIND", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_SINGLEPLAYER", 1, 1, 0.7f);
        V("COLOR_DIALOG_CREDITS", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_WEAPONS", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_VIEW", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_MODEL", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_CROSSHAIR", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_HUD", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_SERVERINFO", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_WELCOME", 1, 0.7f, 0.7f);
        V("COLOR_DIALOG_CVARS", 1, 0, 0);
        V("COLOR_DIALOG_SCREENSHOTVIEWER", 0.7f, 0.7f, 1);
        V("COLOR_DIALOG_HUDCONFIRM", 1, 0, 0);

        // nexposee positions of windows (scale transformation centers, NOT actual window positions)
        V("POSITION_DIALOG_MULTIPLAYER", 0.9f, 0.5f, 0);
        V("POSITION_DIALOG_SINGLEPLAYER", 0.1f, 0.1f, 0);
        V("POSITION_DIALOG_MEDIA", 0.9f, 0.9f, 0);
        V("POSITION_DIALOG_SETTINGS", 0.1f, 0.9f, 0);
        V("POSITION_DIALOG_CREDITS", 0.3f, 1.2f, 0);
        V("POSITION_DIALOG_QUIT", 0.9f, 1.2f, 0);

        // mouse
        S("GFX_CURSOR", "cursor");
        V("SIZE_CURSOR", 32, 32, 0);
        V("OFFSET_CURSOR", 0, 0, 0);
        F("ALPHA_CURSOR_INTRO", 0);

        // general
        V("COLOR_BACKGROUND", 0, 0, 0);
        S("GFX_BACKGROUND", "background");
        S("GFX_BACKGROUND_INGAME", "background_ingame");
        S("ALIGN_BACKGROUND", "5");
        S("ALIGN_BACKGROUND_INGAME", "5");
        F("ALPHA_BACKGROUND_INGAME", 0.7f);
        F("ALPHA_DISABLED", 0.2f);
        F("ALPHA_BEHIND", 0.5f);
        F("ALPHA_TEXT", 0.7f);
        V("COLOR_TEXT", 1, 1, 1);
        F("ALPHA_HEADER", 0.5f);
        V("COLOR_HEADER", 1, 1, 1);

        // item: button
        S("GFX_BUTTON", "button");
        S("GFX_BUTTON_GRAY", "buttongray");
        S("GFX_BUTTON_BIG", "bigbutton");
        S("GFX_BUTTON_BIG_GRAY", "bigbuttongray");
        V("COLOR_BUTTON_N", 1, 1, 1);
        V("COLOR_BUTTON_C", 1, 1, 1);
        V("COLOR_BUTTON_F", 1, 1, 1);
        V("COLOR_BUTTON_D", 1, 1, 1);
        F("MARGIN_BUTTON", 0.5f);

        // item: campaign
        F("ALPHA_CAMPAIGN_SELECTABLE", 0.8f);
        V("COLOR_CAMPAIGN_SELECTABLE", 1, 1, 1);
        F("ALPHA_CAMPAIGN_CURRENT", 1);
        V("COLOR_CAMPAIGN_CURRENT", 1, 1, 0);
        F("ALPHA_CAMPAIGN_FUTURE", 0.2f);
        V("COLOR_CAMPAIGN_FUTURE", 1, 1, 1);
        F("ALPHA_CAMPAIGN_DESCRIPTION", 0.7f);

        // item: checkbox
        S("GFX_CHECKBOX", "checkbox");
        V("COLOR_CHECKBOX_N", 1, 1, 1);
        V("COLOR_CHECKBOX_C", 1, 1, 1);
        V("COLOR_CHECKBOX_F", 1, 1, 1);
        V("COLOR_CHECKBOX_D", 1, 1, 1);

        // item: color picker
        S("GFX_COLORPICKER", "colorpicker");
        V("MARGIN_COLORPICKER", 0, 0, 0);

        // item: credits list
        V("COLOR_CREDITS_TITLE", 1, 1, 1);
        F("ALPHA_CREDITS_TITLE", 1);
        V("COLOR_CREDITS_FUNCTION", 1, 1, 1);
        F("ALPHA_CREDITS_FUNCTION", 0.7f);
        V("COLOR_CREDITS_PERSON", 0.7f, 0.7f, 1);
        F("ALPHA_CREDITS_PERSON", 0.7f);
        F("ROWS_CREDITS", 20);
        F("WIDTH_CREDITS", 0.5f);

        // item: cvar list
        F("ALPHA_CVARLIST_SAVED", 1);
        F("ALPHA_CVARLIST_TEMPORARY", 0.7f);
        V("COLOR_CVARLIST_CHANGED", 1, 1, 0.4f);
        V("COLOR_CVARLIST_UNCHANGED", 1, 1, 1);
        V("COLOR_CVARLIST_CONTROLS", 1, 0, 0);

        // item: dialog
        S("GFX_DIALOGBORDER", "border");
        S("GFX_CLOSEBUTTON", "closebutton");
        F("MARGIN_TOP", 8);
        F("MARGIN_BOTTOM", 8);
        F("MARGIN_LEFT", 8);
        F("MARGIN_RIGHT", 8);
        F("MARGIN_COLUMNS", 4);
        F("MARGIN_ROWS", 4);
        F("HEIGHT_DIALOGBORDER", 1);

        // item: input box
        S("GFX_INPUTBOX", "inputbox");
        V("COLOR_INPUTBOX_N", 1, 1, 1);
        V("COLOR_INPUTBOX_F", 1, 1, 1);
        F("MARGIN_INPUTBOX_CHARS", 1);

        // item: clear button
        S("GFX_CLEARBUTTON", "clearbutton");
        F("OFFSET_CLEARBUTTON", 0);
        V("COLOR_CLEARBUTTON_N", 1, 1, 1);
        V("COLOR_CLEARBUTTON_F", 1, 1, 1);
        V("COLOR_CLEARBUTTON_C", 1, 1, 1);

        // item: gametype list
        F("BOOL_GAMETYPELIST_ICON_BLUR", 1);

        // item: key grabber
        V("COLOR_KEYGRABBER_TITLES", 1, 1, 1);
        F("ALPHA_KEYGRABBER_TITLES", 1);
        V("COLOR_KEYGRABBER_KEYS", 1, 1, 1);
        F("ALPHA_KEYGRABBER_KEYS", 0.7f);
        V("COLOR_KEYGRABBER_KEYS_IMMUTABLE", 0.5f, 0.5f, 0.5f);
        F("ALPHA_KEYGRABBER_KEYS_IMMUTABLE", 0.7f);

        // item: list box
        V("COLOR_LISTBOX_SELECTED", 0, 0, 1);
        F("ALPHA_LISTBOX_SELECTED", 0.5f);
        V("COLOR_LISTBOX_WAITING", 1, 0, 0);
        F("ALPHA_LISTBOX_WAITING", 0.5f);
        V("COLOR_LISTBOX_BACKGROUND", 0, 0, 0);
        F("ALPHA_LISTBOX_BACKGROUND", 0.5f);
        V("COLOR_LISTBOX_FOCUSED", 0, 0, 1);
        F("ALPHA_LISTBOX_FOCUSED", 0.7f);
        F("FADEALPHA_LISTBOX_FOCUSED", 0.3f);

        // item: map list
        V("COLOR_MAPLIST_TITLE", 1, 1, 1);
        V("COLOR_MAPLIST_AUTHOR", 0.4f, 0.4f, 0.7f);
        V("COLOR_MAPLIST_INCLUDEDBG", 0, 0, 0);
        F("ALPHA_MAPLIST_INCLUDEDFG", 1);
        F("ALPHA_MAPLIST_INCLUDEDBG", 0.5f);
        F("ALPHA_MAPLIST_NOTINCLUDEDFG", 0.4f);

        // item: nexposee
        V("ALPHAS_MAINMENU", 0.6f, 0.8f, 0.9f);
        F("ALPHA_DIALOG_SANDBOXTOOLS", 0.6f);

        // item: player color button
        S("GFX_COLORBUTTON", "colorbutton");

        // item: player model
        V("COLOR_MODELTITLE", 1, 1, 1);
        F("ALPHA_MODELTITLE", 1);

        // item: special character picker
        V("COLOR_CHARMAP_CHAR", 1, 1, 1);
        F("ALPHA_CHARMAP_CHAR", 1);

        // item: crosshair picker
        V("COLOR_CROSSHAIRPICKER_CROSSHAIR", 1, 1, 1);
        F("ALPHA_CROSSHAIRPICKER_CROSSHAIR", 1);

        // item: radio button
        S("GFX_RADIOBUTTON", "radiobutton");
        V("COLOR_RADIOBUTTON_N", 1, 1, 1);
        V("COLOR_RADIOBUTTON_C", 1, 1, 1);
        V("COLOR_RADIOBUTTON_F", 1, 1, 1);
        V("COLOR_RADIOBUTTON_D", 1, 1, 1);

        // item: scrollbar
        S("GFX_SCROLLBAR", "scrollbar");
        V("COLOR_SCROLLBAR_N", 1, 1, 1);
        V("COLOR_SCROLLBAR_F", 1, 1, 1);
        V("COLOR_SCROLLBAR_C", 1, 1, 1);
        V("COLOR_SCROLLBAR_S", 1, 1, 1);
        F("WIDTH_SCROLLBAR", 16);

        // item: server list
        F("ALPHA_SERVERLIST_CATEGORY", 0.7f);
        V("COLOR_SERVERLIST_CATEGORY", 1, 1, 1);
        F("ALPHA_SERVERLIST_FULL", 0.4f);
        F("ALPHA_SERVERLIST_EMPTY", 0.7f);
        V("COLOR_SERVERLIST_LOWPING", 0, 1, 0);
        V("COLOR_SERVERLIST_MEDPING", 1, 1, 0);
        V("COLOR_SERVERLIST_HIGHPING", 1, 0, 0);
        F("ALPHA_SERVERLIST_HIGHPING", 0.4f);
        F("ALPHA_SERVERLIST_FAVORITE", 0.8f);
        V("COLOR_SERVERLIST_FAVORITE", 1, 1, 1);
        F("ALPHA_SERVERLIST_IMPOSSIBLE", 0.7f);
        V("COLOR_SERVERLIST_IMPOSSIBLE", 0.3f, 0.3f, 0.3f);
        F("ALPHA_SERVERLIST_ICON_NONPURE", 0.5f);

        // item: server info
        V("COLOR_SERVERINFO_NAME", 1, 1, 1);
        V("COLOR_SERVERINFO_IP", 0.4f, 0.4f, 0.7f);

        // item: skin list
        V("COLOR_SKINLIST_TITLE", 1, 1, 1);
        V("COLOR_SKINLIST_AUTHOR", 0.4f, 0.4f, 0.7f);

        // item: demo list
        V("COLOR_DEMOLIST_SUBDIR", 0.5f, 0.5f, 0.5f);

        // item: screenshot list
        V("COLOR_SCREENSHOTLIST_SUBDIR", 0.5f, 0.5f, 0.5f);

        // item: slider
        S("GFX_SLIDER", "slider");
        V("COLOR_SLIDER_N", 1, 1, 1);
        V("COLOR_SLIDER_C", 1, 1, 1);
        V("COLOR_SLIDER_F", 1, 1, 1);
        V("COLOR_SLIDER_D", 1, 1, 1);
        V("COLOR_SLIDER_S", 1, 1, 1);
        F("WIDTH_SLIDERTEXT", 0.333333333333f);
    }

    private void F(string key, float v) => _floats[key] = v;
    private void V(string key, float x, float y, float z) => _vectors[key] = new SkinVec(x, y, z);
    private void S(string key, string v) => _strings[key] = v;
}
