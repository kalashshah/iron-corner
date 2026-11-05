# Iron Corner backend

One Express process on port 8787. It implements the contract in `../docs/spec.md`. State lives in memory and in `data/state.json` (gitignored).

## Run

```sh
npm install
npm start                    # tsx src/server.ts
npm run smoke                # full loop against http://127.0.0.1:8787
npm run check                # game rules self-check
```

Secrets load from `backend/.env` and then `~/.iron-corner/wallets.env`. Variables already set in the shell win. Every name is listed in `.env.example`.

Deploy: `docker build -t iron-corner-backend .` then run it with the env vars set (Fly.io: `fly secrets set ...`). Set `SELF_URL` if the server cannot reach itself on `http://127.0.0.1:$PORT`, because the bot payers call `/arena/hit` on it. Keep `data/` on a volume, or set `BRAIN_SECRET`; without it a fresh secret is generated and older brains on 0G can no longer be decrypted.

## Modes

Each sponsor integration runs live when its credentials are present and mock otherwise. Force either with `live` or `mock`. Startup prints the mode of each one, and `GET /health` returns them. Mock responses carry `"mock": true`.

| Var | Live needs | Mock does |
|---|---|---|
| `ZG_MODE` | `ZG_API_KEY` (0G router, `qwen-flash` + `whisper-large-v3`) | deterministic planner; STT returns the form field `text` (or `"block"`) |
| `ZG_STORAGE_MODE` | `OG_OPS_PRIVATE_KEY` (Galileo testnet gas) | AES-GCM blobs kept in the state file |
| `HEDERA_MODE` | `HEDERA_OPERATOR_PRIVATE_KEY`, `BOT_A_PRIVATE_KEY`, `BOT_B_PRIVATE_KEY` | payments settle after 300 ms with `mock-...` tx ids; no HCS |
| `WORLD_MODE` | `WORLD_CLIENT_ID` (+ `WORLD_CLIENT_SECRET` if the client has one) | approvals turn `approved` after `MOCK_APPROVE_MS` (3 s) |

On the first live Hedera start the operator creates both bot accounts (2 HBAR each, unlimited auto token associations) and the HCS ledger topic, then saves their ids in `data/`. Bots under 1 HBAR get topped up, and bots get 1 USDC when the operator holds some.

`PAY_ASSET=hbar` (default) pays damage in testnet HBAR at `TINYBAR_PER_CENT` (1,000,000, so 1 cent = 0.01 HBAR). Switch to `PAY_ASSET=usdc` once testnet USDC (`0.0.429274`) reaches the operator.

Facilitator: `X402_FACILITATOR_URL` (default `https://x402.org/facilitator`; backup `https://api.testnet.blocky402.com`).
