namespace SupportDesk.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
    public LocalStore Local
    {
        get;
    }
    ApiClient? api;
    Dictionary<string, Answer> answers = [];
    SearchIndex index = new([]);
    long revision = -1;
    CancellationTokenSource? searchCancel;
    CancellationTokenSource connectionCancel = new();
    readonly SemaphoreSlim syncGate = new(1);
    int epoch;
    bool unlocked;
    string query = "", category = "Все категории", platform = "Все", status = "Войдите с паролем поддержки.", mode = "Все ответы";
    Answer? selected;
    ReplyOption? selectedResponse;
    public IReadOnlyList<ReplyOption> ResponseOptions => Selected?.Options ?? [];
    public ReplyOption? SelectedResponse { get => selectedResponse; set { selectedResponse = value; Notify(nameof(SelectedResponse)); Notify(nameof(ResponseBody)); Notify(nameof(ResponseHint)); } }
    public bool HasMultipleResponses => ResponseOptions.Count > 1;
    public IReadOnlyDictionary<string, string> CommandLabels { get; private set; } = new Dictionary<string, string>();
    public string SelectedCommand => Selected is not null && CommandLabels.TryGetValue(Selected.Id, out var label) ? "Вставка: " + label : "";
    public void RefreshCommands()
    {
        CommandLabels = Local.Settings.Commands.GroupBy(c => c.AnswerId).ToDictionary(g => g.Key,
            g => string.Join("  ·  ", g.Select(c => c.Command).Distinct().Select(c => c.StartsWith('/') ? ".." + c[1..] + "  ·  " + c : c)));
        index = new SearchIndex(answers.Values, CommandLabels);
        Notify(nameof(CommandLabels));
        Notify(nameof(SelectedCommand));
        ScheduleSearch();
    }
    public string ResponseBody => SelectedResponse?.Body ?? Selected?.Body ?? "";
    public string ResponseHint => SelectedResponse is null || ResponseOptions.Count == 1 && SelectedResponse.Step == 0 && SelectedResponse.When.Length == 0 ? "" : (SelectedResponse.Step > 0 ? $"Шаг {SelectedResponse.Step}. " : "Вариант ответа. ") + SelectedResponse.When;
    public void MoveResponse(int delta) { var options = ResponseOptions; int i = options.ToList().FindIndex(o => o.Id == SelectedResponse?.Id); if (options.Count > 0) SelectedResponse = options[Math.Clamp(i + delta, 0, options.Count - 1)]; }
    bool detailVisible = true;
    bool busy;
    public bool Unlocked
    {
        get => unlocked; private set
        {
            unlocked = value;
            Notify(nameof(Unlocked));
            Notify(nameof(Gated));
        }
    }
    public bool Gated => !Unlocked;
    public bool Busy
    {
        get => busy; private set
        {
            busy = value;
            Notify(nameof(Busy));
        }
    }
    public bool Connected => Local.Settings.Session is not null;
    public string GateTitle => "База поддержки";
    public bool NeedsPassword => !Connected;
    public bool SupportLogin => !Connected;
    public string OpenLabel => Connected ? "Открыть базу" : "Подключить базу";
    public string GateHint => !Connected ? "Введите общий пароль поддержки." : "Нажмите «Открыть базу», чтобы продолжить.";
    public string Status
    {
        get => status; set
        {
            status = value;
            Notify(nameof(Status));
        }
    }
    public string Query
    {
        get => query; set
        {
            query = value ?? "";
            Notify(nameof(Query));
            ScheduleSearch();
        }
    }
    public string Category
    {
        get => category; set
        {
            category = value ?? "Все категории";
            Notify(nameof(Category));
            ScheduleSearch();
        }
    }
    public string Platform
    {
        get => platform; set
        {
            platform = value ?? "Все";
            Notify(nameof(Platform));
            ScheduleSearch();
        }
    }
    public string Mode
    {
        get => mode; set
        {
            mode = value ?? "Все ответы";
            Notify(nameof(Mode));
            ScheduleSearch();
        }
    }
    public bool SearchEverywhere
    {
        get => Local.Settings.SearchEverywhere; set
        {
            Local.Settings.SearchEverywhere = value;
            Local.SaveSettings();
            Notify(nameof(SearchEverywhere));
            ScheduleSearch();
        }
    }
    public Answer? Selected
    {
        get => selected; set
        {
            string? keep = selected?.Id == value?.Id ? selectedResponse?.Id : null;
            selected = value;
            Notify(nameof(ResponseOptions));
            Notify(nameof(HasMultipleResponses));
            SelectedResponse = ResponseOptions.FirstOrDefault(o => o.Id == keep) ?? ResponseOptions.FirstOrDefault();
            Notify(nameof(Selected));
            Notify(nameof(SelectedCommand));
            Notify(nameof(Detail));
            Notify(nameof(FavoriteLabel));
        }
    }
    public bool DetailVisible
    {
        get => detailVisible; set
        {
            detailVisible = value;
            Notify(nameof(DetailVisible));
            Notify(nameof(Detail));
        }
    }
    public bool Detail => DetailVisible && Selected is not null;
    public string FavoriteLabel => Selected is not null && Local.Settings.Favorites.Contains(Selected.Id) ? "★ В избранном" : "☆ В избранное";
    public IReadOnlyList<Answer> Results { get; private set; } = [];
    public ObservableCollection<string> Categories { get; } = ["Все категории"];
    public string[] Platforms { get; } = ["Все", "Windows", "Linux", "macOS"];
    public string[] Modes { get; } = ["Все ответы", "Избранное", "Недавние"];
    public string[] SearchMatches { get; } = ["Все слова", "Любое слово", "Слова подряд", "Целые слова"];
    public string[] SearchScopes { get; } = ["Вся запись", "Только заголовок", "Только ответ", "Только команды вставки"];
    public int SearchMatchIndex { get => (int)Local.Settings.SearchMatch; set { Local.Settings.SearchMatch = (SearchMatch)Math.Clamp(value, 0, 3); Local.SaveSettings(); Notify(nameof(SearchMatchIndex)); ScheduleSearch(); } }
    public int SearchScopeIndex { get => (int)Local.Settings.SearchScope; set { Local.Settings.SearchScope = (SearchScope)Math.Clamp(value, 0, 3); Local.SaveSettings(); Notify(nameof(SearchScopeIndex)); ScheduleSearch(); } }
    public event Action? BindingsChanged;
    public MainViewModel(LocalStore local)
    {
        Local = local;
        if (Local.Settings.Url.Length == 0)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "connection.json");
                using var connection = File.Exists(path) ? File.OpenRead(path) : typeof(MainViewModel).Assembly.GetManifestResourceStream("SupportDesk.Connection")!;
                using var d = JsonDocument.Parse(connection);
                Local.Settings.Url = d.RootElement.GetProperty("Url").GetString()!;
                Local.Settings.CertificateSha256 = d.RootElement.GetProperty("CertificateSha256").GetString()!;
                Local.SaveSettings();
            }
            catch { }
        }
        NotifyGate();
    }
    void NotifyGate()
    {
        Notify(nameof(Connected));
        Notify(nameof(GateTitle));
        Notify(nameof(NeedsPassword));
        Notify(nameof(SupportLogin));
        Notify(nameof(OpenLabel));
        Notify(nameof(GateHint));
    }
    public async Task Open(string password, bool remember)
    {
        if (Busy)
            return;
        Busy = true;
        int generation = epoch;
        try
        {
            bool login = !Connected;
            if (login)
            {
                api?.Dispose();
                api = new(Local);
                await api.Login(password, remember, connectionCancel.Token);
            }
            await Task.Run(() => Local.Unlock());
            if (generation != epoch)
            {
                Local.Lock();
                return;
            }
            Unlocked = true;
            NotifyGate();
            Status = "Загрузка базы…";
            api ??= new(Local);
            var cached = await Task.Run(Local.ReadCache);
            if (cached is not null && Local.Settings.OfflineUntil > DateTimeOffset.UtcNow)
                await Apply(new(cached.Revision, true, cached.Answers, []));
            await Sync();
        }
        catch (CryptographicException) { Status = "Не удалось открыть сохраненные данные. Выйдите из базы и подключитесь снова."; }
        catch (AccessDeniedException) { Status = "Неверный пароль поддержки или срок доступа истек."; await Forget(); }
        catch (Exception) { Status = Unlocked ? "Нет связи с сервером. Доступна сохраненная база." : "Не удалось подключиться. Проверьте адрес API и соединение."; }
        finally { Busy = false; }
    }
    public async Task Sync()
    {
        if (!Unlocked || api is null || !await syncGate.WaitAsync(0))
            return;
        int generation = epoch;
        try
        {
            var changes = await api.Request<Changes>(HttpMethod.Get, "/v2/changes?since=" + revision, ct: connectionCancel.Token);
            if (generation != epoch || !Unlocked)
                return;
            if (changes.Revision != revision || changes.Reset)
                await Apply(changes);
            Local.Settings.OfflineUntil = DateTimeOffset.UtcNow.AddDays(1);
            Local.SaveSettings();
            Status = "Синхронизировано · " + DateTime.Now.ToString("HH:mm");
        }
        catch (AccessDeniedException) { await Forget(); Status = "Доступ истек. Введите пароль поддержки."; }
        catch (OperationCanceledException) { }
        catch (Exception) { if (generation != epoch) return; if (Local.Settings.OfflineUntil <= DateTimeOffset.UtcNow) { Lock(); Status = "Для продолжения подключитесь к серверу."; } else Status = "Нет связи · сохраненная база доступна для чтения."; }
        finally { syncGate.Release(); }
    }
    async Task Apply(Changes c)
    {
        if (c.Reset)
            answers.Clear();
        foreach (var id in c.Deleted)
            answers.Remove(id);
        foreach (var a in c.Answers)
            answers[a.Id] = a;
        revision = c.Revision;
        var snapshot = answers.Values.ToArray();
        index = await Task.Run(() => new SearchIndex(snapshot, CommandLabels));
        if (!Unlocked)
            return;
        var previousCategory = category;
        var categories = snapshot.Select(a => a.Category).Distinct().Order().ToArray();
        Categories.Clear();
        Categories.Add("Все категории");
        foreach (var v in categories)
            Categories.Add(v);
        category = Categories.Contains(previousCategory) ? previousCategory : "Все категории";
        Notify(nameof(Category));
        if (Selected is not null)
            Selected = answers.GetValueOrDefault(Selected.Id);
        Local.Settings.Commands.RemoveAll(b => !answers.ContainsKey(b.AnswerId));
        BindingsChanged?.Invoke();
        await Local.SaveCache(new(revision, snapshot.ToList()));
        ScheduleSearch();
    }
    async void ScheduleSearch()
    {
        searchCancel?.Cancel();
        searchCancel?.Dispose();
        searchCancel = new();
        var ct = searchCancel.Token;
        int generation = epoch;
        string q = query, cat = SearchEverywhere && query.Length > 0 ? "Все категории" : category, p = platform, m = mode;
        var captured = index;
        var favorites = new HashSet<string>(Local.Settings.Favorites);
        var recent = new Dictionary<string, long>(Local.Settings.Recent);
        var searchMatch = Local.Settings.SearchMatch;
        var searchScope = Local.Settings.SearchScope;
        try
        {
            await Task.Delay(160, ct);
            var found = await Task.Run(() => captured.Find(q, cat == "Все категории" ? "" : cat, p, m == "Избранное" ? favorites : null, ct, searchMatch, searchScope), ct);
            if (m == "Недавние")
                found = found.Where(a => recent.ContainsKey(a.Id)).OrderByDescending(a => recent[a.Id]).ToList();
            if (ct.IsCancellationRequested || generation != epoch || !Unlocked)
                return;
            Results = found;
            Notify(nameof(Results));
            Notify(nameof(ResultCount));
        }
        catch (OperationCanceledException) { }
    }
    public string ResultCount => Results.Count + " ответов";
    public async Task SemanticSearch()
    {
        if (!Unlocked || api is null || query.Trim().Length == 0)
            return;
        searchCancel?.Cancel();
        int generation = epoch;
        string q = query;
        Busy = true;
        Status = "Поиск по смыслу…";
        try
        {
            var r = await api.Request<SemanticResponse>(HttpMethod.Post, "/v2/search", new SearchRequest(q), connectionCancel.Token);
            if (generation != epoch || q != query || !Unlocked)
                return;
            Results = r.Hits.Where(h => answers.ContainsKey(h.Id)).Select(h => answers[h.Id]).Where(a => (SearchEverywhere || category == "Все категории" || a.Category == category) && (platform == "Все" || a.Platform == "Все" || a.Platform == platform) && (mode != "Избранное" || Local.Settings.Favorites.Contains(a.Id)) && (mode != "Недавние" || Local.Settings.Recent.ContainsKey(a.Id))).ToList();
            Notify(nameof(Results));
            Notify(nameof(ResultCount));
            Status = r.Indexing ? "Индекс обновляется. Показаны доступные совпадения." : Results.Count == 0 ? "Подходящие ответы не найдены." : "Ответы по смыслу · текст паст сохранен";
        }
        catch (AccessDeniedException) { await Forget(); Status = "Войдите с паролем поддержки."; }
        catch (Exception) { if (generation == epoch) Status = "Поиск по смыслу пока недоступен. Обычный поиск работает."; }
        finally { Busy = false; }
    }
    public void Copy(Answer? a = null)
    {
        a ??= Selected;
        if (!Unlocked || a is null)
            return;
        Clipboard.SetText(a.Id == Selected?.Id ? ResponseBody : a.Body);
        Local.Settings.Recent[a.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Local.SaveSettings();
        Status = "Ответ скопирован";
    }
    public void Favorite()
    {
        if (Selected is null)
            return;
        if (!Local.Settings.Favorites.Add(Selected.Id))
            Local.Settings.Favorites.Remove(Selected.Id);
        Local.SaveSettings();
        Notify(nameof(FavoriteLabel));
        ScheduleSearch();
    }
    public async Task SaveAnswer(Answer a, long version, bool delete = false)
    {
        if (api is null || !Unlocked)
            throw new InvalidOperationException("Нет подключения к общей базе. Подключитесь и повторите действие.");
        await api.Request<JsonElement>(delete ? HttpMethod.Delete : HttpMethod.Put, "/v2/answers/" + Uri.EscapeDataString(a.Id), new ChangeRequest(version, delete ? null : a), connectionCancel.Token);
        await Sync();
    }
    public Answer? AnswerById(string id) => Unlocked ? answers.GetValueOrDefault(id) : null;
    public void Lock()
    {
        epoch++;
        searchCancel?.Cancel();
        connectionCancel.Cancel();
        connectionCancel.Dispose();
        connectionCancel = new();
        Local.Lock();
        Unlocked = false;
        answers.Clear();
        index = new([]);
        revision = -1;
        Selected = null;
        Results = [];
        Notify(nameof(Results));
        Notify(nameof(ResultCount));
        NotifyGate();
    }
    async Task Forget()
    {
        Lock();
        Local.Settings.Session = null;
        Local.Settings.OfflineUntil = default;
        await Local.ClearCache();
        Local.ResetProtection();
        api?.Dispose();
        api = null;
        NotifyGate();
    }
    public async Task Logout()
    {
        if (api is not null)
            await api.Logout();
        await Forget();
        Status = "Вы вышли из базы поддержки.";
    }
}
