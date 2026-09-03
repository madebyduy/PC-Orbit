using System.Globalization;
using System.Text;

namespace PcOrbit.Core.Localization;

/// <summary>
/// The subset of ICU MessageFormat this product actually needs: named arguments, plurals and
/// select.
/// </summary>
/// <remarks>
/// <para>
/// Spec 21.11 requires ICU MessageFormat so plural and word-order differences are the translator's
/// business, not the code's. A full ICU implementation is a large dependency for a privileged
/// process (spec 19.1 wants as little third-party code in here as possible), and we only use three
/// features. Supported:
/// </para>
/// <code>
/// {name}
/// {count, plural, =0 {none} one {# change} other {# changes}}
/// {mode, select, auto {automatic} other {guided}}
/// </code>
/// <para>
/// Plural categories come from <see cref="PluralRules"/>: English distinguishes one/other,
/// Vietnamese does not. Unsupported ICU syntax is left alone rather than half-interpreted, so a
/// translator gets visibly wrong output instead of silently wrong output.
/// </para>
/// </remarks>
public static class IcuMessageFormatter
{
    public static string Format(
        string pattern,
        IReadOnlyDictionary<string, string>? arguments,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(culture);

        arguments ??= EmptyArguments;

        var output = new StringBuilder(pattern.Length + 16);
        int index = 0;

        while (index < pattern.Length)
        {
            char c = pattern[index];

            if (c == '\'' && index + 1 < pattern.Length && pattern[index + 1] == '\'')
            {
                // ICU escapes a literal apostrophe as ''.
                output.Append('\'');
                index += 2;
                continue;
            }

            if (c != '{')
            {
                output.Append(c);
                index++;
                continue;
            }

            int end = FindClosingBrace(pattern, index);

            if (end < 0)
            {
                // Unbalanced braces: emit the rest verbatim so the damage is visible.
                output.Append(pattern.AsSpan(index));
                break;
            }

            output.Append(FormatPlaceholder(pattern[(index + 1)..end], arguments, culture));
            index = end + 1;
        }

        return output.ToString();
    }

    private static string FormatPlaceholder(
        string body,
        IReadOnlyDictionary<string, string> arguments,
        CultureInfo culture)
    {
        int firstComma = body.IndexOf(',', StringComparison.Ordinal);

        if (firstComma < 0)
        {
            string simpleName = body.Trim();
            return arguments.TryGetValue(simpleName, out string? simple) ? simple : $"{{{simpleName}}}";
        }

        string name = body[..firstComma].Trim();
        string rest = body[(firstComma + 1)..].TrimStart();
        int secondComma = rest.IndexOf(',', StringComparison.Ordinal);
        string type = (secondComma < 0 ? rest : rest[..secondComma]).Trim();
        string style = secondComma < 0 ? string.Empty : rest[(secondComma + 1)..].Trim();

        arguments.TryGetValue(name, out string? value);
        value ??= string.Empty;

        return type switch
        {
            "plural" => FormatPlural(value, style, arguments, culture),
            "select" => FormatSelect(value, style, arguments, culture),
            "number" => FormatNumber(value, culture),
            _ => value,
        };
    }

    private static string FormatPlural(
        string value,
        string style,
        IReadOnlyDictionary<string, string> arguments,
        CultureInfo culture)
    {
        IReadOnlyDictionary<string, string> options = ParseOptions(style);

        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
        {
            return options.TryGetValue("other", out string? fallback)
                ? Format(fallback, arguments, culture)
                : value;
        }

        string exactKey = "=" + number.ToString("0.################", CultureInfo.InvariantCulture);
        string category = PluralRules.CategoryOf(culture, number);

        string chosen =
            options.TryGetValue(exactKey, out string? exact) ? exact
            : options.TryGetValue(category, out string? byCategory) ? byCategory
            : options.TryGetValue("other", out string? other) ? other
            : string.Empty;

        // '#' inside a plural option is the number itself, formatted for the locale.
        string formattedNumber = number.ToString("0.################", culture);
        return Format(chosen.Replace("#", formattedNumber, StringComparison.Ordinal), arguments, culture);
    }

    private static string FormatSelect(
        string value,
        string style,
        IReadOnlyDictionary<string, string> arguments,
        CultureInfo culture)
    {
        IReadOnlyDictionary<string, string> options = ParseOptions(style);

        string chosen =
            options.TryGetValue(value, out string? exact) ? exact
            : options.TryGetValue("other", out string? other) ? other
            : string.Empty;

        return Format(chosen, arguments, culture);
    }

    private static string FormatNumber(string value, CultureInfo culture) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number)
            ? number.ToString("#,0.################", culture)
            : value;

    /// <summary>Parses <c>key {message} key2 {message2}</c>, allowing nested braces in messages.</summary>
    private static IReadOnlyDictionary<string, string> ParseOptions(string style)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        int index = 0;

        while (index < style.Length)
        {
            while (index < style.Length && char.IsWhiteSpace(style[index]))
            {
                index++;
            }

            int keyStart = index;

            while (index < style.Length && style[index] != '{' && !char.IsWhiteSpace(style[index]))
            {
                index++;
            }

            if (keyStart == index)
            {
                break;
            }

            string key = style[keyStart..index];

            while (index < style.Length && char.IsWhiteSpace(style[index]))
            {
                index++;
            }

            if (index >= style.Length || style[index] != '{')
            {
                break;
            }

            int end = FindClosingBrace(style, index);

            if (end < 0)
            {
                break;
            }

            options[key] = style[(index + 1)..end];
            index = end + 1;
        }

        return options;
    }

    private static int FindClosingBrace(string text, int openIndex)
    {
        int depth = 0;

        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    internal static IReadOnlyDictionary<string, string> EmptyArguments { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Plural categories for the locales we ship. Deliberately explicit rather than clever: getting
/// this wrong produces text a native speaker immediately recognises as machine-made.
/// </summary>
public static class PluralRules
{
    public static string CategoryOf(CultureInfo culture, decimal number)
    {
        ArgumentNullException.ThrowIfNull(culture);

        string language = culture.TwoLetterISOLanguageName;

        return language switch
        {
            // Vietnamese, Japanese, Korean, Chinese, Thai: one plural form.
            "vi" or "ja" or "ko" or "zh" or "th" or "id" => "other",

            // English and the western European languages we plan for next.
            "en" or "de" or "es" or "it" or "nl" or "pt" => number == 1 ? "one" : "other",

            // French treats 0 and 1 alike.
            "fr" => number is >= 0 and < 2 ? "one" : "other",

            _ => number == 1 ? "one" : "other",
        };
    }
}
