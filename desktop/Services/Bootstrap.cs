using System;
using System.IO;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// What has to be true before the first window opens.
///
/// A Windows install arrives with its settings, its rules and a starting config, because
/// the installer puts them there. A macOS one is a bundle dropped into Applications and
/// an empty folder in Application Support, so the same things are made here instead.
/// </summary>
public static class Bootstrap
{
    public static void Run()
    {
        // The shared services ask EngineState whether the engine is up, and send their
        // running commentary through it; here both are this app's own.
        EngineState.Provide(() => EngineService.IsRunning,
            (text, problem) => EngineLog.Add((problem ? "[!] " : "[i] ") + text));

        var settings = EnsureSettings();
        EnsureConfig(settings);
    }

    private static AppSettings EnsureSettings()
    {
        var settings = SettingsService.Load();     // defaults when the file is missing
        if (File.Exists(Paths.SettingsJson)) return settings;

        // TUN wants privileges this app does not have until the engine's service is
        // installed, and an engine told to raise an interface it may not raise does not
        // start at all. So a fresh macOS install begins on the proxy, which needs
        // nothing; the Home page explains the service, and TUN is a switch away after.
        if (OperatingSystem.IsMacOS()) settings.Tun = false;

        try { SettingsService.Save(settings); } catch { /* reported on the next save */ }
        return settings;
    }

    private static void EnsureConfig(AppSettings settings)
    {
        if (File.Exists(Paths.ConfigJson)) return;
        // Nothing is configured yet, so this is a config that routes everything direct —
        // but it exists, which means "Запустить" works and shows a running engine rather
        // than a file-not-found.
        try { ConfigGenerator.Generate(); } catch { /* the Home page will show the failure */ }
    }
}
