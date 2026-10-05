using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeUsageTracker.App.Diagnostics;

/// <summary>
/// What an error that ends the app leaves behind for diagnosis: for it and each inner error, its type and where
/// it was thrown. Never its message, which may carry data (plan.md section 2, "Diagnostics and build location").
/// </summary>
public static class CrashRecord
{
    public const string FileName = "crash.log";

    public static string Format(Exception error, DateTimeOffset at)
    {
        var text = new StringBuilder().Append(at.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        for (var current = error; current is not null; current = current.InnerException)
        {
            text.Append(current.GetType().FullName).Append('\n').Append(current.StackTrace).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Replaces any earlier record. Returns its path, or null when it could not be written.</summary>
    public static string? Write(string directory, Exception error, DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            File.WriteAllText(path, Format(error, at));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
