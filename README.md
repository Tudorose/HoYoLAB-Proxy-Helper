# HoYoLAB-Proxy-Helper

为 [Snap.Hutao.Remastered（胡桃工具箱）](https://github.com/DGP-Studio/Snap.Hutao) 设计的 **HoYoLAB 连接助手**：通过自建 Cloudflare Worker 反向代理 + 本地插件，解决中国大陆直连 HoYoLAB 时被 Cloudflare 拦截（403）的问题。

---

## ✨ 功能特性

| 模块 | 能力 |
|---|---|
| 🔌 插件 | 拦截 HoYoLAB / HoYoverse 系域名 HTTPS 请求，改写为上游 Worker 请求（携带访问令牌） |
| 🚀 常驻辅助 | ProxyHelper 登录自启，代理先于胡桃就绪，重启胡桃零等待 |
| 🧹 自动清理 | 插件禁用/卸载时：停辅助进程、移除自启项、还原系统代理、卸载 CA 证书 |
| ⚙️ 设置页 | 插件内可视化配置，无需手动改文件 |

## 🏗 工作原理

```
启动胡桃
  └─ 插件 OnLoad → 拉起/复用 ProxyHelper（本地 127.0.0.1:8888）
       ├─ 系统代理指向 8888
       └─ HoYoLAB 域名请求被改写为上游 Worker 请求（携带访问令牌）
胡桃退出
  └─ helper 常驻（Run 键自启模式），代理持续可用
禁用/卸载插件
  └─ 杀 helper + 删 Run 键 + 清系统代理（卸载还会移除 CA）
```

## 📦 目录结构

```
├── HutaoProxyPlugin/   插件源码（C#，嵌入 TLS 改写代理）
├── ProxyHelper/        常驻辅助进程源码
├── worker/             Cloudflare Worker 反代（worker.js + wrangler.jsonc）
├── scripts/            一键安装与发布脚本
└── release/            构建产物输出
```

## 🚀 快速开始

### 前提

- 一个域名（可在 Cloudflare 购买，约 4~8 美元/年），DNS 托管在 Cloudflare
- `workers.dev` 默认路由国内 DNS 污染严重，**必须绑定自定义域名**

### 第一步 · 部署 Worker

1. Cloudflare 控制台创建 Worker，粘贴 `worker/worker.js`，配置环境变量 `PROXY_TOKEN`（自定义访问令牌）。鉴权三通道任选：`x-proxy-token` 请求头 / `?t=<令牌>` 查询参数（自动种 Cookie）/ `proxy_token` Cookie
2. 绑定自定义域名（二选一）：
   - `worker/wrangler.jsonc` 的 `routes` 加入 `{ "pattern": "api.你的域名", "custom_domain": true }`，执行 `wrangler deploy`
   - 或控制台 → Worker → Settings → Domains & Routes → Add → Custom domain
3. 记下绑定地址，如 `https://api.你的域名`


> 另支持 `/gh/<路径>` 前缀模式：`https://api.你的域名/gh/用户名/仓库` 可直接代理 GitHub 任意页面（通用镜像，不含任何私有域名）。

### 第二步 · 安装插件

- 一键安装（需要 Node.js）：

  ```
  npx github:Tudorose/HoYoLAB-Proxy-Helper
  ```

- 或手动：下载 [最新 Release](https://github.com/Tudorose/HoYoLAB-Proxy-Helper/releases/latest) 的 `.hutao`，放入 `%LOCALAPPDATA%\SnapHutaoRemastered\Plugins\`

### 第三步 · 配置

重启胡桃后在插件管理中启用，设置页配置：

| 设置项 | 说明 |
|---|---|
| `ProxyBase` | 你的 Worker 地址，如 `https://api.你的域名` |
| `ProxyToken` | 与 Worker 的 `PROXY_TOKEN` 一致 |
| `加快插件启动速度` | 开：常驻辅助进程 + 登录自启（推荐）；关：代理仅随胡桃进程存在 |

保存后**完全重启胡桃工具箱**生效。

## 🔨 构建

需要 .NET SDK（net11.0）与本机安装的胡桃主程序（引用 `Snap.Hutao.Remastered.dll`）：

```
dotnet build HutaoProxyPlugin/HutaoProxyPlugin.csproj
dotnet build ProxyHelper/ProxyHelper.csproj
```

构建脚本会把 ProxyHelper 打进 `.hutao` 包（`lib/ProxyHelper/`）。

## 🛠 维护者：一键发布

```
powershell -File scripts/release.ps1 -Version 1.0.1
```

自动完成：更新 manifest 版本 → 构建 → 隐私扫描（模式来自 `RELEASE_PRIVACY_PATTERNS`）→ 提交/打 tag → 创建 Release 并上传 `.hutao`。

## ⚠️ 已知限制

- **禁用后立即启用**：插件框架为 collectible ALC 异步卸载，禁用后约 1~3 分钟内重新启用可能失败，稍候重试即可
- **启动窗口期**：关闭"加快插件启动速度"时，胡桃启动后最初几秒的 HoYoLAB 请求可能 403，点击重试即可

## 🔒 隐私

- 不收集、不上传任何数据；日志仅写本地 `proxy.log`
- 访问令牌仅保存在本地插件设置中，随请求头发送给**你自己配置的** Worker
- 仓库源码不含任何令牌、账号或个人域名配置

## 🤖 问题分诊（Triage）

仓库内置 AI 分诊 Agent：issue / PR 打开或收到评论时自动**分类 + 判定状态 + 打标签 + 写分诊说明**，每天兜底扫描未分诊条目。

- 分类：`bug` / `enhancement`；状态：`needs-triage` / `needs-info` / `ready-for-agent` / `ready-for-human` / `wontfix`
- Agent **只分类不关闭** issue，所有评论都带 AI 免责声明
- 配置与用法见 [docs/TRIAGE.md](docs/TRIAGE.md)
## 📄 License

MIT



