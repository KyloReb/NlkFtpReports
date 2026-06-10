using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NlkFtpReports.Services;

namespace NlkFtpReports.Components;

/// <summary>
/// Reusable preview panel displayed inside the right-hand viewer.
/// Renders a single file's content with find, zoom, and wrap controls.
/// Used once in single-view mode and twice in split-view mode.
/// </summary>
public partial class ViewerPanel : IDisposable
{
    // ── Dependencies ──
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private SearchService Search { get; set; } = null!;

    // ── Parameters ──
    [Parameter] public OpenTab? Tab { get; set; }
    [Parameter] public string? PackageName { get; set; }
    [Parameter] public double ZoomLevel { get; set; }
    [Parameter] public bool WrapText { get; set; }
    [Parameter] public bool IsRightPanel { get; set; }
    [Parameter] public int TotalMatchCount { get; set; }
    [Parameter] public int RightMatchCount { get; set; }
    [Parameter] public EventCallback OnZoomIn { get; set; }
    [Parameter] public EventCallback OnZoomOut { get; set; }
    [Parameter] public EventCallback OnZoomReset { get; set; }
    [Parameter] public EventCallback OnToggleWrap { get; set; }

    // ── Find state ──
    private string _findText = "";
    private int _findCount;
    private int _findIndex;
    private bool _isSearching;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _regexMode;

    protected override void OnInitialized()
    {
        Search.OnChanged += OnSearchModeChanged;
    }

    public void Dispose()
    {
        Search.OnChanged -= OnSearchModeChanged;
    }

    private void OnSearchModeChanged()
    {
        _caseSensitive = Search.CaseSensitive;
        _wholeWord = Search.WholeWord;
        _regexMode = Search.RegexMode;
        if (!string.IsNullOrEmpty(_findText))
            _ = DoFind();
        StateHasChanged();
    }

    private async Task DoFind()
    {
        if (string.IsNullOrEmpty(_findText) || Tab?.Content == null)
        {
            _findCount = 0;
            _findIndex = 0;
            _isSearching = false;
            await HighlightFind();
            return;
        }

        _isSearching = true;
        _findCount = 0;
        _findIndex = 0;
        StateHasChanged();

        try
        {
            var flags = Search.GetJsFlags();
            var result = await JS.InvokeAsync<FindResult>("fmcFind.highlight", _findText, flags);
            _findCount = result.left + result.right;
            _findIndex = _findCount > 0 ? 0 : 0;
            if (_findCount > 0)
                await JS.InvokeVoidAsync("fmcFind.goTo", 0);
        }
        catch
        {
            _findCount = 0;
            _findIndex = 0;
        }
        _isSearching = false;
        StateHasChanged();
    }

    private async Task FindNext()
    {
        if (_findCount == 0) return;
        _findIndex = (_findIndex + 1) % _findCount;
        try { await JS.InvokeVoidAsync("fmcFind.goTo", _findIndex); } catch { }
        StateHasChanged();
    }

    private async Task FindPrev()
    {
        if (_findCount == 0) return;
        _findIndex = (_findIndex - 1 + _findCount) % _findCount;
        try { await JS.InvokeVoidAsync("fmcFind.goTo", _findIndex); } catch { }
        StateHasChanged();
    }

    private async Task HighlightFind()
    {
        try { await JS.InvokeVoidAsync("fmcFind.clear"); } catch { }
    }

    // ── Data class for JS return ──
    public class FindResult
    {
        public int left { get; set; }
        public int right { get; set; }
    }
}

/// <summary>
/// Represents a single open file tab inside the viewer.
/// </summary>
public class OpenTab
{
    public string Key { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsTextFile { get; set; }
    public bool IsLoading { get; set; }
    public string? Content { get; set; }
}
