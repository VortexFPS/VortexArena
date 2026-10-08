// Port of nothing new: gl_draw.c's picture cache (LegacyPictureCatalog) and font slots
// (LegacyFontSlots) behind ILegacyDraw, with the drawing recorded (LegacyDrawList) instead of shown.
using VortexArena.Formats.Vfs;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Menu;

/// <summary>
/// What a menu program draws on when there is no window: a tool, a probe, the test suite. Pictures
/// are answered truthfully from the game data (existence and size from the image headers), every draw
/// call is recorded in <see cref="List"/>, and text is measured at a fixed advance per character,
/// since nothing here can rasterise a font - so a layout computed against it is the right SHAPE (the
/// menu's boxes are where they should be) with text that is the wrong width.
/// </summary>
public sealed class HeadlessMenuDraw : ILegacyDraw
{
    /// <summary>A proportional font's average advance, as a fraction of the character cell's width.</summary>
    public const float AdvancePerCell = 0.5f;

    public HeadlessMenuDraw(VirtualFileSystem files) => Pictures = new LegacyPictureCatalog(files);

    public LegacyPictureCatalog Pictures { get; }
    public LegacyFontSlots Fonts { get; } = new();
    /// <summary>The draw calls since <see cref="LegacyDrawList.Clear"/>; the owner clears it before each frame.</summary>
    public LegacyDrawList List { get; } = new();
    /// <summary>Calls received per member, over the object's lifetime.</summary>
    public Dictionary<string, long> Calls { get; } = new(StringComparer.Ordinal);

    private void Count(string member) => Calls[member] = Calls.GetValueOrDefault(member) + 1;

    public bool PictureExists(string name)
    {
        Count(nameof(PictureExists));
        return Pictures.Precache(name);
    }

    public void DefinePicture(string name, ReadOnlySpan<byte> jpeg) => Pictures.Define(name, jpeg);

    public QcVector ImageSize(string name)
    {
        Count(nameof(ImageSize));
        (int width, int height) = Pictures.Size(name);
        return new QcVector(width, height, 0);
    }

    public void FreePicture(string name) => Pictures.Free(name);

    public void Line(float width, QcVector from, QcVector to, QcVector color, float alpha, int flags)
    {
        Count(nameof(Line));
        List.Line(width, from, to, color, alpha, flags);
    }

    public QcVector Text(in LegacyText text)
    {
        Count(nameof(Text));
        return List.Text(text);
    }

    public void Picture(in LegacyPicture picture)
    {
        Count(nameof(Picture));
        List.Picture(picture);
    }

    public void Fill(QcVector position, QcVector size, QcVector color, float alpha, int flags)
    {
        Count(nameof(Fill));
        List.Fill(position, size, color, alpha, flags);
    }

    public void SetClipArea(float x, float y, float width, float height)
    {
        Count(nameof(SetClipArea));
        List.SetClip(x, y, width, height);
    }

    public void ResetClipArea()
    {
        Count(nameof(ResetClipArea));
        List.ResetClip();
    }

    public float StringWidth(string text, bool ignoreColorCodes, QcVector scale, int font, QcVector fontScale)
    {
        Count(nameof(StringWidth));
        if (string.IsNullOrEmpty(text)) return 0;
        int visible = text.Length;
        if (!ignoreColorCodes && text.Contains('^'))
        {
            System.Text.StringBuilder shown = new();
            LegacyTextColors.Walk(text, false, new LegacyColor(1, 1, 1, 1), null, shown);
            visible = shown.Length;
        }
        return visible * scale.X * (fontScale.X != 0 ? fontScale.X : 1) * AdvancePerCell;
    }

    public int FindFont(string name) => Fonts.Find(name);

    public int LoadFont(string name, string files, string sizes, int slot, float scale, float verticalOffset) =>
        Fonts.Load(name, files, slot, scale, verticalOffset);
}
