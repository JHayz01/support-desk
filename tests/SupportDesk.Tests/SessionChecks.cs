using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using SupportDesk.Core;
using SupportDesk.Desktop;

internal static class SessionChecks
{
    public static async Task Run(string root)
    {
        var store = new LocalStore(Path.Combine(root, "session-recovery"));
        store.Settings.Url = "https://api.example.test";
        store.Settings.Session = new SessionTokens("expired-on-server", "refresh", DateTimeOffset.UtcNow.AddMinutes(1));
        using var handler = new SessionHandler();
        using var client = new ApiClient(store, handler);

        var results = await Task.WhenAll(
            client.Request<int>(HttpMethod.Get, "/v2/changes"),
            client.Request<int>(HttpMethod.Get, "/v2/changes"));
        if (results[0] != 42 || results[1] != 42 || handler.Refreshes != 1 ||
            store.Settings.Session.AccessToken != "renewed")
            throw new Exception("Concurrent rejected tokens must share one session refresh and retry successfully.");

        handler.RejectRefresh = true;
        store.Settings.Session = new SessionTokens("expired-on-server", "revoked", DateTimeOffset.UtcNow.AddMinutes(1));
        try
        {
            await client.Request<int>(HttpMethod.Get, "/v2/changes");
            throw new Exception("Revoked sessions must require a new login.");
        }
        catch (AccessDeniedException) { }
        if (handler.Refreshes != 2)
            throw new Exception("A rejected refresh must not loop.");

        Console.WriteLine("PASS: expired access token recovery, concurrent refresh and revoked session");
    }

    sealed class SessionHandler : HttpMessageHandler
    {
        readonly TaskCompletionSource staleRequestsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int staleRequests;
        public int Refreshes { get; private set; }
        public bool RejectRefresh { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v2/auth/refresh")
            {
                Refreshes++;
                return RejectRefresh
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SessionTokens("renewed", "next-refresh", DateTimeOffset.UtcNow.AddMinutes(10)))
                    };
            }

            if (request.Headers.Authorization?.Parameter == "renewed")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(42) };

            if (Interlocked.Increment(ref staleRequests) >= 2)
                staleRequestsReady.TrySetResult();
            await staleRequestsReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
    }
}
