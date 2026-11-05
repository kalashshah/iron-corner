// Read-mostly probe of 0G fine-tuning with our testnet wallet: services, models, ledger, balance. Spends nothing.
import fs from "node:fs"; import os from "node:os";
import { ethers } from "ethers";
import { createZGComputeNetworkBroker } from "@0gfoundation/0g-compute-ts-sdk";
const env = Object.fromEntries(fs.readFileSync(os.homedir() + "/.iron-corner/wallets.env", "utf8").split("\n").filter(l => l.includes("=") && !l.startsWith("#")).map(l => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]));
const rpc = new ethers.JsonRpcProvider("https://evmrpc-testnet.0g.ai");
const w = new ethers.Wallet(env.OG_OPS_PRIVATE_KEY, rpc);
const j = (x) => JSON.stringify(x, (k, v) => typeof v === "bigint" ? v.toString() : v);
console.log("wallet", w.address, "balance", ethers.formatEther(await rpc.getBalance(w.address)), "0G", "dev", process.env.ZG_DEV_MODE ?? "off");
const b = await createZGComputeNetworkBroker(w);
try { console.log("ledger", j(await b.ledger.getLedger()).slice(0, 400)); } catch (e) { console.log("ledger:", e.message.slice(0, 200)); }
try { const s = await b.fineTuning.listService(); console.log("FT services", j(s).slice(0, 1500)); } catch (e) { console.log("FT services err", e.message.slice(0, 300)); }
try { const m = await b.fineTuning.listModel(); console.log("FT models", j(m).slice(0, 1200)); } catch (e) { console.log("FT models err", e.message.slice(0, 300)); }
