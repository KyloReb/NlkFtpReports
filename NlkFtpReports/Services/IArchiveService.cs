namespace NlkFtpReports.Services;

/// <summary>
/// Represents a single .rar file found in the watch directory.
/// Returned by <see cref="IArchiveService.ScanDirectoryAsync"/>.
/// </summary>
public record RarPackageInfo(
    string FileName,
    string FullPath,
    string RelativeDirectory,
    long FileSizeBytes,
    DateTime LastModified
);

/// <summary>
/// Represents a single entry (file) inside a .rar archive.
/// Returned by <see cref="IArchiveService.ListEntriesAsync"/>.
/// </summary>
public record RarEntryInfo(
    string Key,
    long Size,
    long CompressedSize,
    DateTime LastModified,
    bool IsTextFile
);

/// <summary>
/// Centralized service for scanning, listing, and reading .rar archives
/// using SharpCompress. No database — everything is on-demand.
/// </summary>
public interface IArchiveService
{
    /// <summary>
    /// Scans the configured watch directory for all .rar files
    /// and returns basic metadata (filename, path, size, date).
    /// </summary>
    Task<List<RarPackageInfo>> ScanDirectoryAsync(string directoryPath);

    /// <summary>
    /// Opens the specified .rar file and lists all entries inside it.
    /// Returns null if the password is incorrect or the archive is corrupt.
    /// </summary>
    Task<List<RarEntryInfo>?> ListEntriesAsync(string filePath, string password);

    /// <summary>
    /// Opens the specified .rar file, locates the entry matching <paramref name="entryKey"/>,
    /// and streams its decompressed content into a string.
    /// Returns null if the entry is not found or cannot be read.
    /// </summary>
    Task<string?> ReadTextEntryAsync(string filePath, string entryKey, string password);
}
