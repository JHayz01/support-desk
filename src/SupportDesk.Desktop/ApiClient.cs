using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
namespace SupportDesk.Desktop;

public sealed class AccessDeniedException : Exception;
public sealed class ApiClient : IDisposable
{
    readonly LocalStore local;
    readonly HttpClient http;
    readonly SemaphoreSlim sessionGate = new(1);
    readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    public ApiClient(LocalStore store) : this(store, CreateHandler(store)) { }

    internal ApiClient(LocalStore store, HttpMessageHandler handler)
    {
        local = store;
        var uri = new Uri(store.Settings.Url);
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new ArgumentException("Нужен адрес HTTPS.");
        http = new(handler)
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(50)
        };
    }
    static HttpClientHandler CreateHandler(LocalStore store) => new()
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
        {
            var now = DateTime.Now;
            if (cert is null || now < cert.NotBefore || now > cert.NotAfter)
                return false;
            var pin = store.Settings.CertificateSha256;
            return pin.Length == 0
                ? errors == SslPolicyErrors.None
                : Convert.ToHexString(SHA256.HashData(cert.RawData)).Equals(pin.Replace(":", ""), StringComparison.OrdinalIgnoreCase);
        }
    };

    async Task<T> Raw<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (token is not null)
            req.Headers.Authorization = new("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new AccessDeniedException();
        if (res.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("Ответ уже изменен другим сотрудником. Синхронизируйте базу и откройте редактор заново.");
        if ((int)res.StatusCode == 429)
            throw new InvalidOperationException("Слишком много запросов. Подождите минуту.");
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException("Сервер недоступен или отклонил запрос (" + (int)res.StatusCode + ").");
        return (await res.Content.ReadFromJsonAsync<T>(json, ct))!;
    }
    public async Task Login(string password, bool remember, CancellationToken ct)
    {
        local.Settings.Session = await Raw<SessionTokens>(HttpMethod.Post, "/v2/auth/login", new LoginRequest(password, Environment.MachineName), null, ct);
        local.Settings.Remember = remember;
        local.Settings.OfflineUntil = DateTimeOffset.UtcNow.AddDays(1);
        local.SaveSettings();
    }
    public async Task<T> Request<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        var token = await GetAccessToken(ct);
        try
        {
            return await Raw<T>(method, path, body, token, ct);
        }
        catch (AccessDeniedException)
        {
            // The API rejects unauthorized requests before executing their handlers.
            var renewedToken = await GetAccessToken(ct, token);
            return await Raw<T>(method, path, body, renewedToken, ct);
        }
    }

    async Task<string> GetAccessToken(CancellationToken ct, string? rejectedToken = null)
    {
        await sessionGate.WaitAsync(ct);
        try
        {
            var session = local.Settings.Session ?? throw new AccessDeniedException();
            bool needsRefresh = rejectedToken is null
                ? session.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30)
                : session.AccessToken == rejectedToken;
            if (needsRefresh)
            {
                session = await Raw<SessionTokens>(HttpMethod.Post, "/v2/auth/refresh", new RefreshRequest(session.RefreshToken), null, ct);
                local.Settings.Session = session;
                local.SaveSettings();
            }
            return session.AccessToken;
        }
        finally { sessionGate.Release(); }
    }
    public async Task Logout()
    {
        try
        {
            await Request<JsonElement>(HttpMethod.Post, "/v2/auth/logout");
        }
        catch { }
        local.Settings.Session = null;
        local.Settings.OfflineUntil = default;
        local.SaveSettings();
        await local.ClearCache();
    }
    public void Dispose()
    {
        http.Dispose();
        sessionGate.Dispose();
    }
}
