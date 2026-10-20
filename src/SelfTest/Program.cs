using System;
using System.Net;
using System.Net.Sockets;
using EggCoop;

static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

// Invite code round trip, incl. lowercase and O/I/L typos.
var ep = new IPEndPoint(IPAddress.Parse("203.0.113.57"), 7788);
string code = InviteCode.Make(ep, 0xA5);
Check(code.Length == 14 && code[4] == '-' && code[9] == '-', "format " + code);
Check(InviteCode.TryParse(code.ToLowerInvariant(), out var back, out var salt) && back.Equals(ep) && salt == 0xA5, "round trip");
Check(InviteCode.TryParse(code.Replace('0', 'O').Replace('1', 'I'), out back, out _) && back.Equals(ep), "typo tolerance");
Check(!InviteCode.TryParse("ABCD-EFGH", out _, out _), "short code rejected");
Check(!InviteCode.TryParse("1.2.3.4:7788", out _, out _), "IP is not a code");
Check(InviteCode.Topic(code) == InviteCode.Topic(code.ToLowerInvariant().Replace("-", "")), "topic stable");

// STUN: parse a synthetic XOR-MAPPED-ADDRESS response for 198.51.100.7:40000.
var resp = new byte[32];
resp[0] = 0x01; resp[1] = 0x01; resp[3] = 12;
resp[4] = 0x21; resp[5] = 0x12; resp[6] = 0xA4; resp[7] = 0x42;
resp[20] = 0x00; resp[21] = 0x20; resp[23] = 8; resp[25] = 0x01;
int xport = 40000 ^ 0x2112; resp[26] = (byte)(xport >> 8); resp[27] = (byte)xport;
byte[] ip = { 198, 51, 100, 7 }, cookie = { 0x21, 0x12, 0xA4, 0x42 };
for (int k = 0; k < 4; k++) resp[28 + k] = (byte)(ip[k] ^ cookie[k]);
Check(Stun.ParseResponse(resp, resp.Length)?.Equals(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000)) == true, "stun parse");

// Live STUN query (skipped offline).
try
{
    using var udp = new UdpClient(0);
    udp.Client.ReceiveTimeout = 3000;
    var req = new byte[20]; Stun.WriteRequest(req, 0, new byte[12]);
    udp.Send(req, req.Length, "stun.l.google.com", 19302);
    IPEndPoint from = null;
    var data = udp.Receive(ref from);
    Console.WriteLine("live STUN: my public address is " + Stun.ParseResponse(data, data.Length));
}
catch (Exception e) { Console.WriteLine("live STUN skipped: " + e.Message); }

Console.WriteLine("code example: " + code);
Console.WriteLine("ALL OK");
