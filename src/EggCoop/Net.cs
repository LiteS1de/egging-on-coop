using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Layers;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Networking;

namespace EggCoop
{
    public struct EggState
    {
        public Vector3 pos;
        public Quaternion rot;
        public bool yolk;
        public int skin;
        public string nick;
        public byte tpSeq;      // bumps on every teleport: receivers snap instead of sweeping and pause collisions
        public ushort tpTotal;  // shown on everyone's scoreboard
    }

    // Star topology: guests talk only to the host, the host relays every guest's state to the others.
    // Connection setup follows sa-coop: UPnP -> STUN -> invite code -> UDP hole punch via ntfy -> (VPN / direct IP fallback).
    public class CoopNet : INetEventListener
    {
        public const string ConnectKey = "EggCoop/0.4";   // also a version check: different mod versions refuse each other
        public const int MaxPlayers = 4;
        const byte MsgState = 1, MsgLeave = 2, MsgSettings = 3, MsgWelcome = 4, MsgWorld = 5, MsgClock = 6;
        static readonly byte[] Punch = Encoding.ASCII.GetBytes("EGGPUNCH");
        static readonly byte[] Discover = Encoding.ASCII.GetBytes("EGGDISC");
        static readonly byte[] Here = Encoding.ASCII.GetBytes("EGGHERE");

        public bool IsHost { get; private set; }
        public bool Active => manager != null;
        public string Code { get; private set; }
        public bool VpnSuspected { get; private set; }   // STUN and the router disagree on our public IP: replies may leave through a VPN
        public int PlayerCount => IsHost ? manager.ConnectedPeersCount + 1 : (hostPeer != null ? remoteIds.Count + 1 : 0);
        public event Action<int, EggState> OnState;
        public event Action<int> OnLeave;
        public event Action<bool, bool> OnSettings;
        public event Action<string> OnEnded;
        public event Action<int, NetPacketReader> OnWorld;   // (sender player id, payload) for WorldSync
        public int MyId => IsHost ? 0 : myId;               // host is 0, guests peer.Id + 1 (told by the host)
        public bool Connected => manager != null && (IsHost || (hostPeer != null && myId > 0));
        int myId;
        float nextClock;
        // Guest: the host vanished without saying goodbye (Wi-Fi hiccup): keep trying the same address for a while.
        const float ReconnectWindow = 30f;
        IPEndPoint lastHost;
        float reconnectUntil, nextReconnect;
        public bool Reconnecting => reconnectUntil > 0;   // the session ended on its own (host gone, join failed)
        string endReason;   // (collide, crackOnHit) chosen by the host
        public readonly List<string> Log = new List<string>();
        public readonly Dictionary<int, int> Ping = new Dictionary<int, int>();   // player id -> ms (host: every guest; guest: only the host, id 0)

        readonly MonoBehaviour runner;
        readonly CoopConfig cfg;
        NetManager manager;
        StunLayer stun;
        NetPeer hostPeer;
        readonly HashSet<int> remoteIds = new HashSet<int>();
        readonly NetDataWriter writer = new NetDataWriter();
        readonly List<(IPEndPoint ep, float until)> punchTargets = new List<(IPEndPoint, float)>();
        float nextPunch, nextStun;
        Mono.Nat.INatDevice upnpDevice;
        int port;

        public CoopNet(MonoBehaviour runner, CoopConfig cfg) { this.runner = runner; this.cfg = cfg; }

        void Say(string s)
        {
            Plugin.Log.LogInfo(s);
            lock (Log) { Log.Add(s); if (Log.Count > 10) Log.RemoveAt(0); }
        }

        NetManager Create()
        {
            stun = new StunLayer();
            return new NetManager(this, stun)
            {
                IPv6Enabled = false,
                UnconnectedMessagesEnabled = true,
                BroadcastReceiveEnabled = true,
                DisconnectTimeout = 10000,
                MaxConnectAttempts = 30,
                ReconnectDelay = 400,
            };
        }

        // ---------------------------------------------------------------- host

        public void Host()
        {
            Stop();
            IsHost = true;
            port = cfg.Port.Value;
            manager = Create();
            if (!manager.Start(port)) { Say($"Не удалось открыть порт {port}/UDP (занят?)"); manager = null; return; }
            Say($"Сервер запущен на порту {port}/UDP. Ищу внешний адрес...");
            byte salt = (byte)UnityEngine.Random.Range(0, 256);
            runner.StartCoroutine(HostSetup(salt));
        }

        IEnumerator HostSetup(byte salt)
        {
            // UPnP in the background (Mono.Nat), STUN on our own socket at the same time.
            IPAddress upnpIp = null; bool upnpDone = false;
            ThreadPool.QueueUserWorkItem(_ => { upnpIp = TryUpnp(port); upnpDone = true; });
            IPEndPoint mapped = null;
            yield return StunQuery(r => mapped = r);
            float wait = Time.unscaledTime + 8f;
            while (!upnpDone && Time.unscaledTime < wait) yield return null;
            if (manager == null) yield break;

            IPEndPoint pub;
            VpnSuspected = mapped != null && upnpIp != null && !mapped.Address.Equals(upnpIp);
            if (VpnSuspected)
                Say($"Внимание: STUN видит {mapped.Address}, роутер — {upnpIp}. Включён VPN? Друзьям нужен адрес роутера.");
            // The router's WAN address is where UPnP opened the port, so it wins over what STUN saw.
            if (upnpIp != null && !IsPrivate(upnpIp)) pub = new IPEndPoint(upnpIp, port);
            else if (mapped != null) pub = mapped;
            else { Say("Внешний адрес не найден. Друзья могут подключиться по IP (LAN / Radmin)."); yield break; }

            if (upnpIp != null && IsPrivate(upnpIp)) Say("Роутер за CGNAT провайдера: UPnP не поможет, будет пробивка NAT.");
            Code = InviteCode.Make(pub, salt);
            Say($"Внешний адрес {pub}. Код приглашения: {Code}");
            runner.StartCoroutine(PollSignals(InviteCode.Topic(Code)));
        }

        IPAddress TryUpnp(int p)
        {
            try
            {
                var found = new ManualResetEvent(false);
                EventHandler<Mono.Nat.DeviceEventArgs> onFound = (s, e) => { upnpDevice = upnpDevice ?? e.Device; found.Set(); };
                Mono.Nat.NatUtility.DeviceFound += onFound;
                Mono.Nat.NatUtility.StartDiscovery();
                bool ok = found.WaitOne(5000);
                Mono.Nat.NatUtility.StopDiscovery();
                Mono.Nat.NatUtility.DeviceFound -= onFound;
                if (!ok) { Say("UPnP: роутер не ответил"); return null; }
                upnpDevice.CreatePortMapAsync(new Mono.Nat.Mapping(Mono.Nat.Protocol.Udp, p, p, 0, "EggCoop")).GetAwaiter().GetResult();
                var ip = upnpDevice.GetExternalIPAsync().GetAwaiter().GetResult();
                Say($"UPnP: порт {p}/UDP открыт на роутере ({ip})");
                return ip;
            }
            catch (Exception e) { Say("UPnP не сработал: " + e.Message); return null; }
        }

        // Host side of hole punching: guests post their public endpoint to an ntfy topic derived from the code.
        IEnumerator PollSignals(string topic)
        {
            long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
            while (manager != null && IsHost)
            {
                using (var req = UnityWebRequest.Get($"{cfg.SignalUrl.Value}/{topic}/json?poll=1&since={since}"))
                {
                    req.timeout = 10;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                        foreach (var line in req.downloadHandler.text.Split('\n'))
                        {
                            string msg = JsonField(line, "message");
                            string time = JsonField(line, "time");
                            if (time != null && long.TryParse(time, out var t)) since = Math.Max(since, t + 1);
                            if (msg != null && msg.StartsWith("p ") && TryParseEndPoint(msg.Substring(2), port, out var ep))
                            {
                                Say($"Пробиваю NAT к {ep}");
                                punchTargets.Add((ep, Time.unscaledTime + 6f));
                            }
                        }
                }
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        // ---------------------------------------------------------------- guest

        public void Join(string target)
        {
            Stop();
            IsHost = false;
            target = target.Trim();
            if (TryParseEndPoint(target, cfg.Port.Value, out var direct))
            {
                manager = Create();
                manager.Start();
                Say($"Подключаюсь напрямую к {direct}...");
                manager.Connect(direct, ConnectKey);
                return;
            }
            if (!InviteCode.TryParse(target, out var hostEp, out _)) { Say("Это не код приглашения и не IP:порт"); return; }
            manager = Create();
            manager.Start();
            Say($"Подключаюсь к {hostEp} по коду...");
            manager.SendBroadcast(Discover, hostEp.Port);    // same LAN as the host? it will answer EGGHERE
            manager.Connect(hostEp, ConnectKey);
            runner.StartCoroutine(GuestPunch(InviteCode.Topic(target), hostEp));
        }

        IEnumerator GuestPunch(string topic, IPEndPoint hostEp)
        {
            IPEndPoint mapped = null;
            yield return StunQuery(r => mapped = r);
            if (manager == null || hostPeer != null) yield break;
            if (mapped == null) { Say("STUN не ответил, пробую без пробивки NAT"); yield break; }
            using (var req = new UnityWebRequest($"{cfg.SignalUrl.Value}/{topic}", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.ASCII.GetBytes("p " + mapped));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 10;
                yield return req.SendWebRequest();
                Say(req.result == UnityWebRequest.Result.Success ? $"Мой внешний адрес {mapped} отправлен хосту" : "Сигнальный сервер недоступен: " + req.error);
            }
            punchTargets.Add((hostEp, Time.unscaledTime + 6f));
        }

        // ---------------------------------------------------------------- common

        IEnumerator StunQuery(Action<IPEndPoint> done)
        {
            foreach (var server in cfg.StunServers.Value.Split(','))
            {
                IPEndPoint ep = null; bool resolved = false;
                ThreadPool.QueueUserWorkItem(_ => { TryParseEndPoint(server.Trim(), 3478, out ep); resolved = true; });
                while (!resolved) yield return null;
                if (ep == null || manager == null) continue;
                stun.Begin(ep);
                manager.SendUnconnectedMessage(new byte[19], ep);   // StunLayer rewrites these 20 bytes into a binding request
                float until = Time.unscaledTime + 2.5f;
                while (stun.Result == null && Time.unscaledTime < until) yield return null;
                if (stun.Result != null) { done(stun.Result); yield break; }
            }
            done(null);
        }

        public void Poll()
        {
            if (manager == null) return;
            manager.PollEvents();
            if (endReason != null) { var why = endReason; endReason = null; Stop(); OnEnded?.Invoke(why); return; }
            float now = Time.unscaledTime;
            if (reconnectUntil > 0 && hostPeer == null)
            {
                if (now > reconnectUntil) { reconnectUntil = 0; Stop(); OnEnded?.Invoke("Не удалось вернуться к хосту за 30 с — сессия завершена"); return; }
                if (now >= nextReconnect && manager.GetPeersCount(ConnectionState.Outgoing) == 0)
                {
                    nextReconnect = now + 3f;
                    manager.Connect(lastHost, ConnectKey);
                }
            }
            if (IsHost && now >= nextClock && manager.ConnectedPeersCount > 0)
            {
                nextClock = now + 1f;
                var w = new NetDataWriter(); w.Put(MsgClock); w.Put((byte)0); w.Put(Time.time);
                manager.SendToAll(w, DeliveryMethod.Unreliable);
            }
            if (punchTargets.Count > 0 && now >= nextPunch)
            {
                nextPunch = now + 0.25f;
                punchTargets.RemoveAll(t => t.until < now || (!IsHost && hostPeer != null));
                foreach (var t in punchTargets) manager.SendUnconnectedMessage(Punch, t.ep);
            }
            // Keep the host's NAT mapping alive while nobody is connected.
            if (IsHost && Code != null && now >= nextStun && stun.Server != null)
            {
                nextStun = now + 15f;
                manager.SendUnconnectedMessage(new byte[19], stun.Server);
            }
        }

        public void SendLocal(EggState s)
        {
            if (manager == null) return;
            Write(0, s);
            if (IsHost) manager.SendToAll(writer, DeliveryMethod.Sequenced);
            else hostPeer?.Send(writer, DeliveryMethod.Sequenced);
        }

        // World messages: guest -> host (host relays), host -> everyone, or host -> one player (toId).
        public NetDataWriter WorldWriter() { var w = new NetDataWriter(); w.Put(MsgWorld); w.Put((byte)MyId); return w; }
        public void SendWorld(NetDataWriter w, DeliveryMethod m, int toId = -1)
        {
            if (manager == null) return;
            if (!IsHost) { hostPeer?.Send(w, m); return; }
            foreach (var p in manager.ConnectedPeerList) if (toId < 0 || p.Id + 1 == toId) p.Send(w, m);
        }

        // Host only: tell every guest how eggs interact. Guests apply it to their own egg.
        bool collide = true, crackOnHit;
        public void SetSettings(bool collide, bool crackOnHit)
        {
            this.collide = collide; this.crackOnHit = crackOnHit;
            if (IsHost && manager != null) manager.SendToAll(SettingsPacket(), DeliveryMethod.ReliableOrdered);
        }

        NetDataWriter SettingsPacket()
        {
            var w = new NetDataWriter();
            w.Put(MsgSettings); w.Put((byte)0); w.Put((byte)((collide ? 1 : 0) | (crackOnHit ? 2 : 0)));
            return w;
        }

        void Write(int id, EggState s)
        {
            writer.Reset();
            writer.Put(MsgState); writer.Put((byte)id);
            writer.Put(s.pos.x); writer.Put(s.pos.y); writer.Put(s.pos.z);
            writer.Put(s.rot.x); writer.Put(s.rot.y); writer.Put(s.rot.z); writer.Put(s.rot.w);
            writer.Put(s.yolk); writer.Put(s.skin); writer.Put(s.nick ?? "", 24); writer.Put(s.tpSeq); writer.Put(s.tpTotal);
        }

        public void Stop()
        {
            if (manager == null) return;
            manager.Stop(true);
            manager = null;
            hostPeer = null;
            myId = 0;
            reconnectUntil = 0;
            SharedClock.Offset = 0f;
            Code = null;
            punchTargets.Clear();
            Ping.Clear();
            foreach (var id in remoteIds.ToList()) OnLeave?.Invoke(id);
            remoteIds.Clear();
            var dev = upnpDevice; upnpDevice = null;
            if (dev != null) ThreadPool.QueueUserWorkItem(_ => { try { dev.DeletePortMapAsync(new Mono.Nat.Mapping(Mono.Nat.Protocol.Udp, port, port)).GetAwaiter().GetResult(); } catch { } });
            Say("Отключено");
        }

        // ---------------------------------------------------------------- LiteNetLib events

        public void OnConnectionRequest(ConnectionRequest request)
        {
            Say($"Запрос на вход от {request.RemoteEndPoint}");
            if (!IsHost || manager.ConnectedPeersCount >= MaxPlayers - 1) { request.Reject(); return; }
            if (request.AcceptIfKey(ConnectKey) == null) Say($"Отклонён {request.RemoteEndPoint}: другая версия мода");
        }

        public void OnPeerConnected(NetPeer peer)
        {
            if (IsHost)
            {
                Say($"Игрок подключился: {peer}");
                var w = new NetDataWriter(); w.Put(MsgWelcome); w.Put((byte)(peer.Id + 1)); w.Put(Application.version);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
                peer.Send(SettingsPacket(), DeliveryMethod.ReliableOrdered);
                return;
            }
            if (hostPeer != null) { peer.Disconnect(); return; }   // LAN + public route both succeeded: keep the first
            hostPeer = peer;
            lastHost = new IPEndPoint(peer.Address, peer.Port);
            Say(reconnectUntil > 0 ? $"Снова на связи с хостом {peer}" : $"Подключено к хосту {peer}");
            if (reconnectUntil > 0) OnEnded?.Invoke("Снова на связи с хостом");   // just a toast: the session goes on
            reconnectUntil = 0;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (IsHost)
            {
                int id = peer.Id + 1;
                Say($"Игрок отключился ({info.Reason})");
                OnLeave?.Invoke(id);
                writer.Reset(); writer.Put(MsgLeave); writer.Put((byte)id);
                manager.SendToAll(writer, DeliveryMethod.ReliableOrdered);
            }
            else if (peer == hostPeer && info.Reason != DisconnectReason.RemoteConnectionClose && info.Reason != DisconnectReason.DisconnectPeerCalled)
            {
                Say($"Связь с хостом потеряна ({info.Reason}) — переподключаюсь...");
                OnEnded?.Invoke("Связь с хостом потеряна — переподключаюсь...");
                hostPeer = null;
                myId = 0;
                foreach (var id in remoteIds) OnLeave?.Invoke(id);
                remoteIds.Clear();
                reconnectUntil = Time.unscaledTime + ReconnectWindow;
                nextReconnect = 0;
            }
            else if (reconnectUntil > 0 && hostPeer == null) { }   // one failed attempt while reconnecting: Poll tries again
            else if (peer == hostPeer || hostPeer == null)
            {
                string why = hostPeer == null ? $"Не удалось подключиться ({info.Reason})" : $"Хост вышел или пропал ({info.Reason}) — сессия завершена";
                Say(why);
                hostPeer = null;
                foreach (var id in remoteIds) OnLeave?.Invoke(id);
                remoteIds.Clear();
                // A guest without a host is not in a session any more; end it (after PollEvents, not inside it),
                // unless another route to the host (LAN / public) is still being tried.
                if (manager.GetPeersCount(ConnectionState.Outgoing) == 0) { endReason = why; }
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod method)
        {
            byte type = r.GetByte();
            int id = r.GetByte();
            if (type == MsgLeave) { remoteIds.Remove(id); OnLeave?.Invoke(id); r.Recycle(); return; }
            if (type == MsgSettings && !IsHost) { byte f = r.GetByte(); r.Recycle(); OnSettings?.Invoke((f & 1) != 0, (f & 2) != 0); return; }
            if (type == MsgWelcome && !IsHost)
            {
                myId = id;
                string hostGame = r.AvailableBytes > 0 ? r.GetString() : null;   // 0.4.0-0.4.2 hosts do not send it
                if (hostGame != null && hostGame != Application.version) { Say($"Версия игры у хоста {hostGame}, у вас {Application.version}"); OnEnded?.Invoke($"Разные версии игры: у хоста {hostGame}, у вас {Application.version}. Предметы могут не совпадать"); }
                r.Recycle(); return;
            }
            if (type == MsgClock && !IsHost) { SharedClock.Sample(r.GetFloat() + peer.Ping / 1000f); r.Recycle(); return; }
            if (type == MsgWorld)
            {
                if (IsHost)
                {
                    id = peer.Id + 1;
                    // Relay to the other guests with the real sender id, then handle it here too.
                    var relay = new NetDataWriter(); relay.Put(MsgWorld); relay.Put((byte)id); relay.Put(r.RawData, r.Position, r.AvailableBytes);
                    foreach (var p in manager.ConnectedPeerList) if (p != peer) p.Send(relay, method);
                }
                try { OnWorld?.Invoke(id, r); } catch (Exception e) { Plugin.Warn("world", e); }
                r.Recycle();
                return;
            }
            if (type != MsgState) { r.Recycle(); return; }
            var s = new EggState
            {
                pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
                yolk = r.GetBool(), skin = r.GetInt(), nick = r.GetString(24), tpSeq = r.GetByte(), tpTotal = r.GetUShort(),
            };
            r.Recycle();
            if (IsHost)
            {
                id = peer.Id + 1;
                Write(id, s);
                foreach (var p in manager.ConnectedPeerList) if (p != peer) p.Send(writer, DeliveryMethod.Sequenced);
            }
            else remoteIds.Add(id);
            try { OnState?.Invoke(id, s); } catch (Exception e) { Plugin.Warn("state", e); }
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint ep, NetPacketReader r, UnconnectedMessageType type)
        {
            byte[] data = r.GetRemainingBytes();
            r.Recycle();
            if (IsHost && data.SequenceEqual(Discover)) manager.SendUnconnectedMessage(Here, ep);
            else if (!IsHost && hostPeer == null && data.SequenceEqual(Here)) { Say($"Хост найден в локальной сети: {ep}"); manager.Connect(ep, ConnectKey); }
        }

        public void OnNetworkError(IPEndPoint ep, SocketError e) => Say($"Ошибка сети {ep}: {e}");
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) => Ping[IsHost ? peer.Id + 1 : 0] = latency;

        // ---------------------------------------------------------------- helpers

        static bool IsPrivate(IPAddress a)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
        }

        // "1.2.3.4", "1.2.3.4:7788" or "host.name:3478" (DNS lookup blocks: call off the main thread for names).
        static bool TryParseEndPoint(string s, int defPort, out IPEndPoint ep)
        {
            ep = null;
            int colon = s.LastIndexOf(':');
            string host = colon > 0 ? s.Substring(0, colon) : s;
            int port = defPort;
            if (colon > 0 && !int.TryParse(s.Substring(colon + 1), out port)) return false;
            if (!IPAddress.TryParse(host, out var ip))
            {
                if (!host.Contains(".") || host.Count(c => c == '-') >= 2) return false;   // invite codes are not hostnames
                try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                catch { return false; }
                if (ip == null) return false;
            }
            ep = new IPEndPoint(ip, port);
            return true;
        }

        // ponytail: tiny JSON field reader for ntfy's flat one-line objects; swap for a real parser if ntfy output grows nested strings.
        static string JsonField(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\":", StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length + 3;
            if (i < json.Length && json[i] == '"')
            {
                int end = json.IndexOf('"', i + 1);
                return end < 0 ? null : json.Substring(i + 1, end - i - 1);
            }
            int stop = i;
            while (stop < json.Length && (char.IsDigit(json[stop]) || json[stop] == '-')) stop++;
            return json.Substring(i, stop - i);
        }

        // Lets STUN share the game socket, so the mapped address is the one friends must reach.
        class StunLayer : PacketLayerBase
        {
            public volatile IPEndPoint Result;
            public IPEndPoint Server;
            readonly byte[] txid = new byte[12];

            public StunLayer() : base(0) { }

            public void Begin(IPEndPoint server)
            {
                Server = server; Result = null;
                new System.Random().NextBytes(txid);
            }

            public override void ProcessOutBoundPacket(ref IPEndPoint ep, ref byte[] data, ref int offset, ref int length)
            {
                if (length == 20 && Server != null && ep.Equals(Server)) Stun.WriteRequest(data, offset, txid);
            }

            public override void ProcessInboundPacket(ref IPEndPoint ep, ref byte[] data, ref int length)
            {
                if (!Stun.IsStun(data, length)) return;
                var r = Stun.ParseResponse(data, length);
                if (r != null) Result = r;
                length = 0;   // never let LiteNetLib see STUN traffic
            }
        }
    }
}
