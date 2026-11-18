using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmailAnalyser.Core;

namespace EmailAnalyser.App;

/// <summary>
/// Renders the window content to PNG files for reviewing the layout. Before each image it
/// checks every text in the window against the real message texts; on any match nothing is
/// saved, earlier images of the run are deleted, and only a count is written.
/// </summary>
public sealed record SensitiveText(string Kind, string Text);

public sealed class Screenshots(Window window, string directory, IReadOnlyCollection<SensitiveText> sensitive)
{
    public const string LeakFile = "VUOTO-kuvia-ei-tallennettu.txt";

    /// <summary>
    /// Bindings that show only the program's own data (model name, prompt version, label time,
    /// fixed category names). A short sender name such as a company name can match these.
    /// </summary>
    private static readonly string[] ProgramDataBindings = ["ModelInfo", "LabelInfo", "Selected.LabelInfo", "Category"];

    private readonly List<string> _saved = [];

    public bool Failed { get; private set; }

    public void Capture(string name)
    {
        if (Failed)
        {
            return;
        }

        var root = (FrameworkElement)window.Content;
        var hits = (from shown in VisibleTexts(root).Append(new ShownText("ikkunan otsikko", window.Title))
                    where !ProgramDataBindings.Any(b => shown.Source.EndsWith(" " + b, StringComparison.Ordinal))
                    let text = SafeText.Clean(shown.Text, keepNewlines: false)
                    from s in sensitive
                    where text.Contains(s.Text, StringComparison.OrdinalIgnoreCase)
                    select $"{s.Kind} ({s.Text.Length} merkkiä) -> {shown.Source}").ToList();
        if (hits.Count > 0)
        {
            Fail(name, hits);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(directory, name + ".png");
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        _saved.Add(path);
    }

    /// <summary>Distinctive prefixes of the real texts: what must never appear on screen in masked mode.</summary>
    public static IReadOnlyCollection<SensitiveText> Prefixes(IEnumerable<(string Kind, string? Text)> texts) =>
        texts.Select(t => (t.Kind, Text: SafeText.Clean(t.Text, keepNewlines: false).Trim()))
            .Where(t => t.Text.Length >= 5)
            .Select(t => new SensitiveText(t.Kind, t.Text.Length > 24 ? t.Text[..24] : t.Text))
            .DistinctBy(t => t.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Fail(string name, IReadOnlyList<string> hits)
    {
        Failed = true;
        foreach (var path in _saved)
        {
            File.Delete(path);
        }

        // Field kinds, lengths and where they showed: never the matching text itself.
        File.WriteAllLines(Path.Combine(directory, LeakFile),
            [$"Kuvassa {name} {hits.Count} osumaa oikean viestin tekstiin. Kuvia ei tallennettu.", .. hits.Distinct()]);
    }

    private sealed record ShownText(string Source, string Text);

    private static IEnumerable<ShownText> VisibleTexts(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case TextBlock block:
                    var source = BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.ParentBinding.Path?.Path
                                 ?? (block.Inlines.Count > 1 ? "monirivinen teksti" : "kiinteä teksti tai mallipohja");
                    yield return new ShownText($"TextBlock {source}", block.Text);
                    yield return new ShownText($"TextBlock-osat {source}", string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)));
                    break;
                case TextBox box:
                    yield return new ShownText("TextBox", box.Text);
                    break;
            }

            if (node is FrameworkElement { ToolTip: string tip } element)
            {
                yield return new ShownText($"vihje {element.GetType().Name}", tip);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
