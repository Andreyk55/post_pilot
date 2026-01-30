using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// FFmpeg-based implementation of video frame extraction.
/// Requires FFmpeg to be installed and available in PATH.
/// </summary>
public class FFmpegVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly ILogger<FFmpegVideoFrameExtractor> _logger;
    private readonly string _tempDirectory;
    private readonly bool _isAvailable;

    // Default positions for thumbnail candidates as percentages
    private static readonly double[] DefaultPositions = { 0.0, 0.10, 0.25, 0.50, 0.75, 0.90 };

    public FFmpegVideoFrameExtractor(ILogger<FFmpegVideoFrameExtractor> logger)
    {
        _logger = logger;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "postpilot-frames");
        Directory.CreateDirectory(_tempDirectory);

        _isAvailable = CheckFFmpegAvailability();
        _logger.LogInformation("FFmpeg availability: {Available}", _isAvailable);
    }

    public bool IsAvailable() => _isAvailable;

    public async Task<ExtractedFrame> ExtractFrameAsync(
        string videoPath,
        double timestampSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available. Please install FFmpeg and ensure it's in your PATH.");
        }

        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file not found", videoPath);
        }

        var outputPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}.jpg");

        try
        {
            // Extract frame using FFmpeg
            // -ss before -i for faster seeking
            // -frames:v 1 to extract only one frame
            // -q:v 2 for high quality JPEG (range 2-31, lower is better)
            var timestamp = FormatTimestamp(timestampSeconds);
            var args = $"-ss {timestamp} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outputPath}\"";

            await RunFFmpegAsync(args, cancellationToken);

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException($"Failed to extract frame at {timestampSeconds}s");
            }

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            return new ExtractedFrame(timestampSeconds, bytes);
        }
        finally
        {
            // Clean up temp file
            try { File.Delete(outputPath); } catch { /* ignore */ }
        }
    }

    public async Task<List<ExtractedFrame>> ExtractThumbnailCandidatesAsync(
        string videoPath,
        int frameCount = 6,
        CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available. Please install FFmpeg and ensure it's in your PATH.");
        }

        var duration = await GetVideoDurationAsync(videoPath, cancellationToken);

        // Calculate positions based on frame count
        var positions = frameCount == 6
            ? DefaultPositions
            : Enumerable.Range(0, frameCount)
                .Select(i => frameCount > 1 ? (double)i / (frameCount - 1) : 0.0)
                .ToArray();

        var frames = new List<ExtractedFrame>();

        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timestamp = position * duration;

            // If position is 0, use a small offset to avoid potential black frames
            if (timestamp < 0.5 && duration > 1)
            {
                timestamp = 0.5;
            }

            try
            {
                var frame = await ExtractFrameAsync(videoPath, timestamp, cancellationToken);
                frames.Add(frame);
                _logger.LogDebug("Extracted frame at {Timestamp}s", timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract frame at {Timestamp}s, skipping", timestamp);
            }
        }

        return frames;
    }

    public async Task<double> GetVideoDurationAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException("FFmpeg is not available. Please install FFmpeg and ensure it's in your PATH.");
        }

        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video file not found", videoPath);
        }

        // Use ffprobe to get duration
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";
        var output = await RunFFprobeAsync(args, cancellationToken);

        if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            _logger.LogDebug("Video duration: {Duration}s", duration);
            return duration;
        }

        // Fallback: try parsing from FFmpeg output
        var ffmpegOutput = await RunFFmpegAsync($"-i \"{videoPath}\" -f null -", cancellationToken, ignoreExitCode: true);
        var durationMatch = Regex.Match(ffmpegOutput, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");

        if (durationMatch.Success)
        {
            var hours = double.Parse(durationMatch.Groups[1].Value);
            var minutes = double.Parse(durationMatch.Groups[2].Value);
            var seconds = double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);
            return hours * 3600 + minutes * 60 + seconds;
        }

        throw new InvalidOperationException("Failed to determine video duration");
    }

    private async Task<string> RunFFmpegAsync(string args, CancellationToken cancellationToken, bool ignoreExitCode = false)
    {
        return await RunProcessAsync("ffmpeg", $"-y {args}", cancellationToken, ignoreExitCode);
    }

    private async Task<string> RunFFprobeAsync(string args, CancellationToken cancellationToken)
    {
        return await RunProcessAsync("ffprobe", args, cancellationToken);
    }

    private async Task<string> RunProcessAsync(string fileName, string args, CancellationToken cancellationToken, bool ignoreExitCode = false)
    {
        _logger.LogDebug("Running: {FileName} {Args}", fileName, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (process.ExitCode != 0 && !ignoreExitCode)
        {
            _logger.LogError("{FileName} failed with exit code {ExitCode}: {Error}",
                fileName, process.ExitCode, error);
            throw new InvalidOperationException($"{fileName} failed: {error}");
        }

        // Return stdout if available, otherwise stderr (FFmpeg outputs info to stderr)
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private bool CheckFFmpegAvailability()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit(5000);

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FFmpeg not found in PATH");
            return false;
        }
    }

    private static string FormatTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
