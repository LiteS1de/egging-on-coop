"""Compare where the host's egg really was with where the guest drew its ghost.

python -I tools/compare_probe.py lab/p1/BepInEx/coop-probe.txt lab/p2/BepInEx/coop-probe.txt Host [t0_ms t1_ms]
Prints the ghost's lag behind the real egg and, while the egg moves, the position error at that lag
plus how often the ghost froze or jumped. Exit 1 if it looks broken.
"""
import sys


def load(path, who):
    rows = []
    for line in open(path):
        f = line.split()
        t, rest = int(f[0]), f[1:]
        for i in range(0, len(rest) - 3, 4):
            if rest[i] == who:
                rows.append((t, tuple(map(float, rest[i + 1:i + 4]))))
    return rows


def at(rows, t, hint=[0]):
    """Linear interpolation of a track at time t (None outside it). Tracks are time-sorted."""
    i = min(hint[0], len(rows) - 2)
    while i > 0 and rows[i][0] > t:
        i -= 1
    while i + 1 < len(rows) and rows[i + 1][0] < t:
        i += 1
    if i + 1 >= len(rows) or not rows[i][0] <= t <= rows[i + 1][0]:
        return None
    hint[0] = i
    (t0, a), (t1, b) = rows[i], rows[i + 1]
    k = (t - t0) / max(t1 - t0, 1)
    return tuple(x + (y - x) * k for x, y in zip(a, b))


def dist(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def best_lag(real, ghost):
    best = None
    for lag in range(0, 1501, 10):
        errs = [dist(p, q) for t, p in ghost if (q := at(real, t - lag)) is not None]
        if len(errs) >= 20:
            mean = sum(errs) / len(errs)
            if best is None or mean < best[1]:
                best = (lag, mean)
    return best[0] if best else None


def metrics(real, ghost, t0=None, t1=None):
    """Lag, then error/freeze/jump stats over ghost samples while the real egg moved faster than 0.3 m/s."""
    if t0 is not None:
        ghost = [g for g in ghost if t0 <= g[0] <= t1]
    lag = best_lag(real, ghost)
    if lag is None:
        return None
    errs, freezes, jumps, moving = [], 0, 0, 0
    for (ta, ga), (tb, gb) in zip(ghost, ghost[1:]):
        ra, rb = at(real, ta - lag), at(real, tb - lag)
        if ra is None or rb is None:
            continue
        real_step = dist(ra, rb)
        if real_step / max(tb - ta, 1) * 1000 < 0.3:
            continue
        moving += 1
        errs.append(dist(gb, rb))
        ghost_step = dist(ga, gb)
        if ghost_step < 0.2 * real_step:
            freezes += 1
        elif ghost_step > 2 * real_step + 0.05:
            jumps += 1
    if moving < 10:
        return {"lag": lag, "moving": moving}
    errs.sort()
    return {"lag": lag, "moving": moving, "mean": sum(errs) / len(errs), "p95": errs[int(len(errs) * 0.95)],
            "freeze": freezes / moving, "jump": jumps / moving}


def demo():
    real = [(t, (t / 1000.0, 0.0, 0.0)) for t in range(0, 10000, 100)]
    ghost = [(t, (max(t - 150, 0) / 1000.0, 0.0, 0.0)) for t in range(2000, 9000, 100)]
    m = metrics(real, ghost)
    assert m["lag"] == 150 and m["mean"] < 1e-6 and m["freeze"] == 0, m
    stuck = [(t, ((t // 500) * 0.5 - 0.15, 0.0, 0.0)) for t in range(2000, 9000, 100)]   # ghost that only moves every 0.5 s
    assert metrics(real, stuck)["freeze"] > 0.5, metrics(real, stuck)


if __name__ == "__main__":
    demo()
    host_file, guest_file, nick = sys.argv[1:4]
    window = tuple(map(int, sys.argv[4:6])) if len(sys.argv) >= 6 else (None, None)
    m = metrics(load(host_file, "me"), load(guest_file, nick), *window)
    print(m)
    sys.exit(0 if m and m.get("mean", 9) < 0.5 and m["lag"] <= 400 else 1)
