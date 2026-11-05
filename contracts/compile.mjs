// Compile IronCornerAgent.sol with solc-js into out/IronCornerAgent.json (abi + bytecode).
import fs from "node:fs";
import path from "node:path";
import solc from "solc";

const src = fs.readFileSync("IronCornerAgent.sol", "utf8");
const input = {
  language: "Solidity",
  sources: { "IronCornerAgent.sol": { content: src } },
  settings: { optimizer: { enabled: true, runs: 200 }, evmVersion: "cancun",
    outputSelection: { "*": { "*": ["abi", "evm.bytecode.object"] } } },
};
const findImports = (p) => {
  const f = path.join("node_modules", p);
  return fs.existsSync(f) ? { contents: fs.readFileSync(f, "utf8") } : { error: "not found: " + p };
};
const out = JSON.parse(solc.compile(JSON.stringify(input), { import: findImports }));
const errs = (out.errors ?? []).filter((e) => e.severity === "error");
if (errs.length) { console.error(errs.map((e) => e.formattedMessage).join("\n")); process.exit(1); }
const c = out.contracts["IronCornerAgent.sol"].IronCornerAgent;
fs.mkdirSync("out", { recursive: true });
fs.writeFileSync("out/IronCornerAgent.json", JSON.stringify({ abi: c.abi, bytecode: "0x" + c.evm.bytecode.object }, null, 1));
console.log("compiled, bytecode bytes:", c.evm.bytecode.object.length / 2);
