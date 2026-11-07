# Hedera in Iron Corner

Everything uses the Hiero JS SDK and x402, with no Solidity. The links point at the commit we submitted.

## x402 payments

| What | Code |
|---|---|
| Paid routes `POST /arena/hit` and `POST /arena/scout`: price and `payTo` set per request | [`hedera.ts` L180-204](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L180-L204) |
| Which hit a payment is for | [`hedera.ts` L175-179](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L175-L179) |
| Each bot's own x402 client, signing with its own key | [`hedera.ts` L78-85](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L78-L85) |
| Background payment queue, so a fight never waits on settlement | [`hedera.ts` L209-220](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L209-L220) |
| Settle one hit: fight-budget cap, retry, KO when the bot can't pay | [`hedera.ts` L222-253](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L222-L253) |
| A bot buying a scouting report on its own | [`hedera.ts` L257-269](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L257-L269) |
| When a bot decides to scout | [`engine.ts` L247-262](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/engine.ts#L247-L262) |
| Payment failure ends the fight as a KO | [`engine.ts` L265-270](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/engine.ts#L265-L270) |

Example settled payment: [`0.0.9185802@1793980146.210053593`](https://hashscan.io/testnet/transaction/0.0.9185802-1793980146-210053593).

## Accounts

| What | Code |
|---|---|
| One testnet account per fighter, key sealed per bot | [`hedera.ts` L101-116](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L101-L116) |
| House faucet top-up | [`hedera.ts` L119-127](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L119-L127) |
| Balances from the Mirror Node | [`hedera.ts` L149-160](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L149-L160) |

## HCS (fight ledger)

| What | Code |
|---|---|
| Topic setup | [`hedera.ts` L37-76](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L37-L76) |
| Every start, hit, payment, scout, KO and episode root goes to topic 0.0.11326019 | [`hedera.ts` L164-170](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L164-L170) |

## HTS (IRON NFTs)

| What | Code |
|---|---|
| Create the collection and mint the Mumbai Belt (serial 1) | [`hedera.ts` L282-293](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L282-L293) |
| Mint a fighter card to the fighter's account | [`hedera.ts` L294-303](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L294-L303) |
| HIP-412 metadata pointing at the 0G brain | [`hedera.ts` L307-320](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L307-L320) |
| Move the belt to whoever beats its holder | [`hedera.ts` L322-338](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/hedera.ts#L322-L338) |

Collection: [`0.0.11341901`](https://hashscan.io/testnet/token/0.0.11341901). Ledger: [`0.0.11326019`](https://hashscan.io/testnet/topic/0.0.11326019).
