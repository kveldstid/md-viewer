using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MdViewer.App.Services;
using MdViewer.App.ViewModels;
using MdViewer.Core.Documents;
using MdViewer.Core.Session;
using MdViewer.Rendering;

namespace MdViewer.App.Views;

public partial class MainWindow : Window
{
    private const double DefaultSidebarWidth = 260;

    private MainWindowViewModel? _boundModel;
    private double _lastSidebarWidth = DefaultSidebarWidth;
    private bool _syncingTreeSelection;

    public MainWindow()
    {
        InitializeComponent();

        // The tree's activation gestures live here rather than in the view
        // model: they are input concerns, and the view model stays free of
        // Avalonia so it can be tested without a UI thread.
        var tree = this.FindControl<TreeView>("FileTree");
        if (tree is not null)
        {
            tree.SelectionChanged += OnTreeSelectionChanged;
            tree.DoubleTapped += OnTreeDoubleTapped;
        }

        var presenter = this.FindControl<MarkdownPresenter>("Presenter");
        if (presenter is not null)
        {
            presenter.LinkActivated = OnLinkActivated;
            presenter.RemoteImagesRequested = OnRemoteImagesRequested;
            presenter.TaskListToggled = OnTaskListToggled;
        }

        var documentPane = this.FindControl<Panel>("DocumentPane");
        if (documentPane is not null)
        {
            documentPane.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnDocumentPointerWheelChanged,
                RoutingStrategies.Tunnel);
        }

        var previewScroller = this.FindControl<ScrollViewer>("DocumentScroller");
        if (previewScroller is not null)
        {
            previewScroller.ScrollChanged += (_, _) => FollowScrollInOutline();
        }

        var source = this.FindControl<SourceView>("Source");
        if (source is not null)
        {
            source.Scrolled += FollowScrollInOutline;
        }

        DataContextChanged += OnDataContextChanged;

        AddHandler(KeyDownEvent, OnNavigationKeyDown, RoutingStrategies.Bubble);
    }

    // =========================================================== keyboard nav

    /// <summary>
    /// Reading-position hot keys. They drive whichever scroll host is on
    /// screen, so preview and source behave identically. Text entry keeps
    /// priority: while a text box has focus the keys mean caret movement.
    /// </summary>
    private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Model?.SelectedTab is null) return;
        if (e.KeyModifiers is not KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        var scroller = Model.SelectedTab.ViewMode == DocumentViewMode.Source
            ? SourceViewControl?.ScrollHost
            : PreviewScroller;
        if (scroller is null) return;

        switch (e.Key)
        {
            case Key.Up: scroller.LineUp(); break;
            case Key.Down: scroller.LineDown(); break;
            case Key.PageUp: scroller.PageUp(); break;
            case Key.PageDown: scroller.PageDown(); break;
            case Key.Home:
                scroller.Offset = new Vector(scroller.Offset.X, 0);
                break;
            case Key.End:
                scroller.Offset = new Vector(
                    scroller.Offset.X,
                    Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
                break;
            default: return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Keeps the outline pointing at the heading the reader is under. The
    /// position is read in source offsets, so it works the same in both views.
    /// </summary>
    private void FollowScrollInOutline()
    {
        var model = Model;
        if (model?.SelectedTab is null) return;

        model.SyncOutlineSelectionToOffset(CaptureScrollOffset());
        BringSelectedOutlineItemIntoView();
    }

    private void BringSelectedOutlineItemIntoView()
    {
        var tree = this.FindControl<TreeView>("OutlineTree");
        var item = Model?.SelectedOutlineItem;
        if (tree is null || item is null) return;

        // Containers below a collapsed or virtualised branch only exist after
        // the next layout pass, so the scroll has to wait for it.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (tree.TreeContainerFromItem(item) is TreeViewItem container)
                {
                    container.BringIntoView();
                }
            },
            DispatcherPriority.Background);
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    private MarkdownPresenter? PresenterControl => this.FindControl<MarkdownPresenter>("Presenter");

    private ScrollViewer? PreviewScroller => this.FindControl<ScrollViewer>("DocumentScroller");

    private SourceView? SourceViewControl => this.FindControl<SourceView>("Source");

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (Model is null) return;

        Model.Storage = new StorageService(this);

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _ = Model.InitializeAsync(args);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundModel is not null)
        {
            _boundModel.ScrollToOffsetRequested -= OnScrollToOffsetRequested;
            _boundModel.PropertyChanged -= OnModelPropertyChanged;
            _boundModel.CaptureScrollOffset = null;
            _boundModel.RestoreScrollOffset = null;
            _boundModel.CaptureViewState = null;
            _boundModel.ApplyViewState = null;
            _boundModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }

        _boundModel = Model;

        if (_boundModel is not null)
        {
            _boundModel.ScrollToOffsetRequested += OnScrollToOffsetRequested;
            _boundModel.PropertyChanged += OnModelPropertyChanged;
            _boundModel.CaptureScrollOffset = CaptureScrollOffset;
            _boundModel.RestoreScrollOffset = RestoreScrollOffset;
            _boundModel.CaptureViewState = CaptureViewState;
            _boundModel.ApplyViewState = ApplyViewState;
            _boundModel.Settings.PropertyChanged += OnSettingsPropertyChanged;

            ApplySidebarVisibility(_boundModel.IsSidebarPresent);
            ApplySidebarSections();
        }
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.SettingsViewModel.ShowExplorerPanel)
            or nameof(ViewModels.SettingsViewModel.ShowOutlinePanel))
        {
            // The column itself is collapsed via IsSidebarPresent on the model.
            ApplySidebarSections();
        }
    }

    // =========================================================== window state

    /// <summary>
    /// Window bounds, window state and the sidebar splitter width are the parts
    /// of the session only the window can answer for (SPECIFICATION.md 5.13).
    /// </summary>
    private void CaptureViewState(SessionState session)
    {
        session.IsMaximized = WindowState == WindowState.Maximized;

        // Only a normal window's bounds are worth remembering: a maximized
        // one would restore to the size of the screen it happened to be on.
        if (WindowState == WindowState.Normal)
        {
            session.WindowWidth = Width;
            session.WindowHeight = Height;
            session.WindowX = Position.X;
            session.WindowY = Position.Y;
        }

        var grid = this.FindControl<Grid>("BodyGrid");
        var column = grid?.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions[0] : null;

        session.SidebarWidth = column is not null && column.Width.IsAbsolute && column.Width.Value > 0
            ? column.Width.Value
            : _lastSidebarWidth;
    }

    private void ApplyViewState(SessionState session)
    {
        if (session.SidebarWidth > 0)
        {
            _lastSidebarWidth = session.SidebarWidth;
            ApplySidebarVisibility(Model?.IsSidebarPresent ?? true);
        }

        if (session.WindowWidth is > 0 && session.WindowHeight is > 0)
        {
            Width = session.WindowWidth.Value;
            Height = session.WindowHeight.Value;
        }

        if (session.WindowX is not null && session.WindowY is not null)
        {
            var position = new PixelPoint((int)session.WindowX.Value, (int)session.WindowY.Value);
            if (IsOnAScreen(position))
            {
                Position = position;
            }
        }

        if (session.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// A saved position can point at a monitor that is no longer attached.
    /// Restoring it would put the window somewhere the user cannot reach it,
    /// so an off-screen position is dropped and the default one kept.
    /// </summary>
    private bool IsOnAScreen(PixelPoint position) =>
        Screens.All.Any(screen => screen.Bounds.Contains(position));

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // A clean exit flushes whatever the debounce is still holding.
        Model?.SaveNow();
        base.OnClosing(e);
    }

    // =============================================================== sidebar

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarPresent) && _boundModel is not null)
        {
            ApplySidebarVisibility(_boundModel.IsSidebarPresent);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
        {
            SyncTreeSelectionWithSelectedTab();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsFindBarVisible)
                 && _boundModel?.IsFindBarVisible == true)
        {
            FocusFindInput();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsQuickOpenVisible)
                 && _boundModel?.IsQuickOpenVisible == true)
        {
            FocusQuickOpenInput();
        }
    }

    private void FocusFindInput()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var input = this.FindControl<TextBox>("FindInput");
                if (input is null) return;

                input.Focus();
                input.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void FocusQuickOpenInput()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var input = this.FindControl<TextBox>("QuickOpenInput");
                if (input is null) return;

                input.Focus();
                input.SelectAll();
            },
            DispatcherPriority.Input);
    }

    /// <summary>
    /// A pixel-sized column does not collapse when its child is hidden the way
    /// an Auto column does, so hiding the sidebar has to zero the column — and
    /// its MinWidth with it, or the minimum would hold the gap open.
    /// The width the user dragged to is remembered and restored.
    /// </summary>
    private void ApplySidebarVisibility(bool visible)
    {
        var grid = this.FindControl<Grid>("BodyGrid");
        if (grid is null || grid.ColumnDefinitions.Count == 0) return;

        var column = grid.ColumnDefinitions[0];

        if (visible)
        {
            column.MinWidth = 180;
            column.MaxWidth = 640;
            column.Width = new GridLength(_lastSidebarWidth, GridUnitType.Pixel);
        }
        else
        {
            if (column.Width.IsAbsolute && column.Width.Value > 0)
            {
                _lastSidebarWidth = column.Width.Value;
            }

            column.MinWidth = 0;
            column.MaxWidth = 0;
            column.Width = new GridLength(0, GridUnitType.Pixel);
        }
    }

    /// <summary>
    /// Explorer and outline live in star-sized rows, and a star row keeps its
    /// share of the height when its child is merely hidden. Turning a panel off
    /// therefore has to zero the row, the same way hiding the sidebar zeroes
    /// its column.
    /// </summary>
    private void ApplySidebarSections()
    {
        var settings = Model?.Settings;
        if (settings is null) return;

        var grid = this.FindControl<Grid>("SidebarGrid");
        if (grid is null || grid.RowDefinitions.Count < 3) return;

        grid.RowDefinitions[0].Height = settings.ShowExplorerPanel
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);

        grid.RowDefinitions[2].Height = settings.ShowOutlinePanel
            ? new GridLength(1.2, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);

        var explorer = this.FindControl<Grid>("ExplorerSection");
        if (explorer is not null) explorer.IsVisible = settings.ShowExplorerPanel;

        var outline = this.FindControl<Grid>("OutlineSection");
        if (outline is not null) outline.IsVisible = settings.ShowOutlinePanel;

        // The splitter only means something with a panel on each side of it.
        var splitter = this.FindControl<GridSplitter>("SidebarSplitter");
        if (splitter is not null)
        {
            splitter.IsVisible = settings.ShowExplorerPanel && settings.ShowOutlinePanel;
        }
    }

    // ================================================================== zoom

    private void OnDocumentPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var model = Model;
        if (model?.SelectedTab is null) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        if (e.Delta.Y > 0)
        {
            if (model.ZoomInCommand.CanExecute(null)) model.ZoomInCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Delta.Y < 0)
        {
            if (model.ZoomOutCommand.CanExecute(null)) model.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ============================================================= view mode

    /// <summary>
    /// Reads the reading position out of whichever view is currently on screen.
    /// Both answer in source offsets, which is what makes the two modes
    /// interchangeable at all (SPECIFICATION.md 5.15).
    /// </summary>
    private int CaptureScrollOffset()
    {
        var tab = Model?.SelectedTab;
        if (tab is null) return 0;

        if (tab.ViewMode == DocumentViewMode.Source)
        {
            return SourceViewControl?.GetTopSourceOffset() ?? tab.ScrollOffset;
        }

        var presenter = PresenterControl;
        var scroller = PreviewScroller;
        if (presenter is null || scroller is null) return tab.ScrollOffset;

        return presenter.GetSourceOffsetAt(scroller.Offset.Y);
    }

    /// <summary>
    /// Puts it back into the view that just became visible. Posted rather than
    /// called directly: the incoming view has not been measured yet at the
    /// moment the mode flips, so there is nothing to scroll to until layout runs.
    /// </summary>
    private void RestoreScrollOffset(int sourceOffset)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                // Posting alone is not enough: at Loaded priority the incoming
                // view may still be unmeasured, and an unmeasured text layout
                // answers nothing useful. Force the pass before asking.
                UpdateLayout();
                OnScrollToOffsetRequested(sourceOffset);
            },
            DispatcherPriority.Loaded);
    }

    private void OnScrollToOffsetRequested(int sourceOffset)
    {
        var tab = Model?.SelectedTab;

        if (tab?.ViewMode == DocumentViewMode.Source)
        {
            SourceViewControl?.ScrollToSourceOffset(sourceOffset);
            return;
        }

        PresenterControl?.ScrollToSourceOffset(sourceOffset);
    }

    // ================================================================= links

    private void OnLinkActivated(string url)
    {
        if (Model is null) return;
        _ = Model.ActivateLinkAsync(url);
    }

    private void OnRemoteImagesRequested()
    {
        Model?.EnableRemoteImagesForSelectedTab();
    }

    private void OnTaskListToggled(int sourceOffset, bool isChecked)
    {
        if (Model is null) return;
        _ = Model.ToggleTaskAsync(sourceOffset, isChecked);
    }

    // ============================================================== file tree

    /// <summary>
    /// Selecting a tab highlights the matching entry in the file tree,
    /// expanding the folders on the way down and scrolling it into view.
    /// </summary>
    private void SyncTreeSelectionWithSelectedTab()
    {
        var path = Model?.SelectedTab?.FullPath;
        if (string.IsNullOrEmpty(path)) return;

        var tree = this.FindControl<TreeView>("FileTree");
        if (tree is null || Model is null) return;

        var item = FindTreeItem(Model.WorkspaceRoots, path);
        if (item is null || ReferenceEquals(tree.SelectedItem, item)) return;

        _syncingTreeSelection = true;
        try
        {
            tree.SelectedItem = item;
        }
        finally
        {
            _syncingTreeSelection = false;
        }

        // Containers for freshly expanded folders are only realised after the
        // next layout pass, so scrolling has to wait for it.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (tree.TreeContainerFromItem(item) is TreeViewItem container)
                {
                    container.BringIntoView();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Depth-first lookup that expands directories along the path, which also
    /// triggers their lazy child enumeration.
    /// </summary>
    private static FileTreeItemViewModel? FindTreeItem(
        IEnumerable<FileTreeItemViewModel> items,
        string fullPath)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FullPath)) continue;

            if (!item.IsDirectory)
            {
                if (PathsEqual(item.FullPath, fullPath)) return item;
                continue;
            }

            if (!IsUnder(fullPath, item.FullPath)) continue;

            item.IsExpanded = true;

            var match = FindTreeItem(item.Children, fullPath);
            if (match is not null) return match;
        }

        return null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string candidate, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void OnOpenTreeItemInEditorClick(object? sender, RoutedEventArgs e)
    {
        var model = Model;
        if (model is null) return;

        var tree = this.FindControl<TreeView>("FileTree");
        model.OpenTreeItemInEditorCommand.Execute(tree?.SelectedItem as FileTreeItemViewModel);
    }

    private void OnOpenSelectedDocumentInEditorClick(object? sender, RoutedEventArgs e)
    {
        Model?.OpenSelectedDocumentInEditorCommand.Execute(null);
    }

    /// <summary>Single click opens the document in its own tab.</summary>
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTreeSelection) return;
        if (Model is null) return;
        if (sender is not TreeView tree) return;
        if (tree.SelectedItem is not FileTreeItemViewModel item) return;
        if (item.IsDirectory) return;

        _ = Model.ActivateTreeItemAsync(item);
    }

    /// <summary>Double click is equivalent to a single click.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Model is null) return;
        if (sender is not TreeView tree) return;
        if (tree.SelectedItem is not FileTreeItemViewModel item) return;

        _ = Model.ActivateTreeItemAsync(item);
    }

    // =============================================== explorer context menu

    private FileTreeItemViewModel? SelectedTreeItem =>
        this.FindControl<TreeView>("FileTree")?.SelectedItem as FileTreeItemViewModel;

    /// <summary>
    /// Greys out what the current selection cannot do rather than letting the
    /// command fail into the status bar: opening in an editor is for files,
    /// and everything needs something selected.
    /// </summary>
    private void OnFileTreeContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        var item = SelectedTreeItem;
        var hasSelection = item is not null && !string.IsNullOrWhiteSpace(item.FullPath);

        foreach (var entry in menu.Items.OfType<MenuItem>())
        {
            entry.IsEnabled = entry.Name switch
            {
                "OpenInEditorMenuItem" => hasSelection && !item!.IsDirectory,
                _ => hasSelection,
            };
        }
    }

    private void OnOpenTreeItemInEditorClick(object? sender, RoutedEventArgs e) =>
        Model?.OpenTreeItemInEditorCommand.Execute(SelectedTreeItem);

    private void OnRevealTreeItemClick(object? sender, RoutedEventArgs e) =>
        Model?.RevealTreeItemInFileManagerCommand.Execute(SelectedTreeItem);

    /// <summary>
    /// Copying lives here rather than in the view model because the clipboard
    /// hangs off the top level, which the view model deliberately knows nothing about.
    /// </summary>
    private async void OnCopyTreeItemPathClick(object? sender, RoutedEventArgs e)
    {
        var model = Model;
        if (model is null) return;

        var item = SelectedTreeItem;
        if (item is null || string.IsNullOrWhiteSpace(item.FullPath))
        {
            model.StatusMessage = "Select a file or folder in Explorer first.";
            return;
        }

        var clipboard = Clipboard;
        if (clipboard is null)
        {
            model.StatusMessage = "The clipboard is not available.";
            return;
        }

        var fullPath = Path.GetFullPath(item.FullPath);

        try
        {
            await clipboard.SetTextAsync(fullPath);
            model.StatusMessage = $"Copied {fullPath}";
        }
        catch (Exception ex)
        {
            model.StatusMessage = $"Could not copy the path: {ex.Message}";
        }
    }
}
