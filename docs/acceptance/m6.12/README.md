# M6.12 · v2.2.6 选中扫光 / 外观卡改造

对应提交：`07ee75a`（feat(ui): replace easter-egg fx with selected-button sheen; theme picker in appearance）
本地构建：`v2.2.6`（Windows x64 `Carp.exe` + Linux x64 tar.gz）

## 变更

- 删除「趣味动效（彩蛋）」：粒子引擎 `web/src/lib/fx.ts`、`FxLayer.vue`、全部调用点（AppShell/UploadDock/FilesView/LoginView/SettingsView），偏好 `fx` 从前后端白名单移除，迁移 `0019_drop_fx_preference.sql` 清理旧键。
- 新增选中扫光（CSS，无双层动画库）：侧栏当前导航项、主题当前选中项（头像菜单 + 设置页外观卡）。
  - 右→左，单次 **1s**，间隔 **5s**，**20s 后停止**（`animation: carp-sweep 5s linear 4`）。
  - `prefers-reduced-motion` 下关闭。
  - 无开关，默认开启。
- 设置页「外观」卡片改为 5 个主题色卡（日落/森林/海岸/极光/暗色），与头像菜单共用账号偏好。
- 版本号注入沿用 v2.2.2 的 `APP_VERSION`（页脚显示 v2.2.6）。

## 验收

Playwright（390×844 与 1554×1044 各一轮，本机 core）：

- 侧栏 `.nav-item.is-active::after` → `animation-name: carp-sweep`、`5s`、`4` 次。
- 设置页 5 张 `.theme-card`，仅当前项有扫光动画；其余 `animation: none`。
- 无 `.fx-toggle`；`[class*=fx]` 元素 0。
- 点「森林」后 `<html data-theme="forest">`，当前卡切换；账号偏好 PUT 成功。
- 21 秒后 `carp-sweep` 动画全部结束（`playStates: []`），不再循环。
- 截图：`01-files-sweep.png`、`02-settings-theme-sweep.png`、`03-settings-forest.png`。
