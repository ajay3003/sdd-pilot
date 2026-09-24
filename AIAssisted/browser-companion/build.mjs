// BirkNext Browser Companion — production/package build.
// 1. Validates the manifest against the companion's security constraints (no <all_urls>, no cookies/history/webRequest, MV3).
// 2. Syntax-checks every script.
// 3. Runs the unit tests (unless --skip-tests).
// 4. Packages the extension into dist/birknext-browser-companion-<version>.zip (source stays in the repository).
import { readFileSync, mkdirSync, rmSync, existsSync, readdirSync, statSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const manifest = JSON.parse(readFileSync(path.join(root, 'manifest.json'), 'utf8'));
const skipTests = process.argv.includes('--skip-tests');
const forbiddenPermissions = ['cookies', 'history', 'webRequest', 'webRequestBlocking', 'tabs', 'downloads', 'debugger', 'management', 'privacy', 'proxy'];

function fail(message) { console.error(`BUILD FAILED: ${message}`); process.exit(1); }

if (manifest.manifest_version !== 3) fail('manifest_version must be 3');
for (const p of manifest.permissions || []) if (forbiddenPermissions.includes(p)) fail(`forbidden permission: ${p}`);
for (const h of manifest.host_permissions || []) {
  if (h === '<all_urls>' || h.startsWith('*://*') || h === 'https://*/*' || h === 'http://*/*') fail(`host_permissions must be loopback only, found ${h}`);
  if (!/^http:\/\/(127\.0\.0\.1|localhost)\/\*$/.test(h)) fail(`unexpected host permission ${h}`);
}
if ((manifest.optional_host_permissions || []).includes('<all_urls>')) fail('optional_host_permissions must not contain <all_urls>');
if (!manifest.background || !manifest.background.service_worker) fail('service worker missing');

const files = ['background.js', 'content.js', 'main-world.js', 'popup.js', 'lib/sanitize.js', 'lib/page-identity.js', 'lib/dom.js', 'lib/wcag.js', 'lib/wcag-interaction.js', 'lib/wcag-keyboard.js', 'lib/automation.js', 'lib/picker.js', 'lib/a11y.js', 'vendor/axe.min.js', 'lib/axe-evidence.js', 'lib/perf.js', 'lib/navigation.js'];
for (const f of files) {
  if (!existsSync(path.join(root, f))) fail(`missing ${f}`);
  execFileSync(process.execPath, ['--check', path.join(root, f)], { stdio: 'inherit' });
}
for (const f of files.filter(f => !f.startsWith('vendor/'))) {
  const src = readFileSync(path.join(root, f), 'utf8');
  // Credential-bearing browser APIs the companion must never call, and the code-evaluation sinks that would let a
  // typed probe command smuggle in JavaScript (comments/regexes that merely mention the words are fine).
  for (const banned of ['eval(', 'new Function', 'document.cookie', 'localStorage.', 'sessionStorage.', 'chrome.cookies', 'chrome.webRequest', 'getAllResponseHeaders', '.headers.get(', 'msal.', 'chrome.tabs.query']) {
    if (src.includes(banned)) fail(`${f} must not reference ${banned}`);
  }
}
execFileSync(process.execPath, [path.join(root, 'generate-axe-catalog.cjs'), '--check'], { stdio: 'inherit' });
console.log(`manifest ok: ${manifest.name} ${manifest.version}; ${files.length} scripts syntax-checked; credential-API scan clean`);

if (!skipTests) {
  const result = spawnSync(process.execPath, ['--test', 'tests/*.test.mjs'], { cwd: root, stdio: 'inherit' });
  if (result.status !== 0) fail('unit tests failed');
}

const dist = path.join(root, 'dist');
if (path.dirname(path.resolve(dist)) !== path.resolve(root)) fail('package output must be inside companion workspace');
rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });
const zipName = `birknext-browser-companion-${manifest.version}.zip`;
const include = ['manifest.json', 'icon128.png', 'popup.html', 'vendor/LICENSE.txt', 'vendor/NOTICE.txt', ...files];
// Windows ships bsdtar which writes zip archives with -a; fall back to PowerShell Compress-Archive.
let packaged = false;
try {
  execFileSync('tar', ['-a', '-c', '-f', path.join('dist', zipName), ...include], { cwd: root, stdio: 'inherit' });
  packaged = true;
} catch { /* try PowerShell */ }
if (!packaged) {
  const ps = `Compress-Archive -Path ${include.map(f => `'${f}'`).join(',')} -DestinationPath '${path.join(dist, zipName)}' -Force`;
  execFileSync('powershell', ['-NoProfile', '-Command', ps], { cwd: root, stdio: 'inherit' });
}
const size = statSync(path.join(dist, zipName)).size;
console.log(`packaged ${zipName} (${size} bytes) with ${include.length} files: ${readdirSync(dist).join(', ')}`);
