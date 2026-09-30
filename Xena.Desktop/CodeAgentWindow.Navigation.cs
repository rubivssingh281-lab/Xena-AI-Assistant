using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Xena.Desktop;

/// <summary>Finding things and moving around: the palette (files / commands / lines), search
/// in files, explorer file operations, sessions, recent folders, tab menus, terminal links.</summary>
public partial class CodeAgentWindow
{
    // ═════════════════════════════════ palette ═════════════════════════════════

    private sealed record PaletteCommand(string Name, string Gesture, System.Action Run);

    private List<string>? _fileCache;               // every file in the project (full paths)
    private List<PaletteCommand>? _commands;
    private readonly List<string> _recentFiles = new();

    private void PaletteBtn_Click(object sender, RoutedEventArgs e) => OpenPalette("");

    private void OpenPalette(string prefix)
    {
        PaletteOverlay.Visibility = Visibility.Visible;
        PaletteInput.Text = prefix;
        PaletteInput.CaretIndex = prefix.Length;
        PaletteInput.Focus();
        if (_fileCache == null && _workspace != null) _ = LoadFileCacheAsync();
        RefreshPalette();
    }

    private void ClosePalette()
    {
        if (PaletteOverlay.Visibility != Visibility.Visible) return;
        PaletteOverlay.Visibility = Visibility.Collapsed;
        if (_active != null) Editor.TextArea.Focus();
    }

    private bool PaletteOpen => PaletteOverlay.Visibility == Visibility.Visible;

    private async Task LoadFileCacheAsync()
    {
        var root = _workspace;
        if (root == null) return;
        var files = await Task.Run(() => EnumerateProjectFiles(root, 20_000));
        if (root != _workspace) return;
        _fileCache = files;
        if (PaletteOpen) RefreshPalette();
    }

    private void PaletteOverlay_MouseDown(object sender, MouseButtonEventArgs e) => ClosePalette();
    private void PaletteBox_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void PaletteInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshPalette();

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePalette();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when PaletteList.Items.Count > 0:
                var index = PaletteList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                PaletteList.SelectedIndex = (index + PaletteList.Items.Count) % PaletteList.Items.Count;
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when PaletteList.Items.Count > 0:
                PaletteList.SelectedIndex = System.Math.Clamp(PaletteList.SelectedIndex + (e.Key == Key.PageDown ? 10 : -10), 0, PaletteList.Items.Count - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                RunPaletteSelection();
                e.Handled = true;
                break;
        }
    }

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e) => RunPaletteSelection();

    private void RunPaletteSelection()
    {
        if (PaletteList.SelectedItem is not ListBoxItem { Tag: System.Action action }) return;
        ClosePalette();
        action();
    }

    private void RefreshPalette()
    {
        var text = PaletteInput.Text;
        PaletteList.Items.Clear();
        if (text.StartsWith('>'))
        {
            var query = text[1..].Trim();
            var matches = Commands()
                .Select(c => (Command: c, Score: query.Length == 0 ? 0 : Fuzzy(query, c.Name, 0)))
                .Where(m => m.Score >= 0)
                .OrderByDescending(m => m.Score)
                .Take(80);
            foreach (var (command, _) in matches)
                PaletteList.Items.Add(PaletteRow("", Brushes.Transparent, command.Name, null, command.Gesture, command.Run));
            PaletteInfo.Text = PaletteList.Items.Count == 0 ? "No matching command" : "Run a command";
        }
        else if (text.StartsWith(':'))
        {
            var lines = _active?.Document.LineCount ?? 0;
            var m = Regex.Match(text, @"^:\s*(\d+)(?:[:,](\d+))?");
            if (_active == null) PaletteInfo.Text = "Open a file first.";
            else if (!m.Success) PaletteInfo.Text = $"Type a line number between 1 and {lines}, then press Enter.";
            else
            {
                var line = int.Parse(m.Groups[1].Value);
                var column = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                PaletteInfo.Text = $"Current line {Editor.TextArea.Caret.Line} of {lines}.";
                PaletteList.Items.Add(PaletteRow("", Brushes.Transparent, $"Go to line {line}" + (column > 1 ? $", column {column}" : ""), null, "Enter",
                                                 () => GoToLine(line, column)));
            }
        }
        else
        {
            var query = text.Replace(" ", "");
            if (_workspace == null)
            {
                PaletteInfo.Text = "Open a folder to search its files. Type > for commands.";
            }
            else if (query.Length == 0)
            {
                // Recently opened files first, like VS Code.
                var recent = _recentFiles.Where(File.Exists).Take(12).ToList();
                foreach (var path in recent) PaletteList.Items.Add(FileRow(path));
                PaletteInfo.Text = recent.Count > 0 ? "Recently opened  ·  type a name to search  ·  > commands  ·  : line" : "Type part of a file name  ·  > commands  ·  : line";
            }
            else if (_fileCache == null)
            {
                PaletteInfo.Text = "Indexing files…";
            }
            else
            {
                var matches = _fileCache
                    .Select(path =>
                    {
                        var rel = Rel(path);
                        return (Path: path, Score: Fuzzy(query, rel, rel.LastIndexOf('/') + 1));
                    })
                    .Where(m => m.Score >= 0)
                    .OrderByDescending(m => m.Score)
                    .ThenBy(m => m.Path.Length)
                    .Take(60)
                    .ToList();
                foreach (var (path, _) in matches) PaletteList.Items.Add(FileRow(path));
                PaletteInfo.Text = matches.Count switch
                {
                    0 => "No matching files",
                    1 => "1 file",
                    60 => "60+ files — keep typing to narrow it down",
                    var n => $"{n} files",
                };
            }
        }
        if (PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private ListBoxItem FileRow(string path)
    {
        var rel = Rel(path);
        var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
        return PaletteRow("", FileColor(path), Path.GetFileName(path), dir, null, () => OpenFile(path));
    }

    private static ListBoxItem PaletteRow(string glyph, Brush glyphBrush, string title, string? detail, string? gesture, System.Action run)
    {
        var row = new DockPanel();
        if (gesture != null)
        {
            var g = new TextBlock { Text = gesture, FontFamily = Ui, FontSize = 11, Foreground = Muted, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(g, Dock.Right);
            row.Children.Add(g);
        }
        if (glyphBrush != Brushes.Transparent)
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = glyphBrush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { FontFamily = Ui, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run(title) { Foreground = TextBrush });
        if (!string.IsNullOrEmpty(detail)) text.Inlines.Add(new Run("   " + detail) { Foreground = Muted, FontSize = 11.5 });
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = run };
        AutomationProperties.SetName(item, title);
        return item;
    }

    /// <summary>Subsequence match with bonuses for word starts, runs and hits in the file name.
    /// -1 = no match.</summary>
    private static int Fuzzy(string query, string candidate, int nameStart)
    {
        int qi = 0, score = 0, last = -2;
        for (var i = 0; i < candidate.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[qi])) continue;
            score += 1;
            if (i == last + 1) score += 5;
            if (i >= nameStart) score += 3;
            if (i == 0 || "/\\_-. ".IndexOf(candidate[i - 1]) >= 0 || (char.IsUpper(candidate[i]) && char.IsLower(candidate[i - 1]))) score += 4;
            last = i;
            qi++;
        }
        if (qi < query.Length) return -1;
        var name = candidate[nameStart..];
        if (name.StartsWith(query, System.StringComparison.OrdinalIgnoreCase)) score += 20;
        if (Path.GetFileNameWithoutExtension(name).Equals(query, System.StringComparison.OrdinalIgnoreCase)) score += 20;
        return score - candidate.Length / 12;
    }

    private void RememberRecentFile(string path)
    {
        _recentFiles.RemoveAll(p => SamePath(p, path));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > 30) _recentFiles.RemoveAt(_recentFiles.Count - 1);
    }

    private List<PaletteCommand> Commands() => _commands ??= new List<PaletteCommand>
    {
        new("File: New File…", "Ctrl+N", () => NewFile_Click(this, new RoutedEventArgs())),
        new("File: New Folder…", "", () => NewFolder_Click(this, new RoutedEventArgs())),
        new("File: Open Folder…", "Ctrl+O", PickWorkspace),
        new("File: Open Recent Folder…", "", () => Recent_Click(RecentBtn, new RoutedEventArgs())),
        new("File: Save", "Ctrl+S", () => { if (_active != null) Save(_active); }),
        new("File: Save All", "Ctrl+Shift+S", SaveAll),
        new("File: Close Tab", "Ctrl+W", () => { if (_active != null) CloseTab(_active); }),
        new("File: Close All Tabs", "", () => CloseTabs(_tabs.ToList())),
        new("File: Close Saved Tabs", "", () => CloseTabs(_tabs.Where(t => !t.IsDirty).ToList())),
        new("File: Reopen Closed Tab", "Ctrl+Shift+T", ReopenClosedTab),
        new("File: Reveal Active File in Explorer Sidebar", "", () => { if (_active != null) { ShowExplorerView(); RevealInExplorer(_active.Path); } }),
        new("File: Reveal Active File in File Explorer", "", () => { if (_active != null) RevealInShell(_active.Path); }),
        new("File: Copy Path of Active File", "", () => { if (_active != null) Clipboard.SetText(_active.Path); }),
        new("File: Copy Relative Path of Active File", "", () => { if (_active != null) Clipboard.SetText(Rel(_active.Path)); }),
        new("Go to File…", "Ctrl+P", () => OpenPalette("")),
        new("Go to Line…", "Ctrl+G", () => OpenPalette(":")),
        new("Go: Next Tab", "Ctrl+Tab", () => CycleTab(+1)),
        new("Go: Previous Tab", "Ctrl+Shift+Tab", () => CycleTab(-1)),
        new("Search: Find in File", "Ctrl+F", OpenFind),
        new("Search: Find in All Files", "Ctrl+Shift+F", ShowSearchView),
        new("View: Show Explorer", "Ctrl+Shift+E", ShowExplorerView),
        new("View: Toggle Terminal", "Ctrl+`", () => ToggleTerminal()),
        new("View: Toggle Word Wrap", "Alt+Z", ToggleWordWrap),
        new("View: Toggle Show Whitespace", "", ToggleWhitespace),
        new("View: Zoom In", "Ctrl+=", () => SetFontSize(Editor.FontSize + 1)),
        new("View: Zoom Out", "Ctrl+-", () => SetFontSize(Editor.FontSize - 1)),
        new("View: Reset Zoom", "Ctrl+0", () => SetFontSize(13.5)),
        new("Edit: Toggle Line Comment", "Ctrl+/", ToggleComment),
        new("Edit: Move Line Up", "Alt+Up", () => MoveLines(-1)),
        new("Edit: Move Line Down", "Alt+Down", () => MoveLines(+1)),
        new("Edit: Copy Line Up", "Shift+Alt+Up", () => CopyLines(-1)),
        new("Edit: Copy Line Down", "Shift+Alt+Down", () => CopyLines(+1)),
        new("Edit: Delete Line", "Ctrl+Shift+K", DeleteLines),
        new("Edit: Insert Line Below", "Ctrl+Enter", () => InsertLine(above: false)),
        new("Edit: Insert Line Above", "Ctrl+Shift+Enter", () => InsertLine(above: true)),
        new("Edit: Indent Line", "Ctrl+]", () => ShiftLines(indent: true)),
        new("Edit: Outdent Line", "Ctrl+[", () => ShiftLines(indent: false)),
        new("Edit: Convert Indentation to Spaces", "", () => { if (_active != null) { ConvertIndentation(_active, false); ApplyIndentation(_active); } }),
        new("Edit: Convert Indentation to Tabs", "", () => { if (_active != null) { ConvertIndentation(_active, true); ApplyIndentation(_active); } }),
        new("Edit: Change Line Endings to LF", "", () => ConvertLineEndings("\n")),
        new("Edit: Change Line Endings to CRLF", "", () => ConvertLineEndings("\r\n")),
        new("Fold: Toggle Fold", "Ctrl+Shift+[", ToggleFoldAtCaret),
        new("Fold: Fold All", "", () => FoldAll(true)),
        new("Fold: Unfold All", "", () => FoldAll(false)),
        new("Run: Run Current File", "F5", RunActiveFile),
        new("Run: Stop", "Shift+F5", StopProcess),
        new("Run: Clear Terminal", "", () => TerminalOutput.Clear()),
        new("Xena: Ask Xena", "Ctrl+L", () => AgentInput.Focus()),
        new("Xena: Explain This Code", "", () => QuickAsk("Explain what this code does, briefly.")),
        new("Xena: Find and Fix Bugs", "", () => QuickAsk("Find and fix any bugs in this code.")),
        new("Xena: Refactor for Readability", "", () => QuickAsk("Refactor this code to be cleaner and more readable without changing its behaviour.")),
        new("Xena: Add Comments", "", () => QuickAsk("Add clear, concise comments to this code.")),
        new("Xena: Write Unit Tests", "", () => QuickAsk("Write unit tests for this code in a new test file.")),
        new("Xena: New Chat", "", () => NewChat_Click(this, new RoutedEventArgs())),
        new("Preferences: Toggle Auto Save", "", () => ToggleSetting(s => s.AutoSaveEdits = !s.AutoSaveEdits, "Auto save", _ => _settings.AutoSaveEdits)),
        new("Preferences: Toggle Auto-Close Brackets and Quotes", "", () => ToggleSetting(s => s.AutoClosePairs = !s.AutoClosePairs, "Auto-close brackets", _ => _settings.AutoClosePairs)),
        new("Preferences: Toggle Problem Checking", "", () => { ToggleSetting(s => s.CheckProblems = !s.CheckProblems, "Problem checking", _ => _settings.CheckProblems); _ = CheckProblemsAsync(_active); }),
        new("Preferences: Toggle Trim Trailing Whitespace on Save", "", () => ToggleSetting(s => s.TrimTrailingWhitespace = !s.TrimTrailingWhitespace, "Trim trailing whitespace", _ => _settings.TrimTrailingWhitespace)),
        new("Preferences: Toggle Insert Final Newline on Save", "", () => ToggleSetting(s => s.InsertFinalNewline = !s.InsertFinalNewline, "Insert final newline", _ => _settings.InsertFinalNewline)),
    };

    private void ToggleSetting(System.Action<CodeAgentSettings> change, string name, System.Func<CodeAgentSettings, bool> value)
    {
        change(_settings);
        _settings.Save();
        Status($"{name}: {(value(_settings) ? "on" : "off")}");
    }

    private void OpenFind()
    {
        if (_active == null) return;
        Editor.TextArea.Focus();
        ApplicationCommands.Find.Execute(null, Editor.TextArea);   // AvalonEdit's search panel
    }

    private void CycleTab(int step)
    {
        if (_tabs.Count < 2) return;
        var i = _active == null ? 0 : _tabs.IndexOf(_active);
        Activate(_tabs[(i + step + _tabs.Count) % _tabs.Count]);
    }

    // ═════════════════════════════════ settings menu ═════════════════════════════════

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Toggle(string header, string gesture, bool value, System.Action flip)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsChecked = value };
            item.Click += (_, _) => { flip(); _settings.Save(); };
            menu.Items.Add(item);
        }
        void Action(string header, string gesture, System.Action run)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Toggle("Auto Save (after a pause, and when you leave the window)", "", _settings.AutoSaveEdits, () => _settings.AutoSaveEdits = !_settings.AutoSaveEdits);
        Toggle("Word Wrap", "Alt+Z", _settings.WordWrap, ToggleWordWrap);
        Toggle("Show Whitespace", "", _settings.ShowWhitespace, ToggleWhitespace);
        Toggle("Auto-Close Brackets and Quotes", "", _settings.AutoClosePairs, () => _settings.AutoClosePairs = !_settings.AutoClosePairs);
        Toggle("Check for Problems (Python, JSON, XML)", "", _settings.CheckProblems, () =>
        {
            _settings.CheckProblems = !_settings.CheckProblems;
            _ = CheckProblemsAsync(_active);
        });
        Toggle("Trim Trailing Whitespace on Save", "", _settings.TrimTrailingWhitespace, () => _settings.TrimTrailingWhitespace = !_settings.TrimTrailingWhitespace);
        Toggle("Insert Final Newline on Save", "", _settings.InsertFinalNewline, () => _settings.InsertFinalNewline = !_settings.InsertFinalNewline);
        menu.Items.Add(new Separator());
        Action("Zoom In", "Ctrl+=", () => SetFontSize(Editor.FontSize + 1));
        Action("Zoom Out", "Ctrl+-", () => SetFontSize(Editor.FontSize - 1));
        Action("Reset Zoom", "Ctrl+0", () => SetFontSize(13.5));
        menu.Items.Add(new Separator());
        Action("All Commands and Shortcuts…", "Ctrl+Shift+P", () => OpenPalette(">"));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ═════════════════════════════════ search in files ═════════════════════════════════

    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchTimer;

    private void ExplorerTab_Click(object sender, RoutedEventArgs e) => ShowExplorerView();
    private void SearchTab_Click(object sender, RoutedEventArgs e) => ShowSearchView();

    private void ShowExplorerView()
    {
        ExplorerView.Visibility = Visibility.Visible;
        SearchView.Visibility = Visibility.Collapsed;
        ExplorerButtons.Visibility = Visibility.Visible;
        StyleViewTabs(explorer: true);
    }

    private void ShowSearchView()
    {
        ExplorerView.Visibility = Visibility.Collapsed;
        SearchView.Visibility = Visibility.Visible;
        ExplorerButtons.Visibility = Visibility.Collapsed;
        StyleViewTabs(explorer: false);
        if (_active != null && !Editor.TextArea.Selection.IsEmpty && !Editor.SelectedText.Contains('\n'))
            SearchBox.Text = Editor.SelectedText;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void StyleViewTabs(bool explorer)
    {
        ExplorerTab.Foreground = explorer ? TextBrush : Muted;
        ExplorerTab.BorderBrush = explorer ? Cyan : Brushes.Transparent;
        SearchTab.Foreground = explorer ? Muted : TextBrush;
        SearchTab.BorderBrush = explorer ? Brushes.Transparent : Cyan;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _searchTimer?.Stop(); _ = RunSearchAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer ??= Timer(400, () => _ = RunSearchAsync());
        Restart(_searchTimer);
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private sealed record SearchHit(int Line, int Column, int Length, string Text);

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var query = SearchBox.Text;
        SearchResults.Children.Clear();
        if (query.Length == 0 || _workspace == null)
        {
            SearchSummary.Text = _workspace == null ? "Open a folder first" : "";
            return;
        }
        Regex regex;
        try
        {
            var pattern = SearchRegex.IsChecked == true ? query : Regex.Escape(query);
            if (SearchWord.IsChecked == true) pattern = $@"(?<!\w){pattern}(?!\w)";
            regex = new Regex(pattern, (SearchCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                              System.TimeSpan.FromMilliseconds(200));
        }
        catch (System.ArgumentException)
        {
            SearchSummary.Text = "Invalid regular expression";
            return;
        }
        SearchSummary.Text = "Searching…";
        var root = _workspace;
        var buffers = _tabs.Where(t => t.IsDirty).ToDictionary(t => t.Path.ToLowerInvariant(), t => t.Document.Text);
        const int maxHits = 2000;
        List<(string Path, List<SearchHit> Hits)> results;
        var total = 0;
        try
        {
            results = await Task.Run(() =>
            {
                var found = new List<(string, List<SearchHit>)>();
                foreach (var file in EnumerateProjectFiles(root, 20_000))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string? text;
                    if (!buffers.TryGetValue(file.ToLowerInvariant(), out text))
                    {
                        try
                        {
                            if (new FileInfo(file).Length > 1_500_000) continue;
                            text = Decode(File.ReadAllBytes(file))?.Text;
                        }
                        catch (System.Exception) { continue; }
                    }
                    if (text == null) continue;
                    List<SearchHit>? hits = null;
                    var lines = text.Split('\n');
                    for (var n = 0; n < lines.Length && total < maxHits; n++)
                    {
                        MatchCollection matches;
                        try { matches = regex.Matches(lines[n]); }
                        catch (RegexMatchTimeoutException) { break; }
                        foreach (Match m in matches)
                        {
                            if (m.Length == 0) continue;
                            (hits ??= new()).Add(new SearchHit(n + 1, m.Index + 1, m.Length, lines[n].TrimEnd('\r')));
                            if (++total >= maxHits) break;
                        }
                    }
                    if (hits != null) found.Add((file, hits));
                    if (total >= maxHits) break;
                }
                return found;
            }, cts.Token);
        }
        catch (System.OperationCanceledException) { return; }
        if (cts != _searchCts) return;

        var count = results.Sum(r => r.Hits.Count);
        SearchSummary.Text = count == 0 ? "No results" : $"{count}{(count >= maxHits ? "+" : "")} results in {results.Count} file{(results.Count == 1 ? "" : "s")}";
        foreach (var (path, hits) in results) SearchResults.Children.Add(SearchGroup(path, hits));
    }

    private UIElement SearchGroup(string path, List<SearchHit> hits)
    {
        var rows = new StackPanel();
        var chevron = new TextBlock { Text = "", FontFamily = Icons, FontSize = 8, Foreground = Muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 2), Background = Brushes.Transparent, ToolTip = Rel(path) };
        var badge = new Border { Background = B("#1A2A3A"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Child = new TextBlock { Text = hits.Count.ToString(), FontSize = 10.5, Foreground = TextBrush, FontFamily = Ui } };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(chevron);
        header.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = FileColor(path), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { FontFamily = Ui, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new Run(Path.GetFileName(path)) { Foreground = TextBrush, FontWeight = FontWeights.SemiBold });
        var dir = Path.GetDirectoryName(Rel(path).Replace('/', '\\'));
        if (!string.IsNullOrEmpty(dir)) name.Inlines.Add(new Run("  " + dir) { Foreground = Muted, FontSize = 11 });
        header.Children.Add(name);
        header.MouseLeftButtonUp += (_, _) =>
        {
            rows.Visibility = rows.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chevron.Text = rows.Visibility == Visibility.Visible ? "" : "";
        };

        foreach (var hit in hits.Take(200))
        {
            var start = System.Math.Max(0, hit.Column - 1 - 36);
            var before = hit.Text[start..(hit.Column - 1)].TrimStart();
            var match = hit.Text.Substring(hit.Column - 1, System.Math.Min(hit.Length, hit.Text.Length - hit.Column + 1));
            var after = hit.Text[System.Math.Min(hit.Text.Length, hit.Column - 1 + hit.Length)..];
            var line = new TextBlock { FontFamily = Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(22, 1, 4, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            line.Inlines.Add(new Run(hit.Line.ToString().PadLeft(4) + "  ") { Foreground = B("#4E6378") });
            line.Inlines.Add(new Run((start > 0 ? "…" : "") + before) { Foreground = Muted });
            line.Inlines.Add(new Run(match) { Foreground = TextBrush, Background = B("#5A3A12"), FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run(after) { Foreground = Muted });
            line.ToolTip = hit.Text.Trim();
            var target = hit;
            line.MouseEnter += (_, _) => line.Background = B("#13212F");
            line.MouseLeave += (_, _) => line.Background = Brushes.Transparent;
            line.MouseLeftButtonUp += (_, _) => OpenAt(path, target.Line, target.Column, target.Length);
            rows.Children.Add(line);
        }
        if (hits.Count > 200)
            rows.Children.Add(new TextBlock { Text = $"… {hits.Count - 200} more in this file", FontSize = 11, Foreground = Muted, Margin = new Thickness(22, 2, 0, 2), FontFamily = Ui });

        var group = new StackPanel();
        group.Children.Add(header);
        group.Children.Add(rows);
        return group;
    }

    /// <summary>Opens a file with the given range selected.</summary>
    private void OpenAt(string path, int line, int column, int length = 0)
    {
        if (OpenFile(path) == null) return;
        var doc = Editor.Document;
        line = System.Math.Clamp(line, 1, doc.LineCount);
        var docLine = doc.GetLineByNumber(line);
        var offset = docLine.Offset + System.Math.Clamp(column - 1, 0, docLine.Length);
        Editor.Select(offset, System.Math.Min(length, doc.TextLength - offset));
        Editor.CaretOffset = offset + System.Math.Min(length, doc.TextLength - offset);
        Editor.ScrollTo(line, column);
        Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
    }

    // ═════════════════════════════════ explorer operations ═════════════════════════════════

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchTimer;
    private bool _revealing;

    private void WatchWorkspace()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_workspace == null) return;
        try
        {
            _watcher = new FileSystemWatcher(_workspace)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler changed = (_, e) => OnWorkspaceChanged(e.FullPath);
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => OnWorkspaceChanged(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
        catch (System.Exception) { /* e.g. a network drive: the Refresh button still works */ }
    }

    private void OnWorkspaceChanged(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Any(p => SkipDirs.Contains(p))) return;   // build output, .git, caches…
        Dispatcher.BeginInvoke(() =>
        {
            _fileCache = null;
            _watchTimer ??= Timer(700, () => RefreshExplorer());
            Restart(_watchTimer);
        });
    }

    private TreeViewItem? SelectedItem => Explorer.SelectedItem as TreeViewItem;

    private void Explorer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2) { Rename_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Enter && SelectedExplorerPath() is { } path && File.Exists(path)) { OpenFile(path); e.Handled = true; }
    }

    private void ExplorerMenu_Opened(object sender, RoutedEventArgs e)
    {
        var editable = SelectedItem is { } item && item.Parent is TreeViewItem;   // not the project folder itself
        MenuRename.IsEnabled = editable;
        MenuDelete.IsEnabled = editable;
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        static void Collapse(ItemCollection items)
        {
            foreach (var child in items.OfType<TreeViewItem>())
            {
                Collapse(child.Items);
                child.IsExpanded = false;
            }
        }
        foreach (var root in Explorer.Items.OfType<TreeViewItem>()) Collapse(root.Items);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) { PickWorkspace(); if (_workspace == null) return; }
        var baseDir = SelectedFolder();
        var name = Prompt("New folder", $"Folder name (in {Shown(baseDir)}):", "new-folder", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var full = Path.GetFullPath(Path.Combine(baseDir, name.Trim()));
        if (!IsInside(full, _workspace)) { MessageBox.Show(this, "New folders must be inside the project folder.", "Xena Code Agent"); return; }
        try { Directory.CreateDirectory(full); }
        catch (System.Exception ex) { MessageBox.Show(this, "Couldn't create the folder:\n" + ex.Message, "Xena Code Agent"); return; }
        RefreshExplorer();
        RevealInExplorer(full);
    }

    private string SelectedFolder()
    {
        var selected = SelectedExplorerPath();
        return selected == null ? _workspace! : Directory.Exists(selected) ? selected : Path.GetDirectoryName(selected)!;
    }

    private string Shown(string dir)
    {
        var rel = _workspace == null ? dir : Path.GetRelativePath(_workspace, dir);
        return rel == "." ? "the project folder" : rel;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { Parent: TreeViewItem, Tag: string path }) return;
        var isDir = Directory.Exists(path);
        var name = Prompt("Rename", "New name:", Path.GetFileName(path), "Rename");
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == Path.GetFileName(path)) return;
        var target = Path.Combine(Path.GetDirectoryName(path)!, name.Trim());
        if (File.Exists(target) || Directory.Exists(target))
        {
            MessageBox.Show(this, $"'{name.Trim()}' already exists here.", "Xena Code Agent");
            return;
        }
        try
        {
            if (isDir) Directory.Move(path, target);
            else File.Move(path, target);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "Couldn't rename:\n" + ex.Message, "Xena Code Agent");
            return;
        }
        // Open tabs follow the file (or everything inside the renamed folder).
        foreach (var tab in _tabs)
        {
            string? moved = null;
            if (SamePath(tab.Path, path)) moved = target;
            else if (isDir && IsInside(tab.Path, path)) moved = Path.Combine(target, Path.GetRelativePath(path, tab.Path));
            if (moved == null) continue;
            tab.Path = moved;
            tab.Document.FileName = moved;
            tab.Highlighting = DarkHighlighting.For(moved);
            tab.Title.Text = tab.FileName;
            tab.Header.ToolTip = moved;
            if (tab == _active) Editor.SyntaxHighlighting = tab.Highlighting;
        }
        RefreshExplorer();
        RevealInExplorer(target);
        UpdateStatus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { Parent: TreeViewItem, Tag: string path }) return;
        var isDir = Directory.Exists(path);
        var affected = _tabs.Where(t => SamePath(t.Path, path) || (isDir && IsInside(t.Path, path))).ToList();
        var warning = affected.Any(t => t.IsDirty) ? "\n\nIt has unsaved changes open in the editor." : "";
        if (MessageBox.Show(this, $"Move '{Path.GetFileName(path)}'{(isDir ? " and everything in it" : "")} to the Recycle Bin?{warning}",
                            "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!MoveToRecycleBin(path))
        {
            MessageBox.Show(this, "Couldn't move it to the Recycle Bin.", "Xena Code Agent");
            return;
        }
        foreach (var tab in affected) CloseTab(tab, prompt: false);
        RefreshExplorer();
    }

    private void OpenInTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace == null) return;
        SetTerminalCwd(SelectedFolder());
        ToggleTerminal(true);
        TerminalInput.Focus();
    }

    private void CopyRelativePath_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedExplorerPath() ?? _active?.Path;
        if (path != null) Clipboard.SetText(Rel(path));
    }

    /// <summary>Selects a file or folder in the tree, expanding the folders on the way.</summary>
    private void RevealInExplorer(string path)
    {
        if (_workspace == null || !(IsInside(path, _workspace) || SamePath(path, _workspace))) return;
        if (Explorer.Items.Count == 0 || Explorer.Items[0] is not TreeViewItem root) return;
        var rel = Path.GetRelativePath(_workspace, path);
        var item = root;
        var current = _workspace;
        if (rel != ".")
        {
            foreach (var part in rel.Split(Path.DirectorySeparatorChar))
            {
                if (item.Items.Count == 1 && item.Items[0] is string) FillFolder(item, current);
                item.IsExpanded = true;
                current = Path.Combine(current, part);
                var next = item.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is string t && SamePath(t, current));
                if (next == null) return;
                item = next;
            }
        }
        _revealing = true;
        try { item.IsSelected = true; }
        finally { _revealing = false; }
        item.BringIntoView();
    }

    private static void RevealInShell(string path)
    {
        if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public System.IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public System.IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private static bool MoveToRecycleBin(string path)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    // ═════════════════════════════════ sessions & recent folders ═════════════════════════════════

    private static string SessionKey(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/').ToLowerInvariant();

    private void SaveSession()
    {
        if (_workspace == null) return;
        if (_active != null)
        {
            _active.CaretOffset = Editor.CaretOffset;
            _active.ScrollY = Editor.VerticalOffset;
        }
        _settings.Sessions[SessionKey(_workspace)] = new WorkspaceSession
        {
            Files = _tabs.Where(t => !t.IsNew && File.Exists(t.Path))
                         .Select(t => new SessionFile { Path = t.Path, Caret = t.CaretOffset, ScrollY = t.ScrollY })
                         .ToList(),
            Active = _active?.Path,
        };
        while (_settings.Sessions.Count > 30) _settings.Sessions.Remove(_settings.Sessions.Keys.First());
        _settings.Save();
    }

    private void RestoreSession()
    {
        if (_workspace == null || !_settings.Sessions.TryGetValue(SessionKey(_workspace), out var session)) return;
        foreach (var file in session.Files.Where(f => File.Exists(f.Path)))
        {
            var tab = OpenFile(file.Path, activate: false);
            if (tab == null) continue;
            tab.CaretOffset = System.Math.Min(file.Caret, tab.Document.TextLength);
            tab.ScrollY = file.ScrollY;
        }
        var active = (session.Active != null ? _tabs.FirstOrDefault(t => SamePath(t.Path, session.Active)) : null) ?? _tabs.LastOrDefault();
        if (active != null) Activate(active);
    }

    private void AddRecentFolder(string folder)
    {
        _settings.RecentFolders.RemoveAll(f => SamePath(f, folder));
        _settings.RecentFolders.Insert(0, folder);
        if (_settings.RecentFolders.Count > 12) _settings.RecentFolders.RemoveAt(_settings.RecentFolders.Count - 1);
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var folders = _settings.RecentFolders.Where(Directory.Exists).ToList();
        foreach (var folder in folders)
        {
            var item = new MenuItem { Header = Path.GetFileName(folder.TrimEnd('\\')) + "   —   " + folder };
            var target = folder;
            item.Click += (_, _) => OpenWorkspace(target);
            menu.Items.Add(item);
        }
        if (folders.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent folders", IsEnabled = false });
        menu.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Open Folder…", InputGestureText = "Ctrl+O" };
        browse.Click += (_, _) => PickWorkspace();
        menu.Items.Add(browse);
        if (folders.Count > 0)
        {
            var clear = new MenuItem { Header = "Clear Recent Folders" };
            clear.Click += (_, _) => { _settings.RecentFolders.Clear(); _settings.Save(); RefreshWelcome(); };
            menu.Items.Add(clear);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The welcome page: recent folders and the most useful shortcuts.</summary>
    private void RefreshWelcome()
    {
        RecentList.Children.Clear();
        var folders = _settings.RecentFolders.Where(Directory.Exists).Take(7).ToList();
        if (folders.Count == 0)
            RecentList.Children.Add(new TextBlock { Text = "Folders you open will appear here.", FontFamily = Ui, FontSize = 12, Foreground = Muted });
        foreach (var folder in folders)
        {
            var text = new TextBlock { FontFamily = Ui, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines.Add(new Run(Path.GetFileName(folder.TrimEnd('\\'))) { Foreground = Cyan });
            text.Inlines.Add(new Run("   " + Path.GetDirectoryName(folder)) { Foreground = Muted, FontSize = 11.5 });
            var button = new Button { Style = (Style)FindResource("ToolBtn"), Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-9, 0, 0, 0), ToolTip = folder };
            var target = folder;
            button.Click += (_, _) => OpenWorkspace(target);
            RecentList.Children.Add(button);
        }

        ShortcutList.Inlines.Clear();
        foreach (var (keys, what) in new[]
                 {
                     ("Ctrl+P", "Go to file"), ("Ctrl+Shift+P", "All commands"), ("Ctrl+Shift+F", "Search in all files"),
                     ("Ctrl+G", "Go to line"), ("Ctrl+/", "Comment / uncomment"), ("Alt+↑ / ↓", "Move line"),
                     ("Ctrl+Shift+K", "Delete line"), ("F5", "Run file"), ("Ctrl+`", "Terminal"), ("Ctrl+L", "Ask Xena"),
                 })
        {
            ShortcutList.Inlines.Add(new Run(keys.PadRight(14)) { Foreground = TextBrush, FontFamily = Mono, FontSize = 12 });
            ShortcutList.Inlines.Add(new Run(what) { Foreground = Muted });
            ShortcutList.Inlines.Add(new LineBreak());
        }
    }

    // ═════════════════════════════════ tabs ═════════════════════════════════

    private readonly Stack<string> _closedTabs = new();

    private ContextMenu BuildTabMenu(EditorTab tab)
    {
        var menu = new ContextMenu();
        void Add(string header, string gesture, System.Action run)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Close", "Ctrl+W", () => CloseTab(tab));
        Add("Close Others", "", () => CloseTabs(_tabs.Where(t => t != tab).ToList()));
        Add("Close to the Right", "", () => CloseTabs(_tabs.Skip(_tabs.IndexOf(tab) + 1).ToList()));
        Add("Close Saved", "", () => CloseTabs(_tabs.Where(t => !t.IsDirty).ToList()));
        Add("Close All", "", () => CloseTabs(_tabs.ToList()));
        menu.Items.Add(new Separator());
        Add("Copy Path", "", () => Clipboard.SetText(tab.Path));
        Add("Copy Relative Path", "", () => Clipboard.SetText(Rel(tab.Path)));
        Add("Reveal in Explorer Sidebar", "", () => { ShowExplorerView(); RevealInExplorer(tab.Path); });
        Add("Reveal in File Explorer", "", () => RevealInShell(tab.Path));
        return menu;
    }

    /// <summary>Closes several tabs, asking once about the unsaved ones.</summary>
    private bool CloseTabs(List<EditorTab> tabs)
    {
        var dirty = tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"Save changes to {dirty.Count} file{(dirty.Count == 1 ? "" : "s")}?\n\n" + string.Join("\n", dirty.Select(t => "  " + Rel(t.Path))),
                "Xena Code Agent", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && dirty.Any(t => !Save(t))) return false;
        }
        foreach (var tab in tabs) CloseTab(tab, prompt: false);
        return true;
    }

    private void ReopenClosedTab()
    {
        while (_closedTabs.Count > 0)
        {
            var path = _closedTabs.Pop();
            if (File.Exists(path)) { OpenFile(path); return; }
        }
        Status("No recently closed tabs.");
    }

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ═════════════════════════════════ terminal extras ═════════════════════════════════

    private string? _terminalCwd;

    private void SetTerminalCwd(string dir)
    {
        _terminalCwd = dir;
        var shown = _workspace != null && (SamePath(dir, _workspace) || IsInside(dir, _workspace))
            ? Path.GetFileName(_workspace.TrimEnd('\\')) + (SamePath(dir, _workspace) ? "" : "\\" + Path.GetRelativePath(_workspace, dir))
            : dir;
        TerminalPrompt.Text = shown + " ❯";
        TerminalPrompt.ToolTip = dir;
    }

    /// <summary>Commands the terminal handles itself: cd (so it sticks), cls/clear.</summary>
    private bool RunBuiltin(string text)
    {
        var t = text.Trim();
        if (t is "cls" or "clear")
        {
            TerminalOutput.Clear();
            return true;
        }
        var cwd = _terminalCwd ?? BaseFolder() ?? System.Environment.CurrentDirectory;
        string? target = null;
        var drive = Regex.Match(t, @"^([A-Za-z]:)\\?$");
        var cd = Regex.Match(t, @"^(?:cd|chdir)(?:\s+/d)?(?:\s+(.+))?$", RegexOptions.IgnoreCase);
        if (drive.Success) target = drive.Groups[1].Value + "\\";
        else if (cd.Success)
        {
            if (!cd.Groups[1].Success)
            {
                TerminalWrite($"\n❯ {t}\n{cwd}\n");
                return true;
            }
            target = cd.Groups[1].Value.Trim().Trim('"');
            if (target == "~") target = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        }
        if (target == null) return false;
        var full = Path.GetFullPath(Path.Combine(cwd, target));
        TerminalWrite($"\n❯ {t}\n");
        if (Directory.Exists(full)) SetTerminalCwd(full);
        else TerminalWrite("The system cannot find the path specified.\n");
        return true;
    }

    private static readonly Regex[] LocationPatterns =
    {
        new(@"File ""(?<p>[^""]+)"", line (?<l>\d+)"),                                              // Python
        new(@"(?<p>(?:[A-Za-z]:)?[^\s:""'()<>|*?]+\.[A-Za-z0-9]+)\((?<l>\d+)(?:,(?<c>\d+))?\)"),    // C#, TypeScript
        new(@"(?<p>(?:[A-Za-z]:)?[^\s:""'()<>|*?]+\.[A-Za-z0-9]+):(?<l>\d+)(?::(?<c>\d+))?"),       // node, gcc, eslint
    };

    /// <summary>Double-clicking an error line in the terminal opens that file at that line.</summary>
    private void TerminalOutput_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var box = TerminalOutput;
        var index = box.GetCharacterIndexFromPoint(e.GetPosition(box), true);
        if (index < 0) return;
        var text = box.Text;
        var start = text.LastIndexOf('\n', System.Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)];
        foreach (var pattern in LocationPatterns)
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;
            var path = m.Groups["p"].Value;
            var candidates = Path.IsPathRooted(path)
                ? new[] { path }
                : new[] { _terminalCwd, _runFile != null ? Path.GetDirectoryName(_runFile) : null, _workspace }
                    .Where(d => d != null).Select(d => Path.Combine(d!, path)).ToArray();
            var file = candidates.FirstOrDefault(File.Exists);
            if (file == null) continue;
            OpenFile(file);
            GoToLine(int.Parse(m.Groups["l"].Value), m.Groups["c"].Success ? int.Parse(m.Groups["c"].Value) : 1);
            e.Handled = true;
            return;
        }
    }

    // ═════════════════════════════════ drag & drop ═════════════════════════════════

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        var folder = paths.FirstOrDefault(Directory.Exists);
        if (folder != null) { OpenWorkspace(folder); return; }
        foreach (var file in paths.Where(File.Exists)) OpenFile(file);
    }

    // ═════════════════════════════════ helpers ═════════════════════════════════

    /// <summary>Every file in a project folder, skipping dependencies, build output and hidden folders.</summary>
    private static List<string> EnumerateProjectFiles(string root, int max)
    {
        var list = new List<string>();
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0 && list.Count < max)
        {
            var (dir, depth) = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (list.Count >= max) break;
                    list.Add(file);
                }
                if (depth >= 12) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (!SkipDirs.Contains(name) && !name.StartsWith('.')) pending.Push((sub, depth + 1));
                }
            }
            catch (System.Exception) { /* unreadable folder */ }
        }
        return list;
    }

    private void StatusPos_Click(object sender, RoutedEventArgs e) { if (_active != null) OpenPalette(":"); }
}
