# Carp 2.3

单文件自托管网盘：一个可执行文件提供网页界面、文件管理、在线播放、分享、离线下载与多账号管理。数据保存在本地磁盘（SQLite + 对象块），前面挂一个反向代理即可对外提供服务。

由 dsh（蓝色大肥鱼）制作，仓库作者参与设计规划开发。

- **单文件部署**：Go 编译的单个二进制，前端资源已内嵌，无外部依赖。
- **数据自己掌握**：SQLite WAL + 本地对象存储；支持多本地存储根（管理员可配）。
- **多账号**：支持注册（可关）、三标识登录（用户名/邮箱/手机号）、账号注销（3 天宽限）。
- **文件管理**：目录、上传/断点续传、重名冲突处理、复制、移动、重命名、回收站（7 天）、搜索、批量操作。
- **分享**：密码、有效期、次数上限、合并分享，公开页无需登录。
- **在线播放**：全站浮层播放器；音频（频谱、歌词、倍速、记忆续播）、视频（字幕、全屏、播放列表），浏览器原生解码。
- **离线下载**：HTTP/HTTPS 直链 + qBittorrent 磁力/种子（需自备 qB，部署方通过环境变量配置）。
- **组共享空间**：管理员/被委派用户创建组、分配成员权限，组内文件在成员间共享。
- **管理后台**：用户、组、审计日志、系统设置、存储策略；数据可通过脚本备份/恢复。

## 快速开始

### Windows

1. 解压便携包（或安装包）后双击 `Carp.exe`，不需要管理员权限。
2. 首次启动会创建管理员 `admin`，面板弹窗显示初始密码（只显示一次），记下后关闭弹窗。
3. 点面板上的「打开网盘」，或浏览器打开 <http://127.0.0.1:50011> 登录，进入「设置」修改密码。

> 桌面面板是自包含单文件：不需要 .NET 运行时，也不需要管理员权限。`Carp.exe` 旁边固定只有三样东西：`data/`（数据）、`conf/`（配置与日志）、`core/`（释放的核心）。
> 整个文件夹拷走即可迁移，不需要动注册表或 `%APPDATA%`。
> 关闭窗口**不会**停服务：面板会收进托盘（右下角通知区域，新图标通常在溢出箭头里），服务继续在后台运行。左键托盘图标唤回面板，右键菜单是「打开面板 / 打开网盘 / 启动·停止服务 / 退出」，只有「退出」才会停服务并结束进程。
> 勾选「开机自启」后，登录 Windows 时面板静默进托盘并自动启动服务。
> **旧的 `data/` 目录不会自动迁移**：升级到本版本后面板从空数据开始，旧目录原样保留。需要沿用旧数据时，把旧 `data/` 里的 `db/`、`storage/`、`bootstrap/` 原样拷贝到 `Carp.exe\data\` 再启动。

### Linux

```bash
# 解包（包内含 carp / install.sh / systemd unit / 配置模板 / 运维脚本）
tar xzf carp-<ver>-linux-amd64.tar.gz
cd carp-<ver>-linux-amd64

# 自动安装为 systemd 服务（推荐，需 root）
sudo ./install.sh

# 或者便携直跑（无需 root，数据在可执行文件旁的 data/）
./carp serve
```

安装完成后会打印「Carp 安装完成」信息框：服务地址、局域网 IP、管理员账号、初始密码（只显示一次）、数据目录、配置文件、密码文件和本次服务状态；`--no-credentials` 时不显示密码本身。默认只监听 `127.0.0.1:50011`，外部访问请参考手册配置 Nginx/Caddy 反向代理。

## 数据与配置

- Linux 服务安装：数据在 `/opt/carp/data`；Linux 便携或命令行运行：默认 `<可执行文件目录>/data`（可用 `-data` 指定）。
- Windows 桌面面板（便携布局）：`Carp.exe` 同目录下只有 `data/`（核心数据：`db/`、`storage/`、`tmp/`、`bootstrap/`）、`conf/`（面板配置 `gui.json`、日志 `logs/gui-YYYYMMDD.log`、备份 `backups/`）、`core/`（释放的 `carp-core.exe`）。不再写 `%ProgramData%` 或 `%APPDATA%`；exe 目录不可写（例如解压到 `Program Files`）时整棵树回退到 `%LOCALAPPDATA%\Carp`。
- 面板配置在 `conf/gui.json`（端口、profile、日志级别、开机自启、最近备份位置）；面板日志在 `conf/logs/gui-YYYYMMDD.log`，按天轮转、保留 7 天。
- 面板「立即备份」选择目标目录后打包数据；运行中备份会先做 SQLite 快照。为避免泄漏凭据，`secret.key`、`bootstrap/`、`tmp/` 不打进备份包，恢复后需在面板「重置 root 密码」重新设置管理员密码。
- 初始管理员密码保存在 `<程序目录>/data/bootstrap/initial-admin.txt`；修改密码后该文件自动删除。
- 主要配置项（flag 或环境变量，`CARP_` 前缀）：`-addr`/`CARP_ADDR`、`-data`/`CARP_DATA_DIR`、`-profile`/`CARP_PROFILE`、`-cookie-secure`/`CARP_COOKIE_SECURE`、`CARP_ALLOW_ORIGINS`；离线下载需要 `CARP_QB_URL`、`CARP_QB_USERNAME`、`CARP_QB_PASSWORD`。
- Linux 安装后可运行 `carp` 打开运维菜单（状态行显示访问地址；启停会显示进度与结果；另有 doctor、日志、备份、升级、重置管理员密码、卸载）。

## 构建

仓库不提交二进制，发布产物由 GitHub Actions 在打 `v*` tag 时构建（`.github/workflows/release.yml`）。本地构建：

```bash
./scripts/build.sh        # 前端 + Go 核 + Linux 包
./scripts/build-gui.sh    # Windows 桌面面板（需要 .NET 8 SDK）
```

工具链：Go 1.27+、Node 24 + pnpm 11（前端）、.NET 8 SDK（仅 Windows 面板）。

## 文档

完整使用手册（安装、代理、账号、文件、播放、分享、离线下载、管理后台、FAQ）：

**<https://ovonvm.github.io/carp/>**

手册源码在 [`docs/wiki-src/`](docs/wiki-src/)（Markdown），生成结果在 [`docs/wiki/`](docs/wiki/)。

## 目录结构

```
cmd/            carp 主程序与启动器
internal/       服务端（HTTP、领域模型、SQL、存储、任务）
web/            Vue 3 前端（构建产物内嵌进二进制）
gui/            Windows 桌面面板（.NET 8 WPF）
migrations/     SQLite 迁移（内嵌）
scripts/        构建、安装、备份、反向代理示例
docs/wiki/      用户手册（GitHub Pages 发布）
```

## 发布

- 当前版本：`v2.3.0`（2026-11-10）
- Release：<https://github.com/ovonvm/carp/releases>
