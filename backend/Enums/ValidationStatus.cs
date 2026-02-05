namespace PostPilot.Api.Enums;

/// <summary>
/// Status of media validation for a specific platform/placement combination.
/// </summary>
public enum ValidationStatus
{
    /// <summary>
    /// Validation has not been performed yet.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Media passed all validation rules.
    /// </summary>
    Valid = 1,

    /// <summary>
    /// Media failed one or more validation rules and cannot be published.
    /// </summary>
    Invalid = 2,

    /// <summary>
    /// Media passed required rules but has warnings (e.g., suboptimal dimensions).
    /// </summary>
    Warning = 3
}
