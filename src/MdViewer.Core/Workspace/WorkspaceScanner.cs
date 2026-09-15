namespace MdViewer.Core.Workspace;

/// <summary>
/// Enumerates a workspace folder for the file tree (SPECIFICATION.md 5.9).
///
/// Directories are read on expand, not up front: a workspace root can contain
/// a hundred thousand files, and walking it eagerly would stall the window on
/// open for a tree the user may never expand.
/// </summary>
public sealed class WorkspaceScanner
{
    private static readonly string[] MarkdownExtensions =
        { ".md", ".markdown", ".mdown", ".mkd", ".mdx" };

    /// <summary>
    /// Always hidden. Overridable in settings, but these four are noise in every
    /// workspace anyone has ever opened.
    /// </summary>
    private static readonly HashSet<string> AlwaysHidden =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj" };

    public bool ShowAllFiles { get; init; }

    public bool ShowHiddenEntries { get; init; }

    /// <summary>
    /// Hides folders whose subtree holds no markdown file at all. A folder that
    /// only leads to markdown further down is still shown, otherwise the path to
    /// a document would disappear with it.
    /// </summary>
    public bool HideFoldersWithoutMarkdown { get; set; }

    public static bool IsMarkdown(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the root node without enumerating it.</summary>
    public WorkspaceNode CreateRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var full = Path.GetFullPath(directory);
        var name = new DirectoryInfo(full).Name;

        return new WorkspaceNode(string.IsNullOrEmpty(name) ? full : name, full, isDirectory: true);
    }

    /// <summary>
    /// Fills a directory node's children. Safe to call more than once; a second
    /// call re-reads, which is what the file watcher wants.
    /// </summary>
    public void Load(WorkspaceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsDirectory) return;

        node.Children.Clear();

        try
        {
            var entries = new DirectoryInfo(node.FullPath);

            foreach (var directory in entries.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldHide(directory.Name, directory.Attributes)) continue;
                if (HideFoldersWithoutMarkdown && !ContainsMarkdown(directory)) continue;

                node.AddChild(new WorkspaceNode(directory.Name, directory.FullName, isDirectory: true));
            }

            foreach (var file in entries.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ShouldHide(file.Name, file.Attributes)) continue;
                if (!ShowAllFiles && !IsMarkdown(file.Name)) continue;

                node.AddChild(new WorkspaceNode(file.Name, file.FullName, isDirectory: false));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // An unreadable folder is shown empty rather than failing the tree.
            // On Flatpak in particular this is a normal, expected outcome.
        }

        node.IsLoaded = true;
    }

    /// <summary>
    /// Walks the tree for the quick-open index, breadth-first with a cap
    /// (SPECIFICATION.md 5.9).
    /// </summary>
    public IReadOnlyList<WorkspaceNode> EnumerateFiles(string root, int limit = 50_000)
    {
        var results = new List<WorkspaceNode>();
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(root));

        while (queue.Count > 0 && results.Count < limit)
        {
            var current = queue.Dequeue();

            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (results.Count >= limit) break;
                    if (!ShowAllFiles && !IsMarkdown(file)) continue;

                    results.Add(new WorkspaceNode(Path.GetFileName(file), file, isDirectory: false));
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(directory);
                    if (ShouldHide(name, FileAttributes.Directory)) continue;
                    queue.Enqueue(directory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Skip and carry on.
            }
        }

        return results;
    }

    /// <summary>
    /// True when the folder, or anything visible beneath it, holds a markdown
    /// file. Depth-limited so a symlink cycle cannot walk forever.
    /// </summary>
    private bool ContainsMarkdown(DirectoryInfo directory, int depth = 0)
    {
        const int MaxDepth = 32;
        if (depth > MaxDepth) return false;
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if (ShouldHide(file.Name, file.Attributes)) continue;
                if (IsMarkdown(file.Name)) return true;
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                if (ShouldHide(child.Name, child.Attributes)) continue;
                if (ContainsMarkdown(child, depth + 1)) return true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // An unreadable folder counts as empty.
        }

        return false;
    }

    private bool ShouldHide(string name, FileAttributes attributes)
    {
        if (AlwaysHidden.Contains(name)) return true;
        if (ShowHiddenEntries) return false;

        if (name.StartsWith('.')) return true;
        return attributes.HasFlag(FileAttributes.Hidden);
    }
}
