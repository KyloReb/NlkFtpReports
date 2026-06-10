using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using NlkFtpReports.Services;

namespace NlkFtpReports.Components;

public partial class ViewerPanel : IDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private SearchService Search { get; set; } = null!;

    [Parameter] public OpenTab? Tab { get; set; }
    [Parameter] public double ZoomLevel { get; set; }
    [Parameter] public bool WrapText { get; set; }
    [Parameter] public bool IsRightPanel { get; set; }
    [Parameter] public int RightMatchCount { get; set; }
    [Parameter] public List<OpenTab>? OpenTabs { get; set; }
    [Parameter] public EventCallback<int> OnSelectRightTab { get; set; }
    [Parameter] public EventCallback<int> OnSelectLeftTab { get; set; }
    [Parameter] public EventCallback OnZoomIn { get; set; }
    [Parameter] public EventCallback OnZoomOut { get; set; }
    [Parameter] public EventCallback OnZoomReset { get; set; }
    [Parameter] public EventCallback OnToggleWrap { get; set; }

    private string _findText = "";
    private int _findCount;
    private int _findIndex;
    private bool _isSearching;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _regexMode;
    private string? _previousTabKey;
    private int _scrollLine;
    private int _totalLines;
    private int _scrollPercent;
    private bool _searchAllTabs;
    private List<SearchResult> _searchAllResults = new();

    protected override void OnInitialized()
    {
        Search.OnChanged += OnSearchModeChanged;
    }

    protected override void OnParametersSet()
    {
        var newKey = Tab?.Key;
        if (_previousTabKey != null && _previousTabKey != newKey)
        {
            _scrollLine = 0;
            _totalLines = Tab?.Content?.Count(c => c == '\n') + 1 ?? 0;
            _scrollPercent = 0;
            if (!string.IsNullOrEmpty(_findText))
                _ = DoFind();
        }
        _previousTabKey = newKey;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Tab?.Content != null)
        {
            _totalLines = Tab.Content.Count(c => c == '\n') + 1;
            StateHasChanged();
        }
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
        if (!string.IsNullOrEmpty(_findText)) _ = DoFind();
        StateHasChanged();
    }

    private async Task DoFind()
    {
        _searchAllResults.Clear();

        if (string.IsNullOrEmpty(_findText) || (_searchAllTabs ? (OpenTabs == null || OpenTabs.Count == 0) : (Tab?.Content == null)))
        {
            _findCount = 0; _findIndex = 0; _isSearching = false;
            await HighlightFind();
            return;
        }

        _isSearching = true;
        _findCount = 0; _findIndex = 0;
        StateHasChanged();

        try
        {
            if (_searchAllTabs)
            {
                var re = Search.BuildPattern(_findText);
                foreach (var (tab, idx) in OpenTabs!.Select((t, i) => (t, i)))
                {
                    if (tab.Content == null) continue;
                    var lines = tab.Content.Split('\n');
                    for (int li = 0; li < lines.Length; li++)
                    {
                        if (re.IsMatch(lines[li]))
                        {
                            var ctx = lines[li].Trim();
                            if (ctx.Length > 80) ctx = ctx[..77] + "...";
                            _searchAllResults.Add(new SearchResult
                            {
                                TabIndex = idx,
                                FileName = tab.FileName,
                                LineNumber = li + 1,
                                LineText = ctx
                            });
                        }
                    }
                }
                _findCount = _searchAllResults.Count;
                _findIndex = 0;
            }
            else
            {
                var result = await JS.InvokeAsync<FindResult>("fmcFind.highlight", _findText, Search.GetJsFlags());
                _findCount = result.left + result.right;
                _findIndex = _findCount > 0 ? 0 : 0;
                if (_findCount > 0) await JS.InvokeVoidAsync("fmcFind.goTo", 0);
            }
        }
        catch { _findCount = 0; _findIndex = 0; }
        _isSearching = false;
        StateHasChanged();
    }

    private async Task GoToSearchResult(SearchResult r)
    {
        if (r.TabIndex < 0) return;

        if (IsRightPanel)
            await OnSelectRightTab.InvokeAsync(r.TabIndex);
        else
            await OnSelectLeftTab.InvokeAsync(r.TabIndex);

        // Wait for the tab parameter to actually change (poll up to 2s)
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            if (Tab?.FileName == r.FileName) break;
        }

        // Run JS DOM highlight on the new tab
        var savedSearchAll = _searchAllTabs;
        _searchAllTabs = false;
        await DoFind();
        _searchAllTabs = savedSearchAll;

        // Scroll to the exact line
        if (_findCount > 0)
        {
            try { await JS.InvokeVoidAsync("fmcFind.goToLine", r.LineNumber); }
            catch { }
        }
    }

    private void ToggleSearchAll()
    {
        _searchAllTabs = !_searchAllTabs;
        if (!string.IsNullOrEmpty(_findText)) _ = DoFind();
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

    private async Task OnFindKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && e.ShiftKey) await FindPrev();
        else if (e.Key == "Enter") await FindNext();
        else if (e.Key == "F3" && e.ShiftKey) await FindPrev();
        else if (e.Key == "F3") await FindNext();
        else if (e.Key == "Escape")
        {
            _findText = "";
            _findCount = 0; _findIndex = 0;
            await HighlightFind();
            StateHasChanged();
        }
    }

    private async Task OnRightTabSelected(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var index) && index >= 0)
            await OnSelectRightTab.InvokeAsync(index);
    }

    private async Task OnLeftTabSelected(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var index) && index >= 0)
            await OnSelectLeftTab.InvokeAsync(index);
    }

    private async Task OnScroll(EventArgs e)
    {
        try
        {
            var pos = await JS.InvokeAsync<int[]>("fmcFind.getScrollPos");
            if (pos.Length >= 2)
            {
                _scrollLine = pos[0];
                _scrollPercent = pos[1];
                StateHasChanged();
            }
        }
        catch { }
    }

    public class FindResult
    {
        public int left { get; set; }
        public int right { get; set; }
    }

    public class SearchResult
    {
        public int TabIndex { get; set; }
        public string FileName { get; set; } = "";
        public int LineNumber { get; set; }
        public string LineText { get; set; } = "";
    }
}

public class OpenTab
{
    public string Key { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool IsTextFile { get; set; }
    public bool IsLoading { get; set; }
    public string? Content { get; set; }
}
