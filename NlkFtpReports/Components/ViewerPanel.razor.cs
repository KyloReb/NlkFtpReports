using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using NlkFtpReports.Services;
using System.Text.RegularExpressions;

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

    // C# find state
    private List<int> _findMatchLines = new();
    private Regex? _findRegex;
    private int _renderVersion;
    private string? _highlightedContent;

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
            _renderVersion++;
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
            _findRegex = null;
            _findMatchLines.Clear();
            _renderVersion++;
            StateHasChanged();
            return;
        }

        _isSearching = true;
        _findCount = 0; _findIndex = 0;
        _findRegex = Search.BuildPattern(_findText);
        _findMatchLines.Clear();
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
                                TabIndex = idx, FileName = tab.FileName,
                                LineNumber = li + 1, LineText = ctx
                            });
                        }
                    }
                }
                _findCount = _searchAllResults.Count;
            }
            else
            {
                var lines = Tab!.Content!.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                {
                    if (_findRegex.IsMatch(lines[li]))
                    {
                        _findMatchLines.Add(li + 1);
                    }
                }
                _findCount = _findMatchLines.Count;
                _findIndex = _findCount > 0 ? 0 : 0;
                _renderVersion++;

                if (_findCount > 0)
                {
                    try { await JS.InvokeVoidAsync("fmcFind.goToLine", _findMatchLines[0]); } catch { }
                }
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

        await Task.Delay(100);
        RunFindOnCurrentTab();
        StateHasChanged();
        await Task.Delay(50);

        if (_findCount > 0)
        {
            var line = _findMatchLines.IndexOf(r.LineNumber);
            if (line >= 0) _findIndex = line;
            try { await JS.InvokeVoidAsync("fmcFind.goToLine", r.LineNumber); } catch { }
            StateHasChanged();
        }
    }

    private void RunFindOnCurrentTab()
    {
        _findRegex = !string.IsNullOrEmpty(_findText) && Tab?.Content != null ? Search.BuildPattern(_findText) : null;
        _findMatchLines.Clear();
        _findCount = 0;
        _findIndex = 0;
        if (_findRegex == null || Tab?.Content == null) return;

        var lines = Tab.Content.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            if (_findRegex.IsMatch(lines[li]))
                _findMatchLines.Add(li + 1);
        }
        _findCount = _findMatchLines.Count;
        _findIndex = _findCount > 0 ? 0 : 0;
        _renderVersion++;
    }

    private async Task FindNext()
    {
        if (_findCount == 0 || _findMatchLines.Count == 0) return;
        _findIndex = (_findIndex + 1) % _findCount;
        var line = _findMatchLines[_findIndex];
        try { await JS.InvokeVoidAsync("fmcFind.goToLine", line); } catch { }
        StateHasChanged();
    }

    private async Task FindPrev()
    {
        if (_findCount == 0 || _findMatchLines.Count == 0) return;
        _findIndex = (_findIndex - 1 + _findCount) % _findCount;
        var line = _findMatchLines[_findIndex];
        try { await JS.InvokeVoidAsync("fmcFind.goToLine", line); } catch { }
        StateHasChanged();
    }

    private async Task OnFindKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && e.ShiftKey) await FindPrev();
        else if (e.Key == "Enter") await FindNext();
        else if (e.Key == "F3" && e.ShiftKey) await FindPrev();
        else if (e.Key == "F3") await FindNext();
        else if (e.Key == "Escape")
        {
            _findText = ""; _findCount = 0; _findIndex = 0;
            _findRegex = null; _findMatchLines.Clear(); _renderVersion++;
            StateHasChanged();
        }
    }

    private MarkupString HighlightLine(string lineText, int lineNumber)
    {
        if (_findRegex == null || _findCount == 0)
            return (MarkupString)System.Net.WebUtility.HtmlEncode(lineText);

        var isMatch = _findMatchLines.Contains(lineNumber);
        if (!isMatch)
            return (MarkupString)System.Net.WebUtility.HtmlEncode(lineText);

        var escaped = System.Net.WebUtility.HtmlEncode(lineText);
        var matches = _findRegex.Matches(lineText);
        if (matches.Count == 0)
        {
            var cls = "fmc-line" + (_findMatchLines[_findIndex] == lineNumber ? " fmc-active" : "");
            return (MarkupString)$"<mark class=\"{cls}\">{escaped}</mark>";
        }

        var sb = new System.Text.StringBuilder();
        int lastIdx = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            sb.Append(System.Net.WebUtility.HtmlEncode(lineText.Substring(lastIdx, m.Index - lastIdx)));
            sb.Append("<mark class=\"fmc-match\">");
            sb.Append(System.Net.WebUtility.HtmlEncode(m.Value));
            sb.Append("</mark>");
            lastIdx = m.Index + m.Length;
        }
        sb.Append(System.Net.WebUtility.HtmlEncode(lineText.Substring(lastIdx)));

        var cls2 = "fmc-line" + (_findMatchLines[_findIndex] == lineNumber ? " fmc-active" : "");
        return (MarkupString)$"<mark class=\"{cls2}\">{sb}</mark>";
    }

    private void ToggleSearchAll()
    {
        _searchAllTabs = !_searchAllTabs;
        if (!string.IsNullOrEmpty(_findText)) _ = DoFind();
        StateHasChanged();
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
            var idx = IsRightPanel ? 1 : 0;
            var pos = await JS.InvokeAsync<int[]>("fmcFind.getScrollPos", idx);
            if (pos.Length >= 2)
            {
                _scrollLine = pos[0];
                _scrollPercent = pos[1];
                StateHasChanged();
            }
        }
        catch { }
    }

    public class FindResult { public int left { get; set; } public int right { get; set; }
        public FindResult() { }
        public FindResult(int l, int r) { left = l; right = r; }
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
