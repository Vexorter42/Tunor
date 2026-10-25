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
