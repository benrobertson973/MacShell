using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace MacShell.Services;

/// <summary>
/// Things dragged out of a web browser (a picture, a link to a file): browsers don't hand over files but "virtual
/// files" (FileGroupDescriptorW + FileContents: the bytes and a name), usually with the address too. This saves them
/// into a folder the way Windows' own desktop does — the bytes when the browser gives them, else a download from the
/// address (a picture's src for picture drags). The drag's data must be read during the drop: <see cref="Extract"/>
/// copies it out, <see cref="SaveAsync"/> writes the files.
/// </summary>
public static class WebDrop
{
    public sealed class Item
    {
        public string Name;
        public byte[] Data;   // the browser's bytes, or null: download Url
        public string Url;
    }

    static readonly string[] Formats = { "FileGroupDescriptorW", "UniformResourceLocatorW", "UniformResourceLocator", "text/x-moz-url", "HTML Format" };

    /// <summary>Whether the drag carries something from a web page to save (and no real files).</summary>
    public static bool Has(IDataObject d)
    {
        try { return !d.GetDataPresent(DataFormats.FileDrop) && Formats.Any(d.GetDataPresent); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ reading the drag (during the drop)

    public static List<Item> Extract(IDataObject d)
    {
        var items = new List<Item>();
        try
        {
            if (d.GetDataPresent("FileGroupDescriptorW") && d.GetData("FileGroupDescriptorW") is MemoryStream desc)
            {
                var names = Descriptors(desc.ToArray());
                for (int i = 0; i < names.Count; i++)
                {
                    var bytes = FileContents(d, i);
                    if (bytes != null && bytes.Length > 0) items.Add(new Item { Name = names[i], Data = bytes });
                }
            }
            if (items.Count > 0) return items;
            // no bytes: the picture's own address from the page's HTML, else the dragged link
            string html = d.GetDataPresent("HTML Format") ? d.GetData("HTML Format") as string : null;
            string url = ReadUrl(d);
            string img = html != null ? ImageSrc(html, url) : null;
            string src = img ?? url;
            if (!string.IsNullOrEmpty(src) && src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(src, @"^data:image/([a-z0-9+.-]+);base64,(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (m.Success) items.Add(new Item { Name = "image." + ExtFor("image/" + m.Groups[1].Value), Data = Convert.FromBase64String(m.Groups[2].Value) });
            }
            else if (!string.IsNullOrEmpty(src) && Uri.TryCreate(src, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                items.Add(new Item { Url = src, Name = NameFromUrl(u) });
        }
        catch (Exception ex) { App.Log("WebDrop.Extract: " + ex.Message); }
        return items;
    }

    static string ReadUrl(IDataObject d)
    {
        foreach (var f in new[] { "UniformResourceLocatorW", "UniformResourceLocator", "text/x-moz-url" })
        {
            if (!d.GetDataPresent(f)) continue;
            var o = d.GetData(f);
            string s = o switch
            {
                string str => str,
                MemoryStream ms => f == "UniformResourceLocator" ? Encoding.Default.GetString(ms.ToArray()) : Encoding.Unicode.GetString(ms.ToArray()),
                _ => null,
            };
            s = s?.Split('\0', '\r', '\n')[0].Trim();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    /// <summary>The first &lt;img src&gt; of the dragged HTML fragment, made absolute.</summary>
    static string ImageSrc(string html, string pageOrLink)
    {
        var m = Regex.Match(html, @"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        var baseUrl = Regex.Match(html, @"^SourceURL:(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        if (Uri.TryCreate(src, UriKind.Absolute, out var abs)) return abs.ToString();
        foreach (var b in new[] { baseUrl, pageOrLink })
            if (Uri.TryCreate(b, UriKind.Absolute, out var bu) && Uri.TryCreate(bu, src, out var r)) return r.ToString();
        return null;
    }

    /// <summary>FILEGROUPDESCRIPTORW: a count, then 592-byte FILEDESCRIPTORWs with the name at offset 72.</summary>
    static List<string> Descriptors(byte[] b)
    {
        var names = new List<string>();
        if (b.Length < 4) return names;
        int n = BitConverter.ToInt32(b, 0);
        for (int i = 0; i < n; i++)
        {
            int off = 4 + i * 592 + 72;
            if (off + 520 > b.Length) break;
            names.Add(Encoding.Unicode.GetString(b, off, 520).Split('\0')[0]);
        }
        return names;
    }

    /// <summary>FileContents for one descriptor (lindex = its index), as a stream or global memory.</summary>
    static byte[] FileContents(IDataObject d, int index)
    {
        if (d is ComTypes.IDataObject com)
        {
            var fmt = new ComTypes.FORMATETC
            {
                cfFormat = (short)DataFormats.GetDataFormat("FileContents").Id,
                dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                tymed = ComTypes.TYMED.TYMED_ISTREAM | ComTypes.TYMED.TYMED_HGLOBAL,
            };
            try
            {
                com.GetData(ref fmt, out var med);
                try { return ReadMedium(med); }
                finally { ReleaseStgMedium(ref med); }
            }
            catch { }
        }
        if (index == 0 && d.GetData("FileContents") is MemoryStream ms) return ms.ToArray();
        return null;
    }

    static byte[] ReadMedium(ComTypes.STGMEDIUM med)
    {
        if (med.tymed == ComTypes.TYMED.TYMED_ISTREAM)
        {
            var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(med.unionmember);
            using var ms = new MemoryStream();
            var buf = new byte[65536];
            IntPtr pRead = Marshal.AllocCoTaskMem(4);
            try
            {
                while (true)
                {
                    stream.Read(buf, buf.Length, pRead);
                    int got = Marshal.ReadInt32(pRead);
                    if (got <= 0) break;
                    ms.Write(buf, 0, got);
                }
            }
            finally { Marshal.FreeCoTaskMem(pRead); }
            return ms.ToArray();
        }
        if (med.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
        {
            IntPtr p = GlobalLock(med.unionmember);
            try
            {
                int size = (int)GlobalSize(med.unionmember);
                var data = new byte[size];
                Marshal.Copy(p, data, 0, size);
                return data;
            }
            finally { GlobalUnlock(med.unionmember); }
        }
        return null;
    }

    // ------------------------------------------------------------------ writing

    static readonly HttpClient Http = MakeClient();
    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MacShell");
        return c;
    }

    /// <summary>
    /// Writes the items into <paramref name="folder"/> (unique names, "name 2.jpg"); <paramref name="placing"/> gets each
    /// final path just before it is written (the desktop puts the icon where it was dropped). Returns the saved files.
    /// </summary>
    public static async Task<List<string>> SaveAsync(List<Item> items, string folder, Action<string, int> placing)
    {
        var saved = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            try
            {
                byte[] data = it.Data;
                string name = Clean(it.Name);
                if (data == null && it.Url != null)
                {
                    using var resp = await Http.GetAsync(it.Url);
                    resp.EnsureSuccessStatusCode();
                    data = await resp.Content.ReadAsByteArrayAsync();
                    var cd = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName;
                    if (!string.IsNullOrWhiteSpace(cd)) name = Clean(cd.Trim('"'));
                    string type = resp.Content.Headers.ContentType?.MediaType;
                    if (type != null && type.StartsWith("image/") && !Path.HasExtension(name)) name += "." + ExtFor(type);
                    if (type == "text/html" && !Path.HasExtension(name)) name += ".html";
                }
                if (data == null || data.Length == 0) continue;
                if (!Path.HasExtension(name) && SniffExt(data) is { } ext) name += ext;
                string path = Unique(Path.Combine(folder, name));
                placing?.Invoke(path, i);
                await File.WriteAllBytesAsync(path, data);
                // like a browser download: marked as from the internet
                try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n" + (it.Url != null ? $"HostUrl={it.Url}\r\n" : "")); } catch { }
                saved.Add(path);
            }
            catch (Exception ex)
            {
                ShellHost.ShowAlert($"“{it.Name}” couldn’t be saved.", ex.Message);
            }
        }
        return saved;
    }

    static string NameFromUrl(Uri u)
    {
        string n = Path.GetFileName(Uri.UnescapeDataString(u.AbsolutePath));
        return string.IsNullOrWhiteSpace(n) ? "download" : n;
    }

    static string Clean(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "download" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 150 ? name[..150] : name;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} {i}{ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static string ExtFor(string mime) => mime.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/pjpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        "image/bmp" => "bmp",
        "image/avif" => "avif",
        "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
        _ => mime.Contains('/') ? mime[(mime.IndexOf('/') + 1)..] : "bin",
    };

    static string SniffExt(byte[] d)
    {
        if (d.Length < 12) return null;
        if (d[0] == 0xFF && d[1] == 0xD8) return ".jpg";
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return ".png";
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return ".gif";
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ".webp";
        if (d[0] == 'B' && d[1] == 'M') return ".bmp";
        return null;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}
