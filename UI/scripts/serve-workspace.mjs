import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';

const cli = createRequire(import.meta.url).resolve('@angular/cli/bin/ng.js');
const args = process.argv.slice(2);
const managementPort = process.env.WORKFLOW_BUILDER_MANAGEMENT_PORT || '4201';
const management = spawn(process.execPath, [cli, 'serve', 'engine-management', '--host', '127.0.0.1', '--port', managementPort], { stdio: 'inherit' });
const product = spawn(process.execPath, [cli, 'serve', 'workflow-builder', ...args], { stdio: 'inherit' });
let stopping = false;
function stop(code) {
  if (stopping) return;
  stopping = true;
  process.exitCode = code;
  management.kill();
  product.kill();
}
for (const server of [management, product]) {
  server.on('exit', code => stop(code ?? 1));
  server.on('error', () => stop(1));
}
process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));
