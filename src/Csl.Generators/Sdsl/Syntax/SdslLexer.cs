using System;
using System.Collections.Generic;
using System.Text;

namespace Csl.Generators.Sdsl.Syntax;

public enum SdslLexKind
{
    Identifier,
    Number,
    String,
    Punctuation,

    /// <summary>A preprocessor line, continuations joined: the text after '#', trimmed.</summary>
    Directive,

    End,
}

/// <summary>A token and the comments and blank lines before it.</summary>
public sealed class SdslLexToken
{
    public SdslLexToken(SdslLexKind kind, string text, int line, int column)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
    }

    public SdslLexKind Kind { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }
    public List<string>? Comments { get; set; }
    public bool BlankLineBefore { get; set; }

    /// <summary>Whitespace or a comment separates this token from the one before (so "&gt; &gt;" is two tokens).</summary>
    public bool SpaceBefore { get; set; }

    public SdslPosition Position => new SdslPosition(Line, Column);

    public bool Is(string punctuation) => Kind == SdslLexKind.Punctuation && Text == punctuation;
    public bool IsWord(string word) => Kind == SdslLexKind.Identifier && Text == word;

    public override string ToString() => Kind + " '" + Text + "' at " + Line + ":" + Column;
}

/// <summary>
/// Splits SDSL into tokens, keeping what a faithful conversion needs: comments (attached to the next
/// token), blank lines, and preprocessor lines as tokens of their own.
/// </summary>
public static class SdslLexer
{
    private static readonly string[] Punctuations =
    {
        "<<=", ">>=", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::",
        "<<", ">>",
    };

    public static List<SdslLexToken> Tokenize(string text)
    {
        var tokens = new List<SdslLexToken>();
        List<string>? comments = null;
        int i = 0, line = 1, lineStart = 0;
        int newlinesSinceToken = 0;
        bool space = false;
        bool atLineStart = true;

        void Attach(SdslLexToken token)
        {
            token.Comments = comments;
            token.BlankLineBefore = newlinesSinceToken >= 2;
            token.SpaceBefore = space;
            comments = null;
            newlinesSinceToken = 0;
            space = false;
            tokens.Add(token);
        }

        // Comments in order; an empty string stands for an empty line between them.
        void AddComment(string comment)
        {
            comments ??= new List<string>();
            if (newlinesSinceToken >= 2 && (comments.Count > 0 || tokens.Count > 0))
                comments.Add(string.Empty);
            comments.Add(comment);
            newlinesSinceToken = 0;
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                lineStart = i + 1;
                i++;
                newlinesSinceToken++;
                atLineStart = true;
                space = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                space = true;
                continue;
            }

            int column = i - lineStart + 1;

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                // A comment at the end of a line goes with the next node, as a comment line before it.
                AddComment(text.Substring(i, end - i).TrimEnd('\r', ' ', '\t'));
                i = end;
                space = true;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) end = text.Length - 2;
                var comment = text.Substring(i, Math.Min(text.Length, end + 2) - i);
                AddComment(comment.Replace("\r", string.Empty));
                for (int k = i; k < end + 2 && k < text.Length; k++)
                {
                    if (text[k] == '\n')
                    {
                        line++;
                        lineStart = k + 1;
                    }
                }
                i = end + 2;
                space = true;
                continue;
            }

            if (c == '#' && atLineStart)
            {
                // The whole line, with '\' continuations, comments at the end dropped.
                var directive = new StringBuilder();
                int j = i + 1;
                while (j < text.Length)
                {
                    if (text[j] == '\\' && (j + 1 < text.Length && (text[j + 1] == '\n' || (text[j + 1] == '\r' && j + 2 < text.Length && text[j + 2] == '\n'))))
                    {
                        j += text[j + 1] == '\r' ? 3 : 2;
                        line++;
                        lineStart = j;
                        directive.Append(' ');
                        continue;
                    }
                    if (text[j] == '\n')
                        break;
                    if (text[j] == '/' && j + 1 < text.Length && text[j + 1] == '/')
                    {
                        while (j < text.Length && text[j] != '\n') j++;
                        break;
                    }
                    directive.Append(text[j]);
                    j++;
                }
                Attach(new SdslLexToken(SdslLexKind.Directive, directive.ToString().Trim(), line, column));
                i = j;
                continue;
            }
            atLineStart = false;

            if (c == '"')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != '"')
                {
                    if (text[j] == '\\') j++;
                    j++;
                }
                Attach(new SdslLexToken(SdslLexKind.String, text.Substring(i + 1, Math.Max(0, Math.Min(j, text.Length) - i - 1)), line, column));
                i = j + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                Attach(new SdslLexToken(SdslLexKind.Identifier, text.Substring(i, j - i), line, column));
                i = j;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int j = i;
                bool hex = c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X');
                if (hex) j += 2;
                while (j < text.Length)
                {
                    char d = text[j];
                    if (char.IsLetterOrDigit(d) || d == '.')
                    {
                        j++;
                        continue;
                    }
                    if (!hex && (d == '+' || d == '-') && (text[j - 1] == 'e' || text[j - 1] == 'E'))
                    {
                        j++;
                        continue;
                    }
                    // 1.#INF and the like.
                    if (d == '#' && j > i && text[j - 1] == '.')
                    {
                        j++;
                        continue;
                    }
                    break;
                }
                Attach(new SdslLexToken(SdslLexKind.Number, text.Substring(i, j - i), line, column));
                i = j;
                continue;
            }

            string punctuation = c.ToString();
            foreach (var candidate in Punctuations)
            {
                if (string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                {
                    punctuation = candidate;
                    break;
                }
            }
            Attach(new SdslLexToken(SdslLexKind.Punctuation, punctuation, line, column));
            i += punctuation.Length;
        }

        var endToken = new SdslLexToken(SdslLexKind.End, string.Empty, line, i - lineStart + 1);
        Attach(endToken);
        return tokens;
    }
}
