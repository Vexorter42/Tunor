using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Nyx.Services;

public enum RuleItemKind { Domain, ProcessName }

public class RuleGroup
{
    public string Tag { get; set; } = "";
    public string Type { get; set; } = "inline";

    public RuleItemKind ItemKind { get; set; } = RuleItemKind.Domain;
    public ObservableCollection<string> Items { get; set; } = new();

    public string Format { get; set; } = "binary";
    public string Url { get; set; } = "";
    public string UpdateInterval { get; set; } = "1d";

    public string Path { get; set; } = "";
    public string SourceUrl { get; set; } = "";

    public JsonObject? Original { get; set; }

    public bool IsInline => string.Equals(Type, "inline", StringComparison.OrdinalIgnoreCase);
    public bool IsRemote => string.Equals(Type, "remote", StringComparison.OrdinalIgnoreCase);
    public bool IsLocal  => string.Equals(Type, "local",  StringComparison.OrdinalIgnoreCase);

    public string KindBadge => IsRemote ? "remote"
        : IsLocal ? "local"
        : (ItemKind == RuleItemKind.ProcessName ? "proc" : "dom");
}

public static class RulesService
{
    public static List<RuleGroup> Load()
    {
        var result = new List<RuleGroup>();
        if (!File.Exists(Paths.RulesJson)) return result;

        try
        {
            var json = File.ReadAllText(Paths.RulesJson);
            var root = JsonNode.Parse(json) as JsonArray;
            if (root == null) return result;

            foreach (var item in root)
            {
                if (item is not JsonObject obj) continue;
                var group = new RuleGroup
                {
                    Tag = obj["tag"]?.GetValue<string>() ?? "",
                    Type = obj["type"]?.GetValue<string>() ?? "inline",
                    Original = (JsonObject)obj.DeepClone(),
                };

                if (group.IsRemote)
                {
                    group.Format = obj["format"]?.GetValue<string>() ?? "binary";
                    group.Url = obj["url"]?.GetValue<string>() ?? "";
                    group.UpdateInterval = obj["update_interval"]?.GetValue<string>() ?? "1d";
                    group.SourceUrl = group.Url;
                }
                else if (group.IsLocal)
                {
                    group.Format = obj["format"]?.GetValue<string>() ?? "binary";
                    group.Path = obj["path"]?.GetValue<string>() ?? "";
                    group.SourceUrl = obj["_source_url"]?.GetValue<string>() ?? "";
                }
                else
                {
                    if (obj["rules"] is JsonArray rules && rules.Count > 0 && rules[0] is JsonObject ro)
                    {
                        if (ro["process_name"] is JsonArray procs)
                        {
                            group.ItemKind = RuleItemKind.ProcessName;
                            foreach (var p in procs)
                            {
                                var s = p?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(s)) group.Items.Add(s!);
                            }
                        }
                        else if (ro["domain"] is JsonArray domains)
                        {
                            group.ItemKind = RuleItemKind.Domain;
                            foreach (var d in domains)
                            {
                                var s = d?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(s)) group.Items.Add(s!);
                            }
                        }
                    }
                }
                result.Add(group);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RulesService.Load: {ex.Message}");
        }
        return result;
    }

    public static void Save(IEnumerable<RuleGroup> groups)
    {
        var arr = new JsonArray();
        foreach (var g in groups)
        {
            JsonObject item = g.Original != null ? (JsonObject)g.Original.DeepClone() : new JsonObject();
            item["type"] = g.Type;
            item["tag"] = g.Tag;

            if (g.IsRemote)
            {
                item["format"] = g.Format;
                item["url"] = g.Url;
                item["update_interval"] = g.UpdateInterval;
                item.Remove("rules"); item.Remove("path"); item.Remove("_source_url");
            }
            else if (g.IsLocal)
            {
                item["format"] = g.Format;
                item["path"] = g.Path;
                if (!string.IsNullOrEmpty(g.SourceUrl)) item["_source_url"] = g.SourceUrl;
                item.Remove("rules"); item.Remove("url"); item.Remove("update_interval");
            }
            else
            {
                var itemsArr = new JsonArray();
                foreach (var s in g.Items) itemsArr.Add(s);
                var ruleObj = new JsonObject();
                ruleObj[g.ItemKind == RuleItemKind.ProcessName ? "process_name" : "domain"] = itemsArr;
                item["rules"] = new JsonArray(ruleObj);
                item.Remove("format"); item.Remove("url"); item.Remove("update_interval");
                item.Remove("path"); item.Remove("_source_url");
            }
            arr.Add(item);
        }
        File.WriteAllText(Paths.RulesJson, arr.ToJsonString(JsonOpts));
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Regenerates build/config.json from the data files (replaces SSnetCli).
    /// </summary>
    public static Task<(bool ok, string output)> ApplyAsync() => Task.Run<(bool ok, string output)>(() =>
    {
        try
        {
            ConfigGenerator.Generate();
            return (true, "config.json пересобран");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    });
}
