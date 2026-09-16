# KT MD Viewer — Specification

A cross-platform Markdown viewer for Windows and Linux, built on Avalonia UI.

|                      |                                                             |
| -------------------- | ----------------------------------------------------------- |
| **Status**           | Draft v0.1                                                  |
| **Date**             | 2026-09-10                                                  |
| **Target platforms** | Windows 10/11 (x64, ARM64), Linux (x64, ARM64)              |
| **Scope of v1**      | Read-only viewer; architecture prepared for an editor in v2 |

---

## 1. Purpose and Goals

KT MD Viewer is a fast, native desktop application for reading Markdown documents. It opens files in tabs, renders them with native Avalonia controls, and stays out of the way.

### 1.1 Design goals

1. **Instant.** Cold start under 1 second; a 100 KB document rendered under 200 ms.
2. **Native.** No embedded browser. The rendered document is a live Avalonia visual tree — themable, selectable, accessible, and consistent across platforms.
3. **Faithful.** CommonMark plus the GitHub-Flavored Markdown extensions people actually use: tables, task lists, strikethrough, autolinks, footnotes.
4. **Unobtrusive.** Reading is the primary activity. Chrome is minimal, keyboard navigation is complete, and the window remembers what you were doing.
5. **Extensible toward editing.** Every architectural decision in v1 is made so that a split-pane source editor can be added in v2 without a rewrite.

### 1.2 Explicit non-goals for v1

- No editing, no writing to Markdown files.
- No Markdown *authoring* aids (snippets, formatting toolbar, paste-as-Markdown).
- No cloud sync, no accounts, no telemetry.
- No macOS build in v1. The codebase must not preclude one; it is simply untested and unshipped.
- No arbitrary raw-HTML rendering (see §5.6).

---

## 2. Technology Decisions

### 2.1 Rendering approach: native controls, not WebView

This is the foundational decision, so it is recorded with its alternatives.

| Criterion                       | Native Avalonia visual tree                                                | Markdig → HTML → WebView                                                                                   |
| ------------------------------- | -------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| Linux reliability               | Full control; one rendering path (Skia) on both platforms                  | Depends on WebKitGTK version present on the user's distro; a known source of blank panes and crash reports |
| Deployment size                 | No extra runtime                                                           | Pulls in a browser engine or depends on a system one                                                       |
| Startup cost                    | Milliseconds                                                               | Browser process spin-up, typically 200–600 ms                                                              |
| Theming                         | Avalonia styles; theme switching is instant and shares the app's resources | Separate CSS layer to keep in sync with app theme                                                          |
| Text selection across blocks    | Must be built (see §5.7) — real work                                       | Free                                                                                                       |
| Tables, nested structures       | Must be built — real work                                                  | Free                                                                                                       |
| Raw HTML in Markdown            | Not supported; degrade to escaped text                                     | Free, and a security liability                                                                             |
| Accessibility                   | Avalonia's automation peers; Linux AT-SPI2 support since Avalonia 12       | Browser accessibility tree, isolated from the app's                                                        |
| Debuggability                   | Standard .NET debugging                                                    | Two runtimes, IPC boundary                                                                                 |
| Scroll sync for a future editor | Direct: every visual knows its source offset                               | Requires JS interop plumbing                                                                               |

**Decision: native.** The two genuine advantages of the WebView path — free text selection and free table layout — are one-time engineering costs that we pay once and control forever. The disadvantages of the WebView path are recurring and largely outside our control, and the worst of them lands on Linux, which is half our target.

**Consequence to accept honestly:** cross-block text selection and complex table layout are the two hardest parts of this project. They are specified in §5.7 and §5.4 rather than waved at.

### 2.2 Dependencies

Versions verified against NuGet on 2026-09-10.

| Package                                    | Version | Role                                             |
| ------------------------------------------ | ------- | ------------------------------------------------ |
| `Avalonia`                                 | 12.1.2  | UI framework. Targets .NET 10, SkiaSharp 3.      |
| `Avalonia.Desktop`                         | 12.1.2  | Windows/X11/Wayland backends                     |
| `Avalonia.Themes.Fluent`                   | 12.1.2  | Base theme                                       |
| `Markdig`                                  | 1.3.2   | Markdown parser → AST                            |
| `TextMateSharp`                            | 2.0.4   | TextMate grammars + themes for code highlighting |
| `TextMateSharp.Grammars`                   | 2.0.4   | Bundled grammar set                              |
| `CommunityToolkit.Mvvm`                    | 8.4.2   | Observable objects, commands, messaging          |
| `Microsoft.Extensions.DependencyInjection` | 10.x    | Composition root                                 |
| `Microsoft.Extensions.Logging`             | 10.x    | Diagnostics                                      |

**Target framework:** `net10.0`. Avalonia 12 dropped `netstandard2.0` from most projects and targets .NET 10.

#### Open dependency risk — SVG rendering

`Avalonia.Svg.Skia`, the obvious choice for displaying rendered diagrams (§5.5), has no Avalonia 12–compatible release as of this writing (latest: 11.3.0). Three mitigations, in order of preference:

1. Wait for or contribute an Avalonia 12 build; the package is actively maintained.
2. Rasterize diagram SVG to PNG in the out-of-process renderer and display a plain `Image`. Loses crispness on DPI change; acceptable for v1.
3. Vendor a minimal SVG→Skia path ourselves. Only if diagrams become a headline feature.

**v1 ships with option 2** unless option 1 has landed by the time §5.5 is implemented. This keeps diagrams off the critical path.

### 2.3 Architectural pattern

MVVM, with the view models free of Avalonia dependencies so they are unit-testable without a UI thread. `CommunityToolkit.Mvvm` source generators for `[ObservableProperty]` and `[RelayCommand]`. Dependency injection through a composition root in `App.axaml.cs`; no service locator.

---

## 3. Project Structure

```
MdViewer.sln
├── src/
│   ├── MdViewer.Core/            # No UI references
│   │   ├── Documents/            # Document model, loading, watching
│   │   ├── Markdown/             # Markdig pipeline configuration, AST helpers
│   │   ├── Outline/              # Heading tree extraction
│   │   ├── Search/               # In-document search over the AST
│   │   └── Session/              # Persisted state model
│   ├── MdViewer.Rendering/       # Avalonia-dependent; AST → visual tree
│   │   ├── Blocks/               # One renderer per block type
│   │   ├── Inlines/              # One renderer per inline type
│   │   ├── CodeHighlighting/     # TextMateSharp integration
│   │   ├── Diagrams/             # Pluggable diagram renderers
│   │   └── MarkdownPresenter.cs  # The control that hosts a document
│   ├── MdViewer.App/             # Views, view models, DI, entry point
│   └── MdViewer.Diagnostics/     # Logging, crash capture
├── tests/
│   ├── MdViewer.Core.Tests/
│   ├── MdViewer.Rendering.Tests/ # Headless Avalonia + golden files
│   └── MdViewer.App.Tests/
├── samples/                      # Markdown torture tests, used by tests and by hand
└── docs/
    └── SPECIFICATION.md          # this document
```

The `Core` / `Rendering` split is what makes the v2 editor tractable: the editor will consume `Core` directly and drive `Rendering` with an incrementally-reparsed AST.

---

## 4. Application Shell

### 4.1 Window layout

```
┌─────────────────────────────────────────────────────────────┐
│ ☰  [ README.md ×] [ NOTES.md ×] [ spec.md ×]           ─ □ × │  Title bar + tab strip
├──────────┬──────────────────────────────────────────────────┤
│          │  ┌────────────────────────────────────────────┐  │
│  Files   │  │ Find: [_______]  3 of 17   ∧ ∨   Aa  .*  × │  │  Find bar (toggleable)
│  ────    │  └────────────────────────────────────────────┘  │
│ ▾ docs   │                                                  │
│   spec   │        Rendered document                         │
│   notes  │        (virtualized scroll)                      │
│ ▾ src    │                                                  │
│          │                                                  │
├──────────┤                                                  │
│ Outline  │                                                  │
│  ────    │                                                  │
│ 1 Purpose│                                                  │
│ 2 Tech   │                                                  │
│  2.1 …   │                                                  │
└──────────┴──────────────────────────────────────────────────┘
```

- **Tab strip** is integrated into the title bar using Avalonia 12's themeable window decorations, falling back to a strip below a standard title bar where client-side decoration is unavailable (some Linux compositors).
- **Left sidebar** hosts the file tree (§5.9) and the document outline (§5.8) in a vertical splitter. Either can be collapsed. The whole sidebar toggles with `Ctrl+B`.
- **Find bar** overlays the top of the document pane; it does not reflow the document.

### 4.2 Tabs

- Unlimited tabs; the strip scrolls horizontally when it overflows and offers a dropdown list of all open tabs.
- Middle-click closes. `Ctrl+W` closes, `Ctrl+Shift+T` reopens the last closed tab (stack of 10).
- `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle in most-recently-used order; `Ctrl+1`…`Ctrl+9` jump by position, `Ctrl+9` being the last tab.
- Drag to reorder. Drag out of the window is **not** supported in v1 (no tab tear-off).
- Tab titles use the file's base name; when two open tabs share a base name, both disambiguate with the nearest distinguishing parent directory (`docs/spec.md` vs `api/spec.md`).
- Tooltip shows the full path. A modified-on-disk indicator (§5.10) appears as a dot on the tab.
- Each tab owns its own scroll position, outline state, and find state.

### 4.3 Opening documents

| Route                                  | Behavior                                                                                                                                                   |
| -------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `File → Open` (`Ctrl+O`)               | Storage provider file picker, filtered to `.md`, `.markdown`, `.mdown`, `.mkd`, `.mdx`, `.txt`                                                             |
| `File → Open Folder` (`Ctrl+K Ctrl+O`) | Sets the workspace root for the file tree; does not open any document                                                                                      |
| Command line                           | `mdviewer file1.md file2.md` — each argument becomes a tab. Relative paths resolve against the invocation CWD.                                             |
| Drag and drop                          | Files onto the window open as tabs; a folder sets the workspace root                                                                                       |
| OS file association                    | Registered as a handler for `.md` on both platforms; opening a second file while running raises the existing window and adds a tab (single-instance, §7.3) |
| Internal link                          | A relative link to another Markdown file opens in a new tab (§5.6)                                                                                         |

**Where a newly opened document starts.** A document opened for the first time
opens at the top — a reader who has never seen it has no place to return to. A
document that has been read before opens at the reading position it was left at,
taken from its recent entry (§5.14); reopening an already-open tab simply selects
it, which restores that tab's own position. Positions are recorded when a tab is
left, when it is closed, and when the session is saved.

### 4.4 Keyboard map

Reading is a keyboard activity. The full map is normative.

| Key                                  | Action                                                                       |
| ------------------------------------ | ---------------------------------------------------------------------------- |
| `Ctrl+O` / `Ctrl+W` / `Ctrl+Shift+T` | Open / close tab / reopen closed tab                                         |
| `Ctrl+F` / `F3` / `Shift+F3`         | Find / next match / previous match                                           |
| `Ctrl+B`                             | Toggle sidebar                                                               |
| `Ctrl+Shift+O`                       | Focus outline, type to filter headings                                       |
| `Ctrl+P`                             | Quick-open; opens onto the recent documents before anything is typed (§5.14) |
| `Ctrl+Shift+W`                       | Close all tabs, revealing the start view                                     |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0`       | Zoom in / out / reset (per-tab, §5.11)                                       |
| `Ctrl+E`                             | Toggle preview / raw source (§5.15)                                          |
| `Ctrl+R` / `F5`                      | Reload the current document from disk                                        |
| `Alt+←` / `Alt+→`                    | Navigate back / forward within a tab's link history                          |
| `Space` / `Shift+Space`              | Page down / up                                                               |
| `↑` / `↓`                            | Scroll one line up / down                                                    |
| `Page Up` / `Page Down`              | Page up / down                                                               |
| `Home` / `End`                       | Document start / end                                                         |
| `Ctrl+A` / `Ctrl+C`                  | Select all / copy selection as Markdown source (§5.7)                        |
| `Ctrl+,`                             | Settings                                                                     |
| `F11`                                | Full screen                                                                  |
| `Ctrl+Shift+P`                       | Command palette                                                              |

---

## 5. Feature Specifications

### 5.1 Markdown dialect

The Markdig pipeline is configured with, and only with:

- CommonMark 0.31 core (Markdig's baseline)
- **Pipe tables** and **grid tables**
- **Task lists** (`- [ ]` / `- [x]`)
- **Strikethrough**, **subscript/superscript**, **inserted/marked** text (`++ins++`, `==mark==`)
- **Autolinks** (bare URLs and `<...>`)
- **Footnotes**
- **Definition lists**
- **Abbreviations**
- **YAML front matter** — parsed, not rendered as content; surfaced in a collapsible metadata header (§5.12)
- **Custom containers** (`::: note`) — rendered as callout boxes with a small set of recognized kinds: `note`, `tip`, `warning`, `danger`, `info`
- **GitHub alerts** (`> [!NOTE]`) — mapped onto the same callout renderer
- **Emphasis extras**, **smart punctuation** (configurable, default off)
- **Auto-identifiers** on headings, so `#anchor` links work

Explicitly not enabled: `Bootstrap`, `JiraLinks`, `SelfPipeline`, `Globalization`. Math is deferred (§9.2).

The pipeline is built once and shared; it is stateless and thread-safe for parsing.

### 5.2 Rendering pipeline

```
File bytes
   │  encoding detection (§5.10)
   ▼
Source text ────────────────────────────────┐
   │  Markdig.Parse (background thread)     │  retained: the source
   ▼                                        │  string is the single
Markdig AST (MarkdownDocument)              │  source of truth, and
   │  every node carries Span (byte offsets)│  the anchor for v2's
   ▼                                        │  editor scroll-sync
DocumentModel  ◄────────────────────────────┘
   ├── Blocks[]        flattened top-level block list
   ├── Outline         heading tree (§5.8)
   ├── LinkIndex       anchors and outgoing links
   └── SearchIndex     text runs with source offsets (§5.7)
   │
   │  MarkdownPresenter, on the UI thread, lazily
   ▼
Avalonia visual tree (virtualized, §6.2)
```

**Threading.** Reading and parsing happen on a thread-pool thread. Only visual-tree construction touches the UI thread. A parse that is superseded (rapid file changes) is cancelled via `CancellationToken` and its result discarded.

**Block renderers.** One class per Markdig block type, resolved through a registry so renderers can be replaced or added without touching the presenter:

```csharp
public interface IBlockRenderer
{
    bool CanRender(Block block);
    Control Render(Block block, RenderContext context);
}
```

`RenderContext` carries the theme, the base directory for relative resources, the zoom factor, the link-activation callback, and the source-offset registrar. Renderers must register the source span of every control they create — this is what powers search highlighting, outline scroll-to, and the v2 editor's scroll sync.

**Inline renderers** follow the same shape but produce `Avalonia.Controls.Documents.Inline` objects appended to a `SelectableTextBlock`'s inline collection.

**Block coverage for v1:**

| Markdig block                   | Rendered as                                                                                        |
| ------------------------------- | -------------------------------------------------------------------------------------------------- |
| `HeadingBlock`                  | `SelectableTextBlock`, size/weight per level, with an anchor target and a hover-revealed link icon |
| `ParagraphBlock`                | `SelectableTextBlock` with inline content                                                          |
| `FencedCodeBlock` / `CodeBlock` | Code block control (§5.3)                                                                          |
| `QuoteBlock`                    | `Border` with a left accent bar, recursively rendered children; nests                              |
| `ListBlock` / `ListItemBlock`   | `Grid` per item (marker column + content column), so multi-line items align; nests                 |
| Task list items                 | Checkbox glyph, non-interactive in v1 (read-only)                                                  |
| `Table`                         | Custom table control (§5.4)                                                                        |
| `ThematicBreakBlock`            | `Separator`                                                                                        |
| `FootnoteGroup`                 | Rendered at document end with back-links                                                           |
| `CustomContainer` / alerts      | Callout `Border` with icon, title, accent color                                                    |
| `HtmlBlock`                     | See §5.6                                                                                           |
| `LinkReferenceDefinitionGroup`  | Not rendered (definitions only)                                                                    |

### 5.3 Code blocks and syntax highlighting

Fenced code blocks are highlighted with **TextMateSharp**, using its bundled grammar set and a theme pair matched to the app's light and dark themes.

Design points:

- **Tokenize, don't embed an editor.** AvaloniaEdit is a full text editor control; hosting one per code block is heavy and brings editing affordances we do not want in a viewer. Instead we call TextMateSharp's tokenizer directly and translate its scopes into styled `Run` objects inside a `SelectableTextBlock`. One lightweight control per block.
- **Grammar resolution** by info string first (` ```csharp `), then by file extension alias, then by a small alias table (`js`→JavaScript, `sh`/`bash`/`zsh`→shell, `yml`→YAML, `ps1`→PowerShell). Unknown languages render unhighlighted, not as an error.
- **Tokenization is off the UI thread** and is skipped entirely for blocks over a configurable size (default 200 KB), which render as plain monospace text with a small notice.
- **Chrome:** language label in the top-right corner, a copy button that copies the raw source (not the highlighted text), and optional line numbers (default off, per-language override in settings).
- **Long lines** scroll horizontally within the block by default; a soft-wrap toggle is available per block and as a global default.
- **Theme changes** re-tokenize lazily, only for blocks currently realized.

### 5.4 Tables

Tables are the hardest layout problem in the native approach and get an explicit design.

- Implemented as a custom `Grid`-derived control built from the Markdig `Table` AST, not a `DataGrid` — the content is arbitrary inline Markdown, not uniform data.
- **Column sizing:** measure-based auto-sizing with a proportional fallback. Columns size to content until the table exceeds the viewport width; then columns above a threshold width become star-sized proportionally to their measured width, so wide prose columns compress before narrow numeric ones.
- **Overflow:** when even proportional sizing leaves the table unreadable (more than ~8 columns, or a minimum column width violation), the table scrolls horizontally inside its own `ScrollViewer` with sticky first column and a subtle shadow edge indicating scrollability. The page itself never scrolls horizontally.
- **Alignment** per the delimiter row.
- **Header rows** are visually distinct and, in a scrolling table, stick to the top of the viewport while the table body passes under them.
- Cell content is fully rendered Markdown inlines, including code spans and links.
- Nested block content inside cells (lists, code blocks) is supported for grid tables; pipe tables cannot express it.

### 5.5 Diagrams

Mermaid rendering has no mature native .NET implementation. Rather than pretend otherwise, diagrams are a **pluggable, optional, out-of-process** feature.

```csharp
public interface IDiagramRenderer
{
    string Language { get; }              // "mermaid", "plantuml", "dot"
    bool IsAvailable { get; }
    Task<DiagramResult> RenderAsync(string source, DiagramOptions options, CancellationToken ct);
}
```

**v1 ships one renderer:** an external-process renderer that shells out to `mmdc` (mermaid-cli) if it is on `PATH` or configured in settings, producing PNG at the current DPI (or SVG once the SVG dependency in §2.2 is resolved).

**Graceful degradation is the rule, not the exception.** If no renderer is available, a ```` ```mermaid ```` block renders as a normal highlighted code block with an unobtrusive footer: *"Diagram rendering unavailable — configure a renderer in Settings."* The document is never broken by a missing optional dependency, and the app never auto-downloads or auto-installs anything.

**Caching.** Rendered diagrams are cached on disk keyed by `hash(source + theme + dpi)`, in the platform cache directory, with an LRU cap (default 200 MB). Re-opening a document with unchanged diagrams is instant.

**Sandboxing.** The external process runs with a timeout (default 20 s), no network access flags where the tool supports them, and its output is treated as untrusted image data. PlantUML's server mode is **not** used by default, since it would transmit document content to a remote host; if a user configures a server URL, the settings UI states this plainly.

### 5.6 Links, navigation, and HTML

**Link classification and behavior:**

| Link target                          | Behavior                                                                                                                                                      |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `#anchor`                            | Smooth-scroll to the heading with that auto-identifier, within the current tab; pushes onto the tab's history                                                 |
| Relative path to a Markdown file     | Opens in a **new tab**, resolved against the current document's directory. `Ctrl+click` opens in the background.                                              |
| Relative path with `#anchor`         | Opens the file and scrolls to the anchor                                                                                                                      |
| Relative path to a non-Markdown file | Opens with the OS default handler, after a confirmation dialog naming the file                                                                                |
| `http://` / `https://`               | Opens in the system browser. Confirmation prompt is off by default, on for links whose display text differs from the target host (a phishing-shape mismatch). |
| `mailto:`                            | System mail handler                                                                                                                                           |
| `file://`                            | Treated as an absolute local path, subject to the rules above                                                                                                 |
| Other schemes                        | Blocked, with a one-line explanation on click                                                                                                                 |

**Link history** is per tab, back/forward via `Alt+←` / `Alt+→` and mouse side buttons.

**Broken internal links** — a relative Markdown path that does not exist — are rendered in a muted style with a dotted underline and a tooltip, rather than looking like working links. Checking is lazy and cached; it does not block rendering.

**Raw HTML.** Inline and block HTML in Markdown is **not rendered as HTML** in v1. This is a deliberate security and complexity boundary: rendering arbitrary HTML in a native tree means reimplementing a browser badly, and rendering it in a WebView reintroduces the dependency we rejected in §2.1. Instead:

- A small allowlist is *interpreted*, since it is common in real documents and unambiguous: `<br>`, `<b>`, `<i>`, `<strong>`, `<em>`, `<code>`, `<kbd>`, `<mark>`, `<sub>`, `<sup>`, `<u>`, `<del>`, and `<img>` (subject to §5.13).
- Everything else renders as escaped, muted, monospace text, so the reader can see what is there.
- A per-document toggle in the view menu switches raw HTML blocks between "escaped" and "hidden".
- Comments (`<!-- ... -->`) are hidden by default, with a setting to show them.

**Images.** Local relative images load from disk; remote images are **not** fetched by default (a privacy default — a Markdown file should not phone home when you open it). Remote images show a placeholder with the alt text and a "Load images" affordance, per document, with a setting to always allow. Supported formats: PNG, JPEG, GIF (first frame in v1), WebP, BMP, and SVG once the dependency in §2.2 is resolved. Images scale down to fit the content width, never up past their natural size; click opens a zoomable overlay.

### 5.7 Text selection, search, and copy

**Selection across blocks** is the piece the native approach does not get for free. The design:

- Every rendered text-bearing control registers itself with a per-document `SelectionCoordinator`, along with its source span.
- The coordinator tracks a selection as a **source-offset range**, not a set of visual selections. Pointer drag maps hit-tested positions to source offsets; the coordinator then pushes the corresponding sub-range into each affected control's own selection.
- Because selection is expressed in source offsets, **copy is exact**: `Ctrl+C` copies the original Markdown source for the selected range. A second command, `Copy as plain text` (`Ctrl+Shift+C`), copies the rendered text without markup.
- `Ctrl+A` selects the whole document.
- Selection survives scrolling through virtualized content because it lives in the coordinator, not in the realized controls.

**In-document find** (`Ctrl+F`):

- Searches the source text, with match positions mapped back to visuals through the same offset registry.
- Options: case sensitivity, whole word, regular expression. Invalid regex shows inline, non-blocking feedback.
- Matches are highlighted in place; the current match uses a distinct color and is scrolled into view.
- A match-count indicator (`3 of 17`) and a marker gutter on the scrollbar showing all match positions in the full document, including unrealized parts.
- Search runs on a background thread with debounce; documents up to 10 MB search within one frame budget after the first keystroke settles.
- Find state is per tab and persists while the tab is open.

### 5.8 Outline

- Built from the heading tree at parse time; hierarchical, respecting heading levels including skips.
- Clicking a heading scrolls the document to it; the outline highlights the heading currently at the top of the viewport, updating as you scroll (throttled).
- Collapsible nodes; collapse state is per tab and persisted in the session.
- Type-to-filter (`Ctrl+Shift+O`): substring match over heading text, showing matched headings with their ancestors for context.
- Documents with no headings show an empty-state message rather than an empty panel.
- Heading anchors are also copyable: right-click a heading in the document or the outline to copy its `#anchor` link.

### 5.9 File tree and workspace

- One workspace root at a time, set by `Open Folder`, by dropping a folder, or by the parent directory of the first file opened from the command line.
- Lazy-loaded tree; directory contents are read on expand, not up front.
- Filters to Markdown files and directories by default; a toggle shows all files (non-Markdown files open with the OS handler).
- Respects `.gitignore` when present at the root, and always hides `.git`, `node_modules`, `bin`, `obj`, and dotfiles — all overridable in settings.
- Live updates via the same watcher infrastructure as §5.10, debounced.
- `Ctrl+P` quick-open: fuzzy filename match across the workspace, with a background index built on open and maintained by the watcher. Capped at 50,000 files; beyond that, quick-open falls back to prefix search and says so.
- Every document opens in its own tab. A single click in the tree opens the document; double click is equivalent. Clicking a document that is already open focuses its existing tab. There are no preview (transient) tabs — a viewer is not an editor, and clicking through a folder to read must not close the previous document.

### 5.10 File loading, watching, and encoding

**Encoding detection**, in order: BOM (UTF-8, UTF-16 LE/BE, UTF-32); then a UTF-8 validity scan; then the system ANSI codepage on Windows / UTF-8 on Linux as a last resort. Detected encoding is shown in the status bar and can be overridden per document, which triggers a reload.

**Line endings** are normalized for rendering; the original is preserved in the retained source string so that v2's editor writes back faithfully.

**File watching** uses `FileSystemWatcher` with these deliberate accommodations:

- Changes are debounced by 150 ms, because editors commonly write a file as a delete-and-rename sequence that fires several events.
- After a change, the file is retried up to 5 times over 500 ms if it is locked, before reporting an error — a common Windows race with editors that hold a write handle briefly.
- On reload, **scroll position is preserved by source offset**, not by pixel: the topmost visible element's source offset is recorded, and after re-render the view scrolls to the element containing that offset. This keeps your place when a document changes above where you are reading.
- Default behavior is **auto-reload**. A setting switches to "notify only", which shows a dot on the tab and a reload bar instead.
- A deleted file leaves the tab open with its last content and a clear banner; a recreated file reloads it.
- Linux `inotify` watch limits are finite. The watcher tracks its own subscription count, and beyond a threshold (default 4,000) stops watching the workspace tree — keeping watches only on open documents — and surfaces a one-time explanatory notice rather than failing silently.

**Large files.** Above a configurable threshold (default 5 MB), the document opens in a reduced mode: parsing proceeds, but code highlighting and diagram rendering are disabled and a bar offers to enable them. Above 50 MB, the app asks for confirmation before opening.

### 5.11 Appearance

- **Themes:** Light, Dark, and Follow System (Avalonia's `PlatformSettings.ColorValues`, which reports the OS preference on both Windows and Linux desktops that publish it).
- The rendering theme is a resource dictionary of semantic tokens (heading colors, code background, quote accent, table borders, callout palettes, link colors) consumed by all renderers. Adding a theme means adding one dictionary, not touching renderers.
- **Typography settings:** body font family and size, monospace font family and size, line height, and content max-width (default 900 px at 100% zoom — long lines hurt reading; a setting allows full width).
- **Zoom** is per tab, from 50% to 300% in 10% steps, applied as a scale on the content's layout transform, with font sizes recalculated rather than pixel-scaled so text stays crisp. `Ctrl+scroll` zooms.
- **Reading width, margins, and paragraph spacing** are set in the theme, not hardcoded in renderers.
- A **focus mode** (`Ctrl+Shift+F`) hides all chrome and centers the content.

### 5.12 Front matter and document metadata

- YAML front matter is parsed and displayed as a collapsible card at the top of the document, showing recognized fields (`title`, `author`, `date`, `tags`, `description`) in a readable layout and the remainder as a key–value table.
- Collapsed by default; the collapse state is remembered per document.
- If a `title` field is present, the tab title uses it in preference to the filename, with the filename in the tooltip.
- A setting hides front matter entirely.

### 5.13 Session and settings persistence

**Session state** (restored on launch, default on):

- Open tabs in order, each with: absolute path, scroll position as a source offset, view mode (§5.15), zoom level, outline collapse state, and find state.
- Active tab, window bounds, window state, sidebar visibility and splitter positions, workspace root.
- Saved to the platform config directory (`%APPDATA%\MdViewer\session.json`, `$XDG_CONFIG_HOME/mdviewer/session.json`) on a 2-second debounce after any change, and on clean exit.
- Files that no longer exist at restore time are skipped silently; the app never blocks startup on a missing file.
- Writes are atomic (write-temp-then-rename) so a crash mid-write cannot corrupt the session.
- `--no-restore` on the command line skips restoration for one launch.

**Settings** live in `settings.json` alongside, with a versioned schema and forward-compatible parsing: unknown keys are preserved on write so that downgrading and upgrading do not destroy configuration.

### 5.14 Recent documents

The most recently opened documents are remembered across sessions. Where they are *shown* was a decision with two obvious answers and a better third one, so the reasoning is recorded.

**Not a node in the file tree.** The workspace tree is a location structure: every node maps to a path beneath the workspace root, and expanding it is navigation through a hierarchy. A recent list is a *query result*, and its entries frequently live outside the current workspace — a "Recent" node would have to display paths that do not belong under the root it hangs from. Beyond that, context menus, `.gitignore` filtering and future drag-and-drop all behave differently for the two kinds of node, which spreads type checks through a single control.

**Not a permanent sidebar panel either.** The sidebar already splits its height between the file tree and the outline; a third permanent section costs roughly 120 px of chrome that is useful for a few seconds after launch and dead space for the rest of the session. The deeper reason is that **the open tabs already are the recent list.** A separate list only earns its keep for documents that have been *closed*.

So recency surfaces in the three places where it is actually used:

1. **The start view — primary.** When no tabs are open, the document pane shows the recent documents as cards. This is the moment the need is real and the screen is free. The start view is not only an empty state: a **Home** affordance at the left of the tab strip (and `Ctrl+Home`) brings it back at any time, where it covers the document pane until the user opens something, picks a tab, or presses `Esc`. Its actions — open file, open folder, quick open — are therefore reachable during a session, not only before one starts. It lists the newest five at rest, with a *Show all* toggle for the rest of the history.
2. **Quick-open with an empty query — secondary.** `Ctrl+P` opens onto the recent list before anything is typed, and recent entries outrank equally-scored workspace files once a query is entered. This matches the muscle memory people bring from code editors.
3. **A File ▸ Recent menu — tertiary.** For reopening a closed document while other tabs are open. Deferred until there is a menu bar to hang it from.

**Data model.** One entry is `{ Path, Title, LastOpenedUtc, ScrollOffset }`. `Path` is absolute and canonical and is the entry's identity. `Title` is the front-matter title when present (§5.12), otherwise the file name. `ScrollOffset` is a source offset, so reopening from the recent list restores the reading position rather than the top of the document.

**Rules.**

- Capped at 20. Most-recent-first; the start view shows the newest 5 until expanded.
- De-duplicated by path, compared case-insensitively on Windows and case-sensitively on Linux. Treating both file systems alike produces either duplicate entries on Windows or wrongly merged ones on Linux.
- Persisted in `session.json` (§5.13) and restored with it.
- **Existence is checked at display time, never at startup.** Startup must not block on the file system, so a missing file is shown as unavailable — visibly, in place — rather than silently dropped. The user learns that the file moved instead of watching an entry disappear.
- An unavailable entry is not openable; a context-menu action removes it, and a second action clears the whole list.
- The list is local and is never transmitted. It is covered by the privacy posture in §6.5 like everything else.

### 5.15 Preview and source

Every tab shows one of two views of the same document, switched with `Ctrl+E` or
the segmented control in the status bar.

**Preview is the default**, and stays the default for every newly opened tab.
This is a viewer: the rendered document is the product, and source is the
occasional detour — to check how something was written, to copy a snippet
verbatim, to see what a stray character actually is.

**Source is read-only.** It is not a half-built editor and does not write to the
file (§1.2). It shows the raw text in monospace with a line-number gutter, no
wrapping, and horizontal scrolling for long lines. The gutter is fixed rather
than scrolling with the text, because sideways scrolling is exactly when knowing
the line number matters.

**The reading position survives the switch.** Both views position by source
offset: the preview knows the offset of every block it rendered (§5.2), and the
source view maps offsets to lines through `SourceLineIndex`. On a switch the
outgoing view is asked where the reader is, and the incoming view is told. This
is the same mechanism that keeps your place across a reload (§5.10), and the
same one the v2 split editor will use for scroll sync.

**The mode is per tab**, alongside zoom and scroll position, and is persisted
with them in the session (§5.13). A tab left in source is still in source when
the session is restored.

Everything else follows the active view: the outline scrolls whichever view is
showing, find (M5) searches the same source text in both, and the status bar
reads the same document.

Split view — source and preview side by side with synchronised scrolling — is
v2 (§9.2). It is deliberately absent rather than stubbed: the groundwork is the
offset machinery above, so adding it means adding one enum member and one branch
in the shell.

---

## 6. Non-Functional Requirements

### 6.1 Performance targets

Measured on a 2020-class laptop (4 cores, SATA SSD), Release build, warm file cache.

| Scenario                                      | Target                                |
| --------------------------------------------- | ------------------------------------- |
| Cold start to first paint                     | < 1000 ms                             |
| Warm start to first paint                     | < 400 ms                              |
| Open a 100 KB document (parse + first screen) | < 200 ms                              |
| Open a 1 MB document                          | < 1200 ms                             |
| Scroll a 1 MB document                        | 60 fps sustained, no frame over 32 ms |
| Find in a 1 MB document                       | < 100 ms after debounce               |
| Tab switch                                    | < 50 ms                               |
| Theme switch                                  | < 200 ms, no re-parse                 |
| Idle memory, 5 documents of ~100 KB open      | < 250 MB working set                  |

These are requirements, not aspirations; §8 specifies the benchmarks that enforce them.

### 6.2 Virtualization

A 1 MB document can contain tens of thousands of blocks. Realizing all of them as controls is not viable.

- The document pane uses a virtualizing panel keyed on the top-level block list.
- **Height estimation** is the crux. Each block type provides a cheap estimator (line count × line height for paragraphs and code, measured height for images with known dimensions, a fixed estimate for tables). Estimates are replaced by measured heights as blocks realize, and the scrollbar extent is corrected — with the *scroll offset* adjusted in the same pass so the content under the pointer does not jump.
- Realized blocks outside the viewport by more than one screen are recycled; their state (code tokenization, image bitmaps, diagram renders) is cached separately and keyed by block identity, so re-realization is cheap.
- Selection and find highlighting live outside the visual tree (§5.7), so virtualization does not break them.

### 6.3 Accessibility

- All interactive elements are keyboard-reachable with a visible focus indicator; tab order follows reading order.
- Automation peers expose headings with their level, links with their target, images with their alt text, tables with row/column structure, and code blocks with their language — so a screen reader conveys document structure, not a flat text dump.
- Avalonia 12's native AT-SPI2 backend covers Linux screen readers; UI Automation covers Windows.
- Respects OS high-contrast settings and reduced-motion preferences (the latter disables smooth scrolling and animated transitions).
- No information is conveyed by color alone: broken links have a dotted underline as well as a muted color; the current find match has a border as well as a distinct fill.
- Minimum contrast of 4.5:1 for body text and 3:1 for large text and UI borders in both themes, verified in tests.

### 6.4 Reliability and error handling

- A renderer that throws must not take down the document. Each block render is guarded; a failure renders an inline error card naming the block type and source line, and the rest of the document renders normally.
- Unhandled exceptions are logged with context and shown in a report dialog that offers to copy the details; the app attempts to keep running.
- Logs are written to the platform log directory with size-based rotation (5 files × 5 MB). Log level is configurable; default is Warning.
- No crash reports, usage data, or any other information leave the machine. There is no telemetry, and this is a stated product property, not an oversight.

### 6.5 Security and privacy posture

Consolidating the decisions made above, because they form a coherent stance:

1. Remote images are not fetched without consent.
2. Raw HTML is not executed or rendered.
3. External diagram servers are opt-in and disclosed.
4. Non-Markdown file links and unusual URL schemes require confirmation.
5. Document content never leaves the machine.
6. External renderer processes are time-limited and their output treated as untrusted.
7. The app performs no update checks that transmit information; distribution is via package managers and manual download.

---

## 7. Platform Considerations

### 7.1 Windows

- Distribution: a self-contained single-file build and an MSIX package. Optionally an installer registering `.md` associations.
- Uses Avalonia's Win32 backend; Mica/acrylic backdrop where the OS supports it, with a solid fallback.
- Respects Windows dark mode, accent color, and text scaling.
- Jump list entries for recent documents.

### 7.2 Linux

- Distribution: AppImage (primary, no dependencies), Flatpak, and a `.deb`. A self-contained tarball for everything else.
- X11 and Wayland. Wayland support in Avalonia 12 is foundational and improving; the launcher defaults to X11 (via XWayland where necessary) with an environment variable to opt into the native Wayland backend, until it is proven on our target distributions.
- Desktop entry file with MIME association for `text/markdown`.
- Font fallback configured through Fontconfig, with an explicit fallback chain so CJK and emoji render on minimal systems.
- Flatpak's filesystem sandbox interacts with the file tree and watcher: the manifest requests `--filesystem=home` and the app degrades clearly when a path is not accessible rather than showing an empty tree.

### 7.3 Single instance

Both platforms use a single-instance model: a second launch passes its arguments to the running instance (named pipe on Windows, Unix domain socket in the runtime directory on Linux) and exits. A `--new-instance` flag opts out. If the IPC endpoint is stale (a previous crash), it is reclaimed rather than blocking launch.

---

## 8. Testing Strategy

| Layer             | Approach                                                                                                                                                                                                                                                                                               |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Core**          | Standard unit tests: pipeline configuration, outline extraction, search, offset mapping, encoding detection, session serialization round-trips.                                                                                                                                                        |
| **Rendering**     | Headless Avalonia. For each sample document, render and assert on the resulting visual tree's structure (types, text, source spans) — structural golden files, which are diffable and stable, rather than pixel comparisons.                                                                           |
| **Visual**        | A small set of pixel-golden screenshots for the theme tokens and table layout specifically, tolerant to small deltas, run on both platforms in CI. These catch theme regressions that structural tests cannot.                                                                                         |
| **Performance**   | BenchmarkDotNet for parse and render throughput; an automated scroll test measuring frame times against §6.1. Regressions over 10% fail the build.                                                                                                                                                     |
| **Accessibility** | Automated contrast-ratio checks over both theme dictionaries; automation-peer assertions for each block type; a manual screen-reader pass (NVDA on Windows, Orca on Linux) before each release.                                                                                                        |
| **Integration**   | File watching under realistic editor write patterns (atomic rename, truncate-and-write, rapid successive saves); single-instance argument passing; command-line handling.                                                                                                                              |
| **Corpus**        | `samples/` holds a torture-test set: CommonMark spec examples, deeply nested lists, wide and tall tables, very long lines, mixed RTL and CJK text, emoji, malformed Markdown, a 5 MB generated document, and documents exercising every extension in §5.1. Every sample must render without exception. |

CI runs the full matrix on Windows and Ubuntu.

---

## 9. Roadmap

### 9.1 v1 milestones

| #      | Milestone            | Contents                                                                                                   | Exit criterion                                        |
| ------ | -------------------- | ---------------------------------------------------------------------------------------------------------- | ----------------------------------------------------- |
| **M1** | Skeleton             | Solution structure, DI, window shell, theme infrastructure, tab strip with placeholder content             | Opens, tabs work, themes switch                       |
| **M2** | Core rendering       | Markdig pipeline, block and inline renderers for headings, paragraphs, lists, quotes, rules, links, images | CommonMark corpus renders without exception           |
| **M3** | Code and tables      | TextMateSharp highlighting, code block chrome, table control with sizing and overflow                      | Torture-test tables and 20 languages render correctly |
| **M4** | Navigation           | Outline, anchors, link classification and handling, link history, quick-open                               | Full keyboard navigation of a multi-file document set |
| **M5** | Selection and search | SelectionCoordinator, cross-block selection, copy-as-source, find bar, scrollbar markers                   | §5.7 fully met                                        |
| **M6** | Files and session    | File tree, watcher, encoding detection, session restore, recent documents (§5.14), settings UI             | Close and reopen restores exactly                     |
| **M7** | Performance          | Virtualization, height estimation, caches, benchmark suite                                                 | All §6.1 targets met                                  |
| **M8** | Polish and ship      | Diagrams, front matter, accessibility pass, packaging for both platforms                                   | Installable artifacts, accessibility pass clean       |

Diagrams (§5.5) sit in M8 deliberately: they depend on an external tool and an unresolved package question, and nothing else depends on them.

### 9.2 Deferred to v2 and beyond

- **Split-pane editor** with live preview and bidirectional scroll sync. The groundwork is laid: retained source text, source spans on every visual, offset-based selection and scroll positioning. What remains is an editor control (AvaloniaEdit), incremental reparsing, and a save pipeline.
- **Math rendering** (LaTeX via a native typesetter or an out-of-process renderer on the §5.5 model).
- **Export** to PDF and standalone HTML.
- **Printing.**
- **Multi-document search** across the workspace.
- **PlantUML and Graphviz** renderers on the §5.5 interface.
- **macOS** support.
- **Extensibility**: a plugin surface for custom block renderers, once the internal renderer interfaces have stabilized through real use.

---

## 10. Open Questions

1. **SVG dependency** (§2.2) — does an Avalonia 12–compatible `Avalonia.Svg.Skia` land before M8, or do we ship PNG rasterization?
2. **Wayland default** (§7.2) — at what point does the native Wayland backend become the default rather than opt-in? Proposed: when it passes the full test matrix on Fedora and Ubuntu LTS.
3. **Table sticky headers** (§5.4) — worth the complexity in v1, or defer? Proposed: implement in M3 only if it does not threaten the M3 exit criterion.
4. **Preview tabs** (§5.9) — *Settled: off.* Single-click preview is familiar from code editors but the wrong default for a viewer; every document now opens in its own tab, with no setting.
5. **Smart punctuation default** (§5.1) — off is safer for technical documents; on reads better for prose. Proposed: off, revisit after use.
6. **Zoom persistence scope** — per tab (as specified) or global? Per tab is more flexible but harder to reason about. Proposed: per tab, with the last-used value as the default for new tabs.

---

## Appendix A — Verified Package Versions

Checked against the NuGet flat-container index on 2026-09-10.

- Avalonia — latest stable **12.1.2**; the 11.3.x line remains maintained (11.3.21)
- Avalonia 12 released 2026-04-07; targets .NET 10 and SkiaSharp 3; adds native AT-SPI2 accessibility on Linux, themeable window decorations, and foundational Wayland support
- Markdig — **1.3.2**
- Avalonia.AvaloniaEdit — **12.0.0** (relevant for v2's editor)
- TextMateSharp — **2.0.4**
- CommunityToolkit.Mvvm — **8.4.2**
- Avalonia.Svg.Skia — **11.3.0**, no Avalonia 12 build yet (see §2.2)
