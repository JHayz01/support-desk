using Microsoft.Win32;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
namespace SupportDesk.Desktop;

public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    readonly Forms.NotifyIcon tray;
    readonly Hotkeys hotkeys;
    readonly TextExpander expander;
    volatile HashSet<string> commandNames = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime nextSync = DateTime.UtcNow, nextUpdateCheck = DateTime.UtcNow.AddSeconds(3);
    AppRelease? availableUpdate;
    bool exiting;
    bool detailShown = true;
    GridLength detailWidth = new(1, GridUnitType.Star);
    AnswerPicker? answerPicker;
    PasteToast? pasteToast;
    int unlockFailures;
    DateTime retryAfter;
    public MainWindow(MainViewModel model)
    {
        ThemePalette.Apply(model.Local.Settings.Theme);
        InitializeComponent();
        vm = model;
        DataContext = vm;
        CategoryBox.Configure(vm.Categories, () => vm.Category, value => vm.Category = value);
        hotkeys = new(this);
        var s = vm.Local.Settings;

        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        var area = SystemParameters.WorkArea;
        Left = double.IsNaN(s.Left) ? Math.Max(area.Left, area.Right - Width - 12) : Math.Clamp(s.Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = double.IsNaN(s.Top) ? area.Top + Math.Max(0, (area.Height - Height) / 2) : Math.Clamp(s.Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        if (s.WindowLayoutVersion < 1) { Width = Math.Min(1100, area.Width); Height = Math.Min(800, area.Height); Left = area.Left + (area.Width - Width) / 2; Top = area.Top + (area.Height - Height) / 2; s.WindowLayoutVersion = 1; }
        SetPinned(s.AlwaysOnTop);
        if (s.Maximized) WindowState = WindowState.Maximized;
        ApplyScale();
        tray = new()
        {
            Icon = new System.Drawing.Icon(typeof(MainWindow).Assembly.GetManifestResourceStream("SupportDesk.Icon")!),
            Text = "Cristalix Support",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("Список команд", null, (_, _) => Dispatcher.Invoke(ShowAllBinds));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Quit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Detail)) UpdateDetailLayout(); if (e.PropertyName == nameof(vm.Category)) CategoryBox.RefreshSelection(); if (e.PropertyName == nameof(vm.Unlocked) && !vm.Unlocked) { pasteToast?.Close(); foreach (Window owned in OwnedWindows.Cast<Window>().ToArray()) owned.Close(); } };
        vm.BindingsChanged += Register;
        Register();
        expander = new(Dispatcher, () => vm.Unlocked && answerPicker is null, command => commandNames.Contains(command), ExpandCommand);
        timer.Tick += async (_, _) => { if (vm.Unlocked && DateTime.UtcNow >= nextSync) { nextSync = DateTime.UtcNow.AddSeconds(15); await vm.Sync(); } if (DateTime.UtcNow >= nextUpdateCheck) { nextUpdateCheck = DateTime.UtcNow.AddHours(24); await CheckUpdatesQuietly(); } };
        timer.Start();
        Closing += (_, e) => { SavePlacement(); if (!exiting && vm.Local.Settings.CloseToTray) { e.Cancel = true; Hide(); } else if (!exiting) StopBackground(); };
        Closed += (_, _) => { if (exiting) Application.Current.Shutdown(); };
        Loaded += (_, _) => GatePassword.Focus();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) s.Maximized = WindowState == WindowState.Maximized; };
        UpdateDetailLayout();
        if (vm.Connected) Dispatcher.BeginInvoke(async () => await vm.Open("", s.Remember));
    }
    void SavePlacement()
    {
        var s = vm.Local.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.Width = bounds.Width;
        s.Height = bounds.Height;
        s.Left = bounds.Left;
        s.Top = bounds.Top;
        if (WindowState != WindowState.Minimized) s.Maximized = WindowState == WindowState.Maximized;
        vm.Local.SaveSettings();
    }
    void ApplyScale() => Body.LayoutTransform = new ScaleTransform(vm.Local.Settings.Scale, vm.Local.Settings.Scale);
    void Register()
    {
        RefreshCommands();
        try
        {
            hotkeys.Set(BuildHotkeys(), []);
            if (hotkeys.Warning.Length > 0)
                vm.Status = hotkeys.Warning;
        }
        catch (Exception e) { vm.Status = e.Message; }
    }
    void RefreshCommands()
    {
        commandNames = vm.Local.Settings.Commands.Select(c => c.Command.ToLowerInvariant()).ToHashSet();
        vm.RefreshCommands();
    }
    void UpdateDetailLayout()
    {
        if (detailShown == vm.Detail) return;
        if (!vm.Detail) detailWidth = DetailColumn.Width;
        detailShown = vm.Detail;
        DetailColumn.Width = vm.Detail ? detailWidth : new GridLength(0);
        SplitterColumn.Width = new GridLength(vm.Detail ? 8 : 0);
        DetailSplitter.Visibility = vm.Detail ? Visibility.Visible : Visibility.Collapsed;
    }
    void SetPinned(bool pinned)
    {
        Topmost = pinned;
        PinButton.Foreground = pinned ? ThemePalette.Brush("#A99CDA") : Foreground;
        PinButton.ToolTip = pinned ? "Открепить: окно сейчас поверх других" : "Закрепить поверх других окон";
    }
    void PinClick(object sender, RoutedEventArgs e) { vm.Local.Settings.AlwaysOnTop = !Topmost; SetPinned(!Topmost); vm.Local.SaveSettings(); }
    public void ShowPanel()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = vm.Local.Settings.Maximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        if (vm.Gated)
        {
            if (vm.NeedsPassword)
                GatePassword.Focus();
            else
                OpenButton.Focus();
        }
        else
            SearchBox.Focus();
    }
    void Toggle()
    {
        if (IsVisible && WindowState != WindowState.Minimized && IsActive)
            Hide();
        else
            ShowPanel();
    }
    void Quit()
    {
        SavePlacement();
        StopBackground();
        Close();
    }
    void StopBackground()
    {
        exiting = true;
        timer.Stop();
        tray.Dispose();
        hotkeys.Dispose();
        expander.Dispose();
        vm.Lock();
    }
    async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (vm.Busy || DateTime.UtcNow < retryAfter)
            return;
        string password = GatePassword.Password;
        GatePassword.Clear();
        OpenButton.IsEnabled = false;
        try
        {
            await vm.Open(password, RememberBox.IsChecked == true);
            if (vm.Unlocked)
            {
                unlockFailures = 0;
                SearchBox.Focus();
            }
            else
            {
                unlockFailures++;
                retryAfter = DateTime.UtcNow.AddSeconds(Math.Min(30, unlockFailures * 2));
            }
        }
        finally { OpenButton.IsEnabled = true; }
    }
    void PasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenClick(sender, e);
            e.Handled = true;
        }
    }
    void CommandsClick(object sender, RoutedEventArgs e)
    {
        ShowAllBinds();
    }
    void ClearClick(object sender, RoutedEventArgs e)
    {
        vm.Query = "";
        SearchBox.Focus();
    }
    async void SemanticClick(object sender, RoutedEventArgs e) => await vm.SemanticSearch();
    async void SyncClick(object sender, RoutedEventArgs e) => await vm.Sync();
    void CopyClick(object sender, RoutedEventArgs e) => vm.Copy();
    void PreviousResponseClick(object sender, RoutedEventArgs e) => vm.MoveResponse(-1);
    void NextResponseClick(object sender, RoutedEventArgs e) => vm.MoveResponse(1);
    void FavoriteClick(object sender, RoutedEventArgs e) => vm.Favorite();
    void CloseDetailClick(object sender, RoutedEventArgs e) => vm.DetailVisible = false;
    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.IsKeyboardFocusWithin || Mouse.LeftButton == MouseButtonState.Pressed)
            vm.DetailVisible = true;
    }
    async void LogoutClick(object sender, RoutedEventArgs e)
    {
        await vm.Logout();
    }
    void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (vm.Query.Length > 0 && SearchBox.IsKeyboardFocusWithin)
                vm.Query = "";
            else if (vm.Detail)
                vm.DetailVisible = false;
            else
                Hide();
            e.Handled = true;
            return;
        }
        if (!vm.Unlocked)
            return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            vm.Copy();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Enter)
        {
            SemanticClick(sender, e);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            ShowAllBinds();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            Edit(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && vm.Results.Count > 0)
        {
            ResultList.SelectedIndex = 0;
            ResultList.Focus();
            e.Handled = true;
        }
    }
    void NewClick(object sender, RoutedEventArgs e) => Edit(null);
    void EditClick(object sender, RoutedEventArgs e) => Edit(vm.Selected);
    async void DeleteFromListClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!vm.Unlocked || sender is not Button { DataContext: Answer answer } button)
            return;
        if (MessageBox.Show(this, $"Удалить пасту «{answer.Title}» из общей базы?", "Удаление пасты", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        button.IsEnabled = false;
        try
        {
            await vm.SaveAnswer(answer, answer.Version, true);
            vm.Status = "Паста удалена из общей базы.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex is AccessDeniedException ? "Доступ истёк. Войдите заново." : ex.Message, "Не удалось удалить пасту", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { button.IsEnabled = true; }
    }
    void Edit(Answer? original)
    {
        if (!vm.Unlocked)
            return;
        var a = original is null ? new Answer() : original with
        {
        };
        var form = new StackPanel { Margin = new Thickness(20) };
        var title = Field(form, "Название проблемы", a.Title);
        var category = new CategorySelector(vm.Categories, original is null && vm.Category != "Все категории" ? vm.Category : a.Category);
        form.Children.Add(category);
        var os = new ComboBox { ItemsSource = vm.Platforms, SelectedItem = a.Platform };
        form.Children.Add(os);
        var responseEditor = new ResponseEditor(a);
        form.Children.Add(responseEditor);
        var notes = Field(form, "Внутренняя заметка", a.Notes);
        var source = Field(form, "Источник", a.Source);
        var review = new CheckBox { Content = "Требует проверки", IsChecked = a.NeedsReview };
        form.Children.Add(review);
        var msg = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange };
        form.Children.Add(msg);
        var window = Dialog("Ответ", form, 620, 650);
        var save = new Button { Content = "Сохранить" };
        save.Click += async (_, _) => { save.IsEnabled = false; try { if (category.Value.Length == 0) throw new ArgumentException("Введите название своей категории."); a.Title = title.Text.Trim(); a.Category = category.Value; a.Platform = (string)os.SelectedItem; a.Responses = responseEditor.GetOptions(); a.Body = a.Responses[0].Body; a.Notes = notes.Text; a.Source = source.Text; a.NeedsReview = review.IsChecked == true; await vm.SaveAnswer(a, original?.Version ?? 0); window.Close(); } catch (Exception ex) { msg.Text = ex is AccessDeniedException ? "Доступ истек. Войдите заново." : ex.Message; } finally { save.IsEnabled = true; } };
        form.Children.Add(save);
        if (original is not null)
        {
            var delete = new Button { Content = "Удалить ответ" };
            delete.Click += async (_, _) => { if (MessageBox.Show(window, "Удалить ответ из общей базы?", "Удаление", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; try { await vm.SaveAnswer(original, original.Version, true); window.Close(); } catch (Exception ex) { msg.Text = ex.Message; } };
            form.Children.Add(delete);
        }
        window.ShowDialog();
    }
    static TextBox Field(StackPanel form, string label, string value)
    {
        form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 9, 4, 3) });
        var input = new TextBox { Text = value };
        form.Children.Add(input);
        return input;
    }
    Window Dialog(string title, StackPanel form, double width, double height) => new() { Title = title, Owner = this, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ThemePalette.Brush("#151720"), Foreground = ThemePalette.Brush("#E9EAF0"), ShowInTaskbar = false, Content = new ScrollViewer { Content = form }, Resources = Resources };
    TextBox HotkeyField(StackPanel form, string label, string value)
    {
        var field = Field(form, label, value);
        field.IsReadOnly = true;
        field.ToolTip = "Нажмите на поле, затем нажмите нужную клавишу или сочетание";
        field.GotKeyboardFocus += (_, _) => hotkeys.Suspend();
        field.LostKeyboardFocus += (_, _) => hotkeys.Resume();
        field.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab)
                return;
            e.Handled = true;
            try
            {
                field.Text = Hotkeys.Format(key, Keyboard.Modifiers);
            }
            catch (ArgumentException) { }
        };
        return field;
    }
    void BindClick(object sender, RoutedEventArgs e)
    {
        if (vm.Selected is null) return;
        var id = vm.Selected.Id;
        var form = new StackPanel { Margin = new Thickness(20) };
        var old = vm.Local.Settings.Commands.FirstOrDefault(c => c.AnswerId == id);
        var input = Field(form, "Команда, например ..интернет или /интернет", old?.Command ?? "");
        form.Children.Add(new TextBlock { Text = "Введите ..команда или /команда в поле чата и нажмите пробел. Точки можно набрать в русской раскладке. Она заменится пастой без отправки сообщения. Одной команде можно назначить несколько паст.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 4, 10) });
        var message = new TextBlock { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
        form.Children.Add(message);
        var window = Dialog("Команда для ответа", form, 490, 330);
        var save = new Button { Content = "Сохранить" };
        save.Click += (_, _) => {
            var previous = vm.Local.Settings.Commands.ToList();
            try { var command = CommandMatcher.Normalize(input.Text); vm.Local.Settings.Commands.RemoveAll(c => c.AnswerId == id); vm.Local.Settings.Commands.Add(new(id, command)); vm.Local.SaveSettings(); RefreshCommands(); vm.Status = "Команда сохранена: " + command; window.Close(); }
            catch (Exception ex) { vm.Local.Settings.Commands = previous; message.Text = ex.Message; }
        };
        form.Children.Add(save);
        var remove = new Button { Content = "Удалить команду" };
        remove.Click += (_, _) => { vm.Local.Settings.Commands.RemoveAll(c => c.AnswerId == id); vm.Local.SaveSettings(); RefreshCommands(); window.Close(); };
        form.Children.Add(remove);
        window.ShowDialog();
    }
    IEnumerable<(string, Action)> BuildHotkeys()
    {
        yield return (vm.Local.Settings.PanelGesture, Toggle);
        yield return (vm.Local.Settings.BindListGesture, ShowAllBinds);
    }
    void ShowAllBinds()
    {
        if (!vm.Unlocked)
        {
            ShowPanel();
            vm.Status = "Сначала войдите в базу поддержки.";
            return;
        }
        if (answerPicker is { IsVisible: true })
        {
            answerPicker.Activate();
            return;
        }
        if (OwnedWindows.Cast<Window>().Any(w => w.IsVisible && w is not PasteToast))
            return;
        var answers = vm.Local.Settings.Commands.Select(c => vm.AnswerById(c.AnswerId)).OfType<Answer>().DistinctBy(a => a.Id).SelectMany(a => a.Options.Select(o => a with { Body=o.Body, Notes=o.When, Title=a.Options.Count>1 ? a.Title + " · " + o.DisplayTitle : a.Title, Responses=[] })).ToList();
        if (answers.Count == 0)
        {
            ShowToast("Назначьте команду в карточке ответа", "Нет назначенных команд");
            return;
        }
        var labels = vm.Local.Settings.Commands.GroupBy(b => b.AnswerId).ToDictionary(g => g.Key, g => string.Join(" · ", g.Select(b => b.Command).Distinct()));
        ShowBindPicker(answers, vm.Local.Settings.BindListGesture, PasteService.GetForegroundWindow(), a => labels[a.Id], "Все команды");
    }
    void ShowBindPicker(IReadOnlyList<Answer> answers, string gesture, IntPtr target, Func<Answer, string>? badge = null, string? heading = null)
    {
        var picker = new AnswerPicker(answers, gesture, badge, heading) { Owner = this };
        answerPicker = picker;
        picker.Closed += (_, _) => answerPicker = null;
        picker.Resources = Resources;
        picker.Chosen += async answer => { picker.Close(); await PasteAnswer(answer, target, 0x0D); };
        picker.Show();
        picker.Activate();
    }
    void ShowToast(string title, string hint = "Паста вставлена")
    {
        pasteToast?.Close();
        pasteToast = new PasteToast(title, hint);
        pasteToast.Show();
    }
    async Task PasteAnswer(Answer answer, IntPtr target, uint trigger)
    {
        try
        {
            bool pasted = await PasteService.PasteAsync(target, answer.Body, () => vm.Unlocked && vm.AnswerById(answer.Id) is not null, trigger);
            if (pasted && vm.Unlocked)
            {
                vm.Local.Settings.Recent[answer.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                vm.Local.SaveSettings();
                vm.Status = "Ответ вставлен";
                ShowToast(answer.Title);
            }
        }
        catch (Exception ex) { vm.Status = ex.Message; tray.ShowBalloonTip(3500, "Не удалось вставить ответ", ex.Message, Forms.ToolTipIcon.Info); }
    }
    async void ExpandCommand(string command, IntPtr target, Func<bool> unchanged)
    {
        var answers = vm.Local.Settings.Commands.Where(c => c.Command == CommandMatcher.Normalize(command)).Select(c => vm.AnswerById(c.AnswerId)).OfType<Answer>().DistinctBy(a => a.Id).SelectMany(a => a.Options.Select(o => a with { Body=o.Body, Notes=o.When, Title=a.Options.Count>1 ? a.Title + " · " + o.DisplayTitle : a.Title, Responses=[] })).ToArray();
        if (answers.Length == 0) return;
        if (answers.Length > 1)
        {
            try
            {
                if (await PasteService.PasteAsync(target, "", () => vm.Unlocked && unchanged(), 0, command.Length, true))
                    ShowBindPicker(answers, command, target);
            }
            catch (Exception ex) { ShowToast(ex.Message, "Не удалось открыть список паст"); }
        }
        else await ReplaceCommand(answers[0], command, target, true, unchanged);
    }
    async Task ReplaceCommand(Answer answer, string command, IntPtr target, bool keepFocus, Func<bool> unchanged)
    {
        try
        {
            if (await PasteService.PasteAsync(target, answer.Body, () => vm.AnswerById(answer.Id) is not null && unchanged(), 0, command.Length, keepFocus))
            { vm.Local.Settings.Recent[answer.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); vm.Local.SaveSettings(); ShowToast(answer.Title); }
        }
        catch (Exception ex) { vm.Status = ex.Message; ShowToast(ex.Message, "Не удалось заменить команду"); }
    }
    string UpdateRepository => ReleaseClient.Repository;
    void UpdatesClick(object sender, RoutedEventArgs e) => ShowUpdates();
    async Task CheckUpdatesQuietly()
    {
        if (!vm.Local.Settings.CheckUpdates || UpdateRepository.Length == 0 || exiting) return;
        try
        {
            var release = await ReleaseClient.Check(UpdateRepository);
            if (!exiting && release is not null && ReleaseClient.IsNewer(release.Version))
            {
                availableUpdate = release;
                UpdatesButton.Content = "Обновить · " + release.Version;
                UpdatesButton.ToolTip = "Доступна новая версия. Скачать и установить";
            }
        }
        catch { /* Background failures never interrupt support work. */ }
    }
    void ShowUpdates()
    {
        var form = new StackPanel { Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = "Обновления · " + ReleaseClient.CurrentLabel, FontSize = 20 });
        var automatic = new CheckBox { Content = "Проверять новые версии автоматически", IsChecked = vm.Local.Settings.CheckUpdates };
        form.Children.Add(automatic);
        automatic.Click += (_, _) => { vm.Local.Settings.CheckUpdates = automatic.IsChecked == true; vm.Local.SaveSettings(); };
        var status = new TextBlock { Text = "Проверка при запуске и раз в сутки. Установка выполняется по кнопке с перезапуском приложения. Настройки и команды сохраняются.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 12) };
        form.Children.Add(status);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 7, Margin = new Thickness(4, 8, 4, 8), Visibility = Visibility.Collapsed };
        form.Children.Add(progress);
        var check = new Button { Content = "Проверить обновления" };
        var install = new Button { Content = "Скачать и установить", Visibility = Visibility.Collapsed };
        var cancel = new Button { Content = "Отменить скачивание", Visibility = Visibility.Collapsed };
        var win = Dialog("Обновления", form, 580, 430);
        CancellationTokenSource? download = null;
        AppRelease? shown = availableUpdate;
        void ShowRelease(AppRelease? release)
        {
            shown = release;
            availableUpdate = release is not null && ReleaseClient.IsNewer(release.Version) ? release : null;
            UpdatesButton.Content = availableUpdate is null ? "Обновления" : "Обновить · " + availableUpdate.Version;
            install.Visibility = release is not null && ReleaseClient.IsNewer(release.Version) && release.Package is not null ? Visibility.Visible : Visibility.Collapsed;
            status.Text = release is null ? "Нет опубликованной стабильной версии или репозиторий недоступен. Для обновлений нужна версия в разделе Releases." : !ReleaseClient.IsNewer(release.Version) ? "Установлена актуальная версия." : "Доступна версия " + release.Version + (release.Package is null ? " · Windows-архив еще не опубликован." : "") + "\n\n" + release.Notes;
        }
        if (shown is not null) ShowRelease(shown);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false; install.Visibility = Visibility.Collapsed;
            try
            {
                status.Text = "Проверка GitHub Releases…";
                ShowRelease(await ReleaseClient.Check(UpdateRepository));
            }
            catch (Exception) { status.Text = "Не удалось проверить обновления. Проверьте соединение и доступность репозитория."; }
            finally { check.IsEnabled = true; }
        };
        cancel.Click += (_, _) => download?.Cancel();
        win.Closed += (_, _) => download?.Cancel();
        install.Click += async (_, _) =>
        {
            if (shown is null) return;
            var release = shown;
            download = new();
            check.IsEnabled = install.IsEnabled = false;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            string? stage = null; bool started = false;
            try
            {
                status.Text = "Скачивание обновления…";
                stage = await UpdateInstaller.Download(release, new Progress<int>(p => { progress.Value = p; status.Text = "Скачивание обновления · " + p + "%"; }), download.Token);
                download.Token.ThrowIfCancellationRequested();
                cancel.Visibility = Visibility.Collapsed;
                status.Text = "Проверено. Подготовка перезапуска…";
                await UpdateInstaller.Start(stage, release.Version);
                started = true;
                Quit();
            }
            catch (OperationCanceledException) { status.Text = "Скачивание отменено."; }
            catch (Exception ex) { status.Text = ex is UnauthorizedAccessException ? "Нет прав на запись в папку приложения. Переместите приложение в свою папку." : ex.Message; }
            finally
            {
                if (!started && stage is not null) UpdateInstaller.DeleteStage(stage);
                download.Dispose(); download = null;
                check.IsEnabled = install.IsEnabled = true;
                cancel.Visibility = progress.Visibility = Visibility.Collapsed;
            }
        };
        form.Children.Add(check); form.Children.Add(install); form.Children.Add(cancel);
        win.ShowDialog();
    }
    async void SettingsClick(object sender, RoutedEventArgs e)
    {
        var s = vm.Local.Settings;
        var form = new StackPanel { Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = "Настройки · версия " + ReleaseClient.CurrentLabel, FontSize = 20 });
        var panel = HotkeyField(form, "Показать / скрыть панель", s.PanelGesture);
        var bindList = HotkeyField(form, "Открыть список всех команд", s.BindListGesture);
        var scale = new ComboBox { ItemsSource = new[] { 75, 90, 100, 110, 125, 150 }, SelectedItem = (int)(s.Scale * 100) };
        form.Children.Add(new TextBlock { Text = "Масштаб, %" });
        form.Children.Add(scale);

        form.Children.Add(new TextBlock { Text = "Тема оформления" });
        var theme = new ComboBox { ItemsSource = ThemePalette.Names, SelectedItem = ThemePalette.Names.Contains(s.Theme) ? s.Theme : ThemePalette.Names[0] };
        form.Children.Add(theme);
        theme.SelectionChanged += (_, _) => ThemePalette.Apply((string)theme.SelectedItem);
        var startup = new CheckBox { Content = "Запускать при входе в Windows", IsChecked = s.Startup };
        form.Children.Add(startup);
        var closeToTray = new CheckBox { Content = "При закрытии оставлять приложение в трее", IsChecked = s.CloseToTray, ToolTip = "Команды вставки продолжают работать. Для полного выхода используйте меню значка в трее." };
        form.Children.Add(closeToTray);
        var pinned = new CheckBox { Content = "Закрепить поверх других окон", IsChecked = s.AlwaysOnTop };
        form.Children.Add(pinned);
        var updates = new Button { Content = "Обновления приложения" };
        updates.Click += (_, _) => ShowUpdates();
        form.Children.Add(updates);
        var msg = new TextBlock { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
        form.Children.Add(msg);
        var win = Dialog("Настройки", form, 520, 690);
        win.Closed += (_, _) => { hotkeys.Resume(); ThemePalette.Apply(s.Theme); };
        var save = new Button { Content = "Сохранить настройки" };
        save.Click += (_, _) => { string old = s.PanelGesture, oldList = s.BindListGesture; try { hotkeys.Resume(); s.PanelGesture = panel.Text.Trim(); s.BindListGesture = bindList.Text.Trim(); hotkeys.Set(BuildHotkeys(), [s.PanelGesture, s.BindListGesture]); using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (startup.IsChecked == true) run.SetValue("CristalixSupportDesk", "\"" + Environment.ProcessPath + "\" --background"); else run.DeleteValue("CristalixSupportDesk", false); s.CloseToTray = closeToTray.IsChecked == true; s.AlwaysOnTop = pinned.IsChecked == true; SetPinned(s.AlwaysOnTop); s.Theme = (string)theme.SelectedItem; s.Startup = startup.IsChecked == true; s.Scale = (int)scale.SelectedItem / 100d; vm.Local.SaveSettings(); ApplyScale(); win.Close(); } catch (Exception ex) { s.PanelGesture = old; s.BindListGesture = oldList; Register(); msg.Text = ex.Message; } };
        form.Children.Add(save);
        if (vm.Unlocked)
        {

            var logout = new Button { Content = "Выйти из базы поддержки" };
            logout.Click += async (_, _) => { await vm.Logout(); win.Close(); };
            form.Children.Add(logout);
        }
        else if (!vm.Connected)
        {
            var url = Field(form, "Адрес HTTPS API", s.Url);
            var pin = Field(form, "SHA-256 сертификата · пусто для обычного HTTPS", s.CertificateSha256);
            var connect = new Button { Content = "Сохранить подключение" };
            connect.Click += (_, _) => { try { var u = new Uri(url.Text); if (u.Scheme != "https") throw new ArgumentException("Нужен HTTPS."); s.Url = url.Text.TrimEnd('/'); s.CertificateSha256 = pin.Text.Trim(); vm.Local.SaveSettings(); win.Close(); } catch (Exception ex) { msg.Text = ex.Message; } };
            form.Children.Add(connect);
        }
        await Task.CompletedTask;
        win.ShowDialog();
    }
}
