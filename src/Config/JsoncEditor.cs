namespace CSRoll.Config;

/// <summary>
/// Edits values in a JSONC document (config.jsonc) by key path, leaving everything else - comments,
/// formatting, key order - exactly as it was. Paths are what the config binder sees, so they're
/// matched case-insensitively, and the same key name in two sections ("Enabled" is in several) never
/// gets confused for another.
///
/// The scanner reads JSON plus // and /* */ comments and trailing commas, which is what the config
/// provider accepts. It records where each key's value sits in the text, and where each object's
/// body starts, so an edit is one splice.
/// </summary>
public static class JsoncEditor
{
    private const char Separator = '\u001f';

    /// <summary>
    /// Sets the value at path (e.g. ["Main", "Rarity", "Gold"]) to json, a JSON literal. A key the file
    /// doesn't have yet is added at the top of its object, and any missing parent objects with it.
    /// With onlyIfPresent, a missing key is left missing.
    /// </summary>
    /// <exception cref="FormatException">The text isn't a JSON object this scanner can read.</exception>
    public static string Set(string text, IReadOnlyList<string> path, string json, bool onlyIfPresent = false)
    {
        var doc = Scan(text);
        if (doc.Values.TryGetValue(Join(path, path.Count), out var span))
        {
            return Splice(text, span.Start, span.End, json);
        }

        if (onlyIfPresent)
        {
            return text;
        }

        // The deepest ancestor the file has: add the key there, nested in whatever is missing between.
        for (var depth = path.Count - 1; depth >= 0; depth--)
        {
            var prefix = Join(path, depth);
            if (doc.Objects.TryGetValue(prefix, out var open))
            {
                return Insert(text, open, $"{Quote(path[depth])}: {Nest(path, depth + 1, json)}");
            }

            // There, but not an object ("Rarity": null) - a second key of the same name would be a
            // duplicate the config provider refuses, so the value itself is replaced.
            if (depth > 0 && doc.Values.TryGetValue(prefix, out var parent))
            {
                return Splice(text, parent.Start, parent.End, Nest(path, depth, json));
            }
        }

        throw new FormatException("the file has no top-level object");
    }

    private static string Splice(string text, int start, int end, string value) => text[..start] + value + text[end..];

    /// <summary>The keys from path[from] on wrapped around json: ["A", "B"] gives { "A": { "B": json } }.</summary>
    private static string Nest(IReadOnlyList<string> path, int from, string json)
    {
        var value = json;
        for (var i = path.Count - 1; i >= from; i--)
        {
            value = $"{{ {Quote(path[i])}: {value} }}";
        }

        return value;
    }

    /// <summary>Adds an entry right after an object's opening brace, indented like the object's first line.</summary>
    private static string Insert(string text, int open, string entry)
    {
        var first = open;
        while (first < text.Length && char.IsWhiteSpace(text[first]))
        {
            first++;
        }

        if (first < text.Length && text[first] == '}')
        {
            return text.Insert(open, $" {entry} ");
        }

        // On its own line when the object's first entry is, keeping that line's indent and line ending.
        var lineStart = text.LastIndexOf('\n', first - 1);
        if (lineStart < open)
        {
            return text.Insert(open, $" {entry},");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return text.Insert(open, $"{newline}{text[(lineStart + 1)..first]}{entry},");
    }

    private static string Quote(string key) => System.Text.Json.JsonSerializer.Serialize(key);

    private static string Join(IReadOnlyList<string> path, int count) => string.Join(Separator, path.Take(count));

    // -------------------------------------------------------------------------------------------------
    // Scanner
    // -------------------------------------------------------------------------------------------------

    private sealed class Document
    {
        /// <summary>Every key's value, as [Start, End) in the text, by joined path.</summary>
        public readonly Dictionary<string, (int Start, int End)> Values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every object's body start (just past its brace), by joined path; "" is the root.</summary>
        public readonly Dictionary<string, int> Objects = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Document Scan(string text)
    {
        var doc = new Document();
        var i = 0;
        SkipTrivia(text, ref i);
        if (i >= text.Length || text[i] != '{')
        {
            throw new FormatException("the file doesn't start with an object");
        }

        ScanValue(text, ref i, "", doc);
        return doc;
    }

    private static void ScanValue(string t, ref int i, string path, Document doc)
    {
        switch (Peek(t, i))
        {
            case '{':
                doc.Objects[path] = ++i;
                while (true)
                {
                    SkipTrivia(t, ref i);
                    var c = Peek(t, i);
                    if (c == '}')
                    {
                        i++;
                        return;
                    }

                    if (c == ',')
                    {
                        i++;
                        continue;
                    }

                    var key = ReadString(t, ref i);
                    SkipTrivia(t, ref i);
                    if (Peek(t, i) != ':')
                    {
                        throw new FormatException($"expected ':' after \"{key}\"");
                    }

                    i++;
                    SkipTrivia(t, ref i);

                    var child = path.Length == 0 ? key : path + Separator + key;
                    var start = i;
                    ScanValue(t, ref i, child, doc);
                    doc.Values[child] = (start, i);
                }

            case '[':
                i++;
                while (true)
                {
                    SkipTrivia(t, ref i);
                    var c = Peek(t, i);
                    if (c == ']')
                    {
                        i++;
                        return;
                    }

                    if (c == ',')
                    {
                        i++;
                        continue;
                    }

                    // Elements aren't addressable; a path with a NUL in it can't match a real key.
                    ScanValue(t, ref i, path + Separator + '\0', doc);
                }

            case '"':
                ReadString(t, ref i);
                return;

            default:
                var from = i;
                while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not (',' or '}' or ']' or '/'))
                {
                    i++;
                }

                if (i == from)
                {
                    throw new FormatException($"unexpected '{t[i]}' at {i}");
                }

                return;
        }
    }

    private static char Peek(string t, int i) =>
        i < t.Length ? t[i] : throw new FormatException("the file ends in the middle of a value");

    private static string ReadString(string t, ref int i)
    {
        if (Peek(t, i) != '"')
        {
            throw new FormatException($"expected a key at {i}");
        }

        var start = ++i;
        while (Peek(t, i) != '"')
        {
            i += t[i] == '\\' ? 2 : 1;
        }

        return t[start..i++];
    }

    private static void SkipTrivia(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i]))
            {
                i++;
            }
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
            {
                while (i < t.Length && t[i] != '\n')
                {
                    i++;
                }
            }
            else if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                var end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? t.Length : end + 2;
            }
            else
            {
                return;
            }
        }
    }
}
