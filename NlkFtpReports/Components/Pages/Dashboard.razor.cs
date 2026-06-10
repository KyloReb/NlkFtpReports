using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using NlkFtpReports.Services;

namespace NlkFtpReports.Components.Pages;

public partial class Dashboard : IDisposable
{
    // ── Dependencies ──
    [Inject] private IArchiveService ArchiveService { get; set; } = null!;
    [Inject] private IOptions<NlkFtpSettings> Settings { get; set; } = null!;
    [Inject] private ILogger<Dashboard> Logger { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private ArchiveWatcherService ArchiveWatcher { get; set; } = null!;
    [Inject] private SearchService Search { get; set; } = null!;

    // ── Package list state ──
    private List<RarPackageInfo> _packages = new();
    private List<RarPackageInfo> _filteredPackages = new();
    private RarPackageInfo? _selectedPackage;
    private bool _isScanning;
    private string _filterText = "";
    private readonly HashSet<string> _expandedFolders = new();

    // ── Archive contents state ──
    private List<RarEntryInfo>? _expandedEntries;
    private List<RarEntryInfo>? _filteredEntries;
    private bool _isLoadingEntries;
    private string _entryFilterText = "";
    private readonly HashSet<string> _expandedEntryFolders = new();

    // ── Tab / split-view state ──
    private readonly List<OpenTab> _openTabs = new();
    private int _activeTabIndex;
    private int _activeTabIndexRight = -1;
    private bool _splitView;
    private int _renderTick;

    // ── UI state ──
    private string? _errorMessage;
    private string _errorType = "info";
    private string? _statusText;
    private bool _sidebarCollapsed;
    private bool _contentsCollapsed;
    private double _zoomLevel = 1.0;
    private bool _wrapText;
    private bool _showTabMenu;

    // ── Toast state ──
    private readonly List<ToastMessage> _toasts = new();
    private int _toastCounter;

    // ── Computed properties ──
    private OpenTab? ActiveTab => _openTabs.Count > 0 && _activeTabIndex >= 0 && _activeTabIndex < _openTabs.Count
        ? _openTabs[_activeTabIndex] : null;

    private OpenTab? ActiveTabRight => _splitView && _openTabs.Count > 0 && _activeTabIndexRight >= 0 && _activeTabIndexRight < _openTabs.Count
        ? _openTabs[_activeTabIndexRight] : null;

    // ── Lifecycle ──
    protected override async Task OnInitializedAsync()
    {
        Search.OnChanged += OnSearchModeChanged;
        ArchiveWatcher.OnNewArchiveArrived += OnNewArchiveDetected;
        await RefreshPackages();
    }

    public void Dispose()
    {
        Search.OnChanged -= OnSearchModeChanged;
        ArchiveWatcher.OnNewArchiveArrived -= OnNewArchiveDetected;
    }

    private void OnSearchModeChanged()
    {
        FilterPackages();
        FilterEntries();
    }

    private void OnNewArchiveDetected(string filePath)
    {
        _ = InvokeAsync(async () =>
        {
            ShowToast($"New archive: {Path.GetFileName(filePath)}", "success");
            await RefreshPackages();
        });
    }

    // ── Tree helpers ──
    private List<(TreeNode Node, int Depth)> FlattenTree(List<TreeNode> nodes, int depth)
    {
        var result = new List<(TreeNode, int)>();
        foreach (var node in nodes.OrderBy(n => !n.IsFolder)
            .ThenByDescending(n => n.Name.Contains("NLK_REPORTS_EMV", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => n.Name.Equals("TXT", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(n => StartsWithAny(n.Name, "DATR", "DREJ", "DUNR"))
            .ThenBy(n => n.Name))
        {
            result.Add((node, depth));
            if (node.IsFolder && _expandedEntryFolders.Contains(node.FullPath))
                result.AddRange(FlattenTree(node.Children, depth + 1));
        }
        return result;
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private List<TreeNode> BuildTree(List<RarEntryInfo> entries)
    {
        var root = new TreeNode { Name = "/", FullPath = "", IsFolder = true };
        foreach (var entry in entries)
        {
            var parts = entry.Key.Replace('\\', '/').Split('/');
            var current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var child = current.Children.FirstOrDefault(c => c.IsFolder && c.Name == parts[i]);
                if (child == null)
                {
                    child = new TreeNode { Name = parts[i], FullPath = string.Join("/", parts.Take(i + 1)), IsFolder = true };
                    current.Children.Add(child);
                }
                current = child;
            }
            var fileName = parts.Last();
            if (!current.Children.Any(c => c.Name == fileName))
            {
                current.Children.Add(new TreeNode
                {
                    Name = fileName,
                    FullPath = entry.Key,
                    IsFolder = false,
                    Size = entry.Size,
                    IsTextFile = entry.IsTextFile,
                    LastModified = entry.LastModified
                });
            }
        }
        return root.Children.OrderBy(c => !c.IsFolder).ThenBy(c => c.Name).ToList();
    }

    // ── Package scanning ──
    private async Task RefreshPackages()
    {
        _isScanning = true;
        _selectedPackage = null;
        _expandedEntries = null;
        _openTabs.Clear();
        _activeTabIndex = 0;
        _activeTabIndexRight = -1;
        _errorMessage = null;
        _entryFilterText = "";
        StateHasChanged();

        try
        {
            var dir = Settings.Value.WatchDirectory;
            if (!Directory.Exists(dir))
            {
                _errorMessage = $"Watch directory not found: {dir}";
                _errorType = "error";
                Logger.LogError("Watch directory does not exist: {Path}", dir);
            }
            else
            {
                _packages = await ArchiveService.ScanDirectoryAsync(dir);
                if (_packages.Count == 0)
                {
                    _errorMessage = "Directory contains no .rar files";
                    _errorType = "warning";
                    Logger.LogWarning("No .rar files found in {Path}", dir);
                }
                else
                {
                    ShowToast($"Found {_packages.Count} archive{(_packages.Count != 1 ? "s" : "")}", "success");
                }
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Scan failed: {ex.Message}";
            _errorType = "error";
            Logger.LogError(ex, "Failed to scan watch directory");
        }
        finally
        {
            _isScanning = false;
            FilterPackages();
        }
    }

    private void FilterPackages()
    {
        if (string.IsNullOrWhiteSpace(_filterText))
            _filteredPackages = _packages.ToList();
        else
        {
            var re = Search.BuildPattern(_filterText);
            _filteredPackages = _packages
                .Where(p => re.IsMatch(p.FileName) || re.IsMatch(p.RelativeDirectory))
                .ToList();
        }
        StateHasChanged();
    }

    private void FilterEntries()
    {
        if (_expandedEntries == null) return;
        var query = _expandedEntries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_entryFilterText))
        {
            var re = Search.BuildPattern(_entryFilterText);
            var matches = _expandedEntries.Where(e => re.IsMatch(Path.GetFileName(e.Key))).ToList();
            _filteredEntries = matches;

            // Auto-expand parent folders of matching entries
            _expandedEntryFolders.Clear();
            foreach (var entry in matches)
            {
                var dir = Path.GetDirectoryName(entry.Key);
                while (dir != null)
                {
                    _expandedEntryFolders.Add(dir.Replace('\\', '/'));
                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }
            }
        }
        else
        {
            _filteredEntries = _expandedEntries.ToList();
            _expandedEntryFolders.Clear();
        }
        StateHasChanged();
    }

    // ── Package selection ──
    private async Task SelectPackage(RarPackageInfo pkg)
    {
        if (_selectedPackage?.FullPath == pkg.FullPath)
        {
            _selectedPackage = null;
            _expandedEntries = null;
            _filteredEntries = null;
            _openTabs.Clear();
            _activeTabIndex = 0;
            _activeTabIndexRight = -1;
            _errorMessage = null;
            _entryFilterText = "";
            _isLoadingEntries = false;
            StateHasChanged();
            return;
        }

        _selectedPackage = pkg;
        _expandedEntries = null;
        _filteredEntries = null;
        _openTabs.Clear();
        _activeTabIndex = 0;
        _activeTabIndexRight = -1;
        _errorMessage = null;
        _entryFilterText = "";
        _isLoadingEntries = false;
        _statusText = null;
        StateHasChanged();
    }

    private async Task LoadArchiveEntries()
    {
        if (_selectedPackage == null) return;
        _isLoadingEntries = true;
        _statusText = $"Opening {_selectedPackage.FileName}...";
        StateHasChanged();

        try
        {
            _expandedEntries = await ArchiveService.ListEntriesAsync(_selectedPackage.FullPath, Settings.Value.ArchivePassword);
            _filteredEntries = _expandedEntries;
            if (_expandedEntries == null)
            {
                _errorMessage = "Failed to open archive — incorrect password or corrupt file";
                _errorType = "error";
                _expandedEntries = new List<RarEntryInfo>();
                _filteredEntries = new List<RarEntryInfo>();
            }
            else
            {
                _statusText = $"{_expandedEntries.Count} entries";
                ShowToast($"Loaded {_expandedEntries.Count} entries", "info");
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error listing archive: {ex.Message}";
            _errorType = "error";
            Logger.LogError(ex, "Failed to list entries for {Path}", _selectedPackage.FullPath);
            _expandedEntries = new List<RarEntryInfo>();
            _filteredEntries = new List<RarEntryInfo>();
        }
        finally
        {
            _isLoadingEntries = false;
            StateHasChanged();
        }
    }

    // ── Entry selection ──
    private async Task SelectEntry(RarEntryInfo entry, bool rightPanel = false)
    {
        if (!entry.IsTextFile)
        {
            ShowToast("Cannot preview binary files", "warning");
            return;
        }

        var existing = _openTabs.FindIndex(t => t.Key == entry.Key);
        OpenTab tab;

        if (existing >= 0)
        {
            tab = _openTabs[existing];
            if (rightPanel && _splitView) { _activeTabIndexRight = existing; StateHasChanged(); return; }
            _activeTabIndex = existing;
            if (tab.Content != null) { StateHasChanged(); return; }
        }
        else
        {
            tab = new OpenTab
            {
                Key = entry.Key,
                FileName = Path.GetFileName(entry.Key),
                IsTextFile = entry.IsTextFile,
                IsLoading = true
            };
            _openTabs.Add(tab);
            existing = _openTabs.Count - 1;
            if (rightPanel && _splitView) _activeTabIndexRight = existing; else _activeTabIndex = existing;
            StateHasChanged();

            try
            {
                tab.Content = await ArchiveService.ReadTextEntryAsync(
                    _selectedPackage!.FullPath, entry.Key, Settings.Value.ArchivePassword);
                if (tab.Content == null)
                    ShowToast("Could not read file content", "warning");
                _statusText = null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to read entry '{Key}' from {Path}", entry.Key, _selectedPackage?.FullPath);
                ShowToast($"Read error: {ex.Message}", "error");
            }
            finally
            {
                tab.IsLoading = false;
                ApplyAutoZoom();
                StateHasChanged();
            }
        }
    }

    private async Task SelectEntryByNode(TreeNode node, bool rightPanel = false)
    {
        if (node.IsFolder) return;
        var entry = _filteredEntries?.FirstOrDefault(e => e.Key == node.FullPath);
        if (entry != null) await SelectEntry(entry, rightPanel);
    }

    // ── Tab management ──
    private void SwitchTab(int index)
    {
        if (index < 0 || index >= _openTabs.Count) return;
        _activeTabIndex = index;
        _renderTick++;
        StateHasChanged();
    }

    private void CloseTab(int index)
    {
        var wasLeft = _activeTabIndex == index;
        var wasRight = _activeTabIndexRight == index;

        _openTabs.RemoveAt(index);

        if (_openTabs.Count == 0)
        {
            _activeTabIndex = 0;
            _activeTabIndexRight = -1;
            _renderTick++;
            StateHasChanged();
            return;
        }

        if (wasRight)
            _activeTabIndexRight = -1;
        else if (_activeTabIndexRight > index)
            _activeTabIndexRight--;
        else if (_activeTabIndexRight >= _openTabs.Count)
            _activeTabIndexRight = _openTabs.Count - 1;

        if (_activeTabIndex >= _openTabs.Count)
            _activeTabIndex = _openTabs.Count - 1;

        _renderTick++;
        StateHasChanged();
    }

    private void CloseAllTabs()
    {
        _openTabs.Clear();
        _activeTabIndex = 0;
        _activeTabIndexRight = -1;
        _renderTick++;
        StateHasChanged();
    }

    private void SelectRightTab(int index)
    {
        if (index >= 0 && index < _openTabs.Count)
        {
            _activeTabIndexRight = index;
            StateHasChanged();
        }
    }

    private void SelectLeftTab(int index)
    {
        if (index >= 0 && index < _openTabs.Count)
        {
            _activeTabIndex = index;
            _renderTick++;
            StateHasChanged();
        }
    }

    private void ToggleSplitView()
    {
        _showTabMenu = false;
        _splitView = !_splitView;
        if (!_splitView) _activeTabIndexRight = -1;
        else if (_openTabs.Count > 1 && _activeTabIndexRight < 0)
            _activeTabIndexRight = _activeTabIndex == 0 ? 1 : 0;
        ApplyAutoZoom();
        StateHasChanged();
    }

    private void ToggleTabMenu()
    {
        _showTabMenu = !_showTabMenu;
        StateHasChanged();
    }

    // ── Panel toggles ──
    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ApplyAutoZoom();
        StateHasChanged();
    }

    private void ToggleContents()
    {
        _contentsCollapsed = !_contentsCollapsed;
        ApplyAutoZoom();
        StateHasChanged();
    }

    // ── Folder toggles ──
    private void ToggleFolder(string folderKey)
    {
        if (!_expandedFolders.Remove(folderKey)) _expandedFolders.Add(folderKey);
        StateHasChanged();
    }

    private void ToggleEntryFolder(string folderName)
    {
        if (!_expandedEntryFolders.Remove(folderName)) _expandedEntryFolders.Add(folderName);
        StateHasChanged();
    }

    // ── Zoom / wrap ──
    private void ApplyAutoZoom()
    {
        int panelsOpen = (_sidebarCollapsed ? 0 : 1) + (_contentsCollapsed ? 0 : 1);
        if (_splitView)
            _zoomLevel = panelsOpen switch { 0 => 0.5, 1 => 0.5, _ => 0.4 };
        else
            _zoomLevel = panelsOpen switch { 0 => 1.0, 1 => 0.7, _ => 0.5 };
    }

    private void ZoomIn() { _zoomLevel = Math.Min(3.0, _zoomLevel + 0.1); StateHasChanged(); }
    private void ZoomOut() { _zoomLevel = Math.Max(0.3, _zoomLevel - 0.1); StateHasChanged(); }
    private void ZoomReset() { _zoomLevel = 1.0; StateHasChanged(); }
    private void ToggleWrap() { _wrapText = !_wrapText; StateHasChanged(); }

    // ── Toast ──
    private void ShowToast(string message, string type = "info")
    {
        var id = ++_toastCounter;
        _toasts.Add(new ToastMessage { Id = id, Message = message, Type = type });
        StateHasChanged();
        _ = Task.Run(async () =>
        {
            await Task.Delay(4000);
            var toast = _toasts.FirstOrDefault(t => t.Id == id);
            if (toast != null) toast.IsLeaving = true;
            await InvokeAsync(StateHasChanged);
            await Task.Delay(300);
            _toasts.RemoveAll(t => t.Id == id);
            await InvokeAsync(StateHasChanged);
        });
    }

    // ── Helpers ──
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB"
    };

    // ── Nested types ──
    private class ToastMessage
    {
        public int Id { get; set; }
        public string Message { get; set; } = "";
        public string Type { get; set; } = "info";
        public bool IsLeaving { get; set; }
    }

    private class TreeNode
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public bool IsTextFile { get; set; }
        public DateTime LastModified { get; set; }
        public List<TreeNode> Children { get; set; } = new();
    }
}
