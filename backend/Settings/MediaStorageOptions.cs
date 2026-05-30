namespace PostPilot.Api.Settings;

/// <summary>
/// Provider identifiers for the media storage backend. String values are kept
/// kebab-case to match the legacy config (<c>"local-disk"</c>, <c>"s3-compatible"</c>)
/// and the new Supabase option (<c>"supabase"</c>). New providers (R2, AWS S3 native,
/// Hetzner Object Storage, etc.) should be added here.
/// </summary>
public enum MediaStorageProviderType
{
    LocalDisk,
    S3Compatible,
    Supabase,
}

/// <summary>
/// Configuration for the object storage backend.
/// Bound from "MediaStorage" config section.
///
/// Provider values:
///   "local-disk"     — files on the API container's filesystem (default; no MinIO required).
///   "s3-compatible"  — any S3-compatible endpoint (MinIO, S3, R2, Spaces, B2, Wasabi).
///   "supabase"       — Supabase Storage (private bucket; signed upload/download URLs).
///
/// InternalEndpoint vs PublicUploadEndpoint (S3-compatible only):
///   - InternalEndpoint is what the API/Worker hit for HeadObject/GetObject/DeleteObject.
///     Inside Docker this is e.g. http://minio:9000.
///   - PublicUploadEndpoint is signed into presigned upload URLs that go back to the browser.
///     The browser cannot resolve "minio:9000"; it must receive e.g. http://localhost:9000.
///
/// For Supabase, see <see cref="Supabase"/> for the URL + service role key + bucket
/// settings; there is no internal/public split because Supabase Storage is a single
/// public HTTPS endpoint and the browser-facing signed URLs are issued by Supabase itself.
/// </summary>
public class MediaStorageOptions
{
    public const string SectionName = "MediaStorage";

    public string Provider { get; set; } = "local-disk";

    // ── S3-compatible fields (MinIO/S3/R2/Spaces/B2/Wasabi/Hetzner) ──────────
    // These are intentionally kept on the root object so existing configs keep
    // working without nesting changes. They are ignored when Provider="supabase".

    public string Bucket { get; set; } = string.Empty;

    public string InternalEndpoint { get; set; } = string.Empty;

    public string PublicUploadEndpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public bool UseSSL { get; set; }

    public int PresignedUploadExpirationMinutes { get; set; } = 15;

    // ── Supabase Storage sub-options ─────────────────────────────────────────
    // Bound from "MediaStorage:Supabase" — never read when Provider != "supabase".
    public SupabaseStorageOptions Supabase { get; set; } = new();

    /// <summary>Effective bucket regardless of provider — Supabase config nests its own bucket.</summary>
    public string EffectiveBucket =>
        IsSupabase ? Supabase.Bucket : Bucket;

    public MediaStorageProviderType ProviderType =>
        Provider?.ToLowerInvariant() switch
        {
            "supabase"       => MediaStorageProviderType.Supabase,
            "s3-compatible"  => MediaStorageProviderType.S3Compatible,
            _                => MediaStorageProviderType.LocalDisk,
        };

    public bool IsLocalDisk => ProviderType == MediaStorageProviderType.LocalDisk;
    public bool IsS3Compatible => ProviderType == MediaStorageProviderType.S3Compatible;
    public bool IsSupabase => ProviderType == MediaStorageProviderType.Supabase;
}

/// <summary>
/// Supabase Storage backend options. Bound from <c>MediaStorage:Supabase</c>.
///
/// <para>
/// Security note: <see cref="ServiceRoleKey"/> bypasses Row-Level Security and grants
/// full access to the project. It MUST only be set in backend / worker environments,
/// NEVER in any frontend build (Vercel, etc.). Treat it like a database password.
/// </para>
/// </summary>
public class SupabaseStorageOptions
{
    /// <summary>Supabase project URL — e.g. <c>https://abcxyz.supabase.co</c>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Service-role JWT. Backend/worker only — bypasses RLS.
    /// Read from env var <c>MediaStorage__Supabase__ServiceRoleKey</c>.
    /// </summary>
    public string ServiceRoleKey { get; set; } = string.Empty;

    /// <summary>Storage bucket name (must already exist; must be private).</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Default expiry for signed download URLs handed to the browser, Meta Graph,
    /// or the publisher. Should comfortably cover Meta's media fetch window plus
    /// retries; default 1h is sufficient.
    /// </summary>
    public int SignedUrlExpirySeconds { get; set; } = 3600;

    /// <summary>
    /// Upper bound enforced on signed-upload requests as a defense-in-depth check.
    /// Media-level limits in <see cref="MediaOptions"/> are the primary gate; this
    /// is the absolute ceiling regardless of MIME type. 0 = no extra cap.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 0;
}
