import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';

const CLI = path.resolve('.github/triage/triage.mjs');

const ISSUE = {
  number: 5,
  title: '直连 HoYoLAB 返回 403',
  body: '按 README 配置好 ProxyBase 和 ProxyToken 之后，启动胡桃仍然 403。',
  user: { login: 'alice' },
  author_association: 'NONE',
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  state: 'open',
  labels: [],
};

const DECISION = {
  category: 'bug',
  state: 'needs-info',
  confidence: 0.88,
  summary: '配置后仍 403，需要日志确认代理链路',
  reasoning: '报告人只给了现象，没有版本、系统与日志。',
  questions: ['请提供 proxy.log 中插件启动后的前 20 行。', '请说明系统代理是否指向 127.0.0.1:8888。'],
  brief: null,
  comment: '## 分诊记录\n\n**还需要你提供（@报告人）：**\n\n1. 请提供 proxy.log。\n2. 系统代理端口是多少？',
};

/** 去掉测试运行器注入的变量，否则子进程会尝试连测试 IPC 而挂住 */
function childEnv(extra) {
  const { NODE_TEST_CONTEXT, NODE_V8_COVERAGE, ...base } = process.env;
  return { ...base, ...extra };
}

function safeJson(s) {
  try {
    return JSON.parse(s);
  } catch {
    return s;
  }
}

async function startServer(state) {
  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8');
      const body = raw ? safeJson(raw) : null;
      state.calls.push({ method: req.method, url: req.url, body });

      const json = (code, obj) => {
        res.writeHead(code, { 'content-type': 'application/json' });
        res.end(JSON.stringify(obj));
      };
      const text = (code, s) => {
        res.writeHead(code, { 'content-type': 'text/plain' });
        res.end(s);
      };

      const { pathname } = new URL(req.url, 'http://x');

      if (pathname.endsWith('/chat/completions')) {
        return json(200, { choices: [{ message: { content: JSON.stringify(DECISION) } }] });
      }
      if (req.method === 'GET' && pathname === '/api/repos/o/r') return json(200, { default_branch: 'main' });
      if (req.method === 'GET' && pathname === '/api/repos/o/r/git/trees/main') {
        return json(200, { tree: [{ type: 'blob', path: 'README.md' }, { type: 'blob', path: 'src/a.cs' }] });
      }
      if (req.method === 'GET' && pathname.startsWith('/api/repos/o/r/contents/')) {
        return text(200, '# README\n内容\n');
      }
      if (req.method === 'GET' && pathname === '/api/repos/o/r/labels') return json(200, []);
      if (req.method === 'POST' && pathname === '/api/repos/o/r/labels') return json(201, { name: body.name });
      if (req.method === 'GET' && pathname === '/api/repos/o/r/issues/5') return json(200, state.issue);
      if (req.method === 'GET' && pathname === '/api/repos/o/r/issues/5/comments') return json(200, state.comments);
      if (req.method === 'POST' && pathname === '/api/repos/o/r/issues/5/labels') {
        state.labelAdds.push(body);
        return json(200, body.labels.map((n) => ({ name: n })));
      }
      if (req.method === 'POST' && pathname === '/api/repos/o/r/issues/5/comments') {
        state.commentAdds.push(body);
        return json(201, { id: 1, ...body });
      }
      if (req.method === 'DELETE' && pathname.includes('/labels/')) {
        state.labelDels.push(pathname);
        res.writeHead(204);
        return res.end();
      }
      return json(404, { message: `mock 未实现: ${req.method} ${req.url}` });
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  return server;
}

function baseEnv(port) {
  return {
    GITHUB_TOKEN: 'test-token',
    GITHUB_REPOSITORY: 'o/r',
    GITHUB_API_URL: `http://127.0.0.1:${port}/api`,
  };
}

function runChild(args, env) {
  const child = spawn(process.execPath, [CLI, ...args], { env: childEnv(env), stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', (d) => (stdout += d));
  child.stderr.on('data', (d) => (stderr += d));
  const timer = setTimeout(() => child.kill('SIGKILL'), 25000);
  const done = new Promise((resolve) => child.on('close', (code) => resolve(code)));
  return done.then((code) => {
    clearTimeout(timer);
    return { code, stdout, stderr };
  });
}

async function runCli(server, extraArgs = []) {
  const { port } = server.address();
  const tmp = await fs.mkdtemp(path.join(os.tmpdir(), 'triage-'));
  const summaryFile = path.join(tmp, 'summary.md');
  const r = await runChild(['--number', '5', '--summary-file', summaryFile, ...extraArgs], {
    ...baseEnv(port),
    AI_API_KEY: 'test-key',
    AI_BASE_URL: `http://127.0.0.1:${port}/v1`,
    AI_MODEL: 'mock-model',
  });
  const summary = await fs.readFile(summaryFile, 'utf8').catch(() => '');
  return { ...r, summary };
}

test('端到端：分诊 issue 并写入标签与评论', async (t) => {
  const state = { calls: [], labelAdds: [], labelDels: [], commentAdds: [], issue: ISSUE, comments: [] };
  const server = await startServer(state);
  t.after(() => server.close());

  const r = await runCli(server);
  assert.equal(r.code, 0, `stderr: ${r.stderr}`);

  const labels = state.labelAdds.at(-1)?.labels || [];
  assert.ok(labels.includes('bug'), `应包含 bug，实际 ${JSON.stringify(labels)}`);
  assert.ok(labels.includes('needs-info'), `应包含 needs-info，实际 ${JSON.stringify(labels)}`);

  const posted = state.commentAdds.at(-1)?.body || '';
  assert.match(posted, /<!-- triage-agent:v1 hash=[0-9a-f]{8} -->/);
  assert.match(posted, /AI 在 triage/);
  assert.match(posted, /分诊记录/);
  assert.match(posted, /88%/);

  assert.match(r.summary, /#5/);
  assert.match(r.summary, /needs-info/);
});

test('端到端：dry-run 不写入任何内容', async (t) => {
  const state = { calls: [], labelAdds: [], labelDels: [], commentAdds: [], issue: ISSUE, comments: [] };
  const server = await startServer(state);
  t.after(() => server.close());

  const r = await runCli(server, ['--dry-run']);
  assert.equal(r.code, 0, `stderr: ${r.stderr}`);
  assert.equal(state.commentAdds.length, 0, 'dry-run 不应发评论');
  assert.equal(state.labelAdds.length, 0, 'dry-run 不应打标签');
  assert.match(r.stdout, /将要发布的评论/);
});

test('端到端：无 AI_API_KEY 且无 GITHUB_TOKEN 时给出明确错误', async (t) => {
  const state = { calls: [], labelAdds: [], labelDels: [], commentAdds: [], issue: ISSUE, comments: [] };
  const server = await startServer(state);
  t.after(() => server.close());

  const { port } = server.address();
  // baseEnv 里带 GITHUB_TOKEN；这里显式清空，模拟既无模型密钥也无 GitHub 令牌
  const env = { ...baseEnv(port), GITHUB_TOKEN: '', AI_API_KEY: '', OPENAI_API_KEY: '' };
  const r = await runChild(['--number', '5'], env);
  assert.equal(r.code, 1);
  assert.match(r.stderr, /GITHUB_TOKEN/);
});

test('端到端：无 AI_API_KEY 时回退 GitHub Models', async (t) => {
  const state = { calls: [], labelAdds: [], labelDels: [], commentAdds: [], issue: ISSUE, comments: [] };
  const server = await startServer(state);
  t.after(() => server.close());

  const { port } = server.address();
  const r = await runChild(
    ['--number', '5', '--dry-run', '--base-url', `http://127.0.0.1:${port}/v1`, '--model', 'mock-model'],
    { ...baseEnv(port), AI_API_KEY: '', OPENAI_API_KEY: '' },
  );
  assert.equal(r.code, 0, `stderr: ${r.stderr}`);
  assert.match(r.stdout, /分诊记录/);
});