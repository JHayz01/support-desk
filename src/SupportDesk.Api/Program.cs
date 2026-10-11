using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using SupportDesk.Api;
using SupportDesk.Core;

var home = Environment.GetEnvironmentVariable("SUPPORT_HOME") ?? AppContext.BaseDirectory;
var db = Environment.GetEnvironmentVariable("SUPPORT_POSTGRES") ?? Environment.GetEnvironmentVariable("SUPPORT_DB") ?? Path.Combine(home, "data", "answers.sqlite3");
var passwordFile = Path.Combine(home, "password.json");
if (args.Contains("--init-db")) { _ = new Store(db); Console.WriteLine("Database schema ready."); return; }
if (args.Contains("--set-password"))
{
    Directory.CreateDirectory(home);
    var value = Console.ReadLine() ?? "";
    File.WriteAllText(passwordFile, JsonSerializer.Serialize(Auth.CreatePassword(value)));
    var storage = new Store(db);
    using var c = storage.Open();
    Store.Run(c, "DELETE FROM sessions");
    Console.WriteLine("Password hash saved; sessions revoked.");
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 1000000; if (Environment.GetEnvironmentVariable("SUPPORT_CERT") is string cert) { var key = Environment.GetEnvironmentVariable("SUPPORT_KEY")!; var certificate = X509Certificate2.CreateFromPemFile(cert, key); if (OperatingSystem.IsWindows()) certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null); k.Listen(System.Net.IPAddress.Parse(Environment.GetEnvironmentVariable("SUPPORT_BIND") ?? "0.0.0.0"), int.Parse(Environment.GetEnvironmentVariable("SUPPORT_PORT") ?? "8444"), o => o.UseHttps(certificate)); } });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);
var store = new Store(db);
var password = JsonSerializer.Deserialize<PasswordRecord>(File.ReadAllText(passwordFile))!;
var auth = new Auth(store, password);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(auth);
var semantic = new Semantic(store, Environment.GetEnvironmentVariable("SUPPORT_EMBED_URL") ?? "http://127.0.0.1:8766");
builder.Services.AddSingleton(semantic);
builder.Services.AddHostedService(_ => semantic);
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); o.AddPolicy("search", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new() { PermitLimit = 40, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
var app = builder.Build();
app.Use(async (ctx, next) => { try { await next(); } catch (ConflictException) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { Error = "Ответ уже изменен. Обновите его и повторите действие." }); } catch (ArgumentException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { Error = "Некорректные данные." }); } catch (Exception) when (!ctx.RequestAborted.IsCancellationRequested) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { Error = "Сервис временно недоступен." }); } });
app.UseRateLimiter();
app.Use(async (ctx, next) => { string path = ctx.Request.Path.Value ?? ""; if (path.StartsWith("/v2/") && path is not ("/v2/auth/login" or "/v2/auth/refresh")) { var header = ctx.Request.Headers.Authorization.ToString(); if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !auth.Authorized(header[7..])) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { Error = "Войдите с паролем поддержки." }); return; } } await next(); });
app.MapGet("/health", () => new { Status = "ok", Version = "7" });
app.MapPost("/v2/auth/login", async (LoginRequest req) => { if (req.Password is null || req.Device is null) return Results.BadRequest(); return await Task.Run(() => auth.Check(req.Password)) ? Results.Ok(auth.Login(req.Device)) : Results.Json(new { Error = "Неверный пароль поддержки." }, statusCode: 401); }).RequireRateLimiting("login");
app.MapPost("/v2/auth/refresh", (RefreshRequest req) => auth.Refresh(req.RefreshToken) is SessionTokens t ? Results.Ok(t) : Results.Unauthorized()).RequireRateLimiting("login");
app.MapPost("/v2/auth/logout", (HttpContext c) => { auth.Logout(c.Request.Headers.Authorization.ToString()[7..]); return Results.Ok(new { Ok = true }); });
app.MapGet("/v2/answers", () => store.Snapshot());
app.MapGet("/v2/changes", (long since) => store.Since(since));
app.MapPut("/v2/answers/{id}", (string id, ChangeRequest req) => { store.Change(id, req, false); return Results.Ok(new { Ok = true }); });
app.MapDelete("/v2/answers/{id}", (string id, [FromBody] ChangeRequest req) => { store.Change(id, req, true); return Results.Ok(new { Ok = true }); });
app.MapPost("/v2/search", async (SearchRequest req, HttpContext c) => Results.Ok(await semantic.Search(req.Query, req.Limit, c.RequestAborted))).RequireRateLimiting("search");
app.Run();
