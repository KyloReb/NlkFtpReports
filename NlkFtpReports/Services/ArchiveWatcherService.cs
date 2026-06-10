using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NlkFtpReports.Services;

public class ArchiveWatcherService : IDisposable
{
    private readonly ILogger<ArchiveWatcherService> _logger;
    private readonly string _watchDir;
    private readonly string _downloadsPath;
    private FileSystemWatcher? _downloadsWatcher;
    private FileSystemWatcher? _watchDirWatcher;
    private readonly HashSet<string> _recentlyProcessed = new();
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(10);
    private static readonly Regex NamePattern = new(
        @"NLK_Reports_(\d{2})-(\d{2})-(\d{4})\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public event Action<string>? OnNewArchiveArrived;

    public ArchiveWatcherService(IOptions<NlkFtpSettings> settings, ILogger<ArchiveWatcherService> logger)
    {
        _watchDir = settings.Value.WatchDirectory;
        _downloadsPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads";
        _logger = logger;
    }

    public void Start()
    {
        if (!Directory.Exists(_watchDir))
        {
            _logger.LogWarning("Watch directory does not exist, watcher not started: {Path}", _watchDir);
            return;
        }

        ScanMissedDownloads();

        _downloadsWatcher = new FileSystemWatcher(_downloadsPath, "NLK_Reports_*.rar")
        {
            EnableRaisingEvents = true
        };
        _downloadsWatcher.Created += OnDownloadsFileCreated;

        _watchDirWatcher = new FileSystemWatcher(_watchDir, "*.rar")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        _watchDirWatcher.Created += OnWatchDirFileCreated;

        _logger.LogInformation("ArchiveWatcher started – Downloads: {Dl}, WatchDir: {Wd}", _downloadsPath, _watchDir);
    }

    private void ScanMissedDownloads()
    {
        if (!Directory.Exists(_downloadsPath)) return;

        foreach (var file in Directory.EnumerateFiles(_downloadsPath, "NLK_Reports_*.rar"))
        {
            var name = Path.GetFileName(file);
            if (AlreadyExistsInWatchDir(name)) continue;

            var match = NamePattern.Match(name);
            if (!match.Success) continue;

            try
            {
                WaitForFileReady(file);
                var folderName = $"{match.Groups[1].Value}{match.Groups[3].Value}";
                var targetDir = Directory.CreateDirectory(Path.Combine(_watchDir, folderName)).FullName;
                var targetPath = Path.Combine(targetDir, name);

                if (!File.Exists(targetPath))
                {
                    File.Copy(file, targetPath);
                    _logger.LogInformation("Startup scan – copied: {Name} → {Folder}", name, folderName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup scan – could not copy {Name}", name);
            }
        }
    }

    private bool AlreadyExistsInWatchDir(string fileName)
    {
        if (!Directory.Exists(_watchDir)) return false;
        foreach (var existing in Directory.EnumerateFiles(_watchDir, fileName, SearchOption.AllDirectories))
        {
            if (Path.GetFileName(existing).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnDownloadsFileCreated(object sender, FileSystemEventArgs e)
    {
        var name = e.Name;
        var match = NamePattern.Match(name);
        if (!match.Success) return;

        try
        {
            WaitForFileReady(e.FullPath);

            var folderName = $"{match.Groups[1].Value}{match.Groups[3].Value}";
            var targetDir = Directory.CreateDirectory(Path.Combine(_watchDir, folderName)).FullName;
            var targetPath = Path.Combine(targetDir, name);

            File.Copy(e.FullPath, targetPath, overwrite: false);

            lock (_recentlyProcessed) _recentlyProcessed.Add(targetPath);
            _ = Task.Delay(DedupWindow).ContinueWith(_ => { lock (_recentlyProcessed) _recentlyProcessed.Remove(targetPath); });

            _logger.LogInformation("Copied: {Name} → {Folder}", name, folderName);
            OnNewArchiveArrived?.Invoke(targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process download: {Path}", e.FullPath);
        }
    }

    private void OnWatchDirFileCreated(object sender, FileSystemEventArgs e)
    {
        lock (_recentlyProcessed)
        {
            if (_recentlyProcessed.Contains(e.FullPath))
                return;
        }

        try
        {
            WaitForFileReady(e.FullPath);
            OnNewArchiveArrived?.Invoke(e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process watch dir file: {Path}", e.FullPath);
        }
    }

    private static void WaitForFileReady(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
        }
    }

    public void Dispose()
    {
        _downloadsWatcher?.Dispose();
        _watchDirWatcher?.Dispose();
    }
}
