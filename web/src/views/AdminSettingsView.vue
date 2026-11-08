<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useToast } from '../composables/useToast'
import { formatBytes } from '../lib/format'
import { adminErrorMessage, bytesToGbText, parseGbInput } from '../lib/admin'
import { getAdminQB, updateAdminQB, testAdminQB, type AdminQBConfig, type AdminQBTestResult } from '../api/client'
import BrandingCard from '../components/admin/BrandingCard.vue'
import { useAdminStore } from '../stores/admin'
import { useAuthStore } from '../stores/auth'
import { useSiteStore } from '../stores/site'

const admin = useAdminStore()
const auth = useAuthStore()
const site = useSiteStore()
const toast = useToast()

const SITE_NAME_MAX = 64

/** Only the root administrator may flip the registration/deletion switches. */
const canEditRootSettings = computed(() => auth.isRoot)

const form = reactive({
  siteName: '',
  registrationEnabled: false,
  deletionEnabled: false,
  quotaGb: '0',
})

const touched = ref(false)
const formError = ref<string | null>(null)
const saveError = ref<string | null>(null)

const quotaBytes = computed(() => parseGbInput(form.quotaGb))

const original = computed(() => {
  const settings = admin.settings
  if (!settings) return null
  return {
    siteName: settings.site_name,
    registrationEnabled: settings.registration_enabled,
    deletionEnabled: settings.deletion_enabled,
    quotaGb: bytesToGbText(settings.default_quota_bytes),
  }
})

const dirty = computed(() => {
  const base = original.value
  if (!base) return false
  return (
    form.siteName !== base.siteName ||
    form.registrationEnabled !== base.registrationEnabled ||
    form.deletionEnabled !== base.deletionEnabled ||
    form.quotaGb !== base.quotaGb
  )
})

function fillFromStore(): void {
  const base = original.value
  if (!base) return
  form.siteName = base.siteName
  form.registrationEnabled = base.registrationEnabled
  form.deletionEnabled = base.deletionEnabled
  form.quotaGb = base.quotaGb
  touched.value = false
  formError.value = null
  saveError.value = null
}

watch(() => admin.settings, fillFromStore)

function validate(): string | null {
  const name = form.siteName.trim()
  if (!name) return '站点名不能为空'
  if (Array.from(name).length > SITE_NAME_MAX) return `站点名不能超过 ${SITE_NAME_MAX} 个字符`
  if (quotaBytes.value === null) return '默认配额请填写不小于 0 的数字（GB，0 表示不限）'
  return null
}

async function save(): Promise<void> {
  touched.value = true
  const problem = validate()
  formError.value = problem
  if (problem) return
  saveError.value = null
  try {
    const patch = {
      site_name: form.siteName.trim(),
      default_quota_bytes: quotaBytes.value ?? 0,
      // Non-root admins must not send either root-only key: the server answers 403.
      ...(canEditRootSettings.value
        ? {
            registration_enabled: form.registrationEnabled,
            deletion_enabled: form.deletionEnabled,
          }
        : {}),
    }
    const ok = await admin.saveSettings(patch)
    if (ok) {
      fillFromStore()
      await site.refresh()
      toast.push('系统设置已保存')
    }
  } catch (cause) {
    saveError.value = adminErrorMessage(cause, '保存失败，请稍后重试')
  }
}

async function retryLoad(): Promise<void> {
  await admin.loadSettings()
  void loadQB()
}

/* --- qBittorrent offline downloads (M6.6, root only) --------------------- */

const qb = ref<AdminQBConfig | null>(null)
const qbLoading = ref(false)
const qbSaving = ref(false)
const qbTesting = ref(false)
const qbError = ref<string | null>(null)
const qbTestResult = ref<AdminQBTestResult | null>(null)
const qbForm = reactive({ url: '', username: '', password: '', clearPassword: false })

const qbLocked = computed(() => qb.value?.locked === true)
const qbDirty = computed(() => {
  const base = qb.value
  if (!base) return false
  return (
    qbForm.url.trim() !== base.url ||
    qbForm.username.trim() !== base.username ||
    qbForm.password !== '' ||
    qbForm.clearPassword
  )
})

function fillQB(next: AdminQBConfig): void {
  qb.value = next
  qbForm.url = next.url
  qbForm.username = next.username
  qbForm.password = ''
  qbForm.clearPassword = false
  qbError.value = null
  qbTestResult.value = null
}

async function loadQB(): Promise<void> {
  if (!auth.isRoot) return
  qbLoading.value = true
  try {
    fillQB(await getAdminQB())
  } catch (cause) {
    qbError.value = adminErrorMessage(cause, 'qBittorrent 配置加载失败')
  } finally {
    qbLoading.value = false
  }
}

async function saveQB(): Promise<void> {
  if (qbLocked.value) return
  qbError.value = null
  const url = qbForm.url.trim()
  if (url && !/^https?:\/\//i.test(url)) {
    qbError.value = '地址必须以 http:// 或 https:// 开头'
    return
  }
  qbSaving.value = true
  try {
    const next = await updateAdminQB({
      url,
      username: qbForm.username.trim(),
      // An empty password field means "keep the current password".
      ...(qbForm.password ? { password: qbForm.password } : {}),
      clear_password: qbForm.clearPassword,
    })
    fillQB(next)
    toast.push(next.enabled ? 'qBittorrent 配置已保存并生效' : 'qBittorrent 已停用')
  } catch (cause) {
    qbError.value = adminErrorMessage(cause, 'qBittorrent 配置保存失败')
  } finally {
    qbSaving.value = false
  }
}

async function testQB(): Promise<void> {
  if (!qb.value) return
  qbError.value = null
  qbTestResult.value = null
  const url = qbForm.url.trim()
  if (!url) {
    qbError.value = '请先填写 WebUI 地址'
    return
  }
  qbTesting.value = true
  try {
    const result = await testAdminQB({
      url,
      username: qbForm.username.trim(),
      ...(qbForm.password ? { password: qbForm.password } : {}),
    })
    qbTestResult.value = result
    if (result.ok) toast.push(`连接成功：qBittorrent ${result.version ?? ''}`.trim())
  } catch (cause) {
    qbError.value = adminErrorMessage(cause, '测试连接失败')
  } finally {
    qbTesting.value = false
  }
}

onMounted(() => {
  void admin.loadSettings()
  void loadQB()
})
</script>

<template>
  <div class="page">
    <header class="page-head">
      <div class="page-heading">
        <h1 class="page-title">系统设置</h1>
        <p class="page-sub">这些设置对整个实例生效，保存后立即应用于新会话。</p>
      </div>
    </header>

    <section v-if="admin.settingsLoading && !admin.settings" class="panel state-panel">
      <span class="skeleton-block wide"></span>
      <span class="skeleton-block"></span>
      <span class="skeleton-block short"></span>
    </section>

    <section v-else-if="admin.settingsError && !admin.settings" class="panel state-panel">
      <p class="state-title" role="alert">加载失败</p>
      <p class="state-copy">{{ admin.settingsError }}</p>
      <button type="button" class="btn btn-secondary" @click="retryLoad">重试</button>
    </section>

    <form v-else-if="admin.settings" class="panel form" novalidate @submit.prevent="save">
      <div class="form-block">
        <label class="field">
          <span class="field-label">站点名称</span>
          <input
            v-model="form.siteName"
            class="input"
            type="text"
            maxlength="64"
            placeholder="Carp"
            :aria-invalid="touched && Boolean(formError && !form.siteName.trim())"
          />
          <span class="field-hint">显示在分享页与浏览器标题中，最多 {{ SITE_NAME_MAX }} 个字符。</span>
        </label>

        <div class="switch-row">
          <div class="switch-copy">
            <span class="field-label">允许注册</span>
            <span class="field-hint">注册默认关闭；仅 root 管理员可修改。</span>
            <span v-if="!canEditRootSettings" class="field-hint">当前账号不是 root 管理员，开关不可修改。</span>
          </div>
          <label class="switch" :class="{ 'is-disabled': !canEditRootSettings }">
            <input
              v-model="form.registrationEnabled"
              type="checkbox"
              role="switch"
              aria-label="允许注册"
              :disabled="!canEditRootSettings"
              :title="canEditRootSettings ? '' : '仅 root 管理员可修改'"
            />
            <span class="track" aria-hidden="true"><span class="thumb"></span></span>
          </label>
        </div>

        <div class="switch-row">
          <div class="switch-copy">
            <span class="field-label">允许用户注销账号</span>
            <span class="field-hint">关闭后用户无法自助注销；注销有 3 天宽限期，期间可联系管理员恢复。仅 root 管理员可修改。</span>
            <span v-if="!canEditRootSettings" class="field-hint">当前账号不是 root 管理员，开关不可修改。</span>
          </div>
          <label class="switch" :class="{ 'is-disabled': !canEditRootSettings }">
            <input
              v-model="form.deletionEnabled"
              type="checkbox"
              role="switch"
              aria-label="允许用户注销账号"
              :disabled="!canEditRootSettings"
              :title="canEditRootSettings ? '' : '仅 root 管理员可修改'"
            />
            <span class="track" aria-hidden="true"><span class="thumb"></span></span>
          </label>
        </div>

        <label class="field narrow">
          <span class="field-label">新用户默认配额（GB）</span>
          <input v-model="form.quotaGb" class="input" type="text" inputmode="decimal" placeholder="0" />
          <span class="field-hint">十进制 GB；0 表示不限。当前 {{ formatBytes(quotaBytes ?? 0) }}。</span>
        </label>
      </div>

      <div class="readonly">
        <p class="readonly-title">保留策略（只读）</p>
        <dl class="readonly-grid">
          <div>
            <dt>审计日志保留</dt>
            <dd class="numeric">{{ admin.settings.audit_retention_days }} 天</dd>
          </div>
          <div>
            <dt>回收站保留</dt>
            <dd class="numeric">{{ admin.settings.trash_retention_days }} 天</dd>
          </div>
        </dl>
      </div>

      <p v-if="formError" class="field-error" role="alert">{{ formError }}</p>
      <p v-if="saveError" class="field-error" role="alert">{{ saveError }}</p>

      <div class="form-actions">
        <span v-if="dirty" class="dirty-hint">有未保存的修改</span>
        <button type="button" class="btn btn-secondary" :disabled="admin.settingsSaving || !dirty" @click="fillFromStore">
          还原
        </button>
        <button type="submit" class="btn btn-primary" :disabled="admin.settingsSaving">
          {{ admin.settingsSaving ? '保存中…' : '保存设置' }}
        </button>
      </div>
    </form>

    <section v-if="auth.isRoot" class="panel form qb-panel">
      <div class="form-block">
        <div class="qb-head">
          <div>
            <p class="field-label">离线下载（qBittorrent）</p>
            <p class="field-hint">保存后立即生效，无需重启；磁力链接与种子下载依赖此配置。</p>
          </div>
          <span v-if="qb" class="qb-source" :class="{ 'is-env': qb.locked }">
            {{ qb.locked ? '由环境变量控制' : qb.enabled ? '已启用' : '未启用' }}
          </span>
        </div>

        <p v-if="qbLoading" class="field-hint">加载中…</p>
        <template v-else>
          <label class="field">
            <span class="field-label">WebUI 地址</span>
            <input
              v-model="qbForm.url"
              class="input"
              type="url"
              placeholder="http://127.0.0.1:8080"
              :disabled="qbLocked"
            />
            <span class="field-hint">留空表示停用 qBittorrent 离线下载；仅支持 http/https。</span>
          </label>
          <div class="qb-grid">
            <label class="field">
              <span class="field-label">用户名</span>
              <input v-model="qbForm.username" class="input" type="text" autocomplete="off" :disabled="qbLocked" />
            </label>
            <label class="field">
              <span class="field-label">密码</span>
              <input
                v-model="qbForm.password"
                class="input"
                type="password"
                autocomplete="new-password"
                :disabled="qbLocked"
                :placeholder="qb?.has_password ? '已设置，留空不修改' : '未设置'"
              />
            </label>
          </div>
          <label class="qb-clear">
            <input v-model="qbForm.clearPassword" type="checkbox" :disabled="qbLocked" />
            <span>清除已保存的密码</span>
          </label>
          <p v-if="qbLocked" class="field-hint">
            当前由 CARP_QB_URL / CARP_QB_USERNAME / CARP_QB_PASSWORD 环境变量控制，后台不可修改。
          </p>
          <p v-if="qb?.source === 'db' && !qb.has_password && qb.url" class="field-hint">
            提示：已填写地址但密码未设置或无法解密，下载任务可能登录失败。
          </p>
        </template>

        <p v-if="qbError" class="field-error" role="alert">{{ qbError }}</p>
        <p v-if="qbTestResult" class="qb-test-result" :class="qbTestResult.ok ? 'is-ok' : 'is-error'" role="status">
          <template v-if="qbTestResult.ok">连接成功：qBittorrent {{ qbTestResult.version }}</template>
          <template v-else>连接失败：{{ qbTestResult.error }}</template>
        </p>
        <div class="form-actions">
          <span v-if="qbDirty" class="dirty-hint">有未保存的修改</span>
          <button
            type="button"
            class="btn btn-secondary"
            :disabled="qbTesting || qbLoading || !qb || !qbForm.url.trim()"
            @click="testQB"
          >
            {{ qbTesting ? '测试中…' : '测试连接' }}
          </button>
          <button
            type="button"
            class="btn btn-primary"
            :disabled="qbSaving || qbLocked || !qb || !qbDirty"
            @click="saveQB"
          >
            {{ qbSaving ? '保存中…' : '保存 qBittorrent 设置' }}
          </button>
        </div>
      </div>
    </section>

    <BrandingCard v-if="auth.isRoot" />
  </div>
</template>

<style scoped>
.qb-panel {
  margin-top: 16px;
}

.qb-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.qb-source {
  flex-shrink: 0;
  padding: 3px 10px;
  border-radius: 999px;
  background: var(--color-surface-soft);
  color: var(--color-muted);
  font-size: 12px;
}

.qb-source.is-env {
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
}

.qb-test-result {
  margin: 10px 0 0;
  font-size: 13px;
}

.qb-test-result.is-ok {
  color: var(--color-success, #1f8a4c);
}

.qb-test-result.is-error {
  color: var(--color-danger, #c0392b);
}

.qb-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.qb-clear {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--color-text-soft);
  font-size: 13px;
}

@media (max-width: 640px) {
  .qb-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
  max-width: var(--page-max);
  margin: 0 auto;
}

.page-heading {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.page-title {
  margin: 0;
  font-size: var(--fs-xl);
  font-weight: 600;
  letter-spacing: -0.015em;
}

.page-sub {
  margin: 0;
  color: var(--color-muted);
  font-size: var(--fs-sm);
}

.panel {
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  background: var(--color-surface);
}

.form {
  display: flex;
  flex-direction: column;
  gap: 18px;
  padding: 18px;
}

.form-block {
  display: flex;
  flex-direction: column;
  gap: 14px;
  max-width: 520px;
}

.narrow {
  max-width: 240px;
}

.switch-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 10px 0;
  border-top: 1px solid var(--color-border);
  border-bottom: 1px solid var(--color-border);
}

.switch-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.switch-row + .switch-row {
  border-top: 0;
}

.switch {
  position: relative;
  display: inline-flex;
  flex: none;
}

.switch input {
  position: absolute;
  inset: 0;
  margin: 0;
  opacity: 0;
  cursor: pointer;
}

.track {
  display: flex;
  align-items: center;
  width: 38px;
  height: 22px;
  padding: 2px;
  border-radius: 999px;
  background: var(--color-border-strong);
  transition: background-color var(--dur) var(--ease);
}

.switch input:checked + .track {
  background: var(--color-primary);
}

.switch input:focus-visible + .track {
  outline: 2px solid var(--color-primary);
  outline-offset: 2px;
}

.thumb {
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: #fff;
  transition: transform var(--dur) var(--ease);
}

.switch input:checked + .track .thumb {
  transform: translateX(16px);
}

.switch.is-disabled {
  cursor: not-allowed;
}

.switch.is-disabled input {
  cursor: not-allowed;
}

.switch.is-disabled .track {
  opacity: 0.45;
}

.readonly {
  padding-top: 16px;
  border-top: 1px solid var(--color-border);
}

.readonly-title {
  margin: 0 0 8px;
  color: var(--color-muted);
  font-size: var(--fs-xs);
  font-weight: 600;
  letter-spacing: 0.04em;
}

.readonly-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 24px;
  margin: 0;
}

.readonly-grid div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.readonly-grid dt {
  color: var(--color-muted);
  font-size: var(--fs-xs);
}

.readonly-grid dd {
  margin: 0;
  font-size: var(--fs-sm);
  font-weight: 600;
}

.form-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 4px;
  border-top: 1px solid var(--color-border);
}

.dirty-hint {
  margin-right: auto;
  color: var(--color-muted);
  font-size: var(--fs-xs);
}

.state-panel {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 24px;
}

.state-title {
  margin: 0;
  font-size: var(--fs-base);
  font-weight: 600;
}

.state-copy {
  margin: 0;
  color: var(--color-muted);
  font-size: var(--fs-sm);
}

.skeleton-block {
  display: block;
  width: 42%;
  height: 12px;
  border-radius: 4px;
  background: var(--color-border);
  animation: skeleton-pulse 1.2s ease-in-out infinite;
}

.skeleton-block.wide {
  width: 64%;
}

.skeleton-block.short {
  width: 28%;
}

@keyframes skeleton-pulse {
  0%,
  100% {
    opacity: 0.55;
  }
  50% {
    opacity: 1;
  }
}

@media (max-width: 760px) {
  .form {
    padding: 14px;
  }

  .form-block {
    max-width: none;
  }

  .narrow {
    max-width: none;
  }

  .switch-row {
    align-items: flex-start;
  }

  .form-actions {
    flex-wrap: wrap;
  }

  .dirty-hint {
    flex-basis: 100%;
    margin-right: 0;
  }
}
</style>
