using System.Text;
using System.Text.RegularExpressions;

namespace PulseBar.Utilities;

/// <summary>Turns simple release-notes markdown into plain text for a dialog (no HTML, no links).</summary>
public static partial class MarkdownText
{
    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "No release notes.";
        }

        var output = new StringBuilder();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();

            // "## Added" -> "Added"; "- item" / "* item" -> "• item"
            line = Heading().Replace(line, "");
            line = Bullet().Replace(line, "$1• ");
            line = Link().Replace(line, "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            output.AppendLine(line);
        }

        return output.ToString().Trim();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
}
