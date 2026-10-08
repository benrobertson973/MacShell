using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using MailKit;
using MimeKit;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>
/// A new message, reply or forward, like Mail's: To, Cc and Subject (and From, with more than one account), the text,
/// attachments (the paperclip, or files dropped on the window). Send (⇧⌘D) sends it over SMTP; closing it unsent asks
/// whether to save it as a draft.
/// </summary>
public sealed class MailComposeWindow : MacWindow
{
    readonly TextBox _to = Field(), _cc = Field(), _subject = Field();
    readonly TextBox _body = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        Padding = new Thickness(18, 14, 18, 14), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    readonly WrapPanel _attachList = new() { Margin = new Thickness(14, 4, 14, 8) };
    readonly Button _send = new();
    readonly Button _from = new();
    readonly List<(string name, byte[] data)> _attachments = new();
    MailAccount _account;
    MailMessageInfo _answering, _draftOf;
    string _inReplyTo;
    List<string> _references = new();
    bool _sent, _closingAsked;
    string _initial = "";

    // ------------------------------------------------------------------ opening

    public static void New(MailAccount account, string to = null, string subject = null, string body = null)
    {
        MailService.Start();
        account ??= MailService.Accounts.FirstOrDefault();
        if (account == null) { MailSetupWindow.Open(); return; }
        var w = new MailComposeWindow(account);
        w._to.Text = to ?? "";
        w._subject.Text = subject ?? "";
        w._body.Text = body ?? "";
        w.Present(string.IsNullOrEmpty(to) ? w._to : w._body);
    }

    /// <summary>A mailto: link (from a message, or another app).</summary>
    public static void FromMailto(string uri)
    {
        string rest = uri[7..];
        string to = rest, subject = null, body = null;
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            to = rest[..q];
            foreach (var kv in rest[(q + 1)..].Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq < 0) continue;
                string k = kv[..eq].ToLowerInvariant(), v = Uri.UnescapeDataString(kv[(eq + 1)..].Replace('+', ' '));
                if (k == "subject") subject = v; else if (k == "body") body = v;
            }
        }
        New(null, Uri.UnescapeDataString(to), subject, body);
    }

    public static void Reply(MailMessageInfo m, MimeMessage msg, bool all)
    {
        var account = MailService.AccountOf(m);
        if (account == null) return;
        var w = new MailComposeWindow(account) { _answering = m };
        string me = account.Email;
        var to = (msg.ReplyTo.Count > 0 ? msg.ReplyTo : msg.From).Mailboxes.ToList();
        // (a reply to something one sent oneself goes to its recipients)
        if (to.All(b => Same(b, me)) && msg.To.Mailboxes.Any()) to = msg.To.Mailboxes.ToList();
        var cc = new List<MailboxAddress>();
        if (all)
        {
            foreach (var b in msg.To.Mailboxes) if (!Same(b, me) && !to.Any(x => Same(x, b.Address))) to.Add(b);
            foreach (var b in msg.Cc.Mailboxes) if (!Same(b, me) && !to.Any(x => Same(x, b.Address))) cc.Add(b);
        }
        w._to.Text = Format(to);
        w._cc.Text = Format(cc);
        w._subject.Text = Prefixed("Re:", msg.Subject);
        w._inReplyTo = msg.MessageId;
        w._references = msg.References.ToList();
        if (!string.IsNullOrEmpty(msg.MessageId)) w._references.Add(msg.MessageId);
        w._body.Text = "\n\n" + $"On {MailViewer.LongDate(msg.Date)}, {Sender(msg)} wrote:\n\n" + MailService.Quote(MailService.PlainText(msg));
        w.Present(w._body, caretAtStart: true);
    }

    public static void Forward(MailMessageInfo m, MimeMessage msg)
    {
        var account = MailService.AccountOf(m);
        if (account == null) return;
        var w = new MailComposeWindow(account);
        w._subject.Text = Prefixed("Fwd:", msg.Subject);
        w._body.Text = "\n\n\nBegin forwarded message:\n\n" +
                       $"From: {Sender(msg)}\nSubject: {msg.Subject}\nDate: {MailViewer.LongDate(msg.Date)}\nTo: {MailSync.Addresses(msg.To)}\n" +
                       (msg.Cc.Count > 0 ? $"Cc: {MailSync.Addresses(msg.Cc)}\n" : "") + "\n" + MailService.PlainText(msg);
        foreach (var a in msg.Attachments) w.AddAttachment(a);
        w.Present(w._to);
    }

    public static void EditDraft(MailMessageInfo m, MimeMessage msg)
    {
        var account = MailService.AccountOf(m);
        if (account == null) return;
        var w = new MailComposeWindow(account) { _draftOf = m };
        w._to.Text = MailSync.Addresses(msg.To) ?? "";
        w._cc.Text = MailSync.Addresses(msg.Cc) ?? "";
        w._subject.Text = msg.Subject ?? "";
        w._body.Text = MailService.PlainText(msg);
        w._inReplyTo = msg.InReplyTo;
        w._references = msg.References.ToList();
        foreach (var a in msg.Attachments) w.AddAttachment(a);
        w.Present(w._body);
    }

    void Present(UIElement focus, bool caretAtStart = false)
    {
        _initial = Snapshot();
        Show();
        if (!ShellHost.Offscreen) Activate();
        Dispatcher.BeginInvoke(() =>
        {
            focus.Focus();
            if (focus == _body) _body.CaretIndex = caretAtStart ? 0 : _body.Text.Length;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ------------------------------------------------------------------ the window

    static TextBox Field()
    {
        var t = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6) };
        t.SetResourceReference(Control.ForegroundProperty, "LabelBrush");
        t.SetResourceReference(TextBoxBase.CaretBrushProperty, "LabelBrush");
        return t;
    }

    MailComposeWindow(MailAccount account) : base(MailWindow.AppId)
    {
        _account = account;
        Title = "New Message";
        Width = 680;
        Height = 560;
        MinWidth = 440;
        MinHeight = 300;
        // (the size the last one had)
        if (Settings.Current.MailComposeSize is { Length: 2 } size && size[0] >= MinWidth && size[1] >= MinHeight)
        {
            Width = Math.Min(size[0], SystemParameters.WorkArea.Width);
            Height = Math.Min(size[1], SystemParameters.WorkArea.Height);
        }
        SizeChanged += (_, _) =>
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;
            Settings.Current.MailComposeSize = new[] { Math.Round(ActualWidth), Math.Round(ActualHeight) };
            Settings.Save(notify: false);
        };
        var owner = MailWindow.Front;
        if (owner != null && owner.WindowState == WindowState.Normal) { Left = owner.Left + 80; Top = owner.Top + 60; }
        else CenterOnWorkArea();
        ApplyOffscreen();
        UseVibrancy = false;
        AllowDrop = true;
        _body.FontFamily = Theme.Font;
        _body.SetResourceReference(Control.ForegroundProperty, "LabelBrush");
        _body.SetResourceReference(TextBoxBase.CaretBrushProperty, "LabelBrush");

        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "ContentBackgroundBrush");

        // toolbar: traffic lights, Send … paperclip
        var bar = new DockPanel { Height = 52, LastChildFill = false };
        bar.SetResourceReference(Panel.BackgroundProperty, "ToolbarBackgroundBrush");
        bar.Children.Add(new TrafficLights { Margin = new Thickness(20, 0, 16, 0) });
        _send.Style = (Style)Application.Current.Resources["ToolbarButton"];
        var plane = new SymbolIcon { Symbol = "paperplane", Width = 18, Height = 18, StrokeWidth = 1.8 };
        plane.SetResourceReference(SymbolIcon.ForegroundProperty, "AccentBrush");
        _send.Content = plane;
        _send.ToolTip = "Send (⇧⌘D)";
        _send.Click += (_, _) => Send();
        WindowChrome.SetIsHitTestVisibleInChrome(_send, true);
        bar.Children.Add(_send);
        var clip = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Content = new SymbolIcon { Symbol = "paperclip", Width = 17, Height = 17, StrokeWidth = 1.8 }, ToolTip = "Attach Files", Margin = new Thickness(0, 0, 10, 0) };
        clip.Click += (_, _) => PickFiles();
        WindowChrome.SetIsHitTestVisibleInChrome(clip, true);
        DockPanel.SetDock(clip, Dock.Right);
        bar.Children.Add(clip);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var barLine = new Border { Height = 1 };
        barLine.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
        DockPanel.SetDock(barLine, Dock.Top);
        root.Children.Add(barLine);

        // the address fields
        var fields = new StackPanel();
        fields.Children.Add(FieldRow("To:", _to));
        fields.Children.Add(FieldRow("Cc:", _cc));
        fields.Children.Add(FieldRow("Subject:", _subject));
        if (MailService.Accounts.Count > 1)
        {
            _from.Style = (Style)Application.Current.Resources["MacButton"];
            _from.HorizontalAlignment = HorizontalAlignment.Left;
            _from.MinWidth = 0;
            _from.Padding = new Thickness(9, 0, 7, 0);
            _from.Click += (_, _) => ChooseFrom();
            fields.Children.Add(FieldRow("From:", _from));
        }
        ShowFrom();
        DockPanel.SetDock(fields, Dock.Top);
        root.Children.Add(fields);

        DockPanel.SetDock(_attachList, Dock.Bottom);
        root.Children.Add(_attachList);
        root.Children.Add(_body);
        Content = root;

        _subject.TextChanged += (_, _) => Title = string.IsNullOrWhiteSpace(_subject.Text) ? "New Message" : _subject.Text;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) AddFile(f); };
        _body.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _body.PreviewDrop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) AddFile(f); e.Handled = true; } };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D) { Send(); e.Handled = true; }
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A) { PickFiles(); e.Handled = true; }
        };
        Closing += OnClosing;
    }

    static FrameworkElement FieldRow(string label, FrameworkElement field)
    {
        var dp = new DockPanel { Margin = new Thickness(18, 2, 18, 2), MinHeight = 32 };
        var l = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Width = 62 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryLabelBrush");
        dp.Children.Add(l);
        field.VerticalAlignment = VerticalAlignment.Center;
        dp.Children.Add(field);
        var b = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = dp };
        b.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        return b;
    }

    void ShowFrom()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(_account.Name) ? _account.Email : $"{_account.Name} – {_account.Email}", VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new SymbolIcon { Symbol = "chevron.updown", Width = 10, Height = 10, StrokeWidth = 2.4, Margin = new Thickness(8, 0, 0, 0) });
        _from.Content = sp;
    }

    void ChooseFrom()
    {
        var cm = new ContextMenu { PlacementTarget = _from, Placement = PlacementMode.Bottom };
        foreach (var a in MailService.Accounts)
        {
            var acc = a;
            Mb.Add(cm.Items, Mb.Item(string.IsNullOrWhiteSpace(a.Name) ? a.Email : $"{a.Name} – {a.Email}", () => { _account = acc; ShowFrom(); }, isChecked: a.Id == _account.Id));
        }
        cm.IsOpen = true;
    }

    // ------------------------------------------------------------------ attachments

    void PickFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Attach Files", Multiselect = true };
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) AddFile(f);
    }

    void AddFile(string path)
    {
        try
        {
            if (Directory.Exists(path)) { ShellHost.Alert($"“{Path.GetFileName(path)}” is a folder.", "Only files can be attached.", "OK"); return; }
            var info = new FileInfo(path);
            long total = _attachments.Sum(a => (long)a.data.Length) + info.Length;
            if (total > 20L * 1024 * 1024 &&
                ShellHost.Alert("This message is large.", "iCloud and Gmail don’t accept messages over 20–25 MB. Attach it anyway?", "Cancel", "Attach") != "Attach") return;
            _attachments.Add((info.Name, File.ReadAllBytes(path)));
            RefreshAttachments();
        }
        catch (Exception ex) { ShellHost.ShowAlert("The file couldn’t be attached.", ex.Message); }
    }

    void AddAttachment(MimeEntity a)
    {
        try
        {
            using var ms = new MemoryStream();
            string name;
            if (a is MessagePart mp) { mp.Message.WriteTo(ms); name = (mp.Message.Subject ?? "Message") + ".eml"; }
            else if (a is MimePart p) { p.Content?.DecodeTo(ms); name = p.FileName ?? "Attachment"; }
            else return;
            _attachments.Add((name, ms.ToArray()));
            RefreshAttachments();
        }
        catch { }
    }

    void RefreshAttachments()
    {
        _attachList.Children.Clear();
        for (int i = 0; i < _attachments.Count; i++)
        {
            int idx = i;
            var (name, data) = _attachments[i];
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new SymbolIcon { Symbol = "paperclip", Width = 13, Height = 13, StrokeWidth = 2, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = $"{name}  ({Finder.FileItem.FormatSize(data.Length)})", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Style = (Style)Application.Current.Resources["ToolbarButton"], Height = 18, MinWidth = 18, Width = 18, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), Content = new SymbolIcon { Symbol = "xmark", Width = 9, Height = 9, StrokeWidth = 2.6 }, ToolTip = "Remove" };
            x.Click += (_, _) => { _attachments.RemoveAt(idx); RefreshAttachments(); };
            sp.Children.Add(x);
            var chip = new Border { Child = sp, Padding = new Thickness(9, 4, 4, 4), Margin = new Thickness(4), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(0.5) };
            chip.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
            _attachList.Children.Add(chip);
        }
    }

    // ------------------------------------------------------------------ sending

    static bool Same(MailboxAddress b, string address) => string.Equals(b?.Address, address, StringComparison.OrdinalIgnoreCase);
    static string Format(IEnumerable<MailboxAddress> list) => string.Join(", ", list.Select(b => string.IsNullOrWhiteSpace(b.Name) ? b.Address : $"{b.Name} <{b.Address}>"));
    static string Sender(MimeMessage msg) => msg.From.Mailboxes.FirstOrDefault() is { } f ? (string.IsNullOrWhiteSpace(f.Name) ? f.Address : $"{f.Name} <{f.Address}>") : "";

    static string Prefixed(string prefix, string subject)
    {
        subject ??= "";
        return subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? subject : $"{prefix} {subject}".Trim();
    }

    /// <summary>The addresses typed in a field; false (and which one) when one isn't an address.</summary>
    static bool Parse(string text, out List<MailboxAddress> list, out string bad)
    {
        list = new();
        bad = null;
        foreach (var part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (MailboxAddress.TryParse(part, out var b) && b.Address.Contains('@') && b.Address.LastIndexOf('.') > b.Address.IndexOf('@')) list.Add(b);
            else { bad = part; return false; }
        }
        return true;
    }

    MimeMessage Build(List<MailboxAddress> to, List<MailboxAddress> cc)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(_account.Name ?? "", _account.Email));
        m.To.AddRange(to);
        m.Cc.AddRange(cc);
        m.Subject = _subject.Text.Trim();
        m.Date = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(_inReplyTo)) m.InReplyTo = _inReplyTo;
        foreach (var r in _references) m.References.Add(r);
        var body = new BodyBuilder { TextBody = _body.Text, HtmlBody = Html(_body.Text) };
        foreach (var (name, data) in _attachments) body.Attachments.Add(name, data);
        m.Body = body.ToMessageBody();
        return m;
    }

    /// <summary>The text as HTML too (quoted lines as Mail's blue-barred quote).</summary>
    static string Html(string text)
    {
        var sb = new System.Text.StringBuilder("<html><body><div style=\"font-family: -apple-system, Helvetica, Arial, sans-serif; font-size: 14px;\">");
        bool inQuote = false;
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            bool quoted = raw.StartsWith(">");
            if (quoted && !inQuote) { sb.Append("<blockquote type=\"cite\" style=\"margin:0 0 0 5px;border-left:2px solid #2e7cf6;padding-left:10px;color:#2e7cf6\">"); inQuote = true; }
            if (!quoted && inQuote) { sb.Append("</blockquote>"); inQuote = false; }
            string line = quoted ? raw.TrimStart('>').TrimStart(' ') : raw;
            sb.Append(WebUtility.HtmlEncode(line)).Append("<br>");
        }
        if (inQuote) sb.Append("</blockquote>");
        return sb.Append("</div></body></html>").ToString();
    }

    async void Send()
    {
        if (!_send.IsEnabled) return;
        if (!Parse(_to.Text, out var to, out var bad) || !Parse(_cc.Text, out var cc, out bad))
        {
            ShellHost.Alert($"“{bad}” isn’t a valid email address.", "Check the address and try again.", "OK");
            return;
        }
        if (to.Count + cc.Count == 0) { ShellHost.Alert("This message has no recipients.", "Add an address in To or Cc.", "OK"); return; }
        if (string.IsNullOrWhiteSpace(_subject.Text) &&
            ShellHost.Alert("Send this message without a subject?", "Your message has an empty subject.", "Don’t Send", "Send") != "Send") return;
        var sync = MailService.Sync(_account.Id);
        if (sync == null) return;
        var msg = Build(to, cc);
        _send.IsEnabled = false;
        Title = "Sending…";
        try { await sync.SendAsync(msg); }
        catch (Exception ex)
        {
            _send.IsEnabled = true;
            Title = string.IsNullOrWhiteSpace(_subject.Text) ? "New Message" : _subject.Text;
            ShellHost.Alert("The message couldn’t be sent.", MailSync.Describe(ex), "OK");
            return;
        }
        if (_answering != null) _ = MailService.Sync(_answering.AccountId)?.SetFlagAsync(new[] { _answering }, MessageFlags.Answered, true);
        if (_draftOf != null) _ = MailService.Sync(_draftOf.AccountId)?.EraseAsync(new[] { _draftOf });
        _sent = true;
        Close();
    }

    string Snapshot() => $"{_to.Text}\u0001{_cc.Text}\u0001{_subject.Text}\u0001{_body.Text}\u0001{_attachments.Count}";

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_sent || _closingAsked || Snapshot() == _initial) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () =>
        {
            string answer = ShellHost.Alert("Do you want to save this message as a draft?", "You can finish it later from Drafts.", "Don’t Save", "Cancel", "Save");
            if (answer == "Cancel") return;
            if (answer == "Save")
            {
                Parse(_to.Text, out var to, out _);
                Parse(_cc.Text, out var cc, out _);
                var sync = MailService.Sync(_account.Id);
                if (sync != null && await sync.AppendAsync("drafts", Build(to, cc), MessageFlags.Draft | MessageFlags.Seen) && _draftOf != null)
                    _ = MailService.Sync(_draftOf.AccountId)?.EraseAsync(new[] { _draftOf });
            }
            _closingAsked = true;
            Close();
        });
    }
}
