// Port of Base/darkplaces/prvm_cmds.c VM_tokenize, VM_tokenize_console, VM_tokenizebyseparator,
// VM_argv, VM_argv_start_index, VM_argv_end_index and common.c COM_ParseToken_VM_Tokenize,
// COM_ParseToken_Console.
using static VortexArena.QuakeC.QcStringUtf8;

namespace VortexArena.QuakeC;

public sealed partial class QcStringBuiltins
{
    // The last tokenize of any kind: argv and the index builtins read it until the next one replaces it.
    // DarkPlaces keeps the tokens as tempstrings, which a later call can overwrite; here they are kept
    // as text and argv hands out a fresh tempstring each time, so an old argv result cannot go stale.
    private readonly List<string> _tokens = new();
    private readonly List<int> _tokenStart = new();
    private readonly List<int> _tokenEnd = new();

    // com_token for the two parsers, and tokentext for tokenizebyseparator.
    private readonly byte[] _tokenText;

    // sizeof(tokens) / sizeof(tokens[0])
    private int MaxTokens => _size / 2;

    // ISWHITESPACE, which counts the terminator.
    private static bool IsWhiteSpace(byte c) => c == 0 || c == ' ' || c == '\t' || c == '\r' || c == '\n';

    /// <summary>The string being tokenized: a copy cut to the tempstring size, as dp_strlcpy leaves it.</summary>
    private byte[] BeginTokenize(string text)
    {
        _tokens.Clear();
        _tokenStart.Clear();
        _tokenEnd.Clear();
        return Z(Limit(text));
    }

    private void AddToken(int length, int start, int end)
    {
        _tokens.Add(Text(_tokenText.AsSpan(0, length)));
        _tokenStart.Add(start);
        _tokenEnd.Add(end);
    }

    // float(string s) tokenize = #441
    private void Tokenize(QcVm vm)
    {
        Parms(1, 1, "VM_tokenize");
        byte[] s = BeginTokenize(vm.ArgString(0));
        int p = 0;
        while (_tokens.Count < MaxTokens)
        {
            // Skipped here as well as in the parser so the start index is where the token begins.
            while (s[p] != 0 && IsWhiteSpace(s[p])) p++;
            int start = p;
            if (!ParseTokenQuakeC(s, ref p, out int length)) break;
            AddToken(length, start, p);
        }
        vm.ReturnFloat(_tokens.Count);
    }

    // float(string s) tokenize_console = #514
    private void TokenizeConsole(QcVm vm)
    {
        Parms(1, 1, "VM_tokenize_console");
        byte[] s = BeginTokenize(vm.ArgString(0));
        int p = 0;
        while (_tokens.Count < MaxTokens)
        {
            while (s[p] != 0 && IsWhiteSpace(s[p])) p++;
            int start = p;
            if (!ParseTokenConsole(s, ref p, out int length)) break;
            AddToken(length, start, p);
        }
        vm.ReturnFloat(_tokens.Count);
    }

    /// <summary>
    /// COM_ParseToken_VM_Tokenize (returnnewline false): QuakeC-flavoured tokens. Skips white space
    /// and both comment forms; a string in either kind of quote is one token with \n, \t and
    /// backslash-anything unescaped; each of <c>{ } ( ) [ ] : , ;</c> is a token by itself; anything
    /// else runs to the next white space or one of those characters.
    /// </summary>
    private bool ParseTokenQuakeC(byte[] s, ref int p, out int length)
    {
        byte[] token = _tokenText;
        int d = p;
        length = 0;

        while (true)
        {
            for (; IsWhiteSpace(s[d]); d++)
                if (s[d] == 0) return false;

            if (s[d] == '/' && s[d + 1] == '/')
            {
                while (s[d] != 0 && s[d] != '\n' && s[d] != '\r') d++;
            }
            else if (s[d] == '/' && s[d + 1] == '*')
            {
                // "/*/" is a whole comment: the scan for the closing pair starts on the opening star.
                d++;
                while (s[d] != 0 && (s[d] != '*' || s[d + 1] != '/')) d++;
                if (s[d] != 0) d++;
                if (s[d] != 0) d++;
            }
            else break;
        }

        if (s[d] == '"' || s[d] == '\'')
        {
            byte quote = s[d];
            for (d++; s[d] != 0 && s[d] != quote; d++)
            {
                byte c = s[d];
                if (c == '\\')
                {
                    c = s[++d];
                    if (c == 0) break; // a backslash at the very end must not step over the terminator
                    if (c == 'n') c = (byte)'\n';
                    else if (c == 't') c = (byte)'\t';
                }
                if (length < token.Length - 1) token[length++] = c;
            }
            if (s[d] == quote) d++;
        }
        else if (IsPunctuation(s[d]))
        {
            token[length++] = s[d++];
        }
        else
        {
            for (; !IsWhiteSpace(s[d]) && !IsPunctuation(s[d]); d++)
                if (length < token.Length - 1) token[length++] = s[d];
        }
        p = d;
        return true;
    }

    private static bool IsPunctuation(byte c) =>
        c == '{' || c == '}' || c == ')' || c == '(' || c == ']' || c == '[' || c == ':' || c == ',' || c == ';';

    /// <summary>
    /// COM_ParseToken_Console: console-command tokens. Only <c>//</c> comments and double quotes, and
    /// inside quotes only \" and \\ are escapes; no character is a token by itself.
    /// </summary>
    private bool ParseTokenConsole(byte[] s, ref int p, out int length)
    {
        byte[] token = _tokenText;
        int d = p;
        length = 0;

        while (true)
        {
            for (; IsWhiteSpace(s[d]); d++)
                if (s[d] == 0) return false;
            if (s[d] != '/' || s[d + 1] != '/') break;
            while (s[d] != 0 && s[d] != '\n' && s[d] != '\r') d++;
        }

        if (s[d] == '"')
        {
            for (d++; s[d] != 0 && s[d] != '"'; d++)
            {
                if (s[d] == '\\' && (s[d + 1] == '"' || s[d + 1] == '\\')) d++;
                if (length < token.Length - 1) token[length++] = s[d];
            }
            if (s[d] == '"') d++;
        }
        else
        {
            for (; !IsWhiteSpace(s[d]); d++)
                if (length < token.Length - 1) token[length++] = s[d];
        }
        p = d;
        return true;
    }

    // float(string s, string separator1, ...) tokenizebyseparator = #479
    private void TokenizeBySeparator(QcVm vm)
    {
        Parms(2, 8, "VM_tokenizebyseparator");
        byte[] s = BeginTokenize(vm.ArgString(0));

        // Blank separators are ignored; with none left the whole string is one token.
        List<byte[]> separators = new(7);
        for (int i = 1; i < vm.ArgCount; i++)
        {
            byte[] separator = Z(vm.ArgString(i));
            if (separator.Length > 1) separators.Add(separator);
        }

        // Every token is the text before a separator, and the last is what follows the final one. A
        // leading or doubled separator therefore yields an empty token; a trailing one does not, because
        // the loop ends as soon as the string does; and "" is one empty token.
        byte[] text = _tokenText;
        int p = 0, j = 0;
        while (_tokens.Count < MaxTokens)
        {
            int tokenStart = j, start = p, end = p;
            while (s[p] != 0)
            {
                int matched = 0;
                foreach (byte[] separator in separators)
                {
                    if (StartsWith(s, p, separator))
                    {
                        matched = separator.Length - 1;
                        break;
                    }
                }
                if (matched > 0)
                {
                    p += matched;
                    break;
                }
                if (j < text.Length - 1) text[j++] = s[p];
                p++;
                end = p;
            }
            if (j >= text.Length) break;
            _tokens.Add(Text(text.AsSpan(tokenStart, j - tokenStart)));
            _tokenStart.Add(start);
            _tokenEnd.Add(end);
            j++; // each token's terminator takes a byte of tokentext too
            if (s[p] == 0) break;
        }
        vm.ReturnFloat(_tokens.Count);
    }

    // strncmp(s + at, prefix, strlen(prefix)) == 0
    private static bool StartsWith(byte[] s, int at, byte[] prefix)
    {
        for (int i = 0; i < prefix.Length - 1; i++)
            if (s[at + i] != prefix[i]) return false; // the terminator of s never equals a prefix byte
        return true;
    }

    // A negative index counts from the last token.
    private bool TokenIndex(QcVm vm, out int index)
    {
        index = QcVm.FloatToInt(vm.ArgFloat(0));
        if (index < 0) index += _tokens.Count;
        return index >= 0 && index < _tokens.Count;
    }

    // string(float n) argv = #442
    private void Argv(QcVm vm)
    {
        Parms(1, 1, "VM_argv");
        if (TokenIndex(vm, out int index)) vm.ReturnString(_tokens[index]);
        else vm.ReturnInt(0);
    }

    // float(float idx) argv_start_index = #515: the byte offset in the tokenized string where the token starts
    private void ArgvStartIndex(QcVm vm)
    {
        Parms(1, 1, "VM_argv_start_index");
        vm.ReturnFloat(TokenIndex(vm, out int index) ? _tokenStart[index] : -1);
    }

    // float(float idx) argv_end_index = #516: the offset just past the token (past its closing quote, if quoted)
    private void ArgvEndIndex(QcVm vm)
    {
        Parms(1, 1, "VM_argv_end_index");
        vm.ReturnFloat(TokenIndex(vm, out int index) ? _tokenEnd[index] : -1);
    }
}
