using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MimeKit;
using MacShell.Controls;
using MacShell.Finder;
using MacShell.Native;
using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>
/// The message on the right of Mail's window: its header (the sender's initials, name, date, subject, To and Cc),
/// the message shown by Edge's engine (WebView2) - with scripts off, links opening in the web browser and nothing
/// downloaded - and its attachments (click to open, right-click to save, drag out).
/// </summary>
public sealed class MailViewer : DockPanel
{
    const string Host = "mail.macshell.local";
    static readonly string ViewDir = Path.Combine(MailStore.Root, "view");
    static Task<CoreWebView2Environment> _env;

    readonly Grid _header = new() { Margin = new Thickness(20, 16, 20, 12) };
    readonly Border _avatar = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _initials = new() { Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _from = new() { FontSize = 14, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _date = new() { FontSize = 11.5, Margin = new Thickness(10, 1, 0, 0) };
    readonly TextBlock _subject = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _to = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
    readonly Border _line = new() { Height = 1, Margin = new Thickness(20, 0, 20, 0) };
    readonly WebView2 _web = new() { Visibility = Visibility.Hidden };
    readonly TextBox _plain = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Background = Brushes.Transparent };
    readonly TextBlock _empty = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Text = "No Message Selected" };
    readonly WrapPanel _attachments = new() { Margin = new Thickness(16, 8, 16, 12) };
    readonly Border _attachBar = new() { BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };

    MailMessageInfo _info;
    int _gen;
    bool _ready, _failed;
    (MailMessageInfo info, MimeMessage msg)? _pending;

    public MimeMessage Message { get; private set; }

    public MailViewer()
    {
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition());
        _avatar.Background = new LinearGradientBrush(Color.FromRgb(0xA6, 0xA6, 0xAB), Color.FromRgb(0x82, 0x82, 0x87), 90);
        _avatar.Child = _initials;
        _header.Children.Add(_avatar);
        var lines = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var top = new DockPanel();
        DockPanel.SetDock(_date, Dock.Right);
        top.Children.Add(_date);
        top.Children.Add(_from);
        lines.Children.Add(top);
        lines.Children.Add(_subject);
        lines.Children.Add(_to);
        Grid.SetColumn(lines, 1);
        _header.Children.Add(lines);
        foreach (var t in new[] { _from, _subject }) t.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
        foreach (var t in new[] { _date, _to }) t.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TertiaryLabelBrush");
        _line.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        _plain.SetResourceReference(TextBox.ForegroundProperty, "LabelBrush");
        _plain.FontFamily = Theme.Font;
        _plain.FontSize = 14;
        _attachBar.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        _attachBar.Child = _attachments;

        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_line, Dock.Top);
        DockPanel.SetDock(_attachBar, Dock.Bottom);
        Children.Add(_header);
        Children.Add(_line);
        Children.Add(_attachBar);
        var body = new Grid();
        body.Children.Add(_web);
        body.Children.Add(_plain);
        body.Children.Add(_empty);
        Children.Add(body);
        ShowHeader(false);
        Loaded += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        if (_ready || _failed) return;
        try
        {
            _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(MailStore.Root, "WebView2"));
            var env = await _env;
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            var s = core.Settings;
            s.IsScriptEnabled = false;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            Directory.CreateDirectory(ViewDir);
            core.SetVirtualHostNameToFolderMapping(Host, ViewDir, CoreWebView2HostResourceAccessKind.Deny);
            // the page itself only: a link opens in the web browser (a mailto: link in a new message)
            core.NavigationStarting += (_, e) => { if (!IsOwn(e.Uri)) { e.Cancel = true; OpenLink(e.Uri); } };
            core.FrameNavigationStarting += (_, e) => { if (!IsOwn(e.Uri) && !e.Uri.StartsWith("about:")) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenLink(e.Uri); };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            _ready = true;
            if (_pending is { } p && p.info == _info) Render(p.info, p.msg);
        }
        catch
        {
            _failed = true;   // (no WebView2 runtime: the message's text instead)
            if (_pending is { } p && p.info == _info) Render(p.info, p.msg);
        }
    }

    static bool IsOwn(string uri) => uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase);

    static void OpenLink(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) { MailComposeWindow.FromMailto(uri); return; }
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    void ShowHeader(bool on)
    {
        _header.Visibility = _line.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (!on)
        {
            _web.Visibility = Visibility.Hidden;
            _plain.Visibility = Visibility.Collapsed;
            _attachBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>A message (null: none, or several - <paramref name="count"/> - chosen).</summary>
    public async void Show(MailMessageInfo m, int count)
    {
        _info = m;
        Message = null;
        _pending = null;
        if (m == null)
        {
            ShowHeader(false);
            _empty.Text = count > 1 ? $"{count} Messages Selected" : "No Message Selected";
            return;
        }
        ShowHeader(true);
        _from.Text = m.Sender;
        _initials.Text = Initials(m.FromName, m.FromAddress);
        _date.Text = LongDate(m.Date);
        _subject.Text = string.IsNullOrWhiteSpace(m.Subject) ? "(No Subject)" : m.Subject;
        _to.Text = "To: " + Names(m.To) + (string.IsNullOrWhiteSpace(m.Cc) ? "" : "   Cc: " + Names(m.Cc));
        _web.Visibility = Visibility.Hidden;
        _plain.Visibility = Visibility.Collapsed;
        _attachBar.Visibility = Visibility.Collapsed;

        var sync = MailService.Sync(m.AccountId);
        if (sync == null) return;
        var load = sync.GetMessageAsync(m);
        // (from the server, and slow: say so after a moment, rather than a blank page)
        if (!load.IsCompleted && await Task.WhenAny(load, Task.Delay(500)) != load && _info == m)
        {
            _plain.Text = "Loading…";
            _plain.Visibility = Visibility.Visible;
        }
        var msg = await load;
        if (_info != m) return;   // (another one was chosen meanwhile)
        _plain.Visibility = Visibility.Collapsed;
        if (msg == null)
        {
            _plain.Text = "This message hasn’t been downloaded from the server yet.";
            _plain.Visibility = Visibility.Visible;
            return;
        }
        Message = msg;
        if (!m.Seen) _ = sync.SetFlagAsync(new[] { m }, MailKit.MessageFlags.Seen, true);
        if (_ready || _failed) Render(m, msg); else _pending = (m, msg);
    }

    void Render(MailMessageInfo m, MimeMessage msg)
    {
        ShowAttachments(m, msg);
        if (_failed)
        {
            _plain.Text = MailService.PlainText(msg);
            _plain.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            // a folder per message shown: its page and its pictures (cid:), the last one's thrown away
            string dir = Path.Combine(ViewDir, (++_gen).ToString());
            foreach (var old in Directory.GetDirectories(ViewDir)) try { Directory.Delete(old, true); } catch { }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), PageHtml(msg, dir), new UTF8Encoding(false));
            _web.DefaultBackgroundColor = string.IsNullOrWhiteSpace(msg.HtmlBody) && Theme.IsDark ? System.Drawing.Color.FromArgb(30, 30, 30) : System.Drawing.Color.White;
            _web.CoreWebView2.Navigate($"https://{Host}/{_gen}/index.html");
            _web.Visibility = Visibility.Visible;
        }
        catch
        {
            _plain.Text = MailService.PlainText(msg);
            _plain.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The message as a page: its HTML (with a little room around it, its own styles winning), or its text.</summary>
    static string PageHtml(MimeMessage msg, string dir)
    {
        bool dark = Theme.IsDark;
        const string font = "-apple-system, 'SF Pro Text', 'Segoe UI Variable Text', 'Segoe UI', sans-serif";
        string html = msg.HtmlBody;
        if (string.IsNullOrWhiteSpace(html))
        {
            string text = WebUtility.HtmlEncode(msg.TextBody ?? "");
            text = Regex.Replace(text, @"(https?://[^\s<>&""]+)", "<a href=\"$1\">$1</a>");
            string colors = dark ? "background:#1e1e1e;color:#e6e6e6;" : "background:#fff;color:#1d1d1f;";
            return $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html{{{colors}}}body{{font-family:{font};font-size:14px;line-height:1.45;margin:18px 22px;" +
                   $"white-space:pre-wrap;overflow-wrap:anywhere;}}a{{color:{(dark ? "#4da3ff" : "#0a66d8")}}}</style></head><body>{text}</body></html>";
        }
        // pictures in the message itself (cid:) saved beside the page
        int n = 0;
        foreach (var part in msg.BodyParts.OfType<MimePart>().Where(p => !string.IsNullOrEmpty(p.ContentId)))
        {
            try
            {
                string name = $"part{n++}{Ext(part.ContentType.MimeType)}";
                using (var fs = File.Create(Path.Combine(dir, name))) part.Content?.DecodeTo(fs);
                html = Regex.Replace(html, "cid:" + Regex.Escape(part.ContentId), name, RegexOptions.IgnoreCase);
            }
            catch { }
        }
        // (it's saved as UTF-8: any other charset it names would garble it)
        html = Regex.Replace(html, @"<meta[^>]*charset[^>]*>", "", RegexOptions.IgnoreCase);
        string head = $"<meta charset=\"utf-8\"><style>html{{background:#fff}}body{{font-family:{font};font-size:14px;margin:16px 20px;color:#1d1d1f;overflow-wrap:break-word}}img{{max-width:100%}}</style>";
        var headTag = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
        return headTag.Success ? html.Insert(headTag.Index + headTag.Length, head) : "<!doctype html><html><head>" + head + "</head><body>" + html + "</body></html>";
    }

    static string Ext(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png", "image/gif" => ".gif", "image/jpeg" or "image/jpg" => ".jpg", "image/webp" => ".webp", "image/svg+xml" => ".svg", "image/bmp" => ".bmp", _ => ".bin",
    };

    // ------------------------------------------------------------------ attachments

    void ShowAttachments(MailMessageInfo m, MimeMessage msg)
    {
        _attachments.Children.Clear();
        var list = msg.Attachments.ToList();
        _attachBar.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in list)
        {
            string name = AttachmentName(a);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var img = new Image { Width = 28, Height = 28, Source = MacIcons.GenericDocument, Margin = new Thickness(0, 0, 8, 0) };
            sp.Children.Add(img);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180 };
            var t1 = new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "LabelBrush");
            var t2 = new TextBlock { Text = SizeText(a), FontSize = 11 };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
            texts.Children.Add(t1);
            texts.Children.Add(t2);
            sp.Children.Add(texts);
            var chip = new Border { Child = sp, Padding = new Thickness(8, 6, 10, 6), Margin = new Thickness(4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0.5), Cursor = Cursors.Hand, ToolTip = name };
            chip.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            // its icon, once it's saved where it can be opened from
            string file = null;
            string Saved() => file ??= SaveAttachment(m, a, name);
            Dispatcher.BeginInvoke(() => { try { ShellIcons.Load(Saved(), 64, false, b => { if (b != null) img.Source = b; }); } catch { } });
            chip.MouseLeftButtonUp += (_, _) => { if (Saved() is string f) AppCatalog.OpenFile(f); };
            chip.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                Mb.Context(
                    Mb.Item("Open Attachment", () => { if (Saved() is string f) AppCatalog.OpenFile(f); }),
                    Mb.Item("Save to Downloads", () => SaveToDownloads(Saved())),
                    Mb.Item("Quick Look", () => { if (Saved() is string f) AppCatalog.OpenFile(f); })).IsOpen = true;
            };
            chip.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || Saved() is not string f) return;
                DragDrop.DoDragDrop(chip, new DataObject(DataFormats.FileDrop, new[] { f }), DragDropEffects.Copy);
            };
            _attachments.Children.Add(chip);
        }
    }

    static string AttachmentName(MimeEntity a)
    {
        string name = a is MessagePart mp ? (mp.Message?.Subject is { Length: > 0 } s ? s + ".eml" : "Message.eml") : (a as MimePart)?.FileName;
        if (string.IsNullOrWhiteSpace(name)) name = "Attachment";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name[..120] : name;
    }

    static string SizeText(MimeEntity a)
    {
        try
        {
            if (a is MimePart { Content: { } c })
            {
                long raw = c.Stream?.Length ?? 0;
                long size = c.Encoding == ContentEncoding.Base64 ? raw * 3 / 4 : raw;
                return size > 0 ? FileItem.FormatSize(size) : "";
            }
        }
        catch { }
        return "";
    }

    /// <summary>Saved where it can be opened from, marked as from the internet (Windows warns before running it).</summary>
    static string SaveAttachment(MailMessageInfo m, MimeEntity a, string name)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "MacShell", "Mail", $"{m.AccountId}-{m.Uid}");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, name);
            if (!File.Exists(file))
            {
                using (var fs = File.Create(file))
                {
                    if (a is MessagePart mp) mp.Message.WriteTo(fs);
                    else (a as MimePart)?.Content?.DecodeTo(fs);
                }
                MarkFromInternet(file);
            }
            return file;
        }
        catch { return null; }
    }

    static void MarkFromInternet(string file)
    {
        try { File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); } catch { }
    }

    static void SaveToDownloads(string file)
    {
        if (file == null) return;
        try
        {
            string downloads = Native.NativeMethods.GetKnownFolder(Native.NativeMethods.FOLDERID_Downloads);
            string name = FileOps.UniqueName(downloads, Path.GetFileNameWithoutExtension(file), Path.GetExtension(file));
            string dest = Path.Combine(downloads, name);
            File.Copy(file, dest);
            MarkFromInternet(dest);
            ShellHost.RevealInFinder(dest);
        }
        catch (Exception ex) { ShellHost.ShowAlert("The attachment couldn’t be saved.", ex.Message); }
    }

    // ------------------------------------------------------------------ header text

    static string Initials(string name, string address)
    {
        string s = !string.IsNullOrWhiteSpace(name) ? name : address ?? "?";
        var words = Regex.Matches(s, @"[\p{L}\p{N}]+").Select(w => w.Value).ToList();
        if (words.Count == 0) return "?";
        // a name: its first and last words' initials ("Jane Q. Doe" → JD); an address: its first letter
        bool isName = !string.IsNullOrWhiteSpace(name) && words.Count > 1;
        return (isName ? words[0][..1] + words[^1][..1] : words[0][..1]).ToUpperInvariant();
    }

    static string Names(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return "";
        return string.Join(", ", list.Split(',').Select(t => { int lt = t.IndexOf('<'); var s = (lt > 0 ? t[..lt] : t).Trim().Trim('"'); return s.Length > 0 ? s : t.Trim(); }));
    }

    /// <summary>"Today at 10:24 AM", "Yesterday at …", "October 3, 2026 at 4:12 PM".</summary>
    public static string LongDate(DateTimeOffset d)
    {
        var l = d.LocalDateTime;
        string time = l.ToString(Settings.Current.Clock24Hour ? "H:mm" : "h:mm tt");
        if (l.Date == DateTime.Today) return "Today at " + time;
        if (l.Date == DateTime.Today.AddDays(-1)) return "Yesterday at " + time;
        return l.ToString("MMMM d, yyyy") + " at " + time;
    }
}
