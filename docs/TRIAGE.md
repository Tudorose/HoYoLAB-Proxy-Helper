# Triage AI 问题分诊 Agent

本仓库内置一个自动分诊 Agent：当 issue / PR 打开、有人评论、或每天定时扫描时，它会用 LLM 读取条目内容与仓库上下文，**分类 + 判定状态 + 打标签 + 写分诊评论**。

Agent **只做分类与说明**：不会关闭 issue、不会改代码、不会声称已复现或已测试。

---

## 1. 启用步骤

### ① 配置模型密钥

仓库 → **Settings → Secrets and variables → Actions → New repository secret**

| 名称 | 必填 | 说明 |
|---|---|---|
| `AI_API_KEY` | ✅ | 兼容 OpenAI 接口的密钥（DeepSeek 为 `sk-...`） |

可选 **Secrets**（同样在 Actions secrets 页面添加，不需要 `actions` 管理权限即可由 CI 使用）：

| 名称 | 默认值 | 说明 |
|---|---|---|
| `AI_BASE_URL` | `https://api.openai.com/v1` | 任意 OpenAI 兼容接口地址 |
| `AI_MODEL` | `gpt-4o-mini` | 模型名 |

当前已配置为 DeepSeek：`AI_BASE_URL=https://api.deepseek.com/v1`、`AI_MODEL=deepseek-chat`。

> 未配置 `AI_API_KEY` 时，脚本会自动回退到 **GitHub Models**（用 `GITHUB_TOKEN` 鉴权，模型 `openai/gpt-4o-mini`），此时无需任何密钥。

### ② 推送工作流

`.github/workflows/triage.yml` 必须存在于**默认分支**（`main`）才会被 issue 事件触发。推送后即生效。

### ③ 手动验证（推荐）

**Actions → Triage Agent → Run workflow**

- `issue_number` 留空 → 扫描全部未分诊条目
- 勾选 `dry_run` → 只输出计划，**不写任何标签/评论**

在 run 的 Summary 里能看到判定表；dry-run 还会把将要发布的评论全文打印到日志。

---

## 2. 触发时机

| 事件 | 行为 |
|---|---|
| `issues` opened / reopened / edited | 立刻分诊 |
| `issue_comment` created | 仅当该条目处于 `needs-info` 且有**新的人工评论**时重新评估 |
| `pull_request_target` opened / ready_for_review | 分诊外部 PR（维护者自己的 PR 跳过） |
| `schedule`（每天 10:17 Asia/Shanghai） | 兜底扫描未分诊条目，最多 8 条/次 |
| `workflow_dispatch` | 手动指定编号或全量扫描 |

同一 issue 的并发运行会串行排队（`concurrency`），不会出现两条评论打架。

---

## 3. 标签体系

每个条目**恰好一个分类 + 一个状态**。缺少的标签会由 Agent 自动创建。

### 分类

| 标签 | 含义 |
|---|---|
| `bug` | 现有行为不符合预期 |
| `enhancement` | 新功能 / 改进 |

### 状态

| 标签 | 含义 |
|---|---|
| `needs-triage` | 需要维护者评估或做设计决策 |
| `needs-info` | 等待报告人补充信息 |
| `ready-for-agent` | 需求完整，可交给 AFK Agent |
| `ready-for-human` | 需要人工实现 / 外部凭据 / 真机测试 |
| `wontfix` | 不处理 |

辅助标签（Agent 会视情况建议，由维护者添加）：`question`、`duplicate`、`documentation`、`invalid`。

---

## 4. 行为与安全边界

| 行为 | 说明 |
|---|---|
| ✅ 自动打标签 | 分类 + 状态；状态流转时会移除 Agent **自己**打过的旧状态标签 |
| ✅ 自动发评论 | 固定以 `> *本评论由 AI 在 triage（问题分诊）过程中自动生成。*` 开头 |
| ✅ 幂等 | 评论带 `<!-- triage-agent:v1 hash=... -->` 指纹，内容不变不重复发 |
| ✅ 尊重人工 | 已存在**维护者手动设置**的状态标签与判定冲突时，**不改标签**，只在评论里提示 |
| ✅ 可关闭 | 仓库变量 `AI_API_KEY` 删除或工作流禁用即停 |
| ❌ 不关闭 issue | `wontfix` 只给建议，关闭由维护者执行 |
| ❌ 不执行代码 | PR 只读元数据与 diff 文本，不 checkout、不构建、不运行 |
| ❌ 不删人工标签 | 只增不删（Agent 自己打的状态标签除外） |

---

## 5. 本地调试

```bash
# 只看计划（推荐先跑这个）
GITHUB_TOKEN=ghp_xxx GITHUB_REPOSITORY=Tudorose/HoYoLAB-Proxy-Helper \
AI_API_KEY=sk-xxx \
node .github/triage/triage.mjs --number 12 --dry-run

# 全量扫描
node .github/triage/triage.mjs --all --dry-run

# 真正写入
node .github/triage/triage.mjs --number 12
```

可选参数：`--kind issue|pr`、`--force`（忽略「无新动态」判断）、`--summary-file <path>`、`--config <path>`。

### 测试

```bash
node --test
```

20 个用例：纯函数单测（JSON 解析、标签计划、评论构造、prompt 渲染）+ 3 个端到端集成测试（本地 mock GitHub / mock LLM，真实跑 CLI）。

---

## 6. 换模型

任何 OpenAI 兼容的 `/chat/completions` 接口都能用，改 `AI_BASE_URL` + `AI_MODEL` 即可。

| 提供方 | `AI_BASE_URL` | `AI_MODEL` |
|---|---|---|
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |
| DeepSeek（国内直连） | `https://api.deepseek.com/v1` | `deepseek-chat` |
| Moonshot / Kimi | `https://api.moonshot.cn/v1` | `moonshot-v1-8k` |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4-flash` |
| Ollama（本地） | `http://127.0.0.1:11434/v1` | `qwen2.5:7b` |
| Azure OpenAI | `https://<res>.openai.azure.com/openai/deployments/<dep>?api-version=2024-10-21` | 由部署决定 |

> 脚本会先尝试 `response_format: {type:"json_object"}`，若提供方返回 400 则自动降级为普通请求，因此老接口也能用。

---

## 7. 定制

| 想改什么 | 改哪里 |
|---|---|
| 分类/判定标准、评论模板、输出语言 | `.github/triage/prompt.md` |
| 项目领域词表、已知限制、核心文件 | `.github/triage/config.json` |
| 免责声明文案、标签颜色、扫描上限 | `.github/triage/config.json` |
| 触发时机、权限、密钥名 | `.github/workflows/triage.yml` |

`config.json` 的 `domainTerms` 会被注入 prompt，用来提高中文场景的判定准确率（例如「403 / Cloudflare 拦截」→ bug，「ProxyBase / ProxyToken 填错」→ needs-info）。

---

## 8. 历史拒绝记录（.out-of-scope/）

`wontfix` 的**增强类**请求建议写入 `.out-of-scope/<concept>.md`，格式：

```markdown
# 概念名

本项目不支持的 X。

## 为什么不做

（具体原因：范围、架构约束、上游限制……）

## 历史请求

- #12: "……"
```

Agent 每次分诊都会读取该目录，新请求与历史拒绝相似时会在评论里点名，避免重复讨论。

> 注意：**已经实现**的功能不要写进 `.out-of-scope/`，那不是拒绝，是已完成。

---

## 9. 常见问题

**Q：没有 OPENAI_API_KEY 怎么办？**
A：用任意 OpenAI 兼容服务，见第 6 节。国内推荐 DeepSeek。

**Q：会不会刷评论？**
A：不会。同一决策指纹只发一次；状态不是 `needs-info` 时，新评论不会触发重估。

**Q：会误判吗？**
A：会。所以 Agent 只打标签 + 写理由，**不关闭、不改代码**。所有评论都带 AI 免责声明，维护者可随时手动改标签。

**Q：PR 会执行贡献者的代码吗？**
A：不会。工作流用 `pull_request_target`，只通过 API 读取 PR 元数据与 diff 文本，不 checkout PR 分支、不构建、不运行。

**Q：成本？**
A：每条约 3k~10k input tokens（README + 相关文件 + diff）。`gpt-4o-mini` 下单条成本可忽略；日扫描上限 8 条。