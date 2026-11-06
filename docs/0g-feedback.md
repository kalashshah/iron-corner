# 0G developer feedback

What we hit while building Iron Corner on 0G at ETHGlobal Mumbai (5 to 7 Nov 2026), in the order we hit it. Each item says what happened, what we did, and what would have helped. SDK version: `@0gfoundation/0g-compute-ts-sdk` 0.9.0, Node 24, macOS arm64.

## Router (0G Private Computer)

1. **`qwen-flash` rejects `response_format`.** With `response_format: {type: "json_object"}` the router answered 503 "No available providers for this request". Without it, the same call worked. We ask for JSON in the prompt instead. Helpful: a 400 that says the model doesn't support `response_format`, and a capability flag per model in `/v1/models`.
2. **Whisper disappeared mid-hackathon.** `whisper-large-v3` worked on 5 Nov and returned "Model not found" on 6 Nov; the catalog went from 124 to 79 models. We moved speech-to-text to `qwen3-omni-flash` (audio as a chat input). Helpful: a deprecation notice or a status page for model removals.
3. **Omni audio input needs a data URL.** Plain base64 in `input_audio.data` fails with "The provided URL does not appear to be valid"; `data:audio/wav;base64,...` works. The OpenAI format accepts plain base64, so this is surprising. Helpful: one line in the audio docs.
4. **No `tee_verified` in responses.** `verify_tee: true` on `qwen-flash` returns no `tee_verified` field, and every provider in `/v1/providers` is `standard`. Helpful: docs saying which models currently give TEE-verified runs through the router.
5. **`/v1/account/funds` needs a management key.** Our inference key gets `insufficient_scope`, which is fine, but the balance call is what an app would show users. Helpful: a read-only balance scope on inference keys.

## Fine-tuning (0G Compute)

6. **"Minimum balance to create a ledger is 3 0G" comes from the SDK, not always the chain.** `LedgerProcessor.MIN_LEDGER_BALANCE_OG = 3` is hard-coded for every network. On Galileo the regular testnet ledger contract (`0xE708…E406`) has `MIN_ACCOUNT_BALANCE` = 0.1 0G; only the dev ledger (`0x815B…BbF7`) needs 3. We opened the account on the regular contract directly with 0.3 0G. Helpful: read the minimum from the contract.
7. **`uploadDataset()` spawns a bundled binary that fails on macOS arm64** with `spawn ENOEXEC`. We switched to `uploadDatasetToTEE()`. Helpful: a pure-TypeScript upload path, or a clear error naming the binary and supported platforms.
8. **The ESM build breaks in Node for TEE upload** (`window is not defined`). The CommonJS build works. Helpful: environment detection that doesn't assume a browser in the ESM build.
9. **`broker.fineTuning` is silently `undefined`** when the signer is a `Wallet` from a different copy of ethers (ESM import vs the SDK's CommonJS copy), because the broker checks `signer instanceof ethers.Wallet`. Helpful: duck-typing the signer, or throwing "fine-tuning needs an ethers Wallet" instead of leaving the field undefined.
10. **Dev vs regular testnet is confusing.** The fine-tuning docs and agent skill point to `ZG_DEV_MODE=true`, but the regular testnet also has a fine-tuning provider (same H200 machine, different port) with a lower ledger minimum. Helpful: one table of networks, contracts, providers and minimums.

11. **Fine-tuning itself was smooth once started.** Task `5be0b857-10d1-4d3d-bd6f-f12887a5d913`: set up 1 min, trained 4 min, delivered 1 min; fee 0.023 0G for 150 examples, 120 steps. Loss went 5.14 → 0.034.
12. **`acknowledgeModel` warns "Hash mismatch … expected 0xabc6… got 0x0a83…"** and then acknowledges anyway. Helpful: say whether that is safe to ignore, or stop.
13. **`decryptModel` right after acknowledging fails** with "second arg must be public key"; the same call works a few minutes later. Helpful: wait/poll for the provider's key, or an error that says "secret not published yet, retry".
14. **`downloadLoRAFromTEE` saves the file, then throws** "Cannot read properties of undefined (reading 'slice')" (the return value is undefined).
15. **Serving an adapter needs 2 0G in the ledger.** `deployAdapter` auto-funds the provider sub-account with 2 0G and times out after 120 s ("adapter not found") when the ledger can't cover it. The docs don't mention this amount. Helpful: state the serving minimum, and fail fast instead of waiting 120 s.
16. **The prompt layout used for instruction/input/output training isn't documented.** We had to test layouts against our own adapter: the Alpaca template ("### Instruction / ### Input / ### Response") scored 13/24, the Qwen chat template 8–10/24 with empty answers. Serving with the wrong layout silently degrades a fine-tune. Helpful: one line in the docs, and `chatWithFineTunedModel` applying the same layout automatically.
17. **Chat-format datasets train mostly on the prompt.** Our first adapter (chat format, long system prompt) reached loss 0.034 but learned nothing usable (1/24); the loss was dominated by the repeated system prompt. Helpful: a note recommending instruction/input/output or saying whether prompt tokens are masked.

## Next to test

- Serving the adapter with `deployAdapter` + `chatWithFineTunedModel`, and its latency. On 5 Nov the provider's inference endpoint failed the TLS handshake.

## Adapter serving, second try (6 Nov)

21. **The fine-tuning provider's serving endpoint still fails the TLS handshake.** With 2.1 0G deposited, `deployAdapter` funded the provider sub-account with 2 0G (tx `0x4dc61d06ba9f8f879afed1091f6e686864f9e98afe4b8ac6015cdc6740cf5223`) and then polled for 300 s with "adapter not found". `listAdapters` fails with "Client network socket disconnected before secure TLS connection was established". Provider `0xA02b95Aa6886b1116C4f334eDe00381511E31A09`, endpoint `https://cc05c4570b455b7e5f085906f2f5e51e2b5ca431-3083.dstack-pha-in2.phala.network`. It fails the same way from a Mac in Mumbai (curl: `SSL_ERROR_SYSCALL`) and from a Fly machine in Singapore (`ECONNRESET`), so it is not our network. `deployAdapter` swallows the connection error as "not found", which hides the real cause for 5 minutes. Helpful: surface network errors in the poll loop, and a status page or health field for providers.
22. **The fine-tuning provider isn't listed as an inference service.** `inference.listService()` returns two chatbots (glm-5.3, qwen3.8-omni-flash), not `0xA02b…`, yet serving goes through `inference.deployAdapter` on that address. Helpful: say in the docs which provider serves adapters, and list it.

## Agentic ID + camp API in the game server (6 Nov)

18. **Two signers on one key collide on nonces.** Our Agentic ID mint (ethers) and a 0G Storage upload (`Indexer.upload`) from the same wallet at the same moment gave "replacement fee too low". We retry after 5 s. Helpful: let `upload()` take an ethers `NonceManager`, or document that the storage SDK picks its own nonce.
19. **Fine-tuning task `progress` values aren't listed anywhere.** We only ever saw `Finished`. To know when to call `acknowledgeModel` we watch for a deliverable with our task id in `getAccount(provider).deliverables`. Helpful: the list of progress states in the types (an enum), and which one means "ready to acknowledge".
20. **Acknowledging during `Delivering` reverts with "unknown custom error".** Our first poller treated any progress containing "deliver" as ready; `acknowledgeModel` then reverted on chain with no reason string. Waiting for exactly `Delivered` fixed it, and the task could be resumed by its id. Helpful: a named revert ("deliverable not ready") and an SDK-side check before sending the tx.
