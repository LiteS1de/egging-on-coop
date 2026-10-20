"""Bad-network sweep: chaos relay between the two lab copies, every profile x every ghost delay.

python -I tools/net_sweep.py [--seconds 40] [--profiles lan,bad_wifi,...] [--delays 50,100,...]
Starts the relay, launches the lab (guest joins through the relay), walks the grid, prints a table
and writes out/net_sweep.json. Stops the lab copies at the end.
"""
import argparse
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
sys.path.insert(0, os.path.join(ROOT, "vendor", "coop-anything", "tools", "chaos"))
import client  # noqa: E402  (coop-anything chaos RPC helpers)
import compare_probe  # noqa: E402

RELAY_PORT = 7790
P1, P2 = (os.path.join(ROOT, "lab", p, "BepInEx") for p in ("p1", "p2"))


def lab(cmd, *extra):
    subprocess.run(["pwsh", "-NoProfile", "-File", os.path.join(ROOT, "tools", "lab.ps1"), cmd, *extra], check=True)


def wait_for(path, text, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if os.path.exists(path) and text in open(path, encoding="utf-8", errors="replace").read():
            return True
        time.sleep(2)
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=int, default=40)
    ap.add_argument("--profiles", default="lan,good_wifi,bad_wifi,mobile_tether,transatlantic,hell")
    ap.add_argument("--delays", default="50,100,150,200,300")
    a = ap.parse_args()
    os.makedirs(os.path.join(ROOT, "out"), exist_ok=True)
    info = os.path.join(ROOT, "out", "chaos-info.json")
    if os.path.exists(info):
        os.remove(info)
    relay = subprocess.Popen([sys.executable, "-I", os.path.join(ROOT, "vendor", "coop-anything", "tools", "chaos", "chaos.py"),
                              "relay", "--target", "127.0.0.1:7788", "--listen", str(RELAY_PORT), "--profile", "lan",
                              "--info-file", info])
    segments = []
    try:
        while not os.path.exists(info):
            time.sleep(0.2)
        lab("up", "-join", f"127.0.0.1:{RELAY_PORT}")
        if not wait_for(os.path.join(P2, "LogOutput.log"), "Ghost 0", 240):
            sys.exit("guest never saw the host ghost")
        for profile in a.profiles.split(","):
            client.set_profile(info, profile)
            for d in a.delays.split(","):
                with open(os.path.join(P2, "coop-delay.txt"), "w") as f:
                    f.write(d)
                time.sleep(5)   # plugin polls the file every 3 s, then let the buffer settle
                t0 = int(time.time() * 1000)
                time.sleep(a.seconds)
                segments.append({"profile": profile, "delay": int(d), "t0": t0, "t1": int(time.time() * 1000)})
                print(f"recorded {profile} / {d} ms", flush=True)
        stats = client.stats(info)
    finally:
        relay.terminate()
        lab("down")

    real = compare_probe.load(os.path.join(P1, "coop-probe.txt"), "me")
    ghost = compare_probe.load(os.path.join(P2, "coop-probe.txt"), "Host")
    print(f"\n{'profile':15}{'delay':>6}{'lag':>6}{'moving':>8}{'mean':>8}{'p95':>8}{'freeze':>8}{'jump':>7}")
    for s in segments:
        m = compare_probe.metrics(real, ghost, s["t0"], s["t1"]) or {}
        s.update(m)
        if "mean" in m:
            print(f"{s['profile']:15}{s['delay']:>6}{m['lag']:>6}{m['moving']:>8}{m['mean']:>8.3f}{m['p95']:>8.3f}{m['freeze']:>8.0%}{m['jump']:>7.0%}")
        else:
            print(f"{s['profile']:15}{s['delay']:>6}  not enough movement {m}")
    json.dump({"segments": segments, "relay_stats": stats}, open(os.path.join(ROOT, "out", "net_sweep.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
