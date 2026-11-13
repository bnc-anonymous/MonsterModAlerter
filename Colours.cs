using Color = SharpDX.Color;

namespace MonsterModAlerter;

/// <summary>
/// System.Drawing.Color's constructors for SharpDX.Color, which is the colour ExileApi draws and saves with. Alpha
/// comes first, as in System.Drawing, so colours copied from AutoExpedition's palette read the same here. The same
/// helper as CraftUntilHighlighted's.
/// </summary>
internal static class Colours
{
    public static Color FromArgb(int alpha, int red, int green, int blue) =>
        new((byte)red, (byte)green, (byte)blue, (byte)alpha);

    /// <summary>The same colour with a different alpha.</summary>
    public static Color FromArgb(int alpha, Color colour) => new(colour.R, colour.G, colour.B, (byte)alpha);
}
