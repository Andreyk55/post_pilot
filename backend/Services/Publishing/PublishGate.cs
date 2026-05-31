using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Shared publish-time gate that distinguishes OWNERSHIP (IsConnected) from
/// PUBLISHABILITY (IsConnected AND Status == Active).
///
/// Product rule:
///   - IsConnected=true  + Status=Active         → publish allowed
///   - IsConnected=true  + Status=ReauthRequired → ownership held, publish BLOCKED
///   - IsConnected=false                         → disconnected, publish BLOCKED
///
/// A null parent connection (legacy rows) means "no parent gate" — fall back to the
/// asset's own state. Used by every publisher's prerequisite check as defense in
/// depth; the worker query applies the equivalent filter up-front.
/// </summary>
internal static class PublishGate
{
    /// <summary>
    /// True when the asset (page / IG account) is NOT publishable because either it
    /// or its parent connection is connected-but-ReauthRequired. Disconnected rows are
    /// handled by separate IsConnected checks; this method specifically flags the
    /// reauth case so callers can surface a "needs reconnect" message.
    /// </summary>
    public static bool IsReauthRequired(ConnectedPage? page, MetaConnection? parent)
        => (page is { IsConnected: true, Status: ConnectionStatus.ReauthRequired })
        || (parent is { IsConnected: true, Status: ConnectionStatus.ReauthRequired });

    /// <summary>IG-account overload mirroring <see cref="IsReauthRequired(ConnectedPage?, MetaConnection?)"/>.</summary>
    public static bool IsReauthRequired(ConnectedInstagramAccount? account, MetaConnection? parent)
        => (account is { IsConnected: true, Status: ConnectionStatus.ReauthRequired })
        || (parent is { IsConnected: true, Status: ConnectionStatus.ReauthRequired });
}
