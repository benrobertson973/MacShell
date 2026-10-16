using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacShell.Apps.Notes;

/// <summary>
/// What a note holds, as Apple Notes does: paragraphs - each with a style (title, heading, subheading, body,
/// monostyled), maybe a list (bulleted, dashed, numbered, checklist) at an indent, made of runs of text (bold, italic,
/// underline, strikethrough, links) - and pictures. Saved as note.json beside the note's pictures.
/// </summary>
public sealed class NoteDoc
{
    public int V { get; set; } = 1;
    public List<NoteBlock> Blocks { get; set; } = new();

    static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    public static NoteDoc FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<NoteDoc>(json, Opts) ?? new NoteDoc(); }
        catch { return new NoteDoc(); }
    }

    /// <summary>A new note: an empty title line (new notes start with a title, as on a Mac).</summary>
    public static NoteDoc Empty() => new() { Blocks = { new NoteBlock { Style = "title" } } };

    public static NoteDoc FromText(string text)
    {
        var d = new NoteDoc();
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
            d.Blocks.Add(new NoteBlock { Style = i == 0 ? "title" : "body", Runs = { new NoteRun { T = lines[i] } } });
        return d;
    }

    /// <summary>The note's words, one paragraph a line (pictures left out).</summary>
    public string PlainText()
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks)
        {
            if (b.Image != null) continue;
            sb.Append(b.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The title: the first line with words in it.</summary>
    public string Title()
    {
        foreach (var b in Blocks)
        {
            if (b.Image != null) continue;
            string t = b.Text.Trim();
            if (t.Length > 0) return t.Length > 120 ? t[..120] : t;
        }
        return "";
    }

    /// <summary>The preview under the title in the list: the next words.</summary>
    public string Preview()
    {
        bool titleSeen = false;
        var sb = new StringBuilder();
        foreach (var b in Blocks)
        {
            if (b.Image != null) continue;
            string t = b.Text.Trim();
            if (t.Length == 0) continue;
            if (!titleSeen) { titleSeen = true; continue; }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
            if (sb.Length > 160) break;
        }
        string s = sb.ToString();
        return s.Length > 160 ? s[..160] : s;
    }

    public string FirstImage() => Blocks.FirstOrDefault(b => b.Image != null)?.Image;
}

public sealed class NoteBlock
{
    /// <summary>title | heading | subheading | body | mono</summary>
    public string Style { get; set; } = "body";
    /// <summary>null | bullet | dash | number | check</summary>
    public string List { get; set; }
    public int Indent { get; set; }
    public bool Checked { get; set; }
    public bool Quote { get; set; }
    public List<NoteRun> Runs { get; set; } = new();
    /// <summary>A picture (this block is only that): its file in the note's folder, and its size in pixels.</summary>
    public string Image { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    [JsonIgnore] public string Text => string.Concat(Runs.Select(r => r.T));
    [JsonIgnore] public bool IsEmpty => Image == null && Runs.All(r => string.IsNullOrEmpty(r.T));
}

public sealed class NoteRun
{
    public string T { get; set; } = "";
    public bool B { get; set; }
    public bool I { get; set; }
    public bool U { get; set; }
    public bool S { get; set; }
    public bool Mono { get; set; }
    public string Link { get; set; }

    public bool SameLook(NoteRun o) => B == o.B && I == o.I && U == o.U && S == o.S && Mono == o.Mono && Link == o.Link;
}
