// Deploy IronCornerAgent to 0G Galileo testnet. The trainer (only address allowed to mint/record) is the
// same OG_OPS wallet the backend uses for 0G Storage. Key read from ~/.iron-corner/wallets.env, never printed.
import fs from "node:fs";
import os from "node:os";
import { ethers } from "ethers";

const env = Object.fromEntries(fs.readFileSync(path(os.homedir(), ".iron-corner/wallets.env"), "utf8")
  .split("\n").filter((l) => l.includes("=") && !l.startsWith("#")).map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1)]));
function path(...p) { return p.join("/"); }

const RPC = process.env.ZG_RPC ?? "https://evmrpc-testnet.0g.ai";
const provider = new ethers.JsonRpcProvider(RPC);
const wallet = new ethers.Wallet(env.OG_OPS_PRIVATE_KEY, provider);
const { abi, bytecode } = JSON.parse(fs.readFileSync("out/IronCornerAgent.json", "utf8"));
const factory = new ethers.ContractFactory(abi, bytecode, wallet);
const c = await factory.deploy(wallet.address);
const rcpt = await c.deploymentTransaction().wait();
const address = await c.getAddress();
fs.writeFileSync("deployment.json", JSON.stringify({ network: "0g-galileo", chainId: 16602, address, trainer: wallet.address,
  deployTx: rcpt.hash, block: rcpt.blockNumber, at: new Date().toISOString() }, null, 1));
console.log("deployed", address, "tx", rcpt.hash);
