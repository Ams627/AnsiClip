using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static partial class Program
{
    private const string Font = "font-family:Consolas,'Courier New',monospace";
    private const string White = "#ffffff";
    private const string Black = "#000000";

    // Windows Terminal's Campbell palette for ANSI colours 0–15.
    private static readonly string[] Palette =
    [
        "#0c0c0c", "#c50f1f", "#13a10e", "#c19c00",
        "#0037da", "#881798", "#3a96dd", "#cccccc",
        "#767676", "#e74856", "#16c60c", "#f9f1a5",
        "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("AnsiClip: convert clipboard text to formatted HTML for Teams.");
            return 0;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: AnsiClip [--help]");
            return 2;
        }

        try
        {
            string input = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (input.Length == 0)
            {
                Console.Error.WriteLine("The clipboard contains no text.");
                return 1;
            }

            var (fragment, plainText) = ConvertText(input);
            var data = new DataObject();
            data.SetData(DataFormats.Html, false, WrapForClipboard(fragment));
            data.SetData(DataFormats.UnicodeText, false, plainText);
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);
            return 0;
        }
        catch (ExternalException ex)
        {
            Console.Error.WriteLine($"Could not access the clipboard. {ex.Message}");
            return 1;
        }
    }

    // Consume complete CSI, OSC and other escape commands. Only SGR affects styling.
    [GeneratedRegex(@"(?:\x1b\[|\x9b)(?<parameters>[0-?]*)(?<intermediate>[ -/]*)(?<final>[@-~])|(?:\x1b\]|\x9d)[^\x07\x1b\x9c]*(?:\x07|\x1b\\|\x9c)|(?:\x1b[P^_X]|[\x90\x98\x9e\x9f])[^\x1b\x9c]*(?:\x1b\\|\x9c)|\x1b[ -/]*[0-~]|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]")]
    private static partial Regex ControlSequences();

    private static (string Html, string Text) ConvertText(string input)
    {
        input = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var html = new StringBuilder($"<div style=\"{Font};color:{White};background-color:{Black}\">");
        var plain = new StringBuilder();
        var style = new Style();
        int position = 0;
        int column = 0;

        void AppendRun(ReadOnlySpan<char> run)
        {
            if (run.IsEmpty)
                return;

            string foreground = style.Inverse ? style.Background : style.Foreground;
            string background = style.Inverse ? style.Foreground : style.Background;
            html.Append($"<span style=\"{Font};color:{foreground};background-color:{background}");
            if (style.Bold) html.Append(";font-weight:bold");
            if (style.Italic) html.Append(";font-style:italic");
            if (style.Underline || style.Strike)
            {
                html.Append(";text-decoration:");
                if (style.Underline) html.Append("underline");
                if (style.Underline && style.Strike) html.Append(' ');
                if (style.Strike) html.Append("line-through");
            }
            html.Append("\">");

            foreach (var rune in run.EnumerateRunes())
            {
                switch (rune.Value)
                {
                    case '\n':
                        html.Append("<br>");
                        plain.Append("\r\n");
                        column = 0;
                        break;
                    case '\t':
                        int spaces = 8 - column % 8;
                        for (int i = 0; i < spaces; i++)
                            html.Append("&nbsp;");
                        plain.Append('\t');
                        column += spaces;
                        break;
                    case ' ':
                        html.Append("&nbsp;");
                        plain.Append(' ');
                        column++;
                        break;
                    default:
                        string value = rune.ToString();
                        if (rune.IsAscii)
                            html.Append(WebUtility.HtmlEncode(value));
                        else
                            html.Append(FormattableString.Invariant($"&#x{rune.Value:x};"));
                        plain.Append(value);
                        column++;
                        break;
                }
            }
            html.Append("</span>");
        }

        foreach (Match match in ControlSequences().Matches(input))
        {
            AppendRun(input.AsSpan(position, match.Index - position));
            if (match.Groups["final"].Value == "m" && match.Groups["intermediate"].Length == 0)
                style.Apply(match.Groups["parameters"].Value);
            position = match.Index + match.Length;
        }

        AppendRun(input.AsSpan(position));
        html.Append("</div>");
        return (html.ToString(), plain.ToString());
    }

    private static string WrapForClipboard(string fragment)
    {
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        static string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
            FormattableString.Invariant(
                $"Version:1.0\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n");

        int startHtml = Header(0, 0, 0, 0).Length;
        int startFragment = startHtml + before.Length;
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        return Header(startHtml, endFragment + after.Length, startFragment, endFragment)
            + before + fragment + after;
    }

    private sealed class Style
    {
        public string Foreground { get; private set; } = White;
        public string Background { get; private set; } = Black;
        public bool Bold { get; private set; }
        public bool Italic { get; private set; }
        public bool Underline { get; private set; }
        public bool Strike { get; private set; }
        public bool Inverse { get; private set; }

        public void Apply(string parameters)
        {
            string[] fields = parameters.Split(';');
            for (int i = 0; i < fields.Length; i++)
            {
                string[] parts = fields[i].Split(':');
                int code = parts[0].Length == 0 ? 0 : Number(parts[0]);
                if (code is 38 or 48 or 58)
                {
                    string? colour = parts.Length > 1
                        ? ReadColour(parts.AsSpan(1), colonSeparated: true)
                        : ReadSemicolonColour(fields, ref i);
                    if (colour != null && code == 38) Foreground = colour;
                    if (colour != null && code == 48) Background = colour;
                    continue;
                }

                switch (code)
                {
                    case 0:
                        Foreground = White;
                        Background = Black;
                        Bold = Italic = Underline = Strike = Inverse = false;
                        break;
                    case 1: Bold = true; break;
                    case 3: Italic = true; break;
                    case 4: Underline = parts.Length == 1 || Number(parts[1]) != 0; break;
                    case 7: Inverse = true; break;
                    case 9: Strike = true; break;
                    case 21: Underline = true; break;
                    case 22: Bold = false; break;
                    case 23: Italic = false; break;
                    case 24: Underline = false; break;
                    case 27: Inverse = false; break;
                    case 29: Strike = false; break;
                    case >= 30 and <= 37: Foreground = Palette[code - 30]; break;
                    case 39: Foreground = White; break;
                    case >= 40 and <= 47: Background = Palette[code - 40]; break;
                    case 49: Background = Black; break;
                    case >= 90 and <= 97: Foreground = Palette[code - 90 + 8]; break;
                    case >= 100 and <= 107: Background = Palette[code - 100 + 8]; break;
                }
            }
        }
    }

    private static int Number(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number : -1;

    private static string? ReadSemicolonColour(string[] fields, ref int index)
    {
        if (index + 1 >= fields.Length)
            return null;

        int length = Number(fields[index + 1]) switch { 2 => 4, 5 => 2, _ => 1 };
        length = Math.Min(length, fields.Length - index - 1);
        var arguments = fields.AsSpan(index + 1, length);
        index += length;
        return ReadColour(arguments, colonSeparated: false);
    }

    private static string? ReadColour(ReadOnlySpan<string> arguments, bool colonSeparated)
    {
        int mode = Number(arguments[0]);
        if (mode == 5 && arguments.Length == 2)
            return IndexedColour(Number(arguments[1]));

        if (mode != 2)
            return null;

        // Colon-form RGB may include an optional colour-space field.
        var channels = arguments[(colonSeparated && arguments.Length == 5 ? 2 : 1)..];
        if (channels.Length != 3)
            return null;

        int red = Number(channels[0]);
        int green = Number(channels[1]);
        int blue = Number(channels[2]);
        return red is >= 0 and <= 255 && green is >= 0 and <= 255 && blue is >= 0 and <= 255
            ? Rgb(red, green, blue) : null;
    }

    private static string? IndexedColour(int index)
    {
        if (index is < 0 or > 255) return null;
        if (index < 16) return Palette[index];
        if (index >= 232)
        {
            int gray = 8 + (index - 232) * 10;
            return Rgb(gray, gray, gray);
        }

        int cube = index - 16;
        static int Level(int step) => step == 0 ? 0 : 55 + 40 * step;
        return Rgb(Level(cube / 36), Level(cube / 6 % 6), Level(cube % 6));
    }

    private static string Rgb(int red, int green, int blue) => $"#{red:x2}{green:x2}{blue:x2}";
}
