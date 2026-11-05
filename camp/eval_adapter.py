# Compare base Qwen2.5-0.5B-Instruct vs base+our 0G-trained LoRA on fresh fight states.
# Score = how often the first move matches the coach's style (same rules that generated the training data).
import json, random, sys, time, torch
from transformers import AutoTokenizer, AutoModelForCausalLM
from peft import PeftModel
ADAPTER = sys.argv[1]
MOVES = "jab cross hook_l hook_r uppercut body block dodge_l dodge_r step_in step_back".split()
SYSTEM = "You are a robot boxer's trained instincts. Given the fight state, reply with your next 1-3 moves, space separated, from: " + " ".join(MOVES) + "."
def line(s): return f"opp_last={s['o1'] or 'none'} opp_prev={s['o2'] or 'none'} my_last={s['m1'] or 'none'} dist={s['dist']:.1f} my_budget={s['mine']} opp_budget={s['theirs']} my_guard={s['guard']:.1f}"
def coach_first(s):
    if s['o1']=='hook_l': return 'dodge_r'
    if s['o1']=='hook_r': return 'dodge_l'
    if s['o1']=='body': return 'block'
    if s['dist']>1.6: return 'step_in'
    if s['theirs']<12: return 'body'
    if s['o1']=='jab': return 'block'
    return None  # free choice: not scored
random.seed(7)
states=[]
while len(states)<24:
    s=dict(o1=random.choice([None,'jab','cross','hook_l','hook_r','body']),o2=random.choice([None,'jab','hook_l','block']),m1=random.choice([None,'jab','block']),
           dist=0.9+random.random()*1.2,mine=10+random.randint(0,40),theirs=5+random.randint(0,45),guard=random.random())
    if coach_first(s): states.append(s)
tok=AutoTokenizer.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct")
base=AutoModelForCausalLM.from_pretrained("Qwen/Qwen2.5-0.5B-Instruct", torch_dtype=torch.float32)
def run(model,label):
    hits=0; t0=time.time(); outs=[]
    for s in states:
        msgs=[{"role":"system","content":SYSTEM},{"role":"user","content":line(s)}]
        ids=tok.apply_chat_template(msgs,add_generation_prompt=True,return_tensors="pt")
        with torch.no_grad(): out=model.generate(ids,max_new_tokens=12,do_sample=False)
        txt=tok.decode(out[0][ids.shape[1]:],skip_special_tokens=True).strip()
        first=(txt.split() or [''])[0]; ok=first==coach_first(s); hits+=ok; outs.append((line(s),txt,ok))
    print(f"{label}: {hits}/{len(states)} match the coach ({(time.time()-t0)/len(states)*1000:.0f} ms/answer on CPU)")
    for l,t,ok in outs[:6]: print("   ", "✓" if ok else "✗", l, "→", t)
    return hits
b=run(base,"base model")
tuned=PeftModel.from_pretrained(base, ADAPTER)
a=run(tuned,"base + 0G LoRA")
print(json.dumps({"base":b,"tuned":a,"n":len(states)}))
