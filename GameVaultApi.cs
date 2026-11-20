using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameVaultRemote
{
    public class GvPage { public List<GvGame> data { get; set; } = new List<GvGame>(); }
    public class GvMedia { public int id { get; set; } }
    public class GvMeta { public string title { get; set; } public string launch_executable { get; set; } public string installer_executable { get; set; } public GvMedia cover { get; set; } }
    public class GvVersion { public int id { get; set; } public string type { get; set; } public string version { get; set; } public string size { get; set; } }
    public class GvGame
    {
        public int id { get; set; }
        public string title { get; set; }
        public string version { get; set; }
        public string type { get; set; } // legacy mirror of the selected version's type; used if versions aren't in the list response
        public GvMeta metadata { get; set; }
        public List<GvVersion> versions { get; set; } = new List<GvVersion>();
    }
    public class GvToken { public string access_token { get; set; } public string refresh_token { get; set; } }

    public class GameVaultApi
    {
        // No timeout: downloads are many GB. Cancellation comes from the token instead.
        private static readonly HttpClient http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // A Func, not the object: Playnite swaps the Settings instance on CancelEdit.
        private readonly Func<Settings> getSettings;
        private readonly Action save;
        private Settings s => getSettings();

        public GameVaultApi(Func<Settings> settings, Action saveSettings) { getSettings = settings; save = saveSettings; }

        private string Url(string path) => s.BaseUrl + "/api/" + path;

        // Access tokens live 5 min. Cache one; renew via the SSO refresh token if we have one, else basic login.
        private string token; private DateTime tokenExpiry;

        private async Task<HttpRequestMessage> Authed(string path, CancellationToken ct)
        {
            if (token == null || DateTime.UtcNow >= tokenExpiry) await RenewAsync(ct);
            var req = new HttpRequestMessage(HttpMethod.Get, Url(path));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        public void SetTokens(GvToken t)
        {
            token = t.access_token;
            tokenExpiry = DateTime.UtcNow.AddMinutes(4); // server TTL is 5 min
            if (!string.IsNullOrEmpty(t.refresh_token)) { s.RefreshToken = t.refresh_token; save(); }
        }

        private async Task RenewAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(s.RefreshToken))
            {
                var rq = new HttpRequestMessage(HttpMethod.Post, Url("auth/refresh"));
                rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.RefreshToken);
                using (var r = await http.SendAsync(rq, ct))
                {
                    // Refresh tokens rotate, so SetTokens must persist the new one.
                    if (r.IsSuccessStatusCode) { SetTokens(Serialization.FromJson<GvToken>(await r.Content.ReadAsStringAsync())); return; }
                }
                if (string.IsNullOrEmpty(s.Username))
                    throw new Exception("SSO session expired. Open the plugin settings and click Authenticate.");
            }
            var login = new HttpRequestMessage(HttpMethod.Get, Url("auth/basic/login"));
            login.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Username + ":" + s.Password)));
            using (var r = await http.SendAsync(login, ct))
            {
                if (!r.IsSuccessStatusCode) throw new Exception($"Login failed: {(int)r.StatusCode} {r.ReasonPhrase}");
                SetTokens(Serialization.FromJson<GvToken>(await r.Content.ReadAsStringAsync()));
            }
        }

        public async Task<List<GvGame>> GetGamesAsync(CancellationToken ct)
        {
            // limit=-1: the server's paginate config sets maxLimit -1, i.e. return everything.
            using (var r = await http.SendAsync(await Authed("games?limit=-1", ct), ct))
            {
                r.EnsureSuccessStatusCode();
                return Serialization.FromJson<GvPage>(await r.Content.ReadAsStringAsync()).data;
            }
        }

        public async Task<GvGame> GetGameAsync(string id, CancellationToken ct)
        {
            using (var r = await http.SendAsync(await Authed("games/" + id, ct), ct))
            {
                r.EnsureSuccessStatusCode();
                return Serialization.FromJson<GvGame>(await r.Content.ReadAsStringAsync());
            }
        }

        public async Task DownloadMediaAsync(int mediaId, string destFile, CancellationToken ct)
        {
            using (var r = await http.SendAsync(await Authed("media/" + mediaId, ct), ct))
            {
                r.EnsureSuccessStatusCode();
                File.WriteAllBytes(destFile, await r.Content.ReadAsByteArrayAsync());
            }
        }

        public async Task DownloadAsync(string gameId, int versionId, string destFile, Action<double> progress, CancellationToken ct)
        {
            using (var r = await http.SendAsync(await Authed($"game/{gameId}/versions/{versionId}", ct), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                var total = r.Content.Headers.ContentLength ?? 0;
                using (var src = await r.Content.ReadAsStreamAsync())
                using (var dst = File.Create(destFile))
                {
                    var buf = new byte[81920];
                    long done = 0; int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct);
                        done += n;
                        if (total > 0) progress(done / (double)total);
                    }
                }
            }
        }
    }
}
