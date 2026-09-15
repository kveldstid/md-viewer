using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdViewer.App.Services;
using MdViewer.Core.Documents;
using MdViewer.Core.Markdown;
using MdViewer.Core.Outline;
using MdViewer.Core.Session;
using MdViewer.Core.Workspace;

namespace MdViewer.App.ViewModels;

/// <summary>
/// The shell (SPECIFICATION.md 4). From M2 this drives real documents: files
/// are read and parsed off the UI thread and rendered by MdViewer.Rendering.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// How many recent documents the start page shows before the user asks for
    /// the rest. Five is the glance; the history behind it is longer.
    /// </summary>
    private const int CollapsedRecentCount = 5;
    private static readonly string HelpPagePath = Path.Combine(AppContext.BaseDirectory, "Help", "help-page.md");
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private DocumentLoader _loader = new();
    private readonly WorkspaceScanner _scanner = new();
    private readonly RecentDocumentList _recent = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly SessionStore _sessionStore = new();
    private readonly Dictionary<string, FileSystemWatcher> _fileWatchers = new(PathComparer);
    private readonly Dictionary<string, CancellationTokenSource> _pendingWatchedReloads = new(PathComparer);

    /// <summary>
    /// When each file was last written by us, so the watcher can tell our own
    /// task list edits apart from an external change.
    /// </summary>
    private readonly Dictionary<string, DateTime> _selfWrites = new(PathComparer);

    private const int WatchedReloadDebounceMs = 350;

    /// <summary>
    /// How long after our own write a watcher event is still assumed to be the
    /// echo of it. Generous, because the notification can lag the write.
    /// </summary>
    private static readonly TimeSpan SelfWriteGrace = TimeSpan.FromSeconds(2);
    private const string InternalSourceOffsetLinkPrefix = "mdv-source-offset:";

    private AppSettings _settings = new();
    private SessionState _session = new();

    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _themePopupCancellation;
    private CancellationTokenSource? _saveDebounce;

    /// <summary>
    /// True while the session is being rebuilt. Restoration touches almost
    /// every observable the save logic listens to, and saving the session we
    /// are halfway through restoring would be, at best, pointless.
    /// </summary>
    private bool _isRestoring;

    /// <summary>
    /// True while the outline selection is being driven by the document's own
    /// scroll position. Without it the selection would scroll the document
    /// back to the heading it just followed, and the two would fight.
    /// </summary>
    private bool _isFollowingScroll;

    [ObservableProperty]
    private DocumentTabViewModel? _selectedTab;

    /// <summary>
    /// The start page is more than an empty state: the Home affordance in the
    /// tab strip brings it back at any time (SPECIFICATION.md 5.14), so it can
    /// be shown while tabs are open. It then overlays the document pane and is
    /// dismissed by opening something or by returning to the current tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartPageVisible))]
    private bool _isStartPageRequested;

    /// <summary>Whether the start page lists the whole history or just the newest few.</summary>
    [ObservableProperty]
    private bool _isRecentExpanded;

    [ObservableProperty]
    private OutlineItemViewModel? _selectedOutlineItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarPresent))]
    private bool _isSidebarVisible = true;

    /// <summary>
    /// Whether the sidebar column should occupy space at all. The toggle alone
    /// is not enough: with both explorer and outline turned off the sidebar has
    /// nothing left to show, and an empty panel is just a gap the user cannot
    /// close from the toggle. Hiding the whole column keeps the two settings and
    /// the toggle expressing one idea.
    /// </summary>
    public bool IsSidebarPresent =>
        IsSidebarVisible && (Settings.ShowExplorerPanel || Settings.ShowOutlinePanel);

    /// <summary>
    /// Whether the sidebar toggle is worth offering. With both panels switched
    /// off the toggle has nothing to reveal, so showing it would be a control
    /// that visibly does nothing when clicked.
    /// </summary>
    public bool CanToggleSidebar =>
        Settings.ShowExplorerPanel || Settings.ShowOutlinePanel;

    [ObservableProperty]
    private bool _isFindBarVisible;

    [ObservableProperty]
    private bool _isQuickOpenVisible;

    [ObservableProperty]
    private bool _isFocusMode;

    [ObservableProperty]
    private bool _isSettingsVisible;

    [ObservableProperty]
    private bool _isAboutVisible;

    [ObservableProperty]
    private string _themeName = "System";

    [ObservableProperty]
    private string _themePopupText = string.Empty;

    [ObservableProperty]
    private bool _isThemePopupVisible;

    [ObservableProperty]
    private string _workspaceName = "No folder";

    [ObservableProperty]
    private string? _statusMessage;

    public MainWindowViewModel()
    {
        Find = new FindViewModel();
        Find.MatchSelected += OnFindMatchSelected;
        Settings = new SettingsViewModel();
        QuickOpen = new QuickOpenViewModel(Array.Empty<QuickOpenResultViewModel>(), Array.Empty<QuickOpenResultViewModel>());

        Tabs = new ObservableCollection<DocumentTabViewModel>();
        Tabs.CollectionChanged += OnTabsChanged;

        WorkspaceRoots = new ObservableCollection<FileTreeItemViewModel>();
        RecentDocuments = new ObservableCollection<RecentDocumentViewModel>();
        VisibleRecentDocuments = new ObservableCollection<RecentDocumentViewModel>();
    }

    /// <summary>Set by the view; the view models never touch Avalonia directly.</summary>
    public IStorageService? Storage { get; set; }

    /// <summary>
    /// Raised when the document pane should scroll to a source offset — from an
    /// outline click, an anchor link, or a restored reading position.
    /// </summary>
    public event Action<int>? ScrollToOffsetRequested;

    public ObservableCollection<DocumentTabViewModel> Tabs { get; }

    public ObservableCollection<FileTreeItemViewModel> WorkspaceRoots { get; }

    public ObservableCollection<RecentDocumentViewModel> RecentDocuments { get; }

    /// <summary>
    /// What the start page actually lists: the newest few, or the whole history
    /// once the user asks for it. The full list is kept, so expanding costs no
    /// file system work.
    /// </summary>
    public ObservableCollection<RecentDocumentViewModel> VisibleRecentDocuments { get; }

    public FindViewModel Find { get; }

    /// <summary>Settings surface (SPECIFICATION.md 5.13).</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Product name shown on the about page, read from the assembly.</summary>
    public string ProductName =>
        AboutAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "KT Markdown Viewer";

    /// <summary>Version string shown on the about page.</summary>
    public string VersionDisplay
    {
        get
        {
            var version = AboutAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? AboutAssembly.GetName().Version?.ToString()
                ?? "unknown";

            // Informational versions can carry a "+<commit>" suffix; drop it.
            var plus = version.IndexOf('+');
            return "Version " + (plus >= 0 ? version[..plus] : version);
        }
    }

    public string CompanyName =>
        AboutAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "Kveldstid AS";

    public string CopyrightText =>
        AboutAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © Kveldstid AS";

    public string WebsiteUrl => "https://kveldstid.com";

    /// <summary>The MIT license text as published with the source on GitHub.</summary>
    public string LicenseUrl => "https://github.com/hansos/MdViewer/blob/master/LICENSE";

    /// <summary>The public source repository.</summary>
    public string RepositoryUrl => "https://github.com/hansos/MdViewer";

    /// <summary>The third-party component notices as published with the source on GitHub.</summary>
    public string ThirdPartyNoticesUrl => "https://github.com/hansos/MdViewer/blob/master/THIRD-PARTY-NOTICES.md";

    private static Assembly AboutAssembly => typeof(MainWindowViewModel).Assembly;

    /// <summary>
    /// Supplied by the view: window bounds, window state and the sidebar
    /// splitter width are things only the window knows, so it fills them in
    /// before a save and puts them back on restore (SPECIFICATION.md 5.13).
    /// </summary>
    public Action<SessionState>? CaptureViewState { get; set; }

    public Action<SessionState>? ApplyViewState { get; set; }

    public QuickOpenViewModel QuickOpen { get; }

    public string? WorkspaceRoot { get; private set; }

    public bool HasWorkspace => !string.IsNullOrEmpty(WorkspaceRoot);

    public bool HasNoTabs => Tabs.Count == 0;

    public bool HasRecentDocuments => RecentDocuments.Count > 0;

    /// <summary>
    /// Shown when nothing is open, and whenever the user asks for it by Home.
    /// </summary>
    public bool IsStartPageVisible => HasNoTabs || IsStartPageRequested;

    /// <summary>
    /// There is only somewhere to go back to while a document is open.
    /// </summary>
    public bool CanLeaveStartPage => !HasNoTabs;

    public bool HasMoreRecentDocuments => RecentDocuments.Count > CollapsedRecentCount;

    public string RecentToggleText =>
        IsRecentExpanded ? "Show fewer" : $"Show all {RecentDocuments.Count}";

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFileWatchers();
        PrunePendingWatchedReloads();
        OnPropertyChanged(nameof(HasNoTabs));
        OnPropertyChanged(nameof(IsStartPageVisible));
        OnPropertyChanged(nameof(CanLeaveStartPage));
        RequestSave();
    }

    // ============================================================ start page

    [RelayCommand]
    private void ShowStartPage() => IsStartPageRequested = true;

    [RelayCommand]
    private void HideStartPage() => IsStartPageRequested = false;

    // ============================================================== startup

    /// <summary>
    /// Loads settings, then either opens the command-line arguments or restores
    /// the previous session (SPECIFICATION.md 4.3, 5.13). Files given on the
    /// command line win: an explicit request is not a session to restore.
    /// </summary>
    public async Task InitializeAsync(IReadOnlyList<string> args)
    {
        _isRestoring = true;

        try
        {
            _settings = _settingsStore.Load();
            Settings.LoadFrom(_settings);
            Settings.PropertyChanged += OnSettingsPropertyChanged;
            ApplySmartPunctuation();
            ApplySettings();

            var paths = args
                .Where(a => !a.StartsWith('-'))
                .Select(a => Path.GetFullPath(a))
                .Where(File.Exists)
                .ToList();

            if (paths.Count > 0)
            {
                var folder = Path.GetDirectoryName(paths[0]);
                if (!string.IsNullOrEmpty(folder)) SetWorkspace(folder);

                foreach (var path in paths)
                {
                    await OpenDocumentAsync(path).ConfigureAwait(true);
                }

                return;
            }

            // --no-restore skips restoration for one launch (SPECIFICATION.md 5.13).
            if (args.Any(a => string.Equals(a, "--no-restore", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            await RestoreSessionAsync().ConfigureAwait(true);
        }
        finally
        {
            _isRestoring = false;
        }
    }

    /// <summary>
    /// Rebuilds the previous session. Nothing here is allowed to be fatal: a
    /// file that has since been deleted is skipped silently rather than
    /// blocking startup (SPECIFICATION.md 5.13).
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        _session = _sessionStore.Load();

        RestoreRecent();

        if (Settings.ReopenPreviousFolder &&
            !string.IsNullOrEmpty(_settings.LastFolder) &&
            Directory.Exists(_settings.LastFolder))
        {
            SetWorkspace(_settings.LastFolder);
        }

        IsSidebarVisible = _session.IsSidebarVisible;
        ApplyViewState?.Invoke(_session);

        // Tabs are created first and the active one is selected before any file
        // is read, so the loading popup (bound to SelectedTab.IsLoading) is
        // visible during the startup load just like it is for a later open.
        foreach (var saved in _session.Tabs)
        {
            if (string.IsNullOrEmpty(saved.Path) || !File.Exists(saved.Path)) continue;

            var tab = CreateTab(Path.GetFullPath(saved.Path));
            tab.ViewMode = saved.ViewMode;
            tab.Zoom = saved.Zoom <= 0 ? 1.0 : saved.Zoom;
            tab.ScrollOffset = saved.ScrollOffset;
        }

        if (Tabs.Count == 0) return;

        var index = _session.ActiveTabIndex;
        SelectedTab = index >= 0 && index < Tabs.Count ? Tabs[index] : Tabs[0];

        // The selected tab loads first: it is the one the reader is waiting for.
        var ordered = Tabs.OrderByDescending(t => ReferenceEquals(t, SelectedTab)).ToList();
        foreach (var tab in ordered)
        {
            await LoadIntoAsync(tab, tab.FullPath).ConfigureAwait(true);
        }

        if (SelectedTab.ScrollOffset > 0)
        {
            RestoreScrollOffset?.Invoke(SelectedTab.ScrollOffset);
        }
    }

    private void RestoreRecent()
    {
        foreach (var entry in _session.Recent.AsEnumerable().Reverse())
        {
            if (string.IsNullOrEmpty(entry.Path)) continue;

            _recent.Touch(new RecentDocument(
                entry.Path,
                string.IsNullOrEmpty(entry.Title) ? Path.GetFileName(entry.Path) : entry.Title,
                entry.LastOpenedUtc,
                entry.ScrollOffset));
        }

        RefreshRecent();
    }

    // ========================================================== persistence

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.ShowExplorerPanel)
            or nameof(SettingsViewModel.ShowOutlinePanel))
        {
            OnPropertyChanged(nameof(IsSidebarPresent));
            OnPropertyChanged(nameof(CanToggleSidebar));
            ToggleSidebarCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(SettingsViewModel.EnableFileWatching))
        {
            RebuildFileWatchers();
        }

        if (e.PropertyName == nameof(SettingsViewModel.SmartPunctuation))
        {
            ApplySmartPunctuation();
        }

        if (e.PropertyName == nameof(SettingsViewModel.HideFoldersWithoutMarkdown))
        {
            RefreshWorkspaceTree();
        }

        ApplySettings();
        RequestSave();
    }

    /// <summary>
    /// Smart punctuation is a parse-time option, so the pipeline is swapped and
    /// every open document is re-parsed through it (SPECIFICATION.md 5.1).
    /// </summary>
    private void ApplySmartPunctuation()
    {
        _loader = new DocumentLoader(MarkdownPipelineFactory.For(Settings.SmartPunctuation));

        if (_isRestoring) return;

        _ = ReloadAllTabsAsync();
    }

    private async Task ReloadAllTabsAsync()
    {
        // The selected tab first: it is the one the reader is looking at.
        var ordered = Tabs
            .OrderByDescending(t => ReferenceEquals(t, SelectedTab))
            .ToList();

        foreach (var tab in ordered)
        {
            if (string.IsNullOrEmpty(tab.FullPath)) continue;
            await LoadIntoAsync(tab, tab.FullPath).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Pushes the settings that have a visible effect into the running app:
    /// the theme variant and the typography tokens (SPECIFICATION.md 5.11).
    /// Panel visibility is bound directly by the views.
    /// </summary>
    private void ApplySettings()
    {
        ThemeName = Settings.Theme;

        _scanner.HideFoldersWithoutMarkdown = Settings.HideFoldersWithoutMarkdown;

        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = Settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        AppTypography.Apply(app, Settings);
    }

    /// <summary>
    /// Coalesces the storm of changes a single user action produces into one
    /// write, on the 2-second debounce the spec asks for (SPECIFICATION.md 5.13).
    /// </summary>
    private void RequestSave()
    {
        if (_isRestoring) return;

        _saveDebounce?.Cancel();

        var cancellation = new CancellationTokenSource();
        _saveDebounce = cancellation;

        _ = DebouncedSaveAsync(cancellation);
    }

    private async Task DebouncedSaveAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_saveDebounce == cancellation) SaveNow();
    }

    /// <summary>
    /// Writes settings and session immediately. Called on clean exit, so the
    /// last two seconds of a session are never lost.
    /// </summary>
    public void SaveNow()
    {
        _saveDebounce?.Cancel();

        Settings.WriteTo(_settings);
        _settings.LastFolder = WorkspaceRoot;
        _settingsStore.Save(_settings);

        CaptureSession();
        _sessionStore.Save(_session);
    }

    private void CaptureSession()
    {
        // The live view knows the reading position of the tab on screen; the
        // others still hold the offset captured when they were last left.
        if (SelectedTab is not null && CaptureScrollOffset is not null)
        {
            SelectedTab.ScrollOffset = CaptureScrollOffset();
        }

        _session.Tabs = Tabs
            .Select(t => new SessionTab
            {
                Path = t.FullPath,
                ScrollOffset = t.ScrollOffset,
                ViewMode = t.ViewMode,
                Zoom = t.Zoom,
            })
            .ToList();

        _session.ActiveTabIndex = SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab);
        _session.IsSidebarVisible = IsSidebarVisible;

        _session.Recent = _recent.Items
            .Select(r => new SessionRecentDocument
            {
                Path = r.Path,
                Title = r.Title,
                LastOpenedUtc = r.LastOpenedUtc,
                ScrollOffset = r.ScrollOffset,
            })
            .ToList();

        CaptureViewState?.Invoke(_session);
    }

    // ============================================================ documents

    /// <summary>
    /// Opens a document, or focuses the tab that already has it. Reading and
    /// parsing happen on a thread-pool thread; only the tab update runs here.
    /// </summary>
    public async Task OpenDocumentAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        IsStartPageRequested = false;

        var full = Path.GetFullPath(path);

        var existing = Tabs.FirstOrDefault(t => PathsEqual(t.FullPath, full));
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = CreateTab(full);
        SelectedTab = tab;

        await LoadIntoAsync(tab, full).ConfigureAwait(true);
    }

    /// <summary>
    /// Every document opens in its own tab (SPECIFICATION.md 5.9); a new tab is
    /// appended to the end of the strip.
    /// </summary>
    private DocumentTabViewModel CreateTab(string full)
    {
        var tab = new DocumentTabViewModel(Path.GetFileName(full), full);
        Tabs.Add(tab);
        return tab;
    }

    private async Task LoadIntoAsync(DocumentTabViewModel tab, string path)
    {
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        tab.IsLoading = true;
        StatusMessage = null;

        try
        {
            var result = await Task.Run(() => _loader.LoadAsync(path, token), token).ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            if (result.IsSuccess && result.Document is not null)
            {
                tab.SetDocument(result.Document);
                tab.Title = Path.GetFileName(path);
                tab.IsModifiedOnDisk = false;
                RecordRecent(result.Document);
            }
            else
            {
                tab.SetError(result.ErrorMessage ?? "The document could not be opened.");
            }

            if (ReferenceEquals(tab, SelectedTab))
            {
                SyncFindSearchText();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load; the newer one owns the tab now.
        }
        catch (Exception ex)
        {
            tab.SetError($"Unexpected error: {ex.Message}");
        }
        finally
        {
            tab.IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (SelectedTab is null || string.IsNullOrEmpty(SelectedTab.FullPath)) return;
        await LoadIntoAsync(SelectedTab, SelectedTab.FullPath).ConfigureAwait(true);
    }

    // ============================================================ task lists

    /// <summary>
    /// Ticks or unticks a task list item and writes the change back to the file
    /// (SPECIFICATION.md 5.2). Only the single marker character is replaced, so
    /// everything else about the file — formatting, line endings, encoding —
    /// survives untouched.
    ///
    /// Only reachable when the reader has opted in; MdViewer never writes to a
    /// document otherwise.
    /// </summary>
    public async Task ToggleTaskAsync(int sourceOffset, bool isChecked)
    {
        if (!Settings.EnableTaskListEditing) return;

        var tab = SelectedTab;
        var document = tab?.Document;
        if (tab is null || document is null || string.IsNullOrEmpty(tab.FullPath)) return;

        if (!TryPatchTaskMarker(document.SourceText, sourceOffset, isChecked, out var updated))
        {
            StatusMessage = "The task could not be updated: the document no longer matches.";
            return;
        }

        var offset = CaptureScrollOffset?.Invoke() ?? tab.ScrollOffset;

        try
        {
            // The write is about to fire our own watcher; claim it first so the
            // tab is not flagged as modified on disk by our own edit.
            NoteSelfWrite(tab.FullPath);

            await DocumentWriter.WriteAsync(tab.FullPath, updated, document.Encoding).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The task could not be saved: {ex.Message}";

            // Re-render from the unchanged source so the checkbox snaps back.
            tab.SetDocument(document);
            return;
        }

        tab.SetDocument(_loader.Parse(tab.FullPath, updated, document.Encoding));
        tab.IsModifiedOnDisk = false;
        tab.ScrollOffset = offset;
        RestoreScrollOffset?.Invoke(offset);
    }

    /// <summary>
    /// Replaces the state character of the <c>[ ]</c> marker at
    /// <paramref name="sourceOffset"/>. Returns false when the text there is not
    /// a task marker, which means the document moved under the rendered view and
    /// writing would corrupt it.
    /// </summary>
    private static bool TryPatchTaskMarker(string source, int sourceOffset, bool isChecked, out string updated)
    {
        updated = source;

        if (sourceOffset < 0 || sourceOffset + 2 >= source.Length) return false;
        if (source[sourceOffset] != '[' || source[sourceOffset + 2] != ']') return false;

        var state = source[sourceOffset + 1];
        if (state is not (' ' or 'x' or 'X')) return false;

        var replacement = isChecked ? 'x' : ' ';
        if (state == replacement) return true;

        updated = string.Create(source.Length, (source, sourceOffset, replacement), static (span, state) =>
        {
            state.source.AsSpan().CopyTo(span);
            span[state.sourceOffset + 1] = state.replacement;
        });

        return true;
    }

    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        if (Storage is null) return;

        var paths = await Storage.PickMarkdownFilesAsync().ConfigureAwait(true);
        foreach (var path in paths)
        {
            await OpenDocumentAsync(path).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (Storage is null) return;

        var folder = await Storage.PickFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(folder)) SetWorkspace(folder);
    }

    // ============================================================ workspace

    public void SetWorkspace(string folder)
    {
        if (!Directory.Exists(folder)) return;

        WorkspaceRoot = Path.GetFullPath(folder);
        WorkspaceName = new DirectoryInfo(WorkspaceRoot).Name;

        var root = _scanner.CreateRoot(WorkspaceRoot);
        _scanner.Load(root);

        WorkspaceRoots.Clear();
        WorkspaceRoots.Add(new FileTreeItemViewModel(root, _scanner) { IsExpanded = true });

        OnPropertyChanged(nameof(HasWorkspace));
        RefreshQuickOpenSources();
        RequestSave();
    }

    /// <summary>
    /// Re-reads the workspace root after an explorer filter change. Expansion
    /// state is not preserved: the visible set of folders has changed, so the
    /// tree starts from the root again.
    /// </summary>
    private void RefreshWorkspaceTree()
    {
        _scanner.HideFoldersWithoutMarkdown = Settings.HideFoldersWithoutMarkdown;

        if (string.IsNullOrEmpty(WorkspaceRoot) || !Directory.Exists(WorkspaceRoot)) return;

        var root = _scanner.CreateRoot(WorkspaceRoot);
        _scanner.Load(root);

        WorkspaceRoots.Clear();
        WorkspaceRoots.Add(new FileTreeItemViewModel(root, _scanner) { IsExpanded = true });
    }

    private void RebuildFileWatchers()
    {
        if (!Settings.EnableFileWatching)
        {
            ClearModifiedOnDiskFlags();
            CancelPendingWatchedReloads();

            foreach (var watcher in _fileWatchers.Values)
            {
                DisposeWatcher(watcher);
            }

            _fileWatchers.Clear();
            return;
        }

        var requiredDirectories = Tabs
            .Select(tab => Path.GetDirectoryName(tab.FullPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(PathComparer)
            .ToList();

        foreach (var directory in _fileWatchers.Keys.Except(requiredDirectories, PathComparer).ToList())
        {
            DisposeWatcher(_fileWatchers[directory]);
            _fileWatchers.Remove(directory);
        }

        foreach (var directory in requiredDirectories)
        {
            if (_fileWatchers.ContainsKey(directory)) continue;

            var watcher = CreateWatcher(directory);
            if (watcher is not null)
            {
                _fileWatchers[directory] = watcher;
            }
        }
    }

    private FileSystemWatcher? CreateWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnWatchedFileChanged;
            watcher.Created += OnWatchedFileChanged;
            watcher.Deleted += OnWatchedFileChanged;
            watcher.Renamed += OnWatchedFileRenamed;

            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnWatchedFileChanged;
        watcher.Created -= OnWatchedFileChanged;
        watcher.Deleted -= OnWatchedFileChanged;
        watcher.Renamed -= OnWatchedFileRenamed;
        watcher.Dispose();
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs e) =>
        MarkModifiedOnDiskFromWatch(e.FullPath);

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs e)
    {
        MarkModifiedOnDiskFromWatch(e.OldFullPath);
        MarkModifiedOnDiskFromWatch(e.FullPath);
    }

    private void MarkModifiedOnDiskFromWatch(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Dispatcher.UIThread.Post(() => HandleWatchedFileChange(path));
    }

    private void HandleWatchedFileChange(string path)
    {
        if (!Settings.EnableFileWatching) return;

        var fullPath = Path.GetFullPath(path);
        if (IsOwnWrite(fullPath)) return;

        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.FullPath, fullPath));
        if (tab is null) return;

        tab.IsModifiedOnDisk = true;
        QueueWatchedReload(fullPath);
    }

    private void NoteSelfWrite(string fullPath) =>
        _selfWrites[Path.GetFullPath(fullPath)] = DateTime.UtcNow;

    private bool IsOwnWrite(string fullPath)
    {
        if (!_selfWrites.TryGetValue(fullPath, out var written)) return false;

        if (DateTime.UtcNow - written <= SelfWriteGrace) return true;

        _selfWrites.Remove(fullPath);
        return false;
    }

    private void QueueWatchedReload(string fullPath)
    {
        if (_pendingWatchedReloads.TryGetValue(fullPath, out var existing))
        {
            existing.Cancel();
        }

        var cancellation = new CancellationTokenSource();
        _pendingWatchedReloads[fullPath] = cancellation;
        _ = ReloadWatchedTabAsync(fullPath, cancellation);
    }

    private async Task ReloadWatchedTabAsync(string fullPath, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(WatchedReloadDebounceMs, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_pendingWatchedReloads.TryGetValue(fullPath, out var current) || current != cancellation)
        {
            return;
        }

        _pendingWatchedReloads.Remove(fullPath);

        if (!Settings.EnableFileWatching) return;

        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.FullPath, fullPath));
        if (tab is null) return;

        var shouldRestoreOffset = ReferenceEquals(tab, SelectedTab);
        var offset = tab.ScrollOffset;

        if (shouldRestoreOffset && CaptureScrollOffset is not null)
        {
            offset = CaptureScrollOffset();
            tab.ScrollOffset = offset;
        }

        await LoadIntoAsync(tab, tab.FullPath).ConfigureAwait(true);

        if (shouldRestoreOffset && ReferenceEquals(tab, SelectedTab))
        {
            RestoreScrollOffset?.Invoke(offset);
        }
    }

    private void ClearModifiedOnDiskFlags()
    {
        foreach (var tab in Tabs)
        {
            tab.IsModifiedOnDisk = false;
        }
    }

    private void PrunePendingWatchedReloads()
    {
        var openPaths = Tabs.Select(t => t.FullPath).ToHashSet(PathComparer);
        foreach (var item in _pendingWatchedReloads.Where(kvp => !openPaths.Contains(kvp.Key)).ToList())
        {
            item.Value.Cancel();
            _pendingWatchedReloads.Remove(item.Key);
        }
    }

    private void CancelPendingWatchedReloads()
    {
        foreach (var cancellation in _pendingWatchedReloads.Values)
        {
            cancellation.Cancel();
        }

        _pendingWatchedReloads.Clear();
    }

    /// <summary>Opens the item in its own tab; directories toggle expansion.</summary>
    public async Task ActivateTreeItemAsync(FileTreeItemViewModel? item)
    {
        if (item is null) return;

        if (item.IsDirectory)
        {
            item.IsExpanded = !item.IsExpanded;
            return;
        }

        await OpenDocumentAsync(item.FullPath).ConfigureAwait(true);
    }

    // ====================================================== recent documents

    private void RecordRecent(LoadedDocument document)
    {
        _recent.Touch(new RecentDocument(
            document.Path,
            Path.GetFileName(document.Path),
            DateTimeOffset.Now,
            SelectedTab?.ScrollOffset ?? 0));

        RefreshRecent();
    }

    private void RefreshRecent()
    {
        var now = DateTimeOffset.Now;

        RecentDocuments.Clear();
        foreach (var entry in _recent.Items)
        {
            RecentDocuments.Add(new RecentDocumentViewModel(entry, now, File.Exists(entry.Path)));
        }

        OnPropertyChanged(nameof(HasRecentDocuments));
        RefreshVisibleRecent();
        RefreshQuickOpenSources();
    }

    /// <summary>
    /// Projects the history onto what the start page shows: the newest few, or
    /// all of it once expanded.
    /// </summary>
    private void RefreshVisibleRecent()
    {
        var take = IsRecentExpanded ? RecentDocuments.Count : CollapsedRecentCount;

        VisibleRecentDocuments.Clear();
        foreach (var item in RecentDocuments.Take(take))
        {
            VisibleRecentDocuments.Add(item);
        }

        OnPropertyChanged(nameof(HasMoreRecentDocuments));
        OnPropertyChanged(nameof(RecentToggleText));
    }

    partial void OnIsRecentExpandedChanged(bool value) => RefreshVisibleRecent();

    [RelayCommand]
    private void ToggleRecentExpanded() => IsRecentExpanded = !IsRecentExpanded;

    private void RefreshQuickOpenSources()
    {
        var recent = RecentDocuments
            .Select(r => new QuickOpenResultViewModel(r.Title, r.FullPath, isRecent: true))
            .ToList();

        var workspace = HasWorkspace
            ? _scanner.EnumerateFiles(WorkspaceRoot!)
                .Select(f => new QuickOpenResultViewModel(f.Name, f.FullPath, isRecent: false))
                .ToList()
            : new List<QuickOpenResultViewModel>();

        QuickOpen.SetSources(recent, workspace);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentDocumentViewModel? recent)
    {
        if (recent is null || !recent.Exists) return;
        await OpenDocumentAsync(recent.FullPath).ConfigureAwait(true);
    }

    // =================================================================== tabs

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null) return;

        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Tabs.Remove(tab);

        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    [RelayCommand]
    private void CloseAllTabs()
    {
        Tabs.Clear();
        SelectedTab = null;
    }

    // ============================================================ view mode

    /// <summary>
    /// Supplied by the view: reads the reading position out of whichever view
    /// is on screen, and puts it back into the one that replaces it. Both work
    /// in source offsets, which is the whole reason a mode switch can keep the
    /// reader's place at all (SPECIFICATION.md 5.15).
    /// </summary>
    public Func<int>? CaptureScrollOffset { get; set; }

    public Action<int>? RestoreScrollOffset { get; set; }

    private void SetViewMode(DocumentViewMode mode)
    {
        var tab = SelectedTab;
        if (tab is null || !tab.HasDocument || tab.ViewMode == mode) return;

        // Capture before the switch, restore after: the outgoing view still
        // knows where the reader was, and only it can say.
        tab.ScrollOffset = CaptureScrollOffset?.Invoke() ?? tab.ScrollOffset;
        tab.ViewMode = mode;
        RestoreScrollOffset?.Invoke(tab.ScrollOffset);
        RequestSave();
    }

    [RelayCommand]
    private void ShowPreview() => SetViewMode(DocumentViewMode.Preview);

    [RelayCommand]
    private void ShowSource() => SetViewMode(DocumentViewMode.Source);

    [RelayCommand]
    private void ToggleViewMode()
    {
        var tab = SelectedTab;
        if (tab is null) return;

        SetViewMode(tab.ViewMode == DocumentViewMode.Preview
            ? DocumentViewMode.Source
            : DocumentViewMode.Preview);
    }

    // ================================================================= links

    /// <summary>
    /// Handles an activated link (SPECIFICATION.md 5.6). Classification is here,
    /// in the shell, rather than in the renderer.
    /// </summary>
    public async Task ActivateLinkAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (TryParseInternalSourceOffsetLink(url, out var sourceOffset))
        {
            var tab = SelectedTab;
            if (tab is not null)
            {
                tab.PushFootnoteReturnOffset(CaptureCurrentSourceOffset(tab));
            }

            StatusMessage = null;
            ScrollToOffsetRequested?.Invoke(sourceOffset);
            return;
        }

        // In-document anchor
        if (url.StartsWith('#'))
        {
            var node = SelectedTab is null
                ? null
                : OutlineBuilder.FindByAnchor(SelectedTab.Document?.Outline ?? Array.Empty<OutlineNode>(), url);

            if (node is null)
            {
                StatusMessage = $"No heading matches {url}";
                return;
            }

            ScrollToOffsetRequested?.Invoke(node.SourceOffset);
            return;
        }

        // Absolute web and mail links go to the OS handler.
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" or "mailto")
        {
            OpenExternal(absolute.ToString());
            return;
        }

        // Relative path, possibly with an anchor.
        var baseDirectory = SelectedTab?.Document?.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            StatusMessage = "Relative links need a saved document.";
            return;
        }

        var hashIndex = url.IndexOf('#');
        var relative = hashIndex >= 0 ? url[..hashIndex] : url;

        if (string.IsNullOrEmpty(relative))
        {
            StatusMessage = null;
            return;
        }

        var target = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(relative)));

        if (!File.Exists(target))
        {
            StatusMessage = $"Not found: {relative}";
            return;
        }

        if (WorkspaceScanner.IsMarkdown(target))
        {
            await OpenDocumentAsync(target).ConfigureAwait(true);
            return;
        }

        // Non-Markdown local files open with the OS handler, but only after a
        // confirmation dialog (5.6). Until there is a dialog service, the link
        // is reported rather than acted on — silently shelling out to an
        // arbitrary file is exactly the behaviour that rule exists to prevent.
        StatusMessage = $"Opening non-Markdown files is not enabled yet: {Path.GetFileName(target)}";
    }

    [RelayCommand]
    private void ReturnFromFootnote()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        if (!tab.TryPopFootnoteReturnOffset(out var sourceOffset)) return;

        StatusMessage = null;
        ScrollToOffsetRequested?.Invoke(sourceOffset);
    }

    public void EnableRemoteImagesForSelectedTab()
    {
        var tab = SelectedTab;
        if (tab?.Document is null) return;

        tab.AllowRemoteImages = true;
        StatusMessage = null;
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the link: {ex.Message}";
        }
    }

    private static bool TryParseInternalSourceOffsetLink(string url, out int sourceOffset)
    {
        sourceOffset = 0;

        if (!url.StartsWith(InternalSourceOffsetLinkPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = url[InternalSourceOffsetLinkPrefix.Length..];
        if (!int.TryParse(value, out sourceOffset) || sourceOffset < 0)
        {
            sourceOffset = 0;
            return false;
        }

        return true;
    }

    private int CaptureCurrentSourceOffset(DocumentTabViewModel tab)
    {
        var liveOffset = CaptureScrollOffset?.Invoke();
        if (liveOffset is >= 0)
        {
            return liveOffset.Value;
        }

        return tab.ScrollOffset;
    }

    partial void OnSelectedOutlineItemChanged(OutlineItemViewModel? value)
    {
        if (value is null) return;

        foreach (var item in Flatten(SelectedTab?.Outline ?? Enumerable.Empty<OutlineItemViewModel>()))
        {
            item.IsCurrent = ReferenceEquals(item, value);
        }

        if (_isFollowingScroll) return;

        ScrollToOffsetRequested?.Invoke(value.SourceOffset);
    }

    /// <summary>
    /// Moves the outline selection to the heading the reader is currently
    /// under, so scrolling the document keeps the panel in step with it.
    /// </summary>
    public void SyncOutlineSelectionToOffset(int sourceOffset)
    {
        var tab = SelectedTab;
        if (tab is null || tab.Outline.Count == 0) return;

        OutlineItemViewModel? match = null;
        foreach (var item in Flatten(tab.Outline))
        {
            if (item.SourceOffset > sourceOffset) break;
            match = item;
        }

        // Above the first heading there is nothing to point at, and the first
        // heading is the closest honest answer.
        match ??= tab.Outline[0];

        if (ReferenceEquals(match, SelectedOutlineItem)) return;

        _isFollowingScroll = true;
        try
        {
            SelectedOutlineItem = match;
        }
        finally
        {
            _isFollowingScroll = false;
        }
    }

    /// <summary>The outline in document order, parents before their children.</summary>
    private static IEnumerable<OutlineItemViewModel> Flatten(IEnumerable<OutlineItemViewModel> items)
    {
        foreach (var item in items)
        {
            yield return item;

            foreach (var child in Flatten(item.Children))
            {
                yield return child;
            }
        }
    }

    partial void OnSelectedTabChanged(DocumentTabViewModel? value)
    {
        SyncFindSearchText();

        if (value is not null)
        {
            IsStartPageRequested = false;
        }

        RequestSave();
    }

    partial void OnIsSidebarVisibleChanged(bool value) => RequestSave();

    // ================================================================ chrome

    [RelayCommand(CanExecute = nameof(CanToggleSidebar))]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleFindBar()
    {
        IsFindBarVisible = !IsFindBarVisible;
        if (!IsFindBarVisible) Find.Query = string.Empty;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        IsAboutVisible = false;
        IsSettingsVisible = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsVisible = false;

    [RelayCommand]
    private void ShowAbout()
    {
        IsSettingsVisible = false;
        IsAboutVisible = true;
    }

    [RelayCommand]
    private void CloseAbout() => IsAboutVisible = false;

    [RelayCommand]
    private async Task OpenHelpPageAsync()
    {
        if (!File.Exists(HelpPagePath))
        {
            StatusMessage = "The bundled help page is not available in this build.";
            return;
        }

        IsSettingsVisible = false;
        IsAboutVisible = false;
        await OpenDocumentAsync(HelpPagePath).ConfigureAwait(true);
    }

    /// <summary>Opens the Kveldstid AS website from the about page.</summary>
    [RelayCommand]
    private void OpenWebsite() => OpenExternal(WebsiteUrl);

    /// <summary>Opens the full MIT license on GitHub from the about page.</summary>
    [RelayCommand]
    private void OpenLicense() => OpenExternal(LicenseUrl);

    /// <summary>Opens the GitHub repository from the about page.</summary>
    [RelayCommand]
    private void OpenRepository() => OpenExternal(RepositoryUrl);

    /// <summary>Opens the third-party notices on GitHub from the about page.</summary>
    [RelayCommand]
    private void OpenThirdPartyNotices() => OpenExternal(ThirdPartyNoticesUrl);

    [RelayCommand]
    private void DismissOverlays()
    {
        if (IsQuickOpenVisible)
        {
            IsQuickOpenVisible = false;
            return;
        }

        if (IsFindBarVisible)
        {
            IsFindBarVisible = false;
            Find.Query = string.Empty;
            return;
        }

        if (IsAboutVisible)
        {
            IsAboutVisible = false;
            return;
        }

        IsSettingsVisible = false;

        if (CanLeaveStartPage)
        {
            IsStartPageRequested = false;
        }
    }

    [RelayCommand]
    private void CloseFindBar()
    {
        IsFindBarVisible = false;
        Find.Query = string.Empty;
    }

    private void OnFindMatchSelected(int sourceOffset)
    {
        if (RestoreScrollOffset is not null)
        {
            RestoreScrollOffset(sourceOffset);
            return;
        }

        ScrollToOffsetRequested?.Invoke(sourceOffset);
    }

    private void SyncFindSearchText()
    {
        Find.SetSearchText(SelectedTab?.SourceText ?? string.Empty);
    }

    // ==================================================== file associations

    /// <summary>
    /// Registers MdViewer as a Markdown handler and opens the OS page where the
    /// user confirms it as the default. Windows owns the final choice, so this
    /// can only prepare the registration.
    /// </summary>
    [Obsolete("Deferred to a later MdViewer release.")]
    [RelayCommand]
    private void SetAsDefaultMarkdownApp() =>
        StatusMessage = FileAssociationService.Register();

    /// <summary>
    /// Sidebar visibility captured when focus mode was entered, so leaving focus
    /// mode gives the user back the layout they had instead of a closed panel.
    /// </summary>
    private bool _sidebarVisibleBeforeFocusMode;

    [RelayCommand]
    private void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;

        if (IsFocusMode)
        {
            _sidebarVisibleBeforeFocusMode = IsSidebarVisible;
            IsSidebarVisible = false;
        }
        else
        {
            IsSidebarVisible = _sidebarVisibleBeforeFocusMode;
        }
    }

    // ============================================================ quick open

    [RelayCommand]
    private void ShowQuickOpen()
    {
        RefreshQuickOpenSources();
        QuickOpen.Reset();
        IsQuickOpenVisible = true;
    }

    [RelayCommand]
    private void CloseQuickOpen() => IsQuickOpenVisible = false;

    [RelayCommand]
    private void SelectPreviousQuickOpenResult()
    {
        if (!IsQuickOpenVisible)
        {
            return;
        }

        QuickOpen.SelectPreviousResult();
    }

    [RelayCommand]
    private void SelectNextQuickOpenResult()
    {
        if (!IsQuickOpenVisible)
        {
            return;
        }

        QuickOpen.SelectNextResult();
    }

    [RelayCommand]
    private async Task AcceptQuickOpenAsync()
    {
        var result = QuickOpen.SelectedResult;
        IsQuickOpenVisible = false;

        if (result is not null)
        {
            await OpenDocumentAsync(result.FullPath).ConfigureAwait(true);
        }
    }

    // ================================================================== zoom

    [RelayCommand]
    private void ZoomIn()
    {
        if (SelectedTab is null) return;
        SelectedTab.Zoom = Math.Min(3.0, Math.Round(SelectedTab.Zoom + 0.1, 2));
        RequestSave();
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (SelectedTab is null) return;
        SelectedTab.Zoom = Math.Max(0.5, Math.Round(SelectedTab.Zoom - 0.1, 2));
        RequestSave();
    }

    [RelayCommand]
    private void ZoomReset()
    {
        if (SelectedTab is not null) SelectedTab.Zoom = 1.0;
        RequestSave();
    }

    // ================================================================= theme

    /// <summary>
    /// System → Light → Dark. The button and the settings page are two views of
    /// one value, so this writes the setting and lets <see cref="ApplySettings"/>
    /// do the applying and the saving.
    /// </summary>
    [RelayCommand]
    private void CycleTheme()
    {
        var next = Settings.Theme switch
        {
            "Light" => "Dark",
            "Dark" => "System",
            _ => "Light",
        };

        Settings.Theme = next;
        _ = ShowThemePopupAsync(next);
    }

    private async Task ShowThemePopupAsync(string themeName)
    {
        _themePopupCancellation?.Cancel();

        var cancellation = new CancellationTokenSource();
        _themePopupCancellation = cancellation;

        ThemePopupText = $"Theme: {themeName}";
        IsThemePopupVisible = true;

        try
        {
            await Task.Delay(1200, cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_themePopupCancellation == cancellation)
        {
            IsThemePopupVisible = false;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
