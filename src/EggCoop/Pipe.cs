using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace EggCoop
{
    // Loopback pipe under a LiteNetLib session (Steam-free, checked by src/SelfTest with a fake relay).
    // Guest: LiteNetLib connects to 127.0.0.1:<OpenGuest()>; whatever it sends goes to SendRemote(host), whatever
    // arrives from the host is handed back to it. Host: each remote id gets its own loopback socket talking to the
    // real host port, so LiteNetLib sees every remote player as a separate 127.0.0.1 peer.
    public class LoopbackPipe
    {
        public Action<ulong, byte[], int> SendRemote;   // (to, data, length) over the relay
        public Func<ulong, bool> Allowed = _ => true;    // host: may this remote id open a pipe?
        public Action<string> Log = _ => { };
        public int HostPort;
        public bool IsGuest => guestSock != null;
        public IEnumerable<ulong> Remotes => hostSocks.Keys;

        ulong host;
        UdpClient guestSock;
        IPEndPoint guestLocal;   // our own LiteNetLib, learned from its first datagram
        readonly Dictionary<ulong, UdpClient> hostSocks = new Dictionary<ulong, UdpClient>();

        public int OpenGuest(ulong hostId)
        {
            Close(null);
            host = hostId;
            guestSock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)guestSock.Client.LocalEndPoint).Port;
        }

        // Local LiteNetLib -> relay. Call every frame.
        public void Pump()
        {
            if (guestSock != null)
                while (guestSock.Available > 0)
                {
                    IPEndPoint from = null;
                    var d = guestSock.Receive(ref from);
                    guestLocal = from;
                    SendRemote(host, d, d.Length);
                }
            foreach (var kv in hostSocks)
                while (kv.Value.Available > 0)
                {
                    IPEndPoint from = null;
                    var d = kv.Value.Receive(ref from);
                    SendRemote(kv.Key, d, d.Length);
                }
        }

        // Relay -> local LiteNetLib.
        public void Deliver(ulong from, byte[] d, int len)
        {
            if (guestSock != null)
            {
                if (from == host && guestLocal != null) guestSock.Send(d, len, guestLocal);
                return;
            }
            if (!hostSocks.TryGetValue(from, out var sock))
            {
                if (HostPort == 0 || !Allowed(from)) return;
                sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                hostSocks[from] = sock;
                Log($"pipe opened for {from}");
            }
            sock.Send(d, len, new IPEndPoint(IPAddress.Loopback, HostPort));
        }

        public void Close(Action<ulong> closed)
        {
            foreach (var kv in hostSocks) { closed?.Invoke(kv.Key); kv.Value.Close(); }
            hostSocks.Clear();
            if (guestSock != null) { closed?.Invoke(host); guestSock.Close(); }
            guestSock = null; guestLocal = null; host = 0;
        }
    }
}
