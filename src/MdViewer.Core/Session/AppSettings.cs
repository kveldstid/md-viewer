using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdViewer.Core.Session;

/// <summary>
/// The contents of <c>settings.json</c> (SPECIFICATION.md 5.13).
///
/// Parsing is forward-compatible: <see cref="Extra"/> collects any key this
/// version does not know about and writes it back out unchanged, so moving
/// between versions never silently destroys configuration.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ============================================================ appearance

    /// <summary>"System", "Light" or "Dark" (SPECIFICATION.md 5.11).</summary>
    public string Theme { get; set; } = "System";

    // ================================================================ panels

    public bool ShowExplorerPanel { get; set; } = true;

    public bool ShowOutlinePanel { get; set; } = true;

    public bool ShowStatusBar { get; set; } = true;

    // ============================================================ typography

    /// <summary>A family name, or "Default" to keep the theme's font stack.</summary>
    public string BodyFontFamily { get; set; } = "Default";

    public double BodyFontSize { get; set; } = 15;

    /// <summary>A family name, or "Default" to keep the theme's font stack.</summary>
    public string MonospaceFontFamily { get; set; } = "Default";

    public double MonospaceFontSize { get; set; } = 13.5;

    /// <summary>Line height as a multiple of the font size.</summary>
    public double LineHeight { get; set; } = 1.65;

    public double ContentMaxWidth { get; set; } = 900;

    public bool UseFullWidth { get; set; }

    // ================================================================ parsing

    /// <summary>
    /// Renders ... as an ellipsis, -- and --- as dashes and straight quotes as
    /// curly ones. Off by default (SPECIFICATION.md 5.1).
    /// </summary>
    public bool SmartPunctuation { get; set; }

    /// <summary>
    /// Lets the reader tick task list checkboxes, which rewrites the marker in
    /// the file on disk. Off by default: MdViewer is a reader unless asked.
    /// </summary>
    public bool EnableTaskListEditing { get; set; }

    // ============================================================== raw view

    public bool ShowLineNumbersInRawView { get; set; } = true;

    public bool ShowFrontMatter { get; set; }

    // ================================================================ editor

    /// <summary>
    /// Full path to the program used by "Open in editor". Empty means no
    /// editor has been chosen, and the command reports that instead of
    /// guessing at one.
    /// </summary>
    public string DefaultEditorPath { get; set; } = string.Empty;

    // ======================================================== file watching

    public bool EnableFileWatching { get; set; } = true;

    // ============================================================= explorer

    /// <summary>
    /// Hides folders in the explorer tree whose subtree contains no markdown
    /// file. Off by default: the tree shows the folder structure as it is.
    /// </summary>
    public bool HideFoldersWithoutMarkdown { get; set; }

    // =============================================================== startup

    public bool ReopenPreviousFolder { get; set; } = true;

    /// <summary>
    /// The workspace root open when the app last exited. Restored on launch
    /// when <see cref="ReopenPreviousFolder"/> is set.
    /// </summary>
    public string? LastFolder { get; set; }

    /// <summary>Keys written by another version, preserved verbatim.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
