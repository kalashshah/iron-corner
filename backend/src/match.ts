// Matchmaking (docs/flow-spec.md): one queue, a matcher pass every 2 s. `pass()` is the pure, testable core.
import crypto from "node:crypto";
import type { QEntry } from "./state.ts";

export const STAKES = [10, 25, 50];
export const HOUSE_AFTER_S = 25, PREV_OPP_LOCK_S = 60;
export const windowFor = (waitedSec: number) => Math.min(800, 150 + 25 * waitedSec);

// Oldest entry first. Candidates: same league + stake, different owner, not the previous opponent (unless the older
// entry waited > 60 s), rating inside the older entry's widening window. One of the 3 closest is drawn with
// crypto.randomInt, so the pairing is never predictable as "always the closest". Waited > 25 s alone → house.
export function pass(queue: QEntry[], now: number, prevOpp: (botId: string) => string | null | undefined,
  randInt: (n: number) => number = (n) => crypto.randomInt(n)) {
  const left = [...queue].sort((x, y) => x.joinedAt - y.joinedAt);
  const pairs: [QEntry, QEntry][] = [], house: QEntry[] = [];
  for (let i = 0; i < left.length;) {
    const e = left[i], waited = (now - e.joinedAt) / 1000;
    const top = left.filter((c) => c !== e && c.league === e.league && c.stakeCents === e.stakeCents && c.ownerId !== e.ownerId &&
        (waited > PREV_OPP_LOCK_S || (prevOpp(e.botId) !== c.botId && prevOpp(c.botId) !== e.botId)) &&
        Math.abs(c.rating - e.rating) <= windowFor(waited))
      .sort((x, y) => Math.abs(x.rating - e.rating) - Math.abs(y.rating - e.rating)).slice(0, 3);
    if (top.length) {
      const c = top[randInt(top.length)];
      pairs.push([e, c]);
      left.splice(left.indexOf(c), 1); left.splice(i, 1);
    } else if (waited > HOUSE_AFTER_S) { house.push(e); left.splice(i, 1); }
    else i++;
  }
  return { pairs, house, rest: left };
}

// Elo, K = 32. Returns the rounded change for a (b's is the negative).
export function elo(ra: number, rb: number, scoreA: number) {
  return Math.round(32 * (scoreA - 1 / (1 + 10 ** ((rb - ra) / 400))));
}

// Self-check: `npx tsx src/match.ts`
if (process.argv[1]?.endsWith("match.ts")) {
  const assert = (await import("node:assert")).strict;
  const now = 1_000_000;
  const q = (botId: string, ownerId: string, rating: number, waitedS = 0, o: Partial<QEntry> = {}): QEntry =>
    ({ botId, ownerId, league: "open", rating, level: 1, stakeCents: 25, joinedAt: now - waitedS * 1000 - botId.length, ...o });
  const none = () => null;
  // never the same owner, never across stakes or leagues
  assert.equal(pass([q("a", "u1", 1000), q("b", "u1", 1000)], now, none).pairs.length, 0);
  assert.equal(pass([q("a", "u1", 1000), q("b", "u2", 1000, 0, { stakeCents: 10 })], now, none).pairs.length, 0);
  assert.equal(pass([q("a", "u1", 1000), q("b", "u2", 1000, 0, { league: "verified" })], now, none).pairs.length, 0);
  // window: 150 at 0 s, widens 25/s, capped at 800
  assert.equal(pass([q("a", "u1", 1000), q("b", "u2", 1200)], now, none).pairs.length, 0);
  assert.equal(pass([q("a", "u1", 1000, 3), q("b", "u2", 1200)], now, none).pairs.length, 1); // 150 + 75 = 225
  assert.equal(windowFor(1000), 800);
  // previous opponent: blocked until the older one waited > 60 s
  const prev = (id: string) => ({ a: "b", b: "a" } as Record<string, string>)[id];
  assert.equal(pass([q("a", "u1", 1000, 10), q("b", "u2", 1000)], now, prev).pairs.length, 0);
  assert.equal(pass([q("a", "u1", 1000, 61), q("b", "u2", 1000)], now, prev).pairs.length, 1);
  // house fallback after 25 s with nobody to fight
  const h = pass([q("a", "u1", 1000, 26), q("b", "u2", 1000, 3, { stakeCents: 50 })], now, none);
  assert.deepEqual([h.house.map((e) => e.botId), h.rest.map((e) => e.botId)], [["a"], ["b"]]);
  // fairness: crypto draw spreads over the 3 closest, never the 4th
  const field = [q("me", "u0", 1000, 5), q("c1", "u1", 1010), q("c2", "u2", 990), q("c3", "u3", 1030), q("c4", "u4", 1100)];
  const seen: Record<string, number> = {};
  for (let i = 0; i < 3000; i++) { const p = pass(field, now, none).pairs[0]; seen[p[1].botId] = (seen[p[1].botId] ?? 0) + 1; }
  assert.equal(seen.c4, undefined);
  for (const c of ["c1", "c2", "c3"]) assert.ok(seen[c] > 800, `top-3 spread ${JSON.stringify(seen)}`);
  // every entry ends up exactly once in pairs/house/rest, pairs leave together
  const big = Array.from({ length: 40 }, (_, i) => q(`b${i}`, `u${i % 7}`, 800 + ((i * 37) % 500), i % 30, { stakeCents: [10, 25, 50][i % 3] }));
  const r = pass(big, now, none);
  const ids = [...r.pairs.flat(), ...r.house, ...r.rest].map((e) => e.botId);
  assert.equal(new Set(ids).size, big.length); assert.equal(ids.length, big.length);
  for (const [a, b] of r.pairs) {
    assert.ok(a.ownerId !== b.ownerId && a.stakeCents === b.stakeCents && a.league === b.league);
    assert.ok(Math.abs(a.rating - b.rating) <= windowFor((now - a.joinedAt) / 1000));
  }
  assert.equal(elo(1000, 1000, 1), 16); assert.equal(elo(1000, 1000, 0), -16); assert.equal(elo(1200, 1000, 1), 8);
  console.log("match.ts self-check ok", JSON.stringify(seen));
}
