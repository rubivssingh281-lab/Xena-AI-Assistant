using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Xena.Desktop;

/// <summary>An open file in the Code Agent editor.</summary>
internal sealed class EditorTab
{
    public EditorTab(string path, TextDocument document)
    {
        Path = path;
        Document = document;
        // Attached to the document, so highlighted ranges move with later edits.
        Changed = new TextSegmentCollection<TextSegment>(document);
    }

    public string Path { get; set; }
    public TextDocument Document { get; }
    public IHighlightingDefinition? Highlighting { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool IsNew { get; set; }                      // not on disk yet
    public System.DateTime DiskTime { get; set; }        // last-write time when loaded or saved
    public int CaretOffset { get; set; }
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public TextSegmentCollection<TextSegment> Changed { get; }   // lines Xena just changed (highlighted)
    public bool UseTabs { get; set; }                    // indentation detected from the file
    public int IndentSize { get; set; } = 4;
    public HashSet<int> FoldedStarts { get; } = new();   // collapsed regions, kept while the tab is in the background
    public List<Problem> Problems { get; set; } = new(); // syntax errors found by the last check
    public int CheckVersion { get; set; }                // discards results of an outdated check

    public Border Header { get; set; } = null!;
    public TextBlock Title { get; set; } = null!;
    public TextBlock DirtyMark { get; set; } = null!;

    public bool IsDirty => IsNew || !Document.UndoStack.IsOriginalFile;
    public string FileName => System.IO.Path.GetFileName(Path);
}

internal enum HunkState { Pending, Accepted, Rejected, Failed }

/// <summary>One change Xena proposed: replace <see cref="Old"/> (starting at line
/// <see cref="OldStart"/>, 0-based, in the file as it was sent) with <see cref="New"/>.</summary>
internal sealed class ProposedHunk
{
    public required string FullPath { get; init; }
    public required int OldStart { get; init; }
    public required List<string> Old { get; init; }
    public required List<string> New { get; init; }
    public required List<string> Before { get; init; }
    public required List<string> After { get; init; }
    public int Shift { get; set; }                        // line shift from hunks accepted above it
    public HunkState State { get; set; } = HunkState.Pending;
    public Button? AcceptButton { get; set; }
    public Button? RejectButton { get; set; }
    public TextBlock? StateText { get; set; }
    public System.Action? Resolved { get; set; }          // lets the proposal hide "Accept all" when done
}

internal sealed class ProposedNewFile
{
    public required string FullPath { get; init; }
    public required string Content { get; init; }
    public HunkState State { get; set; } = HunkState.Pending;
    public Button? AcceptButton { get; set; }
    public Button? RejectButton { get; set; }
    public TextBlock? StateText { get; set; }
    public System.Action? Resolved { get; set; }
}

/// <summary>Soft green background behind lines Xena changed (anchored to the text, so
/// it moves with later edits).</summary>
internal sealed class ChangedLinesRenderer : IBackgroundRenderer
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x29, 0xD1, 0x9B)));
    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x29, 0xD1, 0x9B)));
    public TextSegmentCollection<TextSegment>? Segments { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Segments == null || Segments.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var segment in Segments)
        {
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, true))
            {
                dc.DrawRectangle(Fill, null, new Rect(0, r.Top, textView.ActualWidth, r.Height));
                dc.DrawRectangle(Edge, null, new Rect(0, r.Top, 3, r.Height));
            }
        }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

/// <summary>AvalonEdit's built-in syntax colours are made for white backgrounds; this
/// keeps each colour's hue but lifts it so it reads well on the dark editor.</summary>
internal static class DarkHighlighting
{
    private static readonly Dictionary<string, IHighlightingDefinition?> Cache = new();
    private static readonly HashSet<IHighlightingDefinition> Adapted = new();
    private static readonly Dictionary<string, string> Aliases = new()
    {
        [".ts"] = ".js", [".tsx"] = ".js", [".jsx"] = ".js", [".mjs"] = ".js", [".cjs"] = ".js",
        [".pyw"] = ".py", [".xaml"] = ".xml", [".csproj"] = ".xml", [".config"] = ".xml",
        [".resx"] = ".xml", [".svg"] = ".xml", [".vue"] = ".html", [".htm"] = ".html",
        [".hpp"] = ".cpp", [".cc"] = ".cpp", [".psm1"] = ".ps1", [".kt"] = ".java",
    };

    public static IHighlightingDefinition? For(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (Aliases.TryGetValue(ext, out var alias)) ext = alias;
        if (Cache.TryGetValue(ext, out var cached)) return cached;
        // .md maps to "MarkDownWithFontSize" (huge headings); keep one font size like VS Code.
        var def = ext is ".md" or ".markdown"
            ? HighlightingManager.Instance.GetDefinition("MarkDown")
            : HighlightingManager.Instance.GetDefinitionByExtension(ext);
        if (def != null && Adapted.Add(def))
        {
            foreach (var color in def.NamedHighlightingColors) Adapt(color);
        }
        return Cache[ext] = def;
    }

    private static void Adapt(HighlightingColor color)
    {
        try
        {
            if (color.Foreground?.GetColor(null) is Color fg)
                color.Foreground = new SimpleHighlightingBrush(Lift(fg));
            color.Background = null;   // light-theme backgrounds look wrong on dark
        }
        catch (System.InvalidOperationException) { /* frozen colour: keep as is */ }
    }

    private static Color Lift(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = System.Math.Max(r, System.Math.Max(g, b)), min = System.Math.Min(r, System.Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        l = System.Math.Max(l, 0.70);
        s = System.Math.Min(s, 0.75);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        byte B(double v) => (byte)System.Math.Round(System.Math.Clamp(v, 0, 1) * 255);
        return s == 0
            ? Color.FromRgb(B(l), B(l), B(l))
            : Color.FromRgb(B(Hue(p, q, h + 1.0 / 3)), B(Hue(p, q, h)), B(Hue(p, q, h - 1.0 / 3)));
    }
}

/// <summary>The files that were open in a workspace, to reopen them next time.</summary>
internal sealed class WorkspaceSession
{
    public List<SessionFile> Files { get; set; } = new();
    public string? Active { get; set; }
}

internal sealed class SessionFile
{
    public string Path { get; set; } = "";
    public int Caret { get; set; }
    public double ScrollY { get; set; }
}

/// <summary>Remembers the workspace, open files, layout and editor preferences between sessions.</summary>
internal sealed class CodeAgentSettings
{
    public string? Workspace { get; set; }
    public bool AutoSave { get; set; } = true;          // write Xena's accepted changes straight to disk
    public bool IncludeOpenFiles { get; set; }
    public List<string> RecentFolders { get; set; } = new();
    public Dictionary<string, WorkspaceSession> Sessions { get; set; } = new();   // key: lower-case folder path
    public List<string> AgentHistory { get; set; } = new();

    // Editor preferences
    public double FontSize { get; set; } = 13.5;
    public bool WordWrap { get; set; }
    public bool ShowWhitespace { get; set; }
    public bool AutoClosePairs { get; set; } = true;
    public bool CheckProblems { get; set; } = true;
    public bool AutoSaveEdits { get; set; }             // save by themselves after a short pause / when leaving the window
    public bool TrimTrailingWhitespace { get; set; }
    public bool InsertFinalNewline { get; set; }

    // Layout
    public WindowBounds? Window { get; set; }
    public double SidebarWidth { get; set; } = 250;
    public double ChatWidth { get; set; } = 410;
    public double TerminalHeight { get; set; } = 220;
    public bool TerminalVisible { get; set; } = true;

    private static string FilePath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Xena", "code_agent.json");

    public static CodeAgentSettings Load()
    {
        try { return JsonSerializer.Deserialize<CodeAgentSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience */ }
    }
}
