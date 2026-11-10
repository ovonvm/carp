<!-- slug: linux -->
<!-- chapter: 开始 -->
# 4. Linux 部署

Linux 上推荐用安装包自带的 `install.sh`：它会安装程序、创建受限用户、注册 systemd 服务、启动并打印初始管理员密码。也可以用便携方式直接前台运行。

## 4.1 安装包方式（推荐）

```bash
tar -xzf carp-v2.2.1-linux-amd64.tar.gz
cd carp-v2.2.1-linux-amd64
sha256sum -c SHA256SUMS
sudo ./install.sh
```

`install.sh` 是 `carp install` 的薄包装，常用参数：

| 参数 | 作用 | 默认 |
|---|---|---|
| `--prefix <dir>` | 安装目录 | `/opt/carp` |
| `--user <name>` | 运行服务的系统用户 | `carp` |
| `--port <n>` | 监听端口 | `50011` |
| `--data <dir>` | 数据目录 | `<prefix>/data` |
| `--no-service` | 只复制文件，不注册 systemd | 关 |
| `--dry-run` | 只打印将要做什么（可不用 root） | 关 |
| `--no-credentials` | 安装完成信息框中不打印初始管理员密码 | 关 |

安装完成后：

```bash
carp doctor      # 自检
carp             # 运维菜单
```

安装完成后，终端会打印一个「Carp 安装完成」信息框，字段如下（密码只显示这一次）：

| 字段 | 内容 |
|---|---|
| 服务地址 | `http://127.0.0.1:<port>`，默认端口 `50011` |
| 局域网 IP | 检测到的第一个局域网 IPv4 地址；一个都没有时显示「未检测到」。**默认只监听本机**，局域网/公网访问请配置 Nginx/Caddy 反向代理 |
| 管理员账号 | 通常是 `admin` |
| 初始密码 | 首次安装生成的随机密码，只显示这一次；`--no-credentials` 时该栏显示「（未显示；见下方密码文件）」 |
| 数据目录 | 默认 `/opt/carp/data` |
| 配置文件 | `/opt/carp/carp.env` |
| 密码文件 | `<data>/bootstrap/initial-admin.txt`，首次修改密码后自动删除 |
| 服务状态 | systemd 状态和健康检查结果（例如 `active（健康检查 OK）`）；安装时用了 `--no-service` 则改为「服务方式」一行，提示未安装 systemd、需手动运行 `/usr/local/bin/carp serve` |

信息框下方还会提示首次登录后立即修改密码，并列出下一步：`carp doctor`、`carp`（运维菜单）、`systemctl status carp`。

> `--no-credentials` 只隐藏密码本身，信息框的其它字段仍会打印；服务正常启动时密码仍写入上述密码文件。`--dry-run` 只打印安装计划，不执行安装，也不会出现这个信息框。`--no-service` 不启动服务，初始密码要等首次手动运行 `/usr/local/bin/carp serve` 时才生成，信息框会提示稍后查看密码文件。

字段示例（IP、密码和状态随环境不同）：

```text
╔════════════════════════════════════════════════════════════════════════╗
║ Carp 安装完成                                                          ║
╠════════════════════════════════════════════════════════════════════════╣
║ 服务地址    http://127.0.0.1:50011                                     ║
║ 局域网 IP   192.168.1.10（默认仅本机监听；局域网/公网请配 nginx 反代） ║
║ 管理员账号  admin                                                      ║
║ 初始密码    A1b2C3d4E5                                                 ║
║ 数据目录    /opt/carp/data                                             ║
║ 配置文件    /opt/carp/carp.env                                         ║
║ 密码文件    /opt/carp/data/bootstrap/initial-admin.txt                 ║
║ 服务状态    active（健康检查 OK）                                      ║
╚════════════════════════════════════════════════════════════════════════╝
```

> 边框宽度会按内容自适应，上例只示意字段；实际对齐以终端输出为准（`局域网 IP` 较长时框会更宽）。

## 4.2 便携方式

```bash
chmod +x carp
./carp serve
```

- 不需要 root，也不需要 systemd。
- 数据默认写在程序旁边的 `data/` 目录（可用 `-data /path/to/data` 指定）。
- `Ctrl+C` 会优雅退出。
- 适合临时试用，或放进自己的进程管理器（如 supervisord）里托管。

## 4.3 服务管理

安装后可以用 `carp service` 或 `carp` 运维菜单管理服务；启动/停止/重启都会显示进度和结果。

### 命令行

```bash
sudo carp service status       # 只打印状态词
sudo carp service start        # 启动
sudo carp service stop         # 停止
sudo carp service restart      # 重启服务
```

- `status` 仍只输出一行状态，例如 `state: active`。
- `start`/`stop`/`restart` 先打印 `⏳ 正在… Carp 服务……`，成功后打印 `✓ …（耗时 …）`；启动/重启还会显示访问地址、服务状态和健康检查结果，停止会显示最终状态。
- 失败时打印 `✗ …失败：…`，并自动附上 `journalctl -u carp` 最近 30 行；CLI 最后还会输出一行 `carp service: …` 并以非零码退出。也可手动执行 `sudo journalctl -u carp -n 100 --no-pager`。
- 端口会自动从 `<prefix>/carp.env` 的 `CARP_ADDR` 读取，改过端口也不用手工传参；读不到时才回退到 `50011`。

### 运维菜单

安装为 systemd 服务后，直接运行 `sudo carp` 打开交互菜单。状态行会显示：

```text
服务状态：active   健康检查：OK   地址：http://127.0.0.1:50011   管理员：admin
```

输入 `1`/`2`/`3`（启动/停止/重启）后同样先显示 `⏳ 正在…`，再显示 `✓ …（耗时 …）` 或 `✗ …` + 最近日志，不再静默返回。菜单里的失败以 `✗` 后面的错误和日志为准。

### systemctl 等价命令

```bash
sudo systemctl status carp     # 查看状态
sudo systemctl restart carp    # 重启（改配置后）
sudo systemctl stop carp       # 停止
sudo systemctl disable carp    # 取消开机自启
journalctl -u carp -f          # 跟随日志
```

如果安装时用了 `--no-service`，可以手动生成 unit 模板再放到 systemd：

```bash
carp gen-unit --prefix /opt/carp --port 50011 > carp.service
sudo cp carp.service /etc/systemd/system/carp.service
sudo systemctl daemon-reload
sudo systemctl enable --now carp
```

## 4.4 目录与权限

| 路径 | 内容 | 权限建议 |
|---|---|---|
| `/opt/carp/carp` | 程序 | 0755 |
| `/opt/carp/carp.env` | 配置（环境变量） | 0600 |
| `/opt/carp/data/` | 数据库、文件、初始密码 | 0700，属主为运行用户 |
| `/opt/carp/backup/` | 备份输出 | 运行用户可写 |

手工拷贝或移动过数据目录后，务必修正属主：

```bash
sudo chown -R carp:carp /opt/carp/data
```

## 4.5 常用运维命令

| 目的 | 命令 |
|---|---|
| 自检 | `sudo carp doctor` |
| 交互菜单 | `sudo carp`（状态行显示访问地址） |
| 启动/停止/重启 | `sudo carp service start` / `stop` / `restart`（或对应 `systemctl`） |
| 修改地址/端口 | `sudo carp config`（改完 `sudo systemctl restart carp`；`carp service` 会自动读取新端口） |
| 备份 | `sudo /opt/carp/scripts/backup.sh` |
| 恢复演练 | `sudo /opt/carp/scripts/restore.sh --list` |
| 升级 | `sudo carp upgrade --binary <新二进制>` |
| 重置管理员密码 | `sudo carp reset-admin-password` |

## 4.6 升级

```bash
sudo ./scripts/backup.sh                      # 先备份
sudo carp upgrade --binary ./carp-v2.2.1 --prefix /opt/carp --port 50011
```

`carp upgrade` 会：停止服务 → 备份现有二进制 → 替换 → 启动 → 等健康检查通过；失败会自动换回旧二进制。旧的二进制会保留为 `carp.bak-<时间戳>`，需要时手工回滚。

源码仓库里还有 `scripts/upgrade.sh <新二进制>`（不在安装包内），流程类似：自动备份 + 原子替换 + 失败回滚。

## 4.7 卸载

```bash
sudo carp uninstall
```

会停止并禁用服务、删除 unit、删除安装目录（**包括 data 数据**）和系统用户。卸载前请先备份；如果只想保留数据后重装，请先手工拷走 `<prefix>/data`。
