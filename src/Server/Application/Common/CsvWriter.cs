using System.Text;

namespace Compendio.Application.Common;

/// <summary>
/// RFC 4180 CSV, written by hand.
/// </summary>
/// <remarks>
/// <para>
/// No CSV package: two exports do not justify a dependency, and the escaping rule is one method.
/// The dependency list staying short is a product property here, not an aesthetic one.
/// </para>
/// <para>
/// A UTF-8 BOM is emitted deliberately. Excel opens a BOM-less UTF-8 file as the system code page,
/// which turns <c>Política</c> into mojibake — and the first thing anybody does with a compliance
/// export is open it in Excel.
/// </para>
/// </remarks>
public sealed class CsvWriter
{
    private readonly StringBuilder _builder = new();

    public CsvWriter(params string[] headers) => Row(headers);

    public void Row(params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                _builder.Append(',');
            }

            _builder.Append(Escape(values[i]));
        }

        // CRLF, which the specification asks for and which Excel is happiest with.
        _builder.Append("\r\n");
    }

    /// <summary>The bytes to send, BOM included.</summary>
    public byte[] ToBytes()
    {
        var body = Encoding.UTF8.GetBytes(_builder.ToString());
        var bom = Encoding.UTF8.GetPreamble();

        var output = new byte[bom.Length + body.Length];
        bom.CopyTo(output, 0);
        body.CopyTo(output, bom.Length);
        return output;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // A field is quoted if it contains a delimiter, a quote or a line break; an embedded quote
        // is doubled. A leading '=' or '+' is left alone rather than defanged, because mangling a
        // legitimate title would be a worse surprise than a spreadsheet's own formula prompt.
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
