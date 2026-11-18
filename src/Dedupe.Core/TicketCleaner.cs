using System.Text;
using System.Text.RegularExpressions;

namespace Dedupe.Core;

/// <summary>
/// Turns a raw ticket into the text that gets embedded. Email-created tickets carry a lot that has nothing to do with
/// the problem (the quoted thread, a signature, "Sent from my phone", order numbers, an address), and all of it makes
/// unrelated tickets look alike or two copies of one problem look different.
/// </summary>
public static class TicketCleaner
{
    public const int MaxChars = 4000;

    private static readonly Regex SubjectPrefix = new(@"^(\s*(re|fw|fwd)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReplyHeader = new(@"^\s*on\s.{3,150}\swrote:\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OriginalMessage = new(@"^\s*-{2,}\s*(original message|forwarded message)\s*-{2,}\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromLine = new(@"^\s*from:\s.+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeaderLine = new(@"^\s*(sent|date|to|subject|cc):\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceLine = new(@"^\s*(sent from my .*|get outlook for .*|sent from outlook.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Disclaimer = new(@"confidential|intended recipient|disclaimer", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignOff = new(@"^\s*(thanks|thank you|many thanks|regards|kind regards|best regards|best|cheers|sincerely|thanks again)[\s,!.]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Uuid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LongHex = new(@"\b[0-9a-f]{16,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Reference = new(@"#\d+|\b[A-Za-z]{1,5}-\d+\b", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"\+?\d[\d\s().-]{7,}\d", RegexOptions.Compiled);
    private static readonly Regex LongNumber = new(@"\b\d{5,}\b", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The text to embed. The subject is repeated so it counts for more than a single line of the body, because a
    /// subject is usually the clearest statement of the problem.
    /// </summary>
    public static string Clean(string? subject, string? body)
    {
        var s = Redact(StripSubject(subject));
        var b = Redact(StripBody(body));
        var text = s.Length == 0 ? b : $"{s}. {s}. {b}";
        text = Spaces.Replace(text.ToLowerInvariant(), " ").Trim();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    internal static string StripSubject(string? subject) => SubjectPrefix.Replace(subject ?? "", "").Trim();

    /// <summary>Removes the quoted thread, signature, device lines, sign-offs and disclaimers from an email-style body.</summary>
    internal static string StripBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Everything from a reply header, an "original message" rule or a signature delimiter on is not the new message.
            if (ReplyHeader.IsMatch(line) || OriginalMessage.IsMatch(line) || line.Trim() == "--" || IsForwardedHeader(lines, i))
            {
                break;
            }

            if (line.TrimStart().StartsWith('>') || DeviceLine.IsMatch(line) || Disclaimer.IsMatch(line))
            {
                continue;
            }

            if (SignOff.IsMatch(line))
            {
                // A sign-off ("Thanks,") is followed by a name and a signature block, none of which describes the problem.
                break;
            }

            kept.Append(line).Append('\n');
        }

        return kept.ToString();
    }

    private static bool IsForwardedHeader(string[] lines, int i)
    {
        if (!FromLine.IsMatch(lines[i]))
        {
            return false;
        }

        for (var j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
        {
            if (HeaderLine.IsMatch(lines[j]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes values that identify one customer or order. They are dropped, not replaced with a token, because a
    /// shared token would make every ticket that mentions an order number look a little alike. Short numbers such as
    /// 401 and 429 stay: they are error codes and they matter.
    /// </summary>
    internal static string Redact(string text)
    {
        text = Email.Replace(text, " ");
        text = Url.Replace(text, " ");
        text = Uuid.Replace(text, " ");
        text = LongHex.Replace(text, " ");
        text = Reference.Replace(text, " ");
        text = Phone.Replace(text, " ");
        text = LongNumber.Replace(text, " ");
        return text;
    }
}
