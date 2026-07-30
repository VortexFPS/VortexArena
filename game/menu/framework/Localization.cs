// Port of qcsrc/lib/i18n.qh (the `_()` / CTX runtime helpers) + the gettext APPLICATION step from
// darkplaces/prvm_edict.c (PRVM_PO_Load at progs load, prvm_edict.c:2631-2657) + the language swap from
// qcsrc/menu/menu.qc m_init (menu.qc:65-74). The pure PO/CTX logic lives in VortexArena.Common.Localization
// (PoCatalog, Ctx); this Godot-side facade owns the ACTIVE catalog + current language and exposes the menu's
// translation seam (Tr / Ctx / CtxTr), loading the .po through the asset VFS.
//
// Translation is keyed on the ENGLISH SOURCE string (the .po msgid IS the literal, incl. ^ color codes and a
// CTX^ prefix). So a menu label must pass the EXACT literal the QC `_()` used: the port's labels were ported
// verbatim from the QC `_()` text, so they match the .pot keys. en/""/dump => identity (no PO loaded).
using System;
using Godot;
using VortexArena.Formats.Vfs;
using VortexArena.Common.Localization;

namespace VortexArena.Game.Menu;

/// <summary>
/// The menu's localization facade — the C# successor to QuakeC's <c>_()</c> translation operator and the
/// <c>CTX</c>/<c>ZCTX</c> helpers (i18n.qh), backed by the active <see cref="PoCatalog"/>. The menu's text
/// helpers (<see cref="Ui"/>, <see cref="MenuScreen"/>, <see cref="Widgets"/>) route every user-facing string
/// through <see cref="Tr"/>, so a language change re-translates the whole front-end with no per-dialog edits —
/// exactly as the QC menu got it free from GMQCC's compile-time string-table rewrite.
/// </summary>
public static class Localization
{
    private static PoCatalog _catalog = PoCatalog.Empty;

    /// <summary>
    /// The active language id (the <c>prvm_language</c> cvar value), e.g. <c>"de"</c>; <c>"en"</c> (the default)
    /// for the untranslated English baseline. Mirrors i18n.qh's <c>prvm_language</c> global.
    /// </summary>
    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// Translate an English source string — the C# stand-in for <c>_("...")</c>. Returns the PO translation when
    /// one exists, else the source verbatim (the engine's behavior: only replaces the global when the lookup
    /// hits). A null/empty input passes through unchanged. For <c>en</c>/empty/<c>dump</c> the catalog is empty,
    /// so this is the identity.
    /// </summary>
    public static string Tr(string source)
    {
        if (string.IsNullOrEmpty(source))
            return source;
        return _catalog.Translate(source);
    }

    /// <summary>
    /// Strip a <c>PREFIX^</c> disambiguation context off a string WITHOUT translating — the C# stand-in for
    /// <c>CTX(s)</c>. Use when the literal carries a context prefix that should not be displayed but no PO lookup
    /// is wanted (e.g. a string already passed through <see cref="Tr"/> elsewhere).
    /// </summary>
    public static string Ctx(string source)
        => string.IsNullOrEmpty(source) ? source : VortexArena.Common.Localization.Ctx.Strip(source);

    /// <summary>
    /// Translate THEN strip a context prefix — the C# stand-in for <c>CTX(_("PREFIX^Text"))</c>, the most common
    /// QC idiom. The msgid (incl. the prefix) is looked up first, and the disambiguating <c>PREFIX^</c> is
    /// removed from the result for display. Faithful order: lookup with the prefix, strip after.
    /// </summary>
    public static string CtxTr(string source)
        => VortexArena.Common.Localization.Ctx.Strip(Tr(source));

    /// <summary>
    /// Switch the active language and (re)load its catalog through <paramref name="vfs"/> — the C# successor to
    /// the menu.qc language swap + the engine's <c>PRVM_PO_Load("%s.%s.po", "common.%s.po")</c>. Loads
    /// <c>common.&lt;id&gt;.po</c> (and, if present, <c>menu.dat.&lt;id&gt;.po</c> as the progs overlay so progs
    /// wins) into the active catalog. For <c>en</c>/empty/<c>dump</c> the catalog is cleared (identity), matching
    /// the engine where English has no real <c>common.en.po</c>. Tolerant: a missing/garbage file leaves the
    /// catalog empty (lookups fall through to the source), like <c>FS_LoadFile</c> returning NULL.
    /// </summary>
    public static void SetLanguage(string? id, VirtualFileSystem? vfs)
    {
        CurrentLanguage = string.IsNullOrEmpty(id) ? "en" : id!;

        if (VortexArena.Common.Localization.Ctx.IsIdentityLanguage(CurrentLanguage) || vfs is null)
        {
            _catalog = PoCatalog.Empty;
            return;
        }

        try
        {
            // Engine: common.<lang>.po loaded FIRST, the progs file SECOND so progs overrides. This Xonotic build
            // unifies all translations into common.*.po (no separate menu.dat.*.po ships), but we still overlay a
            // progs file when one is present, for faithfulness.
            string? common = ReadIfExists(vfs, $"common.{CurrentLanguage}.po");
            string? progs = ReadIfExists(vfs, $"menu.dat.{CurrentLanguage}.po")
                            ?? ReadIfExists(vfs, $"progs.dat.{CurrentLanguage}.po");
            _catalog = PoCatalog.Load(common, progs);

            int n = _catalog.Count;
            if (n > 0)
                GD.Print($"[Localization] language '{CurrentLanguage}': {n} translations loaded.");
            else
                GD.Print($"[Localization] language '{CurrentLanguage}': no .po found (untranslated; UI stays English).");
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[Localization] failed to load language '{CurrentLanguage}': {ex.Message}");
            _catalog = PoCatalog.Empty;
        }
    }

    private static string? ReadIfExists(VirtualFileSystem vfs, string path)
        => vfs.Exists(path) ? vfs.ReadText(path) : null;

    /// <summary>
    /// Load and parse <c>languages.txt</c> through the menu's asset VFS — the C# successor to
    /// <c>XonoticLanguageList_getLanguages</c> (it <c>fopen("languages.txt")</c>s the file). Returns the ordered
    /// list of languages, or an empty list when the file is absent (no content repo). Used by the language
    /// pickers (DialogSettingsUser / DialogFirstRun) so they're data-driven like QC rather than a hardcoded set.
    /// </summary>
    public static System.Collections.Generic.List<LanguageEntry> LoadLanguages()
    {
        VirtualFileSystem? vfs = MenuState.Vfs;
        if (vfs is null)
            return new System.Collections.Generic.List<LanguageEntry>();
        try
        {
            return vfs.Exists("languages.txt")
                ? LanguagesTxt.Parse(vfs.ReadText("languages.txt"))
                : new System.Collections.Generic.List<LanguageEntry>();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[Localization] failed to read languages.txt: {ex.Message}");
            return new System.Collections.Generic.List<LanguageEntry>();
        }
    }
}
