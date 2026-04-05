using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.IO;
using System;
namespace Clipper.Services;

public sealed class RecorderService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public string OutputDirectory { get; }
    public string TempDirectory { get; }
    public string FfmpegPath { get; }

    public bool IsRecording => _process is { HasExited: false };

    public RecorderService()
    {
        OutputDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Clipper");

        TempDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipper",
            "Temp");

        FfmpegPath = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRecording)
                return;

            if (!System.IO.File.Exists(FfmpegPath))
            {
                throw new System.IO.FileNotFoundException(
                    "ffmpeg.exe wurde nicht gefunden. Lege ffmpeg.exe neben die Clipper.exe.",
                    FfmpegPath);
            }

            System.IO.Directory.CreateDirectory(OutputDirectory);
            System.IO.Directory.CreateDirectory(TempDirectory);
            ClearTempDirectory();

            var args = BuildRecordArguments();
            _process = StartProcess(args);

            await Task.Delay(800).ConfigureAwait(false);

            if (_process.HasExited)
            {
                var error = await ReadProcessErrorAsync(_process).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "ffmpeg konnte die Aufnahme nicht starten." +
                    (string.IsNullOrWhiteSpace(error) ? string.Empty : Environment.NewLine + error));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SaveClipAsync(int clipSeconds)
    {
        if (clipSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(clipSeconds));

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync().ConfigureAwait(false);

            var files = Directory
                .EnumerateFiles(TempDirectory, "seg_*.mp4")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                throw new InvalidOperationException("Es wurden noch keine Clip-Segmente aufgezeichnet.");

            // Exakt eine Sekunde pro Segment.
            var takeCount = Math.Min(clipSeconds, files.Count);
            var selected = files.Skip(files.Count - takeCount).ToList();

            Directory.CreateDirectory(OutputDirectory);

            var outputFile = Path.Combine(
                OutputDirectory,
                $"Clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            var concatFile = Path.Combine(TempDirectory, $"concat_{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(
                concatFile,
                selected.Select(EscapeForConcatList))
                .ConfigureAwait(false);

            try
            {
                await RunFfmpegAsync(new[]
                {
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-f", "concat",
                    "-safe", "0",
                    "-i", concatFile,
                    "-c", "copy",
                    outputFile
                }).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(concatFile);
            }

            ClearTempDirectory();
            await StartAsync().ConfigureAwait(false);
            return outputFile;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string BuildRecordArguments()
    {
        // Der Desktop wird im virtuellen Monitorraum aufgenommen.
        var left = (int)Math.Round(SystemParameters.VirtualScreenLeft);
        var top = (int)Math.Round(SystemParameters.VirtualScreenTop);
        var width = (int)Math.Round(SystemParameters.VirtualScreenWidth);
        var height = (int)Math.Round(SystemParameters.VirtualScreenHeight);

        var segmentPattern = Path.Combine(TempDirectory, "seg_%06d.mp4");

        return string.Join(" ", new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "gdigrab",
            "-framerate", "30",
            "-draw_mouse", "1",
            "-offset_x", left.ToString(CultureInfo.InvariantCulture),
            "-offset_y", top.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{width}x{height}",
            "-i", "desktop",
            "-an",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-crf", "20",
            "-pix_fmt", "yuv420p",
            "-g", "30",
            "-keyint_min", "30",
            "-force_key_frames", "\"expr:gte(t,n_forced*1)\"",
            "-f", "segment",
            "-segment_time", "1",
            "-reset_timestamps", "1",
            "-segment_format", "mp4",
            $"\"{segmentPattern}\""
        });
    }

    private Process StartProcess(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = arguments,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg konnte nicht gestartet werden.");
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg konnte nicht gestartet werden.");
        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        _ = await outputTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "ffmpeg konnte die Datei nicht schreiben." +
                (string.IsNullOrWhiteSpace(error) ? string.Empty : Environment.NewLine + error));
        }
    }

    private async Task StopInternalAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Wenn ffmpeg nicht mehr sauber reagiert, wird unten hart beendet.
                }

                if (!await WaitForExitAsync(_process, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignore
                    }

                    await WaitForExitAsync(_process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static async Task<string> ReadProcessErrorAsync(Process process)
    {
        try
        {
            return await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string EscapeForConcatList(string path)
    {
        var normalized = path.Replace("\\", "/").Replace("'", "\\'");
        return $"file '{normalized}'";
    }

    private void ClearTempDirectory()
    {
        if (!Directory.Exists(TempDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(TempDirectory))
            TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            _process.Dispose();
            _process = null;
        }
    }
}
