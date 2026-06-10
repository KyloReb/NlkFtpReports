using System.Text.RegularExpressions;

namespace NlkFtpReports.Services;

/// <summary>
/// Thread-safe singleton that owns search-mode state shared across
/// all three search bars (package filter, file filter, in-file find).
/// When any toggle changes, <see cref="OnChanged"/> fires so consumers
/// can re-run their active queries.
/// </summary>
public class SearchService
{
    public bool CaseSensitive { get; private set; }
    public bool WholeWord { get; private set; }
    public bool RegexMode { get; private set; }

    /// <summary>Fires when any of the three toggles change.</summary>
    public event Action? OnChanged;

    public void ToggleCase()
    {
        CaseSensitive = !CaseSensitive;
        OnChanged?.Invoke();
    }

    public void ToggleWord()
    {
        WholeWord = !WholeWord;
        OnChanged?.Invoke();
    }

    public void ToggleRegex()
    {
        RegexMode = !RegexMode;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Builds a flags string passed to the JS highlight function.
    /// 'i' = case-insensitive, 'w' = whole word, 'r' = regex mode.
    /// Absence of a flag means the feature is OFF.
    /// </summary>
    public string GetJsFlags() =>
        (CaseSensitive ? "" : "i") +
        (WholeWord ? "w" : "") +
        (RegexMode ? "r" : "");

    /// <summary>
    /// Builds a CLR regex from <paramref name="text"/> respecting the
    /// current search-mode settings.
    /// </summary>
    public Regex BuildPattern(string text)
    {
        var pattern = text;
        if (!RegexMode) pattern = Regex.Escape(pattern);
        if (WholeWord) pattern = @"\b" + pattern + @"\b";
        var opts = CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(pattern, opts, TimeSpan.FromSeconds(2));
    }
}
