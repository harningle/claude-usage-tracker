using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HookBench;

/// <summary>Wall-clock ticks shared with the helper process (precise system time on Windows 11).</summary>
internal static class Clock
{
    public static long Now => DateTime.UtcNow.Ticks;

    public static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;
}

internal sealed class Options(Dictionary<string, string> values)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected --name value pairs, got '{args[i]}'.");
            }

            values[args[i][2..]] = args[++i];
        }

        return new Options(values);
    }

    public string Required(string name) =>
        values.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required.");

    public string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);

    public int GetInt(string name, int fallback) =>
        values.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
}

internal static class Stats
{
    /// <summary>Linear-interpolated percentile (the R-7 / Excel definition).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.Order().ToArray();
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }

    public static object Summary(IReadOnlyList<double> values) => new
    {
        n = values.Count,
        p50 = Round(Percentile(values, 0.50)),
        p95 = Round(Percentile(values, 0.95)),
        mean = Round(values.Count == 0 ? double.NaN : values.Average()),
        max = Round(values.Count == 0 ? double.NaN : values.Max()),
    };

    public static double? Round(double value) => double.IsNaN(value) ? null : Math.Round(value, 1);
}

internal static class Ports
{
    /// <summary>
    /// A random loopback port outside the machine's current TCP dynamic and excluded ranges, as the
    /// plan requires for the HTTP route; never port zero.
    /// </summary>
    public static int PickEligible(ISet<int> avoid)
    {
        var dynamic = Netsh("int ipv4 show dynamicport tcp");
        var numbers = Regex.Matches(dynamic, @":\s*(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        var (dynamicStart, dynamicCount) = (numbers[0], numbers[1]);
        var excluded = Regex.Matches(Netsh("int ipv4 show excludedportrange protocol=tcp"), @"^\s*(\d+)\s+(\d+)", RegexOptions.Multiline)
            .Select(m => (Start: int.Parse(m.Groups[1].Value), End: int.Parse(m.Groups[2].Value)))
            .ToArray();

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var port = Random.Shared.Next(1025, 65536);
            if (avoid.Contains(port)
                || (port >= dynamicStart && port < dynamicStart + dynamicCount)
                || excluded.Any(r => port >= r.Start && port <= r.End))
            {
                continue;
            }

            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }

        throw new InvalidOperationException("No eligible loopback port found.");
    }

    private static string Netsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Write(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Indented));
}
