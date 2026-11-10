// Package installer implements the Linux install/upgrade/service operations
// behind the `carp install|uninstall|service|config|gen-unit|upgrade`
// subcommands and the interactive ops menu (M6.4).
//
// Everything here is Linux specific by nature (systemd, useradd); the CLI
// refuses to run these commands on other platforms. The functions are small
// and synchronous so the menu and the command line share one implementation.
package installer

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Options carry the install-time choices. Defaults are filled by Defaults.
type Options struct {
	Prefix     string
	User       string
	Port       string
	DataDir    string
	BinaryPath string

	DryRun        bool
	NoService     bool
	NoCredentials bool
	Yes           bool

	Stdout io.Writer
	Stderr io.Writer
}

// Credentials is the first administrator account created on the first start.
type Credentials struct {
	Username string
	Password string
	FilePath string
}

// Defaults returns the documented defaults: /opt/carp, user carp, port 50011.
func Defaults() Options {
	return Options{
		Prefix: "/opt/carp",
		User:   "carp",
		Port:   "50011",
		Stdout: os.Stdout,
		Stderr: os.Stderr,
	}
}

func (o *Options) normalize() {
	if o.Prefix == "" {
		o.Prefix = "/opt/carp"
	}
	if o.User == "" {
		o.User = "carp"
	}
	if o.Port == "" {
		o.Port = "50011"
	}
	if o.DataDir == "" {
		o.DataDir = filepath.Join(o.Prefix, "data")
	}
	if o.Stdout == nil {
		o.Stdout = os.Stdout
	}
	if o.Stderr == nil {
		o.Stderr = os.Stderr
	}
}

func (o Options) addr() string    { return "127.0.0.1:" + o.Port }
func (o Options) envPath() string { return filepath.Join(o.Prefix, "carp.env") }
func (o Options) unitPath() string {
	return filepath.Join("/etc/systemd/system", "carp.service")
}

func (o Options) printf(format string, args ...any) {
	fmt.Fprintf(o.Stdout, format+"\n", args...)
}

func (o Options) logf(format string, args ...any) {
	fmt.Fprintf(o.Stdout, "[carp] "+format+"\n", args...)
}

// Installed reports whether prefix contains an installed binary and unit.
func Installed(prefix string) bool {
	_, err := os.Stat(filepath.Join(prefix, "carp"))
	return err == nil
}

// Install copies the binary, writes config and unit, starts systemd and waits
// for /health. It returns the bootstrap credentials when the service created
// them (first install on fresh data).
func Install(ctx context.Context, opts Options) (Credentials, error) {
	opts.normalize()
	src := opts.BinaryPath
	if src == "" {
		exe, err := os.Executable()
		if err != nil {
			return Credentials{}, fmt.Errorf("cannot locate own binary: %w", err)
		}
		src = exe
	}
	if fi, err := os.Stat(src); err != nil || fi.IsDir() {
		return Credentials{}, fmt.Errorf("source binary %q is not usable: %v", src, err)
	}
	dst := filepath.Join(opts.Prefix, "carp")
	if _, err := os.Stat(dst); err == nil && !opts.DryRun {
		if pid, running := runningFromBinary(dst); running {
			return Credentials{}, fmt.Errorf("refusing to overwrite %s while it is running (pid %d); stop the service or use `carp upgrade`", dst, pid)
		}
	}

	steps := []string{
		fmt.Sprintf("create system user %s", opts.User),
		fmt.Sprintf("prepare %s and %s", opts.Prefix, opts.DataDir),
		fmt.Sprintf("install binary to %s", dst),
		fmt.Sprintf("copy operator scripts to %s/scripts", opts.Prefix),
		fmt.Sprintf("write %s", opts.envPath()),
		fmt.Sprintf("write %s", opts.unitPath()),
		"install symlink /usr/local/bin/carp",
	}
	if !opts.NoService {
		steps = append(steps, "systemctl daemon-reload && enable --now carp")
	}
	if opts.DryRun {
		opts.printf("would perform:")
		for _, s := range steps {
			opts.printf("  - %s", s)
		}
		return Credentials{}, nil
	}

	if !IsRoot() {
		return Credentials{}, fmt.Errorf("install needs root; run: sudo carp install")
	}

	if err := ensureUser(opts.User, opts.Prefix); err != nil {
		return Credentials{}, err
	}
	for _, dir := range []string{opts.Prefix, opts.DataDir, filepath.Join(opts.Prefix, "scripts")} {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return Credentials{}, fmt.Errorf("create %s: %w", dir, err)
		}
	}
	if err := os.Chmod(opts.DataDir, 0o700); err != nil {
		return Credentials{}, err
	}
	if err := chownTo(opts.DataDir, opts.User); err != nil {
		return Credentials{}, err
	}

	if err := copyFile(src, dst, 0o755); err != nil {
		return Credentials{}, fmt.Errorf("install binary: %w", err)
	}
	if err := chownTo(dst, opts.User); err != nil {
		return Credentials{}, err
	}
	opts.logf("installed binary %s", dst)

	// 安装随包脚本（backup/restore/healthcheck），便于 cron 与菜单直接调用。
	pkgDir := filepath.Dir(src)
	for _, name := range []string{"backup.sh", "restore.sh", "healthcheck.sh"} {
		from := filepath.Join(pkgDir, "scripts", name)
		to := filepath.Join(opts.Prefix, "scripts", name)
		if _, err := os.Stat(from); err != nil {
			if os.IsNotExist(err) {
				continue
			}
			return Credentials{}, fmt.Errorf("inspect %s: %w", from, err)
		}
		if err := copyFile(from, to, 0o755); err != nil {
			return Credentials{}, fmt.Errorf("install %s: %w", name, err)
		}
		if err := chownTo(to, opts.User); err != nil {
			return Credentials{}, err
		}
	}
	if from := filepath.Join(pkgDir, "nginx-carp.conf"); fileExists(from) {
		to := filepath.Join(opts.Prefix, "nginx-carp.conf")
		if err := copyFile(from, to, 0o644); err != nil {
			return Credentials{}, fmt.Errorf("install nginx sample: %w", err)
		}
	}

	if _, err := os.Stat(opts.envPath()); os.IsNotExist(err) {
		if err := os.WriteFile(opts.envPath(), []byte(envFile(opts)), 0o600); err != nil {
			return Credentials{}, fmt.Errorf("write env file: %w", err)
		}
	}
	opts.logf("configuration %s", opts.envPath())

	if err := os.WriteFile(opts.unitPath(), []byte(GenUnit(opts.Prefix, opts.User, opts.Port, opts.DataDir)), 0o644); err != nil {
		return Credentials{}, fmt.Errorf("write systemd unit: %w", err)
	}
	opts.logf("systemd unit %s", opts.unitPath())

	if err := os.Symlink(dst, "/usr/local/bin/carp"); err != nil && !os.IsExist(err) {
		return Credentials{}, fmt.Errorf("install /usr/local/bin/carp: %w", err)
	}

	if !opts.NoService {
		if err := systemctl("daemon-reload"); err != nil {
			return Credentials{}, err
		}
		if err := systemctl("enable", "--now", "carp"); err != nil {
			_ = systemctl("disable", "--now", "carp")
			return Credentials{}, fmt.Errorf("%w (see: journalctl -u carp -n 50)", err)
		}
		if err := waitHealthy(ctx, opts.addr(), 20*time.Second); err != nil {
			_ = systemctl("disable", "--now", "carp")
			return Credentials{}, fmt.Errorf("service started but /health did not answer: %w", err)
		}
		opts.logf("service is running at http://%s", opts.addr())
	}

	creds := Credentials{}
	if data, err := os.ReadFile(filepath.Join(opts.DataDir, "bootstrap", "initial-admin.txt")); err == nil {
		creds = parseCredentials(string(data), filepath.Join(opts.DataDir, "bootstrap", "initial-admin.txt"))
	}

	printInstallSummary(opts, creds)
	return creds, nil
}

// Uninstall removes the service, the symlink, the prefix (including data) and
// the system user.
func Uninstall(ctx context.Context, opts Options) error {
	opts.normalize()
	if !IsRoot() {
		return fmt.Errorf("uninstall needs root; run: sudo carp uninstall")
	}
	if opts.DryRun {
		opts.printf("would remove: unit %s, symlink /usr/local/bin/carp, prefix %s (with data), user %s",
			opts.unitPath(), opts.Prefix, opts.User)
		return nil
	}
	_ = systemctl("disable", "--now", "carp")
	_ = os.Remove(opts.unitPath())
	_ = systemctl("daemon-reload")
	_ = os.Remove("/usr/local/bin/carp")
	if err := os.RemoveAll(opts.Prefix); err != nil {
		return fmt.Errorf("remove %s: %w", opts.Prefix, err)
	}
	if err := removeUser(opts.User); err != nil {
		opts.printf("note: system user %s not removed: %v", opts.User, err)
	}
	opts.printf("uninstalled carp (prefix %s, user %s)", opts.Prefix, opts.User)
	return nil
}

// Service runs `systemctl <action> carp` and returns the state.
func Service(action string) error {
	switch action {
	case "status", "start", "stop", "restart":
	default:
		return fmt.Errorf("service: unknown action %q (status|start|stop|restart)", action)
	}
	if err := systemctl(action, "carp"); err != nil {
		return err
	}
	return nil
}

// ServiceStatus returns the one-word systemd state, or "" when unavailable.
func ServiceStatus() string {
	out, err := exec.Command("systemctl", "is-active", "carp").Output()
	state := strings.TrimSpace(string(out))
	if err != nil && state == "" {
		return "unknown"
	}
	return state
}

// GenUnit renders the systemd unit for the given layout.
func GenUnit(prefix, username, port, dataDir string) string {
	if dataDir == "" {
		dataDir = filepath.Join(prefix, "data")
	}
	return fmt.Sprintf(`[Unit]
Description=Carp cloud drive
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=%s
Group=%s
WorkingDirectory=%s
EnvironmentFile=%s
ExecStart=%s serve
Restart=on-failure
RestartSec=3
TimeoutStopSec=20
LimitNOFILE=65536

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=%s

MemoryMax=512M
MemorySwapMax=0
CPUQuota=100%%

StandardOutput=journal
StandardError=journal
SyslogIdentifier=carp

[Install]
WantedBy=multi-user.target
`, username, username, prefix, filepath.Join(prefix, "carp.env"), filepath.Join(prefix, "carp"), prefix)
}

func envFile(opts Options) string {
	return fmt.Sprintf(`# Carp runtime configuration (read by the systemd unit).
CARP_ADDR=%s
CARP_DATA_DIR=%s
CARP_PROFILE=low
CARP_COOKIE_SECURE=auto
`, opts.addr(), opts.DataDir)
}

// ConfiguredPort returns the port configured in <prefix>/carp.env, or 50011.
func ConfiguredPort(prefix string) string {
	data, err := os.ReadFile(filepath.Join(prefix, "carp.env"))
	if err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			if v, ok := strings.CutPrefix(strings.TrimSpace(line), "CARP_ADDR="); ok {
				if _, port, err := net.SplitHostPort(strings.TrimSpace(v)); err == nil && port != "" {
					return port
				}
			}
		}
	}
	return "50011"
}

// ConfigShow prints the current environment file.
func ConfigShow(prefix string, w io.Writer) error {
	path := filepath.Join(prefix, "carp.env")
	data, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	_, err = w.Write(data)
	return err
}

// ConfigEdit interactively updates the well-known keys of the env file.
func ConfigEdit(prefix string, in io.Reader, out io.Writer) error {
	path := filepath.Join(prefix, "carp.env")
	values := readEnvFile(path)
	defaults := []struct{ key, label string }{
		{"CARP_ADDR", "listen address"},
		{"CARP_DATA_DIR", "data directory"},
		{"CARP_PROFILE", "profile (low|high)"},
		{"CARP_COOKIE_SECURE", "cookie secure (auto|always|never)"},
	}
	scanner := bufio.NewScanner(in)
	for _, d := range defaults {
		current := values[d.key]
		fmt.Fprintf(out, "%s [%s]: ", d.label, current)
		if !scanner.Scan() {
			break
		}
		if v := strings.TrimSpace(scanner.Text()); v != "" {
			values[d.key] = v
		}
	}
	keys := make([]string, 0, len(values))
	for k := range values {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var b strings.Builder
	b.WriteString("# Carp runtime configuration (read by the systemd unit).\n")
	for _, k := range keys {
		b.WriteString(k + "=" + values[k] + "\n")
	}
	if err := os.WriteFile(path, []byte(b.String()), 0o600); err != nil {
		return err
	}
	fmt.Fprintf(out, "saved %s; restart the service for the change to take effect\n", path)
	return nil
}

// Upgrade replaces the installed binary, restarts the service and rolls back
// when the new binary does not become healthy.
func Upgrade(ctx context.Context, prefix, newBinary string, port string) error {
	if !IsRoot() {
		return fmt.Errorf("upgrade needs root; run: sudo carp upgrade --binary <path>")
	}
	if port == "" {
		port = "50011"
	}
	target := filepath.Join(prefix, "carp")
	if _, err := os.Stat(newBinary); err != nil {
		return fmt.Errorf("new binary %q: %w", newBinary, err)
	}
	if err := systemctl("stop", "carp"); err != nil {
		return err
	}
	backup := target + ".bak-" + time.Now().UTC().Format("20060102-150405")
	if err := os.Rename(target, backup); err != nil {
		return fmt.Errorf("backup current binary: %w", err)
	}
	restore := func() {
		_ = os.Rename(backup, target)
		_ = systemctl("start", "carp")
	}
	if err := copyFile(newBinary, target, 0o755); err != nil {
		restore()
		return fmt.Errorf("install new binary: %w", err)
	}
	if err := systemctl("start", "carp"); err != nil {
		restore()
		return fmt.Errorf("start with new binary failed: %w", err)
	}
	if err := waitHealthy(ctx, "127.0.0.1:"+port, 20*time.Second); err != nil {
		restore()
		return fmt.Errorf("new binary unhealthy, rolled back: %w", err)
	}
	return nil
}

// ResetAdminPassword sets a new password for an account while the service is
// stopped, then revokes all sessions. It is used by the ops menu.
func ResetAdminPassword(ctx context.Context, dataDir, dbPath, username, password string) error {
	if len(password) < 8 {
		return fmt.Errorf("password must be at least 8 characters")
	}
	_ = dbPath
	return resetAdminPassword(ctx, dataDir, username, password)
}

func systemctl(args ...string) error {
	cmd := exec.Command("systemctl", args...)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("systemctl %s: %s", strings.Join(args, " "), strings.TrimSpace(string(out)))
	}
	return nil
}

// ServiceAction runs a lifecycle action and prints human readable progress and
// the resulting state, including the health check and a journal tail on error.
func ServiceAction(action, port string, out io.Writer) error {
	if out == nil {
		out = os.Stdout
	}
	if port == "" {
		port = "50011"
	}
	label, ok := map[string]string{"start": "启动", "stop": "停止", "restart": "重启"}[action]
	if !ok {
		fmt.Fprintf(out, "✗ 未知服务操作：%s（可用：start/stop/restart）\n", action)
		return fmt.Errorf("service: unknown action %q", action)
	}
	started := time.Now()
	fmt.Fprintf(out, "⏳ 正在%s Carp 服务……\n", label)
	if err := Service(action); err != nil {
		fmt.Fprintf(out, "✗ %s失败：%v\n", label, err)
		journalTail(out, 30)
		return err
	}
	switch action {
	case "start", "restart":
		if err := WaitHealthy("127.0.0.1:"+port, 20*time.Second); err != nil {
			fmt.Fprintf(out, "✗ 服务进程已启动，但健康检查未通过：%v\n", err)
			journalTail(out, 30)
			return err
		}
		fmt.Fprintf(out, "✓ 服务已%s（耗时 %s）\n", label, time.Since(started).Round(time.Millisecond))
		fmt.Fprintf(out, "  访问地址：http://127.0.0.1:%s\n", port)
		fmt.Fprintf(out, "  服务状态：%s    健康检查：OK\n", ServiceStatus())
	case "stop":
		deadline := time.Now().Add(5 * time.Second)
		for time.Now().Before(deadline) {
			state := ServiceStatus()
			if state == "inactive" || state == "failed" {
				fmt.Fprintf(out, "✓ 服务已停止（状态：%s，耗时 %s）\n", state, time.Since(started).Round(time.Millisecond))
				return nil
			}
			time.Sleep(200 * time.Millisecond)
		}
		state := ServiceStatus()
		fmt.Fprintf(out, "✗ 停止超时，当前状态：%s\n", state)
		journalTail(out, 30)
		return fmt.Errorf("service stop timed out (state %s)", state)
	}
	return nil
}

// WaitHealthy waits until /health answers 200 or the timeout expires.
func WaitHealthy(addr string, timeout time.Duration) error {
	return waitHealthy(context.Background(), addr, timeout)
}

func journalTail(out io.Writer, lines int) {
	fmt.Fprintln(out, "\n--- 最近日志（journalctl -u carp）---")
	cmd := exec.Command("journalctl", "-u", "carp", "-n", strconv.Itoa(lines), "--no-pager")
	cmd.Stdout, cmd.Stderr = out, out
	if err := cmd.Run(); err != nil {
		fmt.Fprintf(out, "（无法读取 journal：%v；可运行 systemctl status carp 查看）\n", err)
	}
}

func printInstallSummary(opts Options, creds Credentials) {
	user, pass := creds.Username, creds.Password
	if user == "" {
		user = "admin"
	}
	switch {
	case opts.NoCredentials:
		pass = "（未显示；见下方密码文件）"
	case pass != "":
		// keep the generated one-time password
	case opts.NoService:
		pass = "（首次手动启动后生成，见下方密码文件）"
	default:
		pass = "（未读取到；密码文件可能已被删除或尚未生成）"
	}
	lines := []string{
		fmt.Sprintf("服务地址    http://127.0.0.1:%s", opts.Port),
		fmt.Sprintf("局域网 IP   %s（默认仅本机监听；局域网/公网请配 nginx 反代）", lanIPv4()),
		fmt.Sprintf("管理员账号  %s", user),
		fmt.Sprintf("初始密码    %s", pass),
		fmt.Sprintf("数据目录    %s", opts.DataDir),
		fmt.Sprintf("配置文件    %s", opts.envPath()),
		fmt.Sprintf("密码文件    %s", filepath.Join(opts.DataDir, "bootstrap", "initial-admin.txt")),
	}
	if opts.NoService {
		lines = append(lines, "服务方式    未安装 systemd（--no-service），手动运行 /usr/local/bin/carp serve")
	} else {
		lines = append(lines, fmt.Sprintf("服务状态    %s（健康检查 %s）", ServiceStatus(), healthState(opts.Port)))
	}
	opts.printf("")
	printBox(opts, "Carp 安装完成", lines)
	opts.printf("")
	opts.printf("⚠ 首次登录后请立即修改密码；该密码只显示这一次，改密后密码文件会自动删除。")
	opts.printf("下一步：")
	opts.printf("  carp doctor          环境自检")
	opts.printf("  carp                 打开运维菜单（启停/备份/日志/重置密码）")
	opts.printf("  systemctl status carp")
	opts.printf("")
}

func printBox(opts Options, title string, lines []string) {
	var wrapped []string
	for _, line := range lines {
		wrapped = append(wrapped, wrapLine(line, 76)...)
	}
	width := displayWidth(title)
	for _, line := range wrapped {
		if w := displayWidth(line); w > width {
			width = w
		}
	}
	bar := strings.Repeat("═", width+2)
	opts.printf("╔%s╗", bar)
	opts.printf("║ %s%s ║", title, strings.Repeat(" ", width-displayWidth(title)))
	opts.printf("╠%s╣", bar)
	for _, line := range wrapped {
		opts.printf("║ %s%s ║", line, strings.Repeat(" ", width-displayWidth(line)))
	}
	opts.printf("╚%s╝", bar)
}

func displayWidth(s string) int {
	width := 0
	for _, r := range s {
		switch {
		case r >= 0x2500 && r <= 0x257f:
			width++ // box drawing is ambiguous-width; real terminals render it as 1
		case r > 0x7f:
			width += 2
		default:
			width++
		}
	}
	return width
}

// wrapLine splits a line into chunks no wider than max display columns (CJK=2).
func wrapLine(s string, max int) []string {
	if displayWidth(s) <= max {
		return []string{s}
	}
	var lines []string
	var b strings.Builder
	w := 0
	for _, r := range s {
		rw := 1
		if r > 0x7f {
			rw = 2
		}
		if w+rw > max && b.Len() > 0 {
			lines = append(lines, b.String())
			b.Reset()
			w = 0
		}
		b.WriteRune(r)
		w += rw
	}
	if b.Len() > 0 {
		lines = append(lines, b.String())
	}
	return lines
}

func lanIPv4() string {
	interfaces, err := net.Interfaces()
	if err != nil {
		return "未检测到"
	}
	var ips []string
	for _, iface := range interfaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		name := strings.ToLower(iface.Name)
		if strings.HasPrefix(name, "docker") || strings.HasPrefix(name, "veth") || strings.HasPrefix(name, "br-") ||
			strings.HasPrefix(name, "virbr") || strings.HasPrefix(name, "tun") || strings.HasPrefix(name, "tap") ||
			strings.HasPrefix(name, "wg") || strings.HasPrefix(name, "tailscale") || strings.HasPrefix(name, "zt") ||
			strings.HasPrefix(name, "utun") {
			continue
		}
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, addr := range addrs {
			if netw, ok := addr.(*net.IPNet); ok {
				if ip4 := netw.IP.To4(); ip4 != nil && !strings.HasPrefix(ip4.String(), "169.254.") {
					ips = append(ips, ip4.String())
				}
			}
		}
	}
	if len(ips) == 0 {
		return "未检测到"
	}
	return ips[0]
}

func healthState(port string) string {
	client := &http.Client{Timeout: time.Second}
	resp, err := client.Get("http://127.0.0.1:" + port + "/health")
	if err != nil {
		return "无响应"
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusOK {
		return "OK"
	}
	return fmt.Sprintf("HTTP %d", resp.StatusCode)
}

func waitHealthy(ctx context.Context, addr string, timeout time.Duration) error {
	client := &http.Client{Timeout: 2 * time.Second}
	deadline := time.Now().Add(timeout)
	url := "http://" + addr + "/health"
	var last error
	for time.Now().Before(deadline) {
		req, _ := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
		resp, err := client.Do(req)
		if err == nil {
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return nil
			}
			last = fmt.Errorf("health returned %d", resp.StatusCode)
		} else {
			last = err
		}
		time.Sleep(300 * time.Millisecond)
	}
	if last == nil {
		last = fmt.Errorf("timeout")
	}
	return last
}

func ensureUser(username, home string) error {
	if _, err := user.Lookup(username); err == nil {
		return nil
	}
	cmd := exec.Command("useradd", "--system", "--home", home, "--shell", "/usr/sbin/nologin", username)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("create system user: %s: %w", strings.TrimSpace(string(out)), err)
	}
	return nil
}

func removeUser(username string) error {
	cmd := exec.Command("userdel", username)
	if out, err := cmd.CombinedOutput(); err != nil {
		return fmt.Errorf("%s: %w", strings.TrimSpace(string(out)), err)
	}
	return nil
}

func chownTo(path, username string) error {
	u, err := user.Lookup(username)
	if err != nil {
		return fmt.Errorf("lookup user %s: %w", username, err)
	}
	uid, err := strconv.Atoi(u.Uid)
	if err != nil {
		return err
	}
	gid, err := strconv.Atoi(u.Gid)
	if err != nil {
		return err
	}
	if err := os.Chown(path, uid, gid); err != nil {
		return fmt.Errorf("chown %s: %w", path, err)
	}
	return nil
}

func runningFromBinary(path string) (int, bool) {
	out, err := exec.Command("pgrep", "-f", path).Output()
	if err != nil {
		return 0, false
	}
	fields := strings.Fields(string(out))
	for _, f := range fields {
		if pid, err := strconv.Atoi(f); err == nil && pid != os.Getpid() {
			return pid, true
		}
	}
	return 0, false
}

func copyFile(src, dst string, mode os.FileMode) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	tmp := dst + ".tmp-install"
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, mode)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		os.Remove(tmp)
		return err
	}
	if err := out.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	return os.Rename(tmp, dst)
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func readEnvFile(path string) map[string]string {
	values := map[string]string{}
	data, err := os.ReadFile(path)
	if err != nil {
		return values
	}
	for _, line := range strings.Split(string(data), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if k, v, ok := strings.Cut(line, "="); ok {
			values[strings.TrimSpace(k)] = strings.TrimSpace(v)
		}
	}
	return values
}

func parseCredentials(content, path string) Credentials {
	c := Credentials{FilePath: path}
	for _, line := range strings.Split(content, "\n") {
		if k, v, ok := strings.Cut(line, ":"); ok {
			switch strings.TrimSpace(k) {
			case "username":
				c.Username = strings.TrimSpace(v)
			case "password":
				c.Password = strings.TrimSpace(v)
			}
		}
	}
	return c
}
