using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
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

    private static readonly (string Name, Func<Control> Make)[] Dialogs =
    {
        ("Окно: группа правил", () => new GroupWindow()),
        ("Окно: запущенные программы", () => new ProcessPickerWindow()),
        ("Окно: свой VPN", () => new AddVpnWindow()),
        ("Окно: подтверждение", () => new ConfirmWindow("Заголовок", "Текст")),
        ("Окно: пароль к копии", () => PasswordWindow.ForSaving()),
        ("Окно: пароль к копии (открытие)", () => PasswordWindow.ForOpening("Пароль не подошёл")),
        ("Окно: что нового", () => new WhatsNewWindow("2.2.0", WhatsNew.Format("Текст.\n\n---\n\n**Заголовок**\n\nАбзац."))),
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
                Check("Главная: зелёный баннер «Обновиться»", Banner, failures);
                Check("Диалоги внутри окна: поверх приложения, ждут, закрываются, стопкой, без главного окна",
                      InWindowDialogs, failures);
                Check("Скачивание: сначала GitHub, потом зеркала, короткие таймауты, списки не ждут молчащую сеть",
                      FetchWays, failures);
                Check("Подмена после обновления: ждёт, просит завершиться, добивает — и только потом меняет файлы",
                      SwapScripts, failures);
                Check("Манифест: Intel и Apple Silicon читают каждый свою сборку, чужую и установщик Windows не получают",
                      ManifestParts, failures);
                Check("Журнал на диске: запись, перенос большого файла, хвост, очистка, фильтр для отчёта", LogFile, failures);
                Check("«Что нового»: только после обновления, заметки без разметки и без таблицы файлов, повтор при отсутствии сети",
                      WhatsNewChecks, failures);
                Check("Резервная копия: что входит, пароль, чужое остаётся своим, чужой файл не выйдет из папки",
                      BackupChecks, failures);
            })
            .SetupWithoutStarting();

        // TUNOR_SMOKE_ONLY=service runs just the link to the privileged service. The rest
        // changes things on this machine — autostart is switched on and back off — which
        // is the wrong thing to do to a Mac only to look at one connection.
        var which = Environment.GetEnvironmentVariable("TUNOR_SMOKE_ONLY");
        var only = which == "service";
        if (which != "screens")
        {
            if (!only)
            {
                Programs();
                Swap(failures);
                Autostart(failures);
            }
            ServiceLink(failures);
            Credentials(failures);
            UpdateWatch(failures);
            Live();
            if (!only) ConfigCheck(failures);
        }

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

    /// <summary>
    /// The certificate and key pairing makes, and the PFX that 1.x stored: each must come
    /// back out of the new reader as exactly what went in. A certificate that parses but
    /// carries somebody else's key would be accepted by nothing and explain nothing.
    /// </summary>
    private static void Credentials(List<string> failures)
    {
        Console.WriteLine();

        void Verify(string what, MutualTls.Identity? found, byte[] cert, byte[] modulus)
        {
            var key = found?.Key as Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters;
            var sameCert = found != null && found.Certificate.GetEncoded().SequenceEqual(cert);
            var sameKey = key != null && key.Modulus.ToByteArrayUnsigned().SequenceEqual(modulus);
            if (sameCert && sameKey)
                Console.WriteLine($"  ✓ {what}: сертификат и ключ вернулись теми же");
            else
            {
                Console.WriteLine($"  ✗ {what}: сертификат {(sameCert ? "тот" : "НЕ тот")}, ключ {(sameKey ? "тот" : "НЕ тот")}");
                failures.Add($"{what}: не то, что записали");
            }
        }

        var file = Path.Combine(Path.GetTempPath(), "tunor-smoke-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            // What pairing makes now: a certificate as bytes, a key as text, never together.
            var (der, keyPem) = ServiceClient.MakeCertificate();
            using var key = System.Security.Cryptography.RSA.Create();
            key.ImportFromPem(keyPem);
            File.WriteAllText(file,
                System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", der) + "\n" + keyPem + "\n");
            Verify("сопряжение: новая пара", MutualTls.ReadPem(file), der, key.ExportParameters(false).Modulus!);

            // What 1.x stored, made the way it made it.
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=Tunor", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var old = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var pfx = old.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
            var read = MutualTls.ReadPfx(pfx);
            Verify("старый PFX из 1.x", read, old.RawData, rsa.ExportParameters(false).Modulus!);

            // And what the migration writes from it must be readable again.
            if (read != null)
            {
                File.WriteAllText(file, read.ToPem());
                Verify("старый PFX, переписанный как PEM", MutualTls.ReadPem(file), old.RawData, rsa.ExportParameters(false).Modulus!);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ ключи: " + ex.Message);
            failures.Add("ключи — " + ex.Message);
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static UpdateInfo Fake(string latest) => new()
    {
        Available = true, Latest = latest, Current = "2.0.4", Kind = UpdateKind.Patch,
        Notes = "Исправления.\nВторая строка, которую баннер не показывает.",
    };

    /// <summary>
    /// The Home page with an update on offer: the banner is there, says which version, and
    /// its button is the accent one; and with nothing on offer it is gone again.
    /// </summary>
    /// <summary>
    /// What a dialog inside the window promises, on a stand-in for the main window: it opens
    /// over the app and switches it off; ShowDialog waits until it is closed; Esc closes it;
    /// a second dialog goes on top of the first and the app stays off until both are gone;
    /// and with the window hidden it opens in a window of its own instead of nowhere.
    /// </summary>
    private static void InWindowDialogs()
    {
        var page = new Button { Content = "страница" };
        var root = new Grid();
        root.Children.Add(page);
        var layers = new Grid();
        root.Children.Add(layers);
        var w = new Window { Width = 900, Height = 600, Content = root };
        w.Show();
        DialogHost.Attach(w, root, layers);
        Dispatcher.UIThread.RunJobs();

        void Jobs() => Dispatcher.UIThread.RunJobs();
        void Esc() => layers.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        // opens over the app, waits, closes
        var one = new ConfirmWindow("Удалить?", "Точно?");
        var t1 = one.ShowDialog(w);
        Jobs();
        if (layers.Children.Count != 1) throw new Exception("диалога нет на слое окна");
        if (!DialogHost.IsOpen) throw new Exception("IsOpen говорит, что диалога нет");
        if (page.IsHitTestVisible) throw new Exception("страница позади продолжает принимать мышь");
        if (t1.IsCompleted) throw new Exception("ShowDialog вернулся, не дождавшись закрытия");
        one.Close();
        Jobs();
        if (!t1.IsCompleted) throw new Exception("после Close() ShowDialog не завершился");
        if (!page.IsHitTestVisible || layers.Children.Count != 0 || DialogHost.IsOpen)
            throw new Exception("после закрытия страница не включилась или слой не опустел");

        // Esc closes it, and a confirmation closed that way is not a yes
        var two = new ConfirmWindow("Удалить?", "Точно?");
        var t2 = two.ShowDialog(w);
        Jobs();
        Esc();
        Jobs();
        if (!t2.IsCompleted) throw new Exception("Esc не закрыл диалог");
        if (two.Confirmed) throw new Exception("закрытие по Esc сошло за «да»");

        // stacked: the app stays off until both are gone
        var lower = new ConfirmWindow("Первый", "…");
        var tl = lower.ShowDialog(w);
        var upper = new ConfirmWindow("Второй", "…");
        var tu = upper.ShowDialog(w);
        Jobs();
        if (layers.Children.Count != 2) throw new Exception("два диалога — должно быть два слоя");
        upper.Close();
        Jobs();
        if (!tu.IsCompleted || tl.IsCompleted) throw new Exception("закрылся не тот диалог");
        if (page.IsHitTestVisible) throw new Exception("страница включилась, пока нижний диалог ещё открыт");
        lower.Close();
        Jobs();
        if (!tl.IsCompleted || !page.IsHitTestVisible) throw new Exception("после обоих страница не включилась");

        // with the window hidden there is nothing to draw on: a window of its own
        w.Hide();
        var three = new ConfirmWindow("Скрыто", "в трее");
        var t3 = three.ShowDialog(w);
        Jobs();
        if (layers.Children.Count != 0) throw new Exception("окно скрыто, а диалог попал на его слой");
        if (t3.IsCompleted) throw new Exception("отдельное окно закрылось само");
        three.Close();
        Jobs();
        if (!t3.IsCompleted) throw new Exception("отдельное окно не закрылось по Close()");

        w.Close();
    }

    private static void Banner()
    {
        try
        {
            UpdateWatcher.Set(Fake("9.9.9"));
            var view = new HomeView();
            var w = new Window { Width = 1100, Height = 760, Content = view };
            w.Show();
            w.Measure(new Size(1100, 760));
            w.Arrange(new Rect(0, 0, 1100, 760));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var banner = view.FindControl<Border>("UpdateBanner")!;
            var title = view.FindControl<TextBlock>("UpdateBannerTitle")!;
            var text = view.FindControl<TextBlock>("UpdateBannerText")!;
            var button = view.FindControl<Button>("BtnUpdateNow")!;
            if (!banner.IsVisible) throw new Exception("обновление найдено, а баннера нет");
            if (title.Text?.Contains("9.9.9") != true) throw new Exception("в баннере не та версия: " + title.Text);
            if (text.Text?.Contains("Вторая") == true) throw new Exception("в баннер попали все строки описания, а нужна первая");
            if (!button.Classes.Contains("primary")) throw new Exception("кнопка не зелёная (нет класса primary)");

            UpdateWatcher.Set(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (banner.IsVisible) throw new Exception("обновлений нет, а баннер остался");
            w.Close();
        }
        finally { UpdateWatcher.Set(null); }
    }

    /// <summary>
    /// The background search, with the network replaced by a function and the hours by tens of
    /// milliseconds: it says "found" once and not on every repeat, keeps its answer through a
    /// failed check, clears it when the update is gone, and does not ask while the switch is off.
    /// </summary>
    private static void UpdateWatch(List<string> failures)
    {
        Console.WriteLine();
        var saved = (UpdateWatcher.Source, UpdateWatcher.Enabled, UpdateWatcher.FirstDelay,
                     UpdateWatcher.Every, UpdateWatcher.AfterFailure);
        var calls = 0;
        var events = 0;
        var mode = "found";
        var on = true;
        void Count(object? s, EventArgs e) => System.Threading.Interlocked.Increment(ref events);

        bool Until(Func<bool> done, int ms = 4000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end && !done()) System.Threading.Thread.Sleep(10);
            return done();
        }

        string? problem = null;
        try
        {
            UpdateWatcher.Changed += Count;
            UpdateWatcher.Source = () =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                return Task.FromResult(mode switch
                {
                    "found" => Fake("9.9.9"),
                    "gone" => new UpdateInfo { Available = false, Current = "2.0.4", Latest = "2.0.4" },
                    _ => new UpdateInfo { Error = "нет сети" },
                });
            };
            UpdateWatcher.Enabled = () => on;
            UpdateWatcher.FirstDelay = TimeSpan.FromMilliseconds(40);
            UpdateWatcher.Every = TimeSpan.FromMilliseconds(100);
            UpdateWatcher.AfterFailure = TimeSpan.FromMilliseconds(100);
            UpdateWatcher.Set(null);
            events = 0;

            UpdateWatcher.Start();
            if (!Until(() => UpdateWatcher.Available != null)) problem = "за четыре секунды обновление не нашлось";
            else if (UpdateWatcher.Available!.Latest != "9.9.9") problem = "нашлась не та версия";
            else
            {
                var seen = calls;
                System.Threading.Thread.Sleep(450);
                if (calls <= seen + 1) problem = "проверки не повторяются сами";
                else if (events != 1) problem = $"о той же находке сказано {events} раз, а нужен один";
            }

            if (problem == null)
            {
                mode = "error";
                System.Threading.Thread.Sleep(350);
                if (UpdateWatcher.Available?.Latest != "9.9.9") problem = "неудачная проверка стёрла найденное обновление";
                else if (events != 1) problem = "неудачная проверка подняла тревогу";
            }

            if (problem == null)
            {
                mode = "gone";
                if (!Until(() => UpdateWatcher.Available == null)) problem = "обновление вышло из списка, а баннер остался";
                else if (events != 2) problem = $"событий {events}, а должно быть 2 (нашлось и исчезло)";
            }

            if (problem == null)
            {
                on = false;
                mode = "found";
                System.Threading.Thread.Sleep(200);      // a check already under way may land
                var frozen = calls;
                System.Threading.Thread.Sleep(450);
                if (calls != frozen) problem = "переключатель выключен, а проверки идут";
                else if (UpdateWatcher.Available != null) problem = "переключатель выключен, а обновление появилось";
            }
        }
        finally
        {
            UpdateWatcher.Stop();
            UpdateWatcher.Changed -= Count;
            (UpdateWatcher.Source, UpdateWatcher.Enabled, UpdateWatcher.FirstDelay,
             UpdateWatcher.Every, UpdateWatcher.AfterFailure) = saved;
            UpdateWatcher.Set(null);
        }

        if (problem == null)
            Console.WriteLine("  ✓ автопоиск обновлений: находит, не повторяется, переживает сбой, слушается переключателя");
        else
        {
            Console.WriteLine("  ✗ автопоиск обновлений: " + problem);
            failures.Add("автопоиск обновлений — " + problem);
        }
    }

    /// <summary>
    /// Stand-in for GitHub and for a mirror, on one local port. What a path ends in says how it
    /// behaves: "…direct404" is a 404 from GitHub only, "…mirrorhang" never answers from the mirror,
    /// "both…" does it from both, "…stall" starts a body from GitHub and stops, "…big" is 300 KB.
    /// </summary>
    private sealed class FakeGitHub : IDisposable
    {
        public const int BigSize = 300_000;
        private readonly System.Net.HttpListener _l = new();
        private readonly List<string> _order = new();

        public int Port { get; }

        public FakeGitHub()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    System.Net.HttpListenerContext c;
                    try { c = await _l.GetContextAsync(); } catch { break; }
                    _ = Task.Run(() => Handle(c));
                }
            });
        }

        public string Direct(string name) => $"http://127.0.0.1:{Port}/direct/{name}";
        public string MirrorPrefix => $"http://127.0.0.1:{Port}/mirror/";
        public string[] Seen() { lock (_order) return _order.ToArray(); }
        public void Reset() { lock (_order) _order.Clear(); }

        private void Handle(System.Net.HttpListenerContext c)
        {
            var raw = c.Request.RawUrl ?? "";
            var mirror = raw.StartsWith("/mirror/");
            var me = mirror ? "mirror" : "direct";
            var name = raw[(raw.LastIndexOf('/') + 1)..];
            lock (_order) _order.Add(me);
            try
            {
                if (name.Contains("both404") || name.Contains(me + "404")) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                if (name.Contains("bothhang") || name.Contains(me + "hang")) { Thread.Sleep(2500); c.Response.Abort(); return; }

                var payload = name.Contains("big")
                    ? Enumerable.Repeat((byte)(mirror ? 'M' : 'D'), BigSize).ToArray()
                    : System.Text.Encoding.UTF8.GetBytes((mirror ? "MIRROR:" : "DIRECT:") + name);
                c.Response.ContentLength64 = payload.Length;
                if (name.Contains("stall") && !mirror)
                {
                    c.Response.OutputStream.Write(payload, 0, payload.Length / 3);
                    c.Response.OutputStream.Flush();
                    Thread.Sleep(2500);
                    c.Response.Abort();
                    return;
                }
                c.Response.OutputStream.Write(payload, 0, payload.Length);
                c.Response.Close();
            }
            catch { try { c.Response.Abort(); } catch { } }
        }

        public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
    }

    private sealed class LastValue : IProgress<double>
    {
        public double Last = -1;
        public void Report(double value) => Last = value;
    }

    private static T Run<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
    private static void Run(Func<Task> f) => Task.Run(f).GetAwaiter().GetResult();

    /// <summary>
    /// Fetching from GitHub, and through a mirror when GitHub does not answer. This is what stood
    /// between a dead mirror and every update: the program went through one mirror and nothing else,
    /// and waited half a minute for it each time.
    /// </summary>
    private static void FetchWays()
    {
        var mirrors = GitHubFetch.BackupMirrors;
        var header = GitHubFetch.HeaderTimeout;
        var stall = GitHubFetch.StallTimeout;
        var files = new List<string>();
        using var gh = new FakeGitHub();
        try
        {
            GitHubFetch.BackupMirrors = Array.Empty<string>();
            GitHubFetch.HeaderTimeout = TimeSpan.FromMilliseconds(300);
            GitHubFetch.StallTimeout = TimeSpan.FromMilliseconds(300);
            GitHubFetch.Forget();

            string Get(string name, string? mirror = "") => Run(() => GitHubFetch.GetStringAsync(gh.Direct(name), mirror == "" ? gh.MirrorPrefix : mirror));
            void Expect(string what, string got, string want)
            {
                if (got != want) throw new Exception($"{what}: получено «{got}», нужно «{want}»");
            }
            void Order(string what, params string[] want)
            {
                var seen = gh.Seen();
                gh.Reset();
                if (!seen.SequenceEqual(want))
                    throw new Exception($"{what}: обращения {string.Join(",", seen)}, нужны {string.Join(",", want)}");
            }

            // GitHub answers: the mirror is not touched
            Expect("GitHub отвечает", Get("a-ok"), "DIRECT:a-ok");
            Order("GitHub отвечает", "direct");

            // GitHub says no: the mirror's turn
            Expect("GitHub вернул 404", Get("b-direct404"), "MIRROR:b-direct404");
            Order("GitHub вернул 404", "direct", "mirror");

            // what worked is tried first next time, so a blocked GitHub is waited for once
            Expect("после зеркала", Get("c-ok"), "MIRROR:c-ok");
            Order("после зеркала — идёт к зеркалу первым", "mirror");

            // and when the one that worked stops working, it goes back
            Expect("зеркало отказало", Get("d-mirror404"), "DIRECT:d-mirror404");
            Order("зеркало отказало", "mirror", "direct");
            Expect("после возврата", Get("e-ok"), "DIRECT:e-ok");
            Order("после возврата — GitHub первым", "direct");

            // GitHub never answers: not half a minute, the header timeout
            GitHubFetch.Forget();
            var t0 = DateTime.UtcNow;
            Expect("GitHub молчит", Get("f-directhang"), "MIRROR:f-directhang");
            var took = DateTime.UtcNow - t0;
            if (took > TimeSpan.FromSeconds(2)) throw new Exception($"ждали молчащий GitHub {took.TotalSeconds:0.0} с");
            gh.Reset();

            // nothing works: the message says where it was tried, and whether anyone answered at all
            GitHubFetch.Forget();
            try { Get("g-both404"); throw new Exception("все 404, а исключения нет"); }
            catch (GitHubFetch.FetchFailedException f)
            {
                if (!f.Message.Contains("GitHub") || !f.Message.Contains("зеркало")) throw new Exception("в сообщении не названы оба места: " + f.Message);
                if (f.Unreachable) throw new Exception("оба ответили «нет», а считается, что не отвечают");
            }
            try { Get("h-bothhang"); throw new Exception("все молчат, а исключения нет"); }
            catch (GitHubFetch.FetchFailedException f)
            {
                if (!f.Unreachable) throw new Exception("никто не ответил, а считается, что ответили");
            }
            gh.Reset();

            // an empty mirror is "no go-between": GitHub only, whatever else is configured
            GitHubFetch.BackupMirrors = new[] { gh.MirrorPrefix };
            GitHubFetch.Forget();
            try { Get("i-direct404", mirror: null); throw new Exception("зеркало пусто, а файл нашёлся"); }
            catch (GitHubFetch.FetchFailedException f)
            {
                if (f.Message.Contains("зеркало")) throw new Exception("при пустом зеркале ходили к зеркалу: " + f.Message);
            }
            Order("пустое зеркало", "direct");
            GitHubFetch.BackupMirrors = Array.Empty<string>();

            // the list of ways: the configured mirror once, however many times it is also a backup
            GitHubFetch.BackupMirrors = new[] { "https://a.example/", "https://B.example" };
            GitHubFetch.Forget();
            var ways = GitHubFetch.Ways("https://github.com/x/y", "https://a.example");
            if (ways.Count != 3 || ways[0].Name != "GitHub" || ways[1].Url != "https://a.example/https://github.com/x/y")
                throw new Exception("порядок мест: " + string.Join(" | ", ways.Select(w => w.Url)));
            GitHubFetch.BackupMirrors = Array.Empty<string>();

            // a large file: to disk, with progress
            var path = Path.Combine(Path.GetTempPath(), "tunor-fetch-" + Guid.NewGuid().ToString("N") + ".bin");
            files.Add(path);
            GitHubFetch.Forget();
            var progress = new LastValue();
            Run(() => GitHubFetch.DownloadAsync(gh.Direct("j-big"), gh.MirrorPrefix, path, progress));
            var got = File.ReadAllBytes(path);
            if (got.Length != FakeGitHub.BigSize || !got.All(b => b == (byte)'D')) throw new Exception("скачанный файл не тот");
            if (progress.Last < 0.999) throw new Exception($"прогресс остановился на {progress.Last:0.00}");
            gh.Reset();

            // GitHub starts a body and stops: the half is thrown away, the mirror gives the whole
            Run(() => GitHubFetch.DownloadAsync(gh.Direct("k-stall-big"), gh.MirrorPrefix, path, null));
            got = File.ReadAllBytes(path);
            if (got.Length != FakeGitHub.BigSize || !got.All(b => b == (byte)'M'))
                throw new Exception($"после обрыва на полпути в файле {got.Length} байт, и это не целый файл от зеркала");
            gh.Reset();

            // rule lists: two in a row that nobody answers for, and the rest are skipped, not waited for
            var down = new List<RuleGroup>();
            for (var i = 0; i < 5; i++)
                down.Add(new RuleGroup { Tag = "warp-d" + i, Type = "remote", SourceUrl = gh.Direct($"list{i}-bothhang.srs") });
            GitHubFetch.Forget();
            t0 = DateTime.UtcNow;
            var r1 = Run(() => RulesetDownloader.DownloadAllAsync(down, gh.MirrorPrefix));
            took = DateTime.UtcNow - t0;
            if (r1.Count != 5 || r1.Any(r => r.Ok)) throw new Exception("списки при молчащей сети: результатов " + r1.Count);
            if (!r1.Skip(2).All(r => r.Message.StartsWith("пропущено"))) throw new Exception("после двух неудач подряд остальные не пропущены");
            if (took > TimeSpan.FromSeconds(4)) throw new Exception($"молчащую сеть ждали {took.TotalSeconds:0.0} с");
            gh.Reset();

            // …but lists that are simply gone do not stop the one that is there
            var mixed = new List<RuleGroup>();
            for (var i = 0; i < 4; i++)
                mixed.Add(new RuleGroup { Tag = "warp-m" + i, Type = "remote", SourceUrl = gh.Direct($"gone{i}-both404.srs") });
            mixed.Add(new RuleGroup { Tag = "warp-ok", Type = "remote", SourceUrl = gh.Direct("fetch-smoke-ok.srs") });
            GitHubFetch.Forget();
            var r2 = Run(() => RulesetDownloader.DownloadAllAsync(mixed, gh.MirrorPrefix));
            files.Add(Path.Combine(Paths.RulesetsDir, "fetch-smoke-ok.srs"));
            if (!r2[^1].Ok) throw new Exception("после четырёх отсутствующих списков живой не скачался: " + r2[^1].Message);
        }
        finally
        {
            GitHubFetch.BackupMirrors = mirrors;
            GitHubFetch.HeaderTimeout = header;
            GitHubFetch.StallTimeout = stall;
            GitHubFetch.Forget();
            foreach (var f in files) { try { File.Delete(f); } catch { } }
        }
    }

    /// <summary>
    /// The scripts that replace the program after an update must not wait for ever for a copy that
    /// is still running — that was a macOS update that downloaded, "closed", and came back as the old
    /// version until somebody quit it by hand. Here: they ask it to end and then make it; whether they
    /// do is run on a Mac against a stand-in (TUNOR_SMOKE_DUMP writes the scripts out for that).
    /// </summary>
    private static void SwapScripts()
    {
        var mac = UpdateService.SwapScript(424242, "/tmp/tunor-swaptest/stage/Tunor.app", "/tmp/tunor-swaptest/Old.app", 2, 2);
        var linux = UpdateService.SwapFileScript(424242, "/tmp/tunor-swaptest/new.AppImage", "/tmp/tunor-swaptest/old.AppImage", 2, 2);

        foreach (var (name, script) in new[] { ("macOS", mac), ("Linux", linux) })
        {
            if (!script.Contains("kill 424242")) throw new Exception(name + ": процесс не просят завершиться (TERM)");
            if (!script.Contains("kill -9 424242")) throw new Exception(name + ": упрямый процесс не добивают (KILL)");
            // the order matters: wait, then ask, then make, and only then touch the files
            var ask = script.IndexOf("kill 424242", StringComparison.Ordinal);
            var make = script.IndexOf("kill -9 424242", StringComparison.Ordinal);
            var touch = script.IndexOf("OLD=", StringComparison.Ordinal);
            if (!(ask < make && make < touch)) throw new Exception(name + ": порядок ожидания, TERM, KILL, подмены нарушен");
        }

        // the defaults: half a minute of grace and ten seconds after asking
        var defaults = UpdateService.SwapScript(1, "a", "b");
        if (!defaults.Contains("seq 1 60") || !defaults.Contains("seq 1 20"))
            throw new Exception("по умолчанию ждут не 30 + 10 секунд: " + defaults.Split('\n')[2]);

        var dump = Environment.GetEnvironmentVariable("TUNOR_SMOKE_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, "swap-mac.sh"), mac + "\n");
            File.WriteAllText(Path.Combine(dump, "swap-linux.sh"), linux + "\n");
        }
    }

    /// <summary>
    /// The manifest, as each machine reads it. An Intel Mac gets its own build and not the one for
    /// Apple Silicon, and the other way round; with no part of its own a Mac is told so, and is not
    /// handed the Windows installer; a part may carry a version of its own, for a build that has not
    /// been made for the new release yet.
    /// </summary>
    private static void ManifestParts()
    {
        const string manifest = """
            { "version": "2.1.0", "url": "https://x/win.exe", "sha256": "WIN", "notes": "общие",
              "mac":     { "url": "https://x/mac-arm64.tgz",   "sha256": "ARM", "notes": "arm" },
              "mac-x64": { "url": "https://x/mac-x64.tgz",     "sha256": "X64", "notes": "x64" },
              "linux":   { "url": "https://x/linux.AppImage",  "sha256": "LIN" } }
            """;
        var cur = new Version(2, 0, 9);

        void Expect(string what, UpdateInfo i, string url, string sha)
        {
            if (!string.IsNullOrEmpty(i.Error)) throw new Exception($"{what}: ошибка «{i.Error}»");
            if (i.Url != url || i.Sha256 != sha) throw new Exception($"{what}: получено {i.Url} / {i.Sha256}, нужно {url} / {sha}");
            if (!i.Available || i.Latest != "2.1.0") throw new Exception($"{what}: версия {i.Latest}, доступна: {i.Available}");
        }

        // which part each processor reads
        if (UpdateService.MacSection(System.Runtime.InteropServices.Architecture.X64) != "mac-x64") throw new Exception("Intel читает не свою часть манифеста");
        if (UpdateService.MacSection(System.Runtime.InteropServices.Architecture.Arm64) != "mac") throw new Exception("Apple Silicon читает не свою часть манифеста");

        Expect("Windows", UpdateService.FromManifest(manifest, null, cur), "https://x/win.exe", "WIN");
        Expect("Apple Silicon", UpdateService.FromManifest(manifest, "mac", cur), "https://x/mac-arm64.tgz", "ARM");
        Expect("Intel", UpdateService.FromManifest(manifest, "mac-x64", cur), "https://x/mac-x64.tgz", "X64");
        Expect("Linux", UpdateService.FromManifest(manifest, "linux", cur), "https://x/linux.AppImage", "LIN");
        if (UpdateService.FromManifest(manifest, "mac-x64", cur).Notes != "x64") throw new Exception("у Intel не его заметки");

        // a release made before Intel builds: no part for them, and the answer says so
        const string before = """{ "version": "2.0.9", "url": "https://x/win.exe", "sha256": "W", "mac": { "url": "https://x/a.tgz", "sha256": "A" } }""";
        var none = UpdateService.FromManifest(before, "mac-x64", cur);
        if (string.IsNullOrEmpty(none.Error) || !none.Error.Contains("Intel")) throw new Exception("для Intel нет сборки, а об этом не сказано: «" + none.Error + "»");
        if (!string.IsNullOrEmpty(none.Url)) throw new Exception("Intel-маку вручена чужая ссылка: " + none.Url);
        if (!string.IsNullOrEmpty(UpdateService.FromManifest(before, "mac", cur).Error)) throw new Exception("Apple Silicon перестал находить свою сборку");

        // a part with a version of its own: not offered what it already is
        const string own = """{ "version": "2.1.0", "url": "https://x/w", "sha256": "W", "mac": { "version": "2.0.9", "url": "https://x/m", "sha256": "M" } }""";
        var same = UpdateService.FromManifest(own, "mac", cur);
        if (same.Available || same.Latest != "2.0.9") throw new Exception($"своя версия части не учтена: доступна {same.Available}, версия {same.Latest}");

        // a part without a link is not a build
        const string broken = """{ "version": "2.1.0", "url": "https://x/w", "mac-x64": { "sha256": "X" } }""";
        if (!UpdateService.FromManifest(broken, "mac-x64", cur).Error.Contains("Некорректный")) throw new Exception("часть без ссылки принята за сборку");
    }

    /// <summary>
    /// The log on disk: lines reach the file stamped and marked, a file that grows too big is rolled
    /// and the tail reads across the two, old days are removed and other files are not, the report gets
    /// only what does not say which sites were opened, and a disk that cannot be written to does not
    /// bring anything down.
    /// </summary>
    private static void LogFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunor-logtest-" + Guid.NewGuid().ToString("N"));
        var was = (AppLog.DirectoryOf, AppLog.MaxBytes);
        try
        {
            AppLog.DirectoryOf = () => dir;
            AppLog.MaxBytes = 4000;
            string Today() => Path.Combine(dir, $"tunor-{DateTime.Now:yyyy-MM-dd}.log");

            // written, stamped, marked
            AppLog.Write("[ui] первая строка");
            AppLog.Write("ERROR[0001] что-то сломалось", problem: true);
            AppLog.Flush();
            if (!File.Exists(Today())) throw new Exception("файл дня не появился: " + string.Join(",", Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>()));
            var lines = File.ReadAllLines(Today());
            if (lines.Length < 2) throw new Exception("в файле строк: " + lines.Length);
            var first = lines.First(l => l.Contains("первая строка"));
            if (!System.Text.RegularExpressions.Regex.IsMatch(first, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}       \[ui\] первая строка$"))
                throw new Exception("не такая метка времени: «" + first + "»");
            if (!lines.Any(l => l.Contains(" [err] ERROR[0001]"))) throw new Exception("ошибка не помечена");

            // a tail takes the last lines, in order
            for (var i = 1; i <= 10; i++) AppLog.Write("[ui] пронумерованная " + i);
            var tail = AppLog.Tail(3);
            if (tail.Length != 3 || !tail[0].EndsWith("пронумерованная 8") || !tail[2].EndsWith("пронумерованная 10"))
                throw new Exception("хвост: " + string.Join(" | ", tail.Select(l => l[^20..])));
            if (AppLog.Tail(2, l => l.Contains("ERROR")).Length != 1) throw new Exception("хвост с фильтром");

            // a file that is too big is rolled, and the tail reads over both
            for (var i = 1; i <= 34; i++) { AppLog.Write("[ui] много-много текста ради размера файла, строка " + i); AppLog.Flush(); }
            var rolled = Path.ChangeExtension(Today(), ".1.log");
            if (!File.Exists(rolled)) throw new Exception("большой файл не перенесён в .1.log");
            if (!File.Exists(Today())) throw new Exception("после переноса не начат новый файл");
            var across = AppLog.Tail(100);
            if (across.Length != 46 || !across[^1].EndsWith("строка 34")) throw new Exception("хвост через два файла: " + across.Length + " строк вместо 46");
            for (var i = 1; i < across.Length; i++)
                if (string.CompareOrdinal(across[i - 1][..23], across[i][..23]) > 0) throw new Exception("хвост не по порядку времени");

            // a day never takes more than two files, however much is said
            for (var i = 1; i <= 120; i++) { AppLog.Write("[ui] ещё и ещё текста, чтобы файл переполнялся не раз, строка " + i); AppLog.Flush(); }
            var daysFiles = Directory.GetFiles(dir, $"tunor-{DateTime.Now:yyyy-MM-dd}*").Length;
            if (daysFiles != 2) throw new Exception("файлов за день: " + daysFiles);

            // old days go, other files stay
            var old = Path.Combine(dir, "tunor-2020-01-01.log");
            File.WriteAllText(old, "старое");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
            var other = Path.Combine(dir, "чужой-файл.txt");
            File.WriteAllText(other, "не журнал");
            File.SetLastWriteTime(other, DateTime.Now.AddDays(-30));
            AppLog.Prune(dir);
            if (File.Exists(old)) throw new Exception("старый день не удалён");
            if (!File.Exists(other) || !File.Exists(Today())) throw new Exception("удалено лишнее");

            // what may go into a report
            string Line(string mark, string body) => $"2026-10-25 12:00:00.000 {mark} {body}";
            var safe = new[]
            {
                Line("     ", "[ui] sing-box started"),
                Line("[err]", "ERROR[0017] [4036881739 15.1s] connection: open connection to 149.154.175.100:80: context deadline exceeded"),
                Line("     ", "WARN[0000] lxd: running elevated"),
                Line("     ", "DEBUG endpoint/wireguard[w]: peer(bmXO…fgyo) - sending handshake initiation"),
                Line("     ", "=== Tunor 2.2.0 запущен ==="),
            };
            var unsafeLines = new[]
            {
                Line("     ", "INFO[0001] [123456 1ms] inbound/mixed[proxy-in]: inbound connection to www.example.com:443"),
                Line("     ", "DEBUG[0002] router: match[3] domain_suffix=youtube.com => warp-out"),
                Line("     ", "INFO[0003] dns: exchanged www.example.com A"),
            };
            foreach (var l in safe) if (!AppLog.IsSafeForReport(l)) throw new Exception("для отчёта нужная строка отброшена: " + l[30..]);
            foreach (var l in unsafeLines) if (AppLog.IsSafeForReport(l)) throw new Exception("в отчёт попала строка с адресом сайта: " + l[30..]);

            // the level setting
            foreach (var (given, want) in new (string?, string)[] { ("debug", "debug"), (" DEBUG ", "debug"), ("Info", "info"), ("error", "error"), ("warn", "warn"), ("trace", "warn"), ("что-то", "warn"), (null, "warn") })
                if (AppSettings.NormalizeLevel(given) != want) throw new Exception($"уровень «{given}» → «{AppSettings.NormalizeLevel(given)}», нужно «{want}»");

            // a place that cannot be written to is not an error
            var blocker = Path.Combine(Path.GetTempPath(), "tunor-logtest-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "это файл, а не папка");
            AppLog.DirectoryOf = () => Path.Combine(blocker, "logs");
            AppLog.Write("[ui] в никуда");
            AppLog.Flush();
            _ = AppLog.Tail(5);
            File.Delete(blocker);
        }
        finally
        {
            AppLog.Flush();
            (AppLog.DirectoryOf, AppLog.MaxBytes) = was;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Export and import of the settings: the files that make a setup travel, the machine's own
    /// things do not, a password really locks the file, a wrong one or a damaged file says so, and a
    /// file made by someone else cannot write outside the program's folder, change where updates come
    /// from, or point a tunnel or a rule at a file that is not the program's.
    /// </summary>
    private static void BackupChecks()
    {
        var from = Path.Combine(Path.GetTempPath(), "tunor-backup-from-" + Guid.NewGuid().ToString("N"));
        var to = Path.Combine(Path.GetTempPath(), "tunor-backup-to-" + Guid.NewGuid().ToString("N"));
        var root = Backup.Root;
        try
        {
            void Put(string dir, string name, string text)
            {
                var path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            string Get(string dir, string name) => File.ReadAllText(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));

            const string vless = "vless://11111111-2222-3333-4444-555555555555@secret.example.net:443?security=tls#home";
            const string rules = """[{"type":"local","tag":"warp-youtube","_source_url":"https://example.com/youtube.srs","path":"../data/rulesets/youtube.srs","format":"binary"},{"type":"inline","tag":"warp-custom","rules":[{"domain":["a.example"]}]}]""";
            string tunnels(string file) => "[{\"id\":\"warp\",\"title\":\"WARP\",\"kind\":\"wireguard\",\"file\":\"" + file + "\"},"
                + "{\"id\":\"home\",\"title\":\"home\",\"kind\":\"vless\",\"url\":\"" + vless + "\",\"sub\":\"https://sub.example.net/token123\"}]";

            // ---- the machine the copy is made on
            Put(from, "settings.json", """{"accept":true,"logLevel":"debug","final":"warp","controllerPort":4711,"controllerSecret":"FROM-SECRET","listsUpdatedAt":"2026-01-01T00:00:00","lastSeenVersion":"2.1.0","paths":{"config":"D:/elsewhere/config.json"}}""");
            Put(from, "update.json", """{"Repo":"someone/else","Mirror":""}""");
            Put(from, "data/rules.json", rules);
            Put(from, "data/tunnels.json", tunnels("data/warp.conf"));
            Put(from, "data/warp.conf", "[Interface]\nPrivateKey = WARPKEY\n");
            Put(from, "data/geo.conf", "[Interface]\nPrivateKey = GEOKEY\n");
            Put(from, "data/profiles/awg/home.conf", "[Interface]\nPrivateKey = HOMEKEY\n");
            Put(from, "data/cache.db", "cache");
            Put(from, "data/warp.conf.bak", "old");
            Put(from, "data/rulesets/youtube.srs", "list");
            Put(from, "build/config.json", "{}");

            Backup.Root = () => from;
            var present = Backup.Present();
            var want = new[] { "settings.json", "data/rules.json", "data/tunnels.json", "data/warp.conf", "data/geo.conf", "data/profiles/awg/home.conf" };
            if (!present.OrderBy(x => x).SequenceEqual(want.OrderBy(x => x)))
                throw new Exception("в копию должно идти: " + string.Join(", ", want) + "; идёт: " + string.Join(", ", present));

            // ---- without a password: a plain zip, with the machine's own things taken out
            var plain = Backup.Export(null);
            var read = Backup.Read(plain, null);
            if (read.WasEncrypted || read.Files.Count != want.Length) throw new Exception("копия без пароля прочиталась не так: " + read.Files.Count);
            var settings = System.Text.Encoding.UTF8.GetString(read.Files.First(f => f.Name == "settings.json").Data);
            foreach (var own in new[] { "FROM-SECRET", "4711", "listsUpdatedAt", "lastSeenVersion", "\"accept\"", "elsewhere" })
                if (settings.Contains(own)) throw new Exception("в копию попало то, что принадлежит этой машине: " + own);
            if (!settings.Contains("\"logLevel\": \"debug\"") && !settings.Contains("\"logLevel\":\"debug\"")) throw new Exception("настройка уровня журнала не попала в копию");
            if (read.Files.Any(f => f.Name.Contains("update")) || read.Files.Any(f => f.Name.Contains("cache")))
                throw new Exception("в копию попало лишнее");

            // ---- with a password: nothing readable, the right word opens it, others do not
            var locked = Backup.Export("пароль-1");
            var raw = System.Text.Encoding.Latin1.GetString(locked);
            if (!raw.StartsWith("TNRB1")) throw new Exception("у файла с паролем нет своей метки");
            foreach (var secret in new[] { "WARPKEY", "HOMEKEY", "secret.example.net", "token123" })
                if (raw.Contains(secret)) throw new Exception("в файле с паролем читается: " + secret);
            try { Backup.Read(locked, null); throw new Exception("файл с паролем открылся без пароля"); }
            catch (BackupException e) when (e.NeedsPassword) { }
            try { Backup.Read(locked, "не тот"); throw new Exception("файл с паролем открылся чужим паролем"); }
            catch (BackupException e) when (e.WrongPassword && !e.NeedsPassword) { }
            var opened = Backup.Read(locked, "пароль-1");
            if (!opened.WasEncrypted || opened.Files.Count != want.Length) throw new Exception("с верным паролем прочиталось не всё");
            var broken = (byte[])locked.Clone();
            broken[broken.Length / 2] ^= 0x55;
            try { Backup.Read(broken, "пароль-1"); throw new Exception("испорченный файл открылся"); }
            catch (BackupException) { }
            if (Backup.Export("пароль-1").AsSpan().SequenceEqual(locked)) throw new Exception("два файла с одним паролем одинаковы: нет соли");

            // ---- loaded onto another machine, which keeps what is its own and what it had
            Backup.Root = () => to;
            Put(to, "settings.json", """{"accept":true,"logLevel":"warn","controllerPort":9999,"controllerSecret":"TO-SECRET","paths":{"config":"build/config.json"}}""");
            Put(to, "update.json", """{"Repo":"Vexorter42/Tunor","Mirror":"https://gh-proxy.com/"}""");
            Put(to, "data/rules.json", "[]");
            Put(to, "data/profiles/awg/old.conf", "OLD");
            var plan = Backup.Plan(opened);
            if (!plan.Replaces.Contains("data/rules.json") || !plan.Adds.Contains("data/warp.conf"))
                throw new Exception("план показывает не то: заменит " + string.Join(",", plan.Replaces) + "; добавит " + string.Join(",", plan.Adds));
            var said = Backup.Describe(opened);
            if (!said.Contains("Заменит: настройки, правила.") || !said.Contains("Добавит: ") || !said.Contains("WARP") || !said.Contains("профили (1)") || said.Contains(".json") || said.Contains(".conf"))
                throw new Exception("описание перед загрузкой: " + said);
            var copies = Backup.Apply(opened);

            var after = Get(to, "settings.json");
            if (!after.Contains("TO-SECRET") || !after.Contains("9999") || after.Contains("FROM-SECRET")) throw new Exception("секрет и порт этой машины не сохранились");
            if (!after.Contains("debug") || !after.Contains("\"final\": \"warp\"")) throw new Exception("настройки из копии не применились: " + after);
            if (!after.Contains("build/config.json") || after.Contains("elsewhere")) throw new Exception("пути этой машины изменились");
            if (!after.Contains("\"accept\": true")) throw new Exception("согласие с лицензией потеряно");
            if (Get(to, "update.json").Contains("someone/else")) throw new Exception("копия сменила адрес обновлений");
            if (Get(to, "data/warp.conf") != Get(from, "data/warp.conf") || Get(to, "data/profiles/awg/home.conf") != Get(from, "data/profiles/awg/home.conf"))
                throw new Exception("ключи туннелей не перенеслись");
            if (Get(to, "data/rules.json") != rules) throw new Exception("правила не перенеслись");
            if (File.ReadAllText(Path.Combine(copies, "data", "rules.json")) != "[]") throw new Exception("прежние правила не сохранены в «до загрузки»");
            if (!File.Exists(Path.Combine(copies, "settings.json"))) throw new Exception("прежние настройки не сохранены в «до загрузки»");
            if (File.Exists(Path.Combine(copies, "data", "warp.conf"))) throw new Exception("скопировано то, чего не было");
            if (Get(to, "data/profiles/awg/old.conf") != "OLD") throw new Exception("лишний файл, которого нет в копии, тронут");

            // ---- a file made by someone else
            string Zip(params (string Name, string Data)[] entries)
            {
                using var ms = new MemoryStream();
                using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    void Entry(string n, string d) { using var w = new StreamWriter(z.CreateEntry(n).Open()); w.Write(d); }
                    Entry("backup.json", """{"format":1,"app":"9.9.9","created":"2026-01-01T00:00:00Z"}""");
                    foreach (var (n, d) in entries) Entry(n, d);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
            void Rejected(string why, string base64)
            {
                try { Backup.Read(Convert.FromBase64String(base64), null); }
                catch (BackupException) { return; }
                throw new Exception("принят файл: " + why);
            }

            Rejected("выход из папки", Zip(("../evil.txt", "x")));
            Rejected("выход из папки глубже", Zip(("data/../../evil.txt", "x")));
            Rejected("абсолютный путь", Zip(("/etc/evil", "x"), ("C:/evil.txt", "x")));
            Rejected("выход через профили", Zip(("data/profiles/awg/../../../evil.conf", "x")));
            Rejected("обратные слэши", Zip(("data\\profiles\\awg\\x.conf", "x")));
            Rejected("update.json", Zip(("update.json", """{"Repo":"evil/evil"}""")));
            Rejected("чужое имя", Zip(("build/config.json", "{}")));
            Rejected("не json в туннелях", Zip(("data/tunnels.json", "не json")));
            Rejected("туннель смотрит наружу", Zip(("data/tunnels.json", tunnels("../../Users/x/secret.conf"))));
            Rejected("туннель смотрит на системный файл", Zip(("data/tunnels.json", tunnels("C:/Windows/win.ini"))));
            Rejected("список правил вне папки", Zip(("data/rules.json", """[{"type":"local","tag":"x","path":"../../Windows/win.ini"}]""")));
            Rejected("адрес списка не сайт", Zip(("data/rules.json", """[{"type":"local","tag":"x","_source_url":"file:///etc/passwd","path":"../data/rulesets/x.srs"}]""")));
            Rejected("пустая копия", Zip());
            Rejected("не копия", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("просто текст, не архив")));
            Rejected("пустой файл", "");
            Rejected("обрезанный архив", Convert.ToBase64String(plain.AsSpan(0, plain.Length / 2).ToArray()));
            Rejected("копия из будущего", Convert.ToBase64String(MakeFutureFormat()));

            // folders entries, as other zip programs write them, are not a reason to refuse
            var withFolders = Backup.Read(Convert.FromBase64String(Zip(("data/", ""), ("data/profiles/", ""), ("data/warp.conf", "k"))), null);
            if (withFolders.Files.Count != 1) throw new Exception("записи о папках учтены как файлы");

            // Apply does not take a hand-made list on trust either
            Backup.Root = () => to;
            var evil = Path.Combine(Path.GetDirectoryName(to)!, "tunor-evil-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Backup.Apply(new BackupContents { Files = { ("../" + Path.GetFileName(evil), new byte[] { 1 }) } });
                throw new Exception("Apply записал по недопустимому имени");
            }
            catch (BackupException) { }
            if (File.Exists(evil)) { File.Delete(evil); throw new Exception("файл записан за пределами папки"); }

            // the lists' names, which come from addresses that a copy can carry
            foreach (var (url, tag, wantName) in new[]
                     { ("youtube.srs", "warp-youtube", "youtube.srs"), ("..", "warp-x", "warp-x.srs"), ("", "geo-y", "geo-y.srs"),
                       ("..\\..\\boom", "warp-z", "warp-z.srs"), ("a:b", "t", "t.srs"), ("x?y", "t", "t.srs"), ("", "", "list.srs"), ("a\"b", "t", "t.srs") })
                if (RulesetDownloader.SafeFileName(url, tag) != wantName)
                    throw new Exception($"имя списка «{url}» → «{RulesetDownloader.SafeFileName(url, tag)}», ожидалось «{wantName}»");
        }
        finally
        {
            Backup.Root = root;
            foreach (var d in new[] { from, to }) { try { Directory.Delete(d, true); } catch { } }
        }

        static byte[] MakeFutureFormat()
        {
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                using (var w = new StreamWriter(z.CreateEntry("backup.json").Open())) w.Write("""{"format":2}""");
                using (var w = new StreamWriter(z.CreateEntry("data/warp.conf").Open())) w.Write("k");
            }
            return ms.ToArray();
        }
    }

    /// <summary>
    /// "What's new": shown after an upgrade and only then; a first run, the same version and a step back
    /// show nothing; notes that could not be fetched are tried again at the next start, and are not
    /// marked as seen; with the search for updates switched off nothing is fetched; and a release's notes
    /// become headings and paragraphs, without the table of files or the instructions for the first launch.
    /// </summary>
    private static void WhatsNewChecks()
    {
        foreach (var (last, now, want) in new (string?, string, bool)[]
                 { (null, "2.2.0", false), ("", "2.2.0", false), ("2.1.0", "2.2.0", true), ("2.2.0", "2.2.0", false),
                   ("2.3.0", "2.2.0", false), ("мусор", "2.2.0", false), ("2.1.0", "2.1.1", true), ("2.9.9", "2.10.0", true) })
            if (WhatsNew.Due(last, now) != want) throw new Exception($"Due({last}, {now}) должно быть {want}");

        const string notes = """
            Появилась сборка для маков с процессором Intel. Движок не менялся.

            ---

            **Сборка для Intel-маков**

            До сих пор была только сборка для Apple Silicon. Теперь есть `Tunor-2.1.0-x64.dmg`: [подробнее](https://example.com/x).

            **Что проверено**

            Подпись проходит проверку.

            ---

            **Скачать**

            | | |
            |---|---|
            | Windows | `Tunor-Setup.exe` |

            На macOS при первом запуске система скажет, что не смогла проверить приложение.
            """;
        var blocks = WhatsNew.Format(notes);
        var headings = blocks.Where(b => b.Heading).Select(b => b.Text).ToList();
        if (!headings.SequenceEqual(new[] { "Сборка для Intel-маков", "Что проверено" }))
            throw new Exception("заголовки: " + string.Join(" | ", headings));
        if (blocks[0].Heading || !blocks[0].Text.StartsWith("Появилась сборка")) throw new Exception("первый абзац потерян");
        if (blocks.Any(b => b.Text.Contains("**") || b.Text.Contains('`') || b.Text.Contains("](")))
            throw new Exception("в тексте остались знаки разметки");
        if (!blocks.Any(b => b.Text.Contains("Tunor-2.1.0-x64.dmg: подробнее.")))
            throw new Exception("ссылка не превращена в текст: " + string.Join(" / ", blocks.Select(b => b.Text)));
        if (blocks.Any(b => b.Text.Contains("Windows") || b.Text.Contains("Скачать") || b.Text.Contains("На macOS при первом")))
            throw new Exception("после «Скачать» осталось лишнее");

        // the flow, against a stand-in for the network and for settings.json
        var path = Paths.SettingsJson;
        var had = File.Exists(path) ? File.ReadAllText(path) : null;
        var source = WhatsNew.Source;
        try
        {
            var current = UpdateService.CurrentVersionString;
            var fetches = 0;
            string? answer = notes;
            WhatsNew.Source = _ => { fetches++; return Task.FromResult<string?>(answer); };

            void Put(string last, bool autoCheck)
            {
                var s = new AppSettings { LastSeenVersion = last, AutoCheckUpdates = autoCheck };
                SettingsService.Save(s);
            }
            string Seen() => SettingsService.Load().LastSeenVersion;
            (string Version, IReadOnlyList<WhatsNew.Block> Blocks)? Check() => WhatsNew.CheckAsync().GetAwaiter().GetResult();

            Put("", true);
            if (Check() != null || fetches != 0) throw new Exception("первый запуск показал заметки или ходил в сеть");
            if (Seen() != current) throw new Exception("первый запуск не запомнил версию");

            Put("0.9.0", true);
            fetches = 0;
            var found = Check();
            if (found == null || found.Value.Version != current || found.Value.Blocks.Count == 0) throw new Exception("после обновления заметок нет");
            if (fetches != 1) throw new Exception("за заметками сходили " + fetches + " раз");
            if (Seen() != "0.9.0") throw new Exception("версию отметили увиденной до того, как заметки показаны");
            WhatsNew.MarkSeen(found.Value.Version);
            if (Seen() != current) throw new Exception("MarkSeen не отметил версию");
            if (Check() != null) throw new Exception("заметки показаны дважды");

            Put("0.9.0", true);
            answer = null;
            if (Check() != null) throw new Exception("заметок нет, а что-то показано");
            if (Seen() != "0.9.0") throw new Exception("заметки не достали, а версию отметили: в следующий раз не покажут");

            Put("0.9.0", false);
            answer = notes;
            fetches = 0;
            if (Check() != null || fetches != 0) throw new Exception("поиск обновлений выключен, а заметки ищутся");
            if (Seen() != current) throw new Exception("при выключенном поиске версия не отмечена");

            Put("99.0.0", true);
            fetches = 0;
            if (Check() != null || fetches != 0) throw new Exception("после отката назад показаны заметки");
        }
        finally
        {
            WhatsNew.Source = source;
            if (had == null) { try { File.Delete(path); } catch { } } else File.WriteAllText(path, had);
        }
    }

    /// <summary>How many temporary keychains .NET has left in this user's temp folder.</summary>
    private static int Keychains() =>
        OperatingSystem.IsMacOS() ? Directory.GetFiles(Path.GetTempPath(), "*.keychain").Length : 0;

    /// <summary>
    /// The link to the privileged service: it must not touch the keychain, and it must
    /// refuse a server that is not the one it was paired with. Counts keychains rather
    /// than waiting for a dialog — over ssh there is nobody to ask, so the old behaviour
    /// looked like success and left a keychain behind.
    /// </summary>
    private static void ServiceLink(List<string> failures)
    {
        Console.WriteLine();
        if (!ServiceClient.Paired || ServiceClient.PinnedFingerprint is not { } pin)
        {
            Console.WriteLine("  · связь со службой: приложение с ней не сопряжено — проверять нечего");
            return;
        }

        try
        {
            var before = Keychains();
            var answered = 0;
            Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                    if (await ServiceClient.StatusAsync() != null) answered++;
            }).Wait(TimeSpan.FromSeconds(60));
            var after = Keychains();

            if (after == before)
                Console.WriteLine($"  ✓ связь со службой: пять запросов, связок ключей не прибавилось ({after})");
            else
            {
                Console.WriteLine($"  ✗ связь со службой: было связок {before}, стало {after}");
                failures.Add($"связь со службой оставляет связки ключей: {before} → {after}");
            }

            if (answered == 0)
            {
                Console.WriteLine("  · служба не отвечает — отпечаток проверить не на чем");
                return;
            }

            // One character wrong. A client that answers anyway is trusting nobody.
            var wrong = (pin[0] == '0' ? "1" : "0") + pin[1..];
            var taken = Task.Run(() => ServiceClient.AnswersTo(wrong)).GetAwaiter().GetResult();
            if (!taken)
                Console.WriteLine("  ✓ связь со службой: сервер с чужим отпечатком отвергнут");
            else
            {
                Console.WriteLine("  ✗ связь со службой: ответила служба, которой клиент не должен был верить");
                failures.Add("связь со службой принимает сервер с неверным отпечатком");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ связь со службой: " + ex.Message);
            failures.Add("связь со службой — " + ex.Message);
        }
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

    /// <summary>A dialog is a control now: laid out inside a window, like a page.</summary>
    private static void Show(Control c)
    {
        if (c is Window w) Show(w);
        else Host(c);
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
