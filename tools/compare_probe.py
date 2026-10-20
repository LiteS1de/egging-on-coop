"""Compare where the host's egg really was with where the guest drew its ghost.

python -I tools/compare_probe.py lab/p1/BepInEx/coop-probe.txt lab/p2/BepInEx/coop-probe.txt Host
Prints the ghost's lag behind the real egg and the position error at that lag. Exit 1 if it looks broken.
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


def at(rows, t):
    """Linear interpolation of a track at time t (None outside it)."""
    for (t0, a), (t1, b) in zip(rows, rows[1:]):
        if t0 <= t <= t1:
            k = (t - t0) / max(t1 - t0, 1)
            return tuple(x + (y - x) * k for x, y in zip(a, b))
    return None


def dist(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def compare(real, ghost):
    best = None
    for lag in range(0, 1001, 10):
        errs = [dist(p, q) for t, p in ghost if (q := at(real, t - lag)) is not None]
        if len(errs) >= 20:
            mean = sum(errs) / len(errs)
            if best is None or mean < best[1]:
                best = (lag, mean, max(errs), len(errs))
    return best


def demo():
    real = [(t, (t / 1000.0, 0.0, 0.0)) for t in range(0, 10000, 100)]
    ghost = [(t, (max(t - 150, 0) / 1000.0, 0.0, 0.0)) for t in range(2000, 9000, 100)]
    lag, mean, _, _ = compare(real, ghost)
    assert lag == 150 and mean < 1e-6, (lag, mean)


if __name__ == "__main__":
    demo()
    host_file, guest_file, nick = sys.argv[1:4]
    real, ghost = load(host_file, "me"), load(guest_file, nick)
    moved = dist(real[0][1], real[-1][1]) if real else 0
    best = compare(real, ghost)
    if not best:
        print(f"not enough overlap: real={len(real)} ghost={len(ghost)}")
        sys.exit(1)
    lag, mean, worst, n = best
    print(f"samples={n} host moved={moved:.1f} m  ghost lag={lag} ms  error mean={mean:.3f} m worst={worst:.3f} m")
    sys.exit(0 if lag <= 400 and mean < 0.5 else 1)
