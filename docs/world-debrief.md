# World ID Agent Pass: integration debrief

Running notes, kept during the build (ETHGlobal Mumbai, 5 to 7 Nov 2026).

## What we built

Iron Corner asks the bot's owner for a fresh World ID approval at two moments: entering a high-stakes fight (more money at risk) and reviving a knocked-out bot. The backend starts an OpenID Connect device-flow request on `auth.world.org`, the game shows the QR code and user code, and the owner approves in World App. The backend validates the ID token (issuer, subject pinned to the owner, `acr`, `amr` containing `pop`, `auth_time` after the request started) and marks the approval used in the same step that runs the action.

## Time to first success

First real device code from `auth.world.org`: about 2 h 10 min after hacking started (backend code was ready at about 2 h; the rest was portal access and the auth-method issue below). Full approval in World App: TBD.

## Friction so far

1. The portal sign-in asks for an "enterprise Google account". It was not clear whether a personal Gmail works for hackathon teams.
2. Sandbox or production: the docs point to `sandbox.auth.world.org` and a TestFlight sandbox app, while the prize page says proofs are mocked and the sandbox app is no longer needed. We switched to production `auth.world.org` after reading the prize page.
3. A confidential client must register an exact HTTPS callback even when it only uses the device flow, so we had to deploy the backend before we could register.
4. World App does not show what is being approved (no `binding_message` or action text). The game has to display "Enter high-stakes match" itself, and the user has to trust that the code on screen belongs to that action.
5. `invalid_client` on the first real request, although the client ID and secret were right. The app is registered for `client_secret_basic`, and `openid-client` v6 sends `client_secret_post` when you pass the secret as a plain string. Passing `ClientSecretBasic(secret)` fixed it. The error text ("check the environment, registered authentication method, and credentials") lists three causes without saying which one.
6. Creating the app showed "We could not create the client. The service may be temporarily unavailable", but the app had been created. Its first secret was never displayed, so we added a second one.
7. There is no nullifier in the OIDC flow, so making each approval single-use is entirely the app's job.
8. No same-phone path for native apps. Our game runs on the same iPhone as World App, so a QR code is useless there. Opening `verification_uri_complete` goes game → Safari → World App, and then the player switches back to the game by hand. The docs list no mobile SDK, universal link or return-to-app parameter for Agent Pass. IDKit's Swift/Kotlin SDKs do have a direct deep link with `return_to`.

## Missing capability or documentation

- Action text in World App for device-flow requests.
- A short "agent approval" recipe: backend-initiated request, poll, validate, consume once.

## The one improvement with the greatest impact

Show the requesting app's action text in World App. Without it, a device-flow approval proves a fresh human but not what that human agreed to.
