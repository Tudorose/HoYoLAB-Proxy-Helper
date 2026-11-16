# HoYoLAB-Proxy-Helper

HoYoLAB API connectivity helper for Snap Hutao (胡桃工具箱插件). 通过自建 Cloudflare Worker 反向代理，帮助中国大陆 HoYoLAB 用户解决直连 403 问题。

## 前提

- **你需要拥有一个域名**（可在 Cloudflare 购买，一年约 4~8 美元），并将域名的 DNS 托管在 Cloudflare。

## Worker 部署

1. 在 Cloudflare 控制台创建 Worker，粘贴 `worker/worker.js`，配置环境变量 `PROXY_TOKEN`（自定义访问令牌）。
2. 本仓库的 `worker/wrangler.jsonc` 已关闭 `workers.dev` 默认路由与预览 URL（国内 DNS 对 workers.dev 污染严重），请改为绑定**自定义域名**：
   - `wrangler.jsonc` 的 `routes` 中加入 `{ "pattern": "api.你的域名", "custom_domain": true }`，再 `wrangler deploy`；
   - 或在 Cloudflare 控制台 → Workers → 你的 Worker → Settings → Domains & Routes → Add → Custom domain。
3. 记下绑定的地址，例如 `https://api.你的域名`。

## 插件安装

- 一键安装（需要 Node.js）：

  ```
  npx github:Tudorose/HoYoLAB-Proxy-Helper
  ```

- 或手动：下载 [最新 Release](https://github.com/Tudorose/HoYoLAB-Proxy-Helper/releases/latest) 的 `.hutao`，放入 `%LOCALAPPDATA%\SnapHutaoRemastered\Plugins\`，重启胡桃后在插件管理中启用。

## 插件设置

| 设置项 | 说明 |
|---|---|
| `ProxyBase` | 你的 Worker 地址，如 `https://api.你的域名` |
| `ProxyToken` | 与 Worker 的 `PROXY_TOKEN` 一致 |
| `加快插件启动速度` | 开：常驻辅助进程 + 登录自启（推荐）；关：代理仅随胡桃进程存在 |

保存设置后**完全重启胡桃工具箱**生效。

## 维护者：一键发布

```
powershell -File scripts/release.ps1 -Version 1.0.1
```

自动完成：更新 manifest 版本 → 构建 → 隐私扫描（模式来自 `RELEASE_PRIVACY_PATTERNS`）→ 提交/打 tag → 创建 Release 并上传 `.hutao`。

## 已知限制

- 禁用插件后约 1~3 分钟内重新启用可能失败（宿主插件框架异步卸载），稍候重试即可。
- 关闭“加快插件启动速度”时，胡桃启动后最初几秒的 HoYoLAB 请求可能 403，点击重试即可。

## License

MIT
