# ENSv2 notes (ETHGlobal Mumbai)

Issues we ran into while putting every Iron Corner fighter under `ironcorner.eth` on Sepolia.

1. **Stale Sepolia addresses.** `contracts/docs/addresses/sepolia.md` on `main` of contracts-v2 lists a June 2026 deployment that is no longer live. The live set is the 2026-10-01 redeploy (commit `07e55a05`, `contracts/deployments/sepolia/*.json`), which matches docs.ens.domains/learn/deployments. We confirmed it on chain through the Universal Resolver proxy and `getSubregistry("eth")`.
2. **Stale payment tokens.** The test USDC/DAI addresses linked from `/web/ensv2-readiness` return `false` for `isPaymentToken` on the live registrar. MockUSDC `0x240b…14aa` works, and anyone can mint it.
3. **Per-key roles cover the whole resolver.** A setter role is per key, but it isn't scoped per name. If fighters share one resolver, one owner can edit every fighter's `description`, so we deploy one PermissionedResolver per fighter. A sentence in the Enhanced Access Control docs would save people from finding this out the hard way.
4. **`grantSetterRoles` can't run inside `initialize`.** It checks the caller's admin role, and during init the caller is the factory. So setting up roles takes a second transaction (one multicall), after the initial records are written in `initialize`'s `calls`.
5. **No SDK path for registry writes.** viem resolves v2 names fine through the Universal Resolver (2.35 or later). Deploying a UserRegistry or resolver through the VerifiableFactory and issuing subnames meant hand-encoding calls from the deployment ABIs.
6. **The proxy address comes from an event.** `deployProxy` returns the proxy address, but from a sent transaction you have to parse `ProxyDeployed` from the receipt to get it.
