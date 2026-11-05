# New player flow and live fights (contract for backend + Unity)

Extends `docs/spec.md`. Testnet only; no real money anywhere.

## The flow a new player sees

1. **Welcome** → "Create your fighter".
2. **Create**: name (2-20 chars, letters/digits/spaces) and one of 6 paint schemes (brass, gunmetal, crimson, cobalt, jade, bone). The server creates the bot: Hedera testnet account, 0G Agentic ID token, empty brain.
3. **Connect World ID** (optional but shown): World ID device-flow approval ("Sign in to Iron Corner"). Approved → the player is a verified human; one verified human owns at most one fighter in the Verified league. "Skip for now" → Open league.
4. **First training round (required)**: one 30 s shadow-sparring round (existing Train screen) plus at least one coaching line. Until done, the queue refuses the bot with `409 needs_training`.
5. **Fund**: "Add test funds" tops the bot's wallet up from the house faucet (testnet HBAR, server-side transfer). Cap: once per bot per day, up to 0.5 HBAR, and only while the bot holds < 0.5 HBAR.
6. **Find a fight**: pick a stake ($0.10 / $0.25 / $0.50 of fight budget). The bot joins the matchmaking queue. The app shows "Searching… (n in queue)".
7. **Live fight**: the server runs the fight; the app plays the event stream. Both bots fight on their own (no coaching mid-fight). Every clean hit pays the other corner over x402. KO or time → result, rating change, lessons, brain saved.

## Identity

- `POST /users` → `{userId, token}`. The app stores the token (PlayerPrefs) and sends `Authorization: Bearer <token>` on every owner call. Tokens are random 32-byte values; the server keeps only their SHA-256.
- `GET /me` → `{userId, worldVerified, league, botId|null}`.
- `POST /auth/world/start` → approval `{approvalId, qrUri, userCode, expiresAt}` (action `sign_in`). `GET /approvals/:id` as today. When approved, the server sets `user.worldSub` (rejects if that World subject already owns a verified fighter: the approval ends `denied` with `error: "world_already_used"`; `POST /bots` by a user whose subject is already behind another fighter gets `409 world_already_used`).
- Errors: `{error, reason?}`. Every 409 in this file carries `reason` (same string as `error`), e.g. `{"error":"needs_training","reason":"needs_training"}`. `401` = missing/unknown token, `429` = slow down.
- Owner checks: every `/bots/:id/...` write and every queue call must come from the bot's owner (`403` otherwise). Existing `bot_a`/`bot_b` stay as the demo/house bots.

## Bots

- `POST /bots {name, paint}` (one per user) → bot view. Server: validates name, creates Hedera account (operator, 0.3 HBAR initial, unlimited auto-associations), mints the Agentic ID (contract `0x4Ed79445a389C9442657773138e2ffFabb25d170`, data = brain root once saved), rating 1000.
- Bot view adds: `paint`, `owner` (userId), `league`, `rating`, `record {w,l}`, `onboarding {trained, funded}`, `agentId {tokenId, contract, tx}`.
- `POST /bots/:id/fund` → `{hbar, txId, hashscan}` or `409 {reason}`: `already_funded_today`, `balance_high`, `faucet_empty`, `in_progress`, `demo_bot`. Optional `Idempotency-Key` header: a repeat with the same key returns the first result (`repeat: true`).
- `POST /bots` refusals: `409 one_bot_per_user`, `409 arena_full` (this testnet build caps player bots at 8; the operator pays 0.3 HBAR each), `400` bad name/paint.
- `agentId` also has `chainscan`, `version` (last brain version recorded on chain) and `retry` (`"mint"|"record"|null`; the server retries every minute). Minting runs in the background after `POST /bots`, so `tokenId` is `null` for a few seconds.
- Training writes (`/train/:botId/*`), `revive`, `fund`, `camp` and queue calls on a player's bot need its owner's token. `bot_a`/`bot_b` have no owner and stay open.

## Matchmaking (`POST /queue {botId, stakeCents}`, `DELETE /queue/:botId`, `GET /queue/:botId`)

Queue entry: `{botId, ownerId, league, rating, stakeCents, joinedAt}`. One entry per bot; joining twice is a no-op. A bot must be alive, trained, and hold enough balance for its stake (the stake is reserved while queued and during the fight).

The matcher runs every 2 s (single process, so each pass is atomic):
1. Oldest entry first. Candidates: same league and stake, different owner, not its previous opponent (unless waiting > 60 s).
2. Rating window widens with waiting: `150 + 25 × seconds waited`, capped at 800.
3. Among candidates inside the window, take the 3 closest by rating and pick one with `crypto.randomInt` (fair and unpredictable, never "always the closest").
4. Both entries leave the queue in the same step; a fight is created.
5. Waited > 25 s with no candidate → matched with a house sparring bot whose level is close to the player's (labelled "House" in the app, no rating change).

`GET /queue/:botId` → `{status: "searching"|"matched"|"idle", waitedSec, queueSize, fightId?, house?}` ("matched" while the fight is live and for 2 min after it was made).
`POST /queue` refusals: `409 needs_training`, `repairing` (with `repairIn`), `needs_revive`, `in_fight`, `insufficient_funds` (with `haveCents`, `needCents`: stake + 5 c for fees). Stakes: `stakeCents` 10, 25 or 50.
House bots: `bot_b` plus `house_2` (level 3) and `house_3` (level 6), which pay from `bot_b`'s Hedera account. Win/loss record counts house fights; rating doesn't.

Ratings: Elo, K = 32, updated at fight end (not for house fights).

## Server-authoritative fight

- `POST /fights` stays for the demo; matched fights are created by the matcher.
- Fairness: at creation the server draws a random 32-byte seed and publishes `seedHash = sha256(seed)` in the fight; the seed is revealed in the result. All randomness in the fight comes from a PRNG on that seed, so anyone can replay the fight from the episode log and the seed.
- Engine loop per fight, about every 1.5 s ("exchange"):
  1. Both bots decide in parallel with 0G (`zg.decide`, recall of their owner's demos, safe_stop on timeout).
  2. Each plan becomes up to 3 moves; the engine resolves them pairwise in time order with simple, documented rules: distance (step_in/step_back), attacks only land within reach, defender's block → blocked (guard chips), dodge vs attack → dodged with p = 0.75, otherwise clean with p depending on guard and distance; damage = the existing cents table.
  3. Clean hits are paid over x402 exactly as today (background queue) and logged to HCS.
  4. KO when a budget hits 0; time limit 90 s → winner has more budget left (draw if equal).
- Resolution rules (exact, `backend/src/engine.ts`): start distance 2.0, reach 1.4. Slot i pairs each bot's i-th move. Steps first (step_in −0.5, step_back +0.5, distance 0.8..3). Attack out of reach → whiff (no `hit` event). Defender `block` → blocked, guard −0.15. Defender dodge → dodged with p 0.75, else clean. Otherwise clean with p = 0.45 + 0.4 × (1 − guard) + 0.1 if distance ≤ 1, else blocked (guard −0.05). Guard +0.1 per exchange. `safe_stop` = block. A `when_close` plan out of reach opens with step_in. Fight time advances 1.5 s per exchange, so 60 exchanges = 90 s.
- Events: `GET /fights/:id/events?since=<seq>` → `{events:[{seq, t, type, ...}], status, budgets, winner, seedHash, seed?}`. Types: `start {a, b, budgets, seedHash, stakeCents, house}`, `plan {botId, stance, combo, trigger, moves, say, used, usedText, requestId}`, `move {botId, move}`, `hit {attacker, defender, move, result, cents}`, `pay {hitNo, from, to, cents, status, txId, hashscan}`, `ko {loser, reason?}`, `end {winner, reason: "ko"|"time"|"payment_failed"|"server_restart", ratingDelta, budgets}`. `pay` events can arrive after `end` (payments settle in the background). `t` is fight time in seconds; move/hit events of one exchange sit at t + 0.3/0.7/1.1.
- Replay: `replay(seed, a, b, stakeCents, planEvents)` in `engine.ts` rebuilds every plan/move/hit/ko event. `GET /fights/:id` adds `engine, house, stakeCents, seedHash, seed (when over), endReason, ratingDelta`. The client-driven `/decide`, `/hit` and `/end` (while live) answer 409 on server-run fights.
- The app plays events in order (Fighter in scripted mode: `SetQueue([move])`, `TakeHit`, block/dodge animations), with about 1 s of buffer.

## Fight camp (LoRA on 0G)

- `POST /bots/:id/camp` → starts a fine-tune of this bot's own adapter on 0G Compute from its demos (needs ≥ 40 demos; one camp per bot per day; global budget guard). `GET /bots/:id/camp` → `{status, taskId, progress, version, adapterRoot?, loss?}`.
  - `status`: `none|queued|uploading|training|delivered|downloading|done|failed` (`error` on failure); `progress` is 0G's task progress string. Refusals: `409 not_enough_demos` (`have`, `need`; got_hit demos don't count), `camp_today`, `camp_busy` (one camp at a time server-wide), `camp_budget` (3 real camps a day).
  - Real camps run only with `CAMP_MODE=live` (they spend testnet 0G); otherwise the same statuses run mocked (`mock: true`).
  - Dataset: Alpaca `instruction/input/output`, state in words exactly like `camp/camp.mjs` `stateLine` (shared with serving), successful demos repeated, `got_hit` dropped.
  - Fights keep using qwen-flash + recall. Serving the adapter needs 2 0G on the provider; `ZG_ADAPTER_SERVING=on` turns on the hook in `zg.decide` (untested).
- Each finished camp is a new adapter version, recorded on the bot's Agentic ID.
