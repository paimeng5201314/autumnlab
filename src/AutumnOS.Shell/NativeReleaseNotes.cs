using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AutumnOS.Shell;

/// <summary>
/// A deliberately bounded Markdown subset, rendered entirely with native text controls.
/// Raw HTML and image syntax remain text. There is no browser, script, bridge, or automatic request.
/// </summary>
internal static class NativeReleaseNotes
{
    private const int MaximumCharacters = 65536;
    private const int MaximumBlocks = 400;
    private static readonly Regex Inline = new(@"\[([^\]\r\n]{1,512})\]\(([^)\r\n]{1,2048})\)|\*\*([^*\r\n]{1,4096})\*\*|`([^`\r\n]{1,4096})`", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static FrameworkElement Create(string? markdown)
    {
        var panel = new StackPanel { Spacing = 8 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            panel.Children.Add(Text("此版本未提供发布说明"));
            return panel;
        }
        bool truncated = markdown.Length > MaximumCharacters;
        string body = markdown[..Math.Min(MaximumCharacters, markdown.Length)].Replace("\r\n", "\n").Replace('\r', '\n');
        bool fenced = false;
        var code = new StringBuilder();
        int blocks = 0;
        foreach (string source in body.Split('\n'))
        {
            if (blocks >= MaximumBlocks) { truncated = true; break; }
            string line = source;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fenced) { panel.Children.Add(Code(code.ToString())); code.Clear(); blocks++; }
                fenced = !fenced;
                continue;
            }
            if (fenced) { code.AppendLine(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var block = Text("");
            int heading = line.TakeWhile(c => c == '#').Count();
            if (heading is >= 1 and <= 6 && line.Length > heading && line[heading] == ' ')
            {
                block.FontWeight = FontWeights.SemiBold;
                block.FontSize = heading == 1 ? 22 : heading == 2 ? 19 : 16;
                line = line[(heading + 1)..];
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "• " + line[2..];
            else if (line.StartsWith("> ", StringComparison.Ordinal)) { line = line[2..]; block.Opacity = 0.8; block.Margin = new Thickness(12, 0, 0, 0); }
            AddInline(block, line);
            panel.Children.Add(block);
            blocks++;
        }
        if (code.Length > 0 && blocks < MaximumBlocks) panel.Children.Add(Code(code.ToString()));
        if (truncated) panel.Children.Add(Text("发布说明过长，已限制显示范围。可在官方 GitHub 发行页查看完整内容。"));
        return panel;
    }

    private static TextBlock Text(string value) => new()
    {
        Text = value,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        LineHeight = 24
    };

    private static Border Code(string value) => new()
    {
        Child = new TextBlock { Text = value.TrimEnd('\r', '\n'), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
        Padding = new Thickness(12),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        CornerRadius = new CornerRadius(6)
    };

    private static void AddInline(TextBlock block, string line)
    {
        try
        {
            int offset = 0;
            foreach (Match match in Inline.Matches(line))
            {
                block.Inlines.Add(new Run { Text = line[offset..match.Index] });
                if (match.Groups[1].Success && (match.Index == 0 || line[match.Index - 1] != '!') && TryExternalLink(match.Groups[2].Value, out var uri))
                {
                    var link = new Hyperlink();
                    link.Inlines.Add(new Run { Text = match.Groups[1].Value });
                    link.Click += async (_, _) =>
                    {
                        try { await Windows.System.Launcher.LaunchUriAsync(uri!); }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                    };
                    block.Inlines.Add(link);
                }
                else if (match.Groups[3].Success) block.Inlines.Add(new Bold { Inlines = { new Run { Text = match.Groups[3].Value } } });
                else if (match.Groups[4].Success) block.Inlines.Add(new Run { Text = match.Groups[4].Value, FontFamily = new FontFamily("Consolas") });
                else block.Inlines.Add(new Run { Text = match.Value });
                offset = match.Index + match.Length;
            }
            block.Inlines.Add(new Run { Text = line[offset..] });
        }
        catch (RegexMatchTimeoutException) { block.Inlines.Clear(); block.Text = line; }
    }

    private static bool TryExternalLink(string value, out Uri? uri)
    {
        uri = null;
        return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            uri.Scheme is "https" or "http" && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    }
}
