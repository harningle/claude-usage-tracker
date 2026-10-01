using System.IO;
using System.Text;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Providers.Codex;

namespace ClaudeUsageTracker.App.CodexCli;

public enum CodexLoginState
{
    /// <summary>No <c>auth.json</c>: the Codex CLI is not signed in here.</summary>
    NotSignedIn,

    /// <summary>The file exists but could not be read.</summary>
    Unreadable,

    /// <summary>The file has no usable credentials (or was read mid-write).</summary>
    Invalid,

    Usable,
}

/// <summary>One read-only observation of a Codex home directory.</summary>
public sealed class CodexSnapshot
{
    public required string Home { get; init; }

    public required CodexLoginState State { get; init; }

    public CodexCredentials? Credentials { get; init; }

    /// <summary>The account this login belongs to: its ChatGPT account ID, else the signed-in email.</summary>
    public string? Identity => Credentials?.ResolvedAccountId ?? Credentials?.Email;
}

/// <summary>
/// Read-only access to the Codex CLI's files (upstream <c>CodexAuthService</c>'s file handling). The Codex
/// CLI owns <c>auth.json</c>: the tracker never writes or refreshes it, because a Codex takeover has not
/// been verified (plan.md section 2); it reads the file through and waits for the CLI to renew it.
/// </summary>
public static class CodexStore
{
    /// <summary><c>CODEX_HOME</c> when set and not blank, otherwise <c>.codex</c> in the user profile.</summary>
    public static string Home(string userProfile, string? codexHomeOverride) =>
        string.IsNullOrWhiteSpace(codexHomeOverride)
            ? Path.Combine(userProfile, ".codex")
            : Path.GetFullPath(codexHomeOverride.Trim());

    public static CodexSnapshot Read(string home)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(AtomicFile.ReadSharedBytes(Path.Combine(home, "auth.json"))).TrimStart('﻿');
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CodexSnapshot { Home = home, State = CodexLoginState.NotSignedIn };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new CodexSnapshot { Home = home, State = CodexLoginState.Unreadable };
        }

        var credentials = CodexAuthJson.Parse(text);
        return new CodexSnapshot
        {
            Home = home,
            State = credentials is null ? CodexLoginState.Invalid : CodexLoginState.Usable,
            Credentials = credentials,
        };
    }

    /// <summary>The contents of <c>config.toml</c>, which may redirect the usage endpoint; null when absent or unreadable.</summary>
    public static string? ReadConfig(string home)
    {
        try
        {
            return Encoding.UTF8.GetString(AtomicFile.ReadSharedBytes(Path.Combine(home, "config.toml")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
