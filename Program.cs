using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

string inputPath = Path.GetFullPath(args[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Ошибка: входной файл не найден: {inputPath}");
    return 1;
}

if (!double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double targetSizeMb) || targetSizeMb <= 0)
{
    Console.Error.WriteLine("Ошибка: целевой размер должен быть положительным числом (МБ).");
    return 1;
}

string outputPath = args.Length >= 3
    ? Path.GetFullPath(args[2])
    : Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "_compressed.mp4");

if (!outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
{
    outputPath = Path.ChangeExtension(outputPath, ".mp4");
}

string ffmpegPath = args.Length >= 4 ? args[3] : "ffmpeg";
string ffprobePath = args.Length >= 5 ? args[4] : "ffprobe";

Console.WriteLine($"Входной файл: {inputPath}");
Console.WriteLine($"Целевой размер: {targetSizeMb:F2} MB");
Console.WriteLine($"Выходной файл: {outputPath}");

VideoMetadata metadata;
try
{
    metadata = await ProbeMetadataAsync(ffprobePath, inputPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Ошибка ffprobe: {ex.Message}");
    return 1;
}

if (metadata.DurationSeconds <= 0)
{
    Console.Error.WriteLine("Ошибка: не удалось определить длительность видео.");
    return 1;
}

double targetTotalKbps = (targetSizeMb * 8192.0) / metadata.DurationSeconds;
int audioKbps = CalculateAudioBitrateKbps(targetTotalKbps, metadata.OriginalAudioBitrateKbps);
int videoKbps = (int)Math.Floor(targetTotalKbps - audioKbps - 16);

if (videoKbps < 100)
{
    Console.Error.WriteLine("Ошибка: целевой размер слишком мал для приемлемого качества.");
    return 1;
}

Console.WriteLine($"Длительность: {metadata.DurationSeconds:F2} сек");
Console.WriteLine($"Битрейт аудио: {audioKbps} kbps");
Console.WriteLine($"Битрейт видео: {videoKbps} kbps");

int maxAttempts = 2;
for (int attempt = 1; attempt <= maxAttempts; attempt++)
{
    Console.WriteLine($"\nПопытка {attempt}/{maxAttempts}...");
    bool ok = await EncodeTwoPassAsync(ffmpegPath, inputPath, outputPath, videoKbps, audioKbps);
    if (!ok)
    {
        return 1;
    }

    long actualBytes = new FileInfo(outputPath).Length;
    long targetBytes = (long)(targetSizeMb * 1024 * 1024);
    double diffRatio = (actualBytes - targetBytes) / (double)targetBytes;

    Console.WriteLine($"Итоговый размер: {actualBytes / 1024d / 1024d:F2} MB");

    if (Math.Abs(diffRatio) <= 0.03 || attempt == maxAttempts)
    {
        Console.WriteLine("Готово.");
        return 0;
    }

    double adjustment = targetBytes / (double)actualBytes;
    videoKbps = Math.Max(100, (int)Math.Floor(videoKbps * adjustment));
    Console.WriteLine($"Коррекция битрейта видео до {videoKbps} kbps для более точного размера.");
}

return 0;

static async Task<VideoMetadata> ProbeMetadataAsync(string ffprobePath, string inputPath)
{
    string args = $"-v error -show_entries format=duration:stream=codec_type,bit_rate -of json \"{inputPath}\"";
    ProcessResult result = await RunProcessAsync(ffprobePath, args, printLiveOutput: false);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(result.StdErr.Trim());
    }

    using JsonDocument doc = JsonDocument.Parse(result.StdOut);
    JsonElement root = doc.RootElement;

    double duration = 0;
    if (root.TryGetProperty("format", out JsonElement formatElement) &&
        formatElement.TryGetProperty("duration", out JsonElement durationElement))
    {
        double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
    }

    int? audioKbps = null;
    if (root.TryGetProperty("streams", out JsonElement streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
    {
        foreach (JsonElement stream in streamsElement.EnumerateArray())
        {
            if (!stream.TryGetProperty("codec_type", out JsonElement codecTypeEl))
            {
                continue;
            }

            if (!string.Equals(codecTypeEl.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (stream.TryGetProperty("bit_rate", out JsonElement bitRateEl) &&
                int.TryParse(bitRateEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bitsPerSecond))
            {
                audioKbps = Math.Max(32, bitsPerSecond / 1000);
            }

            break;
        }
    }

    return new VideoMetadata(duration, audioKbps);
}

static int CalculateAudioBitrateKbps(double targetTotalKbps, int? originalAudioKbps)
{
    int baseAudio = originalAudioKbps ?? 128;
    int audio = Math.Clamp(baseAudio, 64, 192);

    double maxAudioShare = targetTotalKbps * 0.25;
    if (audio > maxAudioShare)
    {
        audio = Math.Max(48, (int)Math.Floor(targetTotalKbps * 0.2));
    }

    return audio;
}

static async Task<bool> EncodeTwoPassAsync(string ffmpegPath, string inputPath, string outputPath, int videoKbps, int audioKbps)
{
    string passLogBase = Path.Combine(Path.GetTempPath(), "videocompressor-passlog-" + Guid.NewGuid().ToString("N"));
    string nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    try
    {
        string pass1Args = string.Join(' ',
            "-y",
            $"-i \"{inputPath}\"",
            "-c:v libx264",
            $"-b:v {videoKbps}k",
            "-preset slow",
            "-pass 1",
            "-an",
            "-f mp4",
            $"-passlogfile \"{passLogBase}\"",
            nullDevice
        );

        ProcessResult pass1 = await RunProcessAsync(ffmpegPath, pass1Args, printLiveOutput: true);
        if (pass1.ExitCode != 0)
        {
            Console.Error.WriteLine("Ошибка первой проходки ffmpeg.");
            return false;
        }

        string pass2Args = string.Join(' ',
            "-y",
            $"-i \"{inputPath}\"",
            "-c:v libx264",
            $"-b:v {videoKbps}k",
            "-preset slow",
            "-pass 2",
            "-c:a aac",
            $"-b:a {audioKbps}k",
            "-movflags +faststart",
            $"-passlogfile \"{passLogBase}\"",
            $"\"{outputPath}\""
        );

        ProcessResult pass2 = await RunProcessAsync(ffmpegPath, pass2Args, printLiveOutput: true);
        if (pass2.ExitCode != 0)
        {
            Console.Error.WriteLine("Ошибка второй проходки ffmpeg.");
            return false;
        }

        return true;
    }
    finally
    {
        CleanupPassLogFiles(passLogBase);
    }
}

static void CleanupPassLogFiles(string passLogBase)
{
    string? directory = Path.GetDirectoryName(passLogBase);
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
    {
        return;
    }

    string filePattern = Path.GetFileName(passLogBase) + "*";
    foreach (string file in Directory.EnumerateFiles(directory, filePattern))
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // Игнорируем ошибки очистки временных файлов.
        }
    }
}

static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, bool printLiveOutput)
{
    ProcessStartInfo psi = new()
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using Process process = new() { StartInfo = psi };

    process.Start();

    Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

    await process.WaitForExitAsync();

    string stdOut = await stdOutTask;
    string stdErr = await stdErrTask;

    if (printLiveOutput && !string.IsNullOrWhiteSpace(stdErr))
    {
        Console.WriteLine(stdErr);
    }

    return new ProcessResult(process.ExitCode, stdOut, stdErr);
}

static void PrintUsage()
{
    Console.WriteLine("Использование:");
    Console.WriteLine("  dotnet run -- <inputVideo> <targetSizeMb> [outputMp4] [ffmpegPath] [ffprobePath]");
    Console.WriteLine();
    Console.WriteLine("Пример:");
    Console.WriteLine("  dotnet run -- input.mkv 25 output.mp4");
}

internal sealed record VideoMetadata(double DurationSeconds, int? OriginalAudioBitrateKbps);
internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
