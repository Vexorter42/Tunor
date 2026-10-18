using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>
/// Looks for a new version in the background, so the Home page can say so without being asked.
///
/// Until this existed the only way to learn that there was an update was to press «Проверить
/// обновления», which nobody does for a program that works. This asks on its own — shortly
/// after the app starts, then every few hours — and keeps the answer where every front end
/// can read it. It never opens a dialog and never complains: a check that cannot reach GitHub
/// is not news, it is tried again sooner and that is all.
/// </summary>
public static class UpdateWatcher
{
    /// <summary>The newer version that was found, or null — none, or not asked yet.</summary>
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Raised, from a background thread, whenever <see cref="Available"/> changes.</summary>
    public static event EventHandler? Changed;

    // Replaceable, so the behaviour can be checked without a network and without waiting hours.
    internal static Func<Task<UpdateInfo>> Source { get; set; } = UpdateService.CheckAsync;
    internal static Func<bool> Enabled { get; set; } = () => SettingsService.Load().AutoCheckUpdates;
    internal static TimeSpan FirstDelay { get; set; } = TimeSpan.FromSeconds(20);
    internal static TimeSpan Every { get; set; } = TimeSpan.FromHours(6);
    internal static TimeSpan AfterFailure { get; set; } = TimeSpan.FromMinutes(30);

    private static CancellationTokenSource? _cts;
    private static readonly object Gate = new();

    /// <summary>Starts the background checks. Calling it again does nothing.</summary>
    public static void Start()
    {
        CancellationToken token;
        lock (Gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        _ = Task.Run(() => Loop(token));
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private static async Task Loop(CancellationToken token)
    {
        try
        {
            // Not at the very start: the app has more urgent things to do with the network,
            // like bringing the tunnel up.
            await Task.Delay(FirstDelay, token);
            while (!token.IsCancellationRequested)
            {
                var asked = true;
                try
                {
                    // The setting is read every time, so switching it off takes effect at once.
                    if (Enabled()) asked = await CheckNow();
                }
                catch { asked = false; }
                await Task.Delay(asked ? Every : AfterFailure, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One check. False when it could not be made at all; the previous answer then stands.</summary>
    public static async Task<bool> CheckNow()
    {
        try
        {
            var info = await Source();
            if (info.Error is { Length: > 0 }) return false;
            Set(info.Available ? info : null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Records what a check found — for callers that made a check of their own.</summary>
    public static void Set(UpdateInfo? found)
    {
        var changed = Available?.Latest != found?.Latest;
        Available = found;
        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }
}
