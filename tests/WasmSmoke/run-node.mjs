import { dotnet } from './_framework/dotnet.js'
const { runMain } = await dotnet.create();
process.exitCode = await runMain();
// Run from the published app's wwwroot folder: dotnet publish tests/WasmSmoke -c Release, copy this file there, then `node run-node.mjs`.
