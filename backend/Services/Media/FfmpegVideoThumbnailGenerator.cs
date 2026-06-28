using System.ComponentModel;
using System.Diagnostics;
using SixLabors.ImageSharp;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Local ffmpeg-based video thumbnail extraction. This never calls external media or AI services.
/// </summary>
public sealed class FfmpegVideoThumbnailGenerator : IVideoThumbnailGenerator
{
    private readonly ILogger<FfmpegVideoThumbnailGenerator> _logger;
    private readonly string _ffmpegPath;

    public FfmpegVideoThumbnailGenerator(
        ILogger<FfmpegVideoThumbnailGenerator> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _ffmpegPath = configuration["Ffmpeg:Path"] ?? "ffmpeg";
    }

    public async Task<VideoThumbnailResult> GenerateAsync(
        string sourceVideoPath,
        string outputImagePath,
        int maxWidth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoPath) || !File.Exists(sourceVideoPath))
            throw new FileNotFoundException("Video source file was not found.", sourceVideoPath);

        var outputDirectory = Path.GetDirectoryName(outputImagePath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var filter = $"scale='min({maxWidth},iw)':-2";
        var primaryArgs = $"-hide_banner -loglevel error -y -ss 00:00:01 -i \"{sourceVideoPath}\" -frames:v 1 -vf \"{filter}\" -q:v 2 -f image2 \"{outputImagePath}\"";

        var (primaryExitCode, primaryError) = await RunProcessAsync(primaryArgs, cancellationToken);
        if (primaryExitCode != 0 || !File.Exists(outputImagePath))
        {
            TryDelete(outputImagePath);

            var fallbackArgs = $"-hide_banner -loglevel error -y -ss 00:00:00 -i \"{sourceVideoPath}\" -frames:v 1 -vf \"{filter}\" -q:v 2 -f image2 \"{outputImagePath}\"";
            var (fallbackExitCode, fallbackError) = await RunProcessAsync(fallbackArgs, cancellationToken);
            if (fallbackExitCode != 0 || !File.Exists(outputImagePath))
            {
                throw new InvalidOperationException(
                    $"FFmpeg thumbnail extraction failed ({DescribeFailure(primaryError, fallbackError)}).");
            }
        }

        await using var imageStream = File.OpenRead(outputImagePath);
        using var image = await Image.LoadAsync(imageStream, cancellationToken);
        var fileInfo = new FileInfo(outputImagePath);

        _logger.LogInformation(
            "Generated local video thumbnail {Width}x{Height} sizeBytes={SizeBytes}",
            image.Width,
            image.Height,
            fileInfo.Length);

        return new VideoThumbnailResult(
            MimeType: "image/jpeg",
            Width: image.Width,
            Height: image.Height,
            SizeBytes: fileInfo.Length);
    }

    private async Task<(int ExitCode, string Error)> RunProcessAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (process.ExitCode, error.Trim());
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"FFmpeg is not available at '{_ffmpegPath}'.", ex);
        }
    }

    private static string DescribeFailure(string primaryError, string fallbackError)
    {
        if (!string.IsNullOrWhiteSpace(fallbackError))
            return fallbackError;
        if (!string.IsNullOrWhiteSpace(primaryError))
            return primaryError;
        return "no ffmpeg output";
    }

    private static void TryDelete(string outputImagePath)
    {
        try
        {
            if (File.Exists(outputImagePath))
                File.Delete(outputImagePath);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}