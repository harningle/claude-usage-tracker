using System.Text.Json;
using ClaudeUsageTracker.App.ClaudeCode;
using ClaudeUsageTracker.App.Storage;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.App.Usage;

/// <summary>
/// What polling needs to know about one profile, copied from its settings so the polling threads never
/// read objects the UI thread edits.
/// </summary>
public sealed record ProfileWatch
{
    public required Guid ProfileId { get; init; }

    public required Provider Provider { get; init; }

    public required TimeSpan Interval { get; init; }

    public CliLink? Cli { get; init; }

    public ClaudeAiLink? ClaudeAi { get; init; }

    public ConsoleLink? Console { get; init; }

    public CodexLink? Codex { get; init; }

    /// <summary>Changes whenever any link or the provider changes; an unchanged key means only settings such as the interval moved.</summary>
    public required string LinkKey { get; init; }

    /// <summary>Where usage comes from; the same rule as <see cref="ProfileSettings.UsageSource"/>.</summary>
    public UsageSourceKind? Source =>
        Provider == Provider.Codex ? (Codex is null ? null : UsageSourceKind.Codex)
        : ClaudeAi is not null ? UsageSourceKind.ClaudeAi
        : Cli is not null ? UsageSourceKind.ClaudeCode
        : null;

    /// <summary>The account the usage belongs to, so one account's cache or rate-limit wait is never applied to another.</summary>
    public string? AccountIdentity => Source switch
    {
        UsageSourceKind.ClaudeCode => Cli!.Identity,
        UsageSourceKind.ClaudeAi => ClaudeAi!.OrganizationId,
        UsageSourceKind.Codex => Codex!.Identity,
        _ => null,
    };

    /// <summary>Console billing applies only to providers that have it and profiles that set it up.</summary>
    public bool TracksBilling => Console is not null && Provider.Descriptor().Capabilities.ConsoleBilling;

    public static ProfileWatch From(ProfileSettings profile)
    {
        // A JSON round trip is the deep copy; its text is also the key that tells a changed link from an unchanged one.
        var links = JsonSerializer.Serialize(
            new Links(profile.CliLink, profile.ClaudeAi, profile.Console, profile.Codex), SettingsStore.JsonOptions);
        var copy = JsonSerializer.Deserialize<Links>(links, SettingsStore.JsonOptions)!;
        return new ProfileWatch
        {
            ProfileId = profile.Id,
            Provider = profile.ProviderKind,
            Interval = TimeSpan.FromSeconds(profile.RefreshInterval),
            Cli = copy.Cli,
            ClaudeAi = copy.ClaudeAi,
            Console = copy.Console,
            Codex = copy.Codex,
            LinkKey = profile.ProviderKind + "|" + links,
        };
    }

    private sealed record Links(CliLink? Cli, ClaudeAiLink? ClaudeAi, ConsoleLink? Console, CodexLink? Codex);
}

/// <summary>
/// The credential a source would send now. A class rather than a record so no generated <c>ToString</c>
/// can print what <see cref="Secret"/> holds.
/// </summary>
public sealed class SourceCredentials
{
    public required LinkStatus Status { get; init; }

    /// <summary>What the source sends: a token or parsed credentials. Null when nothing usable exists.</summary>
    public object? Secret { get; init; }

    /// <summary>
    /// A marker that changes when the credential changes. After the service rejects a credential, the same
    /// marker is not sent again automatically.
    /// </summary>
    public string? Stamp { get; init; }

    /// <summary>A failure to read or save the app's encrypted copy; polling may continue.</summary>
    public string? StorageError { get; init; }

    public CredentialOwnership? Ownership { get; init; }

    public LinkNotice? Notice { get; init; }

    public bool CanFetch => Secret is not null;
}

/// <param name="Status">Set when the request itself showed the link's state, e.g. a signed-out browser session.</param>
public sealed record SourceFetch(FetchResult Result, LinkStatus? Status = null);

/// <summary>
/// One way of getting a profile's usage. Each source uses only that profile's own credentials: a background
/// profile never borrows the active profile's login (plan.md, Milestone 3).
/// </summary>
public interface IUsageSource
{
    /// <summary>Decides which credential to use now. Sends no usage request; may renew a login the app owns.</summary>
    Task<SourceCredentials> ResolveAsync(ProfileWatch watch, CancellationToken cancellation);

    /// <summary>Sends the read-only usage request.</summary>
    /// <param name="previous">The profile's last usage, for providers that reuse a still-future reset time.</param>
    Task<SourceFetch> FetchAsync(ProfileWatch watch, SourceCredentials credentials, UsageData? previous, CancellationToken cancellation);
}
