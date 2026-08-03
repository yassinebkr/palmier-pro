namespace PalmierShell.Core;

/// One AI provider the core can talk to, as reported by
/// palmier_agent_providers. `Id` is the stable machine value passed back to
/// palmier_agent_configure; `Name` is display only.
public sealed record ProviderInfo(string Id, string Name, string DefaultModel) {
    /// True when the provider's model list can be fetched without a key, so
    /// the picker can show the real catalogue straight away.
    public bool PublicModelList { get; init; }
}
