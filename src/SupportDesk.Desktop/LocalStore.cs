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
        Settings = new();
        var settingsPath = Path.Combine(root, "settings.bin");
        var candidates = new[] { settingsPath, settingsPath + ".bak" }
            .Concat(Directory.GetFiles(root, "settings.bin.*.tmp").OrderByDescending(File.GetLastWriteTimeUtc));
        foreach (var candidate in candidates)
        {
            if (!TryReadSettings(candidate, out var loaded, out oldLocalPassword)) continue;
            Settings = loaded!;
            // Repair the primary from a validated copy before the next save rotates the backup.
            if (candidate != settingsPath)
            {
                PreserveDamagedSettings(settingsPath);
                Atomic(settingsPath, File.ReadAllBytes(candidate));
            }
            break;
        }
        if (oldLocalPassword)
        {
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
            var path = Path.Combine(root, "settings.bin");
            if (File.Exists(path) && !TryReadSettings(path, out _, out _))
                PreserveDamagedSettings(path);
            Atomic(path, bytes, backup: true);
        }
    }
    static bool TryReadSettings(string path, out Settings? settings, out bool legacyPassword)
    {
        settings = null;
        legacyPassword = false;
        try
        {
            var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize<Settings>(data, options);
            if (loaded is null || loaded.Commands is null || loaded.Favorites is null || loaded.Recent is null || loaded.WrappedKey is null)
                return false;
            using var legacy = JsonDocument.Parse(data);
            legacyPassword = legacy.RootElement.TryGetProperty("PasswordEnabled", out var flag) && flag.ValueKind == JsonValueKind.True;
            settings = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    static void PreserveDamagedSettings(string path)
    {
        if (File.Exists(path))
            File.Move(path, path + ".damaged." + Guid.NewGuid().ToString("N"));
    }
    static void Atomic(string path, byte[] data, bool backup = false)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Flush file data before publishing its name; rename alone does not flush write caches.
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        if (backup && File.Exists(path))
            File.Replace(temp, path, path + ".bak");
        else
            File.Move(temp, path, true);
        // The first save also needs a recovery copy, not just subsequent replacements.
        if (backup && !File.Exists(path + ".bak"))
            Atomic(path + ".bak", data);
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
