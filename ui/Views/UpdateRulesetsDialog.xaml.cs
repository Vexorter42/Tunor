using System;
using System.Collections.Generic;
using System.Windows;
using Nyx.Services;

namespace Nyx.Views;

public partial class UpdateRulesetsDialog : Window
{
    private readonly List<RuleGroup> _groups;
    public bool AnyDownloaded { get; private set; }

    public UpdateRulesetsDialog(List<RuleGroup> groups)
    {
        InitializeComponent();
        _groups = groups;

        // Count how many groups have a source URL
        var candidates = 0;
        foreach (var g in _groups)
            if (!string.IsNullOrEmpty(g.SourceUrl) || g.IsRemote) candidates++;

        LogBox.Text = $"Найдено групп для обновления: {candidates}\n" +
                      $"Папка: data\\rulesets\\\n\n" +
                      "Нажми «Скачать», чтобы обновить их.";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        BtnDownload.IsEnabled = false;
        var mirror = MirrorBox.Text.Trim();
        LogBox.Text = "Качаем…\n";
        try
        {
            var results = await RulesetDownloader.DownloadAllAsync(_groups, mirror);
            LogBox.Text = RulesetDownloader.FormatSummary(results);

            foreach (var r in results)
                if (r.Ok) { AnyDownloaded = true; break; }
        }
        catch (Exception ex)
        {
            LogBox.Text += "\nОшибка: " + ex.Message;
        }
        finally
        {
            BtnDownload.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
