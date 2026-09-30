using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace Xena.Desktop;

/// <summary>Editor behaviour: typing helpers, line commands, folding, zoom, indentation,
/// syntax checks and auto save.</summary>
public partial class CodeAgentWindow
{
    private readonly BracketRenderer _bracketRenderer = new();
    private readonly OccurrenceRenderer _occurrenceRenderer = new();
    private readonly ProblemRenderer _problemRenderer = new();
    private FoldingManager? _folding;
    private DispatcherTimer _foldTimer = null!, _checkTimer = null!, _autoSaveTimer = null!, _occurTimer = null!;
    private ToolTip? _problemTip;

    private void SetupEditorExtras()
    {
        var view = Editor.TextArea.TextView;
        view.BackgroundRenderers.Add(_occurrenceRenderer);
        view.BackgroundRenderers.Add(_bracketRenderer);
        view.BackgroundRenderers.Add(_problemRenderer);
        view.NonPrintableCharacterBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x47, 0x5B));
        FoldingElementGenerator.TextBrush = new SolidColorBrush(Color.FromRgb(0x71, 0x83, 0x98));

        Editor.FontSize = _settings.FontSize;
        Editor.WordWrap = _settings.WordWrap;
        Editor.Options.ShowSpaces = Editor.Options.ShowTabs = _settings.ShowWhitespace;
        Editor.Options.EnableRectangularSelection = true;   // Alt+drag selects a block

        Editor.TextArea.TextEntering += Editor_TextEntering;
        Editor.TextArea.PreviewKeyDown += EditorArea_PreviewKeyDown;
        Editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
        Editor.TextArea.Caret.PositionChanged += (_, _) => { UpdateBracket(); Restart(_occurTimer); };
        Editor.TextArea.SelectionChanged += (_, _) => Restart(_occurTimer);
        view.MouseHover += TextView_MouseHover;
        view.MouseHoverStopped += (_, _) => { if (_problemTip != null) _problemTip.IsOpen = false; };

        _foldTimer = Timer(600, UpdateFoldings);
        _checkTimer = Timer(1100, () => _ = CheckProblemsAsync(_active));
        _autoSaveTimer = Timer(1200, () => SaveAllDirty(quiet: true));
        _occurTimer = Timer(180, UpdateOccurrences);
        Deactivated += (_, _) => { if (_settings.AutoSaveEdits) SaveAllDirty(quiet: true); };
    }

    private DispatcherTimer Timer(int ms, System.Action action)
    {
        var timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        return timer;
    }

    private static void Restart(DispatcherTimer timer) { timer.Stop(); timer.Start(); }

    /// <summary>Called whenever a tab's text changes (typing, Xena's edits, reloads).</summary>
    private void OnTabTextChanged(EditorTab tab)
    {
        if (tab == _active)
        {
            Restart(_foldTimer);
            if (_settings.CheckProblems && CanCheck(tab.Path)) Restart(_checkTimer);
        }
        if (_settings.AutoSaveEdits) Restart(_autoSaveTimer);
    }

    private void SaveAllDirty(bool quiet)
    {
        foreach (var tab in _tabs.Where(t => t.IsDirty).ToList()) Save(tab, quiet);
    }

    // ───────────────────────── typing helpers ─────────────────────────

    private const string OpenChars = "([{\"'`", CloseChars = ")]}\"'`";

    private void Editor_TextEntering(object sender, TextCompositionEventArgs e)
    {
        if (!_settings.AutoClosePairs || e.Text.Length != 1 || _active == null) return;
        var c = e.Text[0];
        var area = Editor.TextArea;
        var doc = Editor.Document;
        var caret = area.Caret.Offset;
        var next = caret < doc.TextLength ? doc.GetCharAt(caret) : '\0';
        var prev = caret > 0 ? doc.GetCharAt(caret - 1) : '\0';

        // Typing a closer that's already there: just step over it.
        if (area.Selection.IsEmpty && CloseChars.IndexOf(c) >= 0 && next == c)
        {
            area.Caret.Offset = caret + 1;
            e.Handled = true;
            return;
        }
        var i = OpenChars.IndexOf(c);
        if (i < 0) return;
        var close = CloseChars[i];

        if (!area.Selection.IsEmpty)
        {
            // Wrap the selection: text -> (text) or "text". Multi-line only for brackets.
            if (area.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            if (area.Selection.IsMultiline && c is not ('(' or '[' or '{')) return;
            var start = Editor.SelectionStart;
            var length = Editor.SelectionLength;
            var inner = Editor.SelectedText;
            doc.Replace(start, length, c + inner + close);
            Editor.Select(start + 1, length);
            e.Handled = true;
            return;
        }

        var quote = c is '"' or '\'' or '`';
        if (quote)
        {
            var before = caret > 1 ? doc.GetCharAt(caret - 2) : '\0';
            var stringPrefix = "fFrRbBuU".IndexOf(prev) >= 0 && !char.IsLetterOrDigit(before);   // Python f'...', r"..."
            if ((char.IsLetterOrDigit(prev) && !stringPrefix) || prev == c || prev == '\\') return;   // don't, it's, """
        }
        // Only pair when nothing is directly after the caret (or a closer / punctuation is).
        if (next != '\0' && !char.IsWhiteSpace(next) && ")]};,:".IndexOf(next) < 0) return;
        doc.Insert(caret, c.ToString() + close);
        area.Caret.Offset = caret + 1;
        e.Handled = true;
    }

    private void EditorArea_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_active == null || Keyboard.Modifiers != ModifierKeys.None) return;
        var area = Editor.TextArea;
        var doc = Editor.Document;
        if (!area.Selection.IsEmpty) return;
        var caret = area.Caret.Offset;

        if (e.Key == Key.Back && _settings.AutoClosePairs && caret > 0 && caret < doc.TextLength)
        {
            // Backspace inside an empty pair removes both: (|) -> |
            var i = OpenChars.IndexOf(doc.GetCharAt(caret - 1));
            if (i >= 0 && doc.GetCharAt(caret) == CloseChars[i])
            {
                doc.Remove(caret - 1, 2);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Return) e.Handled = SmartNewLine(caret);
    }

    /// <summary>Enter with the indentation you'd expect: one level deeper after "{", "(", "[" or
    /// a Python ":", the closing bracket on its own line, and one level back after return/pass.</summary>
    private bool SmartNewLine(int caret)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(caret);
        var before = doc.GetText(line.Offset, caret - line.Offset);
        var after = doc.GetText(caret, line.EndOffset - caret);
        var indent = before[..(before.Length - before.TrimStart(' ', '\t').Length)];
        var unit = IndentUnit();
        var eol = EolOf(doc);
        var trimmed = before.TrimEnd();
        var last = trimmed.Length > 0 ? trimmed[^1] : '\0';
        var afterTrim = after.TrimStart(' ', '\t');
        var first = afterTrim.Length > 0 ? afterTrim[0] : '\0';
        var python = IsPython(_active!.Path);
        var comment = python && trimmed.TrimStart().StartsWith('#');

        string insert;
        int caretAfter;
        var opener = "([{".IndexOf(last);
        if (opener >= 0 && first == ")]}"[opener])
        {
            insert = eol + indent + unit + eol + indent;
            caretAfter = caret + eol.Length + indent.Length + unit.Length;
        }
        else if (opener >= 0 || (python && last == ':' && !comment))
        {
            insert = eol + indent + unit;
            caretAfter = caret + insert.Length;
        }
        else if (python && Regex.IsMatch(trimmed.TrimStart(), @"^(return|pass|break|continue|raise)\b") && indent.Length > 0)
        {
            var less = indent.EndsWith(unit) ? indent[..^unit.Length] : indent.TrimEnd(' ', '\t');
            insert = eol + less;
            caretAfter = caret + insert.Length;
        }
        else
        {
            return false;   // AvalonEdit keeps the current indentation
        }
        doc.Replace(caret, after.Length - afterTrim.Length, insert);
        Editor.TextArea.Caret.Offset = caretAfter;
        Editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    private string IndentUnit() =>
        _active is { UseTabs: true } ? "\t" : new string(' ', _active?.IndentSize ?? 4);

    private static bool IsPython(string path) => Path.GetExtension(path).ToLowerInvariant() is ".py" or ".pyw";

    // ───────────────────────── line commands ─────────────────────────

    /// <summary>First and last line touched by the selection (or the caret's line).</summary>
    private (int First, int Last) SelectedLines()
    {
        var doc = Editor.Document;
        if (Editor.TextArea.Selection.IsEmpty)
        {
            var n = Editor.TextArea.Caret.Line;
            return (n, n);
        }
        var start = Editor.SelectionStart;
        var end = start + Editor.SelectionLength;
        var first = doc.GetLineByOffset(start).LineNumber;
        var lastLine = doc.GetLineByOffset(end);
        var last = lastLine.LineNumber;
        if (last > first && end == lastLine.Offset) last--;   // selection ends at the start of a line
        return (first, last);
    }

    private static string? LineCommentToken(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" or ".pyw" or ".rb" or ".sh" or ".bash" or ".ps1" or ".psm1" or ".yml" or ".yaml" or ".toml"
            or ".r" or ".pl" or ".ini" or ".cfg" or ".conf" or ".dockerfile" or ".mk" or ".gitignore" => "#",
        ".sql" or ".lua" or ".hs" => "--",
        ".bat" or ".cmd" => "::",
        ".html" or ".htm" or ".xml" or ".xaml" or ".csproj" or ".svg" or ".md" or ".css" or ".vue" => null,
        _ => "//",
    };

    private void ToggleComment()
    {
        if (_active == null) return;
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var token = LineCommentToken(_active.Path);
        if (token == null)
        {
            ToggleBlockComment(first, last);
            return;
        }
        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineByNumber).ToList();
        var used = lines.Where(l => doc.GetText(l).Trim().Length > 0).ToList();
        if (used.Count == 0) return;
        var commented = used.All(l => doc.GetText(l).TrimStart().StartsWith(token));
        var column = used.Min(l => { var t = doc.GetText(l); return t.Length - t.TrimStart().Length; });
        doc.BeginUpdate();
        try
        {
            foreach (var number in Enumerable.Range(first, last - first + 1))
            {
                var line = doc.GetLineByNumber(number);
                var text = doc.GetText(line);
                if (text.Trim().Length == 0) continue;
                if (commented)
                {
                    var at = text.IndexOf(token, System.StringComparison.Ordinal);
                    var length = token.Length + (at + token.Length < text.Length && text[at + token.Length] == ' ' ? 1 : 0);
                    doc.Remove(line.Offset + at, length);
                }
                else
                {
                    doc.Insert(line.Offset + column, token + " ");
                }
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    private void ToggleBlockComment(int first, int last)
    {
        var doc = Editor.Document;
        var ext = Path.GetExtension(_active!.Path).ToLowerInvariant();
        var (open, close) = ext == ".css" ? ("/*", "*/") : ("<!--", "-->");
        var start = doc.GetLineByNumber(first);
        var end = doc.GetLineByNumber(last);
        var text = doc.GetText(start.Offset, end.EndOffset - start.Offset);
        var trimmed = text.Trim();
        doc.BeginUpdate();
        try
        {
            if (trimmed.StartsWith(open) && trimmed.EndsWith(close))
            {
                var inner = trimmed[open.Length..^close.Length].Trim();
                var lead = text[..(text.Length - text.TrimStart().Length)];
                doc.Replace(start.Offset, end.EndOffset - start.Offset, lead + inner);
            }
            else
            {
                var lead = text[..(text.Length - text.TrimStart().Length)];
                doc.Replace(start.Offset, end.EndOffset - start.Offset, lead + open + " " + text.TrimStart() + " " + close);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    /// <summary>Alt+Up / Alt+Down: move the current line(s) up or down.</summary>
    private void MoveLines(int direction)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        if ((direction < 0 && first == 1) || (direction > 0 && last == doc.LineCount)) return;
        var startLine = doc.GetLineByNumber(first);
        var endLine = doc.GetLineByNumber(last);
        var blockStart = startLine.Offset;
        var block = doc.GetText(blockStart, endLine.EndOffset - blockStart);
        var selStart = Editor.SelectionStart - blockStart;
        var selLength = Editor.SelectionLength;
        var caretRel = Editor.CaretOffset - blockStart;
        int newStart;
        doc.BeginUpdate();
        try
        {
            if (direction < 0)
            {
                var prev = doc.GetLineByNumber(first - 1);
                var prevText = doc.GetText(prev);
                var eol = doc.GetText(prev.EndOffset, prev.DelimiterLength);
                newStart = prev.Offset;
                doc.Replace(prev.Offset, endLine.EndOffset - prev.Offset, block + eol + prevText);
            }
            else
            {
                var next = doc.GetLineByNumber(last + 1);
                var nextText = doc.GetText(next);
                var eol = doc.GetText(endLine.EndOffset, endLine.DelimiterLength);
                newStart = blockStart + nextText.Length + eol.Length;
                doc.Replace(blockStart, next.EndOffset - blockStart, nextText + eol + block);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
        if (selLength > 0) Editor.Select(newStart + selStart, selLength);
        Editor.CaretOffset = newStart + caretRel;
    }

    /// <summary>Shift+Alt+Up / Down: copy the current line(s) above or below.</summary>
    private void CopyLines(int direction)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var startLine = doc.GetLineByNumber(first);
        var endLine = doc.GetLineByNumber(last);
        var block = doc.GetText(startLine.Offset, endLine.EndOffset - startLine.Offset);
        var eol = EolOf(doc);
        var caretRel = Editor.CaretOffset - startLine.Offset;
        if (direction > 0)
        {
            var start = startLine.Offset;
            doc.Insert(endLine.EndOffset, eol + block);
            Editor.CaretOffset = start + block.Length + eol.Length + caretRel;   // onto the copy
        }
        else
        {
            doc.Insert(startLine.Offset, block + eol);
            Editor.CaretOffset = startLine.Offset + caretRel;
        }
    }

    /// <summary>Ctrl+Shift+K: delete the current line(s).</summary>
    private void DeleteLines()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var startLine = doc.GetLineByNumber(first);
        var endLine = doc.GetLineByNumber(last);
        var column = Editor.TextArea.Caret.Column;
        if (endLine.DelimiterLength > 0)
            doc.Remove(startLine.Offset, endLine.EndOffset + endLine.DelimiterLength - startLine.Offset);
        else if (startLine.PreviousLine is { } previous)
            doc.Remove(previous.EndOffset, endLine.EndOffset - previous.EndOffset);
        else
            doc.Remove(startLine.Offset, endLine.EndOffset - startLine.Offset);
        var line = doc.GetLineByNumber(System.Math.Min(first, doc.LineCount));
        Editor.CaretOffset = line.Offset + System.Math.Min(column - 1, line.Length);
    }

    /// <summary>Ctrl+Enter / Ctrl+Shift+Enter: start a new line below / above, keeping indentation.</summary>
    private void InsertLine(bool above)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByNumber(Editor.TextArea.Caret.Line);
        var text = doc.GetText(line);
        var indent = text[..(text.Length - text.TrimStart(' ', '\t').Length)];
        var eol = EolOf(doc);
        if (above)
        {
            doc.Insert(line.Offset, indent + eol);
            Editor.CaretOffset = line.Offset + indent.Length;
        }
        else
        {
            doc.Insert(line.EndOffset, eol + indent);
            Editor.CaretOffset = line.EndOffset + eol.Length + indent.Length;
        }
        Editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>Ctrl+] / Ctrl+[: indent or outdent the current line(s).</summary>
    private void ShiftLines(bool indent)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var unit = IndentUnit();
        doc.BeginUpdate();
        try
        {
            for (var n = first; n <= last; n++)
            {
                var line = doc.GetLineByNumber(n);
                var text = doc.GetText(line);
                if (indent)
                {
                    if (text.Length > 0) doc.Insert(line.Offset, unit);
                    continue;
                }
                var remove = 0;
                if (text.StartsWith('\t')) remove = 1;
                else while (remove < unit.Length && remove < text.Length && text[remove] == ' ') remove++;
                if (remove > 0) doc.Remove(line.Offset, remove);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    private void GoToLine(int line, int column = 1)
    {
        if (_active == null) return;
        var doc = Editor.Document;
        line = System.Math.Clamp(line, 1, doc.LineCount);
        var docLine = doc.GetLineByNumber(line);
        Editor.CaretOffset = docLine.Offset + System.Math.Clamp(column - 1, 0, docLine.Length);
        Editor.ScrollTo(line, column);
        Editor.TextArea.Focus();
    }

    // ───────────────────────── zoom, wrap, whitespace ─────────────────────────

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetFontSize(Editor.FontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void SetFontSize(double size)
    {
        size = System.Math.Clamp(size, 8, 32);
        Editor.FontSize = size;
        _settings.FontSize = size;
        Status($"Zoom {size / 13.5 * 100:0}%");
    }

    private void ToggleWordWrap()
    {
        _settings.WordWrap = !_settings.WordWrap;
        Editor.WordWrap = _settings.WordWrap;
        Status(_settings.WordWrap ? "Word wrap on" : "Word wrap off");
    }

    private void ToggleWhitespace()
    {
        _settings.ShowWhitespace = !_settings.ShowWhitespace;
        Editor.Options.ShowSpaces = Editor.Options.ShowTabs = _settings.ShowWhitespace;
    }

    // ───────────────────────── indentation & line endings ─────────────────────────

    /// <summary>Guesses whether a file is indented with tabs or spaces, and how many.</summary>
    private static (bool UseTabs, int Size) DetectIndentation(string text)
    {
        int tabs = 0, spaces = 0, previous = 0;
        var steps = new Dictionary<int, int>();
        foreach (var raw in text.Split('\n').Take(2000))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line[0] == '\t') { tabs++; continue; }
            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent > 0) spaces++;
            var step = System.Math.Abs(indent - previous);
            if (step is >= 2 and <= 8) steps[step] = steps.GetValueOrDefault(step) + 1;
            previous = indent;
        }
        if (tabs > spaces) return (true, 4);
        if (steps.Count == 0) return (false, 4);
        var size = steps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        return (false, size is 2 or 3 or 4 or 8 ? size : 4);
    }

    private void ApplyIndentation(EditorTab tab)
    {
        Editor.Options.ConvertTabsToSpaces = !tab.UseTabs;
        Editor.Options.IndentationSize = tab.IndentSize;
        StatusIndent.Content = tab.UseTabs ? $"Tab Size: {tab.IndentSize}" : $"Spaces: {tab.IndentSize}";
    }

    private void StatusIndent_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        var tab = _active;
        var menu = new ContextMenu();
        void Add(string header, bool check, System.Action action)
        {
            var item = new MenuItem { Header = header, IsChecked = check };
            item.Click += (_, _) => { action(); ApplyIndentation(tab); };
            menu.Items.Add(item);
        }
        Add("Indent Using Spaces", !tab.UseTabs, () => tab.UseTabs = false);
        Add("Indent Using Tabs", tab.UseTabs, () => tab.UseTabs = true);
        menu.Items.Add(new Separator());
        foreach (var size in new[] { 2, 4, 8 })
            Add($"Indent Size: {size}", tab.IndentSize == size, () => tab.IndentSize = size);
        menu.Items.Add(new Separator());
        Add("Convert Indentation to Spaces", false, () => ConvertIndentation(tab, useTabs: false));
        Add("Convert Indentation to Tabs", false, () => ConvertIndentation(tab, useTabs: true));
        OpenMenu(menu, (UIElement)sender);
    }

    private void ConvertIndentation(EditorTab tab, bool useTabs)
    {
        var doc = tab.Document;
        doc.BeginUpdate();
        try
        {
            foreach (var line in doc.Lines.ToList())
            {
                var text = doc.GetText(line);
                var lead = text.Length - text.TrimStart(' ', '\t').Length;
                if (lead == 0) continue;
                var columns = 0;
                foreach (var c in text[..lead]) columns = c == '\t' ? (columns / tab.IndentSize + 1) * tab.IndentSize : columns + 1;
                var replacement = useTabs
                    ? new string('\t', columns / tab.IndentSize) + new string(' ', columns % tab.IndentSize)
                    : new string(' ', columns);
                if (replacement != text[..lead]) doc.Replace(line.Offset, lead, replacement);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
        tab.UseTabs = useTabs;
    }

    private void StatusEol_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null) return;
        var menu = new ContextMenu();
        foreach (var (label, eol) in new[] { ("LF  (Linux / Mac / Git)", "\n"), ("CRLF  (Windows)", "\r\n") })
        {
            var item = new MenuItem { Header = label, IsChecked = EolOf(_active.Document) == eol };
            item.Click += (_, _) => ConvertLineEndings(eol);
            menu.Items.Add(item);
        }
        OpenMenu(menu, (UIElement)sender);
    }

    private void ConvertLineEndings(string eol)
    {
        if (_active == null) return;
        var doc = _active.Document;
        var line = Editor.TextArea.Caret.Line;
        var column = Editor.TextArea.Caret.Column;
        var text = Regex.Replace(doc.Text, "\r\n|\r|\n", eol);
        if (text == doc.Text) return;
        doc.Replace(0, doc.TextLength, text);
        GoToLine(line, column);
        UpdateStatus();
    }

    private static void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    // ───────────────────────── folding ─────────────────────────

    private enum FoldKind { None, Braces, Indent, Xml }

    private static FoldKind FoldingFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".py" or ".pyw" or ".yml" or ".yaml" => FoldKind.Indent,
        ".xml" or ".xaml" or ".csproj" or ".config" or ".svg" or ".resx" or ".html" or ".htm" or ".vue" => FoldKind.Xml,
        ".cs" or ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" or ".json" or ".java" or ".kt" or ".c" or ".cc"
            or ".cpp" or ".h" or ".hpp" or ".go" or ".rs" or ".php" or ".css" or ".scss" or ".swift" or ".dart" => FoldKind.Braces,
        _ => FoldKind.None,
    };

    /// <summary>Folding follows the active document (AvalonEdit ties it to one document).</summary>
    private void InstallFolding(EditorTab tab)
    {
        if (_folding != null)
        {
            FoldingManager.Uninstall(_folding);
            _folding = null;
        }
        if (FoldingFor(tab.Path) == FoldKind.None || tab.Document.TextLength > 3_000_000) return;
        _folding = FoldingManager.Install(Editor.TextArea);
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.FoldingMarkerBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0x63, 0x78));
            margin.FoldingMarkerBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x07, 0x0C, 0x14));
            margin.SelectedFoldingMarkerBrush = new SolidColorBrush(Color.FromRgb(0x19, 0xD7, 0xDF));
            margin.SelectedFoldingMarkerBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0C, 0x1E, 0x2A));
        }
        UpdateFoldings();
        foreach (var folding in _folding.AllFoldings)
            if (tab.FoldedStarts.Contains(folding.StartOffset)) folding.IsFolded = true;
    }

    private void RememberFolds(EditorTab tab)
    {
        tab.FoldedStarts.Clear();
        if (_folding == null) return;
        foreach (var folding in _folding.AllFoldings.Where(f => f.IsFolded)) tab.FoldedStarts.Add(folding.StartOffset);
    }

    private void UpdateFoldings()
    {
        if (_folding == null || _active == null) return;
        var doc = _active.Document;
        try
        {
            switch (FoldingFor(_active.Path))
            {
                case FoldKind.Xml:
                    var foldings = new XmlFoldingStrategy { ShowAttributesWhenFolded = true }.CreateNewFoldings(doc, out var firstError);
                    _folding.UpdateFoldings(foldings, firstError);
                    break;
                case FoldKind.Braces:
                    _folding.UpdateFoldings(BraceFoldings(doc), -1);
                    break;
                case FoldKind.Indent:
                    _folding.UpdateFoldings(IndentFoldings(doc), -1);
                    break;
            }
        }
        catch (System.Exception) { /* half-typed code: keep the previous folds */ }
    }

    private static IEnumerable<NewFolding> BraceFoldings(TextDocument doc)
    {
        var text = doc.Text;
        var list = new List<NewFolding>();
        var stack = new Stack<(int Offset, char Char)>();
        var quote = '\0';
        bool lineComment = false, blockComment = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (lineComment) { if (c == '\n') lineComment = false; continue; }
            if (blockComment) { if (c == '*' && next == '/') { blockComment = false; i++; } continue; }
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote || (c == '\n' && quote != '`')) quote = '\0';
                continue;
            }
            if (c == '/' && next == '/') { lineComment = true; continue; }
            if (c == '/' && next == '*') { blockComment = true; i++; continue; }
            if (c is '"' or '\'' or '`') { quote = c; continue; }
            if (c is '{' or '[') stack.Push((i, c));
            else if (c is '}' or ']' && stack.Count > 0)
            {
                var (open, openChar) = stack.Pop();
                if ((openChar == '{') != (c == '}')) continue;
                if (doc.GetLineByOffset(open).LineNumber < doc.GetLineByOffset(i).LineNumber)
                    list.Add(new NewFolding(open, i + 1) { Name = openChar == '{' ? "{…}" : "[…]" });
            }
        }
        return list.OrderBy(f => f.StartOffset);
    }

    private static IEnumerable<NewFolding> IndentFoldings(TextDocument doc)
    {
        var list = new List<NewFolding>();
        var stack = new Stack<(int Indent, DocumentLine Header)>();
        DocumentLine? lastUsed = null;
        void Close(DocumentLine header)
        {
            if (lastUsed != null && lastUsed.LineNumber > header.LineNumber)
                list.Add(new NewFolding(header.EndOffset, lastUsed.EndOffset) { Name = " …" });
        }
        foreach (var line in doc.Lines)
        {
            var text = doc.GetText(line);
            if (text.Trim().Length == 0) continue;
            var indent = 0;
            foreach (var ch in text)
            {
                if (ch == ' ') indent++;
                else if (ch == '\t') indent += 4;
                else break;
            }
            while (stack.Count > 0 && stack.Peek().Indent >= indent) Close(stack.Pop().Header);
            stack.Push((indent, line));
            lastUsed = line;
        }
        while (stack.Count > 0) Close(stack.Pop().Header);
        return list.OrderBy(f => f.StartOffset);
    }

    private void FoldAll(bool fold)
    {
        if (_folding == null) { Status("Nothing to fold in this file."); return; }
        foreach (var folding in _folding.AllFoldings) folding.IsFolded = fold;
    }

    private void ToggleFoldAtCaret()
    {
        if (_folding == null) return;
        var line = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
        var folding = _folding.GetFoldingsContaining(line.EndOffset).LastOrDefault()
                      ?? _folding.GetFoldingsAt(line.EndOffset).LastOrDefault();
        if (folding != null) folding.IsFolded = !folding.IsFolded;
    }

    // ───────────────────────── brackets & occurrences ─────────────────────────

    private void UpdateBracket()
    {
        if (_active == null || Editor.Document.TextLength > 5_000_000) { _bracketRenderer.Pair = null; return; }
        var pair = BracketRenderer.Find(Editor.Document, Editor.CaretOffset);
        if (pair == _bracketRenderer.Pair) return;
        _bracketRenderer.Pair = pair;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void UpdateOccurrences()
    {
        if (_active == null) return;
        string? word = null;
        var whole = true;
        if (!Editor.TextArea.Selection.IsEmpty)
        {
            var selected = Editor.SelectedText;
            if (selected.Length is >= 2 and <= 100 && !selected.Contains('\n') && selected.Trim().Length == selected.Length)
            {
                word = selected;
                whole = Regex.IsMatch(selected, @"^\w+$");
            }
        }
        else
        {
            var doc = Editor.Document;
            var caret = Editor.CaretOffset;
            int start = caret, end = caret;
            while (start > 0 && IsWordChar(doc.GetCharAt(start - 1))) start--;
            while (end < doc.TextLength && IsWordChar(doc.GetCharAt(end))) end++;
            if (end - start >= 2)
            {
                var candidate = doc.GetText(start, end - start);
                if (!char.IsDigit(candidate[0])) word = candidate;
            }
        }
        _occurrenceRenderer.SetWord(word, whole);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ───────────────────────── problems (syntax check) ─────────────────────────

    private static bool CanCheck(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".py" or ".pyw" or ".json" or ".xml" or ".xaml" or ".csproj" or ".config" or ".svg" or ".resx";

    private async Task CheckProblemsAsync(EditorTab? tab)
    {
        if (tab == null) return;
        if (!_settings.CheckProblems || !CanCheck(tab.Path))
        {
            SetProblems(tab, new List<Problem>());
            return;
        }
        var version = ++tab.CheckVersion;
        var text = tab.Document.Text;
        List<Problem> problems;
        switch (Path.GetExtension(tab.Path).ToLowerInvariant())
        {
            case ".py" or ".pyw":
                problems = await CheckPythonAsync(text, tab.Path);
                break;
            case ".json":
                problems = CheckJson(text);
                break;
            default:
                problems = CheckXml(text);
                break;
        }
        if (version != tab.CheckVersion || !_tabs.Contains(tab)) return;   // the file changed meanwhile
        SetProblems(tab, problems);
    }

    private void SetProblems(EditorTab tab, List<Problem> problems)
    {
        tab.Problems = problems;
        if (tab != _active) return;
        _problemRenderer.Problems = problems;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        UpdateProblemsStatus();
    }

    private void UpdateProblemsStatus()
    {
        var problems = _active?.Problems ?? new List<Problem>();
        if (problems.Count == 0)
        {
            StatusProblems.Visibility = Visibility.Collapsed;
            return;
        }
        var p = problems[0];
        StatusProblems.Content = $"\u2297 {problems.Count}   Ln {p.Line}: {p.Message}";
        StatusProblems.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        StatusProblems.Visibility = Visibility.Visible;
    }

    private void StatusProblems_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.Problems.FirstOrDefault() is { } p) GoToLine(p.Line, p.Column);
    }

    private const string PythonCheckScript =
        "import sys, json\n" +
        "src = sys.stdin.buffer.read().decode('utf-8', 'replace')\n" +
        "try:\n" +
        "    compile(src, sys.argv[1], 'exec', dont_inherit=True)\n" +
        "    print('[]')\n" +
        "except SyntaxError as e:\n" +
        "    print(json.dumps([{'line': e.lineno or 1, 'col': e.offset or 1, 'msg': e.msg}]))\n" +
        "except Exception:\n" +
        "    print('[]')\n";

    /// <summary>Asks Python itself whether the file compiles (no code is run).</summary>
    private async Task<List<Problem>> CheckPythonAsync(string text, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(PythonFor(Path.GetDirectoryName(path)!))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(PythonCheckScript);
            psi.ArgumentList.Add(Path.GetFileName(path));
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = Process.Start(psi);
            if (process == null) return new List<Problem>();
            var bytes = new UTF8Encoding(false).GetBytes(text);
            await process.StandardInput.BaseStream.WriteAsync(bytes);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(System.TimeSpan.FromSeconds(6));
            using var json = JsonDocument.Parse(output.Trim());
            return json.RootElement.EnumerateArray()
                .Select(p => new Problem(p.GetProperty("line").GetInt32(), p.GetProperty("col").GetInt32(), p.GetProperty("msg").GetString() ?? "syntax error"))
                .ToList();
        }
        catch (System.Exception)
        {
            return new List<Problem>();   // no Python installed, or it timed out: say nothing
        }
    }

    private static List<Problem> CheckJson(string text)
    {
        if (text.Trim().Length == 0) return new List<Problem>();
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return new List<Problem>();
        }
        catch (JsonException ex)
        {
            var message = Regex.Replace(ex.Message, @"\s*(Path:|LineNumber:).*$", "").Trim();
            return new List<Problem> { new((int)(ex.LineNumber ?? 0) + 1, (int)(ex.BytePositionInLine ?? 0) + 1, message) };
        }
    }

    private static List<Problem> CheckXml(string text)
    {
        if (text.Trim().Length == 0) return new List<Problem>();
        try
        {
            System.Xml.Linq.XDocument.Parse(text);
            return new List<Problem>();
        }
        catch (System.Xml.XmlException ex)
        {
            var message = Regex.Replace(ex.Message, @"\s*Line \d+, position \d+\.?$", "").Trim();
            return new List<Problem> { new(System.Math.Max(1, ex.LineNumber), System.Math.Max(1, ex.LinePosition), message) };
        }
    }

    private void TextView_MouseHover(object sender, MouseEventArgs e)
    {
        var view = Editor.TextArea.TextView;
        var position = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        if (position == null || _problemRenderer.At(position.Value.Line) is not { } problem) return;
        _problemTip ??= new ToolTip { Placement = PlacementMode.Mouse };
        _problemTip.PlacementTarget = Editor;
        _problemTip.Content = "\u2297  " + problem.Message;
        _problemTip.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The project's own Python (a .venv / venv folder) if it has one.</summary>
    private string PythonFor(string folder)
    {
        foreach (var root in new[] { _workspace, folder }.Where(r => r != null).Distinct())
        {
            foreach (var env in new[] { ".venv", "venv", "env" })
            {
                var exe = Path.Combine(root!, env, "Scripts", "python.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return "python";
    }
}
