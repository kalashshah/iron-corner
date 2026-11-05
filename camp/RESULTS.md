# Fight camp results (LoRA on 0G Compute)

Every camp is a real fine-tuning task on 0G Compute (Galileo testnet, Qwen2.5-0.5B-Instruct, LoRA r=8 on all attention and MLP projections). Score = how often the first move matches the coach on 24 fight situations the model never saw. Base model with no adapter: 0/24.

| Camp | Task id | Data | Steps | Fee (0G) | Train time | Score |
|---|---|---|---|---|---|---|
| v1 | `5be0b857-10d1-4d3d-bd6f-f12887a5d913` | 150 chat examples, long system prompt | 120 | 0.023 | ~6 min | 1/24 |
| v2 | `21df8938-5929-4a09-9801-7581bf15cf45` | 600 instruction examples, numeric state | 300 | 0.078 | ~8 min | 13/24 |
| v3 | `c74f6ca5-17f1-49f9-8914-221006c028cf` | 900 instruction examples, state in words | 400 | ~0.1 | ~9 min | 17/24 |

v3 by situation: left-hook counter 7/7, right-hook counter 3/3, body shot 3/5, far away 2/3, opponent almost broke 1/1, jab 0/4 (jab is under-represented; it falls back to "step_in jab cross").

What made the difference:
- Instruction/input/output data, scored with the Alpaca layout 0G's trainer uses (v1 learned only the repeated prompt).
- The fight state in words (distance=far, opp_money=almost_broke) instead of numbers, which a 0.5B model compares badly.

Acknowledgement transactions on Galileo: v1 `0x690104ee…c929`, v2 `0xf81ee426…1815`, v3 `0x163726c1…f196`.
Reproduce: `node camp.mjs dataset --synthetic coach.jsonl && node camp.mjs start coach.jsonl`, then `status`, `ack`, and `python eval_adapter.py <adapter dir> words`.
