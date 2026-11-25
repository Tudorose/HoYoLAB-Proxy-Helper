import test from 'node:test';
import assert from 'node:assert/strict';

import {
  DEFAULTS,
  MARKER,
  buildComment,
  buildMessages,
  hashDecision,
  parseDecision,
  planLabelChange,
  renderPrompt,
  truncate,
} from '../.github/triage/triage.mjs';

const sample = {
  category: 'bug',
  state: 'needs-info',
  confidence: 0.82,
  summary: '启动后系统代理未恢复',
  reasoning: '报告人只描述了现象，没有提供版本与日志。',
  questions: ['请提供 proxy.log 中插件启动后的前 20 行。'],
  brief: null,
  comment: '## 分诊记录\n\n**还需要你提供（@报告人）：**\n\n1. 请提供 proxy.log。',
};

test('parseDecision 接受裸 JSON', () => {
  const d = parseDecision(JSON.stringify(sample));
  assert.equal(d.category, 'bug');
  assert.equal(d.state, 'needs-info');
  assert.equal(d.questions.length, 1);
});

test('parseDecision 容忍 ```json 围栏与前后噪声', () => {
  const raw = '好的，结果如下：\n```json\n' + JSON.stringify(sample) + '\n```\n以上。';
  const d = parseDecision(raw);
  assert.equal(d.state, 'needs-info');
  assert.equal(d.summary, '启动后系统代理未恢复');
});

test('parseDecision 拒绝非法 category', () => {
  assert.throws(() => parseDecision('{"category":"question","state":"needs-triage"}'), /非法/);
});

test('parseDecision 拒绝非法 state', () => {
  assert.throws(() => parseDecision('{"category":"bug","state":"done"}'), /非法/);
});

test('parseDecision 把 confidence 夹到 [0,1]', () => {
  const d = parseDecision('{"category":"bug","state":"needs-triage","confidence":7}');
  assert.equal(d.confidence, 1);
});

test('parseDecision 规范化 brief', () => {
  const raw = JSON.stringify({
    category: 'enhancement',
    state: 'ready-for-agent',
    confidence: 0.9,
    summary: 's',
    reasoning: 'r',
    questions: [],
    brief: {
      summary: 'x',
      current: 'a',
      desired: 'b',
      interfaces: ['TypeName'],
      acceptance: ['c1'],
      out_of_scope: ['no'],
    },
    comment: 'body',
  });
  const d = parseDecision(raw);
  assert.deepEqual(d.brief.interfaces, ['TypeName']);
  assert.deepEqual(d.brief.outOfScope, ['no']);
});

test('planLabelChange 未标注时只增不删', () => {
  const plan = planLabelChange([], { category: 'bug', state: 'needs-info' }, DEFAULTS, false);
  assert.deepEqual(plan.add.sort(), ['bug', 'needs-info']);
  assert.deepEqual(plan.remove, []);
  assert.deepEqual(plan.conflict, []);
});

test('planLabelChange Agent 接管状态时移除旧状态标签', () => {
  const plan = planLabelChange(
    ['bug', 'needs-info'],
    { category: 'bug', state: 'ready-for-agent' },
    DEFAULTS,
    true,
  );
  assert.deepEqual(plan.add, ['ready-for-agent']);
  assert.deepEqual(plan.remove, ['needs-info']);
});

test('planLabelChange 非接管时报告冲突但不删除', () => {
  const plan = planLabelChange(
    ['bug', 'needs-triage'],
    { category: 'bug', state: 'ready-for-agent' },
    DEFAULTS,
    false,
  );
  assert.deepEqual(plan.add, ['ready-for-agent']);
  assert.deepEqual(plan.remove, []);
  assert.deepEqual(plan.conflict, ['needs-triage']);
});

test('hashDecision 稳定且对内容敏感', () => {
  const a = hashDecision({ ...sample, brief: null });
  const b = hashDecision({ ...sample, brief: null });
  const c = hashDecision({ ...sample, state: 'ready-for-agent', brief: null });
  assert.equal(a, b);
  assert.notEqual(a, c);
  assert.equal(a.length, 8);
});

test('buildComment 包含标记、免责声明与置信度', () => {
  const d = { ...sample, hash: 'abcd1234' };
  const body = buildComment(d, DEFAULTS, { isPR: false });
  assert.match(body, new RegExp(`<!-- ${MARKER} hash=abcd1234 -->`));
  assert.match(body, /AI 在 triage/);
  assert.match(body, /82%/);
  assert.match(body, /needs-info/);
});

test('buildComment 对冲突状态给出提示', () => {
  const d = { ...sample, hash: 'abcd1234' };
  const body = buildComment(d, DEFAULTS, { isPR: false, conflict: ['needs-triage'] });
  assert.match(body, /未自动修改/);
});

test('buildComment 在缺少 comment 时回退到模板', () => {
  const d = { ...sample, comment: '', hash: 'x' };
  const body = buildComment(d, DEFAULTS, {});
  assert.match(body, /分诊记录/);
  assert.match(body, /proxy\.log/);
});

test('buildComment 截断超长正文', () => {
  const d = { ...sample, comment: 'x'.repeat(9000), hash: 'x' };
  const body = buildComment(d, { ...DEFAULTS, maxCommentChars: 1000 }, {});
  assert.ok(body.length < 1100);
});

test('renderPrompt 替换全部占位符', () => {
  const out = renderPrompt('{{REPO}}|{{REPO_SUMMARY}}|{{DOMAIN_TERMS}}|{{KNOWN_LIMITATIONS}}|{{LANGUAGE}}', {
    repo: 'a/b',
    repoSummary: '摘要',
    domainTerms: { '403': '核心场景' },
    knownLimitations: ['限制一'],
    language: 'zh-CN',
  });
  assert.equal(out, 'a/b|摘要|- **403**：核心场景|- 限制一|zh-CN');
});

test('buildMessages 包含标题、评论、diff 与 README', () => {
  const item = {
    number: 7,
    title: '直连返回 403',
    body: '如题',
    user: { login: 'alice' },
    author_association: 'NONE',
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-02T00:00:00Z',
    state: 'open',
    labels: [],
    pull_request: { url: 'https://api.github.com/repos/a/b/pulls/7' },
  };
  const comments = [{ user: { login: 'bob' }, created_at: '2026-01-01T01:00:00Z', body: '同遇到' }];
  const repoCtx = {
    repoInfo: { default_branch: 'main' },
    paths: ['README.md', 'src/app.js', 'bin/ignored.js'],
    readme: '# Hello',
    outOfScope: ['### .out-of-scope/dark-mode.md\n\n不行'],
    files: ['### README.md\n\n```md\n# Hello\n```'],
  };
  const [sys, user] = buildMessages({
    item,
    comments,
    diff: '+ added line',
    repoCtx,
    cfg: DEFAULTS,
    promptText: 'SYSTEM',
  });
  assert.equal(sys.role, 'system');
  assert.equal(sys.content, 'SYSTEM');
  assert.match(user.content, /#7/);
  assert.match(user.content, /直连返回 403/);
  assert.match(user.content, /同遇到/);
  assert.match(user.content, /\+ added line/);
  assert.match(user.content, /README（节选）/);
  assert.match(user.content, /dark-mode/);
  assert.doesNotMatch(user.content, /bin\/ignored\.js/);
});

test('truncate 保留短文本并标注截断', () => {
  assert.equal(truncate('abc', 10), 'abc');
  assert.match(truncate('abcdef', 3), /已截断/);
});