using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SupportDesk.Core;
using SupportDesk.Desktop;
public static class UiChecks
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 40)]
    struct TestInput
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Type;
        [System.Runtime.InteropServices.FieldOffset(8)] public ushort Key;
        [System.Runtime.InteropServices.FieldOffset(12)] public uint Flags;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, TestInput[] inputs, int size);
    public static void Run(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                var local = new LocalStore(Path.Combine(root, "ui"));
                local.Settings.PanelGesture = "Ctrl+Alt+Shift+F12";
                local.Settings.BindListGesture = "Ctrl+Alt+Shift+F11";
                local.Unlock();
                var vm = new MainViewModel(local);
                var window = new MainWindow(vm) { Left = -10000, Top = -10000 };
                window.Show();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                vm.Categories.Add("Ошибки запуска"); vm.Categories.Add("Оплата"); vm.Categories.Add("Linux");
                var categoryBox = (CategorySearchBox)window.FindName("CategoryBox");
                // Test the actual editable template once the panel is visible.
                Render(window, Path.Combine(root, "login.png"));
                if (window.WindowStyle != WindowStyle.SingleBorderWindow) throw new Exception("Native window frame missing");
                if (window.Topmost || !window.ShowInTaskbar) throw new Exception("Normal app window defaults failed");
                ((Button)window.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!window.Topmost || !local.Settings.AlwaysOnTop) throw new Exception("Pin toggle failed");
                ((Button)window.FindName("PinButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (window.Topmost) throw new Exception("Unpin toggle failed");
                window.WindowState = WindowState.Minimized;
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (window.WindowState != WindowState.Minimized) throw new Exception("Normal minimize failed");
                window.ShowPanel();
                var data = Enumerable.Range(0, 10000).Select(i => new Answer { Id = "demo-" + i, Title = "Пример проблемы " + i, Category = "Тестовая категория", Body = "Искусственный ответ для проверки интерфейса.\nТекст не взят из базы поддержки." }).ToList();
                typeof(MainViewModel).GetField("unlocked", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);
                typeof(MainViewModel).GetProperty("Results")!.SetValue(vm, data);
                typeof(MainViewModel).GetMethod("Notify", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, ["Unlocked"]);
                typeof(MainViewModel).GetMethod("Notify", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, ["Gated"]);
                typeof(MainViewModel).GetMethod("Notify", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, ["Results"]);
                typeof(MainViewModel).GetField("answers", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, data.ToDictionary(a => a.Id));
                local.Settings.Commands.Add(new(data[0].Id, "/интернет"));
                vm.RefreshCommands();
                vm.Query = "интернет";
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while ((vm.Results.Count != 1 || vm.Results[0].Id != data[0].Id) && wait.ElapsedMilliseconds < 2000) { app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                if (vm.Results.Count != 1) throw new Exception("View model command search failed");
                vm.Selected = vm.Results[0];
                if (!vm.SelectedCommand.Contains("..интернет")) throw new Exception("Command badge missing");
                vm.Query = "";
                wait.Restart();
                while (vm.Results.Count != 10000 && wait.ElapsedMilliseconds < 2000) { app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background); Thread.Sleep(10); }
                vm.Selected = data[0];
                window.UpdateLayout();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                var list = (ListBox)window.FindName("ResultList");
                if (list.ItemContainerGenerator.ContainerFromIndex(9000) is not null)
                    throw new Exception("List virtualization disabled");
                categoryBox.ApplyTemplate();
                var categoryInput = (TextBox)categoryBox.Template.FindName("PART_EditableTextBox", categoryBox);
                categoryInput.Focus();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                categoryInput.Text = "плат";
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (categoryBox.Items.Count != 1 || (string)categoryBox.Items[0] != "Оплата") throw new Exception("Typed category filter failed: " + categoryBox.Text + "; " + categoryBox.Items.Count + "; focus=" + categoryInput.IsKeyboardFocusWithin + "; state=" + window.WindowState);
                categoryBox.SelectedItem = "Оплата";
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (vm.Category != "Оплата") throw new Exception("Category selection did not reach search");
                categoryBox.IsDropDownOpen = false;
                ((TextBox)window.FindName("SearchBox")).Focus();
                vm.Category = "Все категории";
                Render(window, Path.Combine(root, "main.png"));
                vm.DetailVisible = false;
                window.UpdateLayout();
                if (((ColumnDefinition)window.FindName("DetailColumn")).Width.Value != 0) throw new Exception("Closed detail did not release list space");
                Render(window, Path.Combine(root, "list.png"));
                window.Width = 820;
                window.Height = 700;
                vm.DetailVisible = true;
                window.UpdateLayout();
                Render(window, Path.Combine(root, "compact.png"));
                foreach (var theme in ThemePalette.Names) { ThemePalette.Apply(theme); Render(window, Path.Combine(root, "theme-" + Array.IndexOf(ThemePalette.Names, theme) + ".png")); }
                ThemePalette.Apply("Темная");
                var multiple = data[0] with { Responses=[new() { Id="one", Title="Проверка настроек", Step=1, Body="Первый точный ответ" }, new() { Id="two", Title="Следующий способ решения", Step=2, Body="Второй точный ответ\nhttps://example.com", When="Если после проверки настроек проблема сохраняется." }] };
                vm.Selected=multiple; vm.MoveResponse(1);
                if(vm.ResponseBody!=multiple.Responses[1].Body || !vm.ResponseHint.Contains("Если после")) throw new Exception("Selected response binding failed");
                Render(window,Path.Combine(root,"responses.png"));
                var editor = new ResponseEditor(multiple);
                T Field<T>(string name) => (T)typeof(ResponseEditor).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
                var originalBody=multiple.Responses[0].Body;
                Field<TextBox>("label").Text="Измененное название"; Field<TextBox>("body").Text="Правка\nбез потери текста"; Field<TextBox>("condition").Text="Условие";
                Field<ComboBox>("options").SelectedIndex=1;
                var edited=editor.GetOptions();
                if(edited[0].Body!="Правка\nбез потери текста" || edited[0].When!="Условие" || multiple.Responses[0].Body!=originalBody) throw new Exception("Editor lost unsaved text or changed original");
                void EditorButton(string title) => ((WrapPanel)editor.Children[2]).Children.OfType<Button>().Single(b=>(string)b.Content==title).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                EditorButton("Добавить вариант"); Field<TextBox>("body").Text="Третий ответ"; Field<TextBox>("step").Text="0";
                EditorButton("Выше");
                if(editor.GetOptions()[1].Body!="Третий ответ") throw new Exception("Editor ordering failed");
                EditorButton("Удалить вариант");
                if(editor.GetOptions().Count!=2) throw new Exception("Editor delete failed");
                if(Field<ComboBox>("options").SelectedItem is not ReplyOption chosenOption || chosenOption.Id!=editor.GetOptions()[0].Id) throw new Exception("Editor selection does not match fields after delete");
                var editorWindow=new Window { Content=new ScrollViewer { Content=editor, VerticalScrollBarVisibility=ScrollBarVisibility.Auto }, Width=620, Height=660, Resources=window.Resources, Background=window.Background, Foreground=window.Foreground, Left=-10000, Top=-10000 };
                editorWindow.Show(); Render(editorWindow,Path.Combine(root,"response-editor.png")); editorWindow.Close();
                var picker = new AnswerPicker(data.Take(3).ToArray(), "F23") { Owner = window, Resources = window.Resources, Left = -10000, Top = -10000 };
                picker.Show();
                Render(picker, Path.Combine(root, "bind-picker.png"));
                picker.Close();
                local.Settings.CloseToTray = true;
                window.Close();
                if (window.IsVisible) throw new Exception("Close to tray failed");
                window.ShowPanel();
                local.Settings.CloseToTray = false;
                window.Close();
                Console.WriteLine("PASS: WPF rendering, pin toggle, taskbar/minimize behavior and 10,000-item virtualization; previews: " + root);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr LoadKeyboardLayout(string name, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern short VkKeyScanEx(char key, IntPtr layout);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    public static void Paste()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application();
            var text = new TextBox { Text = "before ", AcceptsReturn = true, Margin = new Thickness(20) };
            var target = new Window { Title = "Проверка команд Cristalix Support", Width = 560, Height = 180, Content = text };
            target.Show();
            app.Dispatcher.BeginInvoke(async () =>
            {
                var previousLayout = ActivateKeyboardLayout(LoadKeyboardLayout("00000419", 0), 0);
                try
                {
                    target.Activate(); await Task.Delay(100); text.Focus(); text.CaretIndex = text.Text.Length;
                    var handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;
                    var local = new LocalStore(Path.Combine(Path.GetTempPath(), "supportdesk-commands-" + Guid.NewGuid().ToString("N")));
                    local.Settings.PanelGesture = "Ctrl+Alt+Shift+F23";
                    var vm = new MainViewModel(local);
                    typeof(MainViewModel).GetField("unlocked", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);
                    var answer = new Answer { Id = "one", Title = "Тестовая паста", Body = "Точный ответ\nhttps://example.com/?a=1&b=2" };
                    typeof(MainViewModel).GetField("answers", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, new Dictionary<string, Answer> { [answer.Id] = answer });
                    local.Settings.Commands.Add(new(answer.Id, "/интернет"));
                    var main = new MainWindow(vm);
                    await PasteService.PasteAsync(handle, "", () => true);
                    await Task.Delay(100); text.Focus(); text.CaretIndex = text.Text.Length;
                    var expand = typeof(MainWindow).GetMethod("ExpandCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    string received = "";
                    using var keyboard = new TextExpander(app.Dispatcher, () => vm.Unlocked, command => command == "/интернет", (command, window, unchanged) => { received = command + ":" + unchanged(); expand.Invoke(main, [command, window, unchanged]); }, window => window == handle);
                    void Type(string value)
                    {
                        var inputs = new List<TestInput>();
                        foreach (char c in value) {
                            short mapped = VkKeyScanEx(c, GetKeyboardLayout(0));
                            if (mapped == -1) throw new Exception("No keyboard mapping: " + c);
                            ushort key = (ushort)(mapped & 255);
                            bool shift = (mapped & 0x100) != 0;
                            if (shift) inputs.Add(new TestInput { Type=1, Key=0x10 });
                            inputs.Add(new TestInput { Type=1, Key=key });
                            inputs.Add(new TestInput { Type=1, Key=key, Flags=2 });
                            if (shift) inputs.Add(new TestInput { Type=1, Key=0x10, Flags=2 });
                        }
                        var batch = inputs.ToArray();
                        if (SendInput((uint)batch.Length, batch, 40) != batch.Length) throw new Exception("Test keyboard input failed");
                    }
                    foreach (char letter in "/интернет ") { Type(letter.ToString()); await Task.Delay(25); }
                    await Task.Delay(400);
                    if (text.Text.Replace("\r\n", "\n") != "before " + answer.Body) throw new Exception("Typed command did not replace exactly: " + text.Text + "; received=" + received + "; focused=" + text.IsKeyboardFocused + "; active=" + (PasteService.GetForegroundWindow()==handle) + "; status=" + vm.Status);
                    if (PasteService.GetForegroundWindow() != handle || typeof(MainWindow).GetField("pasteToast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) is not PasteToast { IsVisible: true }) throw new Exception("Toast stole focus or is absent");
                    text.Text = "before "; text.CaretIndex = text.Text.Length;
                    foreach (char letter in "..интернет ") { Type(letter.ToString()); await Task.Delay(25); }
                    await Task.Delay(400);
                    if (text.Text.Replace("\r\n", "\n") != "before " + answer.Body) throw new Exception("Russian dots alias failed: " + text.Text);
                    text.Text = "before /"; text.CaretIndex = text.Text.Length;
                    keyboard.Reset(); Type("/");
                    var switchKeys = new[] { new TestInput { Type=1, Key=0x12 }, new TestInput {Type=1, Key=0x10}, new TestInput {Type=1, Key=0x10,Flags=2},new TestInput {Type=1, Key=0x12,Flags=2} };
                    SendInput(4, switchKeys, 40); await Task.Delay(100);
                    ActivateKeyboardLayout(LoadKeyboardLayout("00000419", 0), 0);
                    text.Text = "before /"; text.CaretIndex = text.Text.Length;
                    foreach (char letter in "интернет ") { Type(letter.ToString()); await Task.Delay(25); }
                    await Task.Delay(400);
                    if (text.Text.Replace("\r\n", "\n") != "before " + answer.Body) throw new Exception("Layout modifier reset command: " + text.Text);
                    text.Text = ""; text.CaretIndex = 0;
                    Type("/интернет_другая "); await Task.Delay(200);
                    if (text.Text != "/интернет_другая ") throw new Exception("Prefix expanded incorrectly");
                    text.Text = ""; text.CaretIndex = 0;
                    Type("/интернет х"); await Task.Delay(250);
                    if (!text.Text.Contains("/интернет") || !text.Text.EndsWith('х')) throw new Exception("Changed input was destructively replaced");
                    text.Text = "before "; text.CaretIndex = text.Text.Length;
                    var second = answer with { Id = "two", Title = "Вторая паста" };
                    ((Dictionary<string, Answer>)typeof(MainViewModel).GetField("answers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Add(second.Id, second);
                    local.Settings.Commands.Add(new(second.Id, "/интернет"));
                    foreach (char letter in "/интернет ") { Type(letter.ToString()); await Task.Delay(25); }
                    await Task.Delay(300);
                    if (typeof(MainWindow).GetField("answerPicker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main) is not AnswerPicker { IsVisible: true }) throw new Exception("Shared command did not open picker: text=" + text.Text + "; received=" + received + "; active=" + (PasteService.GetForegroundWindow() == handle) + "; toast=" + ((PasteToast?)typeof(MainWindow).GetField("pasteToast", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main))?.Title);
                    if (text.Text != "before ") throw new Exception("Shared command erased wrong text: " + text.Text);
                    Type("\r"); await Task.Delay(400);
                    if (text.Text.Replace("\r\n", "\n") != "before " + answer.Body) throw new Exception("Picker did not paste exact answer");
                    local.Settings.Commands.RemoveAll(c => c.AnswerId==second.Id);
                    answer.Responses=[new() { Id="main", Title="Основной", Body=answer.Body, Step=1 }, new() { Id="alternative", Title="Вариант", Body="Иной ответ\nhttps://example.com/alternative", When="Если проверка не помогла", Step=2 }];
                    text.Text="before "; text.CaretIndex=text.Text.Length; keyboard.Reset();
                    foreach(char letter in "/интернет ") { Type(letter.ToString()); await Task.Delay(25); }
                    await Task.Delay(300);
                    if(typeof(MainWindow).GetField("answerPicker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main) is not AnswerPicker { IsVisible:true }) throw new Exception("One problem with multiple replies did not open picker");
                    SendInput(2,[new TestInput {Type=1,Key=0x28},new TestInput {Type=1,Key=0x28,Flags=2}],40);
                    await Task.Delay(50); Type("\r"); await Task.Delay(400);
                    if(text.Text.Replace("\r\n","\n")!="before "+answer.Responses[1].Body) throw new Exception("Second reply was not inserted literally: "+text.Text);
                    if (!main.Topmost) throw new Exception("Panel not topmost");
                    
                    Console.WriteLine("PASS: real keyboard hook command expansion, exact multiline paste, unknown-prefix preservation, changed-input cancellation, shared-command picker, title toast, topmost panel");
                }
                catch (Exception ex) { failure = ex; }
                finally { ActivateKeyboardLayout(previousLayout, 0); target.Close(); app.Shutdown(); }
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
    static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        image.Render(bg);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var f = File.Create(path);
        encoder.Save(f);
    }
}

