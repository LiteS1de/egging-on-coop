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

// Emote wheel: 8 slices clockwise from the top, centre closes.
Check(SocialMath.Sector(0, 0, 30, 8) == -1 && SocialMath.Sector(10, 10, 30, 8) == -1, "centre");
Check(SocialMath.Sector(0, -100, 30, 8) == 0 && SocialMath.Sector(100, 0, 30, 8) == 2, "top, right");
Check(SocialMath.Sector(0, 100, 30, 8) == 4 && SocialMath.Sector(-100, 0, 30, 8) == 6, "bottom, left");
Check(SocialMath.Sector(70, -70, 30, 8) == 1 && SocialMath.Sector(-20, -100, 30, 8) == 0 && SocialMath.Sector(-70, -70, 30, 8) == 7, "diagonals, wrap");
Check(SocialMath.Sector(0, -900, 30, 8) == 0, "past the rim");

// Falls: a drop counts once the egg rests; small bumps and slow climbs do not.
var ft = new FallTracker(); ft.Reset(100);
float got = 0;
for (int i = 0; i < 60; i++) got += ft.Step(100 - i * 0.5f, 8f, 0.02f);   // falling 30 m, fast
Check(got == 0, "no count mid-air");
for (int i = 0; i < 40; i++) got += ft.Step(70, 0.1f, 0.02f);              // resting 0.8 s
Check(Math.Abs(got - 30f) < 0.01f, "fall counted once: " + got);
for (int i = 0; i < 40; i++) got += ft.Step(68, 0.1f, 0.02f);              // 2 m lower after a nudge
Check(Math.Abs(got - 30f) < 0.01f, "small drop ignored");
ft.Reset(200); got = 0;                                                       // teleported up
for (int i = 0; i < 40; i++) got += ft.Step(200, 0.1f, 0.02f);
Check(got == 0, "teleport is not a fall");

// Shame board survives a config round trip.
var sh = new System.Collections.Generic.Dictionary<string, Shame> { ["Host"] = new Shame { Cracks = 3 }, ["a:b"] = new Shame() };
sh["Host"].AddFall(12.34f); sh["Host"].AddFall(40f);
var back2 = Shame.Parse(Shame.Format(sh));
Check(back2.Count == 2 && back2["Host"].Falls == 2 && back2["Host"].Cracks == 3 && Math.Abs(back2["Host"].MaxFall - 40) < 0.01 && Math.Abs(back2["Host"].Lost - 52.3) < 0.01, "shame round trip");
Check(back2.ContainsKey("a:b") && Shame.Parse("junk;x:1,2").Count == 0, "odd nicks, junk");

// Streamer mode hides addresses and codes, keeps clocks, ports and ordinary text.
Check(StreamerMask.Apply("Подключено к хосту 26.12.34.56:7788") == "Подключено к хосту •.•.•.•:7788", "ipv4 + port");
Check(StreamerMask.Apply("код ABCD-EFGH-JKMN готов") == "код ••••-••••-•••• готов", "invite code");
Check(!StreamerMask.Apply("from [fe80::1c2d:3e4f]:7788").Contains("fe80") && !StreamerMask.Apply("2001:db8:85a3:0:0:8a2e:370:7334").Contains("db8"), "ipv6");
Check(StreamerMask.Apply("t=10:41:09 пинг 80 мс, версия 5.4.23.5 тоже IP-подобна") == "t=10:41:09 пинг 80 мс, версия •.•.•.• тоже IP-подобна", "clock kept, dotted versions masked");

Console.WriteLine("code example: " + code);
Console.WriteLine("ALL OK");
