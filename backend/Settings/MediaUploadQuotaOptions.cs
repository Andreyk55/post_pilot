namespace PostPilot.Api.Settings;

public class MediaUploadQuotaOptions
{
    public const string SectionName = "MediaUploadQuota";

    public bool Enabled { get; set; } = true;

    public int MaxUploadsPerUserPerWindow { get; set; } = 100;

    public int WindowHours { get; set; } = 24;
}
