using System;
using System.Collections.Generic;
using System.Globalization;

namespace Csl.Cpu;

/// <summary>
/// The macros an effect is compiled with, for shader code run on the CPU: what <c>Sdsl.If("A &amp;&amp; B &gt; 2")</c>
/// tests and <c>Sdsl.Macro("ThreadNumberX")</c> reads. The defaults are those the engine gives every
/// Direct3D 11 effect.
/// </summary>
public sealed class Macros
{
    private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> parsed = new System.Collections.Concurrent.ConcurrentDictionary<string, object>(StringComparer.Ordinal);

    public static Macros Direct3D11()
    {
        var macros = new Macros();
        macros.Set("STRIDE_GRAPHICS_API_DIRECT3D", "1");
        macros.Set("STRIDE_GRAPHICS_API_DIRECT3D11", "1");
        macros.Set("STRIDE_GRAPHICS_PROFILE", "0xb000");
        macros.Set("GRAPHICS_PROFILE_LEVEL_9_1", "0x9100");
        macros.Set("GRAPHICS_PROFILE_LEVEL_9_2", "0x9200");
        macros.Set("GRAPHICS_PROFILE_LEVEL_9_3", "0x9300");
        macros.Set("GRAPHICS_PROFILE_LEVEL_10_0", "0xa000");
        macros.Set("GRAPHICS_PROFILE_LEVEL_10_1", "0xa100");
        macros.Set("GRAPHICS_PROFILE_LEVEL_11_0", "0xb000");
        macros.Set("GRAPHICS_PROFILE_LEVEL_11_1", "0xb100");
        macros.Set("GRAPHICS_PROFILE_LEVEL_11_2", "0xb200");
        return macros;
    }

    public Macros Set(string name, object value)
    {
        values[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        parsed.Clear();
        return this;
    }

    public bool IsDefined(string name) => values.ContainsKey(name);

    /// <summary>A macro's value: a <see cref="MacroValue"/> for a number, else its text; undefined is an error, as the shader would not compile.</summary>
    public object Value(string name) => parsed.GetOrAdd(name, Parse);

    private object Parse(string name)
    {
        if (!values.TryGetValue(name, out var text))
            throw new InvalidOperationException($"The macro {name} is not defined for this run (Macros.Set it)");
        return Number(text) switch
        {
            int i => new MacroValue(i),
            float f => new MacroValue(f),
            _ => text,
        };
    }

    private object? Number(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (float.TryParse(text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            return f;
        if (values.ContainsKey(text))
            return Number(values[text]);
        return null;
    }

    /// <summary>A #if condition: defined(), !, &amp;&amp;, ||, comparisons, + - * / %, parentheses; an unknown name is 0.</summary>
    public bool Evaluate(string condition) => new Parser(this, condition).Parse() != 0;

    private sealed class Parser
    {
        private readonly Macros macros;
        private readonly string text;
        private int at;

        public Parser(Macros macros, string text)
        {
            this.macros = macros;
            this.text = text;
        }

        public long Parse()
        {
            long value = Or();
            Skip();
            if (at < text.Length)
                throw new FormatException($"Unexpected '{text.Substring(at)}' in #if {text}");
            return value;
        }

        private void Skip()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        }

        private bool Eat(string token)
        {
            Skip();
            if (string.CompareOrdinal(text, at, token, 0, token.Length) != 0)
                return false;
            // "<" must not eat the start of "<=", "!" the start of "!=".
            if (token.Length == 1 && at + 1 < text.Length && text[at + 1] == '=' && token is "<" or ">" or "!" or "=")
                return false;
            at += token.Length;
            return true;
        }

        private long Or()
        {
            long left = And();
            while (Eat("||")) { long right = And(); left = left != 0 || right != 0 ? 1 : 0; }
            return left;
        }

        private long And()
        {
            long left = Equality();
            while (Eat("&&")) { long right = Equality(); left = left != 0 && right != 0 ? 1 : 0; }
            return left;
        }

        private long Equality()
        {
            long left = Relation();
            while (true)
            {
                if (Eat("==")) left = left == Relation() ? 1 : 0;
                else if (Eat("!=")) left = left != Relation() ? 1 : 0;
                else return left;
            }
        }

        private long Relation()
        {
            long left = Sum();
            while (true)
            {
                if (Eat("<=")) left = left <= Sum() ? 1 : 0;
                else if (Eat(">=")) left = left >= Sum() ? 1 : 0;
                else if (Eat("<")) left = left < Sum() ? 1 : 0;
                else if (Eat(">")) left = left > Sum() ? 1 : 0;
                else return left;
            }
        }

        private long Sum()
        {
            long left = Product();
            while (true)
            {
                if (Eat("+")) left += Product();
                else if (Eat("-")) left -= Product();
                else return left;
            }
        }

        private long Product()
        {
            long left = Unary();
            while (true)
            {
                if (Eat("*")) left *= Unary();
                else if (Eat("/")) { long r = Unary(); left = r == 0 ? 0 : left / r; }
                else if (Eat("%")) { long r = Unary(); left = r == 0 ? 0 : left % r; }
                else return left;
            }
        }

        private long Unary()
        {
            if (Eat("!")) return Unary() == 0 ? 1 : 0;
            if (Eat("-")) return -Unary();
            if (Eat("("))
            {
                long value = Or();
                if (!Eat(")")) throw new FormatException("Missing ) in #if " + text);
                return value;
            }
            Skip();
            int start = at;
            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_' || text[at] == '.')) at++;
            if (start == at)
                throw new FormatException($"Unexpected '{text.Substring(at)}' in #if {text}");
            var word = text.Substring(start, at - start);
            if (word == "defined")
            {
                bool parenthesized = Eat("(");
                Skip();
                int nameStart = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_')) at++;
                var name = text.Substring(nameStart, at - nameStart);
                if (parenthesized && !Eat(")")) throw new FormatException("Missing ) in #if " + text);
                return macros.IsDefined(name) ? 1 : 0;
            }
            if (char.IsDigit(word[0]))
            {
                if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return long.Parse(word.Substring(2).TrimEnd('u', 'U', 'l', 'L'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return (long)double.Parse(word.TrimEnd('u', 'U', 'l', 'L', 'f', 'F'), CultureInfo.InvariantCulture);
            }
            if (!macros.values.TryGetValue(word, out var text2))
                return 0;
            return text2.Trim().Length == 0 ? 0 : new Parser(macros, text2).Parse();
        }
    }
}
