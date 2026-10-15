// Local-only acceptance receiver. Imports the real Web service and records
// request counts without putting names, coordinates or payloads into metrics.
import { mkdir, writeFile, lstat } from "node:fs/promises";
import { isAbsolute, resolve, join } from "node:path";
import { pathToFileURL } from "node:url";

const [webPath, outputPath, portText, catalogPath, ...extra] = process.argv.slice(2);
const port = Number(portText);
if (!webPath || !outputPath || !isAbsolute(webPath) || !isAbsolute(outputPath)
  || !Number.isInteger(port) || port < 1024 || port > 65535 || extra.length)
  throw new Error("Usage: node test-server.mjs ABSOLUTE_WEB_REPO NEW_PRIVATE_DIRECTORY PORT [ABSOLUTE_CATALOG]");
const output = resolve(outputPath);
try { await lstat(output); throw new Error("Use a fresh private acceptance directory"); }
catch (error) { if (error.code !== "ENOENT") throw error; }
if (catalogPath && !isAbsolute(catalogPath)) throw new Error("Catalog must be an explicit absolute path");
await mkdir(output, { recursive: true, mode: 0o700 });
const { createLiveServer } = await import(pathToFileURL(resolve(webPath, "PeakTrailPlatform/server/live-server.mjs")));
const service = createLiveServer({ trajectoryDir: join(output, "store"), ...(catalogPath ? { catalogPath } : {}) });
const requests = [];
let writing = Promise.resolve();
function report() {
  return { localOnly: true, productionUploads: false, endpoint: `http://127.0.0.1:${port}/api/route-uploads`,
    uploadRequests: requests.length, uploads: requests };
}
function persist() {
  const snapshot = JSON.stringify(report(), null, 2) + "\n";
  writing = writing.then(() => writeFile(join(output, "http-summary.json"), snapshot, { mode: 0o600 }));
}
service.server.prependListener("request", (request, response) => {
  if (request.method !== "POST" || request.url?.split("?")[0] !== "/api/route-uploads") return;
  const item = { startedUtc: new Date().toISOString(), contentBytes: Number(request.headers["content-length"] ?? 0),
    format: String(request.headers["x-trajectory-format"] ?? ""), status: null, elapsedMs: null };
  const started = performance.now();
  requests.push(item);
  response.once("finish", () => {
    item.status = response.statusCode; item.elapsedMs = performance.now() - started; persist();
  });
});
await new Promise((done, reject) => { service.server.once("error", reject); service.server.listen(port, "127.0.0.1", done); });
persist(); await writing;
console.log(JSON.stringify({ ready: true, ...report() }));
async function stop() {
  await new Promise(done => { service.server.closeAllConnections?.(); service.server.close(done); });
  persist(); await writing; process.exit(0);
}
process.once("SIGINT", stop); process.once("SIGTERM", stop);
