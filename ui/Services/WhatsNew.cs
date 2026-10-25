using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>
/// "What's new", shown once after the program has been updated.
///
/// An update used to end with the program restarting as if nothing had happened: what changed was
/// on a page of GitHub that nobody opens. The release's notes are fetched — from the file
/// <c>notes.md</c> attached to the release, and failing that from the manifest's short line — and
/// shown in a dialog the first time the new version opens.
///
/// Only after an upgrade, never on a first run (the wizard has its say then) and never going back.
/// What has not been shown is not marked as seen: with no network the dialog comes at the next start.
/// </summary>
public static class WhatsNew
{
    public sealed record Block(bool Heading, string Text);

    /// <summary>Where the notes come from; replaceable, so that the logic can be checked with no network.</summary>
    internal static Func<string, Task<string?>> Source = FetchNotesAsync;

    /// <summary>Whether this start is the first after an upgrade from a version that is known.</summary>
    public static bool Due(string? lastSeen, string current)
    {
        if (string.IsNullOrWhiteSpace(lastSeen)) return false;
        return Version.TryParse(current, out var now) && Version.TryParse(lastSeen, out var then) && now > then;
    }

    /// <summary>
    /// The notes to show, or null when there is nothing to show. Always brings the "seen" version up
    /// to date, except when the notes were due and could not be fetched.
    /// </summary>
    public static async Task<(string Version, IReadOnlyList<Block> Blocks)?> CheckAsync()
    {
        var settings = SettingsService.Load();
        var current = UpdateService.CurrentVersionString;
        if (settings.LastSeenVersion == current) return null;

        // Looking for notes is a request to GitHub like the search for updates, and is switched off with it.
        if (!Due(settings.LastSeenVersion, current) || !settings.AutoCheckUpdates)
        {
            MarkSeen(current);
            return null;
        }

        var text = await Source(current).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;          // later: nothing was shown, so nothing is marked

        var blocks = Format(text);
        if (blocks.Count == 0) { MarkSeen(current); return null; }
        return (current, blocks);
    }

    public static void MarkSeen(string version)
    {
        try
        {
            var s = SettingsService.Load();
            if (s.LastSeenVersion == version) return;
            s.LastSeenVersion = version;
            SettingsService.Save(s);
        }
        catch { }
    }

    private static async Task<string?> FetchNotesAsync(string version)
    {
        var cfg = UpdateConfig.Load();
        try
        {
            return await GitHubFetch.GetStringAsync(
                $"https://github.com/{cfg.Repo}/releases/download/v{version}/notes.md", cfg.Mirror).ConfigureAwait(false);
        }
        catch { /* a release made before the notes were attached to it */ }

        try
        {
            // Right after an update the latest release is this one, and its manifest has a line about it.
            var info = await UpdateService.CheckAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(info.Error) && info.Latest == version && !string.IsNullOrWhiteSpace(info.Notes))
                return info.Notes;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The release notes as headings and paragraphs. What is for the page on GitHub and not for a
    /// dialog is left out: the rules between sections, the table of files and, with the heading
    /// "Скачать", everything after it — the download table and how to open the program the first time.
    /// </summary>
    internal static IReadOnlyList<Block> Format(string markdown)
    {
        var blocks = new List<Block>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "---" || line.StartsWith('|')) continue;

            // a line that is a heading: **Like this**, and nothing else
            var heading = line.Length > 4 && line.StartsWith("**") && line.EndsWith("**")
                          && line.IndexOf("**", 2, StringComparison.Ordinal) == line.Length - 2;
            var text = Plain(line);
            if (text.Length == 0) continue;
            if (heading && text.StartsWith("Скачать", StringComparison.Ordinal)) break;
            blocks.Add(new Block(heading, text));
        }
        return blocks;
    }

    private static string Plain(string line)
    {
        line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");   // [text](link) → text
        return line.Replace("**", "").Replace("`", "").Trim();
    }
}
