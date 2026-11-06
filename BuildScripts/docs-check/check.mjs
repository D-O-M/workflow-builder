import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import GithubSlugger from 'github-slugger';
import MarkdownIt from 'markdown-it';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('../../', import.meta.url));
const listed = execFileSync('git', ['ls-files', '-z', '--cached', '--others', '--exclude-standard'], {
  cwd: root, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024,
});
const files = new Set(listed.split('\0').filter(file => file && existsSync(path.join(root, file))));
const directories = new Set(['.']);
for (const file of files) {
  let directory = path.posix.dirname(file);
  while (!directories.has(directory)) {
    directories.add(directory);
    directory = path.posix.dirname(directory);
  }
}

const parser = new MarkdownIt();
const headings = new Map();
const links = [];
const diagrams = [];
const problems = [];
for (const file of files) {
  if (!file.toLowerCase().endsWith('.md')) continue;
  const tokens = parser.parse(readFileSync(path.join(root, file), 'utf8'), {});
  const slugger = new GithubSlugger();
  const anchors = new Set();
  headings.set(file, anchors);
  let line = 1;
  for (const [index, token] of tokens.entries()) {
    if (token.map) line = token.map[0] + 1;
    if (token.type === 'heading_open') {
      const title = tokens[index + 1].children.map(child => child.content).join('');
      anchors.add(slugger.slug(title));
    }
    for (const child of token.children ?? []) {
      const url = child.type === 'link_open' ? child.attrGet('href') : child.attrGet('src');
      if (url) links.push({ file, line, url });
    }
    if (token.type === 'fence' && token.info.trim() === 'mermaid') {
      diagrams.push({ file, line, source: token.content });
    }
  }
}

function checkLink({ file, url }) {
  if (/^(https?:|mailto:)/i.test(url)) return;
  if (/^(?:[a-z]+:|[/\\])/i.test(url)) throw new Error('expected a repository-relative link');
  const [relative, fragment] = url.split('#', 2).map(decodeURIComponent);
  const target = relative
    ? path.posix.normalize(path.posix.join(path.posix.dirname(file), relative.split('?')[0])).replace(/\/$/, '')
    : file;
  if (!files.has(target) && !directories.has(target)) throw new Error('missing target or incorrect case');
  if (!fragment) return;
  const anchors = headings.get(directories.has(target) ? path.posix.join(target, 'README.md') : target);
  if (anchors) {
    if (!anchors.has(fragment)) throw new Error('unknown heading');
    return;
  }
  const range = /^L(\d+)(?:-L(\d+))?$/.exec(fragment);
  if (!range || !files.has(target)) throw new Error('expected a GitHub source line anchor');
  const first = Number(range[1]);
  const last = Number(range[2] ?? first);
  const lines = readFileSync(path.join(root, target), 'utf8').trimEnd().split('\n').length;
  if (first < 1 || last < first || last > lines) throw new Error('line range outside source file');
}

for (const link of links) {
  try {
    checkLink(link);
  } catch (error) {
    problems.push(`${link.file}:${link.line}  ${link.url} — ${error.message}`);
  }
}

const require = createRequire(import.meta.url);
const mermaidBundle = path.join(path.dirname(require.resolve('mermaid/package.json')), 'dist/mermaid.min.js');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 980, height: 900 } });
  await page.setContent('<main style="width:896px;margin:auto"></main>');
  await page.addScriptTag({ path: mermaidBundle });
  await page.evaluate(() => window.mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' }));
  for (const [index, diagram] of diagrams.entries()) {
    try {
      await page.evaluate(async ({ index, source }) => {
        const main = document.querySelector('main');
        main.replaceChildren();
        const result = await window.mermaid.render('diagram' + index, source, main);
        main.innerHTML = result.svg;
        const svg = main.querySelector('svg');
        if (!svg || svg.getBoundingClientRect().height <= 0 || svg.querySelector('.error-icon')) {
          throw new Error('empty or invalid diagram');
        }
        if (svg.getBoundingClientRect().width > main.clientWidth + 1) throw new Error('diagram overflows reading column');
        if (svg.getScreenCTM().a < 0.75) {
          throw new Error('diagram shrinks below 75% at 896px. Shorten labels or split the view');
        }
      }, { index, source: diagram.source });
    } catch (error) {
      problems.push(`${diagram.file}:${diagram.line}  ${error.message}`);
    }
  }
} finally {
  await browser.close();
}

console.log(`Checked ${headings.size} Markdown files, ${links.length} links and ${diagrams.length} Mermaid diagrams.`);
if (problems.length) {
  console.error(problems.join('\n'));
  process.exitCode = 1;
}
