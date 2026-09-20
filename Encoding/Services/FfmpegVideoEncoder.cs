using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Services;

internal interface IVideoEncoder
{
    Task<VideoEncodingResult> EncodeAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<EncodingProgress>? encodingProgress = null,
        CancellationToken cancellationToken = default);
}

internal sealed record VideoEncodingResult(int ExitCode, string Log);

/// <summary>Current FFmpeg encoding position and its reported processing speed.</summary>
internal sealed record EncodingProgress(
    TimeSpan? TotalDuration,
    TimeSpan ProcessedDuration,
    double? Speed,
    bool IsCompleted);

/// <summary>Runs the managed FFmpeg executable for an already validated encoding request.</summary>
internal sealed class FfmpegVideoEncoder(IFfmpegArgumentBuilder argumentBuilder) : IVideoEncoder
{
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<duration>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<VideoEncodingResult> EncodeAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<EncodingProgress>? encodingProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        if (request.EncodingProfile == DefaultEncodingPreset.EncodingProfileDefault &&
            await IsFdkaacAvailableAsync(cancellationToken))
        {
            logProgress?.Report(
                $"[오디오] fdkaac HE-AAC v1 {DefaultEncodingPreset.FdkaacBitrateKbps} kbps를 사용합니다.");
            return await EncodeWithFdkaacAsync(
                ffmpegExecutable,
                request,
                logProgress,
                encodingProgress,
                cancellationToken);
        }

        if (request.EncodingProfile == DefaultEncodingPreset.EncodingProfileDefault)
        {
            logProgress?.Report(
                $"[오디오] fdkaac를 찾지 못해 AAC-LC {DefaultEncodingPreset.AudioBitrate}로 인코딩합니다.");
        }

        return await RunFfmpegWithProgressAsync(
            ffmpegExecutable,
            argumentBuilder.Build(request),
            logProgress,
            encodingProgress,
            cancellationToken);
    }

    private async Task<VideoEncodingResult> EncodeWithFdkaacAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress,
        IProgress<EncodingProgress>? encodingProgress,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.Combine(
            Path.GetDirectoryName(request.OutputPath)!,
            $".127c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        var videoPath = Path.Combine(workingDirectory, "video.mp4");
        var audioPath = Path.Combine(workingDirectory, "audio.m4a");
        var combinedLog = new StringBuilder();

        try
        {
            var videoResult = await RunFfmpegWithProgressAsync(
                ffmpegExecutable,
                argumentBuilder.BuildVideoOnly(request, videoPath),
                logProgress,
                encodingProgress,
                cancellationToken);
            AppendLog(combinedLog, videoResult.Log);
            if (videoResult.ExitCode != 0)
            {
                return new VideoEncodingResult(videoResult.ExitCode, combinedLog.ToString().Trim());
            }

            logProgress?.Report("[오디오] FFmpeg PCM 파이프 → fdkaac 인코딩을 시작합니다.");
            var audioResult = await EncodeAudioWithFdkaacAsync(
                ffmpegExecutable,
                request,
                audioPath,
                logProgress,
                cancellationToken);
            AppendLog(combinedLog, audioResult.Log);

            if (audioResult.ExitCode != 0)
            {
                logProgress?.Report(
                    $"[오디오] fdkaac 인코딩 실패(exit {audioResult.ExitCode}). AAC-LC 방식으로 다시 인코딩합니다.");
                TryDelete(request.OutputPath);
                return await RunFfmpegWithProgressAsync(
                    ffmpegExecutable,
                    argumentBuilder.Build(request),
                    logProgress,
                    encodingProgress,
                    cancellationToken);
            }

            logProgress?.Report("[리먹싱] H.264 영상과 HE-AAC 오디오를 MP4로 합칩니다.");
            var remuxResult = await RunProcessAsync(
                ffmpegExecutable,
                argumentBuilder.BuildRemux(videoPath, audioPath, request.OutputPath),
                logProgress,
                cancellationToken);
            AppendLog(combinedLog, remuxResult.Log);
            return new VideoEncodingResult(remuxResult.ExitCode, combinedLog.ToString().Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logProgress?.Report($"[정리] 임시 파일을 지우지 못했습니다: {workingDirectory}");
            }
        }
    }

    private async Task<VideoEncodingResult> EncodeAudioWithFdkaacAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        string outputPath,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var ffmpegStartInfo = CreateStartInfo(
            ffmpegExecutable,
            argumentBuilder.BuildAudioPipe(request),
            redirectStandardOutput: true);
        var fdkaacStartInfo = CreateStartInfo(
            "fdkaac",
            [
                "-p", DefaultEncodingPreset.FdkaacProfile,
                "-b", DefaultEncodingPreset.FdkaacBitrateKbps,
                "-S",
                "-",
                "-o", outputPath
            ],
            redirectStandardInput: true);

        using var ffmpeg = Process.Start(ffmpegStartInfo)
            ?? throw new InvalidOperationException("오디오용 FFmpeg 프로세스를 시작할 수 없습니다.");
        using var fdkaac = Process.Start(fdkaacStartInfo)
            ?? throw new InvalidOperationException("fdkaac 프로세스를 시작할 수 없습니다.");

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            TryKill(ffmpeg);
            TryKill(fdkaac);
        });

        var log = new StringBuilder();
        var ffmpegErrorTask = ReadLinesAsync(ffmpeg.StandardError, "ffmpeg", log, logProgress, cancellationToken);
        var fdkaacOutputTask = ReadLinesAsync(fdkaac.StandardOutput, "fdkaac", log, logProgress, cancellationToken);
        var fdkaacErrorTask = ReadLinesAsync(fdkaac.StandardError, "fdkaac", log, logProgress, cancellationToken);

        Exception? pipeException = null;
        try
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(
                fdkaac.StandardInput.BaseStream,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            pipeException = exception;
        }
        finally
        {
            try
            {
                fdkaac.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }

        await Task.WhenAll(
            ffmpeg.WaitForExitAsync(cancellationToken),
            fdkaac.WaitForExitAsync(cancellationToken),
            ffmpegErrorTask,
            fdkaacOutputTask,
            fdkaacErrorTask);

        if (pipeException is not null && ffmpeg.ExitCode == 0 && fdkaac.ExitCode == 0)
        {
            throw pipeException;
        }

        var exitCode = ffmpeg.ExitCode != 0 ? ffmpeg.ExitCode : fdkaac.ExitCode;
        return new VideoEncodingResult(exitCode, log.ToString().Trim());
    }

    private async Task<VideoEncodingResult> RunFfmpegWithProgressAsync(
        string ffmpegExecutable,
        IEnumerable<string> arguments,
        IProgress<string>? logProgress,
        IProgress<EncodingProgress>? encodingProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(
            ffmpegExecutable,
            arguments,
            redirectStandardOutput: true);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var log = new StringBuilder();
        long totalDurationTicks = 0;
        var standardErrorTask = ReadStandardErrorAsync();
        var standardOutputTask = ReadProgressAsync();

        await Task.WhenAll(standardErrorTask, standardOutputTask);
        await process.WaitForExitAsync(cancellationToken);
        return new VideoEncodingResult(process.ExitCode, log.ToString().Trim());

        async Task ReadStandardErrorAsync()
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                log.AppendLine(line);
                logProgress?.Report(line);

                var durationMatch = DurationPattern.Match(line);
                if (durationMatch.Success && TimeSpan.TryParse(
                        durationMatch.Groups["duration"].Value,
                        CultureInfo.InvariantCulture,
                        out var duration))
                {
                    Interlocked.Exchange(ref totalDurationTicks, duration.Ticks);
                }
            }
        }

        async Task ReadProgressAsync()
        {
            var processedDuration = TimeSpan.Zero;
            double? speed = null;

            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 1)
                {
                    continue;
                }

                var key = line[..separatorIndex];
                var value = line[(separatorIndex + 1)..];
                switch (key)
                {
                    case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds):
                        processedDuration = TimeSpan.FromTicks(microseconds * 10);
                        break;
                    case "speed":
                        speed = ParseSpeed(value);
                        break;
                    case "progress":
                        var totalTicks = Interlocked.Read(ref totalDurationTicks);
                        encodingProgress?.Report(new EncodingProgress(
                            totalTicks > 0 ? TimeSpan.FromTicks(totalTicks) : null,
                            processedDuration,
                            speed,
                            value == "end"));
                        break;
                }
            }
        }
    }

    private static async Task<VideoEncodingResult> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executable, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{executable} 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var log = new StringBuilder();
        var outputTask = ReadLinesAsync(process.StandardOutput, executable, log, logProgress, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, executable, log, logProgress, cancellationToken);

        await Task.WhenAll(
            process.WaitForExitAsync(cancellationToken),
            outputTask,
            errorTask);

        return new VideoEncodingResult(process.ExitCode, log.ToString().Trim());
    }

    private static async Task<bool> IsFdkaacAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = CreateStartInfo("fdkaac", ["--help"]);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        bool redirectStandardOutput = true,
        bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        string source,
        StringBuilder log,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            log.Append('[').Append(source).Append("] ").AppendLine(line);
            logProgress?.Report($"[{source}] {line}");
        }
    }

    private static void AppendLog(StringBuilder builder, string log)
    {
        if (!string.IsNullOrWhiteSpace(log))
        {
            builder.AppendLine(log);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double? ParseSpeed(string value)
    {
        var normalizedValue = value.Trim().TrimEnd('x');
        return double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            && speed > 0
            ? speed
            : null;
    }
}
