using System.Diagnostics;
using System.Text.Json;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Extracts video metadata using ffprobe.
/// Requires ffprobe to be available on PATH or configured via settings.
/// For AWS Lambda: use Lambda Layer with ffprobe binary or container image.
/// </summary>
public class FfprobeVideoMetadataExtractor : IVideoMetadataExtractor
{
    private readonly ILogger<FfprobeVideoMetadataExtractor> _logger;
    private readonly string _ffprobePath;

    public FfprobeVideoMetadataExtractor(
        ILogger<FfprobeVideoMetadataExtractor> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        // Allow override via configuration, default to PATH lookup
        _ffprobePath = configuration["Ffprobe:Path"] ?? "ffprobe";
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe is not available at {Path}", _ffprobePath);
            return false;
        }
    }

    public async Task<VideoMetadata?> ExtractAsync(string filePath)
    {
        try
        {
            // Run ffprobe to get JSON output with format and stream info
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start ffprobe process");
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("ffprobe failed with exit code {ExitCode}: {Error}",
                    process.ExitCode, error);
                return null;
            }

            return ParseFfprobeOutput(output, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract video metadata from {FilePath}", filePath);
            return null;
        }
    }

    private VideoMetadata? ParseFfprobeOutput(string jsonOutput, string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;

            // Get format info
            var format = root.GetProperty("format");
            var formatName = format.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
            var durationStr = format.TryGetProperty("duration", out var dur) ? dur.GetString() : null;

            double duration = 0;
            if (!string.IsNullOrEmpty(durationStr) && double.TryParse(durationStr, out var parsedDuration))
            {
                duration = parsedDuration;
            }

            // Find video and audio streams
            int width = 0, height = 0;
            double? fps = null;
            string? videoCodec = null;
            string? audioCodec = null;
            long? bitrate = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;

                    if (codecType == "video" && videoCodec == null)
                    {
                        videoCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;

                        if (stream.TryGetProperty("width", out var w))
                            width = w.GetInt32();
                        if (stream.TryGetProperty("height", out var h))
                            height = h.GetInt32();

                        // Parse frame rate (can be in format "30/1" or "29.97")
                        if (stream.TryGetProperty("r_frame_rate", out var fr))
                        {
                            var fpsStr = fr.GetString();
                            fps = ParseFrameRate(fpsStr);
                        }

                        if (stream.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String)
                        {
                            if (long.TryParse(br.GetString(), out var parsedBitrate))
                                bitrate = parsedBitrate;
                        }
                    }
                    else if (codecType == "audio" && audioCodec == null)
                    {
                        audioCodec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                    }
                }
            }

            // Determine container from format name
            var container = GetContainerFromFormat(formatName);

            // Determine MIME type
            var mimeType = GetMimeTypeFromContainer(container, filePath);

            return new VideoMetadata(
                Width: width,
                Height: height,
                DurationSeconds: duration,
                Container: container,
                VideoCodec: videoCodec,
                AudioCodec: audioCodec,
                Fps: fps,
                Bitrate: bitrate,
                MimeType: mimeType
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ffprobe output");
            return null;
        }
    }

    private static double? ParseFrameRate(string? fpsStr)
    {
        if (string.IsNullOrEmpty(fpsStr))
            return null;

        // Handle format like "30/1" or "30000/1001"
        if (fpsStr.Contains('/'))
        {
            var parts = fpsStr.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var num) &&
                double.TryParse(parts[1], out var den) &&
                den > 0)
            {
                return Math.Round(num / den, 2);
            }
        }

        // Handle plain number
        if (double.TryParse(fpsStr, out var fps))
            return fps;

        return null;
    }

    private static string? GetContainerFromFormat(string? formatName)
    {
        if (string.IsNullOrEmpty(formatName))
            return null;

        // ffprobe may return comma-separated list of formats
        var formats = formatName.Split(',');

        foreach (var format in formats)
        {
            var f = format.Trim().ToLowerInvariant();
            switch (f)
            {
                case "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2":
                    return "mp4";
                case "matroska" or "webm":
                    return "webm";
                case "avi":
                    return "avi";
                case "flv":
                    return "flv";
                case "ogg":
                    return "ogg";
            }
        }

        return formats.FirstOrDefault()?.Trim().ToLowerInvariant();
    }

    private static string GetMimeTypeFromContainer(string? container, string filePath)
    {
        if (!string.IsNullOrEmpty(container))
        {
            return container.ToLowerInvariant() switch
            {
                "mp4" or "m4v" => "video/mp4",
                "webm" => "video/webm",
                "avi" => "video/x-msvideo",
                "mov" => "video/quicktime",
                "flv" => "video/x-flv",
                "ogg" => "video/ogg",
                "mkv" => "video/x-matroska",
                _ => "video/mp4"
            };
        }

        // Fallback to extension
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".flv" => "video/x-flv",
            ".ogg" => "video/ogg",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
    }
}
