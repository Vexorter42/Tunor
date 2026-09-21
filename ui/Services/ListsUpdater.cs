using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nyx.Services;

/// <summary>
/// Refreshes the downloaded rule lists once they are a week old, in the background.
/// Lists are fetched once and then went stale forever — the upstream project updates
/// them far more often than anyone remembers to press the button.
/// </summary>
public static class ListsUpdater
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public static async Task RunIfDueAsync()
    {
        try
        {
            var s = SettingsService.Load();
            if (!s.AutoUpdateLists) return;
            if (s.ListsUpdatedAt is { } at && DateTime.UtcNow - at < Interval) return;

            var groups = RulesService.Load();
            if (!groups.Any(g => g.IsRemote || !string.IsNullOrEmpty(g.SourceUrl))) return;

            var results = await RulesetDownloader.DownloadAllAsync(groups, UpdateConfig.Load().Mirror);
            var ok = results.Count(r => r.Ok);
            if (ok == 0)
            {
                // Offline, or the mirror is down. Leave the date alone: retry next launch.
                ProcessService.Note("фоновое обновление списков не удалось — попробую при следующем запуске", true);
                return;
            }

            RulesService.Save(groups);
            ConfigGenerator.Generate();
            MarkUpdated();

            // The engine reloads local rule-set files when they change on disk, so a
            // running tunnel picks the new lists up without a restart.
            ProcessService.Note(ok == results.Count
                ? $"списки правил обновлены ({ok})"
                : $"списки правил обновлены: {ok} из {results.Count}");
        }
        catch (Exception ex)
        {
            ProcessService.Note("обновление списков: " + ex.Message, true);
        }
    }

    /// <summary>Records a successful download, from here or from a manual update.</summary>
    public static void MarkUpdated()
    {
        try
        {
            // Re-read rather than reuse: settings may have changed during the download.
            var s = SettingsService.Load();
            s.ListsUpdatedAt = DateTime.UtcNow;
            SettingsService.Save(s);
        }
        catch { }
    }
}
