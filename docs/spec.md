# Iron Corner — build spec (contract between backend and Unity)

Robot boxing. You teach your bot in training (voice or text, turned into brain rules by 0G); fights are fully
autonomous: each bot fights on its own using a 0G model and the rules it learned; every hit it takes
pays the opponent through x402 on Hedera; high-stakes actions need a fresh World ID approval.
All testnet. Never say betting or gambling.

## Pieces

| Piece | Where | Owns |
|---|---|---|
| Backend | `backend/` Node 22 + TypeScript + Express, one process | All keys. 0G inference + storage, x402 arena + bot payers, HCS ledger, World approvals, bot state |
| Game | `unity/` Unity 6000.3.26f1, URP, landscape, iOS + Android + Mac | Ring, robots, animation, hit detection, UI, mic capture. Plays plans; never decides strategy |

Unity talks only to the backend over HTTPS/JSON. Base URL is a setting (default `http://localhost:8787`).
No keys in Unity.

## Moves (fixed list, both sides use these names)

`jab`, `cross`, `hook_l`, `hook_r`, `uppercut`, `body`, `block`, `dodge_l`, `dodge_r`, `step_in`, `step_back`

Attack damage (USDC cents paid by the defender per clean hit): jab 1, cross 2, hook_l/hook_r 3, uppercut 4, body 2.
A blocked hit pays 0 but chips guard. A dodged hit misses.

## Money = health

- Each bot has a Hedera testnet account holding testnet USDC (`0.0.429274`).
- Until testnet USDC is funded, the backend pays in HBAR (`PAY_ASSET=hbar`, default): 1 cent of damage = 0.01 testnet HBAR
  (`TINYBAR_PER_CENT`). Budgets and the API stay in cents either way.
- Fight stake: each bot brings a **fight budget** (default $0.50). Health bar = budget left.
- On a clean hit, the backend makes the defender bot's agent pay the attacker bot via the x402 `POST /arena/hit` route
  (`payTo` = attacker account, price = damage). Payments run in the background; the health bar moves at once.
- A bot whose budget can't cover a hit, or whose payment fails, is **knocked out**.
- Every hit and payment is logged to the HCS fight-ledger topic.

## Brain (0G)

Bot memory, encrypted (AES-256) on 0G Storage. Each save = new version with a root hash.

```json
{ "botId": "bot_a", "version": 3,
  "phrases":  [{ "say": "light him up", "means": "fast jab combos", "moves": ["jab","jab","cross"] }],
  "counters": [{ "when": "hook_l", "moves": ["dodge_r","uppercut"], "said": "when he throws a left hook, slip right and uppercut" }],
  "combos":   [{ "use": "opener|finisher|any", "name": "double jab cross", "moves": ["jab","jab","cross"], "said": "open with jab jab cross" }],
  "lessons":  ["body hooks land on tall bots", "uppercuts keep missing"],
  "scouting": { "bot_b": ["drops guard after a hook"] },
  "style": { "aggression": 0.6, "guard": 0.5 } }
```

Rule ids are 1-based positions: `counter#1`, `combo#2`, `phrase#1`. Caps: 12 counters (one per attack, newest wins),
10 combos, 30 phrases. Style values stay in 0..1; one coaching line moves each by at most ±0.2.

Decision call (0G `qwen-flash` (JSON asked in the prompt; it has no `response_format` provider), max_tokens 110,
measured median ~1.2 s, max ~2.4 s) gets the rules as one compact line each plus a hint when one fits the moment
(opponent just threw a countered move, fight start → opener, opponent ≤ 12 c → finisher). It returns a **plan**:

```json
{ "stance": "aggressive|defensive|counter", "combo": ["dodge_r","uppercut"],
  "trigger": "now|after_block|when_close", "say": "short line the bot 'thinks'",
  "used": "counter#1", "usedText": "counter: hook_l → dodge_r, uppercut",
  "requestId": "0G x_0g_trace.request_id", "teeVerified": true }
```

`used`/`usedText` appear only when the rule exists and its moves appear in order in the combo (a model "none" whose
combo is exactly a taught sequence is attributed to that rule). Unity shows "Using: <usedText> (learned in training)".

**What breaks without 0G:** no new rules are learned (coaching returns `learned: null`), and every fight decision is a
safe stop, so the bots just cover up. There is no local fallback planner in live mode.

**Safe stop:** if 0G fails or takes > 4 s (Unity keeps playing the current combo meanwhile), the backend returns `{ "stance": "safe_stop" }` and logs it.
Unity makes the bot cover up (block) and hold still until the next good plan. Never invent a fake plan.

## API

All JSON unless noted. Errors: `{ "error": "message" }` with a 4xx/5xx status.

### Bots
- `GET /health` → `{ ok, modes: {zg, storage, hedera, world}, payAsset, ledgerTopic }`
- `GET /bots` → `[{ botId, name, house, accountId, evmAddress, usdc, hbar, payAsset, brainVersion, brainRoot, alive, daily: { goal, drills, done, streak } }]`
  (`usdc`/`hbar` in whole units; `payAsset` is `"usdc"` or `"hbar"`, see Money below)
- `GET /bots/:id` → bot + `brain` (decrypted, for the owner UI) + `brainHistory: [{version, root, txHash, at}]`
- `GET /bots/:id/brain?root=0x…` → `{ root, brain }` downloaded from 0G Storage and decrypted (latest root if omitted). Brain screen "verify" button.
- `POST /bots/:id/revive` `{ approvalId }` → `{ botId, alive: true }`. Needs an approved `revive` approval. A KO sets the loser's
  `alive=false`; the owner's bot must be revived before its next fight (409 otherwise). The house bot revives itself.

### Training (sparring)
- `POST /train/:botId/coach` — `multipart/form-data` field `audio` (wav, 16 kHz mono, ≤ 10 s)
  → `{ text, requestId, sttRequestId, command: "<move>" | null, diff: "Learned counter: hook_l → dodge_r, uppercut" | null,
       learned: { kind: "phrase"|"counter"|"combo"|"style", id, say, means, moves, when?, use?, name?, aggression?, guard? } | null,
       daily: { goal, drills, done, streak, justDone } }`
  In mock STT mode an optional form field `text` stands in for the transcript (default `"block"`).
  0G `qwen3-omni-flash` transcribes (audio as a chat input; 0G dropped whisper on Nov 6). Then: a command word (`"jab"`, `"back off"`) is an instant sparring drill (`command`, no model);
  a known phrase is drilled; anything else goes to 0G (qwen-flash), which classifies it into one brain update:
  `{"kind":"counter","when":"hook_l","moves":[…]}`, `{"kind":"combo","use":"opener","name":…,"moves":[…]}`,
  `{"kind":"style","aggression":0.15,"guard":-0.05}` (deltas) or `{"kind":"phrase","means":…,"moves":[…]}`.
  Validated strictly (moves from the fixed list, counters only against attacks); invalid → `learned: null`.
  `requestId` is the classifier's 0G id (falls back to the speech-to-text id).
- `POST /train/:botId/coach-text` `{ text }` → same response without `sttRequestId` (typing fallback, and for tests).
- `POST /train/:botId/spar` `{ state }` → plan (same shape as fight decide) for your bot's sparring round. The dummy is
  local in Unity and throws the attacks your counters answer, so you see the counters fire.
- **Daily goal:** 3 drills a day (any coaching call that drilled or taught something). Hitting it on consecutive UTC days
  grows `streak`; missing a day resets it. Shown on Home (`GET /bots` → `daily`).
- `POST /train/:botId/save` → `{ version, root, txHash }` saves the brain to 0G Storage.

### Fight
- `POST /fights` `{ a: "bot_a", b: "bot_b", stakes: "normal" | "high", budgetUsd?: 0.5 }`
  → `{ fightId, a, b, budgets: {bot_a: 50, bot_b: 50} }` (cents). `high` requires an approved approval id: `approvalId`.
- `POST /fights/:id/decide` `{ botId, state }` → plan. `state`:
  `{ t, selfBudget, oppBudget, distance, selfGuard, oppGuard, oppLastMoves: [...], selfLastMoves: [...], lastHitBy: "<move>|null" }`
- `POST /fights/:id/hit` `{ attacker, defender, move, result: "clean"|"blocked"|"dodged" }`
  → `{ budgets, ko: "bot_b"|null }` — returns immediately; payment queued.
- Fights are autonomous. `POST /fights/:id/coach` and `/fights/:id/coach-text` return **409** `{ "error": "fights are autonomous; coach in training" }`.
- `GET /fights/:id` → `{ status: "live"|"over", budgets, winner, payments: [{ hitNo, from, to, cents, status: "pending"|"settled"|"failed", txId, hashscan }], ledgerTopic, decisions: [{botId, requestId, stance, used?, at}] }`
- `POST /fights/:id/end` → lessons summarised by 0G; `{ winner, lessons: {bot_a:[...], bot_b:[...]}, brainSave: "queued" }`.
  The two 0G Storage uploads take ~15 s each, so they run in the background; new versions appear in `GET /bots/:id` `brainHistory`.
  Calling it again returns the same lessons.

### Approvals (World ID Agent Pass / World IdP device flow)
- `POST /approvals` `{ botId, action: "high_stakes"|"revive"|"cash_out" }`
  → `{ approvalId, qrUri, userCode, expiresAt }` — Unity shows a QR of `qrUri` + the code + the action text.
- `GET /approvals/:id` → `{ status: "pending"|"approved"|"denied"|"expired"|"cancelled"|"used" }`
- `GET /approvals/:id/qr.png` → PNG of the QR for `qrUri` (so Unity needs no QR library)
- `POST /approvals/:id/cancel`
- Approved approvals are single-use: the action marks them `used` in the same step that runs it.

### Arena (x402, called by the backend's own bot agents, not by Unity)
- `POST /arena/hit` — x402 `exact` on `hedera:testnet`, dynamic `payTo`/`price` from the queued hit.

## Modes (env)

Every sponsor integration has a mock so the whole loop runs offline:
`ZG_MODE=live|mock`, `ZG_STORAGE_MODE=live|mock`, `HEDERA_MODE=live|mock`, `WORLD_MODE=live|mock`.
Mock responses are clearly marked (`"mock": true`). The demo runs all-live.

## Unity screens (landscape)

1. **Home** — your bot card (name, USDC, brain version, daily goal "2/3 drills · 4-day streak"), buttons Train / Fight / Brain.
2. **Train** — your bot spars a dummy that throws the attacks you've taught counters for; big hold-to-talk mic button; transcript line;
   diff toast ("Learned counter: hook_l → dodge_r, uppercut") with the 0G request id; bubble shows "Using: … (learned in training)"
   when a sparring plan follows a rule; Save Brain button showing the new root hash.
3. **Fight** — autonomous, no coaching controls. Two robots in a ring; health bars = budgets in $; payment ticker (each hit: `-$0.03 → 0.0.x  ✓ settled` with HashScan link); thought bubble from plan `say` plus "Using: <rule> (learned in training)"; 0G request id small text; KO screen with ledger link.
4. **Approval modal** — action text ("Enter high-stakes match"), QR code, user code, live status; denied/expired shows the action did not happen.
5. **Brain** — version history with root hashes, then counters, combos, phrases, lessons.
