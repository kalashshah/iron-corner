# The /connect sign-in page (Privy) bundled into one gzipped file.
FROM node:22-slim AS connect
WORKDIR /connect
COPY connect/package.json connect/package-lock.json ./
RUN npm install --no-audit --no-fund
COPY connect/ ./
RUN npm run build && gzip -9 ../public/connect.js

FROM node:22-slim
RUN apt-get update && apt-get install -y --no-install-recommends unzip ca-certificates python3 python3-venv && rm -rf /var/lib/apt/lists/*
# Camp adapter serving, as 0G's fine-tuning docs describe: base model + LoRA with transformers and peft (CPU torch).
RUN python3 -m venv /opt/venv && /opt/venv/bin/pip install --no-cache-dir -U pip && /opt/venv/bin/pip install --no-cache-dir torch transformers peft safetensors --extra-index-url https://download.pytorch.org/whl/cpu \
 && /opt/venv/bin/python -c "import torch; assert '+cpu' in torch.__version__, torch.__version__" \
 && /opt/venv/bin/python -c "from transformers import AutoTokenizer, AutoModelForCausalLM as M; AutoTokenizer.from_pretrained('Qwen/Qwen2.5-0.5B-Instruct'); M.from_pretrained('Qwen/Qwen2.5-0.5B-Instruct')"
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY serve_adapter.py ./
COPY --from=connect /public/connect.js.gz ./public/
COPY src ./src
ENV PORT=8787 CAMP_PYTHON=/opt/venv/bin/python
EXPOSE 8787
CMD ["npx", "tsx", "src/server.ts"]
