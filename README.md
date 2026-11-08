# Carp

Carp 是一个自托管的个人网盘：一个可执行文件放在自己的电脑或服务器上，浏览器就能管理文件、在线播放音视频、创建带密码/有效期的分享链接，并让服务器替你下载文件。

- 单二进制：Go 编写，SQLite 存储，前端静态资源内嵌，不需要 Node/Python/.NET 运行时
- 多账号：每人文件互相隔离，管理后台可管理用户、组、配额、审计
- 文件能力：上传/下载、文件夹、重命名、移动、复制、回收站、按文件名搜索
- 在线播放：音频小窗 + 视频浮层，字幕/歌词/倍速/续播/播放列表
- 分享：单文件或合并分享、密码、有效期、下载次数、在线预览（预览不扣次数）
- 离线下载：HTTP 链接；可选 qBittorrent 下载磁力/种子
- 组空间：多人共享目录，按成员控制六项权限
- 多存储根：本地磁盘、挂载盘可分策略存放
- 低占用：默认 low 配置面向 512 MB 内存的小机器
- 跨平台：Linux amd64/arm64/armv7，Windows x64/arm64（控制面板为单文件 GUI）

## 快速开始

### Linux（安装包）

```bash
tar -xzf carp-vX.Y.Z-linux-amd64.tar.gz
cd carp-vX.Y.Z-linux-amd64
sha256sum -c SHA256SUMS
sudo ./install.sh
```

安装脚本会注册 systemd 服务、生成 `/opt/carp/carp.env`，并在安装摘要中打印管理员初始密码。

### Windows（单文件控制面板）

把 `Carp.exe` 放到单独文件夹，双击运行（需要管理员权限）→ 点「启动服务」→「在浏览器打开网盘」→ 用 `admin` 和首次启动显示的初始密码登录。

### 便携运行

```bash
chmod +x carp
./carp serve
# 浏览器访问 http://127.0.0.1:50011
```

默认只监听 `127.0.0.1:50011`；局域网/公网访问请配置反向代理并保留原始 Host（详见手册）。

## 从源码构建

依赖：Go 1.27.3、Node.js 24、pnpm 11.28.5；构建 Windows GUI 还需要 .NET 8 SDK。

```bash
# 核心（Linux/Windows 交叉编译 + 打包）
bash scripts/build-all.sh

# Windows 单文件控制面板（需要 .NET 8）
bash scripts/build-gui.sh x64
```

## 下载

正式构建产物发布在 GitHub Releases 页面（由 CI 从 tag 自动构建）：本仓库 Releases 页。

## 文档

- 在线手册（GitHub Pages）：<https://ovonvm.github.io/carp/>
- Wiki：<https://github.com/ovonvm/carp/wiki>
- 更新记录：[CHANGELOG.md](CHANGELOG.md)

## 安全与部署提示

- 首次启动会自动生成管理员账号与随机密码，请登录后立即修改
- 对外访问必须使用 HTTPS；反向代理需保留原始 Host（含端口），否则写操作会因 CSRF 校验失败
- 分享链接本身就是访问凭证，建议同时设置密码与有效期
- 离线下载默认拒绝私网/回环地址，仅在可信环境使用 `-allow-private-downloads`
- 数据目录包含数据库与文件内容，备份请成对保存 `db/` 与 `storage/`

## 第三方组件

- 内置 Noto Sans SC 字体子集（SIL Open Font License 1.1）
- 其余 Go/前端依赖见 `go.mod` 与 `web/package.json`

## 状态

当前版本：v2.2.5。项目处于活跃开发中，接口与数据迁移均以向后兼容为原则。
