import sys, torch
from transformers import AutoTokenizer, AutoModelForCausalLM
from peft import PeftModel
sys.argv=[sys.argv[0], sys.argv[1], "words"]
exec(open("eval_adapter.py").read().split("tok=AutoTokenizer")[0])
tok=AutoTokenizer.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct")
m=PeftModel.from_pretrained(AutoModelForCausalLM.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct", dtype=torch.float32), sys.argv[1]); m.eval()
I="Robot boxer: next moves for this fight state."
def rule(s):
    for k,v in [("hook_l","hook_l"),("hook_r","hook_r"),("body","body"),("uppercut","uppercut")]:
        if s['o1']==v: return "opp "+k
    if s['dist']>1.6: return "far (dist>1.6)"
    if s['theirs']<12: return "opp broke (<12)"
    return "opp "+str(s['o1'])
from collections import defaultdict
agg=defaultdict(lambda:[0,0])
for s in states:
    p=f"Below is an instruction that describes a task, paired with an input that provides further context. Write a response that appropriately completes the request.\n\n### Instruction:\n{I}\n\n### Input:\n{line(s)}\n\n### Response:\n"
    enc=tok(p, return_tensors="pt")
    with torch.no_grad(): out=m.generate(**enc, max_new_tokens=10, do_sample=False)
    t=tok.decode(out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True).strip()
    r=rule(s); agg[r][1]+=1; agg[r][0]+= (t.split() or [""])[0]==coach_first(s)
for r,(h,n) in sorted(agg.items()): print(f"{r:20s} {h}/{n}")
