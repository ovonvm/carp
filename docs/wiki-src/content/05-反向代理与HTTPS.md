<!-- slug: proxy -->
<!-- chapter: 开始 -->
# 5. 反向代理与 HTTPS

Carp 自身只提供明文 HTTP，默认还只监听本机。要使用域名、HTTPS、或让局域网/公网访问，推荐在最前面放一个反向代理（Nginx 或 Caddy）。

## 5.1 先记住两条约束

1. **Carp 只能挂在根路径**（`https://你的域名/`）。挂在 `/carp/` 这样的子路径会无法加载前端资源，请用独立域名或子域名。
2. **反代必须保留原始 Host（含端口）**。Nginx 里要用 `$http_host`，不要用 `$host`；否则登录后的写操作会因 CSRF/Origin 校验失败而报 403。

## 5.2 Nginx 示例（HTTP 先跑通）

```nginx
server {
    listen 80;
    server_name carp.example.com;

    client_max_body_size 8g;

    location / {
        proxy_pass http://127.0.0.1:50011;

        proxy_set_header Host $http_host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # 保持 nginx 默认缓冲：实测关闭代理缓冲 / 指定 proxy_http_version 1.1
        # 会显著拖慢大文件上传（32 MiB 分片约 1 MB/s）。
    }
}
```

```bash
sudo nginx -t && sudo systemctl reload nginx
```

## 5.3 加上 HTTPS

用证书工具（certbot 等）签发证书后，把 server 改为 443，并把 HTTP 重定向到 HTTPS：

```nginx
server {
    listen 443 ssl http2;
    server_name carp.example.com;
    ssl_certificate     /etc/letsencrypt/live/carp.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/carp.example.com/privkey.pem;

    client_max_body_size 8g;
    location / {
        proxy_pass http://127.0.0.1:50011;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Real-IP $remote_addr;
    }
}

server {
    listen 80;
    server_name carp.example.com;
    return 301 https://$host$request_uri;
}
```

启用 HTTPS 后，建议在 `carp.env` 中设置 `CARP_COOKIE_SECURE=always`（或先设为 `auto` 观察），然后重启服务。

## 5.4 Caddy 示例

Caddy 会自动申请证书，配置更短：

```caddyfile
carp.example.com {
    reverse_proxy 127.0.0.1:50011 {
        transport http {
            read_timeout 300s
        }
    }
    request_body {
        max_size 8GB
    }
}
```

## 5.5 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 页面白屏、静态资源 404 | 挂在子路径 | 改为根路径或独立子域名 |
| 登录后一切正常，但上传/删除报 403 | Host 被改写（用了 `$host` 丢了端口）或 Origin 不匹配 | 改回 `Host $http_host`；必要时配置 `CARP_ALLOW_ORIGINS` |
| 大文件上传失败 | 代理请求体上限太小 | 提高 `client_max_body_size`（示例 8g） |
| 上传速度只有 1 MB/s 左右 | 代理被配置成不缓冲 | 移除 `proxy_request_buffering off`、`proxy_buffering off`、`proxy_http_version 1.1`，保持 nginx 默认 |
| 长下载中断 | 网络抖动/代理空闲超时 | 一般无需改；确需时再调 `proxy_read_timeout` |
| 反代后所有用户显示同一 IP | 未传或未信任 `X-Forwarded-For` | Carp 默认按 `RemoteAddr` 计限流；在可信反代后按需配置 |

## 5.6 安全清单

- 公网暴露前：**改掉初始密码**，为每个使用者单独建账号 (不要共用 `admin`)。
- 注册功能默认关闭；需要开放时，在「系统设置」里打开，并确认有备份与访问控制。
- Carp 内置登录失败锁定（连续失败会临时锁定账号），但**没有 IP 级限流**；公网部署建议在反向代理/WAF 上补全局限流。
- 定期备份（「7. 升级、备份与迁移」），并至少做一次恢复演练。
- 不要把 `127.0.0.1:50011` 明文端口直接映射到公网。
