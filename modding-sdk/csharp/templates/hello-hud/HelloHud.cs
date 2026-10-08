using System.Runtime.InteropServices;
using Vortex.Modding;

// The smallest useful mod: a speedometer in the bottom-left corner.
public static class HelloHud
{
    [UnmanagedCallersOnly(EntryPoint = "mod_init")]
    public static void Init() => Mod.Log("hello-hud loaded");

    [UnmanagedCallersOnly(EntryPoint = "mod_frame")]
    public static void Frame(float dt)
    {
        ScreenState screen = Mod.Screen;
        float y = screen.VirtualHeight - 40;

        Draw.Rect(8, y, 200, 32, Color.Rgba(0, 0, 0, 160));

        // A stack buffer and TryFormat, not string interpolation: this runs every frame, and a frame
        // that allocates is a frame the garbage collector may interrupt.
        Span<char> text = stackalloc char[32];
        "speed ".CopyTo(text);
        Mod.LocalPlayer.Speed.TryFormat(text[6..], out int written, "0");
        Draw.Text(Fonts.Default, 16, y + 6, 16, Color.White, text[..(6 + written)]);

        Draw.Flush();
    }
}
