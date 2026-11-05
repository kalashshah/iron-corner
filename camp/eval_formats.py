# Which prompt layout did 0G's trainer use for instruction/input/output? Score the v2 adapter under each.
import random, sys, torch
from transformers import AutoTokenizer, AutoModelForCausalLM
from peft import PeftModel
sys.argv=[sys.argv[0], sys.argv[1]]
exec(open("eval_adapter.py").read().split("tok=AutoTokenizer")[0])   # reuse states, line(), coach_first()
tok=AutoTokenizer.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct")
m=PeftModel.from_pretrained(AutoModelForCausalLM.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct", dtype=torch.float32), sys.argv[1]); m.eval()
I="Robot boxer: next moves for this fight state."
def chat(u, sys_=None):
    msgs=([{"role":"system","content":sys_}] if sys_ else [])+[{"role":"user","content":u}]
    return tok.apply_chat_template(msgs, add_generation_prompt=True, tokenize=False)
layouts={
 "chat: instr\\ninput": lambda s: chat(I+"\n"+line(s)),
 "chat: instr\\n\\ninput": lambda s: chat(I+"\n\n"+line(s)),
 "chat: sys=instr, user=input": lambda s: chat(line(s), I),
 "alpaca raw": lambda s: f"Below is an instruction that describes a task, paired with an input that provides further context. Write a response that appropriately completes the request.\n\n### Instruction:\n{I}\n\n### Input:\n{line(s)}\n\n### Response:\n",
 "raw instr\\ninput\\n": lambda s: f"{I}\n{line(s)}\n",
}
for name, f in layouts.items():
    hits=0; empty=0
    for s in states:
        enc=tok(f(s), return_tensors="pt")
        with torch.no_grad(): out=m.generate(**enc, max_new_tokens=10, do_sample=False)
        t=tok.decode(out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True).strip()
        empty+= t==""; hits+= (t.split() or [""])[0]==coach_first(s)
    print(f"{name:32s} {hits}/{len(states)} match, {empty} empty")
