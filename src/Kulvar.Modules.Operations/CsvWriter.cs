using System.Globalization;
using System.Text;

namespace Kulvar.Modules.Operations;

/// <summary>
/// Export CSV yazıcısı. Biçim: UTF-8 (BOM'lu), ayırıcı virgül, satır sonu CRLF, RFC 4180 tırnaklama.
/// Formül enjeksiyonu: hesap tablosu uygulamalarının formül olarak yorumladığı karakterlerle (= + - @ sekme, satır başı) başlayan
/// hücrelerin başına tek tırnak eklenir; hücre metin olarak kalır.
/// </summary>
public sealed class CsvWriter(TextWriter writer)
{
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private const string LineEnding = "\r\n";

    public int RowCount { get; private set; }

    public async Task WriteRowAsync(params object?[] cells)
    {
        var line = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }
            line.Append(Escape(Format(cells[i])));
        }
        line.Append(LineEnding);
        await writer.WriteAsync(line.ToString());
        RowCount++;
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string Neutralize(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

    public static string Escape(string value)
    {
        var safe = Neutralize(value);
        var needsQuotes = safe.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (safe.Length > 0 && (safe[0] == ' ' || safe[^1] == ' '));
        return needsQuotes ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }
}
