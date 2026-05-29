namespace PostPilot.Api.Enums;

/// <summary>
/// Identity-layer provider that owns an OAuth connection in a workspace.
///
/// Distinct from <see cref="Platform"/> (which is the per-post target surface,
/// e.g. Facebook vs Instagram). One provider may own multiple platforms —
/// Meta owns both Facebook and Instagram.
///
/// The product rule enforced via DB index is: at most one *active* connection
/// per <c>(WorkspaceId, ProviderType)</c> tuple.
/// </summary>
public enum ProviderType
{
    Meta = 0,
    LinkedIn = 1,
    X = 2,
    TikTok = 3,
}
