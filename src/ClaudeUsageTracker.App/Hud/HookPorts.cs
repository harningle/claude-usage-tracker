using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeUsageTracker.App.Hud;

/// <summary>An inclusive range of TCP ports.</summary>
public sealed record PortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;
}

/// <summary>The ports Windows may hand out or has set aside, which the hook listener must stay out of.</summary>
public sealed record PortReservations(PortRange Dynamic, IReadOnlyList<PortRange> Excluded)
{
    /// <summary>
    /// A registered port (1024 to 65535) outside the dynamic range, from which Windows assigns outgoing
    /// connections' ports, and outside every excluded range, which other software has reserved.
    /// </summary>
    public bool IsEligible(int port) =>
        port is >= 1024 and <= 65535 && !Dynamic.Contains(port) && !Excluded.Any(range => range.Contains(port));
}

/// <summary>The port ranges could not be read, so no port can be called eligible.</summary>
public sealed class PortQueryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Chooses the hook listener's loopback port (plan.md section 2): a random port outside this machine's current
/// dynamic and excluded ranges, which are read from Windows each time and differ between machines. The choice
/// is kept only once the listener has bound it, and it is checked again at every start. Nothing here changes a
/// machine's port reservations.
/// </summary>
public static partial class HookPorts
{
    private const int ChoiceAttempts = 32;

    /// <summary>The ranges as <c>netsh</c> reports them for IPv4 TCP.</summary>
    public static PortReservations Query()
    {
        var dynamic = ParseDynamicRange(Netsh("int", "ipv4", "show", "dynamicport", "tcp"));
        var excluded = ParseExcludedRanges(Netsh("int", "ipv4", "show", "excludedportrange", "protocol=tcp"));
        return new PortReservations(dynamic, excluded);
    }

    /// <summary>
    /// Reads "Start Port : 1024 / Number of Ports : 13977". The labels are translated on a localised Windows, so
    /// only the two numbers after a colon are read, in their fixed order.
    /// </summary>
    public static PortRange ParseDynamicRange(string output)
    {
        var numbers = LabelledNumber().Matches(output).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        if (numbers is not [var start, var count] || start is < 1 or > 65535 || count < 1 || start + count - 1 > 65535)
        {
            throw new PortQueryException("Windows did not report its dynamic TCP port range in the expected form.");
        }

        return new PortRange(start, start + count - 1);
    }

    /// <summary>Reads the table rows "start end", with or without the "*" of an administered exclusion.</summary>
    public static IReadOnlyList<PortRange> ParseExcludedRanges(string output) =>
        [.. RangeRow().Matches(output).Select(m => new PortRange(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))];

    /// <summary>
    /// A random eligible port that <paramref name="tryBind"/> could bind exclusively, or null when none of the
    /// attempts could. A port in use is skipped, never taken over.
    /// </summary>
    public static int? Choose(PortReservations reservations, Random random, Func<int, bool> tryBind)
    {
        var candidates = Enumerable.Range(1024, 65536 - 1024).Where(reservations.IsEligible).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        random.Shuffle(candidates);
        foreach (var port in candidates.Take(ChoiceAttempts))
        {
            if (tryBind(port))
            {
                return port;
            }
        }

        return null;
    }

    private static string Netsh(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            // The numbers are ASCII in every code page; the translated labels are not read.
            StandardOutputEncoding = Encoding.Latin1,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new PortQueryException("netsh did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill();
                throw new PortQueryException("netsh did not answer in time.");
            }

            return process.ExitCode == 0
                ? output.GetAwaiter().GetResult()
                : throw new PortQueryException($"netsh ended with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new PortQueryException($"netsh could not be run: {e.Message}", e);
        }
    }

    [GeneratedRegex(@":[ \t]*(\d+)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex LabelledNumber();

    [GeneratedRegex(@"^[ \t]*(\d+)[ \t]+(\d+)[ \t]*\*?[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex RangeRow();
}
