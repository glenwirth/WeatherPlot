using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

class MdToHtml
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: MdToHtml <input.md> <output.html> [title]");
            return 1;
        }
        string title = args.Length >= 3 ? args[2] : Path.GetFileNameWithoutExtension(args[0]);
        string md = File.ReadAllText(args[0]);
        string html = Convert(md, title);
        File.WriteAllText(args[1], html, new UTF8Encoding(false));
        Console.WriteLine("Wrote " + args[1] + " (" + new FileInfo(args[1]).Length + " bytes)");
        return 0;
    }

    static string Convert(string md, string title)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\"><title>");
        sb.Append(WebUtility.HtmlEncode(title));
        sb.Append("</title><style>").Append(Css).Append("</style></head><body><main>");

        bool inCode = false;
        var codeLines = new List<string>();
        var tableRows = new List<string[]>();
        bool tableHadSeparator = false;
        var listBuf = new List<string>();
        char listKind = '\0'; // '-' for ul, 'o' for ol
        var bqLines = new List<string>();

        Action flushBlockquote = () =>
        {
            if (bqLines.Count == 0) return;
            sb.Append("<blockquote>");
            // Group consecutive non-empty lines into paragraphs; blank stripped lines separate them.
            var para = new StringBuilder();
            Action emitPara = () =>
            {
                if (para.Length == 0) return;
                sb.Append("<p>").Append(InlineFormat(para.ToString().TrimEnd())).Append("</p>");
                para.Length = 0;
            };
            foreach (var bl in bqLines)
            {
                if (string.IsNullOrWhiteSpace(bl)) { emitPara(); continue; }
                if (para.Length > 0) para.Append(' ');
                para.Append(bl);
            }
            emitPara();
            sb.Append("</blockquote>");
            bqLines.Clear();
        };

        Action flushTable = () =>
        {
            if (tableRows.Count == 0) return;
            sb.Append("<table>");
            for (int r = 0; r < tableRows.Count; r++)
            {
                string tag = (r == 0 && tableHadSeparator) ? "th" : "td";
                sb.Append("<tr>");
                foreach (var cell in tableRows[r])
                {
                    sb.Append("<").Append(tag).Append(">")
                      .Append(InlineFormat(cell))
                      .Append("</").Append(tag).Append(">");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table>");
            tableRows.Clear();
            tableHadSeparator = false;
        };

        Action flushList = () =>
        {
            if (listBuf.Count == 0) return;
            string tag = listKind == 'o' ? "ol" : "ul";
            sb.Append("<").Append(tag).Append(">");
            foreach (var item in listBuf)
                sb.Append("<li>").Append(InlineFormat(item)).Append("</li>");
            sb.Append("</").Append(tag).Append(">");
            listBuf.Clear();
            listKind = '\0';
        };

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // --- Code fence ---
            if (Regex.IsMatch(line, @"^```"))
            {
                if (inCode)
                {
                    sb.Append("<pre><code>");
                    bool first = true;
                    foreach (var cl in codeLines)
                    {
                        if (!first) sb.Append('\n');
                        sb.Append(WebUtility.HtmlEncode(cl));
                        first = false;
                    }
                    sb.Append("</code></pre>");
                    codeLines.Clear();
                    inCode = false;
                }
                else
                {
                    flushTable(); flushList(); flushBlockquote();
                    inCode = true;
                }
                continue;
            }
            if (inCode) { codeLines.Add(line); continue; }

            // --- Blockquote line ---
            // Match "> rest" OR a bare ">" continuation line.
            var bqm = Regex.Match(line, @"^>\s?(.*)$");
            if (bqm.Success)
            {
                flushTable(); flushList();
                bqLines.Add(bqm.Groups[1].Value);
                continue;
            }
            flushBlockquote();

            // --- Table row ---
            if (line.StartsWith("|") && line.TrimEnd().EndsWith("|"))
            {
                // Separator row (|---|---|) ?
                if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$"))
                {
                    tableHadSeparator = true;
                    continue;
                }
                flushList();
                tableRows.Add(SplitTableRow(line));
                continue;
            }
            // Leaving table?
            flushTable();

            // --- Heading ---
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (hm.Success)
            {
                flushList();
                int level = hm.Groups[1].Value.Length;
                sb.Append("<h").Append(level).Append(">")
                  .Append(InlineFormat(hm.Groups[2].Value))
                  .Append("</h").Append(level).Append(">");
                continue;
            }

            // --- Horizontal rule ---
            if (Regex.IsMatch(line.Trim(), @"^-{3,}$") || Regex.IsMatch(line.Trim(), @"^_{3,}$"))
            {
                flushList();
                sb.Append("<hr>");
                continue;
            }

            // --- Unordered list item ---
            var um = Regex.Match(line, @"^\s*[-*+]\s+(.+)$");
            if (um.Success)
            {
                if (listKind != '\0' && listKind != '-') flushList();
                listKind = '-';
                listBuf.Add(um.Groups[1].Value);
                continue;
            }

            // --- Ordered list item ---
            var om = Regex.Match(line, @"^\s*\d+\.\s+(.+)$");
            if (om.Success)
            {
                if (listKind != '\0' && listKind != 'o') flushList();
                listKind = 'o';
                listBuf.Add(om.Groups[1].Value);
                continue;
            }
            flushList();

            // --- Blank line ---
            if (string.IsNullOrWhiteSpace(line)) continue;

            // --- Image-only paragraph (start of line) ---
            var im = Regex.Match(line.Trim(), @"^!\[([^\]]*)\]\(([^)]+)\)\s*$");
            if (im.Success)
            {
                sb.Append("<figure><img alt=\"")
                  .Append(WebUtility.HtmlEncode(im.Groups[1].Value))
                  .Append("\" src=\"")
                  .Append(WebUtility.HtmlEncode(im.Groups[2].Value))
                  .Append("\"></figure>");
                continue;
            }

            // --- Default: paragraph ---
            sb.Append("<p>").Append(InlineFormat(line)).Append("</p>");
        }

        flushTable(); flushList(); flushBlockquote();
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    static string[] SplitTableRow(string line)
    {
        line = line.Trim();
        if (line.StartsWith("|")) line = line.Substring(1);
        if (line.EndsWith("|")) line = line.Substring(0, line.Length - 1);
        return line.Split('|').Select(c => c.Trim()).ToArray();
    }

    // Inline formatting: code spans, bold, italic, links, images.
    // Strategy: extract <code> spans first (with their contents HTML-escaped) to placeholders,
    // then HTML-escape the rest, then apply other inline transforms, then restore placeholders.
    static string InlineFormat(string s)
    {
        var spans = new List<string>();
        s = Regex.Replace(s, @"`([^`]+)`", m =>
        {
            spans.Add("<code>" + WebUtility.HtmlEncode(m.Groups[1].Value) + "</code>");
            return "CODE" + (spans.Count - 1) + "";
        });

        s = WebUtility.HtmlEncode(s);

        // After HtmlEncode, our placeholder  chars survive but won't conflict with anything.
        // Bold **x**
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        // Italic *x* (but not inside already-tagged tokens — simple regex is fine here)
        s = Regex.Replace(s, @"(?<!\*)\*([^*\s][^*]*?)\*(?!\*)", "<em>$1</em>");
        // Image ![alt](url)
        s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)]+)\)", "<img alt=\"$1\" src=\"$2\">");
        // Link [text](url)
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "<a href=\"$2\">$1</a>");

        s = Regex.Replace(s, @"CODE(\d+)", m => spans[int.Parse(m.Groups[1].Value)]);
        return s;
    }

    static readonly string Css = @"
@page { size: Letter; margin: 0.75in 0.85in; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body {
  font-family: 'Segoe UI', -apple-system, 'Helvetica Neue', Arial, sans-serif;
  font-size: 10.5pt;
  line-height: 1.45;
  color: #1f2328;
  background: #ffffff;
  -webkit-font-smoothing: antialiased;
}
main { max-width: 7.0in; margin: 0 auto; }
h1, h2, h3, h4, h5, h6 {
  color: #0a1929;
  margin: 1.4em 0 0.5em;
  line-height: 1.25;
  font-weight: 600;
}
h1 { font-size: 22pt; border-bottom: 2px solid #d0d7de; padding-bottom: 0.3em; margin-top: 0.4em; }
h2 { font-size: 16pt; border-bottom: 1px solid #d8dee4; padding-bottom: 0.25em; margin-top: 1.6em; page-break-before: auto; }
h3 { font-size: 13pt; }
h4 { font-size: 11.5pt; color: #424a53; }
p { margin: 0.5em 0 0.7em; }
a { color: #0969da; text-decoration: none; }
a:hover { text-decoration: underline; }
strong { font-weight: 600; color: #0a1929; }
em { font-style: italic; }
hr {
  border: 0;
  border-top: 1px solid #d0d7de;
  margin: 1.5em 0;
}
ul, ol { margin: 0.4em 0 0.9em; padding-left: 1.7em; }
li { margin: 0.18em 0; }
code {
  font-family: 'Cascadia Mono', 'Consolas', 'Menlo', 'Courier New', monospace;
  font-size: 9.5pt;
  background: #f3f4f6;
  color: #24292f;
  padding: 0.12em 0.35em;
  border-radius: 3px;
  border: 1px solid #e6e9ee;
}
pre {
  background: #f6f8fa;
  border: 1px solid #d8dee4;
  border-radius: 5px;
  padding: 10px 12px;
  overflow-x: auto;
  page-break-inside: avoid;
  margin: 0.8em 0 1em;
  font-size: 9pt;
  line-height: 1.4;
}
pre code {
  background: none;
  border: 0;
  padding: 0;
  font-size: 9pt;
  color: #1f2328;
  white-space: pre;
}
table {
  border-collapse: collapse;
  width: 100%;
  margin: 0.7em 0 1.1em;
  font-size: 9.5pt;
  page-break-inside: auto;
}
th, td {
  border: 1px solid #d0d7de;
  padding: 6px 9px;
  text-align: left;
  vertical-align: top;
}
th {
  background: #f6f8fa;
  font-weight: 600;
  color: #0a1929;
}
tbody tr:nth-child(even) { background: #fafbfc; }
img { max-width: 100%; height: auto; }
figure { margin: 1em 0; text-align: center; page-break-inside: avoid; }
blockquote {
  border-left: 4px solid #d0d7de;
  margin: 0.8em 0;
  padding: 0.1em 1em;
  color: #57606a;
}
/* Avoid page breaks splitting tables and headings awkwardly */
h1, h2, h3, h4 { page-break-after: avoid; }
table, pre, figure { page-break-inside: avoid; }
";
}
