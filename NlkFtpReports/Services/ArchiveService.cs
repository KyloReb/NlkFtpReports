using System.Diagnostics;
using System.Text;

namespace NlkFtpReports.Services;

public class ArchiveService : IArchiveService
{
    private readonly ILogger<ArchiveService> _logger;
    private static readonly string UnrarPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "UnRAR.exe");

    public ArchiveService(ILogger<ArchiveService> logger)
    {
        _logger = logger;
    }

    public Task<List<RarPackageInfo>> ScanDirectoryAsync(string directoryPath)
    {
        var results = new List<RarPackageInfo>();

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Watch directory does not exist: {Path}", directoryPath);
            return Task.FromResult(results);
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.rar", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            var relDir = Path.GetRelativePath(directoryPath, Path.GetDirectoryName(file)!);
            results.Add(new RarPackageInfo(
                info.Name, info.FullName,
                relDir == "." ? "" : relDir,
                info.Length, info.LastWriteTimeUtc
            ));
        }

        return Task.FromResult(results.OrderByDescending(r => r.LastModified).ToList());
    }

    public async Task<List<RarEntryInfo>?> ListEntriesAsync(string filePath, string password)
    {
        return await Task.Run(() =>
        {
            try
            {
                var entries = new List<RarEntryInfo>();
                var psi = new ProcessStartInfo(UnrarPath, $"l -p{password} \"{filePath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    _logger.LogError("UnRAR.exe exited with code {Code} for {File}", proc.ExitCode, filePath);
                    return null;
                }

                var lines = output.Split('\n');
                var regex = new System.Text.RegularExpressions.Regex(
                    @"^\*\s+\.\.A\.\.\.\.\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+)$");

                foreach (var line in lines)
                {
                    var m = regex.Match(line);
                    if (!m.Success) continue;

                    var size = long.Parse(m.Groups[1].Value);
                    var date = DateTime.Parse($"{m.Groups[2].Value} {m.Groups[3].Value}");
                    var name = m.Groups[4].Value.Trim();

                    var ext = Path.GetExtension(name)?.ToLowerInvariant();
                    entries.Add(new RarEntryInfo(name, size, 0, date, ext == ".txt"));
                }

                _logger.LogInformation("UnRAR listed {Count} entries from {File}",
                    entries.Count, Path.GetFileName(filePath));
                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list entries for archive: {Path}", filePath);
                return null;
            }
        });
    }

    public async Task<string?> ReadTextEntryAsync(string filePath, string entryKey, string password)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(UnrarPath, $"p -inul -p{password} \"{filePath}\" \"{entryKey}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi)!;
                var text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0 || string.IsNullOrEmpty(text))
                {
                    _logger.LogWarning("UnRAR returned empty for '{Key}' in {File}", entryKey, filePath);
                    return null;
                }

                return text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read entry '{Key}' from archive: {Path}", entryKey, filePath);
                return null;
            }
        });
    }
}
