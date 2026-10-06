using System;

namespace Tunor.Services;

/// <summary>
/// Whether the engine is up — asked by the shared code, answered by whichever front end
/// is running.
///
/// Starting and stopping the engine is the one job that cannot be shared: Windows needs
/// its console control API to stop it cleanly, and everywhere else a signal does. So the
/// services that only need to *know* ask here, and each app plugs in its own answer at
/// startup. Without this seam they would have to reference the Windows implementation,
/// which is exactly what a port cannot do.
/// </summary>
public static class EngineState
{
    private static Func<bool>? _isRunning;
    private static Action<string, bool>? _note;

    /// <summary>Set once, at startup, by the app that owns the engine.</summary>
    public static void Provide(Func<bool> isRunning, Action<string, bool>? note = null)
    {
        _isRunning = isRunning;
        _note = note;
    }

    /// <summary>
    /// A line for whatever the app shows the user as a running commentary — the log on
    /// Windows, the log page elsewhere. Dropped when nothing is listening, because a
    /// background list update must not fail over a message with nowhere to go.
    /// </summary>
    public static void Note(string text, bool problem = false)
    {
        try { _note?.Invoke(text, problem); } catch { }
    }

    /// <summary>
    /// True when the engine is running. Without a provider the answer is "no", which is
    /// the safe way round: a check that needs the engine says so rather than hanging on
    /// a port that nothing is listening to.
    /// </summary>
    public static bool IsRunning
    {
        get
        {
            try { return _isRunning?.Invoke() ?? false; }
            catch { return false; }
        }
    }
}
