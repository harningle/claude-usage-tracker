namespace ClaudeUsageTracker.Core.Providers;

/// <summary>Stable identity for a usage provider. Port of upstream <c>Provider</c>.</summary>
public enum Provider
{
    Anthropic,
    Codex,
}

/// <summary>
/// Capability flags that gate provider-specific features; features read these flags rather than
/// comparing provider identities. Port of upstream <c>ProviderCapabilities</c>.
/// </summary>
public sealed record ProviderCapabilities(
    bool TokenCounts,
    bool PerModelBreakdown,
    bool ConsoleBilling,
    bool CliAccountSync,
    bool AutoStartSession,
    bool OverageChecks,
    bool Credits);

/// <summary>
/// Declarative facts about a provider. Ports the identity, name and capabilities of upstream
/// <c>ProviderDescriptor</c>; branding, status page and settings sections arrive with their UI.
/// </summary>
public sealed record ProviderDescriptor(Provider Id, string DisplayName, ProviderCapabilities Capabilities);

public static class ProviderRegistry
{
    private static readonly ProviderDescriptor Anthropic = new(
        Provider.Anthropic,
        "Anthropic",
        new ProviderCapabilities(
            TokenCounts: true,
            PerModelBreakdown: true,
            ConsoleBilling: true,
            CliAccountSync: true,
            AutoStartSession: true,
            OverageChecks: true,
            Credits: false));

    private static readonly ProviderDescriptor Codex = new(
        Provider.Codex,
        "OpenAI Codex",
        new ProviderCapabilities(
            TokenCounts: false,
            PerModelBreakdown: false,
            ConsoleBilling: false,
            CliAccountSync: false,
            // Unlike upstream: through the Codex CLI, for a profile that reads the CLI's login (docs/parity.md difference 39).
            AutoStartSession: true,
            OverageChecks: false,
            Credits: true));

    public static ProviderDescriptor Descriptor(this Provider provider) => provider switch
    {
        Provider.Anthropic => Anthropic,
        Provider.Codex => Codex,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No descriptor for provider."),
    };

    /// <summary>The persisted identifier, matching upstream raw values; never rename one.</summary>
    public static string RawValue(this Provider provider) => provider switch
    {
        Provider.Anthropic => "anthropic",
        Provider.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No raw value for provider."),
    };
}
