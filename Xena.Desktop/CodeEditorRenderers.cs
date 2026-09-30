using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Xena.Desktop;

/// <summary>A syntax problem found in a file (1-based line and column).</summary>
internal sealed record Problem(int Line, int Column, string Message);

internal static class Paint
{
    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(byte a, byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Brush(a, r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}

/// <summary>Outlines the bracket next to the caret and the one that matches it.</summary>
internal sealed class BracketRenderer : IBackgroundRenderer
{
    private static readonly Brush Fill = Paint.Brush(0x30, 0x19, 0xD7, 0xDF);
    private static readonly Pen Edge = Paint.Pen(0xA0, 0x19, 0xD7, 0xDF, 1);
    public (int Open, int Close)? Pair { get; set; }
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Pair is not { } pair || textView.Document == null) return;
        foreach (var offset in new[] { pair.Open, pair.Close })
        {
            if (offset < 0 || offset >= textView.Document.TextLength) continue;
            var segment = new TextSegment { StartOffset = offset, Length = 1 };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                dc.DrawRectangle(Fill, Edge, new Rect(r.X, r.Y, r.Width, r.Height));
        }
    }

    private const string Opens = "([{", Closes = ")]}";

    /// <summary>The bracket pair at the caret (just before or just after it), if any.</summary>
    public static (int Open, int Close)? Find(TextDocument doc, int caret)
    {
        foreach (var at in new[] { caret - 1, caret })
        {
            if (at < 0 || at >= doc.TextLength) continue;
            var c = doc.GetCharAt(at);
            int i;
            if ((i = Opens.IndexOf(c)) >= 0)
            {
                var match = Scan(doc, at, +1, c, Closes[i]);
                if (match >= 0) return (at, match);
            }
            else if ((i = Closes.IndexOf(c)) >= 0)
            {
                var match = Scan(doc, at, -1, c, Opens[i]);
                if (match >= 0) return (match, at);
            }
        }
        return null;
    }

    private static int Scan(TextDocument doc, int from, int step, char self, char other)
    {
        var depth = 0;
        var limit = 40_000;
        for (var i = from; i >= 0 && i < doc.TextLength && limit-- > 0; i += step)
        {
            var c = doc.GetCharAt(i);
            if (c == self) depth++;
            else if (c == other && --depth == 0) return i;
        }
        return -1;
    }
}

/// <summary>Softly highlights the other places the selected word (or the word under the
/// caret) appears on screen, like VS Code.</summary>
internal sealed class OccurrenceRenderer : IBackgroundRenderer
{
    private static readonly Brush Fill = Paint.Brush(0x38, 0x6E, 0x8E, 0xA8);
    private static readonly Pen Edge = Paint.Pen(0x50, 0x9F, 0xC7, 0xD3, 1);
    private Regex? _pattern;
    public KnownLayer Layer => KnownLayer.Selection;

    public void SetWord(string? text, bool wholeWord)
    {
        _pattern = string.IsNullOrEmpty(text)
            ? null
            : new Regex(wholeWord ? $@"(?<![\w]){Regex.Escape(text)}(?![\w])" : Regex.Escape(text), RegexOptions.CultureInvariant);
    }

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_pattern == null || textView.Document == null || !textView.VisualLinesValid) return;
        var doc = textView.Document;
        foreach (var visual in textView.VisualLines)
        {
            var line = visual.FirstDocumentLine;
            var start = line.Offset;
            var end = visual.LastDocumentLine.EndOffset;
            var text = doc.GetText(start, end - start);
            foreach (Match m in _pattern.Matches(text))
            {
                var segment = new TextSegment { StartOffset = start + m.Index, Length = m.Length };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    dc.DrawRectangle(Fill, Edge, new Rect(r.X, r.Y, r.Width, r.Height));
            }
        }
    }
}

/// <summary>Red wavy underline under syntax problems.</summary>
internal sealed class ProblemRenderer : IBackgroundRenderer
{
    private static readonly Pen Wave = Paint.Pen(0xFF, 0xF1, 0x4C, 0x4C, 1.1);
    private static readonly Brush LineTint = Paint.Brush(0x18, 0xF1, 0x4C, 0x4C);
    public IReadOnlyList<Problem> Problems { get; set; } = new List<Problem>();
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        var doc = textView.Document;
        if (doc == null || Problems.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var p in Problems)
        {
            if (p.Line < 1 || p.Line > doc.LineCount) continue;
            var line = doc.GetLineByNumber(p.Line);
            var text = doc.GetText(line);
            // Underline from the reported column to the end of that word (or the whole line).
            var col = System.Math.Clamp(p.Column - 1, 0, System.Math.Max(0, text.Length - 1));
            var len = 1;
            while (col + len < text.Length && !char.IsWhiteSpace(text[col + len])) len++;
            if (text.Trim().Length == 0) { col = 0; len = System.Math.Max(1, text.Length); }
            var segment = new TextSegment { StartOffset = line.Offset + System.Math.Min(col, text.Length), Length = System.Math.Max(1, System.Math.Min(len, text.Length - col)) };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = line.Offset, Length = line.Length }))
                dc.DrawRectangle(LineTint, null, new Rect(0, r.Top, textView.ActualWidth, r.Height));
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var y = r.Bottom - 1;
                    var x = r.Left;
                    var width = System.Math.Max(r.Width, 6);
                    ctx.BeginFigure(new Point(x, y), false, false);
                    var up = true;
                    for (var dx = 2.0; dx <= width; dx += 2)
                    {
                        ctx.LineTo(new Point(x + dx, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, Wave, geometry);
            }
        }
    }

    public Problem? At(int line) => Problems.FirstOrDefault(p => p.Line == line);
}
