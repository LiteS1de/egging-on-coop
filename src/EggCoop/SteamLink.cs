using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace EggCoop
{
    // Steam as the main way in: the host opens a friends-only Steam lobby, friends join it from the overlay invite or
    // "Join game" in their friends list, and all traffic goes through Valve's relays (SteamNetworkingMessages), so it
    // works behind any router and nobody's IP is ever shown. Needs the Steam version of the game on every PC.
    //
    // It is a pipe under the existing LiteNetLib session, not a second protocol: each side keeps a loopback UDP socket
    // and forwards datagrams both ways. The guest's LiteNetLib connects to 127.0.0.1:<its pipe>; on the host every
    // remote player gets a loopback socket that talks to the normal host port. Reliability, ordering, pings and
    // reconnects stay LiteNetLib's job, so every message type works unchanged.
    public class SteamLink
    {
        const string LobbyKey = "eggcoop";   // lobby data: the mod's ConnectKey (version check before connecting)
        readonly CoopNet net;
        readonly CoopConfig cfg;
        public bool Available { get; private set; }
        public CSteamID Lobby { get; private set; } = CSteamID.Nil;
        public bool InLobby => Lobby.IsValid();
        public bool GuestViaSteam => hostId.IsValid();
        public string HostName => hostId.IsValid() ? SteamFriends.GetFriendPersonaName(hostId) : null;
        public string Status = "";

        CSteamID hostId = CSteamID.Nil;
        readonly LoopbackPipe pipe = new LoopbackPipe();
        readonly IntPtr[] recv = new IntPtr[64];
        IntPtr sendBuf;
        byte[] copyBuf = new byte[2048];
        float nextProbe;
        bool wasActive, joinAfterInit;
        ulong pendingLobby;
        CallResult<LobbyCreated_t> lobbyCreated;
        CallResult<LobbyEnter_t> lobbyEntered;
        Callback<GameLobbyJoinRequested_t> joinRequested;
        Callback<GameRichPresenceJoinRequested_t> presenceJoin;
        Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequest;

        public SteamLink(CoopNet net, CoopConfig cfg)
        {
            this.net = net; this.cfg = cfg;
            pipe.SendRemote = (to, d, len) => Send(new CSteamID(to), d, len);
            pipe.Allowed = id => IsLobbyMember(new CSteamID(id));
            pipe.Log = s => Plugin.Log.LogInfo("Steam: " + s);
            // Launched by accepting an invite while the game was closed: Steam passes +connect_lobby <id>.
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++)
                if (a[i] == "+connect_lobby" && ulong.TryParse(a[i + 1], out var id)) { pendingLobby = id; joinAfterInit = true; }
        }

        // The game starts Steam itself a little after we load: look again every second until it is up.
        void Probe()
        {
            if (Available || Time.unscaledTime < nextProbe) return;
            nextProbe = Time.unscaledTime + 1f;
            try
            {
                if (!SteamAPI.IsSteamRunning() || !SteamUser.BLoggedOn()) return;
                lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
                lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
                joinRequested = Callback<GameLobbyJoinRequested_t>.Create(c => JoinLobby(c.m_steamIDLobby.m_SteamID));
                presenceJoin = Callback<GameRichPresenceJoinRequested_t>.Create(c => { if (TryLobbyFromConnect(c.m_rgchConnect, out var id)) JoinLobby(id); });
                sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                SteamNetworkingUtils.InitRelayNetworkAccess();   // warm up the relay network before anyone connects
                sendBuf = Marshal.AllocHGlobal(64 * 1024);
                Available = true;
                Plugin.Log.LogInfo($"Steam: ready as {SteamFriends.GetPersonaName()} (app {SteamUtils.GetAppID()})");
                if (joinAfterInit) { joinAfterInit = false; JoinLobby(pendingLobby); }
            }
            catch (Exception e) { Plugin.Log.LogInfo("Steam not available yet: " + e.Message); nextProbe = Time.unscaledTime + 5f; }
        }

        static bool TryLobbyFromConnect(string connect, out ulong id)
        {
            id = 0;
            var p = (connect ?? "").Split(' ');
            int i = Array.IndexOf(p, "+connect_lobby");
            return i >= 0 && i + 1 < p.Length && ulong.TryParse(p[i + 1], out id);
        }

        // ---------------------------------------------------------------- host

        // After CoopNet.Host(): open the lobby friends can join.
        public void OpenLobby()
        {
            if (!Available || !net.IsHost) return;
            Status = "Создаю лобби Steam…";
            lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, CoopNet.MaxPlayers));
        }

        void OnLobbyCreated(LobbyCreated_t c, bool ioFailure)
        {
            if (ioFailure || c.m_eResult != EResult.k_EResultOK) { Status = $"Steam не создал лобби ({c.m_eResult})"; return; }
            if (!net.Active || !net.IsHost) { SteamMatchmaking.LeaveLobby(new CSteamID(c.m_ulSteamIDLobby)); return; }   // hosting stopped meanwhile
            Lobby = new CSteamID(c.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, LobbyKey, CoopNet.ConnectKey);
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);   // "Join game" in friends' lists
            SteamFriends.SetRichPresence("status", "Egging On Co-op");
            Status = "Лобби Steam открыто — пригласите друзей";
            Plugin.Log.LogInfo("Steam: lobby " + Lobby.m_SteamID);
        }

        public void Invite()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(Lobby);
        }

        bool IsLobbyMember(CSteamID who)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(Lobby);
            for (int i = 0; i < n; i++) if (SteamMatchmaking.GetLobbyMemberByIndex(Lobby, i) == who) return true;
            return false;
        }

        // Valve asks before the first message from someone new: only players in our lobby get through.
        void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t c)
        {
            var who = c.m_identityRemote.GetSteamID();
            if ((net.IsHost && IsLobbyMember(who)) || (!net.IsHost && who == hostId)) SteamNetworkingMessages.AcceptSessionWithUser(ref c.m_identityRemote);
            else Plugin.Log.LogInfo("Steam: ignored a session from someone outside the lobby");
        }

        // ---------------------------------------------------------------- guest

        void JoinLobby(ulong id)
        {
            if (!Available) { pendingLobby = id; joinAfterInit = true; return; }
            if (net.Active && net.IsHost) { Plugin.Notify("Вы сейчас хост — сначала отключитесь (F9), потом примите приглашение"); return; }
            Status = "Захожу в лобби Steam…";
            lobbyEntered.Set(SteamMatchmaking.JoinLobby(new CSteamID(id)));
        }

        void OnLobbyEntered(LobbyEnter_t c, bool ioFailure)
        {
            var lobby = new CSteamID(c.m_ulSteamIDLobby);
            if (ioFailure || c.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) { Status = "Не удалось зайти в лобби (оно закрыто или полное)"; Plugin.Notify(Status); return; }
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID()) return;   // our own lobby (host side)
            string key = SteamMatchmaking.GetLobbyData(lobby, LobbyKey);
            if (key != CoopNet.ConnectKey)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                Status = $"У хоста другая версия мода ({(string.IsNullOrEmpty(key) ? "?" : key)}, у вас {CoopNet.ConnectKey})";
                Plugin.Notify(Status);
                return;
            }
            Lobby = lobby; hostId = owner;
            int port = pipe.OpenGuest(owner.m_SteamID);
            Status = $"Подключаюсь через Steam к {HostName}…";
            Plugin.Log.LogInfo($"Steam: joined lobby {lobby.m_SteamID}, host {owner.m_SteamID}, pipe 127.0.0.1:{port}");
            net.Join("127.0.0.1:" + port);
        }

        // ---------------------------------------------------------------- pipe

        // Every frame, before and after CoopNet.Poll: move datagrams between loopback and Steam.
        public void Tick()
        {
            Probe();
            if (!Available) return;
            if (wasActive && !net.Active) Close();
            wasActive = net.Active;
            if (!InLobby && !pipe.IsGuest) return;
            pipe.HostPort = net.IsHost ? cfg.Port.Value : 0;
            try
            {
                pipe.Pump();
                int n;
                while ((n = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, recv, recv.Length)) > 0)
                    for (int i = 0; i < n; i++)
                    {
                        var m = SteamNetworkingMessage_t.FromIntPtr(recv[i]);
                        var who = m.m_identityPeer.GetSteamID();
                        int len = m.m_cbSize;
                        if (copyBuf.Length < len) copyBuf = new byte[len];
                        Marshal.Copy(m.m_pData, copyBuf, 0, len);
                        SteamNetworkingMessage_t.Release(recv[i]);
                        pipe.Deliver(who.m_SteamID, copyBuf, len);
                    }
            }
            catch (SocketException e) { Plugin.Log.LogWarning("Steam pipe: " + e.Message); }
        }


        void Send(CSteamID to, byte[] d, int len)
        {
            if (!to.IsValid() || len > 64 * 1024) return;
            Marshal.Copy(d, 0, sendBuf, len);
            var id = new SteamNetworkingIdentity();
            id.SetSteamID(to);
            // Unreliable on purpose: LiteNetLib already does acks and resends on top.
            SteamNetworkingMessages.SendMessageToUser(ref id, sendBuf, (uint)len, Constants.k_nSteamNetworkingSend_UnreliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, 0);
        }

        // The session ended (we left, the host closed, the join failed): leave the lobby and drop every pipe.
        public void Close()
        {
            if (!Available) return;
            try
            {
                pipe.Close(who => { var id = new SteamNetworkingIdentity(); id.SetSteamID(new CSteamID(who)); SteamNetworkingMessages.CloseSessionWithUser(ref id); });
                if (InLobby) SteamMatchmaking.LeaveLobby(Lobby);
                SteamFriends.ClearRichPresence();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Steam close: " + e.Message); }
            Lobby = CSteamID.Nil; hostId = CSteamID.Nil; Status = "";
        }
    }
}
