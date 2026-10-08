using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacShell.Controls;
using MacShell.Services;

namespace MacShell.Apps.Mail;

/// <summary>
/// Mail's accounts. With none yet: "Choose a Mail account provider…" (iCloud or Google), then the sign-in - email and
/// an app-specific password, with where to make one - checked with the server before the account is added. With
/// accounts: the list of them (add, remove) and the new-mail banner setting.
/// </summary>
public sealed class MailSetupWindow : MacWindow
{
    static MailSetupWindow _open;

    /// <summary>Mail's settings (the accounts) - or, with no account yet, adding the first.</summary>
    public static void Open()
    {
        MailService.Start();
        Show(MailService.Accounts.Count == 0 ? Page.Provider : Page.Accounts);
    }

    public static void AddAccount() { MailService.Start(); Show(Page.Provider); }

    enum Page { Provider, SignIn, Accounts }

    static void Show(Page page)
    {
        if (_open == null) { _open = new MailSetupWindow(); _open.Closed += (_, _) => _open = null; _open.Show(); }
        _open.Go(page);
        if (!ShellHost.Offscreen) _open.Activate();
    }

    readonly StackPanel _page = new() { Margin = new Thickness(28, 0, 28, 20) };
    MailProvider _provider = MailProvider.ICloud;
    readonly TextBox _name = Input(), _email = Input();
    readonly PasswordBox _password = new() { Height = 26, Padding = new Thickness(4, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13 };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x3B, 0x30)), Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
    Button _signIn;

    MailSetupWindow() : base(MailWindow.AppId)
    {
        Title = "Mail Accounts";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        UseVibrancy = false;
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        var head = new Grid { Height = 52 };
        head.Children.Add(new TrafficLights { Margin = new Thickness(20, 0, 0, 0), CanMinimize = false, CanZoom = false });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_page);
        Content = root;
        _name.Text = Environment.UserName;
        Loaded += (_, _) => CenterOnWorkArea(0.3);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    static TextBox Input() => new() { Style = (Style)Application.Current.Resources["MacTextField"], Height = 26, FontSize = 13 };

    static TextBlock Text(string s, double size = 13, FontWeight? weight = null, string brush = "LabelBrush")
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    static Button Btn(string text, bool primary, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[primary ? "MacDefaultButton" : "MacButton"], Height = 26, MinWidth = 86, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
        b.Click += (_, _) => click();
        return b;
    }

    static FrameworkElement Buttons(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    void Go(Page page)
    {
        _page.Children.Clear();
        switch (page)
        {
            case Page.Provider: BuildProvider(); break;
            case Page.SignIn: BuildSignIn(); break;
            default: BuildAccounts(); break;
        }
    }

    // ------------------------------------------------------------------ choose a provider

    void BuildProvider()
    {
        Title = "Choose a Mail account provider";
        _page.Children.Add(Text("Choose a Mail account provider…", 15, FontWeights.Bold));
        var list = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        list.Children.Add(ProviderRow(MailProvider.ICloud, ICloudMark()));
        list.Children.Add(ProviderRow(MailProvider.Gmail, GoogleMark()));
        _page.Children.Add(list);
        _page.Children.Add(Buttons(
            Btn(MailService.Accounts.Count == 0 ? "Quit" : "Cancel", false, Close),
            Btn("Continue", true, () => Go(Page.SignIn))));
    }

    FrameworkElement ProviderRow(MailProvider p, FrameworkElement mark)
    {
        var radio = new RadioButton { Style = (Style)Application.Current.Resources["MacRadio"], GroupName = "provider", IsChecked = _provider == p, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 18, 0) };
        radio.Checked += (_, _) => _provider = p;
        var dp = new DockPanel { Height = 64, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        dp.Children.Add(radio);
        dp.Children.Add(mark);
        dp.MouseLeftButtonUp += (_, _) => radio.IsChecked = true;
        dp.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { radio.IsChecked = true; Go(Page.SignIn); } };
        var b = new Border { Child = dp, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 2) };
        b.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        return b;
    }

    static FrameworkElement ICloudMark()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cloud = new SymbolIcon { Symbol = "icloud", Width = 34, Height = 34, StrokeWidth = 1.6, Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xF6)) };
        sp.Children.Add(cloud);
        var t = Text("iCloud", 24, FontWeights.Medium);
        t.Margin = new Thickness(10, 0, 0, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        return sp;
    }

    static FrameworkElement GoogleMark()
    {
        var tb = new TextBlock { FontSize = 26, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
        string[] colors = { "#4285F4", "#EA4335", "#FBBC05", "#4285F4", "#34A853", "#EA4335" };
        string word = "Google";
        for (int i = 0; i < word.Length; i++) tb.Inlines.Add(new System.Windows.Documents.Run(word[i].ToString()) { Foreground = new SolidColorBrush(Theme.C(colors[i])) });
        return tb;
    }

    // ------------------------------------------------------------------ sign in

    void BuildSignIn()
    {
        bool icloud = _provider == MailProvider.ICloud;
        Title = icloud ? "iCloud" : "Google";
        _page.Children.Add(Text(icloud ? "Sign in to iCloud Mail" : "Sign in to Gmail", 15, FontWeights.Bold));
        var help = Text(icloud
            ? "Mail signs in to iCloud with an app-specific password, not your Apple Account password. Make one at account.apple.com → Sign-In and Security → App-Specific Passwords, then paste it below."
            : "Mail signs in to Gmail with an app password. Turn on 2-Step Verification for your Google Account, make an app password at myaccount.google.com/apppasswords, then paste it below.", 12, brush: "SecondaryLabelBrush");
        help.Margin = new Thickness(0, 8, 0, 6);
        _page.Children.Add(help);
        var link = new Button { Content = icloud ? "Open account.apple.com…" : "Open Google app passwords…", Style = (Style)Application.Current.Resources["MacButton"], HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 0, Padding = new Thickness(10, 0, 10, 0), Height = 24 };
        link.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(icloud ? "https://account.apple.com/account/manage" : "https://myaccount.google.com/apppasswords") { UseShellExecute = true }); } catch { }
        };
        _page.Children.Add(link);

        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(grid, 0, "Name:", _name);
        AddRow(grid, 1, icloud ? "iCloud Email Address:" : "Gmail Address:", _email);
        AddRow(grid, 2, "App-Specific Password:", _password);
        _page.Children.Add(grid);
        _error.Text = "";
        _page.Children.Add(_error);
        _signIn = Btn("Sign In", true, SignIn);
        _page.Children.Add(Buttons(Btn("Back", false, () => Go(Page.Provider)), Btn("Cancel", false, Close), _signIn));
        Dispatcher.BeginInvoke(() => (_email.Text.Length == 0 ? (Control)_email : _password).Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    static void AddRow(Grid g, int row, string label, Control field)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = Text(label, 13);
        l.HorizontalAlignment = HorizontalAlignment.Right;
        l.VerticalAlignment = VerticalAlignment.Center;
        l.Margin = new Thickness(0, 5, 10, 5);
        Grid.SetRow(l, row);
        g.Children.Add(l);
        field.Margin = new Thickness(0, 5, 0, 5);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
    }

    async void SignIn()
    {
        string email = _email.Text.Trim(), password = _password.Password.Replace(" ", "").Trim();
        if (!email.Contains('@') || email.LastIndexOf('.') < email.IndexOf('@')) { _error.Text = "Enter your full email address."; _email.Focus(); return; }
        if (password.Length == 0) { _error.Text = "Enter an app-specific password."; _password.Focus(); return; }
        if (MailService.Accounts.Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase))) { _error.Text = "That account is already in Mail."; return; }
        var account = new MailAccount { Id = "a" + Guid.NewGuid().ToString("N")[..10], Provider = _provider, Email = email, Name = _name.Text.Trim() };
        account.Password = password;
        _signIn.IsEnabled = false;
        _signIn.Content = "Signing In…";
        _error.Text = "";
        string problem = await MailSync.CheckAsync(account, CancellationToken.None);
        _signIn.IsEnabled = true;
        _signIn.Content = "Sign In";
        if (problem != null) { _error.Text = problem; return; }
        _password.Clear();
        MailService.AddAccount(account);
        Close();
        MailWindow.OpenApp();
    }

    // ------------------------------------------------------------------ the accounts

    void BuildAccounts()
    {
        Title = "Mail Accounts";
        _page.Children.Add(Text("Accounts", 15, FontWeights.Bold));
        var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var a in MailService.Accounts)
        {
            var acc = a;
            var sync = MailService.Sync(a.Id);
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 2), MinHeight = 46 };
            var remove = Btn("Remove…", false, () =>
            {
                if (ShellHost.Alert($"Remove “{acc.Email}” from Mail?", "Its mail stays on the server; Mail forgets it and its password on this computer.", "Cancel", "Remove") != "Remove") return;
                MailService.RemoveAccount(acc);
                Go(MailService.Accounts.Count == 0 ? Page.Provider : Page.Accounts);
            });
            remove.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(remove, Dock.Right);
            dp.Children.Add(remove);
            var mark = a.Provider == MailProvider.Gmail ? (FrameworkElement)new TextBlock { Text = "G", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Theme.C("#4285F4")), Width = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                                       : new SymbolIcon { Symbol = "icloud", Width = 28, Height = 28, StrokeWidth = 1.7, Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0x8D, 0xF6)), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            dp.Children.Add(mark);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            texts.Children.Add(Text(a.Email, 13, FontWeights.SemiBold));
            texts.Children.Add(Text(sync?.Error ?? (a.Title + (string.IsNullOrWhiteSpace(a.Name) ? "" : " · " + a.Name)), 11.5, brush: sync?.Error != null ? "LabelBrush" : "SecondaryLabelBrush"));
            dp.Children.Add(texts);
            var row = new Border { Child = dp, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 6) };
            row.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
            box.Children.Add(row);
        }
        _page.Children.Add(box);
        var notify = new CheckBox { Style = (Style)Application.Current.Resources["MacCheckBox"], Content = "Show a banner and play a sound when new mail arrives", IsChecked = Settings.Current.MailNotify, Margin = new Thickness(0, 16, 0, 0) };
        notify.Click += (_, _) => { Settings.Current.MailNotify = notify.IsChecked == true; Settings.Save(notify: false); };
        _page.Children.Add(notify);
        _page.Children.Add(Buttons(Btn("Add Account…", false, () => Go(Page.Provider)), Btn("Done", true, Close)));
    }
}
