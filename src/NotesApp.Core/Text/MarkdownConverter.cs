using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NotesApp.Core.Text;

// Minimal Markdown -> HTML converter for rendering AI output in the rich editor.
// Handles tables, headings, bold, and bullet lists; everything else becomes a
// line. Not a full Markdown implementation - just enough to make AI results
// (especially tables) render as real formatting instead of literal syntax.
public static class MarkdownConverter
{
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var inList = false;
        var i = 0;

        void CloseList()
        {
            if (inList)
            {
                sb.Append("</ul>");
                inList = false;
            }
        }

        while (i < lines.Length)
        {
            var line = lines[i];

            // A table is a row of pipes immediately followed by a |---|---| separator.
            if (IsTableRow(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                CloseList();
                i = AppendTable(sb, lines, i);
                continue;
            }

            var trimmed = line.TrimStart();
            // Skip ``` code-fence lines the model sometimes wraps output in.
            if (trimmed.StartsWith("```")) { i++; continue; }
            if (trimmed.StartsWith("### ")) { CloseList(); sb.Append($"<h3>{Inline(trimmed[4..])}</h3>"); }
            else if (trimmed.StartsWith("## ")) { CloseList(); sb.Append($"<h2>{Inline(trimmed[3..])}</h2>"); }
            else if (trimmed.StartsWith("# ")) { CloseList(); sb.Append($"<h1>{Inline(trimmed[2..])}</h1>"); }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                if (!inList) { sb.Append("<ul>"); inList = true; }
                sb.Append($"<li>{Inline(trimmed[2..])}</li>");
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                CloseList();
            }
            else
            {
                CloseList();
                sb.Append($"<div>{Inline(line)}</div>");
            }

            i++;
        }

        CloseList();
        return sb.ToString();
    }

    private static bool IsTableRow(string line)
    {
        var l = line.Trim();
        return l.StartsWith("|") && l.Length > 1;
    }

    private static bool IsTableSeparator(string line)
    {
        var l = line.Trim();
        return l.Contains('-') && Regex.IsMatch(l, @"^\|?[\s:|-]+\|?$");
    }

    private static int AppendTable(StringBuilder sb, string[] lines, int start)
    {
        var header = SplitRow(lines[start]);
        sb.Append("<table><tr>");
        foreach (var cell in header)
        {
            sb.Append($"<th>{Inline(cell)}</th>");
        }
        sb.Append("</tr>");

        var i = start + 2; // skip the header row and the separator row
        while (i < lines.Length && IsTableRow(lines[i]))
        {
            sb.Append("<tr>");
            foreach (var cell in SplitRow(lines[i]))
            {
                sb.Append($"<td>{Inline(cell)}</td>");
            }
            sb.Append("</tr>");
            i++;
        }

        sb.Append("</table>");
        return i;
    }

    private static string[] SplitRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

    // Escapes HTML, then turns **bold** into <b>.
    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        return Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<b>$1</b>");
    }
}
