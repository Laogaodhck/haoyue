# Haoyue 发布手册

面向维护者。Haoyue 采用「统一版本号 + Git Tag 驱动」的发布流程：CLI、桌面端与 npm 包共享同一个版本号（`major.minor.patch`），推送 `vX.Y.Z` tag 后由 GitHub Actions 自动完成构建与发布。

## 版本号来源

统一版本号同时存在于以下三处，必须始终一致，统一由 `version.py` 管理：

| 位置 | 作用 |
| --- | --- |
| `haoyue_cli/Ui/Banner.cs` | CLI 横幅与版本输出的展示来源 |
| `haoyue_desktop/package.json` | 桌面端应用与安装包版本 |
| `packaging/npm/staging/packages/*/package.json` | npm 包快照（含 optionalDependencies 平台子包） |

- **CI 强校验**：每次推送 `main` 时运行 `python version.py check`，版本漂移直接失败；发布流水线额外校验 tag 与版本号一致。
- **本地构建自动同步**：`build.py` 默认将版本号 patch+1 并同步全部版本源（构建失败自动回滚）；加 `--use-current-version` 则不递增（CI 使用该模式）。
- 查看与手动操作：`python version.py`（报告）、`current`（输出统一版本）、`set X.Y.Z`（一键同步）。

## 标准发版流程

```bash
# 1. 同步版本号并自检
python version.py set 1.4.0
python version.py check --tag v1.4.0

# 2. 提交并打标签推送
git add -A
git commit -m "chore: 发布 v1.4.0"
git tag v1.4.0
git push origin main --tags
```

3. 观察 Actions → 「Release」流水线（约 15–25 分钟）：

   `validate`（版本校验）→ `cli`（6 平台 CLI 打包，配置了 NPM_TOKEN 则同步发布 npm）→ `desktop-windows` / `desktop-linux`（并行）→ `release`（创建 GitHub Release 并附全部产物与校验和）。

4. 在 GitHub Releases 核对产物（见下表）。

## 发布产物

| 产物 | 说明 |
| --- | --- |
| `haoyue-cli-<版本>-<rid>.zip / .tar.gz` | CLI 自包含二进制 ×6（win/linux/macos × x64/arm64）；Windows 为 `.zip`，其余为 `.tar.gz` |
| `Haoyue-Setup-<版本>-win-x64.exe` | Windows NSIS 安装包（未签名，安装时有 SmartScreen 提示） |
| `Haoyue-portable-<版本>-win-x64.zip` | Windows 免安装便携版 |
| `Haoyue-portable-<版本>-linux-x64.tar.gz` | Linux 免安装便携版 |
| `Haoyue-<版本>_amd64.deb` / `Haoyue-<版本>.x86_64.rpm` | Debian/Ubuntu 与 RHEL/Fedora 安装包 |
| `SHA256SUMS.txt` | 全部产物的 SHA-256 校验和 |
| npm `haoyue-cli` 及平台子包 | 需配置 `NPM_TOKEN`，见下文 |

## 启用 npm 自动发布（一次性）

1. 登录 npmjs.com → Access Tokens → 生成 **Automation** 类型 token（该类型可绕过 2FA，适合 CI）。
2. GitHub 仓库 → Settings → Secrets and variables → Actions → New repository secret，名称 `NPM_TOKEN`。
3. 未配置时流水线自动跳过 npm 发布，其余产物照常。

注意：npm 不允许重复发布同一版本号；若发布在个别包上失败，可对缺失的包手动补发（`npm publish <平台包目录>`），或递增版本后重发。

## 干跑（不产生任何发布）

Actions → 「Release」→ Run workflow（手动触发）。将按当前仓库版本完整构建全部产物并上传为工作流附件（Artifacts），**不**创建 GitHub Release、**不**发布 npm。适合验证流水线改动。

## 本地发布（保留流程）

```bash
# 桌面端全量：自动 patch+1 并同步全部版本源（交互式菜单）
python build.py

# 桌面端：按当前版本构建（与 CI 一致，不递增）
python build.py --platform windows --target all --use-current-version

# CLI → npm（默认仅 win-x64；--rids 支持逗号分隔多平台）
python publish_npm.py --rids win-x64 --pack      # 仅生成 tarball
python publish_npm.py --rids win-x64 --publish   # 打包并发布到 npm

# 生成 GitHub Release 分发归档（zip / tar.gz，附 LICENSE 与 README）
python publish_npm.py --rids win-x64 --pack --release-assets dist/cli
```

Linux 产物可追加 `--sign` 生成 SHA256SUMS 与 GPG 分离签名（内置默认签名密钥指纹，可用环境变量 `HAOYUE_GPG_KEY_ID` 覆盖）。

## 故障排查

| 现象 | 处理 |
| --- | --- |
| `validate` 失败：版本不一致 | 运行 `python version.py set X.Y.Z`，提交后重新打 tag 推送 |
| tag 与版本号不符 | 删除并重建 tag：`git tag -d vX.Y.Z && git push origin :refs/tags/vX.Y.Z`，再重新打 |
| npm 提示版本已存在 | npm 不支持覆盖发布；递增版本重发 |
| 桌面端 electron 下载缓慢/失败 | 流水线已固定使用官方下载源；本地默认走 npmmirror 镜像，可用 `ELECTRON_MIRROR` 覆盖 |
| 需要重跑 | Actions → 该次运行 → Re-run failed jobs |
