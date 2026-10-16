using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Notes;

/// <summary>Notes' Import window (from Apple Notes on a Mac - with the steps to zip its notes - or from files) and Export.</summary>
public static class NotesTransfer
{
    public static void ShowImport(Window owner, bool appleFirst = false) => new ImportWindow(owner).Show();

    /// <summary>Notes saved as an .enex file that Apple Notes on a Mac imports (File › Import to Notes…).</summary>
    public static void Export(Window owner, List<NoteInfo> notes, string name)
    {
        if (notes == null || notes.Count == 0) return;
        string safe = string.Concat((name ?? "Notes").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safe.Length == 0) safe = "Notes";
        if (safe.Length > 60) safe = safe[..60].Trim();
        var r = Finder.FilePanel.ShowModal(owner, new Finder.FilePanelRequest
        {
            Save = true, FileName = safe + ".enex", Title = "Export to Apple Notes", Prompt = "Export", AppKey = "macshell:notes-export",
            Folder = Settings.Current.FilePanelPlaces.ContainsKey("macshell:notes-export") ? null : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Filters = { "Apple Notes export (*.enex)" },
        });
        if (r?.Paths.Count is not > 0) return;
        string path = r.Paths[0];
        if (!path.EndsWith(".enex", StringComparison.OrdinalIgnoreCase)) path += ".enex";
        try
        {
            NotesExchange.WriteEnex(notes, path);
            string what = notes.Count == 1 ? "1 note" : $"{notes.Count} notes";
            if (ShellHost.Alert($"Exported {what} for Apple Notes.",
                    $"On your Mac, open Notes, choose File › Import to Notes…, and choose “{Path.GetFileName(path)}”. The notes come in with their pictures, lists and checklists, in a folder called Imported Notes.",
                    "Show in Finder", "OK") == "Show in Finder")
                ShellHost.RevealInFinder(path);
        }
        catch (Exception ex) { ShellHost.Alert("The notes couldn’t be exported.", ex.Message, "OK"); }
    }

    sealed class ImportWindow : PanelWindow
    {
        readonly TextBlock _status;
        readonly Button _zip, _files, _done;
        bool _busy;

        public ImportWindow(Window owner) : base(24)
        {
            Title = "Import to Notes";
            Topmost = owner?.Topmost ?? false;
            if (owner != null && owner.IsVisible) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; } else CenterOnScreen(0.2);
            var sp = new StackPanel { Width = 470, Margin = new Thickness(22, 14, 22, 16) };
            var lights = new TrafficLights { CanMinimize = false, CanZoom = false, Margin = new Thickness(-8, 0, 0, 10) };
            lights.CloseRequested += () => { if (!_busy) Close(); };
            sp.Children.Add(lights);

            var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(new Image { Source = MacIcons.Notes, Width = 52, Height = 52, Margin = new Thickness(0, 0, 14, 0) });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(Text("Import to Notes", 15, FontWeights.Bold));
            titles.Children.Add(Text("Bring in notes from Apple Notes on a Mac, or from files.", 12, brush: "SecondaryLabelBrush"));
            Grid.SetColumn(titles, 1);
            head.Children.Add(titles);
            sp.Children.Add(head);

            sp.Children.Add(Section("From Apple Notes on a Mac"));
            var steps = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
            steps.Children.Add(Step(1, "On the Mac, quit Notes (Notes › Quit Notes), so everything is saved."));
            steps.Children.Add(Step(2, "In Finder, choose Go › Go to Folder… (⇧⌘G), type this and press Return:"));
            var path = new TextBox
            {
                Text = "~/Library/Group Containers/group.com.apple.notes", IsReadOnly = true, FontFamily = Theme.MonoFont, FontSize = 12,
                Margin = new Thickness(26, 2, 0, 6), Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0.5),
            };
            path.SetResourceReference(Control.BackgroundProperty, "TextFieldBrush");
            path.SetResourceReference(Control.ForegroundProperty, "LabelBrush");
            path.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
            steps.Children.Add(path);
            steps.Children.Add(Step(3, "Press ⌘↑ to go up one folder. Control-click “group.com.apple.notes” and choose Compress “group.com.apple.notes”. If the Mac asks whether Finder may use data from other apps, click Allow."));
            steps.Children.Add(Step(4, "Bring the new group.com.apple.notes.zip to this PC: AirDrop it to your iPhone and send it on, or use a USB drive, iCloud Drive or email."));
            steps.Children.Add(Step(5, "Click Choose Zip… below and pick it."));
            var note = Text("Your folders come along, with the notes’ styles, lists, checklists and pictures. Locked notes are encrypted on the Mac, so they can’t be brought over.", 11, brush: "SecondaryLabelBrush");
            note.Margin = new Thickness(2, 6, 0, 0);
            steps.Children.Add(note);
            sp.Children.Add(steps);
            _zip = new Button { Content = "Choose Zip…", Style = (Style)Application.Current.Resources["MacDefaultButton"], HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Height = 24, MinWidth = 110 };
            _zip.Click += (_, _) => ChooseApple();
            sp.Children.Add(_zip);

            sp.Children.Add(Line());
            sp.Children.Add(Section("From files"));
            sp.Children.Add(Text("Apple Notes or Evernote exports (.enex), web pages (.html), Markdown (.md), RTF documents and text files. Each file becomes a note (an .enex brings all of its notes).", 12, brush: "SecondaryLabelBrush"));
            _files = new Button { Content = "Choose Files…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Height = 24, MinWidth = 110 };
            _files.Click += (_, _) => ChooseFiles();
            sp.Children.Add(_files);

            sp.Children.Add(Line());
            var foot = new DockPanel();
            _done = new Button { Content = "Done", Style = (Style)Application.Current.Resources["MacButton"], MinWidth = 80, Height = 24, IsCancel = true };
            _done.Click += (_, _) => { if (!_busy) Close(); };
            DockPanel.SetDock(_done, Dock.Right);
            foot.Children.Add(_done);
            _status = Text("", 12, brush: "SecondaryLabelBrush");
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(0, 0, 10, 0);
            foot.Children.Add(_status);
            sp.Children.Add(foot);

            Card.Child = sp;
            KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
            Loaded += (_, _) => Activate();
        }

        static TextBlock Section(string s)
        {
            var t = Text(s, 13, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 0, 0, 6);
            return t;
        }

        static FrameworkElement Step(int n, string s)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var num = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
            num.Background = new SolidColorBrush(NoteFlow.Yellow);
            num.Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(num);
            var t = Text(s, 12);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        static Border Line()
        {
            var b = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 12) };
            b.SetResourceReference(Border.BackgroundProperty, "SeparatorBrush");
            return b;
        }

        void Busy(bool on, string status)
        {
            _busy = on;
            _zip.IsEnabled = _files.IsEnabled = _done.IsEnabled = !on;
            _status.Text = status;
        }

        void ChooseApple()
        {
            var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest
            {
                Title = "Choose the zip of group.com.apple.notes (or NoteStore.sqlite)", Prompt = "Import", AppKey = "macshell:notes-import",
                Folder = Settings.Current.FilePanelPlaces.ContainsKey("macshell:notes-import") ? null : Finder.FinderLocation.Downloads,
                Filters = { "Apple Notes from a Mac (*.zip;*.sqlite)", "All files (*.*)" },
            });
            if (r?.Paths.Count is not > 0) return;
            string source = r.Paths[0];
            Busy(true, "Reading the notes…");
            var dispatcher = Dispatcher;
            Task.Run(() => AppleNotesImport.Import(source,
                (done, total) => dispatcher.BeginInvoke(() => _status.Text = $"Importing… {done:N0} of {total:N0}"),
                act => dispatcher.Invoke(act)))
                .ContinueWith(t => dispatcher.BeginInvoke(() =>
                {
                    Busy(false, "");
                    var res = t.IsCompletedSuccessfully ? t.Result : new AppleNotesImport.Result { Problems = { t.Exception?.GetBaseException().Message ?? "Something went wrong." } };
                    NotesLibrary.SaveNow();
                    NotesWindow.Instance?.Reload();
                    if (res.Notes == 0 && res.Problems.Count > 0) { _status.Text = res.Problems[0]; return; }
                    var parts = new List<string> { $"Imported {res.Notes:N0} {(res.Notes == 1 ? "note" : "notes")}" };
                    if (res.Folders > 0) parts.Add($"{res.Folders} new {(res.Folders == 1 ? "folder" : "folders")}");
                    if (res.Pictures > 0) parts.Add($"{res.Pictures:N0} {(res.Pictures == 1 ? "picture" : "pictures")}");
                    string extra = (res.Locked > 0 ? $" {res.Locked} locked {(res.Locked == 1 ? "note was" : "notes were")} skipped." : "") +
                                   (res.Failed > 0 ? $" {res.Failed} couldn’t be read." : "");
                    _status.Text = string.Join(", ", parts) + "." + extra;
                }));
        }

        void ChooseFiles()
        {
            var r = Finder.FilePanel.ShowModal(this, new Finder.FilePanelRequest
            {
                Title = "Choose notes to import", Prompt = "Import", AppKey = "macshell:notes-import-files", Multiple = true,
                Filters = { "Notes (*.enex;*.html;*.htm;*.md;*.markdown;*.rtf;*.txt)", "All files (*.*)" },
            });
            if (r?.Paths.Count is not > 0) return;
            Busy(true, "Importing…");
            string folder = NotesWindow.Instance?.ImportFolder() ?? NotesLibrary.DefaultFolder;
            var res = NotesExchange.ImportFiles(r.Paths, folder);
            NotesLibrary.SaveNow();
            Busy(false, "");
            NotesWindow.Instance?.Reload();
            _status.Text = $"Imported {res.Notes:N0} {(res.Notes == 1 ? "note" : "notes")}." + (res.Problems.Count > 0 ? " " + res.Problems[0] : "");
        }
    }
}
