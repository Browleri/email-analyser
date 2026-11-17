using System.Text.RegularExpressions;

namespace EmailAnalyser.Core;

/// <summary>Programmatic phishing signals from the receiving server's verdict and the sender fields.</summary>
public sealed record SenderCheck(IReadOnlyList<string> Signals, string? Spf, string? Dkim, string? Dmarc)
{
    public bool Suspicious => Signals.Count > 0;
}

/// <summary>
/// Reads the topmost Authentication-Results header, which the receiving server
/// (Microsoft 365 or Google) adds on delivery. Headers further down can be written
/// by the sender and are ignored. Independent of the model, so a message cannot
/// talk its way past it.
/// </summary>
public static partial class SenderAuthentication
{
    public const string ReviewReason = "huijausmerkki";

    public static SenderCheck Evaluate(string? senderName, string? senderAddress, string? headers)
    {
        var signals = new List<string>();
        string? spf = null, dkim = null, dmarc = null;
        if (TopAuthenticationResults(headers) is { } results)
        {
            spf = Result(results, "spf");
            dkim = Result(results, "dkim");
            dmarc = Result(results, "dmarc");
            var compauth = Result(results, "compauth");
            if (dmarc == "fail")
            {
                signals.Add("dmarc_fail");
            }
            else if (compauth == "fail")
            {
                signals.Add("compauth_fail");
            }
            else if (spf is "fail" && dkim is not "pass" && dmarc is not "pass")
            {
                signals.Add("spf_fail");
            }
        }

        if (DisplayNameMismatch(senderName, senderAddress))
        {
            signals.Add("nimi_ristiriita");
        }

        return new SenderCheck(signals, spf, dkim, dmarc);
    }

    /// <summary>The first Authentication-Results header with its folded continuation lines.</summary>
    internal static string? TopAuthenticationResults(string? headers)
    {
        if (string.IsNullOrEmpty(headers))
        {
            return null;
        }

        var lines = headers.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("Authentication-Results:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = lines[i]["Authentication-Results:".Length..];
            for (var j = i + 1; j < lines.Length && lines[j].Length > 0 && lines[j][0] is ' ' or '\t'; j++)
            {
                value += " " + lines[j].Trim();
            }

            return value.Length > 4000 ? value[..4000] : value;
        }

        return null;
    }

    private static string? Result(string results, string method)
    {
        var match = MethodPattern().Matches(results).FirstOrDefault(m => m.Groups[1].Value.Equals(method, StringComparison.OrdinalIgnoreCase));
        return match?.Groups[2].Value.ToLowerInvariant();
    }

    /// <summary>
    /// The display name contains an address whose domain differs from the real
    /// sender's, e.g. "support@pankki.fi" &lt;x@huijari.example&gt;.
    /// </summary>
    internal static bool DisplayNameMismatch(string? senderName, string? senderAddress)
    {
        if (string.IsNullOrEmpty(senderName) || string.IsNullOrEmpty(senderAddress) || !senderAddress.Contains('@'))
        {
            return false;
        }

        var actual = BaseDomain(senderAddress[(senderAddress.LastIndexOf('@') + 1)..]);
        return AddressPattern().Matches(senderName).Any(m => BaseDomain(m.Groups[1].Value) != actual);
    }

    private static string BaseDomain(string domain)
    {
        var labels = domain.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
    }

    [GeneratedRegex(@"(?:^|[;\s])(spf|dkim|dmarc|compauth)\s*=\s*([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AddressPattern();
}
