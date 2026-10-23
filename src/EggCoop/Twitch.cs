using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

using static EggCoop.Lang;

namespace EggCoop
{
    // Twitch for viewer effects, no extra programs:
    //  - login: device code flow for a public client (no secret): the streamer types a code on twitch.tv/activate;
    //    the refresh token lives in BepInEx/config/eggcoop-twitch.txt (never in eggcoop.cfg, which people share);
    //  - channel point rewards: the mod creates one per enabled effect, keeps price/cooldown in sync with F9, pauses
    //    them while effects are off or the game is closed, and fulfills or refunds each redemption;
    //  - EventSub WebSocket: redemptions and cheers arrive here; a cheer picks the effect named in its message.
    // Channel points and bits exist only for Twitch affiliates and partners.
    public partial class Plugin
    {
        const string TwitchDefaultClientId = "oja3vofrdh6vh7ca7cpfgi6j710qur";   // the mod's public Twitch app ("Egging On Co-op")
        const string TwitchScopes = "channel:manage:redemptions bits:read";

        [Serializable] class TDevice { public string device_code, user_code, verification_uri, message; public int interval, expires_in; }
        [Serializable] class TToken { public string access_token, refresh_token, message; public int expires_in; }
        [Serializable] class TValidate { public string login, user_id; public int expires_in; }
        // Flat replies go through JsonUtility; anything nested through Json (JsonUtility leaves nested plugin types empty).

        // Words a cheer message may use to pick an effect (besides its full name).
        static readonly Dictionary<string, string[]> CheerWords = new Dictionary<string, string[]>
        {
            ["emote"] = new[] { "эмоц", "emote" }, ["confetti"] = new[] { "конфет", "confetti" }, ["wind"] = new[] { "ветер", "ветр", "wind" },
            ["hop"] = new[] { "прыг", "jump", "hop" }, ["moon"] = new[] { "луна", "лунн", "гравит", "moon" }, ["slowmo"] = new[] { "замедл", "slow" },
            ["ice"] = new[] { "скольз", "лёд", "лед", "ice" }, ["flip"] = new[] { "камер", "перевер", "flip" },
            ["tofriend"] = new[] { "к другу", "tofriend" }, ["pullfriend"] = new[] { "друга сюда", "друга к", "pullfriend" },
        };

        ConfigEntry<string> twitchClientId;
        string twAccess, twRefresh, twUserId, twLogin, twType;
        string twState = "off";          // off, code, connecting, online, error
        string twStatus = "";            // one line for F9
        TDevice twDevice;
        float twDeviceUntil;
        readonly Dictionary<string, string> rewardEffect = new Dictionary<string, string>();   // reward id -> effect id
        readonly Dictionary<string, string> effectReward = new Dictionary<string, string>();   // effect id -> reward id
        readonly ConcurrentQueue<string> wsInbox = new ConcurrentQueue<string>();
        readonly HashSet<string> wsSeen = new HashSet<string>();
        CancellationTokenSource wsStop;
        string wsSession;
        float wsLastMessage, rewardsDirtyAt = -1f, nextValidate;
        bool rewardsPausedOnTwitch;
        string TwitchClientId => string.IsNullOrEmpty(twitchClientId?.Value) ? TwitchDefaultClientId : twitchClientId.Value.Trim();
        static string TokenFile => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "eggcoop-twitch.txt");

        void InitTwitch()
        {
            twitchClientId = Config.Bind("Stream", "TwitchClientId", "", T("Свой Client ID приложения Twitch (пусто — встроенный)", "Your own Twitch app Client ID (empty: the built-in one)"));
            OnEffectDone += TwitchEffectDone;
            Application.quitting += PauseRewardsNow;
            try
            {
                if (System.IO.File.Exists(TokenFile))
                {
                    var lines = System.IO.File.ReadAllLines(TokenFile);
                    if (lines.Length > 0 && lines[0].Length > 0) { twRefresh = lines[0]; StartCoroutine(TwitchResume()); }
                }
            }
            catch (Exception e) { Log.LogWarning("Twitch token file: " + e.Message); }
        }

        void SaveToken()
        {
            try { System.IO.File.WriteAllText(TokenFile, (twRefresh ?? "") + "\n" + (twLogin ?? "") + "\n"); }
            catch (Exception e) { Log.LogWarning("Twitch token file: " + e.Message); }
        }

        // ---------------------------------------------------------------- HTTP

        IEnumerator Http(string method, string url, string body, bool helix, Action<long, string> done)
        {
            using (var req = new UnityWebRequest(url, method))
            {
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.SetRequestHeader("Content-Type", helix ? "application/json" : "application/x-www-form-urlencoded");
                }
                req.downloadHandler = new DownloadHandlerBuffer();
                if (helix) { req.SetRequestHeader("Client-Id", TwitchClientId); req.SetRequestHeader("Authorization", "Bearer " + twAccess); }
                req.timeout = 15;
                yield return req.SendWebRequest();
                long code = req.responseCode;
                string text = req.downloadHandler?.text ?? "";
                if (helix && code == 401 && twRefresh != null)
                {
                    // Access tokens live 4 h: refresh once and repeat the call.
                    bool ok = false;
                    yield return RefreshToken(r => ok = r);
                    if (ok) { yield return Http(method, url, body, true, done); yield break; }
                }
                if (code >= 400) Log.LogWarning($"Twitch {method} {url.Split('?')[0]}: {code} {text}");
                done(code, text);
            }
        }

        static string Form(params string[] kv) => string.Join("&", Enumerable.Range(0, kv.Length / 2).Select(i => kv[2 * i] + "=" + Uri.EscapeDataString(kv[2 * i + 1])));
        static string J(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---------------------------------------------------------------- login

        void TwitchConnect() { StopTwitch(false); StartCoroutine(TwitchLogin()); }

        IEnumerator TwitchLogin()
        {
            twState = "code"; twStatus = T("Запрашиваю код у Twitch…", "Asking Twitch for a code…");
            TDevice dev = null;
            yield return Http("POST", "https://id.twitch.tv/oauth2/device", Form("client_id", TwitchClientId, "scopes", TwitchScopes), false,
                (c, t) => { if (c == 200) dev = JsonUtility.FromJson<TDevice>(t); });
            if (dev == null || string.IsNullOrEmpty(dev.device_code)) { twState = "error"; twStatus = T("Twitch не выдал код входа (нет интернета?)", "Twitch gave no sign-in code (no internet?)"); yield break; }
            twDevice = dev;
            twDeviceUntil = Time.unscaledTime + dev.expires_in;
            GUIUtility.systemCopyBuffer = dev.user_code;
            Application.OpenURL(dev.verification_uri);   // the link already carries the code: just confirm in the browser
            Log.LogInfo("Twitch: waiting for the streamer to confirm the code");
            while (twDevice == dev && Time.unscaledTime < twDeviceUntil)
            {
                yield return new WaitForSecondsRealtime(Mathf.Max(dev.interval, 5));
                if (twDevice != dev) yield break;   // cancelled
                TToken tok = null; long code = 0;
                yield return Http("POST", "https://id.twitch.tv/oauth2/token", Form("client_id", TwitchClientId, "scopes", TwitchScopes, "device_code", dev.device_code,
                    "grant_type", "urn:ietf:params:oauth:grant-type:device_code"), false, (c, t) => { code = c; tok = JsonUtility.FromJson<TToken>(t); });
                if (code == 200 && tok != null && !string.IsNullOrEmpty(tok.access_token))
                {
                    twDevice = null;
                    twAccess = tok.access_token; twRefresh = tok.refresh_token;
                    yield return TwitchStart();
                    yield break;
                }
                if (tok?.message != null && tok.message != "authorization_pending") { twDevice = null; twState = "error"; twStatus = T("Вход отменён: ", "Sign-in cancelled: ") + tok.message; yield break; }
            }
            if (twDevice == dev) { twDevice = null; twState = "error"; twStatus = T("Код устарел — нажмите «Подключить Twitch» ещё раз", "The code expired: click \"Connect Twitch\" again"); }
        }

        IEnumerator RefreshToken(Action<bool> done)
        {
            TToken tok = null; long code = 0;
            yield return Http("POST", "https://id.twitch.tv/oauth2/token", Form("client_id", TwitchClientId, "grant_type", "refresh_token", "refresh_token", twRefresh), false,
                (c, t) => { code = c; tok = JsonUtility.FromJson<TToken>(t); });
            bool ok = code == 200 && tok != null && !string.IsNullOrEmpty(tok.access_token);
            if (ok) { twAccess = tok.access_token; twRefresh = tok.refresh_token; SaveToken(); }   // refresh tokens are single-use
            else { twState = "error"; twStatus = T("Вход в Twitch истёк — подключите заново", "Twitch sign-in expired: connect again"); twRefresh = null; SaveToken(); StopTwitch(false); }
            done(ok);
        }

        IEnumerator TwitchResume()
        {
            twState = "connecting"; twStatus = T("Подключаюсь к Twitch…", "Connecting to Twitch…");
            bool ok = false;
            yield return RefreshToken(r => ok = r);
            if (ok) yield return TwitchStart();
        }

        IEnumerator TwitchStart()
        {
            twState = "connecting"; twStatus = T("Проверяю аккаунт…", "Checking the account…");
            TValidate v = null;
            yield return ValidateToken(r => v = r);
            if (v == null || string.IsNullOrEmpty(v.user_id)) { twState = "error"; twStatus = T("Twitch не принял вход", "Twitch rejected the sign-in"); yield break; }
            twUserId = v.user_id; twLogin = v.login;
            object us = null;
            yield return Http("GET", "https://api.twitch.tv/helix/users?id=" + twUserId, null, true, (c, t) => { if (c == 200) us = Json.Parse(t); });
            twType = Json.Str(us, "data", 0, "broadcaster_type") ?? "";
            SaveToken();
            Log.LogInfo($"Twitch: signed in as {twLogin} ({(twType.Length > 0 ? twType : "no channel points")})");
            if (twType.Length > 0)
            {
                yield return SyncRewards();
                yield return RefundStale();
            }
            StartWs("wss://eventsub.wss.twitch.tv/ws");
        }

        IEnumerator ValidateToken(Action<TValidate> done)
        {
            TValidate v = null; long code = 0;
            using (var req = UnityWebRequest.Get("https://id.twitch.tv/oauth2/validate"))
            {
                req.SetRequestHeader("Authorization", "OAuth " + twAccess);
                yield return req.SendWebRequest();
                code = req.responseCode;
                if (code == 200) v = JsonUtility.FromJson<TValidate>(req.downloadHandler.text);
            }
            if (code == 401 && twRefresh != null)
            {
                bool ok = false;
                yield return RefreshToken(r => ok = r);
                if (ok) { yield return ValidateToken(done); yield break; }
            }
            nextValidate = Time.unscaledTime + 3600f;   // Twitch asks apps to validate tokens hourly
            done(v);
        }

        void TwitchDisconnect()
        {
            PauseRewardsNow();
            if (twAccess != null) StartCoroutine(Http("POST", "https://id.twitch.tv/oauth2/revoke", Form("client_id", TwitchClientId, "token", twAccess), false, (c, t) => { }));
            twRefresh = null; twAccess = null; twLogin = null; twUserId = null;
            SaveToken();
            StopTwitch(true);
            twState = "off"; twStatus = "";
        }

        void StopTwitch(bool clearRewards)
        {
            twDevice = null;
            wsStop?.Cancel(); wsStop = null; wsSession = null;
            if (clearRewards) { rewardEffect.Clear(); effectReward.Clear(); }
        }

        // ---------------------------------------------------------------- rewards

        // Our rewards (Twitch lets an app edit only the ones it created) follow F9: one per effect sold for points.
        IEnumerator SyncRewards()
        {
            if (twUserId == null || twType.Length == 0) yield break;
            object have = null;
            yield return Http("GET", $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={twUserId}&only_manageable_rewards=true", null, true,
                (c, t) => { if (c == 200) have = Json.Parse(t); });
            if (have == null) { twStatus = T("Не удалось прочитать награды канала", "Could not read the channel rewards"); yield break; }
            var ours = Json.List(have, "data");
            rewardEffect.Clear(); effectReward.Clear();
            bool live = streamEffects.Value && !streamPaused;
            int made = 0, failed = 0;
            foreach (var d in Effects)
            {
                var c = effCfg[d.Id];
                bool want = streamEffects.Value && c.On.Value && c.Points.Value > 0;
                var r = ours.FirstOrDefault(x => Json.Str(x, "title") == d.Ru || Json.Str(x, "title") == d.En);
                int cd = c.Cooldown.Value;
                string fields = $"\"title\":{J(d.Name)},\"cost\":{Math.Max(1, c.Points.Value)},\"prompt\":{J(d.Desc)},\"is_enabled\":{(want ? "true" : "false")},\"is_paused\":{(live ? "false" : "true")}," +
                                $"\"is_global_cooldown_enabled\":{(cd > 0 ? "true" : "false")},\"global_cooldown_seconds\":{Math.Max(1, cd)}";
                if (r == null && !want) continue;
                string id = Json.Str(r, "id");
                if (r == null)
                {
                    yield return Http("POST", $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={twUserId}",
                        "{\"background_color\":\"#FFA727\"," + fields + "}", true,
                        (code, t) => { if (code == 200) { id = Json.Str(Json.Parse(t), "data", 0, "id"); made++; } else failed++; });
                }
                else
                {
                    yield return Http("PATCH", $"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={twUserId}&id={id}", "{" + fields + "}", true,
                        (code, t) => { if (code != 200) failed++; });
                }
                if (id != null) { rewardEffect[id] = d.Id; effectReward[d.Id] = id; }
            }
            rewardsPausedOnTwitch = !live;
            twStatus = failed > 0 ? T($"Награды: {failed} не удалось обновить (журнал BepInEx)", $"Rewards: {failed} failed to update (BepInEx log)") : T($"Награды на канале: {effectReward.Count}", $"Channel rewards: {effectReward.Count}") + (made > 0 ? T($" (новых {made})", $" ({made} new)") : "") + (live ? "" : T(", на паузе", ", paused"));
            Log.LogInfo("Twitch: " + twStatus);
        }

        // Redemptions left waiting from a crash or an earlier session: give the points back.
        IEnumerator RefundStale()
        {
            foreach (var kv in effectReward.ToList())
            {
                object list = null;
                yield return Http("GET", $"https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions?broadcaster_id={twUserId}&reward_id={kv.Value}&status=UNFULFILLED&first=50", null, true,
                    (c, t) => { if (c == 200) list = Json.Parse(t); });
                foreach (var r in Json.List(list, "data")) yield return SetRedemption(kv.Value, Json.Str(r, "id"), false);
            }
        }

        IEnumerator SetRedemption(string rewardId, string redemptionId, bool fulfilled) =>
            Http("PATCH", $"https://api.twitch.tv/helix/channel_points/custom_rewards/redemptions?broadcaster_id={twUserId}&reward_id={rewardId}&id={redemptionId}",
                "{\"status\":\"" + (fulfilled ? "FULFILLED" : "CANCELED") + "\"}", true, (c, t) => { });

        // On quit (no coroutines any more): pause our rewards with plain blocking requests, a couple of seconds at most.
        void PauseRewardsNow()
        {
            if (twAccess == null || twUserId == null || effectReward.Count == 0) return;
            foreach (var id in effectReward.Values)
            {
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create($"https://api.twitch.tv/helix/channel_points/custom_rewards?broadcaster_id={twUserId}&id={id}");
                    req.Method = "PATCH"; req.Timeout = 1500; req.ContentType = "application/json";
                    req.Headers["Client-Id"] = TwitchClientId; req.Headers["Authorization"] = "Bearer " + twAccess;
                    var b = Encoding.UTF8.GetBytes("{\"is_paused\":true}");
                    using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                    req.GetResponse().Close();
                }
                catch (Exception e) { Log.LogWarning("Twitch pause on quit: " + e.Message); }
            }
            rewardsPausedOnTwitch = true;
        }

        void TwitchEffectDone(StreamQueue.Req r, bool ran)
        {
            if (r.Source == "points" && r.Tag is string[] ids)
            {
                if (twAccess == null) Log.LogInfo($"Twitch: would mark redemption {ids[1]} {(ran ? "FULFILLED" : "CANCELED")} (not signed in)");
                else StartCoroutine(SetRedemption(ids[0], ids[1], ran));
            }
            else if (r.Source == "bits" && !ran) Toast(T($"{r.Viewer}: «{Def(r.Effect).Name}» не удалось запустить (bits не возвращаются)", $"{r.Viewer}: \"{Def(r.Effect).Name}\" could not run (bits are not refunded)"));
        }

        // ---------------------------------------------------------------- EventSub

        void StartWs(string url)
        {
            var stop = new CancellationTokenSource();
            var old = wsStop;
            wsStop = stop;
            twState = "connecting"; twStatus = T("Подключаюсь к событиям Twitch…", "Connecting to Twitch events…");
            Task.Run(() => WsLoop(url, stop.Token, old));
        }

        // Background thread: read whole text messages into the inbox; the main thread does the rest.
        async Task WsLoop(string url, CancellationToken ct, CancellationTokenSource previous)
        {
            try
            {
                using (var ws = new ClientWebSocket())
                {
                    Log.LogInfo("Twitch events: connecting " + url.Split('?')[0]);
                    await ws.ConnectAsync(new Uri(url), ct);
                    Log.LogInfo("Twitch events: socket " + ws.State);
                    var buf = new byte[64 * 1024];
                    var sb = new StringBuilder();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var seg = new ArraySegment<byte>(buf);
                        var res = await ws.ReceiveAsync(seg, ct);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                        if (!res.EndOfMessage) continue;
                        var text = sb.ToString(); sb.Clear();
                        wsInbox.Enqueue(text);
                        // A reconnect hands over to the new socket once it says welcome; then the old one goes.
                        if (previous != null && text.Contains("\"session_welcome\"")) { previous.Cancel(); previous = null; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { wsInbox.Enqueue("{\"metadata\":{\"message_type\":\"x_error\",\"message_id\":" + J(e.Message) + "}}"); }
            if (!ct.IsCancellationRequested) wsInbox.Enqueue("{\"metadata\":{\"message_type\":\"x_closed\"}}");
        }

        void TwitchTick()
        {
            float now = Time.unscaledTime;
            while (wsInbox.TryDequeue(out var text))
            {
                try { HandleWs(text); } catch (Exception e) { Warn("twitch message", e); }
            }
            if (twAccess == null) return;
            // Keepalives come every ~10 s; silence for long means a dead socket: start again.
            if (wsSession != null && now - wsLastMessage > 40f) { Log.LogInfo("Twitch: events silent, reconnecting"); wsSession = null; StartWs("wss://eventsub.wss.twitch.tv/ws"); }
            if (twState == "online" && now > nextValidate) StartCoroutine(ValidateToken(v => { if (v == null) twStatus = T("Вход в Twitch надо обновить", "Twitch sign-in needs a refresh"); }));
            // Rewards follow F9: price edits settle for 3 s, pause/unpause right away.
            bool live = streamEffects.Value && !streamPaused;
            if (twType?.Length > 0 && twState == "online" && (live == rewardsPausedOnTwitch || (rewardsDirtyAt > 0 && now - rewardsDirtyAt > 3f)))
            {
                rewardsDirtyAt = -1f; rewardsPausedOnTwitch = !live;
                StartCoroutine(SyncRewards());
            }
        }

        void HandleWs(string text)
        {
            var m = Json.Parse(text);
            var type = Json.Str(m, "metadata", "message_type");
#if LAB
            if (type != "session_keepalive") Log.LogInfo($"Lab twitch: msg {type} sub={Json.Str(m, "metadata", "subscription_type")}");
#endif
            wsLastMessage = Time.unscaledTime;
            switch (type)
            {
                case "session_welcome":
                    wsSession = Json.Str(m, "payload", "session", "id");
                    if (twAccess != null) StartCoroutine(Subscribe());
                    else Log.LogInfo("Twitch: events socket open (no login), session " + wsSession);
                    break;
                case "session_reconnect":
                    StartWs(Json.Str(m, "payload", "session", "reconnect_url"));
                    break;
                case "notification":
                    if (!wsSeen.Add(Json.Str(m, "metadata", "message_id") ?? "")) break;   // Twitch may deliver the same event twice
                    if (wsSeen.Count > 500) wsSeen.Clear();
                    OnTwitchEvent(Json.Str(m, "metadata", "subscription_type") ?? Json.Str(m, "payload", "subscription", "type"), Json.Get(m, "payload", "event"));
                    break;
                case "revocation":
                    twStatus = T("Twitch отозвал подписку: ", "Twitch revoked the subscription: ") + Json.Str(m, "payload", "subscription", "status");
                    break;
                case "x_error":
                    Log.LogWarning("Twitch events: " + Json.Str(m, "metadata", "message_id"));
                    goto case "x_closed";
                case "x_closed":
                    wsSession = null;
                    if (twAccess != null) { twState = "connecting"; twStatus = T("Связь с Twitch прервалась — переподключаюсь…", "Lost Twitch: reconnecting…"); StartCoroutine(ReconnectLater()); }
                    break;
            }
        }

        IEnumerator ReconnectLater()
        {
            yield return new WaitForSecondsRealtime(5f);
            if (twAccess != null && wsSession == null) StartWs("wss://eventsub.wss.twitch.tv/ws");
        }

        IEnumerator Subscribe()
        {
            int ok = 0, want = 0;
            var types = new List<string> { "channel.cheer" };
            if (twType.Length > 0) types.Add("channel.channel_points_custom_reward_redemption.add");
            foreach (var t in types)
            {
                want++;
                string body = "{\"type\":" + J(t) + ",\"version\":\"1\",\"condition\":{\"broadcaster_user_id\":" + J(twUserId) + "},\"transport\":{\"method\":\"websocket\",\"session_id\":" + J(wsSession) + "}}";
                yield return Http("POST", "https://api.twitch.tv/helix/eventsub/subscriptions", body, true, (c, x) => { if (c == 202) ok++; });
            }
            twState = ok == want ? "online" : "error";
            twStatus = ok == want ? T($"Twitch: {twLogin} · события подключены", $"Twitch: {twLogin} · events connected") + (twType.Length == 0 ? T(" · баллов канала нет (нужен статус компаньона)", " · no channel points (needs Affiliate status)") : "")
                                  : T($"Twitch: подписка на события не удалась ({ok}/{want}), см. журнал BepInEx", $"Twitch: event subscription failed ({ok}/{want}), see the BepInEx log");
            Log.LogInfo(twStatus);
        }

        void OnTwitchEvent(string type, object e)
        {
            if (e == null) return;
            string viewer = Json.Bool(e, "is_anonymous") ? T("Аноним", "Anonymous") : Json.Str(e, "user_name") ?? Json.Str(e, "user_login") ?? T("Зритель", "Viewer");
            if (type == "channel.channel_points_custom_reward_redemption.add")
            {
                string rewardId = Json.Str(e, "reward", "id"), redemptionId = Json.Str(e, "id");
                if (rewardId == null || !rewardEffect.TryGetValue(rewardId, out var effect)) return;   // not one of ours
                if (!RequestEffect(effect, viewer, "points", new[] { rewardId, redemptionId })) StartCoroutine(SetRedemption(rewardId, redemptionId, false));
            }
            else if (type == "channel.cheer")
            {
                int bits = Json.Int(e, "bits");
                string effect = CheerEffect(Json.Str(e, "message"), bits);
                if (effect == null) { Log.LogInfo($"Twitch: {viewer} cheered {bits}: no enabled effect named that they cover"); return; }
                RequestEffect(effect, viewer, "bits");
            }
        }

        // The priciest enabled effect named in the cheer message that the bits cover.
        string CheerEffect(string message, int bits)
        {
            string msg = (message ?? "").ToLowerInvariant();
            return Effects.Where(d => effCfg[d.Id].On.Value && effCfg[d.Id].Bits.Value > 0 && bits >= effCfg[d.Id].Bits.Value
                                      && (msg.Contains(d.Ru.ToLowerInvariant()) || msg.Contains(d.En.ToLowerInvariant()) || CheerWords[d.Id].Any(w => msg.Contains(w))))
                          .OrderByDescending(d => effCfg[d.Id].Bits.Value).Select(d => d.Id).FirstOrDefault();
        }

        // ---------------------------------------------------------------- F9

        void DrawTwitchRow()
        {
            GUILayout.BeginHorizontal();
            switch (twState)
            {
                case "off":
                case "error":
                    GUILayout.Label(twState == "error" ? twStatus : T("Twitch не подключён", "Twitch not connected"), twState == "error" ? sText : sDim);
                    if (GUILayout.Button(T("Подключить Twitch", "Connect Twitch"), sBtn, GUILayout.Width(180))) TwitchConnect();
                    break;
                case "code":
                    if (twDevice == null) GUILayout.Label(twStatus, sDim);
                    else if (Streamer) GUILayout.Label(T("Браузер открыт: подтвердите вход на Twitch (код скрыт режимом стримера и уже скопирован)", "The browser is open: confirm the sign-in on Twitch (the code is hidden by streamer mode and already copied)"), sText);
                    else GUILayout.Label(T($"Откройте twitch.tv/activate и введите код <color=#FFA727><b>{twDevice.user_code}</b></color> (уже скопирован, браузер открыт)", $"Open twitch.tv/activate and enter the code <color=#FFA727><b>{twDevice.user_code}</b></color> (already copied, the browser is open)"), sText);
                    if (GUILayout.Button(T("Отмена", "Cancel"), sBtn, GUILayout.Width(100))) { twDevice = null; twState = "off"; }
                    break;
                default:
                    GUILayout.Label(twStatus, sText);
                    if (GUILayout.Button(T("Отключить", "Disconnect"), sBtn, GUILayout.Width(120))) TwitchDisconnect();
                    break;
            }
            GUILayout.EndHorizontal();
            if (twState == "online" && twType?.Length > 0 && GUILayout.Button(T("Обновить награды на канале", "Update the channel rewards"), sBtn)) StartCoroutine(SyncRewards());
        }

#if LAB
        // -coop-twitchtest: the events socket without login (TLS + WebSocket from inside the game), a device code
        // request (HTTPS POST + JSON, no browser), and fake notifications through the real handler.
        IEnumerator TwitchLabTest()
        {
            StartWs("wss://eventsub.wss.twitch.tv/ws");
            TDevice dev = null; long code = 0;
            yield return Http("POST", "https://id.twitch.tv/oauth2/device", Form("client_id", TwitchClientId, "scopes", TwitchScopes), false, (c, x) => { code = c; dev = JsonUtility.FromJson<TDevice>(x); });
            Log.LogInfo($"Lab twitch: device code request {code}, user_code={(dev?.user_code != null ? dev.user_code.Substring(0, 2) + "…" : "-")} uri={dev?.verification_uri?.Split('?')[0]} interval={dev?.interval}");
            yield return new WaitForSecondsRealtime(4f);
            streamEffects.Value = true; streamGap.Value = 3;
            rewardEffect["fake-reward"] = "wind"; effectReward["wind"] = "fake-reward";
            string Note(string id, string type, string ev) => "{\"metadata\":{\"message_id\":\"" + id + "\",\"message_type\":\"notification\",\"subscription_type\":\"" + type + "\"},\"payload\":{\"subscription\":{\"type\":\"" + type + "\"},\"event\":" + ev + "}}";
            HandleWs(Note("m1", "channel.channel_points_custom_reward_redemption.add", "{\"id\":\"red-1\",\"user_name\":\"Viewer_Points\",\"user_login\":\"viewer_points\",\"reward\":{\"id\":\"fake-reward\",\"title\":\"Порыв ветра\"}}"));
            HandleWs(Note("m1", "channel.channel_points_custom_reward_redemption.add", "{\"id\":\"red-1\",\"user_name\":\"Viewer_Points\",\"reward\":{\"id\":\"fake-reward\"}}"));   // duplicate
            HandleWs(Note("m2", "channel.cheer", "{\"user_name\":\"Viewer_Bits\",\"bits\":200,\"message\":\"Cheer200 ЛУННАЯ гравитация!\",\"is_anonymous\":false}"));
            HandleWs(Note("m3", "channel.cheer", "{\"user_name\":\"Viewer_Tip\",\"bits\":500,\"message\":\"просто спасибо\",\"is_anonymous\":false}"));
            HandleWs(Note("m4", "channel.cheer", "{\"bits\":20,\"message\":\"ветер\",\"is_anonymous\":true}"));   // too cheap for wind (50)
            Log.LogInfo($"Lab twitch: queue {streamQ.Count}: {string.Join(", ", streamQ.Pending.Select(q => $"{q.Effect}/{q.Viewer}/{q.Source}"))}");
        }
#endif

        // F9 changed a price, cooldown or switch: push it to Twitch a moment later.
        void MarkRewardsDirty() => rewardsDirtyAt = Time.unscaledTime;
    }
}
