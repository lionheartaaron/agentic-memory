using System.Text;
using System.Text.RegularExpressions;

namespace AgenticMemory.Helpers;

/// <summary>
/// Extracts a priority-ordered structural summary of a source file for LLM context.
/// Two-pass design: collect structural elements with their doc summaries, then emit
/// in priority order: file header → type declarations → public API → internals → fields.
/// </summary>
public static partial class CodeContextExtractor
{
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;

    private enum Language { CSharp, TypeScript, JavaScript, Python, Generic }

    // Access rank: lower = higher priority in output
    private enum AccessRank { Public = 0, Internal = 1, Private = 2 }

    private enum ElementKind { Namespace, TypeDecl, Method, Property }

    private sealed record CodeElement(
        ElementKind Kind,
        string      Signature,   // cleaned declaration line (no opening brace)
        string?     DocSummary,  // one-line summary extracted from the preceding comment block
        AccessRank  Access
    );

    // ── Public API ────────────────────────────────────────────────────────────

    public static string ExtractContext(string filePath, int maxLines = 100, int maxLineLength = 200)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var fi = new FileInfo(filePath);
            if (fi.Length > MaxFileSizeBytes || IsBinary(filePath)) return string.Empty;

            var lang = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".cs"                               => Language.CSharp,
                ".ts" or ".tsx"                     => Language.TypeScript,
                ".js" or ".jsx" or ".mjs" or ".cjs" => Language.JavaScript,
                ".py"                               => Language.Python,
                _                                   => Language.Generic
            };

            var lines = File.ReadAllLines(filePath);

            var (elements, fileSummary, imports) = lang == Language.Python
                ? CollectPython(lines)
                : CollectBraceBased(lines, lang);

            return FormatOutput(Path.GetFileName(filePath), elements, fileSummary, imports, maxLines, maxLineLength);
        }
        catch (IOException)                 { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    // ── Pass 1: Collect structural elements ───────────────────────────────────

    private static (List<CodeElement> elements, string? fileSummary, List<string> imports)
        CollectBraceBased(string[] lines, Language lang)
    {
        var elements   = new List<CodeElement>();
        var docBuffer  = new List<string>();   // accumulates comment lines until consumed by a declaration
        var imports    = new List<string>();
        string? fileSummary    = null;
        bool    inBlockComment = false;
        bool    fileSummarySet = false;
        int     depth          = 0;

        // C# nests namespace { class { member { } } } — structural content at depth 0–2.
        // TS/JS nests class { member { } } — structural content at depth 0–1.
        int maxStructuralDepth = lang == Language.CSharp ? 2 : 1;

        foreach (var raw in lines)
        {
            var t = raw.TrimStart();

            // ── Block comment ─────────────────────────────────────────────────
            if (inBlockComment)
            {
                docBuffer.Add(raw);
                if (t.Contains("*/")) inBlockComment = false;
                // No TrackBraces — braces inside /* */ must not affect depth
                continue;
            }
            if (t.StartsWith("/*"))
            {
                docBuffer.Add(raw);
                if (!t.Contains("*/")) inBlockComment = true;
                continue;
            }

            // ── Single-line comment (// and ///) ─────────────────────────────
            if (t.StartsWith("//"))
            {
                docBuffer.Add(raw);
                continue;
            }

            // ── Blank line: flush orphaned doc buffer as file summary ─────────
            if (string.IsNullOrWhiteSpace(t))
            {
                if (!fileSummarySet && docBuffer.Count > 0)
                {
                    fileSummary    = DrainDocSummary(docBuffer);
                    fileSummarySet = true;
                }
                else
                {
                    docBuffer.Clear();
                }
                TrackBraces(t, ref depth);
                continue;
            }

            // ── Import / using ────────────────────────────────────────────────
            if (depth == 0 && IsImportLine(t, lang))
            {
                CollectImportName(t, lang, imports);
                docBuffer.Clear();
                TrackBraces(t, ref depth);
                continue;
            }

            // ── Structural line? ──────────────────────────────────────────────
            bool isStructural = depth == 0
                || (depth <= maxStructuralDepth && IsDeclarationLine(t, lang));

            if (isStructural)
            {
                fileSummarySet = true; // first declaration marks end of file header zone
                string? summary = docBuffer.Count > 0 ? DrainDocSummary(docBuffer) : null;
                docBuffer.Clear();

                var el = ClassifyElement(t, lang, summary);
                if (el != null) elements.Add(el);
            }
            else
            {
                // Body content: discard any accumulated doc (it belonged to a skipped line)
                docBuffer.Clear();
            }

            TrackBraces(t, ref depth);
        }

        return (elements, fileSummary, imports);
    }

    private static (List<CodeElement>, string?, List<string>) CollectPython(string[] lines)
    {
        var elements    = new List<CodeElement>();
        var imports     = new List<string>();
        string? fileSummary    = null;
        bool    fileSummarySet = false;
        bool    prevWasDef     = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t   = raw.TrimStart();

            if (string.IsNullOrWhiteSpace(t)) { prevWasDef = false; continue; }

            if (t.StartsWith("import ") || t.StartsWith("from "))
            {
                CollectPyImportName(t, imports);
                prevWasDef = false;
                continue;
            }

            // Decorator, def, async def, class
            if (t.StartsWith("@") || t.StartsWith("def ") || t.StartsWith("async def ") || t.StartsWith("class "))
            {
                fileSummarySet = true;
                var el = ClassifyPyElement(t);
                if (el != null) elements.Add(el);
                prevWasDef = !t.StartsWith('@');
                continue;
            }

            // Docstring
            if (t.StartsWith("\"\"\"") || t.StartsWith("'''"))
            {
                string closing = t.StartsWith("\"\"\"") ? "\"\"\"" : "'''";
                var docLines   = new List<string> { t };
                bool selfClose = t.Length > 3 && t[3..].Contains(closing);

                if (!selfClose)
                {
                    while (++i < lines.Length)
                    {
                        docLines.Add(lines[i]);
                        if (lines[i].Contains(closing)) break;
                    }
                }

                string? summary = ExtractPyDocSummary(docLines);

                if (!fileSummarySet)
                {
                    fileSummary    = summary;
                    fileSummarySet = true;
                }
                else if (prevWasDef && elements.Count > 0)
                {
                    // Attach to the most recent def/class element
                    elements[^1] = elements[^1] with { DocSummary = summary };
                }

                prevWasDef = false;
                continue;
            }

            // Module-level comments feed into file summary
            if (t.StartsWith("#") && !fileSummarySet)
            {
                var text = t[1..].Trim();
                fileSummary = fileSummary == null ? text : fileSummary + " " + text;
            }

            prevWasDef = false;
        }

        return (elements, fileSummary?.Trim(), imports);
    }

    // ── Element classification ────────────────────────────────────────────────

    private static CodeElement? ClassifyElement(string t, Language lang, string? summary)
    {
        // Standalone braces carry no information
        if (t is "{" or "}") return null;

        // Namespace / module declaration
        if (t.StartsWith("namespace ") || t.StartsWith("module "))
        {
            var sig = t.TrimEnd('{', ';', ' ').Trim();
            return new CodeElement(ElementKind.Namespace, sig, summary, AccessRank.Public);
        }

        var stripped = StripModifiers(t, lang);

        // Type declarations (class, interface, enum, struct, record, type alias)
        if (TypeKeywordPattern().IsMatch(stripped))
        {
            // Remove the opening brace so the signature stays on one clean line
            var sig = t.Contains('{') ? t[..t.LastIndexOf('{')].TrimEnd() : t.TrimEnd(';');
            return new CodeElement(ElementKind.TypeDecl, sig, summary, AccessRank.Public);
        }

        var rank = DetermineRank(t);

        // Method (has parentheses)
        if (t.Contains('('))
        {
            var sig = t.Contains('{') ? t[..t.LastIndexOf('{')].TrimEnd() : t.TrimEnd(';', ' ');
            return new CodeElement(ElementKind.Method, sig, summary, rank);
        }

        // Property / field / constant
        var propSig = t.TrimEnd(';', ' ');
        return new CodeElement(ElementKind.Property, propSig, summary, rank);
    }

    private static CodeElement? ClassifyPyElement(string t)
    {
        if (t.StartsWith("class "))
            return new CodeElement(ElementKind.TypeDecl, t.TrimEnd(':', ' '), null, AccessRank.Public);

        if (t.StartsWith("def ") || t.StartsWith("async def "))
        {
            var nameStart = t.StartsWith("async def ") ? 10 : 4;
            var name      = t[nameStart..].TrimStart();
            var rank      = name.StartsWith('_') ? AccessRank.Private : AccessRank.Public;
            return new CodeElement(ElementKind.Method, t.TrimEnd(':', ' '), null, rank);
        }

        return null;
    }

    private static AccessRank DetermineRank(string t)
    {
        var s = t.TrimStart();
        if (s.StartsWith("private "))   return AccessRank.Private;
        if (s.StartsWith("protected ") || s.StartsWith("internal ")) return AccessRank.Internal;
        return AccessRank.Public;
    }

    // ── Pass 2: Format output ─────────────────────────────────────────────────

    private static string FormatOutput(
        string            fileName,
        List<CodeElement> elements,
        string?           fileSummary,
        List<string>      imports,
        int               maxLines,
        int               maxLineLength)
    {
        var sb        = new StringBuilder();
        int lineCount = 0;

        void Emit(string line)
        {
            if (lineCount >= maxLines) return;
            sb.AppendLine(Clip(line, maxLineLength));
            lineCount++;
        }

        void Section(string header, IEnumerable<CodeElement> members, bool includeSummary)
        {
            var list = members.ToList();
            if (list.Count == 0 || lineCount >= maxLines) return;
            Emit("");
            Emit(header);
            foreach (var m in list)
            {
                if (lineCount >= maxLines) break;
                var line = (includeSummary && m.DocSummary != null)
                    ? $"  {m.Signature}  // {m.DocSummary}"
                    : $"  {m.Signature}";
                Emit(line);
            }
        }

        // 1. File header — always first, always included
        Emit($"// FILE: {fileName}");
        if (fileSummary != null)
            Emit($"// {fileSummary}");

        // 2. Import summary — one compact line instead of 20 import lines
        if (imports.Count > 0)
        {
            const int ShowMax = 8;
            var shown = string.Join(", ", imports.Take(ShowMax));
            var extra = imports.Count > ShowMax ? $" (+{imports.Count - ShowMax} more)" : "";
            Emit($"// imports: {shown}{extra}");
        }

        // 3. Namespace and type declarations — orient the LLM to what this file defines
        var namespaces = elements.Where(e => e.Kind == ElementKind.Namespace).ToList();
        var types      = elements.Where(e => e.Kind == ElementKind.TypeDecl).ToList();

        if (namespaces.Count > 0 || types.Count > 0)
        {
            Emit("");
            foreach (var ns in namespaces) Emit(ns.Signature);
            foreach (var td in types)
            {
                Emit(td.Signature);
                if (td.DocSummary != null) Emit($"  // {td.DocSummary}");
            }
        }

        // 4–6. Members in priority order: public → internal/protected → private
        //      Public members get doc summaries inline; private ones get signatures only.
        Section(
            "// PUBLIC",
            elements.Where(e => e.Kind is ElementKind.Method or ElementKind.Property && e.Access == AccessRank.Public),
            includeSummary: true);

        Section(
            "// INTERNAL",
            elements.Where(e => e.Kind is ElementKind.Method or ElementKind.Property && e.Access == AccessRank.Internal),
            includeSummary: false);

        Section(
            "// PRIVATE",
            elements.Where(e => e.Kind is ElementKind.Method or ElementKind.Property && e.Access == AccessRank.Private),
            includeSummary: false);

        return sb.ToString().TrimEnd();
    }

    // ── Doc summary extraction ────────────────────────────────────────────────

    // Pulls a single-sentence summary from the buffered comment lines, then clears the buffer.
    private static string? DrainDocSummary(List<string> lines)
    {
        string? result    = null;
        bool    inSummary = false;

        foreach (var raw in lines)
        {
            var t = NormalizeCommentLine(raw);
            if (t.Length == 0) continue;

            // C# XML doc <summary> tag
            if (t.StartsWith("<summary>", StringComparison.OrdinalIgnoreCase))
            {
                inSummary = true;
                var inline = t
                    .Replace("<summary>",  "", StringComparison.OrdinalIgnoreCase)
                    .Replace("</summary>", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                if (inline.Length > 0) { result = inline; break; }
                continue;
            }
            if (inSummary)
            {
                if (t.Contains("</summary>", StringComparison.OrdinalIgnoreCase)) break;
                if (!t.StartsWith('<')) { result = t; break; }
                continue;
            }

            // JSDoc / plain comment: first real content line that isn't a tag
            if (!t.StartsWith('@') && !t.StartsWith('<') && t is not ("/*" or "/**" or "*/"))
            {
                result = t;
                break;
            }
        }

        lines.Clear();
        return result;
    }

    // Strips leading whitespace and comment markers to get at the raw text.
    private static string NormalizeCommentLine(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("///")) return t[3..].TrimStart();
        if (t.StartsWith("**"))  return t[2..].TrimStart();
        if (t.StartsWith("* "))  return t[2..];
        if (t.StartsWith('*'))   return t[1..].TrimStart();
        if (t.StartsWith("//"))  return t[2..].TrimStart();
        if (t.StartsWith("/*"))  return t[2..].TrimStart().TrimEnd('*', ' ', '/');
        return t;
    }

    private static string? ExtractPyDocSummary(List<string> lines)
    {
        foreach (var line in lines)
        {
            var t = line.TrimStart().Trim('"', '\'').Trim();
            if (t.Length > 0) return t;
        }
        return null;
    }

    // ── Import name extraction ────────────────────────────────────────────────

    private static void CollectImportName(string t, Language lang, List<string> imports)
    {
        if (lang == Language.CSharp)
        {
            // "using AgenticMemory.Brain.Search;" → "Search"
            var ns   = t.TrimEnd(';').Replace("using ", "").Trim();
            var leaf = ns.Split('.').LastOrDefault();
            if (!string.IsNullOrEmpty(leaf)) imports.Add(leaf);
        }
        else
        {
            // Named: "import { Foo, Bar as B } from './foo'"
            int open  = t.IndexOf('{');
            int close = t.IndexOf('}');
            if (open >= 0 && close > open)
            {
                foreach (var part in t[(open + 1)..close].Split(','))
                {
                    var name = part.Trim().Split(' ')[0].Trim(); // drop "as Alias"
                    if (!string.IsNullOrEmpty(name)) imports.Add(name);
                }
            }
            else
            {
                // Default: "import Foo from './foo'"
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "import"
                    && !parts[1].StartsWith('\'') && !parts[1].StartsWith('"'))
                    imports.Add(parts[1]);
            }
        }
    }

    private static void CollectPyImportName(string t, List<string> imports)
    {
        // "from x import Foo, Bar" → names after "import"
        // "import Foo, Bar"         → names after "import"
        int idx   = t.IndexOf(" import ");
        var names = idx >= 0 ? t[(idx + 8)..] : t["import ".Length..];
        foreach (var part in names.Split(','))
        {
            var name = part.Trim().Split(' ')[0].Trim(); // drop "as alias"
            if (!string.IsNullOrEmpty(name) && !name.StartsWith('*')) imports.Add(name);
        }
    }

    // ── Brace / declaration helpers ───────────────────────────────────────────

    // Counts { and } while ignoring characters inside strings and after // comments.
    // inStr is local per call; multi-line strings may cause minor depth drift, but
    // those appear in method bodies (which we skip) so it has no practical effect.
    private static void TrackBraces(string line, ref int depth)
    {
        bool inStr = false;
        char delim = '\0';

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inStr)
            {
                if (c == delim && (i == 0 || line[i - 1] != '\\')) inStr = false;
            }
            else if (c is '"' or '\'' or '`') { inStr = true; delim = c; }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            else if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
        }
    }

    private static bool IsImportLine(string t, Language lang) => lang switch
    {
        Language.CSharp                            => t.StartsWith("using ") && !t.Contains('('),
        Language.TypeScript or Language.JavaScript => t.StartsWith("import ") || t.StartsWith("require("),
        _                                          => false
    };

    private static bool IsDeclarationLine(string t, Language lang)
    {
        if (NonDeclarationPattern().IsMatch(t)) return false;
        if (TypeKeywordPattern().IsMatch(StripModifiers(t, lang))) return true;
        if (lang == Language.CSharp) return CSharpModPattern().IsMatch(t);
        if (TsJsLeadPattern().IsMatch(t)) return true;
        if (t.StartsWith("function ") || t.StartsWith("async function ")) return true;
        // Object-literal method shorthand: "name: (args) =>" and "name: async (args) =>"
        if (TsObjectMethodPattern().IsMatch(t)) return true;
        return t.Contains('(') && TsMethodShorthandPattern().IsMatch(t);
    }

    private static string StripModifiers(string t, Language lang)
    {
        var pat = lang == Language.CSharp ? CSharpModPattern() : TsJsModPattern();
        string prev;
        do { prev = t; t = pat.Replace(t, "").TrimStart(); }
        while (t != prev);
        return t;
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    private static bool IsBinary(string filePath)
    {
        Span<byte> buf = stackalloc byte[512];
        using var fs = File.OpenRead(filePath);
        int read = fs.Read(buf);
        return buf[..read].IndexOf((byte)0) >= 0;
    }

    private static string Clip(string line, int max) =>
        line.Length > max ? string.Concat(line.AsSpan(0, max), "…") : line;

    // ── Compiled regex patterns ───────────────────────────────────────────────

    [GeneratedRegex(@"^(if\s*\(|else\b|for\s*\(|foreach\s*\(|while\s*\(|do\b|switch\s*\(|case\b|default:|return\b|throw\b|yield\b|try\b|catch\b|finally\b|break\b|continue\b|use[A-Z]\w*\s*\()")]
    private static partial Regex NonDeclarationPattern();

    [GeneratedRegex(@"^(class|interface|enum|struct|record|type\s+\w|namespace|module|abstract\s+class)\b")]
    private static partial Regex TypeKeywordPattern();

    [GeneratedRegex(@"^(public|private|protected|internal|static|sealed|abstract|partial|readonly|async|virtual|override|new|extern|unsafe|const|event)\s+")]
    private static partial Regex CSharpModPattern();

    [GeneratedRegex(@"^(export|default|declare|abstract|async|readonly|static|public|private|protected|override)\s+")]
    private static partial Regex TsJsModPattern();

    [GeneratedRegex(@"^(public|private|protected|static|async|readonly|abstract|override|declare|export|get|set|constructor)\b")]
    private static partial Regex TsJsLeadPattern();

    [GeneratedRegex(@"^\*?\w+\s*(<[^>]*>)?\s*\(.*\)\s*(\{|=>|:\s*\w)")]
    private static partial Regex TsMethodShorthandPattern();

    // Matches object-literal arrow-function properties: "name: (args) =>" and "name: async (args) =>"
    [GeneratedRegex(@"^[\w$][\w$]*\s*:\s*(async\s+)?\(")]
    private static partial Regex TsObjectMethodPattern();
}
