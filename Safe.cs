using System;

namespace MonsterModAlerter;

/// <summary>
/// One read of remote memory, or the fallback.
///
/// Every memory object in this HUD can throw at arbitrary times - zoning, an entity despawning, a hot reload - and a
/// throw inside Render takes the whole overlay down, not just this plugin. So nothing reads the game without going
/// through here. Taken from CraftUntilHighlighted's Safe.
/// </summary>
internal static class Safe
{
    public static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// The same read, with the thing being read passed in rather than captured. Written as
    /// <c>Safe.Read(gameController, static g =&gt; g.InGame, false)</c> the lambda captures nothing and the call
    /// allocates nothing, which matters for reads made every frame.
    /// </summary>
    public static TValue Read<TState, TValue>(TState state, Func<TState, TValue> read, TValue fallback)
    {
        try
        {
            return read(state);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>For a call whose only purpose is its side effect, such as offering one entity to the rules.</summary>
    public static void Try(Action act)
    {
        try
        {
            act();
        }
        catch
        {
            // What could not be read this time is read again on the next scan or frame.
        }
    }
}
