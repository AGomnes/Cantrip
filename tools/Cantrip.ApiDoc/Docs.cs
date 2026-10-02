using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Cantrip.ApiDoc;

/// <summary>One symbol's documentation comment, already turned into Markdown.</summary>
public sealed class DocComment
{
    public static readonly DocComment Empty = new DocComment();

    public string Summary { get; init; } = string.Empty;
    public string Remarks { get; init; } = string.Empty;
    public string Returns { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Example { get; init; } = string.Empty;
    public List<(string Name, string Text)> Parameters { get; init; } = new List<(string, string)>();
    public List<(string Name, string Text)> TypeParameters { get; init; } = new List<(string, string)>();
    public List<(string Name, string Text)> Exceptions { get; init; } = new List<(string, string)>();

    public bool HasSummary => Summary.Length > 0;
    public bool IsEmpty => !HasSummary && Remarks.Length == 0 && Returns.Length == 0 && Value.Length == 0
        && Parameters.Count == 0 && TypeParameters.Count == 0 && Exceptions.Count == 0 && Example.Length == 0;
}

/// <summary>Reads a symbol's XML documentation comment and renders it as Markdown.</summary>
public static class Docs
{
    public static DocComment Read(ISymbol symbol)
    {
        string xml = symbol.GetDocumentationCommentXml(preferredCulture: null, expandIncludes: false) ?? string.Empty;
        if (xml.Length == 0) return DocComment.Empty;

        XElement root;
        try { root = XElement.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return DocComment.Empty; }

        // An `<inheritdoc/>` with nothing else in it documents nothing a reader can read here, so
        // it counts as undocumented rather than quietly passing for prose.
        return new DocComment
        {
            Summary = Text(root.Element("summary")),
            Remarks = Text(root.Element("remarks")),
            Returns = Text(root.Element("returns")),
            Value = Text(root.Element("value")),
            Example = Text(root.Element("example")),
            Parameters = Named(root, "param", "name"),
            TypeParameters = Named(root, "typeparam", "name"),
            Exceptions = Named(root, "exception", "cref"),
        };
    }

    private static List<(string, string)> Named(XElement root, string element, string attribute)
    {
        var found = new List<(string, string)>();
        foreach (XElement child in root.Elements(element))
        {
            string name = (string?)child.Attribute(attribute) ?? string.Empty;
            if (attribute == "cref") name = Crefs.Short(name);
            found.Add((name, Text(child)));
        }

        return found;
    }

    private static string Text(XElement? element)
    {
        if (element == null) return string.Empty;

        var text = new StringBuilder();
        Write(element, text);
        return Tidy(text.ToString());
    }

    private static void Write(XNode node, StringBuilder text)
    {
        switch (node)
        {
            case XText content:
                text.Append(Collapse(content.Value));
                return;

            case XElement element:
                switch (element.Name.LocalName)
                {
                    case "para":
                        Block(element, text);
                        return;

                    case "c":
                        text.Append('`').Append(Collapse(element.Value).Trim()).Append('`');
                        return;

                    case "code":
                        text.Append("\n\n```\n").Append(Dedent(element.Value)).Append("\n```\n\n");
                        return;

                    case "see" or "seealso":
                    {
                        string? langword = (string?)element.Attribute("langword");
                        if (langword != null) { text.Append('`').Append(langword).Append('`'); return; }

                        string? href = (string?)element.Attribute("href");
                        if (href != null)
                        {
                            string label = element.Value.Trim();
                            text.Append('[').Append(label.Length > 0 ? label : href).Append("](").Append(href).Append(')');
                            return;
                        }

                        string? cref = (string?)element.Attribute("cref");
                        if (cref != null)
                        {
                            string label = element.Value.Trim();
                            text.Append('`').Append(label.Length > 0 ? label : Crefs.Short(cref)).Append('`');
                            return;
                        }

                        text.Append(Collapse(element.Value));
                        return;
                    }

                    case "paramref" or "typeparamref":
                        text.Append('`').Append((string?)element.Attribute("name") ?? string.Empty).Append('`');
                        return;

                    case "b" or "strong":
                        text.Append("**").Append(Inline(element)).Append("**");
                        return;

                    case "i" or "em":
                        text.Append('*').Append(Inline(element)).Append('*');
                        return;

                    case "br":
                        text.Append("  \n");
                        return;

                    case "list":
                        List(element, text);
                        return;

                    default:
                        foreach (XNode child in element.Nodes()) Write(child, text);
                        return;
                }
        }
    }

    private static void Block(XElement element, StringBuilder text)
    {
        text.Append("\n\n");
        foreach (XNode child in element.Nodes()) Write(child, text);
        text.Append("\n\n");
    }

    private static string Inline(XElement element)
    {
        var text = new StringBuilder();
        foreach (XNode child in element.Nodes()) Write(child, text);
        return text.ToString().Trim();
    }

    private static void List(XElement element, StringBuilder text)
    {
        string kind = (string?)element.Attribute("type") ?? "bullet";
        text.Append("\n\n");

        int number = 1;
        foreach (XElement item in element.Elements("item"))
        {
            XElement? term = item.Element("term");
            XElement? description = item.Element("description");

            var body = new StringBuilder();
            if (term != null) body.Append("**").Append(Inline(term)).Append(":** ");
            if (description != null) body.Append(Inline(description));
            else if (term == null) body.Append(Inline(item));

            string marker = kind == "number" ? (number++) + ". " : "- ";
            text.Append(marker).Append(Tidy(body.ToString()).Replace("\n", " ")).Append('\n');
        }

        text.Append('\n');
    }

    /// <summary>
    /// Documentation comments are indented and line-wrapped; prose is not. A run of whitespace
    /// becomes one space, and a leading or trailing one is kept: the space between
    /// <c>&lt;/c&gt;</c> and the next word lives at the start of the next text node, and dropping
    /// it welds the two together.
    /// </summary>
    private static string Collapse(string value)
    {
        var text = new StringBuilder(value.Length);
        bool space = false;

        foreach (char c in value)
        {
            if (c is ' ' or '\t' or '\r' or '\n') { space = true; continue; }
            if (space) text.Append(' ');
            space = false;
            text.Append(c);
        }

        if (space) text.Append(' ');
        return text.ToString();
    }

    private static string Dedent(string value)
    {
        string[] lines = value.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines);

        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[kept.Count - 1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        if (kept.Count == 0) return string.Empty;

        int indent = kept.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", kept.Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart()));
    }

    /// <summary>Squeezes the blank lines a <c>&lt;para&gt;</c> leaves behind, and trims the ends.</summary>
    private static string Tidy(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        bool blank = true;
        bool fenced = false;

        foreach (string line in lines)
        {
            // Inside a fence the indentation is the content; outside it a stray leading space is
            // only where a `<para>` ended, and four of them would turn a paragraph into code.
            string trimmed = fenced ? line.TrimEnd() : line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;

            if (!fenced && trimmed.Length == 0)
            {
                if (!blank) kept.Add(string.Empty);
                blank = true;
                continue;
            }

            kept.Add(trimmed);
            blank = false;
        }

        while (kept.Count > 0 && kept[kept.Count - 1].Length == 0) kept.RemoveAt(kept.Count - 1);
        return string.Join("\n", kept).Trim();
    }
}

/// <summary>Turns a <c>cref</c> such as <c>T:Cantrip.Num</c> into something a reader recognises.</summary>
/// <remarks>
/// The letter in front of a documentation-comment id says what it names, so no guessing is needed:
/// <c>T:</c> is a type and reads as its own name, and <c>M:</c>, <c>P:</c>, <c>F:</c> and
/// <c>E:</c> are members and read as <c>Type.Member</c>. A parameter list is shortened the same
/// way, so <c>M:Cantrip.CardRuntime.Tick(System.Int32)</c> reads as <c>CardRuntime.Tick(int)</c>
/// rather than as a line of namespaces.
/// </remarks>
public static class Crefs
{
    private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Boolean"] = "bool", ["Byte"] = "byte", ["SByte"] = "sbyte", ["Char"] = "char",
        ["Int16"] = "short", ["UInt16"] = "ushort", ["Int32"] = "int", ["UInt32"] = "uint",
        ["Int64"] = "long", ["UInt64"] = "ulong", ["Single"] = "float", ["Double"] = "double",
        ["Decimal"] = "decimal", ["String"] = "string", ["Object"] = "object", ["Void"] = "void",
    };

    public static string Short(string cref)
    {
        string name = cref;
        char kind = name.Length > 2 && name[1] == ':' ? name[0] : 'M';
        if (name.Length > 2 && name[1] == ':') name = name.Substring(2);

        int parenthesis = name.IndexOf('(');
        string arguments = string.Empty;
        if (parenthesis >= 0)
        {
            arguments = Arguments(name.Substring(parenthesis + 1).TrimEnd(')'));
            name = name.Substring(0, parenthesis);
        }

        string[] parts = name.Split('.');
        if (parts.Length == 0) return cref;

        string last = parts[parts.Length - 1];

        // A constructor reads as its own type: `CardRuntime`, not `CardRuntime.#ctor`.
        if (last == "#ctor") return (parts.Length >= 2 ? parts[parts.Length - 2] : last) + arguments;

        string shortened = kind == 'T' || parts.Length < 2
            ? last
            : parts[parts.Length - 2] + "." + last;

        return shortened + arguments;
    }

    private static string Arguments(string arguments)
    {
        if (arguments.Length == 0) return "()";

        var parts = new List<string>();
        int depth = 0, start = 0;

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c is '{' or '(') depth++;
            else if (c is '}' or ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(arguments.Substring(start, i - start));
                start = i + 1;
            }
        }

        parts.Add(arguments.Substring(start));
        return "(" + string.Join(", ", parts.Select(Argument)) + ")";
    }

    private static string Argument(string argument)
    {
        string suffix = string.Empty;
        string name = argument.Trim();

        while (name.EndsWith("[]", StringComparison.Ordinal) || name.EndsWith("@", StringComparison.Ordinal))
        {
            suffix = (name.EndsWith("@", StringComparison.Ordinal) ? string.Empty : "[]") + suffix;
            name = name.Substring(0, name.Length - (name.EndsWith("@", StringComparison.Ordinal) ? 1 : 2));
        }

        int brace = name.IndexOf('{');
        string generic = string.Empty;
        if (brace >= 0)
        {
            generic = "<" + string.Join(", ", Split(name.Substring(brace + 1).TrimEnd('}')).Select(Argument)) + ">";
            name = name.Substring(0, brace);
        }

        int backtick = name.IndexOf('`');
        if (backtick >= 0) name = name.Substring(0, backtick);

        string last = name.Split('.').Last();
        return (Keywords.TryGetValue(last, out string? keyword) ? keyword : last) + generic + suffix;
    }

    private static IEnumerable<string> Split(string arguments)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return arguments.Substring(start, i - start);
                start = i + 1;
            }
        }

        yield return arguments.Substring(start);
    }
}
