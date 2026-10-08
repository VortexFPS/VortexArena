// Port of Base/darkplaces/cmd.c Cbuf_ParseText / Cbuf_AddText (how console text is cut into
// commands), common.c COM_ParseToken_Console (how a command is cut into arguments) and the $cvar
// expansion of cmd.c Cmd_PreprocessString, as far as svc_stufftext needs them.
using System.Text;

namespace VortexArena.Legacy.Protocol;

/// <summary>
/// Reassembles the console commands a server sends with svc_stufftext.
///
/// The server does not send commands; it sends text, which DarkPlaces appends to its console buffer.
/// A command ends at a newline, a carriage return, or a semicolon outside quotes, and nothing says
/// one svc_stufftext holds exactly one command: Xonotic sends long lines in several pieces, and a
/// piece without a line ending stays pending until the rest arrives. So the text has to be buffered
/// and cut the same way, or commands come out split in half.
/// </summary>
public sealed class DpStuffTextBuffer
{
    /// <summary>The most text held waiting for a line ending. A server that never sends one would
    /// otherwise grow this without limit; DarkPlaces caps its whole command buffer similarly.</summary>
    public const int MaxPending = 65536;

    private readonly StringBuilder _pending = new();

    /// <summary>Text received so far that has not been terminated.</summary>
    public string Pending => _pending.ToString();

    public void Clear() => _pending.Clear();

    /// <summary>Cbuf_AddText: append <paramref name="text"/> and move every command it completes into
    /// <paramref name="commands"/>, in order.</summary>
    public void Add(string text, List<string> commands)
    {
        int size = 0, start = 0;
        // Quote and comment state begin fresh on every call, even when continuing a pending line.
        // That is what the C does, so it is what a server's text is written against.
        bool quotes = false, comment = false;

        for (int pos = 0; pos < text.Length; pos++)
        {
            char c = text[pos];
            if (c == '\n' || c == '\r' || (c == ';' && !comment && !quotes))
            {
                comment = false;
                quotes = false;
                if (size != 0)
                {
                    _pending.Append(text, start, size);
                    size = 0;
                }
                // With no new text this is "all I got was this lousy \n": it ends the pending line.
                if (_pending.Length != 0)
                {
                    commands.Add(_pending.ToString());
                    _pending.Clear();
                }
                continue;
            }
            if (c == '/')
            {
                // "//" starts a comment only at the start or after whitespace, so URLs survive.
                if (!quotes && pos + 1 < text.Length && text[pos + 1] == '/' && (pos == 0 || text[pos - 1] <= ' '))
                    comment = true;
            }
            else if (c == '"')
            {
                if (!comment && (pos == 0 || text[pos - 1] != '\\'))
                    quotes = !quotes;
            }

            if (!comment)
            {
                if (size == 0)
                    start = pos;
                size++;
            }
        }

        // the line didn't end yet but we do have a string
        if (size != 0)
        {
            if (_pending.Length + size > MaxPending)
                _pending.Clear();
            else
                _pending.Append(text, start, size);
        }
    }
}

/// <summary>Helpers for reading one console command line.</summary>
public static class DpStuffText
{
    /// <summary>
    /// Cmd_TokenizeString: arguments are separated by whitespace (any character at or below a space);
    /// a double-quoted argument may contain whitespace, with \" and \\ as its only escapes; "//"
    /// starts a comment.
    /// </summary>
    public static List<string> Tokenize(string line)
    {
        var args = new List<string>();
        var token = new StringBuilder();
        int i = 0;
        while (true)
        {
            while (i < line.Length && line[i] <= ' ')
                i++;
            if (i >= line.Length)
                break;
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break; // a command is one line, so a comment runs to its end
            token.Clear();
            if (line[i] == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i++)
                {
                    if (line[i] == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                        i++;
                    token.Append(line[i]);
                }
                if (i < line.Length)
                    i++; // closing quote
            }
            else
            {
                for (; i < line.Length && line[i] > ' '; i++)
                    token.Append(line[i]);
            }
            args.Add(token.ToString());
        }
        return args;
    }

    /// <summary>Cmd_Args: the line after its first argument, verbatim. This, not the re-joined tokens,
    /// is what the <c>cmd</c> command forwards to the server.</summary>
    public static string ArgsAfterFirst(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] <= ' ')
            i++;
        if (i < line.Length && line[i] == '"')
        {
            for (i++; i < line.Length && line[i] != '"'; i++)
                if (line[i] == '\\' && i + 1 < line.Length && (line[i + 1] == '"' || line[i + 1] == '\\'))
                    i++;
            if (i < line.Length)
                i++;
        }
        else
            while (i < line.Length && line[i] > ' ')
                i++;
        while (i < line.Length && line[i] <= ' ')
            i++;
        return line[i..];
    }

    /// <summary>
    /// Replace <c>$name</c> and <c>${name}</c> with values from <paramref name="variables"/> and
    /// <c>$$</c> with <c>$</c>. A name that is not in the table is left as written, as DarkPlaces
    /// leaves an unknown cvar. This is how "cmd clientversion $gameversion" becomes
    /// "clientversion 806" before it is sent back.
    /// </summary>
    public static string Expand(string line, IReadOnlyDictionary<string, string> variables)
    {
        if (line.IndexOf('$') < 0)
            return line;
        var sb = new StringBuilder(line.Length + 16);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c != '$' || i + 1 >= line.Length)
            {
                sb.Append(c);
                continue;
            }
            if (line[i + 1] == '$')
            {
                sb.Append('$');
                i++;
                continue;
            }
            int nameStart, nameEnd, next;
            if (line[i + 1] == '{')
            {
                int close = line.IndexOf('}', i + 2);
                if (close < 0)
                {
                    sb.Append(c);
                    continue;
                }
                nameStart = i + 2;
                nameEnd = close;
                next = close;
            }
            else
            {
                nameStart = i + 1;
                nameEnd = nameStart;
                while (nameEnd < line.Length && (char.IsAsciiLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_'))
                    nameEnd++;
                next = nameEnd - 1;
            }
            if (nameEnd > nameStart && variables.TryGetValue(line[nameStart..nameEnd], out string? value))
            {
                sb.Append(value);
                i = next;
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>C atoi: optional whitespace and sign, then digits; anything else, or nothing, is 0.
    /// Saturates instead of overflowing.</summary>
    public static int Atoi(string text)
    {
        int i = 0;
        while (i < text.Length && text[i] <= ' ')
            i++;
        bool negative = false;
        if (i < text.Length && (text[i] == '-' || text[i] == '+'))
            negative = text[i++] == '-';
        long value = 0;
        for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
        {
            value = value * 10 + (text[i] - '0');
            if (value > int.MaxValue)
                return negative ? int.MinValue : int.MaxValue;
        }
        return (int)(negative ? -value : value);
    }
}
