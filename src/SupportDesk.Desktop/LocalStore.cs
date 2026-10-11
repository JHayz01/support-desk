namespace SupportDesk.Desktop;

public sealed record AnswerCommand(string AnswerId, string Command);
public sealed class Settings
{
    public string Url { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public SessionTokens? Session
    {
        get; set;
    }
    public bool Remember { get; set; } = true;
    public string PanelGesture { get; set; } = "Ctrl+Alt+Space";
    public string BindListGesture { get; set; } = "Ctrl+Alt+B";
    public List<AnswerCommand> Commands { get; set; } = [];
    public SearchMatch SearchMatch { get; set; }
    public SearchScope SearchScope { get; set; }
    public HashSet<string> Favorites { get; set; } = [];
    public Dictionary<string, long> Recent { get; set; } = [];
    public bool Startup
    {
        get; set;
    }
    public bool SearchEverywhere
    {
        get; set;
    }
    public string Theme { get; set; } = "Темная";
    public double Scale { get; set; } = 1;
    public bool AlwaysOnTop { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool Maximized { get; set; }
    public int WindowLayoutVersion { get; set; }
    public string UpdateRepository { get; set; } = "";
    public bool CheckUpdates { get; set; } = true;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 800;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public byte[] WrappedKey { get; set; } = [];
    public DateTimeOffset OfflineUntil
    {
        get; set;
    }
}
public sealed class LocalStore
{
    const int CacheFormat = 2;
    sealed record CacheEnvelope(int Format, Snapshot Snapshot);
    readonly string root;
    readonly SemaphoreSlim fileGate = new(1);
    public Settings Settings
    {
        get; private set;
    }
    byte[]? key;
    static readonly JsonSerializerOptions options = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public LocalStore(string? directory = null)
    {
        root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CristalixSupportPublic");
        Directory.CreateDirectory(root);
        bool oldLocalPassword = false;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(root, "settings.bin")), null, DataProtectionScope.CurrentUser);
            Settings = JsonSerializer.Deserialize<Settings>(data, options) ?? new();
            using var legacy = JsonDocument.Parse(data);
            oldLocalPassword = legacy.RootElement.TryGetProperty("PasswordEnabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        }
        catch { Settings = new(); }
        if (oldLocalPassword)
        {
            Settings.WrappedKey = [];
            Settings.WrappedKey = [];
            var cache = Path.Combine(root, "answers.vault");
            if (File.Exists(cache)) File.Delete(cache);
        }
        if (Settings.WrappedKey.Length == 0)
        {
            key = RandomNumberGenerator.GetBytes(32);
            Settings.WrappedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            SaveSettings();
            key = null;
        }
    }
    public void SaveSettings()
    {
        lock (Settings)
        {
            var clone = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(Settings, options), options)!;
            if (!clone.Remember)
                clone.Session = null;
            byte[] bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(clone, options), null, DataProtectionScope.CurrentUser);
            Atomic(Path.Combine(root, "settings.bin"), bytes);
        }
    }
    static void Atomic(string path, byte[] data)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }
    public void Unlock()
    {
        var candidate = ProtectedData.Unprotect(Settings.WrappedKey, null, DataProtectionScope.CurrentUser);
        if (candidate.Length != 32) throw new CryptographicException();
        key = candidate;
    }
    public void Lock()
    {
        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
        key = null;
    }
    public void ResetProtection()
    {
        Lock();
        var fresh = RandomNumberGenerator.GetBytes(32);
        Settings.WrappedKey = ProtectedData.Protect(fresh, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(fresh);
        SaveSettings();
    }
    public async Task SaveCache(Snapshot snapshot)
    {
        if (key is null)
            return;
        var copy = key.ToArray();
        var data = JsonSerializer.SerializeToUtf8Bytes(new CacheEnvelope(CacheFormat, snapshot));
        await fileGate.WaitAsync();
        try
        {
            await Task.Run(() => Atomic(Path.Combine(root, "answers.vault"), Encrypt(data, copy)));
        }
        finally { CryptographicOperations.ZeroMemory(copy); fileGate.Release(); }
    }
    public Snapshot? ReadCache()
    {
        if (key is null || !File.Exists(Path.Combine(root, "answers.vault")))
            return null;
        try
        {
            var cached = JsonSerializer.Deserialize<CacheEnvelope>(Decrypt(File.ReadAllBytes(Path.Combine(root, "answers.vault")), key));
            // Older clients discarded response options while retaining the server revision.
            // A matching revision cannot establish completeness across cache formats.
            return cached?.Format == CacheFormat ? cached.Snapshot : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
    public async Task ClearCache()
    {
        await fileGate.WaitAsync();
        try
        {
            var p = Path.Combine(root, "answers.vault");
            if (File.Exists(p))
                File.Delete(p);
        }
        finally { fileGate.Release(); }
    }
    static byte[] Encrypt(byte[] data, byte[] k)
    {
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var output = new byte[data.Length + 28];
            nonce.CopyTo(output, 0);
            using var aes = new AesGcm(k, 16);
            aes.Encrypt(nonce, data, output.AsSpan(28), output.AsSpan(12, 16));
            return output;
        }
        finally { /* cache key is owned by caller */ }
    }
    static byte[] Decrypt(byte[] data, byte[] k)
    {
        if (data.Length < 28)
            throw new CryptographicException();
        var result = new byte[data.Length - 28];
        using var aes = new AesGcm(k, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result);
        return result;
    }
}
