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
- `POST /auth/world/start` → approval `{approvalId, qrUri, userCode, expiresAt}` (action `sign_in`). `GET /approvals/:id` as today. When approved, the server sets `user.worldSub` (rejects if that World subject already owns a verified fighter: `409 world_already_used`).
- Owner checks: every `/bots/:id/...` write and every queue call must come from the bot's owner (`403` otherwise). Existing `bot_a`/`bot_b` stay as the demo/house bots.

## Bots

- `POST /bots {name, paint}` (one per user) → bot view. Server: validates name, creates Hedera account (operator, 0.3 HBAR initial, unlimited auto-associations), mints the Agentic ID (contract `0x4Ed79445a389C9442657773138e2ffFabb25d170`, data = brain root once saved), rating 1000.
- Bot view adds: `paint`, `owner` (userId), `league`, `rating`, `record {w,l}`, `onboarding {trained, funded}`, `agentId {tokenId, contract, tx}`.
- `POST /bots/:id/fund` → `{hbar, txId}` or `409 {reason}`.

## Matchmaking (`POST /queue {botId, stakeCents}`, `DELETE /queue/:botId`, `GET /queue/:botId`)

Queue entry: `{botId, ownerId, league, rating, stakeCents, joinedAt}`. One entry per bot; joining twice is a no-op. A bot must be alive, trained, and hold enough balance for its stake (the stake is reserved while queued and during the fight).

The matcher runs every 2 s (single process, so each pass is atomic):
1. Oldest entry first. Candidates: same league and stake, different owner, not its previous opponent (unless waiting > 60 s).
2. Rating window widens with waiting: `150 + 25 × seconds waited`, capped at 800.
3. Among candidates inside the window, take the 3 closest by rating and pick one with `crypto.randomInt` (fair and unpredictable, never "always the closest").
4. Both entries leave the queue in the same step; a fight is created.
5. Waited > 25 s with no candidate → matched with a house sparring bot whose level is close to the player's (labelled "House" in the app, no rating change).

`GET /queue/:botId` → `{status: "searching"|"matched"|"idle", waitedSec, queueSize, fightId?}`.

Ratings: Elo, K = 32, updated at fight end (not for house fights).

## Server-authoritative fight

- `POST /fights` stays for the demo; matched fights are created by the matcher.
- Fairness: at creation the server draws a random 32-byte seed and publishes `seedHash = sha256(seed)` in the fight; the seed is revealed in the result. All randomness in the fight comes from a PRNG on that seed, so anyone can replay the fight from the episode log and the seed.
- Engine loop per fight, about every 1.5 s ("exchange"):
  1. Both bots decide in parallel with 0G (`zg.decide`, recall of their owner's demos, safe_stop on timeout).
  2. Each plan becomes up to 3 moves; the engine resolves them pairwise in time order with simple, documented rules: distance (step_in/step_back), attacks only land within reach, defender's block → blocked (guard chips), dodge vs attack → dodged with p = 0.75, otherwise clean with p depending on guard and distance; damage = the existing cents table.
  3. Clean hits are paid over x402 exactly as today (background queue) and logged to HCS.
  4. KO when a budget hits 0; time limit 90 s → winner has more budget left (draw if equal).
- Events: `GET /fights/:id/events?since=<seq>` → `{events:[{seq, t, type, ...}], status, budgets, winner, seedHash, seed?}`. Types: `start`, `plan {botId, stance, moves, say, used, usedText, requestId}`, `move {botId, move}`, `hit {attacker, defender, move, result, cents}`, `pay {hitNo, txId, hashscan}`, `ko {loser}`, `end {winner, reason, ratingDelta}`.
- The app plays events in order (Fighter in scripted mode: `SetQueue([move])`, `TakeHit`, block/dodge animations), with about 1 s of buffer.

## Fight camp (LoRA on 0G)

- `POST /bots/:id/camp` → starts a fine-tune of this bot's own adapter on 0G Compute from its demos (needs ≥ 40 demos; one camp per bot per day; global budget guard). `GET /bots/:id/camp` → `{status, taskId, progress, version, adapterRoot?, loss?}`.
- Each finished camp is a new adapter version, recorded on the bot's Agentic ID.
