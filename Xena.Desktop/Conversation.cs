using System.Collections.Generic;
using System.Windows.Media;

namespace Xena.Desktop;

/// <summary>Base type for every card rendered inside the Neural Conversation Stream.</summary>
public abstract class Entry { }

// ---------------------------------------------------------------------------
// System initiation / protocol warning block
// ---------------------------------------------------------------------------
public sealed class ProtocolLine
{
    public string Prefix { get; set; } = "";
    public string Risk { get; set; } = "";
    public Brush RiskBrush { get; set; } = Brushes.Gray;
    public string Rest { get; set; } = "";
}

public sealed class ProtocolEntry : Entry
{
    public string Header { get; set; } = "";
    public List<ProtocolLine> Lines { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Tool execution result block
// ---------------------------------------------------------------------------
public sealed class ToolLine
{
    public string Text { get; set; } = "";
    public string Highlight { get; set; } = "";
    public Brush HighlightBrush { get; set; } = Brushes.Gray;
}

public sealed class ToolEntry : Entry
{
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public Brush StatusBrush { get; set; } = Brushes.Gray;
    public Brush StatusBg { get; set; } = Brushes.Transparent;
    public List<ToolLine> Lines { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Agent message (Xena broadcast / synapse overlord / system notices)
// ---------------------------------------------------------------------------
public sealed class Pill
{
    public string Text { get; set; } = "";
    public Brush Brush { get; set; } = Brushes.Gray;
    public Brush Background { get; set; } = Brushes.Transparent;
}

public enum EntryKind { Xena, System, Error }

public sealed class AgentEntry : Entry, System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public EntryKind Kind { get; set; } = EntryKind.Xena;   // for the conversation filters

    public string Glyph { get; set; } = "⚙"; // gear by default
    public string Title { get; set; } = "";
    public string Timestamp { get; set; } = "";

    // Notifies the UI so a streamed reply can grow word by word.
    private string _body = "";
    public string Body
    {
        get => _body;
        set
        {
            if (_body == value) return;
            _body = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Body)));
        }
    }
    public Brush Accent { get; set; } = Brushes.Gray;
    public Brush AvatarBg { get; set; } = Brushes.Transparent;
    public List<Pill> Pills { get; set; } = new();
}

// ---------------------------------------------------------------------------
// User directive (right-aligned cyan card)
// ---------------------------------------------------------------------------
public sealed class UserDirectiveEntry : Entry
{
    public string Title { get; set; } = "USER DIRECTIVE";
    public string Timestamp { get; set; } = "";
    public string Body { get; set; } = "";
}
