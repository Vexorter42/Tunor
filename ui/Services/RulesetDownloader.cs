using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tunor.Services;

public class RulesetDownloadResult
{
    public string Tag { get; init; } = "";
    public string Url { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
}

public static class RulesetDownloader
{
    /// <summary>
    /// Downloads all rule sets that have a SourceUrl into <see cref="Paths.RulesetsDir"/>.
    /// Each comes from GitHub, and through the mirror (<paramref name="mirrorPrefix"/>, e.g.
    /// "https://ghproxy.net/") only if GitHub does not answer — see <see cref="GitHubFetch"/>.
    /// An empty mirror means GitHub only.
    /// Also updates each group's LocalPath so the caller can then save rules.json.
    /// </summary>
    public static async Task<List<RulesetDownloadResult>> DownloadAllAsync(
        IEnumerable<RuleGroup> groups,
        string? mirrorPrefix = null)
    {
        Directory.CreateDirectory(Paths.RulesetsDir);
        var results = new List<RulesetDownloadResult>();

        // Every list that cannot be fetched costs the time of every way there is to try. With
        // two in a row failing, the rest would fail the same way — nobody has the network or
        // GitHub and every mirror are down — so they are skipped instead of waited for.
        var failedInARow = 0;

        foreach (var g in groups)
        {
            // Only groups with a known source URL and either remote or local type.
            var url = !string.IsNullOrEmpty(g.SourceUrl) ? g.SourceUrl
                    : g.IsRemote ? g.Url
                    : "";
            if (string.IsNullOrEmpty(url)) continue;

            var fname = url.Split('/').Last();
            if (string.IsNullOrEmpty(fname)) fname = g.Tag + ".srs";
            // Path is relative to sing-box's CWD which is the build/ folder.
            // The rulesets live one level up in data/rulesets/.
            var relPath = "../data/rulesets/" + fname;
            var absPath = Path.Combine(Paths.RulesetsDir, fname);

            if (failedInARow >= 2)
            {
                results.Add(new RulesetDownloadResult
                {
                    Tag = g.Tag, Url = url, LocalPath = relPath, Ok = false,
                    Message = "пропущено: ни GitHub, ни зеркала не отвечают",
                });
                continue;
            }

            try
            {
                var bytes = await GitHubFetch.GetBytesAsync(url, mirrorPrefix);
                if (bytes.Length == 0)
                {
                    results.Add(new RulesetDownloadResult
                    {
                        Tag = g.Tag, Url = url, LocalPath = relPath, Ok = false,
                        Message = "empty response",
                    });
                    continue;
                }
                await File.WriteAllBytesAsync(absPath, bytes);
                failedInARow = 0;

                // Convert group to local
                g.Type = "local";
                g.Path = relPath;
                if (string.IsNullOrEmpty(g.SourceUrl)) g.SourceUrl = url;

                results.Add(new RulesetDownloadResult
                {
                    Tag = g.Tag, Url = url, LocalPath = relPath, Ok = true,
                    Message = $"{bytes.Length} bytes",
                });
            }
            catch (Exception ex)
            {
                // Only "nobody answered" counts: a list that is simply gone says nothing about the next.
                if (ex is GitHubFetch.FetchFailedException { Unreachable: true }) failedInARow++;
                results.Add(new RulesetDownloadResult
                {
                    Tag = g.Tag, Url = url, LocalPath = relPath, Ok = false,
                    Message = ex.Message,
                });
            }
        }

        return results;
    }

    public static string FormatSummary(IEnumerable<RulesetDownloadResult> results)
    {
        var sb = new StringBuilder();
        var ok = 0; var fail = 0;
        foreach (var r in results)
        {
            var mark = r.Ok ? "✓" : "✗";
            sb.AppendLine($"{mark} {r.Tag}  ({r.Message})");
            if (r.Ok) ok++; else fail++;
        }
        sb.AppendLine();
        sb.AppendLine($"Успешно: {ok}, ошибок: {fail}");
        return sb.ToString();
    }
}
