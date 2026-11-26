#!/usr/bin/env node
/**
 * Triage agent —— 自动分诊 GitHub issue / PR。
 *
 * 零依赖：仅使用 Node 20+ 内置的 fetch。
 *
 * 用法：
 *   node .github/triage/triage.mjs --number 12 [--kind issue|pr] [--dry-run]
 *   node .github/triage/triage.mjs --all [--dry-run]
 *
 * 环境变量：
 *   GITHUB_TOKEN       GitHub token（Actions 中自动注入）
 *   AI_API_KEY         LLM 密钥（兼容 OpenAI / OPENAI_API_KEY）
 *   AI_BASE_URL        兼容 OpenAI 的接口地址，默认 https://api.openai.com/v1
 */

import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL, fileURLToPath } from 'node:url';

export const MARKER = 'triage-agent:v1';

export const DEFAULTS = {
  language: 'zh-CN',
  disclaimer: '> *本评论由 AI 在 triage（问题分诊）过程中自动生成。*',
  labels: {
    category: { bug: 'bug', enhancement: 'enhancement' },
    state: ['needs-triage', 'needs-info', 'ready-for-agent', 'ready-for-human', 'wontfix'],
    extra: [],
  },
  labelColors: {},
  skipLogins: ['github-actions[bot]', 'dependabot[bot]'],
  coreFiles: [],
  maxContextChars: 24000,
  maxDiffChars: 16000,
  maxCommentChars: 6000,
  maxSweep: 8,
  autoApply: true,
  autoCloseWontfix: false,
};

const LABEL_DOCS = {
  bug: 'Something is broken',
  enhancement: 'New feature or improvement',
  'needs-triage': 'Maintainer needs to evaluate',
  'needs-info': 'Waiting on reporter for more information',
  'ready-for-agent': 'Fully specified, ready for an AFK agent',
  'ready-for-human': 'Needs human implementation',
  wontfix: 'Will not be actioned',
  question: 'Question about usage or configuration',
  duplicate: 'Duplicate of another issue',
  documentation: 'Documentation improvement',
  invalid: 'Not a valid report',
};

/* ------------------------------------------------------------------ */
/* 纯函数（可单测）                                                     */
/* ------------------------------------------------------------------ */

export function truncate(value, max) {
  const s = String(value ?? '');
  if (s.length <= max) return s;
  return `${s.slice(0, max)}\n\n…（已截断，原长度 ${s.length} 字符）`;
}

function asString(v, max = 2000) {
  return typeof v === 'string' ? v.trim().slice(0, max) : '';
}

function asArray(v, max = 10) {
  if (!Array.isArray(v)) return [];
  return v.filter((x) => typeof x === 'string' && x.trim()).map((x) => x.trim()).slice(0, max);
}

/**
 * 把 LLM 输出解析成结构化决策。容忍 ```json 围栏、前后噪声文本。
 */
export function parseDecision(raw) {
  let text = String(raw ?? '').trim();
  const fenced = text.match(/```(?:json)?\s*([\s\S]*?)```/i);
  if (fenced) text = fenced[1].trim();

  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start === -1 || end <= start) throw new Error('LLM 未返回 JSON 对象');

  let obj;
  try {
    obj = JSON.parse(text.slice(start, end + 1));
  } catch (err) {
    throw new Error(`LLM 返回的 JSON 无法解析：${err.message}`);
  }

  const category = obj.category === 'bug' || obj.category === 'enhancement' ? obj.category : null;
  const state = DEFAULTS.labels.state.includes(obj.state) ? obj.state : null;
  if (!category || !state) {
    throw new Error(`LLM 返回了非法的 category/state：${obj.category}/${obj.state}`);
  }

  const confidence = Math.max(0, Math.min(1, Number(obj.confidence ?? 0) || 0));
  const brief =
    obj.brief && typeof obj.brief === 'object'
      ? {
          summary: asString(obj.brief.summary, 300),
          current: asString(obj.brief.current, 2000),
          desired: asString(obj.brief.desired, 2000),
          interfaces: asArray(obj.brief.interfaces, 12),
          acceptance: asArray(obj.brief.acceptance, 12),
          outOfScope: asArray(obj.brief.out_of_scope ?? obj.brief.outOfScope, 12),
        }
      : null;

  return {
    category,
    state,
    confidence,
    summary: asString(obj.summary, 300),
    reasoning: asString(obj.reasoning, 2000),
    questions: asArray(obj.questions, 10),
    brief,
    comment: asString(obj.comment, 8000),
    hash: '',
  };
}

/**
 * 计算标签增删计划。
 * agentOwnsState=true 表示此前由本 Agent 打过状态标签，此时允许移除旧状态标签。
 */
export function planLabelChange(currentLabels, decision, cfg = DEFAULTS, agentOwnsState = false) {
  const current = new Set((currentLabels || []).map((l) => (typeof l === 'string' ? l : l?.name)).filter(Boolean));
  const categoryLabel = cfg.labels.category[decision.category];
  const states = cfg.labels.state;

  const add = [];
  const remove = [];
  if (categoryLabel && !current.has(categoryLabel)) add.push(categoryLabel);
  if (!current.has(decision.state)) add.push(decision.state);

  if (agentOwnsState) {
    for (const s of states) {
      if (s !== decision.state && current.has(s)) remove.push(s);
    }
  }

  const conflict = states.filter((s) => current.has(s) && s !== decision.state);
  return { add, remove, conflict };
}

/** 决策指纹：内容不变时不重复评论。 */
export function hashDecision(decision) {
  return createHash('sha256')
    .update(JSON.stringify([decision.category, decision.state, decision.summary, decision.questions, decision.brief]))
    .digest('hex')
    .slice(0, 8);
}

function fallbackComment(decision) {
  if (decision.state === 'needs-info') {
    const qs = decision.questions.length ? decision.questions.map((q, i) => `${i + 1}. ${q}`).join('\n') : '1. 请补充复现步骤、版本与日志。';
    return `## 分诊记录\n\n**目前已确认：**\n\n- ${decision.summary}\n\n**还需要你提供（@报告人）：**\n\n${qs}\n`;
  }
  return `${decision.reasoning || decision.summary}\n`;
}

export function buildComment(decision, cfg = DEFAULTS, opts = {}) {
  const categoryLabel = cfg.labels.category[decision.category];
  const head = [
    `<!-- ${MARKER} hash=${decision.hash || ''} -->`,
    '',
    cfg.disclaimer,
    '',
    `**分类：** \`${categoryLabel}\`　**状态：** \`${decision.state}\`　**置信度：** ${Math.round(decision.confidence * 100)}%`,
    '',
  ].join('\n');

  const body = (decision.comment || '').trim() || fallbackComment(decision);

  const tail = [];
  if (opts.isPR) tail.push('\n> ℹ️ 这是一个 PR：按「附带代码的 issue」处理。');
  if (opts.conflict && opts.conflict.length) {
    tail.push(`\n> ⚠️ 已存在状态标签 ${opts.conflict.map((c) => `\`${c}\``).join('、')}，与本次判定不同，未自动修改，请维护者确认。`);
  }

  return truncate(head + body + tail.join(''), cfg.maxCommentChars || 6000);
}

export function renderPrompt(template, vars) {
  return template
    .replaceAll('{{REPO}}', vars.repo)
    .replaceAll('{{REPO_SUMMARY}}', vars.repoSummary)
    .replaceAll('{{DOMAIN_TERMS}}', Object.entries(vars.domainTerms).map(([k, v]) => `- **${k}**：${v}`).join('\n'))
    .replaceAll('{{KNOWN_LIMITATIONS}}', vars.knownLimitations.map((x) => `- ${x}`).join('\n'))
    .replaceAll('{{LANGUAGE}}', vars.language);
}

export function buildMessages({ item, comments, diff, repoCtx, cfg, promptText }) {
  const isPR = Boolean(item.pull_request);
  const labels = (item.labels || []).map((l) => l.name).filter(Boolean);

  const meta = [
    '# 条目',
    `- 类型：${isPR ? 'pull request' : 'issue'}`,
    `- 编号：#${item.number}`,
    `- 标题：${item.title}`,
    `- 作者：@${item.user?.login}（${item.author_association}）`,
    `- 创建：${item.created_at}　更新：${item.updated_at}　状态：${item.state}`,
    `- 现有标签：${labels.join(', ') || '（无）'}`,
    '',
    '## 正文',
    truncate(item.body || '（空）', 8000),
    '',
    '## 评论（按时间正序）',
    comments.length
      ? comments
          .map((c) => {
            const isAgent = String(c.body || '').includes(`<!-- ${MARKER}`);
            const body = String(c.body || '').replace(/\r\n/g, '\n').replace(/\n/g, '\n  ');
            return `- @${c.user?.login} · ${c.created_at}${isAgent ? ' · [agent]' : ''}\n\n  ${body}`;
          })
          .join('\n\n')
      : '（无评论）',
    '',
  ].join('\n');

  const pr = diff ? `\n## PR diff（已截断）\n\`\`\`diff\n${truncate(diff, cfg.maxDiffChars)}\n\`\`\`\n` : '';

  const sourcePaths = repoCtx.paths.filter((p) => !/^(bin|obj|release|\.wrangler|\.github)\//.test(p)).slice(0, 300);
  const repo = [
    '# 仓库上下文',
    `默认分支：${repoCtx.repoInfo?.default_branch || 'main'}`,
    '',
    '## README（节选）',
    repoCtx.readme || '（无）',
    '',
    '## 源码文件列表',
    sourcePaths.length ? sourcePaths.map((p) => `- ${p}`).join('\n') : '（无）',
    '',
    repoCtx.outOfScope.length ? `## 历史拒绝记录（.out-of-scope/）\n\n${repoCtx.outOfScope.join('\n\n')}\n` : '',
    repoCtx.files.length ? `## 相关文件\n\n${repoCtx.files.join('\n\n')}` : '',
  ]
    .filter(Boolean)
    .join('\n');

  return [
    { role: 'system', content: promptText },
    { role: 'user', content: `${meta}${pr}\n${repo}` },
  ];
}

/* ------------------------------------------------------------------ */
/* GitHub 客户端                                                        */
/* ------------------------------------------------------------------ */

export class GitHub {
  constructor({ token, repo, api = 'https://api.github.com' }) {
    if (!token) throw new Error('缺少 GITHUB_TOKEN');
    if (!repo || !repo.includes('/')) throw new Error('缺少 GITHUB_REPOSITORY（应为 owner/repo）');
    this.token = token;
    this.repo = repo;
    this.api = api.replace(/\/+$/, '');
  }

  async raw(method, p, { body, headers = {} } = {}) {
    const url = p.startsWith('http') ? p : `${this.api}${p}`;
    const res = await fetch(url, {
      method,
      headers: {
        'User-Agent': 'hoyolab-triage-agent',
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        Authorization: `Bearer ${this.token}`,
        ...(body ? { 'Content-Type': 'application/json' } : {}),
        ...headers,
      },
      body: body ? JSON.stringify(body) : undefined,
    });
    return res;
  }

  async request(method, p, opts = {}) {
    const res = await this.raw(method, p, opts);
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      throw new Error(`GitHub ${method} ${p} -> ${res.status} ${text.slice(0, 300)}`);
    }
    if (res.status === 204) return null;
    const ct = res.headers.get('content-type') || '';
    return ct.includes('json') ? res.json() : res.text();
  }

  async getJSON(p) {
    return this.request('GET', p, { headers: { Accept: 'application/vnd.github+json' } });
  }

  async getText(p, accept = 'application/vnd.github.raw+json') {
    return this.request('GET', p, { headers: { Accept: accept } });
  }

  /** 简单分页：跟随 Link: rel="next" */
  async list(p) {
    const out = [];
    let url = p;
    let guard = 0;
    while (url && guard++ < 10) {
      const res = await this.raw('GET', url);
      if (!res.ok) {
        const text = await res.text().catch(() => '');
        throw new Error(`GitHub GET ${url} -> ${res.status} ${text.slice(0, 300)}`);
      }
      const items = await res.json();
      if (Array.isArray(items)) out.push(...items);
      const link = res.headers.get('link') || '';
      const next = link.match(/<([^>]+)>;\s*rel="next"/i)?.[1];
      url = next ? next.replace(this.api, '') : null;
    }
    return out;
  }
}

function encodePath(p) {
  return String(p).split('/').map(encodeURIComponent).join('/');
}

async function ensureLabels(gh, cfg) {
  const existing = new Set((await gh.list(`/repos/${gh.repo}/labels?per_page=100`)).map((l) => l.name));
  const wanted = [...Object.values(cfg.labels.category), ...cfg.labels.state, ...(cfg.labels.extra || [])];
  for (const name of [...new Set(wanted)]) {
    if (existing.has(name)) continue;
    await gh.request('POST', `/repos/${gh.repo}/labels`, {
      body: {
        name,
        color: (cfg.labelColors && cfg.labelColors[name]) || 'ededed',
        description: LABEL_DOCS[name] || '',
      },
    });
  }
}

/* ------------------------------------------------------------------ */
/* 仓库上下文                                                           */
/* ------------------------------------------------------------------ */

async function buildRepoContext(gh, cfg, item) {
  const repoInfo = await gh.getJSON(`/repos/${gh.repo}`).catch(() => ({}));
  const branch = repoInfo.default_branch || 'main';
  const tree = await gh.getJSON(`/repos/${gh.repo}/git/trees/${encodeURIComponent(branch)}?recursive=1`).catch(() => ({ tree: [] }));
  const paths = (tree.tree || []).filter((t) => t.type === 'blob').map((t) => t.path);

  const readme = await gh.getText(`/repos/${gh.repo}/contents/README.md`).catch(() => '');

  const outOfScope = [];
  const oosPaths = paths.filter((p) => /^\.out-of-scope\/[^/]+\.md$/i.test(p));
  for (const p of oosPaths.slice(0, 8)) {
    const text = await gh.getText(`/repos/${gh.repo}/contents/${encodePath(p)}`).catch(() => '');
    if (text) outOfScope.push(`### ${p}\n\n${truncate(text, 2000)}`);
  }

  const haystack = `${item.title}\n${item.body || ''}`.toLowerCase();
  const mentioned = paths.filter((p) => {
    const base = p.split('/').pop().toLowerCase();
    return base.length > 4 && haystack.includes(base);
  });
  const core = (cfg.coreFiles || []).filter((p) => paths.includes(p));
  const wanted = [...new Set([...core, ...mentioned])].slice(0, 6);

  const files = [];
  for (const p of wanted) {
    const text = await gh.getText(`/repos/${gh.repo}/contents/${encodePath(p)}`).catch(() => '');
    if (text) files.push(`### ${p}\n\n\`\`\`\n${truncate(text, 4000)}\n\`\`\``);
  }

  return { repoInfo, paths, readme: truncate(readme, 6000), outOfScope, files };
}

/* ------------------------------------------------------------------ */
/* LLM                                                                 */
/* ------------------------------------------------------------------ */

export async function callLLM(messages, { baseUrl, apiKey, model, hint = '' }) {
  if (!apiKey) throw new Error('缺少 AI_API_KEY（或 GITHUB_TOKEN，用于 GitHub Models）环境变量');
  const url = `${baseUrl.replace(/\/+$/, '')}/chat/completions`;
  const payload = { model, messages, temperature: 0.2 };

  const send = (body) =>
    fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${apiKey}` },
      body: JSON.stringify(body),
    });

  let res = await send({ ...payload, response_format: { type: 'json_object' } });
  if (res.status === 400) res = await send(payload); // 提供方不支持 response_format

  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`LLM 请求失败 HTTP ${res.status}：${text.slice(0, 300)}${hint ? `\n${hint}` : ''}`);
  }
  const data = await res.json();
  const content = data?.choices?.[0]?.message?.content;
  if (!content) throw new Error('LLM 返回空内容');
  return content;
}

/* ------------------------------------------------------------------ */
/* 分诊主流程                                                           */
/* ------------------------------------------------------------------ */

async function triageOne(gh, cfg, llm, promptText, number, opts = {}) {
  const dryRun = Boolean(opts.dryRun);
  const item = await gh.getJSON(`/repos/${gh.repo}/issues/${number}`);
  const isPR = Boolean(item.pull_request) || opts.kind === 'pr';

  if ((cfg.skipLogins || []).includes(item.user?.login)) {
    return { number, skipped: `机器人 ${item.user?.login}` };
  }

  const comments = await gh.list(`/repos/${gh.repo}/issues/${number}/comments?per_page=100`);
  const agentComments = comments.filter((c) => String(c.body || '').includes(`<!-- ${MARKER}`));
  const lastAgent = agentComments.at(-1);

  const stateLabels = cfg.labels.state;
  const currentLabels = (item.labels || []).map((l) => l.name).filter(Boolean);
  const existingState = stateLabels.filter((l) => currentLabels.includes(l));

  if (lastAgent && !opts.force) {
    const lastAt = new Date(lastAgent.created_at).getTime();
    const newComments = comments.filter((c) => {
      if (String(c.body || '').includes(`<!-- ${MARKER}`)) return false;
      return new Date(c.created_at).getTime() > lastAt;
    });
    if (!newComments.length) return { number, skipped: '无新动态' };
    if (!existingState.includes('needs-info')) {
      return { number, skipped: `状态为 ${existingState[0] || '未标注'}，非等待补充信息` };
    }
  }

  const diff = isPR
    ? await gh.getText(`/repos/${gh.repo}/pulls/${number}`, 'application/vnd.github.diff').catch(() => '')
    : '';

  const repoCtx = await buildRepoContext(gh, cfg, item);
  const messages = buildMessages({ item, comments, diff, repoCtx, cfg, promptText });
  const raw = await callLLM(messages, llm);
  const decision = parseDecision(raw);
  decision.hash = hashDecision(decision);

  const plan = planLabelChange(item.labels || [], decision, cfg, Boolean(lastAgent));
  const alreadyPosted = agentComments.some((c) => String(c.body || '').includes(`hash=${decision.hash}`));
  const commentBody = buildComment(decision, cfg, { isPR, conflict: plan.conflict });

  const applied = { added: [], removed: [], commented: false };
  if (!dryRun && cfg.autoApply) {
    const blockedByConflict = plan.conflict.length > 0 && !lastAgent;
    if (!blockedByConflict) {
      for (const label of plan.remove) {
        await gh.request('DELETE', `/repos/${gh.repo}/issues/${number}/labels/${encodeURIComponent(label)}`);
        applied.removed.push(label);
      }
      if (plan.add.length) {
        await gh.request('POST', `/repos/${gh.repo}/issues/${number}/labels`, { body: { labels: plan.add } });
        applied.added.push(...plan.add);
      }
    }
    if (!alreadyPosted) {
      await gh.request('POST', `/repos/${gh.repo}/issues/${number}/comments`, { body: { body: commentBody } });
      applied.commented = true;
    }
  }

  return {
    number,
    kind: isPR ? 'pr' : 'issue',
    title: item.title,
    decision,
    plan,
    alreadyPosted,
    applied,
    body: commentBody,
  };
}

async function sweep(gh, cfg, llm, promptText, opts = {}) {
  const items = await gh.list(`/repos/${gh.repo}/issues?state=open&per_page=100&sort=created&direction=asc`);
  const results = [];
  for (const item of items) {
    if (results.length >= (cfg.maxSweep || 8)) break;
    if ((cfg.skipLogins || []).includes(item.user?.login)) continue;
    const labels = (item.labels || []).map((l) => l.name);
    if (cfg.labels.state.some((s) => labels.includes(s))) continue;
    const comments = await gh.list(`/repos/${gh.repo}/issues/${item.number}/comments?per_page=100`);
    if (comments.some((c) => String(c.body || '').includes(`<!-- ${MARKER}`))) continue;
    try {
      results.push(await triageOne(gh, cfg, llm, promptText, item.number, opts));
    } catch (err) {
      results.push({ number: item.number, error: err.message });
    }
  }
  return results;
}

/* ------------------------------------------------------------------ */
/* CLI                                                                 */
/* ------------------------------------------------------------------ */

function parseArgs(argv) {
  const args = { number: null, kind: 'issue', all: false, dryRun: false, force: false, summaryFile: null, config: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--all') args.all = true;
    else if (a === '--dry-run') args.dryRun = true;
    else if (a === '--force') args.force = true;
    else if (a === '--number') args.number = Number(argv[++i]);
    else if (a === '--kind') args.kind = argv[++i];
    else if (a === '--summary-file') args.summaryFile = argv[++i];
    else if (a === '--config') args.config = argv[++i];
  }
  return args;
}

async function loadConfig(explicitPath) {
  const file = explicitPath || fileURLToPath(new URL('./config.json', import.meta.url));
  const raw = await fs.readFile(file, 'utf8');
  const cfg = JSON.parse(raw);
  return {
    ...DEFAULTS,
    ...cfg,
    labels: { ...DEFAULTS.labels, ...(cfg.labels || {}) },
    labelColors: { ...DEFAULTS.labelColors, ...(cfg.labelColors || {}) },
  };
}

function writeSummary(results, dryRun) {
  const rows = results.map((r) => {
    if (r.skipped) return `| #${r.number} | — | — | 跳过 | — | ${r.skipped} |`;
    if (r.error) return `| #${r.number} | — | — | ⚠️ 失败 | — | ${r.error} |`;
    return `| #${r.number} | ${r.kind} | \`${r.decision.category}\` | \`${r.decision.state}\` | ${Math.round(r.decision.confidence * 100)}% | ${String(r.decision.summary || '').replace(/\|/g, '/')} |`;
  });
  return [
    '## Triage 结果',
    '',
    dryRun ? '> 本次为 **dry-run**：未写入任何标签或评论。' : '',
    '',
    '| 编号 | 类型 | 分类 | 状态 | 置信度 | 摘要 |',
    '|---|---|---|---|---|---|',
    ...(rows.length ? rows : ['| — | — | — | — | — | 没有需要分诊的条目 |']),
    '',
  ]
    .filter(Boolean)
    .join('\n');
}

async function main(argv) {
  const args = parseArgs(argv);
  const cfg = await loadConfig(args.config);
  const dryRun = args.dryRun || process.env.TRIAGE_DRY_RUN === 'true';

  const gh = new GitHub({
    token: process.env.GITHUB_TOKEN,
    repo: process.env.GITHUB_REPOSITORY,
    api: process.env.GITHUB_API_URL || 'https://api.github.com',
  });

  // 未配置 AI_API_KEY 时，回退到 GitHub Models（用 GITHUB_TOKEN 鉴权，无需额外密钥）
  const hasProviderKey = Boolean(process.env.AI_API_KEY || process.env.OPENAI_API_KEY);
  const useGithubModels = !hasProviderKey && Boolean(process.env.GITHUB_TOKEN);
  const llm = {
    baseUrl: process.env.AI_BASE_URL || (useGithubModels ? 'https://models.github.ai/inference' : 'https://api.openai.com/v1'),
    apiKey: process.env.AI_API_KEY || process.env.OPENAI_API_KEY || (useGithubModels ? process.env.GITHUB_TOKEN : ''),
    model: process.env.AI_MODEL || (useGithubModels ? 'openai/gpt-4o-mini' : 'gpt-4o-mini'),
    hint: useGithubModels
      ? '提示：GitHub Models 需要 Actions 的 models: read 权限；若仍失败，可在仓库 Secret 配置 AI_API_KEY 并按需设置 AI_BASE_URL / AI_MODEL。'
      : '',
  };

  const promptTemplate = await fs.readFile(new URL('./prompt.md', import.meta.url), 'utf8');
  const promptText = renderPrompt(promptTemplate, {
    repo: gh.repo,
    repoSummary: cfg.repoSummary || '',
    domainTerms: cfg.domainTerms || {},
    knownLimitations: cfg.knownLimitations || [],
    language: cfg.language || 'zh-CN',
  });

  if (!args.all && !args.number) {
    throw new Error('请提供 --number <编号> 或 --all');
  }

  if (!dryRun) await ensureLabels(gh, cfg);

  const results = args.all
    ? await sweep(gh, cfg, llm, promptText, { dryRun })
    : [await triageOne(gh, cfg, llm, promptText, args.number, { dryRun, force: args.force, kind: args.kind })];

  const summary = writeSummary(results, dryRun);
  console.log(summary);
  for (const r of results) {
    if (r.error) console.error(`#${r.number}: ${r.error}`);
    if (r.body && dryRun) console.log(`\n----- #${r.number} 将要发布的评论 -----\n${r.body}`);
  }
  if (args.summaryFile) await fs.writeFile(args.summaryFile, summary, 'utf8');

  if (results.some((r) => r.error)) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main(process.argv.slice(2)).catch((err) => {
    console.error(`[triage] 错误：${err.message}`);
    process.exit(1);
  });
}