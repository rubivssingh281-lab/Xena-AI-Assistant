using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;

namespace Xena.Desktop;

/// <summary>
/// VS Code-style workspace: explorer, tabbed editor you can type in, terminal, and
/// Xena as a side panel. Xena proposes precise edits (hunks); nothing changes in a
/// file until the operator accepts that hunk.
/// </summary>
public partial class CodeAgentWindow : Window
{
    private readonly System.Func<PythonBridge?> _bridge;   // looked up per request: the backend may still be starting
    private readonly CodeAgentSettings _settings = CodeAgentSettings.Load();
    private readonly List<EditorTab> _tabs = new();
    private readonly List<(string Role, string Content)> _history = new();
    private readonly ChangedLinesRenderer _changedRenderer = new();
    private readonly StringBuilder _terminalPending = new();
    private readonly StringBuilder _runLog = new();
    private readonly List<string> _commandHistory = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _agentClock = new();
    private string? _workspace;
    private EditorTab? _active;
    private Process? _process;
    private string? _runFile;
    private string _runLabel = "";
    private DateTime _runStarted;
    private bool _agentBusy;
    private double _terminalHeight = 220;
    private int _historyIndex = -1;
    private string? _statusMessage;
    private DateTime _statusUntil;

    private static readonly HashSet<string> SkipDirs = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "__pycache__", "venv", ".venv", "dist", "build", "target",
        "out", "packages", ".git", ".idea", ".vs", ".next", ".gradle", ".cache",
    };

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new();
    private static SolidColorBrush B(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            BrushCache[hex] = brush;
        }
        return brush;
    }
    private static readonly FontFamily Ui = new("Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly FontFamily Icons = new("Segoe MDL2 Assets");
    private static SolidColorBrush TextBrush => B("#D7E3EE");
    private static SolidColorBrush Muted => B("#718398");
    private static SolidColorBrush Cyan => B("#19D7DF");
    private static SolidColorBrush Orange => B("#FF6A18");
    private static SolidColorBrush Green => B("#29D19B");
    private static SolidColorBrush Red => B("#E86B60");
    private static SolidColorBrush LineBrush => B("#1D3244");

    internal CodeAgentWindow(System.Func<PythonBridge?> bridge)
    {
        InitializeComponent();
        _bridge = bridge;
        _settings.Window?.ApplyTo(this);
        ApplyLayout();
        SetupEditor();
        SetupEditorExtras();
        AutoSave.IsChecked = _settings.AutoSave;
        IncludeOpenFiles.IsChecked = _settings.IncludeOpenFiles;
        IncludeOpenFiles.Checked += (_, _) => UpdateContext();
        IncludeOpenFiles.Unchecked += (_, _) => UpdateContext();

        _uiTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(60) };
        _uiTimer.Tick += (_, _) => { FlushTerminal(); TickStatus(); };
        _uiTimer.Start();

        PreviewKeyDown += Window_PreviewKeyDown;
        PreviewDragOver += Window_PreviewDragOver;
        PreviewDrop += Window_PreviewDrop;
        Closing += Window_Closing;
        Activated += (_, _) => ReloadChangedFiles();
        StyleViewTabs(explorer: true);
        SetTerminalCwd(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
        RefreshWelcome();
        Loaded += (_, _) =>
        {
            if (_settings.Workspace is { } ws && Directory.Exists(ws)) OpenWorkspace(ws);
            AddXenaNote("Hi! Open a file and tell me what you need: I can change, fix, explain or create code. " +
                        "Every change I suggest appears here as a small diff for you to accept or reject.");
            UpdateStatus();
            UpdateContext();
        };
    }

    /// <summary>Panel sizes and terminal visibility from last time.</summary>
    private void ApplyLayout()
    {
        SidebarColumn.Width = new GridLength(System.Math.Clamp(_settings.SidebarWidth, 150, 600));
        ChatColumn.Width = new GridLength(System.Math.Clamp(_settings.ChatWidth, 280, 800));
        _terminalHeight = System.Math.Clamp(_settings.TerminalHeight, 80, 700);
        TerminalRow.Height = new GridLength(_terminalHeight);
        if (!_settings.TerminalVisible) ToggleTerminal(false);
    }

    private void SaveLayout()
    {
        _settings.Window = WindowBounds.Capture(this);
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        _settings.ChatWidth = ChatColumn.ActualWidth;
        _settings.TerminalVisible = TerminalPanel.Visibility == Visibility.Visible;
        _settings.TerminalHeight = _settings.TerminalVisible && TerminalRow.ActualHeight > 40 ? TerminalRow.ActualHeight : _terminalHeight;
    }

    // ═════════════════════════════════ editor setup ═════════════════════════════════

    private void SetupEditor()
    {
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.AllowScrollBelowDocument = true;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x19, 0x6C, 0x8F));
        Editor.TextArea.SelectionForeground = null;   // keep the syntax colours inside a selection
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.Caret.CaretBrush = Cyan;
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x28, 0x3A, 0x5A, 0x74));
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x5A, 0x74)), 1);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_changedRenderer);
        var search = SearchPanel.Install(Editor);   // Ctrl+F / F3
        search.MarkerBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0x6A, 0x18));
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorStatus();
        Editor.TextArea.SelectionChanged += (_, _) => { UpdateCursorStatus(); UpdateContext(); };
        Editor.ContextMenu = BuildEditorMenu();
    }

    private ContextMenu BuildEditorMenu()
    {
        var menu = new ContextMenu();
        void Ask(string header, string instruction)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => QuickAsk(instruction);
            menu.Items.Add(item);
        }
        Ask("Xena: explain this", "Explain what this code does, briefly.");
        Ask("Xena: find and fix bugs", "Find and fix any bugs in this code.");
        Ask("Xena: refactor for readability", "Refactor this code to be cleaner and more readable without changing its behaviour.");
        Ask("Xena: add comments", "Add clear, concise comments to this code.");
        Ask("Xena: add documentation", "Add documentation comments (docstrings) to the functions and classes in this code.");
        Ask("Xena: write unit tests", "Write unit tests for this code in a new test file.");
        menu.Items.Add(new Separator());
        void Command(string header, string gesture, System.Action run)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Command("Go to Line…", "Ctrl+G", () => OpenPalette(":"));
        Command("Toggle Line Comment", "Ctrl+/", ToggleComment);
        Command("Toggle Fold", "Ctrl+Shift+[", ToggleFoldAtCaret);
        Command("Find in All Files", "Ctrl+Shift+F", ShowSearchView);
        Command("All Commands…", "Ctrl+Shift+P", () => OpenPalette(">"));
        menu.Items.Add(new Separator());
        foreach (var (header, command) in new (string, RoutedUICommand)[]
                 {
                     ("Undo", ApplicationCommands.Undo), ("Redo", ApplicationCommands.Redo),
                     ("Cut", ApplicationCommands.Cut), ("Copy", ApplicationCommands.Copy),
                     ("Paste", ApplicationCommands.Paste), ("Select All", ApplicationCommands.SelectAll),
                 })
        {
            menu.Items.Add(new MenuItem { Header = header, Command = command, CommandTarget = Editor.TextArea });
        }
        return menu;
    }

    private void QuickAsk(string instruction)
    {
        if (_active == null) { Status("Open a file first."); return; }
        var selected = !Editor.TextArea.Selection.IsEmpty;
        _ = SendAgentRequest(selected ? instruction + " Focus on the selected lines." : instruction);
    }

    // ═════════════════════════════════ workspace & explorer ═════════════════════════════════

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => PickWorkspace();

    private void PickWorkspace()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open a project folder" };
        if (_workspace != null) dialog.InitialDirectory = _workspace;
        if (dialog.ShowDialog(this) == true) OpenWorkspace(dialog.FolderName);
    }

    private void OpenWorkspace(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (_workspace != null && SamePath(_workspace, full))
        {
            ShowExplorerView();
            return;
        }
        if (_workspace != null)
        {
            // Switching projects: remember this one's open files, then close them.
            SaveSession();
            if (!CloseTabs(_tabs.ToList())) return;
        }
        _workspace = full;
        _settings.Workspace = _workspace;
        AddRecentFolder(_workspace);
        _settings.Save();
        _fileCache = null;
        WorkspaceText.Text = _workspace;
        WorkspaceText.ToolTip = _workspace;
        Title = $"XENA // CODE AGENT";
        ExplorerHint.Visibility = Visibility.Collapsed;
        LoadExplorer();
        WatchWorkspace();
        SetTerminalCwd(_workspace);
        RestoreSession();
        RefreshWelcome();
        UpdateStatus();
    }

    private void LoadExplorer(HashSet<string>? expanded = null)
    {
        Explorer.Items.Clear();
        if (_workspace == null) return;
        var name = Path.GetFileName(_workspace.TrimEnd('\\', '/'));
        var root = MakeFolderItem(_workspace, string.IsNullOrEmpty(name) ? _workspace : name.ToUpperInvariant());
        Explorer.Items.Add(root);
        root.IsExpanded = true;
        if (expanded != null) RestoreExpanded(root, expanded);
    }

    private static void RestoreExpanded(TreeViewItem item, HashSet<string> expanded)
    {
        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            if (child.Tag is string dir && Directory.Exists(dir) && expanded.Contains(dir))
            {
                child.IsExpanded = true;
                RestoreExpanded(child, expanded);
            }
        }
    }

    private static void CollectExpanded(ItemCollection items, HashSet<string> into)
    {
        foreach (var child in items.OfType<TreeViewItem>())
        {
            if (child.IsExpanded && child.Tag is string dir) into.Add(dir);
            CollectExpanded(child.Items, into);
        }
    }

    /// <summary>Reloads the tree, keeping open folders, the selection and the scroll position.</summary>
    private void RefreshExplorer()
    {
        var expanded = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        CollectExpanded(Explorer.Items, expanded);
        var selected = SelectedExplorerPath();
        var scroller = FindChild<ScrollViewer>(Explorer);
        var offset = scroller?.VerticalOffset ?? 0;
        LoadExplorer(expanded);
        if (selected != null && (File.Exists(selected) || Directory.Exists(selected))) RevealInExplorer(selected);
        if (scroller != null)
            Dispatcher.BeginInvoke(() => FindChild<ScrollViewer>(Explorer)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshExplorer();

    private TreeViewItem MakeFolderItem(string dir, string? label = null)
    {
        var item = new TreeViewItem
        {
            Header = ItemHeader(label ?? Path.GetFileName(dir), "", B("#C9A45C")),
            Tag = dir,
            Foreground = TextBrush,
        };
        AutomationProperties.SetName(item, label ?? Path.GetFileName(dir));
        item.Items.Add("…");   // placeholder: children load when the folder is first expanded
        item.Expanded += Folder_Expanded;
        return item;
    }

    private void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item || item.Tag is not string dir) return;
        e.Handled = true;
        if (item.Items.Count == 1 && item.Items[0] is string) FillFolder(item, dir);
    }

    private void FillFolder(TreeViewItem item, string dir)
    {
        item.Items.Clear();
        try
        {
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, System.StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                item.Items.Add(MakeFolderItem(sub));
            }
            foreach (var file in Directory.GetFiles(dir).OrderBy(Path.GetFileName, System.StringComparer.OrdinalIgnoreCase))
            {
                var fileItem = new TreeViewItem
                {
                    Header = ItemHeader(Path.GetFileName(file), "", FileColor(file)),
                    Tag = file,
                    Foreground = TextBrush,
                    ToolTip = file,
                };
                AutomationProperties.SetName(fileItem, Path.GetFileName(file));
                // Also reopen on click when it's already selected (e.g. after its tab was closed).
                fileItem.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && fileItem.IsSelected) OpenFile(file); };
                item.Items.Add(fileItem);
            }
        }
        catch (System.Exception ex)
        {
            item.Items.Add(new TreeViewItem { Header = "(can't read: " + ex.Message + ")", IsEnabled = false });
        }
    }

    private static StackPanel ItemHeader(string name, string glyph, Brush colour)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        panel.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = colour, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = name, FontFamily = Ui, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static Brush FileColor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" or ".pyw" => B("#4B9BD8"),
        ".js" or ".mjs" or ".cjs" or ".jsx" => B("#E8D44D"),
        ".ts" or ".tsx" => B("#3E8FE0"),
        ".cs" or ".csproj" or ".sln" => B("#A77BDB"),
        ".html" or ".htm" => B("#E44D26"),
        ".css" or ".scss" => B("#6FA8DC"),
        ".json" => B("#CBCB41"),
        ".md" => B("#519ABA"),
        ".xml" or ".xaml" or ".config" => B("#E37933"),
        ".java" or ".kt" => B("#E76F00"),
        ".c" or ".cpp" or ".h" or ".hpp" => B("#659AD2"),
        ".go" => B("#00ADD8"),
        ".rs" => B("#DEA584"),
        ".bat" or ".cmd" or ".ps1" or ".sh" => B("#89E051"),
        _ => B("#8A9BB0"),
    };

    private void Explorer_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_revealing) return;   // we selected it ourselves to show the active file
        if (e.NewValue is TreeViewItem { Tag: string path } && File.Exists(path)) OpenFile(path);
    }

    private string? SelectedExplorerPath() => (Explorer.SelectedItem as TreeViewItem)?.Tag as string;

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedExplorerPath() ?? _active?.Path ?? _workspace;
        if (path != null) RevealInShell(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedExplorerPath() ?? _active?.Path;
        if (path != null) Clipboard.SetText(path);
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace == null)
        {
            PickWorkspace();
            if (_workspace == null) return;
        }
        var baseDir = SelectedFolder();
        var suggested = _active != null ? "untitled" + Path.GetExtension(_active.Path) : "untitled.py";
        var name = Prompt("New file", $"File name (in {Shown(baseDir)}) — use a/b.py to create folders too:", suggested, "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var full = Path.GetFullPath(Path.Combine(baseDir, name.Trim()));
        if (!IsInside(full, _workspace))
        {
            MessageBox.Show(this, "New files must be inside the project folder.", "Xena Code Agent");
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) File.WriteAllText(full, "");
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the file:\n" + ex.Message, "Xena Code Agent");
            return;
        }
        RefreshExplorer();
        OpenFile(full);
    }

    // ═════════════════════════════════ tabs & files ═════════════════════════════════

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), System.StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return full.StartsWith(r, System.StringComparison.OrdinalIgnoreCase);
    }

    private EditorTab? OpenFile(string path, bool activate = true)
    {
        var full = Path.GetFullPath(path);
        var tab = _tabs.FirstOrDefault(t => SamePath(t.Path, full));
        if (tab == null)
        {
            if (!File.Exists(full)) return null;
            byte[] bytes;
            try
            {
                if (new FileInfo(full).Length > 8_000_000) { Status($"{Path.GetFileName(full)} is too large to open here."); return null; }
                bytes = File.ReadAllBytes(full);
            }
            catch (System.Exception ex) { Status("Couldn't open file: " + ex.Message); return null; }
            var decoded = Decode(bytes);
            if (decoded == null) { Status($"{Path.GetFileName(full)} looks like a binary file."); return null; }
            tab = CreateTab(full, decoded.Value.Text, decoded.Value.Encoding, isNew: false);
            tab.DiskTime = File.GetLastWriteTimeUtc(full);
        }
        RememberRecentFile(full);
        if (activate) Activate(tab);
        return tab;
    }

    private static (string Text, Encoding Encoding)? Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        if (bytes.Take(8000).Contains((byte)0)) return null;   // binary
        try { return (new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false)); }
        catch (DecoderFallbackException) { return (Encoding.Latin1.GetString(bytes), Encoding.Latin1); }
    }

    private EditorTab CreateTab(string fullPath, string text, Encoding encoding, bool isNew)
    {
        var doc = new TextDocument(text) { FileName = fullPath };
        var (useTabs, indentSize) = DetectIndentation(text);
        var tab = new EditorTab(fullPath, doc)
        {
            Highlighting = DarkHighlighting.For(fullPath),
            Encoding = encoding,
            IsNew = isNew,
            UseTabs = useTabs,
            IndentSize = indentSize,
        };
        BuildTabHeader(tab);
        doc.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) RefreshTab(tab);
        };
        doc.TextChanged += (_, _) => OnTabTextChanged(tab);
        _tabs.Add(tab);
        TabStrip.Children.Add(tab.Header);
        RefreshTab(tab);
        return tab;
    }

    private void BuildTabHeader(EditorTab tab)
    {
        var title = new TextBlock { Text = tab.FileName, FontFamily = Ui, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var dirty = new TextBlock { Text = "●", Foreground = Orange, FontSize = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Unsaved changes" };
        var close = new Button
        {
            Style = (Style)FindResource("ToolBtn"),
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Close (Ctrl+W)",
            Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 9 },
        };
        close.Click += (_, e) => { CloseTab(tab); e.Handled = true; };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = FileColor(tab.Path), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(title);
        panel.Children.Add(dirty);
        panel.Children.Add(close);
        var header = new Border
        {
            Child = panel,
            Padding = new Thickness(12, 0, 6, 0),
            Margin = new Thickness(0, 0, 1, 0),
            BorderThickness = new Thickness(0, 2, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tab.Path,
        };
        header.MouseLeftButtonDown += (_, _) => Activate(tab);
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        header.ContextMenu = BuildTabMenu(tab);
        AutomationProperties.SetName(header, tab.FileName);
        tab.Header = header;
        tab.Title = title;
        tab.DirtyMark = dirty;
    }

    private void RefreshTab(EditorTab tab)
    {
        var active = tab == _active;
        tab.Header.Background = active ? B("#070C14") : B("#0D1420");
        tab.Header.BorderBrush = active ? Cyan : Brushes.Transparent;
        tab.Title.Foreground = active ? TextBrush : Muted;
        tab.DirtyMark.Visibility = tab.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    private void Activate(EditorTab tab)
    {
        if (_active == tab) return;
        var previous = _active;
        if (previous != null)
        {
            previous.CaretOffset = Editor.CaretOffset;
            previous.ScrollX = Editor.HorizontalOffset;
            previous.ScrollY = Editor.VerticalOffset;
            RememberFolds(previous);
        }
        _active = tab;
        Editor.Document = tab.Document;
        Editor.SyntaxHighlighting = tab.Highlighting;
        _changedRenderer.Segments = tab.Changed;
        _problemRenderer.Problems = tab.Problems;
        _bracketRenderer.Pair = null;
        ApplyIndentation(tab);
        InstallFolding(tab);
        Editor.Visibility = Visibility.Visible;
        EditorEmpty.Visibility = Visibility.Collapsed;
        Editor.CaretOffset = System.Math.Min(tab.CaretOffset, tab.Document.TextLength);
        Dispatcher.BeginInvoke(() =>
        {
            Editor.ScrollToVerticalOffset(tab.ScrollY);
            Editor.ScrollToHorizontalOffset(tab.ScrollX);
            if (!PaletteOpen) Editor.TextArea.Focus();
        }, DispatcherPriority.Loaded);
        if (previous != null) RefreshTab(previous);
        RefreshTab(tab);
        tab.Header.BringIntoView();
        RememberRecentFile(tab.Path);
        RevealInExplorer(tab.Path);
        UpdateCursorStatus();
        UpdateContext();
        UpdateProblemsStatus();
        if (tab.CheckVersion == 0) _ = CheckProblemsAsync(tab);   // first look at this file
    }

    private bool CloseTab(EditorTab tab, bool prompt = true)
    {
        if (prompt && tab.IsDirty)
        {
            var answer = MessageBox.Show(this, $"Save changes to {tab.FileName}?", "Xena Code Agent",
                                         MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !Save(tab)) return false;
        }
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Header);
        if (!tab.IsNew) _closedTabs.Push(tab.Path);
        if (_active == tab)
        {
            _active = null;
            if (_tabs.Count > 0)
            {
                Activate(_tabs[System.Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                if (_folding != null) { ICSharpCode.AvalonEdit.Folding.FoldingManager.Uninstall(_folding); _folding = null; }
                Editor.Document = new TextDocument();
                Editor.Visibility = Visibility.Collapsed;
                EditorEmpty.Visibility = Visibility.Visible;
                _changedRenderer.Segments = null;
                _problemRenderer.Problems = new List<Problem>();
                StatusIndent.Content = "";
                RefreshWelcome();
                UpdateCursorStatus();
                UpdateContext();
                UpdateProblemsStatus();
            }
        }
        UpdateStatus();
        return true;
    }

    private bool Save(EditorTab tab, bool quiet = false)
    {
        try
        {
            TidyBeforeSave(tab);
            Directory.CreateDirectory(Path.GetDirectoryName(tab.Path)!);
            File.WriteAllText(tab.Path, tab.Document.Text, tab.Encoding);
            tab.DiskTime = File.GetLastWriteTimeUtc(tab.Path);
            var wasNew = tab.IsNew;
            tab.IsNew = false;
            tab.Document.UndoStack.MarkAsOriginalFile();
            RefreshTab(tab);
            if (wasNew) RefreshExplorer();
            if (!quiet) Status($"Saved {tab.FileName}");
            if (tab == _active && _settings.CheckProblems && CanCheck(tab.Path)) _ = CheckProblemsAsync(tab);
            return true;
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save {tab.FileName}:\n{ex.Message}", "Xena Code Agent", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Optional clean-ups on save (off by default, see the settings menu).</summary>
    private void TidyBeforeSave(EditorTab tab)
    {
        var doc = tab.Document;
        if (!_settings.TrimTrailingWhitespace && !_settings.InsertFinalNewline) return;
        doc.BeginUpdate();
        try
        {
            if (_settings.TrimTrailingWhitespace)
            {
                foreach (var line in doc.Lines.ToList())
                {
                    var text = doc.GetText(line);
                    var trimmed = text.TrimEnd(' ', '\t');
                    if (trimmed.Length != text.Length) doc.Remove(line.Offset + trimmed.Length, text.Length - trimmed.Length);
                }
            }
            if (_settings.InsertFinalNewline && doc.TextLength > 0 && doc.GetCharAt(doc.TextLength - 1) != '\n')
                doc.Insert(doc.TextLength, EolOf(doc));
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    /// <summary>Picks up changes made to open files outside this window (another editor,
    /// git, a script). Tabs with unsaved edits are left alone.</summary>
    private void ReloadChangedFiles()
    {
        foreach (var tab in _tabs.Where(t => !t.IsNew).ToList())
        {
            try
            {
                if (!File.Exists(tab.Path)) continue;
                var time = File.GetLastWriteTimeUtc(tab.Path);
                if (time == tab.DiskTime) continue;
                if (tab.IsDirty) { Status($"{tab.FileName} changed on disk. Save to overwrite, or close without saving to reload."); continue; }
                var decoded = Decode(File.ReadAllBytes(tab.Path));
                tab.DiskTime = time;
                if (decoded == null || decoded.Value.Text == tab.Document.Text) continue;
                tab.Document.Replace(0, tab.Document.TextLength, decoded.Value.Text);
                tab.Document.UndoStack.MarkAsOriginalFile();
                Status($"Reloaded {tab.FileName} (changed on disk)");
            }
            catch (IOException) { /* being written right now: try again next time */ }
        }
    }

    private void SaveAll()
    {
        foreach (var tab in _tabs.Where(t => t.IsDirty).ToList()) Save(tab);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { if (_active != null) Save(_active); }
    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();

    private string Rel(string fullPath)
    {
        if (_workspace != null && IsInside(fullPath, _workspace))
            return Path.GetRelativePath(_workspace, fullPath).Replace('\\', '/');
        return Path.GetFileName(fullPath);
    }

    private string? BaseFolder() => _workspace ?? (_active != null ? Path.GetDirectoryName(_active.Path) : null);

    private string? FullFromRel(string rel)
    {
        if (_active != null && Rel(_active.Path).Equals(rel, System.StringComparison.OrdinalIgnoreCase)) return _active.Path;
        var match = _tabs.FirstOrDefault(t => Rel(t.Path).Equals(rel, System.StringComparison.OrdinalIgnoreCase));
        if (match != null) return match.Path;
        var root = BaseFolder();
        return root == null ? null : Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    // ═════════════════════════════════ status bar ═════════════════════════════════

    private void Status(string message)
    {
        _statusMessage = message;
        _statusUntil = System.DateTime.Now.AddSeconds(4);
        UpdateStatus();
    }

    private void TickStatus()
    {
        if (_statusMessage != null && System.DateTime.Now > _statusUntil)
        {
            _statusMessage = null;
            UpdateStatus();
        }
        if (_agentBusy) UpdateAgentState();
    }

    private void UpdateStatus()
    {
        if (StatusLeft == null) return;
        var left = _workspace == null ? "No folder open" : Path.GetFileName(_workspace.TrimEnd('\\', '/'));
        var dirty = _tabs.Count(t => t.IsDirty);
        if (dirty > 0) left += $"    ● {dirty} unsaved";
        if (_process != null) left += $"    ▶ running {_runLabel}";
        StatusLeft.Text = _statusMessage ?? left;
        if (_active == null)
        {
            StatusLang.Text = StatusEnc.Text = "";
            StatusEol.Content = "";
        }
        else
        {
            StatusLang.Text = _active.Highlighting?.Name switch
            {
                null => "Plain Text",
                "MarkDown" or "MarkDownWithFontSize" => "Markdown",
                "TSQL" => "SQL",
                "Patch" => "Diff",
                "ASP/XHTML" => "HTML",
                var name => name,
            };
            var first = _active.Document.GetLineByNumber(1);
            StatusEol.Content = first.DelimiterLength == 2 ? "CRLF" : "LF";
            StatusEnc.Text = _active.Encoding is UTF8Encoding u
                ? (u.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8")
                : _active.Encoding.WebName.ToUpperInvariant();
        }
        UpdateAgentState();
    }

    private void UpdateCursorStatus()
    {
        if (_active == null) { StatusPos.Content = ""; return; }
        var caret = Editor.TextArea.Caret;
        var selected = Editor.SelectionLength;
        StatusPos.Content = $"Ln {caret.Line}, Col {caret.Column}" + (selected > 0 ? $"  ({selected} selected)" : "");
    }

    private void UpdateAgentState()
    {
        var text = _agentBusy ? $"working… {_agentClock.Elapsed.TotalSeconds:0}s" : "ready";
        AgentState.Text = text;
        StatusXena.Text = "Xena: " + text;
    }

    private void UpdateContext()
    {
        if (ContextText == null) return;
        if (_active == null) { ContextText.Text = "Context: no file open"; return; }
        var text = "Context: " + Rel(_active.Path);
        if (!Editor.TextArea.Selection.IsEmpty)
        {
            var a = Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber;
            var b = Editor.Document.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength).LineNumber;
            text += a == b ? $" · line {a} selected" : $" · lines {a}–{b} selected";
        }
        var others = _tabs.Count - 1;
        if (IncludeOpenFiles.IsChecked == true && others > 0) text += $" · +{others} open tab{(others == 1 ? "" : "s")}";
        ContextText.Text = text;
    }

    // ═════════════════════════════════ keyboard ═════════════════════════════════

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (PaletteOpen) return;   // the palette handles its own keys
        var inEditor = _active != null && Editor.TextArea.IsKeyboardFocusWithin;

        // ── anywhere ──
        if (ctrl && shift && key == Key.P) OpenPalette(">");
        else if (ctrl && !shift && key == Key.P) OpenPalette("");
        else if (ctrl && key == Key.G) { if (_active != null) OpenPalette(":"); }
        else if (ctrl && shift && key == Key.F) ShowSearchView();
        else if (ctrl && shift && key == Key.E) { ShowExplorerView(); Explorer.Focus(); }
        else if (ctrl && shift && key == Key.T) ReopenClosedTab();
        else if (ctrl && key == Key.S) { if (shift) SaveAll(); else if (_active != null) Save(_active); }
        else if (ctrl && key == Key.O) PickWorkspace();
        else if (ctrl && key == Key.N) NewFile_Click(this, new RoutedEventArgs());
        else if (ctrl && key == Key.W) { if (_active != null) CloseTab(_active); }
        else if (ctrl && key == Key.L) AgentInput.Focus();
        else if (ctrl && key == Key.Oem3) ToggleTerminal();
        else if (ctrl && key == Key.Tab) CycleTab(shift ? -1 : +1);
        else if (ctrl && !shift && key is Key.OemPlus or Key.Add) SetFontSize(Editor.FontSize + 1);
        else if (ctrl && !shift && key is Key.OemMinus or Key.Subtract) SetFontSize(Editor.FontSize - 1);
        else if (ctrl && !shift && key is Key.D0 or Key.NumPad0) SetFontSize(13.5);
        else if (alt && !ctrl && key == Key.Z) ToggleWordWrap();
        else if (key == Key.F5) { if (shift) StopProcess(); else RunActiveFile(); }
        // ── in the editor ──
        else if (inEditor && ctrl && !shift && key is Key.Oem2 or Key.Divide) ToggleComment();
        else if (inEditor && alt && !ctrl && !shift && key == Key.Up) MoveLines(-1);
        else if (inEditor && alt && !ctrl && !shift && key == Key.Down) MoveLines(+1);
        else if (inEditor && alt && shift && !ctrl && key == Key.Up) CopyLines(-1);
        else if (inEditor && alt && shift && !ctrl && key == Key.Down) CopyLines(+1);
        else if (inEditor && ctrl && shift && key == Key.K) DeleteLines();
        else if (inEditor && ctrl && key == Key.Enter) InsertLine(above: shift);
        else if (inEditor && ctrl && shift && key == Key.Oem4) ToggleFoldAtCaret();   // Ctrl+Shift+[
        else if (inEditor && ctrl && shift && key == Key.Oem6) ToggleFoldAtCaret();   // Ctrl+Shift+]
        else if (inEditor && ctrl && !shift && key == Key.Oem6) ShiftLines(indent: true);    // Ctrl+]
        else if (inEditor && ctrl && !shift && key == Key.Oem4) ShiftLines(indent: false);   // Ctrl+[
        else return;
        e.Handled = true;
    }

    // ═════════════════════════════════ terminal & run ═════════════════════════════════

    private void ToggleTerminal_Click(object sender, RoutedEventArgs e) => ToggleTerminal();

    private void ToggleTerminal(bool? show = null)
    {
        var visible = TerminalPanel.Visibility == Visibility.Visible;
        var target = show ?? !visible;
        if (target == visible) return;
        if (!target)
        {
            if (TerminalRow.ActualHeight > 40) _terminalHeight = TerminalRow.ActualHeight;
            TerminalRow.Height = new GridLength(0);
            TerminalSplitRow.Height = new GridLength(0);
            TerminalPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            TerminalPanel.Visibility = Visibility.Visible;
            TerminalSplitRow.Height = new GridLength(4);
            TerminalRow.Height = new GridLength(_terminalHeight);
        }
    }

    private void TerminalWrite(string text)
    {
        lock (_terminalPending) _terminalPending.Append(text);
    }

    private void FlushTerminal()
    {
        string text;
        lock (_terminalPending)
        {
            if (_terminalPending.Length == 0) return;
            text = _terminalPending.ToString();
            _terminalPending.Clear();
        }
        TerminalOutput.AppendText(text);
        if (TerminalOutput.Text.Length > 300_000)
            TerminalOutput.Text = TerminalOutput.Text[^200_000..];
        TerminalOutput.ScrollToEnd();
        lock (_runLog)
        {
            _runLog.Append(text);
            if (_runLog.Length > 16_000) _runLog.Remove(0, _runLog.Length - 12_000);
        }
    }

    private void ClearTerminal_Click(object sender, RoutedEventArgs e) => TerminalOutput.Clear();
    private void Run_Click(object sender, RoutedEventArgs e) => RunActiveFile();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopProcess();

    private void RunActiveFile()
    {
        if (_active == null) { Status("Open a file to run it."); return; }
        if (_active.IsDirty && !Save(_active)) return;
        var path = _active.Path;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".html" or ".htm")
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }
        var python = PythonFor(Path.GetDirectoryName(path)!);   // the project's .venv if it has one
        (string Exe, string Args)? command = ext switch
        {
            ".py" or ".pyw" => (python == "python" ? "python" : $"\"{python}\"", $"-u \"{path}\""),
            ".js" or ".mjs" or ".cjs" => ("node", $"\"{path}\""),
            ".ts" => ("npx", $"--yes tsx \"{path}\""),
            ".ps1" => ("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\""),
            ".bat" or ".cmd" => ("cmd.exe", $"/c \"{path}\""),
            ".rb" => ("ruby", $"\"{path}\""),
            ".go" => ("go", $"run \"{path}\""),
            ".java" => ("java", $"\"{path}\""),
            ".php" => ("php", $"\"{path}\""),
            _ => null,
        };
        if (command == null)
        {
            ToggleTerminal(true);
            TerminalWrite($"\nI don't know how to run {ext} files directly. Type a command in the terminal below instead.\n");
            return;
        }
        // Through cmd with 2>&1 so errors and normal output arrive in the order they were printed.
        var label = Path.GetFileName(path) + (ext is ".py" or ".pyw" && python != "python" ? $"  ({Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(python)))})" : "");
        StartProcess("cmd.exe", $"/s /c \"{command.Value.Exe} {command.Value.Args} 2>&1\"",
                     Path.GetDirectoryName(path)!, label, path);
    }

    private void StartProcess(string exe, string args, string workingDir, string label, string? runFile)
    {
        if (_process != null)
        {
            TerminalWrite("\nSomething is already running. Stop it first (Shift+F5).\n");
            return;
        }
        ToggleTerminal(true);
        FixBtn.Visibility = Visibility.Collapsed;
        FlushTerminal();
        lock (_runLog) _runLog.Clear();
        TerminalWrite($"\n❯ {label}\n");
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) TerminalWrite(e.Data + "\n"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) TerminalWrite(e.Data + "\n"); };
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() => OnProcessExited(process));
        try
        {
            process.Start();
        }
        catch (System.Exception ex)
        {
            TerminalWrite($"Couldn't start '{exe}': {ex.Message}. Is it installed and on your PATH?\n");
            return;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _runFile = runFile;
        _runLabel = label;
        _runStarted = System.DateTime.Now;
        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        TerminalState.Text = "running " + label;
        TerminalState.Foreground = Cyan;
        UpdateStatus();
    }

    private void OnProcessExited(Process process)
    {
        if (_process != process) return;
        try { process.WaitForExit(); } catch { /* already gone */ }
        var code = process.ExitCode;
        var seconds = (System.DateTime.Now - _runStarted).TotalSeconds;
        TerminalWrite($"\n[{_runLabel} finished with exit code {code} in {seconds:0.0}s]\n");
        _process = null;
        process.Dispose();
        RunBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        TerminalState.Text = code == 0 ? "finished" : $"failed (exit code {code})";
        TerminalState.Foreground = code == 0 ? Green : Red;
        if (code != 0 && _runFile != null) FixBtn.Visibility = Visibility.Visible;
        UpdateStatus();
    }

    private void StopProcess()
    {
        if (_process == null) return;
        try
        {
            _process.Kill(entireProcessTree: true);
            TerminalWrite("\n[stopped]\n");
        }
        catch { /* it already ended */ }
    }

    private void TerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && TerminalInput.SelectionLength == 0 && _process != null)
        {
            StopProcess();   // Ctrl+C, like a real terminal
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            if (_commandHistory.Count == 0) return;
            _historyIndex = e.Key == Key.Up
                ? System.Math.Max(0, (_historyIndex < 0 ? _commandHistory.Count : _historyIndex) - 1)
                : System.Math.Min(_commandHistory.Count, _historyIndex + 1);
            TerminalInput.Text = _historyIndex < _commandHistory.Count ? _commandHistory[_historyIndex] : "";
            TerminalInput.CaretIndex = TerminalInput.Text.Length;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = TerminalInput.Text;
        TerminalInput.Clear();
        if (_process != null)
        {
            try
            {
                _process.StandardInput.WriteLine(text);   // input for the running program
                TerminalWrite(text + "\n");
            }
            catch { /* program closed its input */ }
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        _commandHistory.Remove(text);
        _commandHistory.Add(text);
        _historyIndex = -1;
        if (RunBuiltin(text)) return;
        var cwd = _terminalCwd != null && Directory.Exists(_terminalCwd) ? _terminalCwd : BaseFolder() ?? System.Environment.CurrentDirectory;
        StartProcess("cmd.exe", $"/s /c \"{text} 2>&1\"", cwd, text, null);
    }

    private void FixWithXena_Click(object sender, RoutedEventArgs e)
    {
        if (_runFile == null) return;
        string log;
        lock (_runLog) log = _runLog.ToString();
        var tail = string.Join("\n", log.Split('\n').TakeLast(60)).Trim();
        OpenFile(_runFile);
        FixBtn.Visibility = Visibility.Collapsed;
        var name = Path.GetFileName(_runFile);
        var error = tail.Split('\n').Select(l => l.Trim())
                        .LastOrDefault(l => l.Length > 0 && !l.StartsWith('[') && !l.StartsWith('❯')) ?? "";
        _ = SendAgentRequest($"When I run {name} it fails with this output:\n{tail}\n\nFind the cause and fix it.",
                             display: $"⚡ Fix the error from running {name}" + (error.Length > 0 ? "\n" + error : ""));
    }

    // ═════════════════════════════════ Xena chat ═════════════════════════════════

    private sealed class ChatBubble
    {
        public required TextBox Body { get; init; }
        public required TextBlock Footer { get; init; }
        public required StackPanel Extra { get; init; }

        public void SetBody(string text, bool muted = false, bool error = false)
        {
            Body.Text = text;
            Body.Foreground = error ? Red : muted ? Muted : TextBrush;
        }

        public void SetFooter(string text)
        {
            Footer.Text = text;
            Footer.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static TextBox ReadOnlyText(string text, Brush foreground) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = foreground,
        FontFamily = Ui,
        FontSize = 12.5,
        Padding = new Thickness(0),
        Cursor = Cursors.IBeam,
    };

    private void AddUserBubble(string text)
    {
        ChatPanel.Children.Add(new Border
        {
            Background = B("#0C1E2A"),
            BorderBrush = B("#1F5E6B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(36, 8, 0, 2),
            Child = ReadOnlyText(text, TextBrush),
        });
        ScrollChat();
    }

    private ChatBubble AddXenaBubble()
    {
        var body = ReadOnlyText("", TextBrush);
        var footer = new TextBlock { FontFamily = Ui, FontSize = 11, FontStyle = FontStyles.Italic, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        var extra = new StackPanel();
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "XENA", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Orange, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(body);
        stack.Children.Add(footer);
        stack.Children.Add(extra);
        ChatPanel.Children.Add(new Border
        {
            Background = B("#0D1420"),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 8, 18, 2),
            Child = stack,
        });
        ScrollChat();
        return new ChatBubble { Body = body, Footer = footer, Extra = extra };
    }

    private void AddXenaNote(string text) => AddXenaBubble().SetBody(text, muted: true);

    private void ScrollChat() =>
        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private static string CleanText(string text) => text.Replace("**", "").Replace("`", "").Trim();

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy) return;
        _history.Clear();
        ChatPanel.Children.Clear();
        AddXenaNote("New conversation. What should we work on?");
    }

    private void AgentInput_TextChanged(object sender, TextChangedEventArgs e) =>
        AgentPlaceholder.Visibility = string.IsNullOrEmpty(AgentInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    private int _agentHistoryIndex = -1;

    private void AgentInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            SendFromInput();
        }
        else if (e.Key == Key.Escape && _agentBusy)
        {
            _agentCts?.Cancel();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && _settings.AgentHistory.Count > 0 &&
                 (AgentInput.Text.Length == 0 || _agentHistoryIndex >= 0) && !AgentInput.Text.Contains('\n'))
        {
            // Earlier requests, like a shell: Up/Down while the box is empty (or showing one).
            var count = _settings.AgentHistory.Count;
            _agentHistoryIndex = e.Key == Key.Up
                ? (_agentHistoryIndex < 0 ? count - 1 : System.Math.Max(0, _agentHistoryIndex - 1))
                : _agentHistoryIndex + 1;
            if (_agentHistoryIndex >= count) { _agentHistoryIndex = -1; AgentInput.Clear(); }
            else
            {
                AgentInput.Text = _settings.AgentHistory[_agentHistoryIndex];
                AgentInput.CaretIndex = AgentInput.Text.Length;
            }
            e.Handled = true;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy) _agentCts?.Cancel();   // the button is "Stop" while Xena works
        else SendFromInput();
    }

    private void SendFromInput()
    {
        var text = AgentInput.Text.Trim();
        if (text.Length == 0 || _agentBusy) return;
        AgentInput.Clear();
        _agentHistoryIndex = -1;
        _settings.AgentHistory.Remove(text);
        _settings.AgentHistory.Add(text);
        if (_settings.AgentHistory.Count > 50) _settings.AgentHistory.RemoveAt(0);
        _ = SendAgentRequest(text);
    }

    private Dictionary<string, object?> BuildRequest(string instruction)
    {
        object? active = null, selection = null;
        if (_active != null)
        {
            active = new { path = Rel(_active.Path), content = _active.Document.Text, cursor_line = Editor.TextArea.Caret.Line };
            if (!Editor.TextArea.Selection.IsEmpty)
            {
                selection = new
                {
                    start_line = Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber,
                    end_line = Editor.Document.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength).LineNumber,
                    text = Editor.SelectedText,
                };
            }
        }
        var others = new List<object>();
        var included = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (_active != null) included.Add(_active.Path);
        if (IncludeOpenFiles.IsChecked == true)
        {
            foreach (var t in _tabs.Where(t => t != _active))
            {
                others.Add(new { path = Rel(t.Path), content = t.Document.Text });
                included.Add(t.Path);
            }
        }
        // Automatic context: files the request names, and modules the current file imports.
        var projectFiles = WorkspaceFiles();
        _autoIncluded.Clear();
        foreach (var file in RelatedFiles(instruction, projectFiles))
        {
            if (!included.Add(file)) continue;
            var text = _tabs.FirstOrDefault(t => SamePath(t.Path, file))?.Document.Text ?? ReadSmallText(file);
            if (text == null) continue;
            others.Add(new { path = Rel(file), content = text });
            _autoIncluded.Add(Rel(file));
        }
        var history = _history.TakeLast(6).Select(h => new { role = h.Role, content = h.Content }).ToList();
        return new Dictionary<string, object?>
        {
            ["instruction"] = instruction,
            ["active"] = active,
            ["selection"] = selection,
            ["others"] = others,
            ["tree"] = projectFiles.Select(Rel).OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase).Take(300).ToList(),
            ["history"] = history,
        };
    }

    private readonly List<string> _autoIncluded = new();

    private static readonly HashSet<string> TextExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".cs", ".java", ".kt", ".go", ".rs", ".rb",
        ".php", ".c", ".cc", ".cpp", ".h", ".hpp", ".swift", ".vue", ".html", ".htm", ".css", ".scss", ".json",
        ".md", ".txt", ".xml", ".xaml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".sh", ".ps1", ".bat", ".cmd", ".sql",
    };

    /// <summary>Files in the project (skipping build output, dependencies and hidden folders).</summary>
    private List<string> WorkspaceFiles(int max = 600) =>
        _workspace == null ? new List<string>() : EnumerateProjectFiles(_workspace, max);

    private List<string> RelatedFiles(string instruction, List<string> projectFiles)
    {
        var words = new HashSet<string>(
            System.Text.RegularExpressions.Regex.Matches(instruction, @"[A-Za-z_][\w\-]*(?:\.[A-Za-z0-9]+)?")
                .Select(m => m.Value.ToLowerInvariant()));
        if (_active != null)
        {
            // Python "import x" / "from x import", JS/TS "require('./x')" / "from './x'".
            var source = _active.Document.Text;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         source, @"^\s*(?:from\s+([\w.]+)\s+import|import\s+([\w.]+))", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                var module = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Split('.').Last();
                if (module.Length > 0) words.Add(module.ToLowerInvariant());
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         source, @"(?:require\(\s*|from\s+|import\s+)['""](\.{1,2}/[^'""]+)['""]"))
            {
                words.Add(Path.GetFileNameWithoutExtension(m.Groups[1].Value).ToLowerInvariant());
            }
        }
        var found = new List<string>();
        foreach (var file in projectFiles)
        {
            if (_active != null && SamePath(file, _active.Path)) continue;
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;
            var name = Path.GetFileName(file).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (words.Contains(name) || (stem.Length >= 3 && words.Contains(stem))) found.Add(file);
            if (found.Count >= 4) break;
        }
        return found;
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 60_000) return null;
            return Decode(File.ReadAllBytes(path))?.Text;
        }
        catch (System.Exception) { return null; }
    }

    private async Task SendAgentRequest(string instruction, string? display = null)
    {
        if (_agentBusy) { Status("Xena is still working on the last request."); return; }
        var bridge = _bridge();
        if (bridge == null) { AddXenaNote("My backend is still starting up. Give it a few seconds and try again."); return; }
        _agentBusy = true;
        var cts = new System.Threading.CancellationTokenSource();
        _agentCts = cts;
        SendBtn.Content = "Stop ■";
        SendBtn.ToolTip = "Stop Xena (Esc)";
        SendBtn.Background = B("#BE2A2A");
        _agentClock.Restart();
        foreach (var t in _tabs) t.Changed.Clear();
        Editor.TextArea.TextView.Redraw();

        AddUserBubble(display ?? instruction);
        var bubble = AddXenaBubble();
        var request = JsonSerializer.Serialize(BuildRequest(instruction));
        bubble.SetBody(_autoIncluded.Count > 0
            ? $"Reading your code (also {string.Join(", ", _autoIncluded)})…"
            : "Reading your code…", muted: true);
        try
        {
            var result = await bridge.SendAsync("code_agent", new Dictionary<string, string> { ["request"] = request },
                partial =>
                {
                    if (partial.ValueKind != JsonValueKind.Object || cts.IsCancellationRequested) return;
                    if (partial.TryGetProperty("text", out var t) && t.GetString() is { Length: > 0 } text)
                        bubble.SetBody(CleanText(text));
                    if (partial.TryGetProperty("edits", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() > 0)
                        bubble.SetFooter($"✎ Writing {n.GetInt32()} change{(n.GetInt32() == 1 ? "" : "s")}…");
                    ScrollChat();
                },
                cts.Token);
            if (cts.IsCancellationRequested)
            {
                // Stopped part-way: keep what she'd said, drop any half-written edits.
                var soFar = bubble.Body.Text.StartsWith("Reading your code") ? "" : bubble.Body.Text.Trim();
                bubble.SetBody(soFar.Length > 0 ? soFar + "  [stopped]" : "Stopped.", muted: soFar.Length == 0);
                bubble.SetFooter("");
                return;
            }
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                bubble.SetBody(error.GetString() ?? "Something went wrong.", error: true);
                bubble.SetFooter("");
                return;
            }
            var message = CleanText(root.GetProperty("message").GetString() ?? "");
            bubble.SetBody(message);
            bubble.SetFooter("");
            _history.Add(("user", instruction));
            _history.Add(("assistant", message));
            ShowProposal(bubble, root);
        }
        catch (System.OperationCanceledException)
        {
            bubble.SetBody("Stopped before I started.", muted: true);
            bubble.SetFooter("");
        }
        catch (System.Exception ex)
        {
            bubble.SetBody("Error: " + ex.Message, error: true);
            bubble.SetFooter("");
        }
        finally
        {
            _agentBusy = false;
            _agentCts = null;
            SendBtn.Content = "Send ➤";
            SendBtn.ToolTip = "Send (Enter)";
            SendBtn.ClearValue(BackgroundProperty);
            _agentClock.Stop();
            UpdateAgentState();
            ScrollChat();
        }
    }

    private System.Threading.CancellationTokenSource? _agentCts;

    // ═════════════════════════════════ proposed changes ═════════════════════════════════

    private static List<string> Lines(JsonElement hunk, string name) =>
        hunk.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();

    private void ShowProposal(ChatBubble bubble, JsonElement root)
    {
        var hunks = new List<ProposedHunk>();
        var newFiles = new List<ProposedNewFile>();
        var panel = bubble.Extra;
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var rel = file.GetProperty("path").GetString() ?? "";
            var full = FullFromRel(rel);
            if (full == null) continue;
            if (file.GetProperty("new_file").GetBoolean())
            {
                var nf = new ProposedNewFile { FullPath = full, Content = file.GetProperty("content").GetString() ?? "" };
                newFiles.Add(nf);
                panel.Children.Add(NewFileCard(nf, rel));
                continue;
            }
            panel.Children.Add(FileHeading(rel, full, file.GetProperty("added").GetInt32(), file.GetProperty("removed").GetInt32()));
            foreach (var h in file.GetProperty("hunks").EnumerateArray())
            {
                var hunk = new ProposedHunk
                {
                    FullPath = full,
                    OldStart = h.GetProperty("old_start").GetInt32(),
                    Old = Lines(h, "old"),
                    New = Lines(h, "new"),
                    Before = Lines(h, "before"),
                    After = Lines(h, "after"),
                };
                hunks.Add(hunk);
                panel.Children.Add(HunkCard(hunk, hunks));
            }
        }

        var failed = root.TryGetProperty("failed", out var f) ? f.GetArrayLength() : 0;
        if (failed > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"⚠ {failed} of my edits didn't match the file, so I left {(failed == 1 ? "it" : "them")} out. " +
                       "Select the code you mean and ask again.",
                Foreground = Orange, FontFamily = Ui, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            });
        }
        var total = hunks.Count + newFiles.Count;
        if (total == 0) return;

        var acceptAll = SmallButton($"✓ Accept all ({total})", Green);
        var rejectAll = SmallButton("✗ Reject all", Red);
        acceptAll.Click += (_, _) => AcceptAll(hunks, newFiles);
        rejectAll.Click += (_, _) =>
        {
            foreach (var h in hunks.Where(h => h.State == HunkState.Pending)) MarkHunk(h, HunkState.Rejected, "rejected");
            foreach (var n in newFiles.Where(n => n.State == HunkState.Pending)) MarkNewFile(n, HunkState.Rejected, "discarded");
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(acceptAll);
        row.Children.Add(rejectAll);
        panel.Children.Add(row);
        void Sync()
        {
            var pending = hunks.Count(h => h.State == HunkState.Pending) + newFiles.Count(n => n.State == HunkState.Pending);
            row.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)acceptAll.Content).Text = $"✓ Accept all ({pending})";
        }
        foreach (var h in hunks) h.Resolved = Sync;
        foreach (var n in newFiles) n.Resolved = Sync;

        // Preview: jump to the first change so the operator sees where it lands.
        if (hunks.Count > 0) RevealHunk(hunks[0]);
        ScrollChat();
    }

    private Button SmallButton(string text, Brush colour)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolBtn"),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Content = new TextBlock { Text = text, FontFamily = Ui, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = colour },
        };
        return button;
    }

    private UIElement FileHeading(string rel, string full, int added, int removed)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, ToolTip = "Open " + rel };
        panel.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = FileColor(full), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = rel, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = $"  +{added}", Foreground = Green, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = $" −{removed}", Foreground = Red, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        panel.MouseLeftButtonDown += (_, _) => OpenFile(full);
        return panel;
    }

    private static UIElement DiffLines(IEnumerable<(string Prefix, string Text, Brush Fore, Brush? Back)> lines)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var (prefix, text, fore, back) in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = prefix + " " + text,
                FontFamily = Mono,
                FontSize = 11.5,
                Foreground = fore,
                Background = back ?? Brushes.Transparent,
                Padding = new Thickness(6, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        return stack;
    }

    private static IEnumerable<(string, string, Brush, Brush?)> Take(IEnumerable<string> lines, string prefix, Brush fore, Brush? back, int max)
    {
        var list = lines.ToList();
        foreach (var line in list.Take(max)) yield return (prefix, line, fore, back);
        if (list.Count > max) yield return (" ", $"… {list.Count - max} more line{(list.Count - max == 1 ? "" : "s")}", Muted, null);
    }

    private UIElement HunkCard(ProposedHunk hunk, List<ProposedHunk> siblings)
    {
        var where = new TextBlock
        {
            Text = hunk.Old.Count == 0 ? $"insert at line {hunk.OldStart + 1}" : hunk.Old.Count == 1 ? $"line {hunk.OldStart + 1}" : $"lines {hunk.OldStart + 1}–{hunk.OldStart + hunk.Old.Count}",
            FontFamily = Ui, FontSize = 11, Foreground = Cyan, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show in the editor",
        };
        where.MouseLeftButtonDown += (_, _) => RevealHunk(hunk);
        var state = new TextBlock { FontFamily = Ui, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var accept = SmallButton("✓ Accept", Green);
        var reject = SmallButton("✗ Reject", Red);
        accept.Click += (_, _) => AcceptHunk(hunk, siblings, save: AutoSave.IsChecked == true);
        reject.Click += (_, _) => MarkHunk(hunk, HunkState.Rejected, "rejected");
        hunk.AcceptButton = accept;
        hunk.RejectButton = reject;
        hunk.StateText = state;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(accept);
        buttons.Children.Add(reject);
        var head = new DockPanel { Margin = new Thickness(8, 5, 2, 3) };
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(where);
        head.Children.Add(state);

        var context = Muted;
        var diff = DiffLines(
            Take(hunk.Before.TakeLast(2), " ", context, null, 2)
                .Concat(Take(hunk.Old, "-", B("#F0A39B"), B("#3A1A1C"), 30))
                .Concat(Take(hunk.New, "+", B("#8FE6C4"), B("#123326"), 40))
                .Concat(Take(hunk.After.Take(2), " ", context, null, 2)));

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(diff);
        return new Border
        {
            Background = B("#070C14"),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 6, 0, 0),
            Child = stack,
        };
    }

    private UIElement NewFileCard(ProposedNewFile file, string rel)
    {
        var lines = file.Content.Replace("\r\n", "\n").Split('\n');
        var title = new TextBlock
        {
            Text = $"＋ new file  {rel}  (+{lines.Length})", FontFamily = Ui, FontSize = 11.5,
            FontWeight = FontWeights.SemiBold, Foreground = Green, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 0),
        };
        var state = new TextBlock { FontFamily = Ui, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var create = SmallButton("✓ Create", Green);
        var discard = SmallButton("✗ Discard", Red);
        create.Click += (_, _) => AcceptNewFile(file, save: AutoSave.IsChecked == true);
        discard.Click += (_, _) => MarkNewFile(file, HunkState.Rejected, "discarded");
        file.AcceptButton = create;
        file.RejectButton = discard;
        file.StateText = state;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(create);
        buttons.Children.Add(discard);
        // Title on its own line so a long path never collides with the buttons.
        var head = new DockPanel { Margin = new Thickness(8, 2, 2, 3) };
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(state);
        state.Margin = new Thickness(0);
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(head);
        stack.Children.Add(DiffLines(Take(lines, "+", B("#8FE6C4"), B("#123326"), 18)));
        return new Border
        {
            Background = B("#070C14"), BorderBrush = LineBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 8, 0, 0), Child = stack,
        };
    }

    private void MarkHunk(ProposedHunk hunk, HunkState state, string text)
    {
        hunk.State = state;
        SetCardState(hunk.StateText, hunk.AcceptButton, hunk.RejectButton, state, text);
        hunk.Resolved?.Invoke();
    }

    private void MarkNewFile(ProposedNewFile file, HunkState state, string text)
    {
        file.State = state;
        SetCardState(file.StateText, file.AcceptButton, file.RejectButton, state, text);
        file.Resolved?.Invoke();
    }

    private static void SetCardState(TextBlock? label, Button? accept, Button? reject, HunkState state, string text)
    {
        if (label != null)
        {
            label.Text = (state == HunkState.Accepted ? "✓ " : state == HunkState.Failed ? "⚠ " : "✗ ") + text;
            label.Foreground = state == HunkState.Accepted ? Green : state == HunkState.Failed ? Orange : Muted;
        }
        if (accept != null) accept.Visibility = Visibility.Collapsed;
        if (reject != null) reject.Visibility = Visibility.Collapsed;
    }

    /// <summary>Where the hunk's original lines are now (the file may have been edited
    /// since Xena looked at it). Searches outwards from the expected line.</summary>
    private static int LocateHunk(TextDocument doc, ProposedHunk h)
    {
        var count = doc.LineCount;
        string LineAt(int i) => doc.GetText(doc.GetLineByNumber(i + 1)).TrimEnd();

        bool Matches(int at)
        {
            if (at < 0 || at > count) return false;
            if (h.Old.Count > 0)
            {
                if (at + h.Old.Count > count) return false;
                for (var k = 0; k < h.Old.Count; k++)
                    if (LineAt(at + k) != h.Old[k].TrimEnd()) return false;
                return true;
            }
            if (h.Before.Count + h.After.Count == 0) return count <= 1;   // insert into an empty file
            for (var k = 0; k < h.Before.Count; k++)
            {
                var i = at - h.Before.Count + k;
                if (i < 0 || i >= count || LineAt(i) != h.Before[k].TrimEnd()) return false;
            }
            for (var k = 0; k < h.After.Count; k++)
            {
                var i = at + k;
                if (i >= count || LineAt(i) != h.After[k].TrimEnd()) return false;
            }
            return true;
        }

        var expected = System.Math.Clamp(h.OldStart + h.Shift, 0, count);
        for (var d = 0; d <= count; d++)
        {
            if (Matches(expected - d)) return expected - d;
            if (d > 0 && Matches(expected + d)) return expected + d;
        }
        return -1;
    }

    private static string EolOf(TextDocument doc)
    {
        foreach (var line in doc.Lines)
            if (line.DelimiterLength > 0) return doc.GetText(line.EndOffset, line.DelimiterLength);
        return System.Environment.NewLine;
    }

    private bool AcceptHunk(ProposedHunk hunk, List<ProposedHunk> siblings, bool save)
    {
        if (hunk.State != HunkState.Pending) return false;
        var tab = OpenFile(hunk.FullPath);
        if (tab == null) { MarkHunk(hunk, HunkState.Failed, "file not found"); return false; }
        var doc = tab.Document;
        var at = LocateHunk(doc, hunk);
        if (at < 0) { MarkHunk(hunk, HunkState.Failed, "the file changed here, ask again"); return false; }

        var eol = EolOf(doc);
        int start;
        string text;
        doc.BeginUpdate();   // one undo step
        try
        {
            if (hunk.Old.Count > 0)
            {
                var first = doc.GetLineByNumber(at + 1);
                var last = doc.GetLineByNumber(at + hunk.Old.Count);
                start = first.Offset;
                var end = last.EndOffset + last.DelimiterLength;
                text = hunk.New.Count == 0 ? "" : string.Join(eol, hunk.New) + (last.DelimiterLength > 0 ? eol : "");
                doc.Replace(start, end - start, text);
            }
            else if (at < doc.LineCount)
            {
                start = doc.GetLineByNumber(at + 1).Offset;
                text = string.Join(eol, hunk.New) + eol;
                doc.Insert(start, text);
            }
            else
            {
                start = doc.TextLength;
                text = (doc.TextLength > 0 ? eol : "") + string.Join(eol, hunk.New);
                doc.Insert(start, text);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
        if (text.Length > 0) tab.Changed.Add(new TextSegment { StartOffset = start, Length = text.Length });
        Editor.TextArea.TextView.Redraw();

        var delta = hunk.New.Count - hunk.Old.Count;
        foreach (var other in siblings)
            if (other != hunk && other.State == HunkState.Pending && SamePath(other.FullPath, hunk.FullPath) && other.OldStart > hunk.OldStart)
                other.Shift += delta;
        MarkHunk(hunk, HunkState.Accepted, save ? "applied and saved" : "applied (unsaved)");
        if (save) Save(tab);
        Editor.ScrollTo(doc.GetLineByOffset(start).LineNumber, 1);
        return true;
    }

    private void AcceptNewFile(ProposedNewFile file, bool save)
    {
        if (file.State != HunkState.Pending) return;
        var root = BaseFolder();
        if (root == null || !IsInside(file.FullPath, root))
        {
            MarkNewFile(file, HunkState.Failed, "outside the project folder");
            return;
        }
        var content = file.Content;
        EditorTab tab;
        if (File.Exists(file.FullPath))
        {
            tab = OpenFile(file.FullPath)!;
            if (tab == null) { MarkNewFile(file, HunkState.Failed, "couldn't open the existing file"); return; }
            tab.Document.Replace(0, tab.Document.TextLength, content);
        }
        else
        {
            tab = _tabs.FirstOrDefault(t => SamePath(t.Path, file.FullPath))
                  ?? CreateTab(file.FullPath, content, new UTF8Encoding(false), isNew: true);
            Activate(tab);
        }
        tab.Changed.Add(new TextSegment { StartOffset = 0, Length = tab.Document.TextLength });
        Editor.TextArea.TextView.Redraw();
        MarkNewFile(file, HunkState.Accepted, save ? "created and saved" : "created (unsaved)");
        if (save) Save(tab);
    }

    private void AcceptAll(List<ProposedHunk> hunks, List<ProposedNewFile> newFiles)
    {
        var touched = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        // Bottom-up per file, so earlier changes don't move the later ones.
        foreach (var hunk in hunks.Where(h => h.State == HunkState.Pending).OrderByDescending(h => h.OldStart).ToList())
        {
            if (AcceptHunk(hunk, hunks, save: false)) touched.Add(hunk.FullPath);
        }
        foreach (var file in newFiles.Where(n => n.State == HunkState.Pending).ToList())
            AcceptNewFile(file, save: false);
        if (AutoSave.IsChecked == true)
        {
            foreach (var tab in _tabs.Where(t => t.IsDirty && (touched.Contains(t.Path) || newFiles.Any(n => SamePath(n.FullPath, t.Path)))).ToList())
                Save(tab);
            foreach (var h in hunks.Where(h => h.State == HunkState.Accepted)) h.StateText!.Text = "✓ applied and saved";
            foreach (var n in newFiles.Where(n => n.State == HunkState.Accepted)) n.StateText!.Text = "✓ created and saved";
        }
    }

    private void RevealHunk(ProposedHunk hunk)
    {
        var tab = OpenFile(hunk.FullPath);
        if (tab == null) return;
        var line = System.Math.Clamp(hunk.OldStart + hunk.Shift + 1, 1, tab.Document.LineCount);
        Editor.ScrollTo(line, 1);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.BringCaretToView();
    }

    // ═════════════════════════════════ dialogs & closing ═════════════════════════════════

    private string? Prompt(string title, string label, string initial, string okText = "OK")
    {
        var box = new TextBox
        {
            Text = initial, Margin = new Thickness(0, 8, 0, 14), Padding = new Thickness(6, 5, 6, 5),
            Background = B("#070C14"), Foreground = TextBrush, BorderBrush = LineBrush, CaretBrush = Cyan,
            FontFamily = Mono, FontSize = 13, SelectionBrush = B("#196C8F"),
        };
        var ok = new Button { Content = okText, Style = (Style)FindResource("AccentBtn"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Style = (Style)FindResource("ToolBtn"), IsCancel = true, Content = new TextBlock { Text = "Cancel", FontFamily = Ui } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontFamily = Ui, FontSize = 12.5 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Owner = this, Content = panel, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = B("#0D1420"), ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            var dot = box.Text.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : box.Text.Length);
        };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    private bool _closeConfirmed;

    /// <summary>Asks about unsaved files (Save / Don't save / Cancel). False = stay open.</summary>
    internal bool ConfirmClose()
    {
        if (_closeConfirmed) return true;
        var dirty = _tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            var answer = MessageBox.Show(this,
                $"Save changes to {dirty.Count} file{(dirty.Count == 1 ? "" : "s")} before closing?\n\n" +
                string.Join("\n", dirty.Select(t => "  " + Rel(t.Path))),
                "Xena Code Agent", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && dirty.Any(t => !Save(t))) return false;
        }
        _closeConfirmed = true;
        return true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmClose()) { e.Cancel = true; return; }
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _agentCts?.Cancel();
        _settings.AutoSave = AutoSave.IsChecked == true;
        _settings.IncludeOpenFiles = IncludeOpenFiles.IsChecked == true;
        SaveLayout();
        SaveSession();   // also saves the settings
        _settings.Save();
        _watcher?.Dispose();
        _uiTimer.Stop();
        foreach (var timer in new[] { _foldTimer, _checkTimer, _autoSaveTimer, _occurTimer }) timer.Stop();
    }
}
