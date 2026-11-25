# AGENTS.md

本文件给在本仓库工作的 AI Agent（Codex / Copilot 等）提供仓库级约定。

## 项目是什么

`HoYoLAB-Proxy-Helper` 是 [Snap.Hutao.Remastered（胡桃工具箱）](https://github.com/DGP-Studio/Snap.Hutao) 的插件，通过 **自建 Cloudflare Worker 反代 + 本地 TLS 改写代理**，解决中国大陆直连 HoYoLAB 被 Cloudflare 403 拦截的问题。

| 目录 | 作用 |
|---|---|
| `HutaoProxyPlugin/` | C# 插件，内嵌 TLS 改写代理（`EmbeddedProxy.cs`）、CA 证书安装（`CertKit.cs`）、系统代理切换（`SystemProxySwitch.cs`）、helper 进程管理（`HelperProcess.cs`） |
| `ProxyHelper/` | 常驻辅助进程（`Program.cs`），支持 `--machine` 机器级模式 |
| `worker/` | Cloudflare Worker 反代（`worker.js` + `wrangler.jsonc`） |
| `scripts/` | 安装脚本（`install.js`）与发布脚本（`release.ps1`） |

## 构建与测试

```bash
# 构建（需要 .NET SDK net11.0 + 本机安装胡桃主程序，引用 Snap.Hutao.Remastered.dll）
dotnet build HutaoProxyPlugin/HutaoProxyPlugin.csproj
dotnet build ProxyHelper/ProxyHelper.csproj

# 测试（Node 20+，零依赖）
node --test
```

发布：`powershell -File scripts/release.ps1 -Version 1.0.1`

## 提交信息

使用 Conventional Commits：`feat:` / `fix:` / `docs:` / `chore:` / `refactor:`。

## Triage（问题分诊）

本仓库启用了自动分诊 Agent，详见 [docs/TRIAGE.md](docs/TRIAGE.md)。**手动分诊（例如 `/triage`）沿用同一套标签体系**：

每个条目恰好 **一个分类 + 一个状态**。

**分类**：`bug`（现有行为不符预期）｜`enhancement`（新功能或改进）

**状态**：

| 标签 | 含义 |
|---|---|
| `needs-triage` | 需要维护者评估或做设计决策 |
| `needs-info` | 等待报告人补充信息 |
| `ready-for-agent` | 需求完整、边界清晰、可独立验证，可交给 AFK Agent |
| `ready-for-human` | 需要人工实现、外部凭据、架构取舍或真机测试 |
| `wontfix` | 不处理 |

**辅助标签**：`question`、`duplicate`、`documentation`、`invalid`。

### 规则

1. 任何发到 issue tracker 的 AI 评论，**必须**以下面这行开头：
   `> *本评论由 AI 在 triage（问题分诊）过程中自动生成。*`
2. `ready-for-agent` / `ready-for-human` 必须附 **Agent Brief**：行为化描述，**不写文件路径与行号**，包含可独立验证的验收标准与明确的 Out of scope。
3. **不关闭 issue**。`wontfix` 只给建议，关闭由维护者执行。
4. 被拒绝的 **enhancement** 写入 `.out-of-scope/<concept>.md`（含决策、理由、历史请求）；**已实现**的功能不要写进去。
5. 分诊前先查 `.out-of-scope/*.md` 与已有 issue，避免重复。
6. 不要编造文件路径、函数名、配置项；不确定就写「需维护者确认」。

### 领域速查

- 「403 / Cloudflare 拦截」→ 通常是 bug
- 「ProxyBase / ProxyToken 填错、Worker 域名没绑」→ 通常是配置问题，判 `needs-info` 并给具体核对步骤
- 「禁用后立刻启用失败」→ 已知限制（collectible ALC 异步卸载，1~3 分钟）
- 「关闭加快启动后头几秒 403」→ 已知限制
- 「workers.dev 打不开」→ 已知限制（国内 DNS 污染，必须绑自定义域名）