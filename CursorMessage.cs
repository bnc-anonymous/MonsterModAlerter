using System;
using System.Numerics;
using ExileCore;
using Color = SharpDX.Color;
using Graphics = ExileCore.Graphics;

namespace MonsterModAlerter;

/// <summary>
/// The last message, written under the mouse cursor for a few seconds and faded over its last second. One at a time: a
/// new message replaces the one showing. Taken from CraftUntilHighlighted, which took it from AutoExpedition's
/// CursorWarning: warnings moved beside the cursor there because players did not notice them anywhere else. Messages
/// are a few words, not sentences.
/// </summary>
internal static class CursorMessage
{
    public enum Kind
    {
        Error,
        Success,
        Info,
    }

    /// <summary>How long a message stays up. AutoExpedition's cursor warnings default to 4 s as well.</summary>
    private const double ShownSeconds = 4d;

    /// <summary>AutoExpedition's warning red.</summary>
    private static readonly Color ErrorColour = Colours.FromArgb(255, 235, 90, 90);

    /// <summary>AutoExpedition's purple.</summary>
    private static readonly Color SuccessColour = Colours.FromArgb(255, 190, 120, 255);

    /// <summary>AutoExpedition's white (its common chest colour).</summary>
    private static readonly Color InfoColour = Colours.FromArgb(255, 245, 245, 245);

    private static (string Text, Kind Kind, DateTime At) _shown = ("", Kind.Info, DateTime.MinValue);

    /// <summary>Shows a message, replacing any before it.</summary>
    public static void Show(Kind kind, string text) => _shown = (text ?? "", kind, DateTime.UtcNow);

    /// <summary>Draws the message showing, if any.</summary>
    public static void Draw(Graphics graphics, GameController gameController)
    {
        if (_shown.Text.Length == 0)
            return;

        var age = (DateTime.UtcNow - _shown.At).TotalSeconds;

        if (age >= ShownSeconds)
            return;

        var cursor = Safe.Read(gameController,
            static g => new Vector2(g.IngameState.MousePosX, g.IngameState.MousePosY), new Vector2(-1f, -1f));
        var window = Safe.Read(gameController, static g => g.Window.GetWindowRectangle(), default);

        if (cursor.X < 0f || window.Width <= 0f)
            return;

        var size = graphics.MeasureText(_shown.Text);

        // Below and right of the pointer, kept inside the window so a message raised near an edge is not cut off.
        var at = new Vector2(
            Math.Clamp(cursor.X + 16f, 0f, Math.Max(0f, window.Width - size.X - 4f)),
            Math.Clamp(cursor.Y + 44f, 0f, Math.Max(0f, window.Height - size.Y - 4f)));

        // Fades over its last second rather than vanishing mid-read.
        var alpha = (int)(255 * Math.Clamp(ShownSeconds - age, 0d, 1d));

        var colour = _shown.Kind switch
        {
            Kind.Error => ErrorColour,
            Kind.Success => SuccessColour,
            _ => InfoColour,
        };

        graphics.DrawTextWithBackground(_shown.Text, at, Colours.FromArgb(alpha, colour),
            Colours.FromArgb(alpha * 200 / 255, Color.Black));
    }
}
