using System.Collections.Generic;

namespace Csl.Generators.Sdsl;

public enum SdslTokenKind
{
    Identifier,
    Number,
    String,
    Punctuation,
    End,
}

/// <summary>One token of an SDSL file, with the doc comment lines (///) that immediately preceded it.</summary>
public sealed class SdslToken
{
    public SdslToken(SdslTokenKind kind, string text, int line, int column, List<string>? doc)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
        Doc = doc;
    }

    public SdslTokenKind Kind { get; }
    public string Text { get; }

    /// <summary>1-based.</summary>
    public int Line { get; }

    /// <summary>1-based.</summary>
    public int Column { get; }

    public List<string>? Doc { get; }

    public bool Is(string text) => Kind == SdslTokenKind.Punctuation && Text == text;
    public bool IsIdentifier(string text) => Kind == SdslTokenKind.Identifier && Text == text;

    public override string ToString() => $"{Kind} '{Text}' at {Line}:{Column}";
}

/// <summary>
/// Splits SDSL into tokens. Comments and preprocessor lines are dropped, except that /// lines are
/// kept and attached to the token that follows them, so a member's documentation reaches the wrapper.
/// </summary>
public static class SdslTokenizer
{
    private static readonly string[] MultiCharPunctuation =
    {
        "<<=", ">>=", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::",
    };

    public static List<SdslToken> Tokenize(string text)
    {
        var tokens = new List<SdslToken>();
        List<string>? doc = null;
        int i = 0, line = 1, lineStart = 0;

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Comments. A /// line is documentation for whatever comes next.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                if (i + 2 < text.Length && text[i + 2] == '/' && (i + 3 >= text.Length || text[i + 3] != '/'))
                {
                    doc ??= new List<string>();
                    doc.Add(text.Substring(i + 3, end - i - 3).Trim());
                }
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                if (end < 0) end = text.Length - 2;
                for (int k = i; k < end + 2 && k < text.Length; k++)
                {
                    if (text[k] == '\n')
                    {
                        line++;
                        lineStart = k + 1;
                    }
                }
                i = end + 2;
                continue;
            }

            // Preprocessor: whole line dropped (the engine's own bases use it for defaults only).
            if (c == '#')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                i = end;
                continue;
            }

            int column = i - lineStart + 1;

            if (c == '"')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '"')
                {
                    if (text[j] == '\\') j++;
                    j++;
                }
                tokens.Add(new SdslToken(SdslTokenKind.String, text.Substring(i + 1, System.Math.Max(0, j - i - 1)), line, column, doc));
                doc = null;
                i = j + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                tokens.Add(new SdslToken(SdslTokenKind.Identifier, text.Substring(i, j - i), line, column, doc));
                doc = null;
                i = j;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '.' ||
                                           ((text[j] == '+' || text[j] == '-') && (text[j - 1] == 'e' || text[j - 1] == 'E'))))
                    j++;
                tokens.Add(new SdslToken(SdslTokenKind.Number, text.Substring(i, j - i), line, column, doc));
                doc = null;
                i = j;
                continue;
            }

            string punct = c.ToString();
            foreach (var candidate in MultiCharPunctuation)
            {
                if (string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                {
                    punct = candidate;
                    break;
                }
            }
            tokens.Add(new SdslToken(SdslTokenKind.Punctuation, punct, line, column, doc));
            doc = null;
            i += punct.Length;
        }

        tokens.Add(new SdslToken(SdslTokenKind.End, string.Empty, line, i - lineStart + 1, null));
        return tokens;
    }
}
