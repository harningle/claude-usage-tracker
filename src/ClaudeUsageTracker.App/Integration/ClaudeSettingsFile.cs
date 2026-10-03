using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.App.Storage;

namespace ClaudeUsageTracker.App.Integration;

/// <summary>Claude Code's <c>settings.json</c> exists but cannot be edited safely. Reported; the file is left as it is.</summary>
public sealed class ClaudeSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Edits of a Claude Code folder's <c>settings.json</c>, which belongs to the user and to other tools. Each edit
/// reads the file, changes only the tracker's own entries, and replaces the file only if it has not changed
/// since it was read; every other member keeps its value and its place. A writer that ignores this can still
/// slip in between the last check and the replacement (plan.md section 2).
/// </summary>
public static class ClaudeSettingsFile
{
    private const int Attempts = 3;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PathFor(string configDirectory) => Path.Combine(configDirectory, "settings.json");

    /// <summary>The file's object, or null when there is no file.</summary>
    public static JsonObject? Read(string path) => ReadBytes(path) is { } bytes ? Parse(path, bytes) : null;

    /// <summary>
    /// Applies <paramref name="change"/> to the settings and saves them when it reports a change. A missing file
    /// is an empty object, and is only created when something is added to it.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public static bool Update(string path, Func<JsonObject, bool> change)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var before = ReadBytes(path);
            var settings = before is null ? new JsonObject() : Parse(path, before);
            string text;
            try
            {
                if (!change(settings))
                {
                    return false;
                }

                text = settings.ToJsonString(Indented);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // A member with a repeated name or of an unexpected kind somewhere inside.
                throw new ClaudeSettingsException($"{path} has a shape that cannot be edited safely and was not changed: {e.Message}", e);
            }

            // Keep the file's final line break if it had one.
            var after = Encoding.UTF8.GetBytes(before is { Length: > 0 } && before[^1] == (byte)'\n' ? text + "\n" : text);

            var current = ReadBytes(path);
            if (current is null != before is null || (current is not null && !current.AsSpan().SequenceEqual(before)))
            {
                // Claude Code or another tool wrote meanwhile: start again from what is there now.
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, after);
            return true;
        }

        throw new ClaudeSettingsException($"{path} kept changing while it was being edited. Nothing was changed.");
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return AtomicFile.ReadSharedBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ClaudeSettingsException($"{path} could not be read: {e.Message}", e);
        }
    }

    private static JsonObject Parse(string path, byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            if (JsonNode.Parse(json, null, new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject settings)
            {
                // Touch every member now, so a duplicate name fails here and not half-way through an edit.
                _ = settings.Count;
                return settings;
            }
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new ClaudeSettingsException($"{path} is not valid JSON and was not changed: {e.Message}", e);
        }

        throw new ClaudeSettingsException($"{path} does not hold a JSON object and was not changed.");
    }
}

/// <summary>How a folder's hook entries compare with what the tracker would install.</summary>
public enum HookInstallStatus
{
    NotInstalled,

    /// <summary>All eight entries point at the running listener.</summary>
    Installed,

    /// <summary>Some of the eight are missing.</summary>
    Partial,

    /// <summary>Entries of the tracker's exist for another port or path secret; a repair replaces them.</summary>
    Outdated,
}

/// <summary>
/// The tracker's hook entries in a <c>settings.json</c>: upstream's eight passive HTTP hooks
/// (<c>NotchHookInstaller</c>), each <c>{"matcher": "", "hooks": [{"type": "http", "url": …, "timeout": 3}]}</c>.
/// An entry is the tracker's own when its URL addresses one of the loopback ports the tracker has used.
/// </summary>
public static class HookSettings
{
    /// <summary>Upstream's eight events. No hook that decides, approves or adds context is among them.</summary>
    public static readonly IReadOnlyList<(string Event, string Suffix)> Events =
    [
        ("SessionStart", "session-start"), ("SessionEnd", "session-end"), ("UserPromptSubmit", "user-prompt-submit"),
        ("PreToolUse", "pre-tool-use"), ("PostToolUse", "post-tool-use"), ("PostToolUseFailure", "post-tool-use-failure"),
        ("Stop", "stop"), ("Notification", "notification"),
    ];

    public const int TimeoutSeconds = 3;

    public static string BaseUrl(int port) => $"http://127.0.0.1:{port}/hook/";

    public static string Url(int port, string token, string suffix) => $"{BaseUrl(port)}{token}/{suffix}";

    public static HookInstallStatus Status(JsonObject? settings, int port, string token, IReadOnlyCollection<int> ownedPorts)
    {
        var expected = Events.Select(e => Url(port, token, e.Suffix)).ToHashSet(StringComparer.Ordinal);
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var outdated = false;
        foreach (var url in OwnedUrls(settings, ownedPorts.Append(port)))
        {
            if (expected.Contains(url))
            {
                installed.Add(url);
            }
            else
            {
                outdated = true;
            }
        }

        return outdated ? HookInstallStatus.Outdated
            : installed.Count == expected.Count ? HookInstallStatus.Installed
            : installed.Count > 0 ? HookInstallStatus.Partial
            : HookInstallStatus.NotInstalled;
    }

    /// <summary>Removes every entry of the tracker's, then adds the eight for this listener. No change when they are already exactly there.</summary>
    public static bool Install(JsonObject settings, int port, string token, IReadOnlyCollection<int> ownedPorts)
    {
        if (Status(settings, port, token, ownedPorts) == HookInstallStatus.Installed)
        {
            return false;
        }

        Remove(settings, [.. ownedPorts, port]);
        if (settings["hooks"] is null)
        {
            settings["hooks"] = new JsonObject();
        }

        if (settings["hooks"] is not JsonObject hooks)
        {
            throw new ClaudeSettingsException("The \"hooks\" setting is not an object; it was not changed.");
        }

        foreach (var (name, suffix) in Events)
        {
            if (hooks[name] is null)
            {
                hooks[name] = new JsonArray();
            }

            if (hooks[name] is not JsonArray groups)
            {
                throw new ClaudeSettingsException($"The \"hooks.{name}\" setting is not a list; nothing was changed.");
            }

            groups.Add(new JsonObject
            {
                ["matcher"] = "",
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = Url(port, token, suffix), ["timeout"] = TimeoutSeconds }),
            });
        }

        return true;
    }

    /// <summary>Removes the tracker's entries from every event; groups and events left empty by that go too. Foreign entries stay.</summary>
    public static bool Remove(JsonObject settings, IReadOnlyCollection<int> ownedPorts)
    {
        if (settings["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var changed = false;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups)
            {
                continue;
            }

            var removedHere = false;
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray list)
                {
                    continue;
                }

                var before = list.Count;
                for (var h = list.Count - 1; h >= 0; h--)
                {
                    if (IsOwned(list[h], ownedPorts))
                    {
                        list.RemoveAt(h);
                    }
                }

                if (list.Count != before)
                {
                    removedHere = true;
                    if (list.Count == 0)
                    {
                        groups.RemoveAt(g);
                    }
                }
            }

            if (removedHere && groups.Count == 0)
            {
                hooks.Remove(name);
            }

            changed |= removedHere;
        }

        if (changed && hooks.Count == 0)
        {
            settings.Remove("hooks");
        }

        return changed;
    }

    private static IEnumerable<string> OwnedUrls(JsonObject? settings, IEnumerable<int> ownedPorts)
    {
        var ports = ownedPorts.ToList();
        if (settings?["hooks"] is not JsonObject hooks)
        {
            yield break;
        }

        foreach (var (_, value) in hooks)
        {
            foreach (var group in (value as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                foreach (var hook in (group["hooks"] as JsonArray) ?? [])
                {
                    if (IsOwned(hook, ports))
                    {
                        yield return hook!["url"]!.GetValue<string>();
                    }
                }
            }
        }
    }

    private static bool IsOwned(JsonNode? hook, IReadOnlyCollection<int> ownedPorts) =>
        hook is JsonObject entry
        && entry["url"] is JsonValue value && value.TryGetValue(out string? url)
        && ownedPorts.Any(port => url.StartsWith(BaseUrl(port), StringComparison.Ordinal));
}

/// <summary>
/// The tracker's <c>statusLine</c> entry: <c>{"type": "command", "command": "&lt;helper&gt; statusline"}</c>, pointing at
/// the installed helper and never at a build folder.
/// </summary>
public static class StatuslineSettings
{
    /// <summary>
    /// The command for a helper path. Claude Code runs it through Git Bash when that is installed and through
    /// PowerShell otherwise. Written with forward slashes and no quotes, one text is valid in both; a path that
    /// needs quoting needs each shell's own form.
    /// </summary>
    public static string Command(string helperPath, bool gitBash)
    {
        var path = helperPath.Replace('\\', '/');
        if (path.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '/' or ':' or '-'))
        {
            return $"{path} statusline";
        }

        return gitBash
            ? $"'{path.Replace("'", "'\\''")}' statusline"
            : $"& '{path.Replace("'", "''")}' statusline";
    }

    /// <summary>Whether a <c>statusLine</c> value runs the tracker's helper at <paramref name="helperPath"/>.</summary>
    public static bool IsOwned(JsonNode? statusLine, string helperPath) =>
        CommandOf(statusLine) is { } command
        && (command == Command(helperPath, gitBash: true) || command == Command(helperPath, gitBash: false)
            || command.Contains(helperPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || command.Contains(helperPath, StringComparison.OrdinalIgnoreCase));

    public static string? CommandOf(JsonNode? statusLine) =>
        statusLine is JsonObject entry && entry["command"] is JsonValue value && value.TryGetValue(out string? command) ? command : null;

    public static JsonObject Entry(string command) => new() { ["type"] = "command", ["command"] = command };
}
