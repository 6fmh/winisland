using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Winisland.Controls;

public sealed class ReleaseNotesLinkEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}

// A small, read-only Markdown surface. Release text is always constructed as
// document objects: no HTML/XAML evaluation, image loading or automatic navigation.
public sealed class ReleaseNotesView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(ReleaseNotesView),
        new PropertyMetadata("", (owner, _) => ((ReleaseNotesView)owner).Rebuild()));
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}\s+(.*?)(?:\s+#+)?$", RegexOptions.CultureInvariant, ParseTimeout);
    private static readonly Regex ListLine = new(@"^(?<indent>[ \t]*)(?<marker>[-*+]|\d+[.)])\s+(?<body>.+)$", RegexOptions.CultureInvariant, ParseTimeout);
    private static readonly Regex InlineTokens = new(
        @"(?<ticks>`+)(?<code>.*?)\k<ticks>|\*\*(?<strong>.+?)\*\*|__(?<strong>.+?)__|\*(?<em>[^*]+)\*|(?<![\p{L}\p{N}])_(?<em>[^_]+)_(?![\p{L}\p{N}])|~~(?<strike>.+?)~~|(?<image>!?)\[(?<label>[^\]]+)\]\((?<uri>\S+?)(?:\s+""[^""]*"")?\)|<(?<auto>https://[^>]+)>",
        RegexOptions.CultureInvariant, ParseTimeout);
    private static readonly Brush CodeFill = new SolidColorBrush(Color.FromRgb(35, 35, 38));
    private static readonly FontFamily CodeFont = new("Consolas");

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public event EventHandler<ReleaseNotesLinkEventArgs>? LinkRequested;

    public ReleaseNotesView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        AcceptsTab = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        FontSize = 13;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(FontFamilyProperty, "UiFont");
        Rebuild();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (Document is not null && (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == ForegroundProperty))
            ApplyDocumentStyle(Document);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_ContentHost") is not ScrollViewer viewer) return;
        viewer.ApplyTemplate();
        // The system ScrollViewer puts a local metric width on its scrollbar,
        // which takes precedence over the notes' scoped style setter.
        if (viewer.Template.FindName("PART_VerticalScrollBar", viewer) is FrameworkElement scrollbar)
        {
            scrollbar.MinWidth = 0;
            scrollbar.Width = 8;
        }
    }

    private void ApplyDocumentStyle(FlowDocument document)
    {
        document.FontFamily = FontFamily;
        document.FontSize = FontSize;
        document.Foreground = Foreground;
        document.LineHeight = 20;
        document.TextAlignment = TextAlignment.Left;
        document.PagePadding = new Thickness(0, 0, 12, 0);
    }

    private void Rebuild()
    {
        var document = new FlowDocument();
        ApplyDocumentStyle(document);
        var source = Markdown ?? "";
        // The release client already bounds the body. Also bound direct callers.
        source = source[..Math.Min(source.Length, 20000)].Replace("\r\n", "\n").Replace('\r', '\n');
        try
        {
            source = Regex.Replace(source, @"<!--[\s\S]*?-->", "", RegexOptions.CultureInvariant, ParseTimeout);
            source = Regex.Replace(source, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, ParseTimeout);
            var lines = source.Split('\n');
            for (var index = 0; index < lines.Length;)
            {
                var line = lines[index];
                if (string.IsNullOrWhiteSpace(line)) { index++; continue; }
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    var fence = trimmed[0];
                    var length = 0;
                    while (length < trimmed.Length && trimmed[length] == fence) length++;
                    var code = new StringBuilder();
                    index++;
                    while (index < lines.Length && !lines[index].TrimStart().StartsWith(new string(fence, length), StringComparison.Ordinal))
                    { if (code.Length > 0) code.Append('\n'); code.Append(lines[index++]); }
                    if (index < lines.Length) index++;
                    document.Blocks.Add(new Paragraph(new Run(code.ToString()))
                    { FontFamily = CodeFont, FontSize = 12, LineHeight = 18, Background = CodeFill, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10) });
                    continue;
                }
                var heading = Heading.Match(line);
                var setext = index + 1 < lines.Length && Regex.IsMatch(lines[index + 1], @"^ {0,3}(?:=+|-{3,})\s*$", RegexOptions.CultureInvariant, ParseTimeout);
                if (heading.Success || setext)
                {
                    var paragraph = ParagraphFor(heading.Success ? heading.Groups[1].Value : line.Trim());
                    paragraph.FontSize = 14;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Foreground = Brushes.White;
                    paragraph.Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 10, 0, 6);
                    document.Blocks.Add(paragraph);
                    index += setext ? 2 : 1;
                    continue;
                }
                if (ListLine.IsMatch(line)) { AddList(document.Blocks, lines, ref index, 0); continue; }
                if (Regex.IsMatch(line, @"^ {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.CultureInvariant, ParseTimeout))
                { index++; continue; }
                if (trimmed.StartsWith('>'))
                {
                    var quote = ParagraphFor(trimmed.TrimStart('>', ' '));
                    quote.Margin = new Thickness(12, 0, 0, 8);
                    document.Blocks.Add(quote);
                    index++;
                    continue;
                }
                var text = new StringBuilder(line.Trim());
                index++;
                while (index < lines.Length && !IsBlockStart(lines[index])
                    && !(index + 1 < lines.Length && Regex.IsMatch(lines[index + 1], @"^ {0,3}(?:=+|-{3,})\s*$", RegexOptions.CultureInvariant, ParseTimeout)))
                { text.Append(' ').Append(lines[index++].Trim()); }
                document.Blocks.Add(ParagraphFor(text.ToString()));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Malformed release text must not make the update page unusable.
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run(source)) { Margin = new Thickness(0) });
        }
        Document = document;
    }

    private static bool IsBlockStart(string line) => string.IsNullOrWhiteSpace(line) || Heading.IsMatch(line) || ListLine.IsMatch(line)
        || line.TrimStart().StartsWith('>') || line.TrimStart().StartsWith("```", StringComparison.Ordinal)
        || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

    private void AddList(BlockCollection blocks, string[] lines, ref int index, int depth)
    {
        var first = ListLine.Match(lines[index]);
        var indent = first.Groups["indent"].Length;
        var ordered = char.IsDigit(first.Groups["marker"].Value[0]);
        var list = new System.Windows.Documents.List
        { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(18, 0, 0, 0), MarkerOffset = 5, Margin = new Thickness(0, 0, 0, 8) };
        if (ordered && int.TryParse(first.Groups["marker"].Value.TrimEnd('.', ')'), out var start)) list.StartIndex = Math.Clamp(start, 1, 10000);
        while (index < lines.Length)
        {
            var item = ListLine.Match(lines[index]);
            if (!item.Success || item.Groups["indent"].Length != indent || char.IsDigit(item.Groups["marker"].Value[0]) != ordered) break;
            var text = new StringBuilder(item.Groups["body"].Value);
            index++;
            while (index < lines.Length && !IsBlockStart(lines[index]) && lines[index].Length - lines[index].TrimStart().Length > indent)
                text.Append(' ').Append(lines[index++].Trim());
            var paragraph = ParagraphFor(text.ToString());
            paragraph.Margin = new Thickness(0, 0, 0, 5);
            var entry = new ListItem(paragraph);
            while (depth < 4 && index < lines.Length && ListLine.Match(lines[index]) is { Success: true } nested && nested.Groups["indent"].Length > indent)
                AddList(entry.Blocks, lines, ref index, depth + 1);
            list.ListItems.Add(entry);
        }
        blocks.Add(list);
    }

    private Paragraph ParagraphFor(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AddInlines(paragraph.Inlines, text, 0);
        return paragraph;
    }

    private void AddInlines(InlineCollection inlines, string text, int depth)
    {
        var offset = 0;
        foreach (Match token in InlineTokens.Matches(text))
        {
            if (token.Index > 0 && text[token.Index - 1] == '\\') continue;
            if (token.Index > offset) inlines.Add(new Run(Plain(text[offset..token.Index])));
            if (token.Groups["code"].Success)
                inlines.Add(new Run(token.Groups["code"].Value) { FontFamily = CodeFont, FontSize = 12, Background = CodeFill });
            else if (token.Groups["strong"].Success || token.Groups["em"].Success || token.Groups["strike"].Success)
            {
                var span = new Span();
                var value = token.Groups["strong"].Success ? token.Groups["strong"].Value : token.Groups["em"].Success ? token.Groups["em"].Value : token.Groups["strike"].Value;
                if (token.Groups["strong"].Success) span.FontWeight = FontWeights.SemiBold;
                if (token.Groups["em"].Success) span.FontStyle = FontStyles.Italic;
                if (token.Groups["strike"].Success) span.TextDecorations = TextDecorations.Strikethrough;
                if (depth < 4) AddInlines(span.Inlines, value, depth + 1); else span.Inlines.Add(new Run(Plain(value)));
                inlines.Add(span);
            }
            else
            {
                var label = token.Groups["auto"].Success ? token.Groups["auto"].Value : token.Groups["label"].Value;
                var address = token.Groups["auto"].Success ? label : token.Groups["uri"].Value;
                if (token.Groups["image"].Value != "!" && Uri.TryCreate(WebUtility.HtmlDecode(address), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0)
                {
                    var link = new Hyperlink { NavigateUri = uri, Foreground = (Brush?)TryFindResource("ActivityBlue") ?? Brushes.LightSkyBlue, ToolTip = uri.AbsoluteUri };
                    if (depth < 4) AddInlines(link.Inlines, label, depth + 1); else link.Inlines.Add(new Run(Plain(label)));
                    link.RequestNavigate += OnRequestNavigate;
                    inlines.Add(link);
                }
                else inlines.Add(new Run(Plain(label)));
            }
            offset = token.Index + token.Length;
        }
        if (offset < text.Length) inlines.Add(new Run(Plain(text[offset..])));
    }

    private static string Plain(string text) => WebUtility.HtmlDecode(Regex.Replace(text, @"\\([\\`*_{}\[\]()#+.!>~-])", "$1", RegexOptions.CultureInvariant, ParseTimeout));
    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        LinkRequested?.Invoke(this, new ReleaseNotesLinkEventArgs(e.Uri));
    }
}
