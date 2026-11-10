import { createRequire } from 'node:module';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';

if (!process.argv[2] || !process.argv[3]) {
  throw new Error('Usage: node scripts/Verify-UploadHost.mjs <host-source-directory> <fixture-output-directory>');
}
const site = resolve(process.argv[2]);
const output = resolve(process.argv[3]);
const root = join(output, `upload-host-runtime-${randomUUID()}`);
await mkdir(root, { recursive: true });
Object.assign(process.env, {
  DISCORD_CLIENT_ID: 'isolated-test-client',
  DISCORD_CLIENT_SECRET: 'isolated-test-secret',
  OWNER_DISCORD_ID: '100000000000000001',
  SITE_ORIGIN: 'https://uploads.example.test',
  DATA_DIR: join(root, 'db'),
  UPLOAD_DIR: join(root, 'uploads'),
  THUMB_DIR: join(root, 'thumbs'),
  PUBLIC_DIR: join(root, 'public'),
  MAX_UPLOAD_BYTES: '94371840',
});
const requireSite = createRequire(pathToFileURL(join(site, 'package.json')));
const fastify = requireSite('fastify');
const multipart = requireSite('@fastify/multipart');
const { createUploadToken, revokeToken } = await import(pathToFileURL(join(site, 'src/auth.js')));
const { db } = await import(pathToFileURL(join(site, 'src/db.js')));
const { default: uploadRoutes } = await import(pathToFileURL(join(site, 'src/routes/upload.js')));
let token = createUploadToken('Isolated Winisland verification', process.env.OWNER_DISCORD_ID);
const app = fastify({ logger: false });
await app.register(multipart);
await app.register(uploadRoutes, { prefix: '/api' });
app.get('/fixture', async () => { token = createUploadToken('Isolated Winisland verification', process.env.OWNER_DISCORD_ID); return { token: token.token }; });
app.post('/fixture/revoke', async () => { revokeToken(token.id); return { ok: true }; });
const address = await app.listen({ port: 0, host: '127.0.0.1' });
await writeFile(join(output, 'upload-host-url.txt'), address);
process.stdout.write(`Isolated upload route ready at ${address}\n`);
let closing = false;
async function shutdown() {
  if (closing) return;
  closing = true;
  clearTimeout(lifetime);
  // Bound shutdown even when a client leaves an upload socket open.
  const deadline = setTimeout(() => process.exit(1), 5000);
  try { app.server.closeAllConnections?.(); await app.close(); db.close(); process.exit(0); }
  catch (error) { process.stderr.write(`Upload fixture shutdown failed: ${error.message}\n`); process.exit(1); }
  finally { clearTimeout(deadline); }
}
const lifetime = setTimeout(shutdown, 10 * 60 * 1000);
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
// A piped owner can stop this helper without desktop input or killing unrelated
// Node processes. No prompt is displayed; the timeout is always the final bound.
process.stdin.on('data', chunk => { if (chunk.toString().trim() === 'quit') void shutdown(); });
