using System.Text;

namespace XppGraft;

public enum TokKind : byte { Ident, Number, String, Punct, Macro }

/// <param name="Line">0-based line offset inside the analysed source block.</param>
public readonly record struct Token(TokKind Kind, string Text, int Line, int Pos, int End);

public static class XppLexer
{
    /// <summary>
    /// Tokenizes X++ source. Comments are dropped; strings keep their unescaped content.
    /// With <paramref name="stopAtFirstBrace"/> the lexer stops after the first "{" outside
    /// parentheses/brackets — enough to read attributes and a method/class header cheaply.
    /// </summary>
    public static List<Token> Tokenize(string src, bool stopAtFirstBrace = false)
    {
        var list = new List<Token>(stopAtFirstBrace ? 32 : Math.Max(16, src.Length / 5));
        int i = 0, line = 0, n = src.Length, paren = 0, bracket = 0;
        StringBuilder? sb = null;

        while (i < n)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == ' ' || c == '\t' || c == '\r' || char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < n)
            {
                if (src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }
                if (src[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                    {
                        if (src[i] == '\n') line++;
                        i++;
                    }
                    i = Math.Min(n, i + 2);
                    continue;
                }
            }

            int start = i, startLine = line;

            if (c == '@' && i + 1 < n && (src[i + 1] == '"' || src[i + 1] == '\''))
            {
                char q = src[i + 1];
                i += 2;
                sb ??= new StringBuilder();
                sb.Clear();
                while (i < n)
                {
                    if (src[i] == q)
                    {
                        if (i + 1 < n && src[i + 1] == q) { sb.Append(q); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (src[i] == '\n') line++;
                    sb.Append(src[i]);
                    i++;
                }
                list.Add(new Token(TokKind.String, sb.ToString(), startLine, start, i));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                char q = c;
                i++;
                sb ??= new StringBuilder();
                sb.Clear();
                while (i < n && src[i] != q)
                {
                    if (src[i] == '\\' && i + 1 < n) { sb.Append(src[i + 1]); i += 2; continue; }
                    if (src[i] == '\n') line++;
                    sb.Append(src[i]);
                    i++;
                }
                i = Math.Min(n, i + 1);
                list.Add(new Token(TokKind.String, sb.ToString(), startLine, start, i));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                list.Add(new Token(TokKind.Ident, src.Substring(start, i - start), startLine, start, i));
                continue;
            }

            if (c == '#')
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                list.Add(new Token(TokKind.Macro, src.Substring(start, i - start), startLine, start, i));
                continue;
            }

            if (char.IsDigit(c))
            {
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '.')) i++;
                list.Add(new Token(TokKind.Number, src.Substring(start, i - start), startLine, start, i));
                continue;
            }

            string p = c.ToString();
            if (i + 1 < n)
            {
                char d = src[i + 1];
                string two = (c, d) switch
                {
                    (':', ':') => "::",
                    ('=', '=') => "==",
                    ('!', '=') => "!=",
                    ('<', '=') => "<=",
                    ('>', '=') => ">=",
                    ('&', '&') => "&&",
                    ('|', '|') => "||",
                    ('+', '+') => "++",
                    ('-', '-') => "--",
                    ('+', '=') => "+=",
                    ('-', '=') => "-=",
                    ('=', '>') => "=>",
                    _ => "",
                };
                if (two.Length == 2) p = two;
            }
            i += p.Length;
            list.Add(new Token(TokKind.Punct, p, startLine, start, i));

            switch (p)
            {
                case "(": paren++; break;
                case ")": paren = Math.Max(0, paren - 1); break;
                case "[": bracket++; break;
                case "]": bracket = Math.Max(0, bracket - 1); break;
                case "{" when stopAtFirstBrace && paren == 0 && bracket == 0:
                    return list;
            }
        }
        return list;
    }
}
