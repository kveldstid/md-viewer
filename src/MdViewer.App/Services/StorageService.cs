using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MdViewer.App.Services;

/// <summary>
/// <see cref="IStorageService"/> over Avalonia's storage provider, which uses
/// the native picker on both Windows and Linux.
/// </summary>
public sealed class StorageService : IStorageService
{
    private readonly TopLevel _topLevel;

    public StorageService(TopLevel topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<IReadOnlyList<string>> PickMarkdownFilesAsync()
    {
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown")
                {
                    Patterns = new[] { "*.md", "*.markdown", "*.mdown", "*.mkd", "*.mdx" }
                },
                new FilePickerFileType("Text")
                {
                    Patterns = new[] { "*.txt" }
                },
                FilePickerFileTypes.All
            }
        });

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        return paths;
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open folder",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickEditorExecutableAsync()
    {
        var fileTypes = new List<FilePickerFileType>();
        if (OperatingSystem.IsWindows())
        {
            fileTypes.Add(new FilePickerFileType("Programs")
            {
                Patterns = new[] { "*.exe", "*.cmd", "*.bat" }
            });
        }

        fileTypes.Add(FilePickerFileTypes.All);

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the editor for Markdown files",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
