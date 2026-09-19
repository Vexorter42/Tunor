using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Nyx.Services;

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
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Downloads all rule sets that have a SourceUrl into <see cref="Paths.RulesetsDir"/>.
    /// If <paramref name="mirrorPrefix"/> is non-empty, it's prepended to the URL
    /// (e.g. "https://ghproxy.net/" for GitHub-proxy access).
    /// Also updates each group's LocalPath so the caller can then save rules.json.
    /// </summary>
    public static async Task<List<RulesetDownloadResult>> DownloadAllAsync(
        IEnumerable<RuleGroup> groups,
        string? mirrorPrefix = null)
    {
        Directory.CreateDirectory(Paths.RulesetsDir);
        var results = new List<RulesetDownloadResult>();

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
            var effectiveUrl = string.IsNullOrWhiteSpace(mirrorPrefix)
                ? url
                : mirrorPrefix.TrimEnd('/') + "/" + url;

            try
            {
                using var resp = await Client.GetAsync(effectiveUrl);
                if (!resp.IsSuccessStatusCode)
                {
                    results.Add(new RulesetDownloadResult
                    {
                        Tag = g.Tag, Url = effectiveUrl, LocalPath = relPath, Ok = false,
                        Message = $"HTTP {(int)resp.StatusCode}",
                    });
                    continue;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0)
                {
                    results.Add(new RulesetDownloadResult
                    {
                        Tag = g.Tag, Url = effectiveUrl, LocalPath = relPath, Ok = false,
                        Message = "empty response",
                    });
                    continue;
                }
                await File.WriteAllBytesAsync(absPath, bytes);

                // Convert group to local
                g.Type = "local";
                g.Path = relPath;
                if (string.IsNullOrEmpty(g.SourceUrl)) g.SourceUrl = url;

                results.Add(new RulesetDownloadResult
                {
                    Tag = g.Tag, Url = effectiveUrl, LocalPath = relPath, Ok = true,
                    Message = $"{bytes.Length} bytes",
                });
            }
            catch (Exception ex)
            {
                results.Add(new RulesetDownloadResult
                {
                    Tag = g.Tag, Url = effectiveUrl, LocalPath = relPath, Ok = false,
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
