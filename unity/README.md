# Iron Corner (Unity)

Unity 6000.3.26f1, URP, landscape. One scene (`Assets/Scenes/Main.unity`) with Home, Train, Fight and Brain modes. The game plays plans from the backend (`../backend`, see `../docs/spec.md`) and never decides strategy itself.

## Mixamo assets (not in git)

`Assets/ThirdParty/Mixamo/` is gitignored because Mixamo files can't be redistributed in a public repo. Before opening the project, copy them in:

```
Assets/ThirdParty/Mixamo/Chars/vanguard.fbx
Assets/ThirdParty/Mixamo/Chars/vanguard_tex/*.png
Assets/ThirdParty/Mixamo/Clips/<clip>.fbx   # idle_boxing_bounce, jab, cross, hook_lead, hook_rear, upper_rear,
                                            # body_hook, block_high, dodge_right, step_forward, step_back,
                                            # hit_head_*, hit_body*, hit_upper*, ko_back, victory, ...
```

`Assets/Editor/Setup.cs` sets the import rules: Humanoid rig, clips renamed from `mixamo.com` to the file name, root motion baked into the pose.

## Build the scene

Everything in `Assets/Game` and `Assets/Scenes` is generated, so the scene can be rebuilt from scratch:

```
U=/Applications/Unity/Hub/Editor/6000.3.26f1/Unity.app/Contents/MacOS/Unity
$U -batchmode -nographics -quit -projectPath . -executeMethod Setup.Build -logFile Logs/setup.log
```

This writes the bot prefabs (`bot_a` Brass Knuckle, `bot_b` Rustbucket; to swap the mesh, change the model in `Setup.Bot`), the animator controller, materials, the post-FX profile, the ring scene and the player settings. In the editor the same step is under the menu: Iron Corner > Build Scene.

## Server

The base URL is set on the Home screen and saved in PlayerPrefs (default `http://localhost:8787`). On a phone, use the laptop's LAN IP. If the server doesn't answer, the game switches to a built-in offline sim (`OfflineSim.cs`) and shows an OFFLINE badge. Every screen works in that mode, but no money moves and nothing reaches 0G, Hedera or World.

## Screenshots (capture mode)

```
$U -batchmode -nographics -quit -projectPath . -executeMethod Setup.BuildMac -logFile Logs/build.log
Builds/IronCorner.app/Contents/MacOS/Iron\ Corner -screen-width 1920 -screen-height 1080 -screen-fullscreen 0 -capture "$PWD/Captures"
# 19.5:9 phone framing
Builds/IronCorner.app/Contents/MacOS/Iron\ Corner -screen-width 2340 -screen-height 1080 -screen-fullscreen 0 -capture "$PWD/Captures" -captureTag wide
```

The player goes through home, brain, training (plus a learned phrase and a brain save), the approval modal, a short fight and the KO, saving `NN_<screen>.png` at each step before it quits. It uses the server if one answers on the saved URL. `Captures/` and `Builds/` are gitignored.

## Notes

- MSAA is off (it breaks on Metal); FXAA runs as post AA instead.
- The project uses the old Input Manager, so it doesn't need the Input System package.
- The mic records 16 kHz mono, up to 10 s, and sends it as a WAV. iOS gets `NSMicrophoneUsageDescription` from PlayerSettings. Android adds RECORD_AUDIO automatically and `Mic.cs` asks for it at runtime.
- Insecure HTTP is allowed so the dev build can reach a laptop over plain HTTP. Switch to HTTPS before a store build.
