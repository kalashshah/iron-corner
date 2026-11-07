# ENSv2 in Iron Corner

Every fighter is a subname of `ironcorner.eth` on ENSv2 (Sepolia). The links point at the commit we submitted.

| What | Code |
|---|---|
| Register `ironcorner.eth`, deploy its UserRegistry and the parent resolver | [`ens.ts` L65-98](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L65-L98) |
| Deploy through the VerifiableFactory and read the proxy from `ProxyDeployed` | [`ens.ts` L50-54](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L50-L54) |
| Keys split by Enhanced Access Control: arena keys vs owner keys | [`ens.ts` L37-38](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L37-L38) |
| The fight records the arena writes | [`ens.ts` L101-105](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L101-L105) |
| Issue a fighter's name: its own PermissionedResolver, setter roles, subname owned by the fighter's key | [`ens.ts` L107-131](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L107-L131) |
| Update the record after every fight | [`ens.ts` L135-145](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L135-L145) |
| Owner edits the bio, signed by the fighter's own key | [`ens.ts` L148-156](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L148-L156) |
| Read through the Universal Resolver | [`ens.ts` L159-163](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/ens.ts#L159-L163) |
| Name issued when a fighter is created | [`server.ts` L276-284](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/server.ts#L276-L284) |
| Sync called at the end of each fight | [`engine.ts` L272-293](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/backend/src/engine.ts#L272-L293) |
| Name and records shown in the game | [`Flow.cs` L373-440](https://github.com/kalashshah/iron-corner/blob/e1cbfc1e53de3a8a6f88628b741dc3f4dba721ba/unity/Assets/Scripts/Flow.cs#L373-L440) |

Example name: [`kal.ironcorner.eth`](https://sepolia.app.ens.domains/kal.ironcorner.eth). What we ran into is in [`docs/ens-feedback.md`](../ens-feedback.md).
