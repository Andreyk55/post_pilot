namespace PostPilot.Api.Enums;

/// <summary>
/// Optional self-selected topic for a <see cref="Entities.SupportContactRequest"/>.
/// Serialized by name (the global <c>JsonStringEnumConverter</c>), so the frontend
/// sends e.g. <c>"category": "DataDeletion"</c>. Optional on the request — a null
/// category is a valid "General question".
/// </summary>
public enum SupportCategory
{
    /// <summary>General question / anything that does not fit another bucket.</summary>
    General = 0,

    /// <summary>Sign-in, profile, or account-level problems.</summary>
    AccountIssue = 1,

    /// <summary>Connecting or re-authorizing a Facebook / Meta account.</summary>
    MetaConnection = 2,

    /// <summary>Problems publishing to Instagram.</summary>
    InstagramPublishing = 3,

    /// <summary>Questions about deleting data or a PostPilot account.</summary>
    DataDeletion = 4,

    /// <summary>Billing or subscription questions.</summary>
    Billing = 5,

    /// <summary>Something is broken / not working as expected.</summary>
    BugReport = 6,

    /// <summary>A request for a new capability.</summary>
    FeatureRequest = 7,
}
