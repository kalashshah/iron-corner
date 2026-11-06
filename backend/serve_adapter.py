# Runs a bot's fight-camp LoRA the way 0G's fine-tuning docs say to use one ("Using the Fine-tuned Model"):
# load the base model, put the LoRA adapter on top, run inference. The adapter itself was trained on 0G Compute.
# POST /moves {"zip": "<adapter.zip from the camp>", "prompt": "<alpaca prompt>"} -> {"text": "dodge_r uppercut", "ms": 412}
# Listens on 127.0.0.1 only; the game server spawns it and is its only client.
import json, os, sys, time, zipfile, threading
from functools import lru_cache
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import torch
from transformers import AutoTokenizer, AutoModelForCausalLM
from peft import PeftModel

BASE = os.environ.get("CAMP_BASE_MODEL", "Qwen/Qwen2.5-0.5B-Instruct")
torch.set_num_threads(max(1, os.cpu_count() or 1))
tok = AutoTokenizer.from_pretrained(BASE)
model = None   # PeftModel once the first adapter loads
loaded = set() # adapter names already on the model
lock = threading.Lock()  # one generation at a time; the CPU is the bottleneck anyway
base = AutoModelForCausalLM.from_pretrained(BASE, torch_dtype=torch.float32)
base.eval()

def adapter_dir(zip_path):
    # The camp zip holds output_model/adapter_config.json + adapter_model.safetensors (plus checkpoints we skip).
    out = zip_path[:-4]
    if not os.path.exists(os.path.join(out, "output_model", "adapter_config.json")):
        with zipfile.ZipFile(zip_path) as z:
            z.extractall(out, [n for n in z.namelist() if n.startswith("output_model/") and "checkpoint" not in n])
    return os.path.join(out, "output_model")

# The state is in words (distance=close, my_money=low...), so the same moment comes back often: cache the answers.
@lru_cache(maxsize=4096)
def moves(zip_path, prompt):
    global model
    name = "a" + str(abs(hash(zip_path)))
    with lock:
        if model is None:
            model = PeftModel.from_pretrained(base, adapter_dir(zip_path), adapter_name=name)
            loaded.add(name)
        elif name not in loaded:
            model.load_adapter(adapter_dir(zip_path), adapter_name=name)
            loaded.add(name)
        model.set_adapter(name)
        enc = tok(prompt, return_tensors="pt")
        with torch.no_grad():
            out = model.generate(**enc, max_new_tokens=12, do_sample=False)
        return tok.decode(out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True).strip()

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def reply(self, code, obj):
        b = json.dumps(obj).encode()
        self.send_response(code); self.send_header("content-type", "application/json"); self.send_header("content-length", str(len(b)))
        self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        self.reply(200, {"ok": True, "base": BASE, "adapters": len(loaded)})
    def do_POST(self):
        try:
            req = json.loads(self.rfile.read(int(self.headers.get("content-length", 0))))
            t0 = time.time()
            text = moves(req["zip"], req["prompt"])
            self.reply(200, {"text": text, "ms": round((time.time() - t0) * 1000)})
        except Exception as e:
            self.reply(500, {"error": str(e)[:300]})

if __name__ == "__main__":
    if len(sys.argv) == 3:  # self-check: python serve_adapter.py <adapter.zip> "<state line>"
        from_line = sys.argv[2]
        p = ("Below is an instruction that describes a task, paired with an input that provides further context. Write a response that appropriately completes the request.\n\n"
             f"### Instruction:\nRobot boxer: next moves for this fight state.\n\n### Input:\n{from_line}\n\n### Response:\n")
        t0 = time.time(); print(moves(sys.argv[1], p), f"({(time.time()-t0)*1000:.0f} ms)"); sys.exit(0)
    port = int(os.environ.get("CAMP_SERVE_PORT", "8790"))
    print(f"[adapter] serving {BASE} + camp LoRAs on 127.0.0.1:{port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
