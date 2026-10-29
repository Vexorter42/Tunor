using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Tunor.Services;

/// <summary>
/// A short animated picture, as data.
///
/// The tutorial explains with moving diagrams, and a diagram that is a video file costs
/// megabytes in an installer that is otherwise a few dozen. These are not files of frames but a
/// list of shapes and, for each shape, the moments at which a property has a value; what is on
/// screen at time t is worked out here, once, for both front ends and for the program that
/// renders the same scenes into a video. A scene is a few kilobytes of JSON.
///
/// This class knows nothing about WPF or Avalonia: it hands back numbers (where, how big, how
/// opaque, which points of the line are drawn, how much of the text is typed) and a front end
/// moves its own shapes to match.
/// </summary>
public sealed class Scene
{
    public string Name { get; private set; } = "";
    public double Width { get; private set; }
    public double Height { get; private set; }

    /// <summary>Seconds from the first frame to the last, the pause on the finished picture included.</summary>
    public double Duration { get; private set; }

    public IReadOnlyList<SceneItem> Items => _items;

    private SceneItem[] _items = Array.Empty<SceneItem>();
    private Track[] _tracks = Array.Empty<Track>();

    private sealed class Track
    {
        public int Item;
        public string Prop = "";
        public double[] T = Array.Empty<double>();
        public double[] V = Array.Empty<double>();
        public char[] Ease = Array.Empty<char>();
    }

    // ------------------------------------------------------------------ loading

    /// <summary>The scene that ships inside the program, by its file name without the extension.</summary>
    public static Scene Load(string name)
    {
        var asm = typeof(Scene).Assembly;
        using var s = asm.GetManifestResourceStream("scene-" + name + ".json")
                      ?? throw new FileNotFoundException("Нет сцены «" + name + "».");
        using var r = new StreamReader(s);
        return Parse(name, r.ReadToEnd());
    }

    /// <summary>Names of the scenes that ship inside the program.</summary>
    public static IEnumerable<string> Shipped()
    {
        foreach (var n in typeof(Scene).Assembly.GetManifestResourceNames())
            if (n.StartsWith("scene-", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                yield return n.Substring(6, n.Length - 11);
    }

    public static Scene Parse(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var scene = new Scene
        {
            Name = name,
            Width = Num(root, "w", 480),
            Height = Num(root, "h", 270),
            Duration = Num(root, "dur", 0),
        };
        if (scene.Width <= 0 || scene.Height <= 0 || scene.Duration <= 0)
            throw new FormatException("Сцена «" + name + "»: размер и длительность должны быть больше нуля.");

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<SceneItem>();
        foreach (var el in root.GetProperty("items").EnumerateArray())
        {
            var item = ParseItem(el);
            if (!ids.TryAdd(item.Id, items.Count))
                throw new FormatException("Сцена «" + name + "»: два предмета «" + item.Id + "».");
            items.Add(item);
        }
        scene._items = items.ToArray();

        var tracks = new List<Track>();
        if (root.TryGetProperty("tracks", out var tr))
        {
            foreach (var el in tr.EnumerateArray())
            {
                var id = el.GetProperty("i").GetString() ?? "";
                if (!ids.TryGetValue(id, out var index))
                    throw new FormatException("Сцена «" + name + "»: дорожка про неизвестный предмет «" + id + "».");

                var prop = el.GetProperty("p").GetString() ?? "";
                if (Array.IndexOf(AnimatedProps, prop) < 0)
                    throw new FormatException("Сцена «" + name + "»: свойство «" + prop + "» не анимируется.");

                var keys = el.GetProperty("k");
                var n = keys.GetArrayLength();
                if (n == 0) continue;
                var t = new Track { Item = index, Prop = prop, T = new double[n], V = new double[n], Ease = new char[n] };
                var i = 0;
                foreach (var k in keys.EnumerateArray())
                {
                    t.T[i] = k[0].GetDouble();
                    t.V[i] = k[1].GetDouble();
                    t.Ease[i] = k.GetArrayLength() > 2 ? EaseCode(k[2].GetString()) : 'x';
                    if (i > 0 && t.T[i] < t.T[i - 1])
                        throw new FormatException("Сцена «" + name + "»: ключи «" + id + "." + prop + "» идут не по порядку.");
                    i++;
                }
                tracks.Add(t);
            }
        }
        scene._tracks = tracks.ToArray();
        return scene;
    }

    private static readonly string[] AnimatedProps = { "x", "y", "w", "h", "o", "s", "draw", "n" };

    private static SceneItem ParseItem(JsonElement el)
    {
        var kind = (el.GetProperty("k").GetString() ?? "") switch
        {
            "rect" => SceneKind.Rect,
            "circle" => SceneKind.Circle,
            "poly" => SceneKind.Poly,
            "text" => SceneKind.Text,
            var other => throw new FormatException("Неизвестный вид предмета «" + other + "»."),
        };
        var item = new SceneItem
        {
            Id = el.GetProperty("id").GetString() ?? "",
            Kind = kind,
            X = Num(el, "x", 0), Y = Num(el, "y", 0),
            W = Num(el, "w", 0), H = Num(el, "h", 0), R = Num(el, "r", 0),
            Size = Num(el, "size", 13), StrokeWidth = Num(el, "sw", 1.5),
            Opacity = Num(el, "o", 1), Scale = Num(el, "s", 1),
            Draw = Num(el, "draw", 1), Typed = Num(el, "n", 1),
            Fill = Str(el, "fill"), Stroke = Str(el, "stroke"),
            Text = Str(el, "text") ?? "", TextColor = Str(el, "tc") ?? "text",
            Align = Str(el, "align") ?? "c",
            Bold = el.TryGetProperty("bold", out var b) && b.ValueKind == JsonValueKind.True,
            Closed = el.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
        };
        if (string.IsNullOrEmpty(item.Id)) throw new FormatException("Предмет без имени.");

        if (kind == SceneKind.Poly)
        {
            var pts = new List<(double, double)>();
            foreach (var p in el.GetProperty("pts").EnumerateArray())
                pts.Add((p[0].GetDouble(), p[1].GetDouble()));
            if (pts.Count < 2) throw new FormatException("«" + item.Id + "»: в линии меньше двух точек.");
            item.Points = pts.ToArray();
        }
        return item;
    }

    private static double Num(JsonElement el, string name, double fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static char EaseCode(string? s) => s switch
    {
        "l" => 'l',      // linear
        "i" => 'i',      // slow start
        "o" => 'o',      // slow end
        "b" => 'b',      // slow end, with a little overshoot: things that pop in
        "h" => 'h',      // hold: the value changes at the next key and not before
        "io" => 'x',
        null => 'x',
        _ => throw new FormatException("Неизвестное сглаживание «" + s + "»."),
    };

    // ------------------------------------------------------------------ evaluating

    /// <summary>
    /// What every item looks like at time <paramref name="t"/>, in the order of <see cref="Items"/>.
    /// The array may be handed back in again to avoid allocating one per frame.
    /// </summary>
    public ItemState[] At(double t, ItemState[]? reuse = null)
    {
        var states = reuse is { } r && r.Length == _items.Length ? r : new ItemState[_items.Length];

        for (var i = 0; i < _items.Length; i++)
        {
            var it = _items[i];
            states[i] = new ItemState
            {
                X = it.X, Y = it.Y, W = it.W, H = it.H,
                Opacity = it.Opacity, Scale = it.Scale, Draw = it.Draw, Typed = it.Typed,
            };
        }

        foreach (var tr in _tracks)
        {
            var v = Sample(tr, t);
            ref var s = ref states[tr.Item];
            switch (tr.Prop)
            {
                case "x": s.X = v; break;
                case "y": s.Y = v; break;
                case "w": s.W = v; break;
                case "h": s.H = v; break;
                case "o": s.Opacity = v; break;
                case "s": s.Scale = v; break;
                case "draw": s.Draw = v; break;
                case "n": s.Typed = v; break;
            }
        }

        for (var i = 0; i < _items.Length; i++) Finish(_items[i], ref states[i]);
        return states;
    }

    private static double Sample(Track tr, double t)
    {
        var n = tr.T.Length;
        if (t <= tr.T[0]) return tr.V[0];
        if (t >= tr.T[n - 1]) return tr.V[n - 1];

        var i = 0;
        while (i < n - 2 && t >= tr.T[i + 1]) i++;

        var span = tr.T[i + 1] - tr.T[i];
        if (span <= 0) return tr.V[i + 1];
        var u = Ease(tr.Ease[i], (t - tr.T[i]) / span);
        return tr.V[i] + (tr.V[i + 1] - tr.V[i]) * u;
    }

    internal static double Ease(char e, double u)
    {
        u = Math.Clamp(u, 0, 1);
        switch (e)
        {
            case 'l': return u;
            case 'i': return u * u * u;
            case 'o': return 1 - Math.Pow(1 - u, 3);
            case 'h': return 0;
            case 'b':
                // easeOutBack: runs past the end and comes back
                const double c1 = 1.70158, c3 = c1 + 1;
                var m = u - 1;
                return 1 + c3 * m * m * m + c1 * m * m;
            default:
                return u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
        }
    }

    /// <summary>Turns the animated numbers into what is drawn: final geometry, the typed part of a text, the drawn part of a line.</summary>
    private static void Finish(SceneItem it, ref ItemState s)
    {
        s.Opacity = Math.Clamp(s.Opacity, 0, 1);
        var k = s.Scale;

        switch (it.Kind)
        {
            case SceneKind.Rect:
            {
                // Scaled about its centre.
                var cx = s.X + s.W / 2;
                var cy = s.Y + s.H / 2;
                s.W *= k; s.H *= k;
                s.X = cx - s.W / 2; s.Y = cy - s.H / 2;
                s.R = it.R * k;
                s.Size = it.Size * k;
                s.StrokeWidth = it.StrokeWidth;
                s.Text = Typed(it.Text, s.Typed);
                break;
            }
            case SceneKind.Circle:
                s.R = it.R * k;
                s.StrokeWidth = it.StrokeWidth;
                break;
            case SceneKind.Text:
                s.Size = it.Size * k;
                s.Text = Typed(it.Text, s.Typed);
                break;
            case SceneKind.Poly:
            {
                s.StrokeWidth = it.StrokeWidth;
                var src = it.Points!;
                var pts = new List<(double X, double Y)>(src.Length + 1);
                foreach (var (px, py) in src) pts.Add((s.X + px * k, s.Y + py * k));
                // A filled outline is not drawn on, it is just there; only an open line grows.
                if (!it.Closed && s.Draw < 1) pts = Partial(pts, Math.Clamp(s.Draw, 0, 1));
                s.Points = pts.ToArray();
                break;
            }
        }
    }

    private static string Typed(string text, double fraction)
    {
        if (fraction >= 1 || text.Length == 0) return text;
        var count = (int)Math.Floor(Math.Clamp(fraction, 0, 1) * text.Length);
        return count <= 0 ? "" : text.Substring(0, count);
    }

    /// <summary>The first <paramref name="fraction"/> of a line's length, ending part-way along a segment.</summary>
    private static List<(double X, double Y)> Partial(List<(double X, double Y)> pts, double fraction)
    {
        var lengths = new double[pts.Count - 1];
        double total = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = Math.Sqrt(Math.Pow(pts[i + 1].X - pts[i].X, 2) + Math.Pow(pts[i + 1].Y - pts[i].Y, 2));
            total += lengths[i];
        }
        var want = total * fraction;
        var result = new List<(double X, double Y)> { pts[0] };
        if (want <= 0) return result;

        double done = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (done + lengths[i] <= want)
            {
                result.Add(pts[i + 1]);
                done += lengths[i];
                continue;
            }
            var u = lengths[i] <= 0 ? 0 : (want - done) / lengths[i];
            result.Add((pts[i].X + (pts[i + 1].X - pts[i].X) * u, pts[i].Y + (pts[i + 1].Y - pts[i].Y) * u));
            break;
        }
        return result;
    }
}

public enum SceneKind { Rect, Circle, Poly, Text }

/// <summary>What an item is made of, as written in the scene file.</summary>
public sealed class SceneItem
{
    public string Id = "";
    public SceneKind Kind;
    public double X, Y, W, H, R, Size, StrokeWidth, Opacity, Scale, Draw, Typed;
    public string? Fill, Stroke;
    public string Text = "", TextColor = "text", Align = "c";
    public bool Bold, Closed;
    public (double X, double Y)[]? Points;
}

/// <summary>Where an item is, how it looks and how much of it shows at one moment.</summary>
public struct ItemState
{
    /// <summary>Rect: top-left corner. Circle: centre. Text: the anchor. Line: where its (0,0) is.</summary>
    public double X, Y;
    public double W, H, R, Size, StrokeWidth;
    public double Opacity, Scale, Draw, Typed;
    public string? Text;
    public (double X, double Y)[]? Points;
}

/// <summary>The colours scenes are drawn in: the app's own palette, so the pictures look like the program.</summary>
public static class SceneColors
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.Ordinal)
    {
        ["bg"] = 0xFF1A1E25,
        ["deep"] = 0xFF11141A,
        ["panel"] = 0xFF242932,
        ["input"] = 0xFF1F242B,
        ["hover"] = 0xFF2E343E,
        ["border"] = 0xFF3A404A,
        ["text"] = 0xFFF4F6F7,
        ["dim"] = 0xFF959DA9,
        ["accent"] = 0xFF3DDC5C,
        ["soft"] = 0x383DDC5C,
        ["ink"] = 0xFF06120A,
        ["danger"] = 0xFFFF6B6B,
        ["dangersoft"] = 0x38FF6B6B,
        ["warn"] = 0xFFFFC857,
        ["blue"] = 0xFF59A5FF,
        ["bluesoft"] = 0x3859A5FF,
    };

    /// <summary>ARGB for a palette name or a literal <c>#RRGGBB</c> / <c>#AARRGGBB</c>; null for null or "none".</summary>
    public static uint? Resolve(string? token)
    {
        if (string.IsNullOrEmpty(token) || token == "none") return null;
        if (Named.TryGetValue(token, out var argb)) return argb;
        if (token[0] == '#' && uint.TryParse(token.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return token.Length == 7 ? 0xFF000000 | v : v;
        throw new FormatException("Нет цвета «" + token + "».");
    }
}
