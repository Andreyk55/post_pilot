using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

public class MetaConnection
{
    public Guid Id { get; set; }

    /// <summary>
    /// Workspace this connection belongs to. A workspace holds at most ONE active
    /// connection per <see cref="Provider"/>. Disconnected rows survive in the table
    /// as history so reconnecting the same provider account can resurface posts.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Identity-layer provider that owns this connection. Today this is always
    /// <see cref="ProviderType.Meta"/>; the column exists so LinkedIn/X/TikTok
    /// can land on the same table without further schema churn.
    /// </summary>
    public ProviderType Provider { get; set; } = ProviderType.Meta;

    /// <summary>
    /// Stable identifier returned by the provider that pins this connection to
    /// a single external account. For Meta this is the FB user id from
    /// <c>/me</c>. Nullable for transitional safety on pre-existing rows; new
    /// rows always populate it. Combined with <see cref="Provider"/> it is the
    /// identity used to "reconnect the same account ⇒ resurface history."
    /// </summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>
    /// Human-friendly account name (e.g. Meta user's display name). Best-effort,
    /// stamped at connect time; not used for identity matching.
    /// </summary>
    public string? ProviderAccountName { get; set; }

    /// <summary>
    /// The AppUser who connected this account (audit trail of who clicked "Connect").
    /// Kept separate from the workspace-level provider identity.
    /// </summary>
    public Guid UserId { get; set; }
    public required string AccessToken { get; set; }
    public DateTime TokenExpiresAt { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Current connection state. False = the user has disconnected this account.
    /// Always written in lockstep with <see cref="DisconnectedAt"/>: true ↔ DisconnectedAt is null.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Audit trail of when the account was disconnected. Null while connected.
    /// The row itself is never deleted on disconnect — it stays as a historical breadcrumb
    /// so posts that referenced any of its pages/IG accounts keep their FK intact.
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

    // Navigation properties
    public ICollection<ConnectedPage> Pages { get; set; } = new List<ConnectedPage>();
    public ICollection<ConnectedInstagramAccount> InstagramAccounts { get; set; } = new List<ConnectedInstagramAccount>();
}

public class ConnectedPage
{
    public Guid Id { get; set; }

    /// <summary>
    /// Workspace this page belongs to. Mirrors the parent MetaConnection's
    /// WorkspaceId at insert time; never changes.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Nullable so a disconnected parent MetaConnection can be removed (legacy paths)
    /// without orphaning history. New active rows always have it set.
    /// </summary>
    public Guid? MetaConnectionId { get; set; }
    public required string PageId { get; set; } // Facebook Page ID
    public required string Name { get; set; }
    public string? Category { get; set; }
    public string? PictureUrl { get; set; }
    public required string AccessToken { get; set; } // Page-specific access token
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Current connection state. False = page is unlinked/disconnected.
    /// Kept in lockstep with <see cref="DisconnectedAt"/>: true ↔ DisconnectedAt is null.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Audit trail of when the page was disconnected. Null while connected.
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

    // Navigation property
    public MetaConnection? MetaConnection { get; set; }
}

public class ConnectedInstagramAccount
{
    public Guid Id { get; set; }

    /// <summary>
    /// Workspace this Instagram account belongs to. Mirrors the parent
    /// MetaConnection's WorkspaceId at insert time; never changes.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Nullable so a disconnected parent MetaConnection can be removed without orphaning history.
    /// </summary>
    public Guid? MetaConnectionId { get; set; }
    public required string IgBusinessId { get; set; } // Instagram Business Account ID
    public required string Username { get; set; }
    public string? Name { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public required string PageId { get; set; } // Associated Facebook Page ID
    public required string PageName { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Current connection state. False = account is unlinked/disconnected.
    /// </summary>
    public bool IsConnected { get; set; } = true;

    /// <summary>
    /// Audit trail of when the account was disconnected. Null while connected.
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

    // Navigation property
    public MetaConnection? MetaConnection { get; set; }
}

// Temporary token storage for OAuth flow (before user confirms page selection)
public class MetaOAuthState
{
    public Guid Id { get; set; }

    /// <summary>
    /// Workspace this OAuth flow is bound to. Set when the flow starts so that
    /// the resulting MetaConnection lands in the correct workspace even if the
    /// user switches workspaces mid-flow.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    public required string State { get; set; } // OAuth state parameter
    public string? TempAccessToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; } // State expiration (e.g., 10 minutes)
}
