using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MdViewer.Core.Session;

namespace MdViewer.App.ViewModels;

/// <summary>
/// The settings surface (SPECIFICATION.md 5.13). This is the editable
/// projection of <see cref="AppSettings"/>: the shell loads a settings file
/// into it, and writes it back out when anything changes.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// Font choice meaning "leave the theme's font stack alone". Naming the
    /// default explicitly is better than an empty entry, and it keeps the
    /// ordered fallback stacks in Tokens.axaml reachable.
    /// </summary>
    public const string DefaultFont = "Default";

    // ============================================================ appearance

    [ObservableProperty]
    private string _theme = "System";

    // ================================================================ panels

    [ObservableProperty]
    private bool _showExplorerPanel = true;

    [ObservableProperty]
    private bool _showOutlinePanel = true;

    [ObservableProperty]
    private bool _showStatusBar = true;

    // ============================================================ typography

    [ObservableProperty]
    private string _bodyFontFamily = DefaultFont;

    [ObservableProperty]
    private double _bodyFontSize = 15;

    [ObservableProperty]
    private string _monospaceFontFamily = DefaultFont;

    [ObservableProperty]
    private double _monospaceFontSize = 13.5;

    [ObservableProperty]
    private double _lineHeight = 1.65;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveContentMaxWidth))]
    private double _contentMaxWidth = 900;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveContentMaxWidth))]
    private bool _useFullWidth;

    /// <summary>
    /// The measure width actually handed to the renderer. Full width means
    /// "no cap", which is expressed as infinity rather than a large number so
    /// the reading column tracks the window instead of a magic constant.
    /// </summary>
    public double EffectiveContentMaxWidth =>
        UseFullWidth ? double.PositiveInfinity : ContentMaxWidth;

    // =============================================================== parsing

    /// <summary>
    /// Ellipses, en/em dashes and curly quotes (SPECIFICATION.md 5.1).
    /// Changing this re-parses every open document.
    /// </summary>
    [ObservableProperty]
    private bool _smartPunctuation;

    /// <summary>
    /// Makes task list checkboxes clickable. Ticking one rewrites the marker in
    /// the file on disk, so this is opt-in (SPECIFICATION.md 5.2).
    /// </summary>
    [ObservableProperty]
    private bool _enableTaskListEditing;

    // ============================================================== raw view

    [ObservableProperty]
    private bool _showLineNumbersInRawView = true;

    [ObservableProperty]
    private bool _showFrontMatter;

    // ================================================================ editor

    /// <summary>
    /// The program launched by "Open in editor". Empty until the reader picks
    /// one: MdViewer never guesses at an editor on the reader's behalf.
    /// </summary>
    [ObservableProperty]
    private string _defaultEditorPath = string.Empty;

    // ======================================================== file watching

    private bool _enableFileWatching = true;

    public bool EnableFileWatching
    {
        get => _enableFileWatching;
        set => SetProperty(ref _enableFileWatching, value);
    }

    // =============================================================== startup

    [ObservableProperty]
    private bool _reopenPreviousFolder = true;

    /// <summary>
    /// Keeps folders without any markdown file out of the explorer tree
    /// (SPECIFICATION.md 5.9).
    /// </summary>
    [ObservableProperty]
    private bool _hideFoldersWithoutMarkdown;

    /// <summary>Theme choices offered by the shell (SPECIFICATION.md 5.11).</summary>
    public ObservableCollection<string> ThemeOptions { get; } = new() { "System", "Light", "Dark" };

    public ObservableCollection<string> BodyFontOptions { get; } = new()
    {
        DefaultFont,
        "Inter",
        "Segoe UI",
        "Calibri",
        "Georgia",
        "Verdana",
    };

    public ObservableCollection<string> MonospaceFontOptions { get; } = new()
    {
        DefaultFont,
        "Cascadia Mono",
        "Consolas",
        "JetBrains Mono",
        "Fira Code",
        "Courier New",
    };

    /// <summary>Fills this view model from a loaded settings file.</summary>
    public void LoadFrom(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Theme = settings.Theme;

        ShowExplorerPanel = settings.ShowExplorerPanel;
        ShowOutlinePanel = settings.ShowOutlinePanel;
        ShowStatusBar = settings.ShowStatusBar;

        BodyFontFamily = settings.BodyFontFamily;
        BodyFontSize = settings.BodyFontSize;
        MonospaceFontFamily = settings.MonospaceFontFamily;
        MonospaceFontSize = settings.MonospaceFontSize;
        LineHeight = settings.LineHeight;
        ContentMaxWidth = settings.ContentMaxWidth;
        UseFullWidth = settings.UseFullWidth;

        SmartPunctuation = settings.SmartPunctuation;
        EnableTaskListEditing = settings.EnableTaskListEditing;

        ShowLineNumbersInRawView = settings.ShowLineNumbersInRawView;
        ShowFrontMatter = settings.ShowFrontMatter;
        DefaultEditorPath = settings.DefaultEditorPath;
        EnableFileWatching = settings.EnableFileWatching;

        HideFoldersWithoutMarkdown = settings.HideFoldersWithoutMarkdown;

        ReopenPreviousFolder = settings.ReopenPreviousFolder;
    }

    /// <summary>
    /// Copies this view model back onto the settings file's contents. The same
    /// instance is reused rather than replaced, so unknown keys collected by
    /// its extension-data bag survive the round trip.
    /// </summary>
    public void WriteTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Theme = Theme;

        settings.ShowExplorerPanel = ShowExplorerPanel;
        settings.ShowOutlinePanel = ShowOutlinePanel;
        settings.ShowStatusBar = ShowStatusBar;

        settings.BodyFontFamily = BodyFontFamily;
        settings.BodyFontSize = BodyFontSize;
        settings.MonospaceFontFamily = MonospaceFontFamily;
        settings.MonospaceFontSize = MonospaceFontSize;
        settings.LineHeight = LineHeight;
        settings.ContentMaxWidth = ContentMaxWidth;
        settings.UseFullWidth = UseFullWidth;

        settings.SmartPunctuation = SmartPunctuation;
        settings.EnableTaskListEditing = EnableTaskListEditing;

        settings.ShowLineNumbersInRawView = ShowLineNumbersInRawView;
        settings.ShowFrontMatter = ShowFrontMatter;
        settings.DefaultEditorPath = DefaultEditorPath;
        settings.EnableFileWatching = EnableFileWatching;

        settings.HideFoldersWithoutMarkdown = HideFoldersWithoutMarkdown;

        settings.ReopenPreviousFolder = ReopenPreviousFolder;
    }
}
