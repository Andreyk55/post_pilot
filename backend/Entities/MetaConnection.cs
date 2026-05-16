namespace PostPilot.Api.Entities;

public class MetaConnection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; } // For future user system, using fixed value for now
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
    public required string State { get; set; } // OAuth state parameter
    public string? TempAccessToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; } // State expiration (e.g., 10 minutes)
}
