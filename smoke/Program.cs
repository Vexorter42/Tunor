using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Tunor.Desktop;
using Tunor.Desktop.Services;
using Tunor.Services;
using Tunor.Desktop.Views;

namespace Tunor.Smoke;

/// <summary>
/// Opens every page, with no screen, and says which ones threw.
///
/// A page is built, shown inside a window so it attaches to a visual tree, and laid out
/// — attaching is the moment most of these faults appear, because that is when styles
/// resolve and the AttachedToVisualTree handlers run.
/// </summary>
internal static class Program
{
    private static readonly (string Name, Func<Control> Make)[] Pages =
    {
        ("Главная", () => new HomeView()),
        ("Настройки", () => new SettingsView()),
        ("Правила", () => new RulesView()),
        ("Конфиги", () => new ConfigsView()),
        ("Туннели", () => new TunnelsView()),
        ("Приложения", () => new AppsView()),
        ("Соединения", () => new ConnectionsView()),
        ("Логи", () => new LogsView()),
        ("Автозапуск", () => new AutostartView()),
    };

    private static readonly (string Name, Func<Window> Make)[] Dialogs =
    {
        ("Окно: группа правил", () => new GroupWindow()),
        ("Окно: запущенные программы", () => new ProcessPickerWindow()),
        ("Окно: свой VPN", () => new AddVpnWindow()),
        ("Окно: подтверждение", () => new ConfirmWindow("Заголовок", "Текст")),
    };

    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(_ =>
            {
                foreach (var (name, make) in Pages) Check(name, () => Host(make()), failures);
                foreach (var (name, make) in Dialogs) Check(name, () => Show(make()), failures);
            })
            .SetupWithoutStarting();

        Programs();
        Swap(failures);
        Autostart(failures);
        Live();
        ConfigCheck(failures);

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine($"все {Pages.Length + Dialogs.Length} экранов открылись");
            return 0;
        }
        Console.WriteLine($"не открылось: {failures.Count}");
        foreach (var f in failures) Console.WriteLine("  " + f);
        return 1;
    }

    /// <summary>
    /// What the "выбрать запущенную" list would show. Printed rather than asserted: the
    /// names are read off this machine, and whether they are the right ones is a thing
    /// to look at, not a thing to compare against a constant.
    /// </summary>
    private static void Programs()
    {
        try
        {
            var all = ProcessList.CollectAsync().GetAwaiter().GetResult();
            var apps = all.Where(e => e.IsApp).ToList();
            Console.WriteLine();
            Console.WriteLine($"программ видно: {apps.Count} своих из {all.Count} всего");
            foreach (var e in apps.Take(8))
                Console.WriteLine($"    {e.Title}  [{string.Join(", ", e.Names.Take(3))}"
                                  + (e.Names.Count > 3 ? ", …]" : "]"));
        }
        catch (Exception ex) { Console.WriteLine("список программ не собрался: " + ex.Message); }
    }

    /// <summary>
    /// The bundle swap, against a bundle made for the purpose. This is the one step of
    /// updating that cannot be undone by trying again — it moves the running app out of
    /// the way — so the script is run here against a throwaway copy, with a pid that has
    /// already exited, and the result checked.
    /// </summary>
    private static void Swap(List<string> failures)
    {
        if (OperatingSystem.IsLinux()) { SwapFile(failures); return; }
        if (!OperatingSystem.IsMacOS()) return;
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "tunor-swap-check");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var oldApp = Path.Combine(root, "Old", "Tunor.app");
            var newApp = Path.Combine(root, "New", "Tunor.app");
            Directory.CreateDirectory(Path.Combine(oldApp, "Contents", "MacOS"));
            Directory.CreateDirectory(Path.Combine(newApp, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(oldApp, "Contents", "MacOS", "mark"), "старая");
            File.WriteAllText(Path.Combine(newApp, "Contents", "MacOS", "mark"), "новая");

            var make = typeof(UpdateService).GetMethod("SwapScript",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (make == null) { Console.WriteLine("  ✗ подмена: SwapScript не найден"); failures.Add("SwapScript не найден"); return; }

            // A pid that has certainly exited, so the script does not wait for anything.
            var text = (string)make.Invoke(null, new object[] { 999999, newApp, oldApp })!;
            var script = Path.Combine(root, "swap.sh");
            File.WriteAllText(script, text);

            var p = Process.Start(new ProcessStartInfo("/bin/bash", $"\"{script}\"")
            { RedirectStandardError = true, RedirectStandardOutput = true })!;
            p.WaitForExit(60_000);

            var mark = Path.Combine(oldApp, "Contents", "MacOS", "mark");
            var landed = File.Exists(mark) ? File.ReadAllText(mark) : "ничего";
            if (landed == "новая" && !Directory.Exists(oldApp + ".old"))
                Console.WriteLine("  ✓ подмена бандла: новая версия встала на место старой");
            else
            {
                Console.WriteLine($"  ✗ подмена бандла: на месте оказалась {landed}");
                failures.Add("подмена бандла: на месте " + landed);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ подмена бандла: " + ex.Message);
            failures.Add("подмена бандла — " + ex.Message);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// What the app sees of a running engine, through its own services: the state the
    /// buttons act on, and the connection list the page draws. Printed, because the
    /// answer depends on what is running right now.
    ///
    /// Run on a pool thread on purpose. Waiting on these from the thread that set up
    /// Avalonia deadlocks: their continuations go back to the dispatcher, and the
    /// dispatcher is not running — there is no app loop here.
    /// </summary>
    /// <summary>
    /// The Linux swap: one file, because an AppImage is the whole program in one. Same
    /// reasoning as the macOS check above — this is the step that cannot be retried, so
    /// it is run here against a file nobody minds losing.
    /// </summary>
    private static void SwapFile(List<string> failures)
    {
        Console.WriteLine();
        var root = Path.Combine(Path.GetTempPath(), "tunor-swap-check");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "Tunor.AppImage");
            var fresh = Path.Combine(root, "downloaded.AppImage");
            File.WriteAllText(target, "старая");
            File.WriteAllText(fresh, "новая");

            var make = typeof(UpdateService).GetMethod("SwapFileScript",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (make == null)
            {
                Console.WriteLine("  ✗ подмена файла: SwapFileScript не найден");
                failures.Add("SwapFileScript не найден");
                return;
            }

            var text = (string)make.Invoke(null, new object[] { 999999, fresh, target })!;
            var script = Path.Combine(root, "swap.sh");
            File.WriteAllText(script, text);

            var p = Process.Start(new ProcessStartInfo("/bin/sh", $"\"{script}\"")
            { RedirectStandardError = true, RedirectStandardOutput = true })!;
            p.WaitForExit(60_000);

            var landed = File.Exists(target) ? File.ReadAllText(target).Trim() : "ничего";
            var tidy = !File.Exists(target + ".old");
            if (landed == "новая" && tidy)
                Console.WriteLine("  ✓ подмена файла: новая версия встала на место старой");
            else
            {
                Console.WriteLine($"  ✗ подмена файла: на месте оказалась {landed}"
                                  + (tidy ? "" : ", и остался .old"));
                failures.Add("подмена файла: на месте " + landed);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ подмена файла: " + ex.Message);
            failures.Add("подмена файла — " + ex.Message);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// Autostart, switched on and off again. Only where it can be asked back: systemd
    /// and launchd both answer whether a unit is enabled, and a file written without
    /// anyone being told about it looks exactly like one that works.
    /// </summary>
    private static void Autostart(List<string> failures)
    {
        if (!AutostartService.Supported) return;
        Console.WriteLine();
        var was = AutostartService.Enabled;
        try
        {
            var (ok, msg) = AutostartService.Enable();
            Console.WriteLine($"    включаю:  {(ok ? "получилось" : "не вышло — " + msg)}");
            Console.WriteLine($"    записано: {AutostartService.Enabled}");

            if (OperatingSystem.IsLinux())
                Console.WriteLine("    systemd говорит: " + Ask("systemctl", "--user is-enabled tunor.service"));

            if (ok && !AutostartService.Enabled)
            {
                Console.WriteLine("  ✗ автозапуск: сказал, что включил, а файла нет");
                failures.Add("автозапуск включился только на словах");
            }
            else Console.WriteLine("  ✓ автозапуск: включается и виден системе");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ автозапуск: " + ex.Message);
            failures.Add("автозапуск — " + ex.Message);
        }
        finally
        {
            // Left as it was found: this is somebody's machine.
            if (!was) try { AutostartService.Disable(); } catch { }
        }
    }

    /// <summary>One line of output from a command, or why there was none.</summary>
    private static string Ask(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p == null) return "не запустилось";
            var outp = p.StandardOutput.ReadToEnd().Trim();
            var err = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return outp.Length > 0 ? outp : err.Length > 0 ? err : "(пусто)";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static void Live()
    {
        Console.WriteLine();
        Console.WriteLine("состояние, как его видит программа:");
        var done = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine($"    EngineService.IsRunning:         {EngineService.IsRunning}");
                Console.WriteLine($"    EngineService.PrivilegedRunning: {EngineService.PrivilegedRunning}");
                Console.WriteLine($"    ServiceClient.Paired:            {ServiceClient.Paired}");
                var said = await ServiceClient.StatusAsync();
                Console.WriteLine($"    служба говорит:                  {said ?? "не отвечает"}");

                var set = SettingsService.Load();
                Console.WriteLine($"    контроллер:                      порт {set.ControllerPort}, "
                                  + (string.IsNullOrEmpty(set.ControllerSecret) ? "секрета нет" : "секрет есть"));

                // The release manifest carries both systems; this is the proof that each
                // one picks its own half, run on each.
                var up = await UpdateService.CheckAsync();
                Console.WriteLine($"    обновление:                      "
                    + (up.Error is { Length: > 0 } e ? "ошибка: " + e
                       : $"установлено {up.Current}, в релизе {up.Latest}"
                         + (up.Available ? " — есть новее" : " — это и есть последняя")));

                var snap = await ConnectionsService.FetchAsync();
                Console.WriteLine(snap == null
                    ? "    ConnectionsService:              NULL — страница покажет пусто"
                    : $"    ConnectionsService:              {snap.Connections.Count} соединений");
                if (snap != null)
                    foreach (var c in snap.Connections.Take(5))
                        Console.WriteLine($"        {c.Process,-24} {c.User,-6} {c.Outbound,-12} {c.Host}");
            }
            catch (Exception ex) { Console.WriteLine("    упало: " + ex.Message); }
        }).Wait(TimeSpan.FromSeconds(40));
        if (!done) Console.WriteLine("    не ответило за 40 с");
    }

    /// <summary>
    /// Rebuilds config.json the way a launch does, and shows what the engine and the
    /// service will each be handed. The cache path is the point: relative in the file so
    /// it stays portable, absolute by the time the service sees it, because launchd
    /// starts the service from a directory nothing can write to.
    /// </summary>
    private static void ConfigCheck(List<string> failures)
    {
        Console.WriteLine();
        try
        {
            // Read-only unless asked: this runs against whatever install it finds, and a
            // check that quietly rewrites the user's config.json is a trap, not a check.
            if (Environment.GetEnvironmentVariable("TUNOR_SMOKE_WRITE") == "1")
            {
                ConfigGenerator.EnsureCompatible();
                Console.WriteLine("    (config.json пересобран — TUNOR_SMOKE_WRITE=1)");
            }
            if (!File.Exists(Paths.ConfigJson))
            {
                Console.WriteLine("    config.json ещё нет — проверять нечего");
                return;
            }
            var text = File.ReadAllText(Paths.ConfigJson);
            var exp = System.Text.Json.Nodes.JsonNode.Parse(text)?["experimental"];
            var inFile = exp?["cache_file"]?["path"]?.GetValue<string>() ?? "нет";
            Console.WriteLine($"    кэш в config.json:    {inFile}");

            var abs = typeof(ServiceClient).GetMethod("Absolutise",
                BindingFlags.NonPublic | BindingFlags.Static);
            var sent = (string?)abs?.Invoke(null, new object[] { text }) ?? "";
            var forService = System.Text.Json.Nodes.JsonNode.Parse(sent)
                ?["experimental"]?["cache_file"]?["path"]?.GetValue<string>() ?? "нет";
            Console.WriteLine($"    кэш для службы:       {forService}");

            if (inFile == "нет" || !Path.IsPathRooted(forService))
            {
                Console.WriteLine("  ✗ путь кэша не абсолютный — служба опять упрётся в read-only");
                failures.Add("путь кэша для службы не абсолютный");
            }
            else Console.WriteLine("  ✓ кэш: в файле относительный, службе уходит абсолютный");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ конфиг не собрался: " + ex.Message);
            failures.Add("конфиг не собрался — " + ex.Message);
        }
    }

    private static void Check(string name, Action act, List<string> failures)
    {
        try
        {
            act();
            Console.WriteLine($"  ✓ {name}");
        }
        catch (Exception ex)
        {
            // The real cause is usually two or three levels down a binding or a loader.
            var deepest = ex;
            while (deepest.InnerException != null) deepest = deepest.InnerException;
            Console.WriteLine($"  ✗ {name}: {deepest.GetType().Name}: {deepest.Message}");
            failures.Add($"{name} — {deepest.Message}");
        }
    }

    /// <summary>A page, inside a window, laid out: the same path the app takes.</summary>
    private static void Host(Control page)
    {
        var w = new Window { Width = 1100, Height = 760, Content = page };
        Show(w);
    }

    private static void Show(Window w)
    {
        w.Show();
        w.Measure(new Size(1100, 760));
        w.Arrange(new Rect(0, 0, 1100, 760));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        w.Close();
    }
}
