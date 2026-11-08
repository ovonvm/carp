<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import BrandMark from '../BrandMark.vue'
import {
  ApiError,
  deleteBrandingLogo,
  getBranding,
  updateBranding,
  uploadBrandingLogo,
  type BrandingConfig,
  type SiteFooterLink,
} from '../../api/client'
import { useSiteStore } from '../../stores/site'
import { useToast } from '../../composables/useToast'

const site = useSiteStore()
const toast = useToast()

const loading = ref(true)
const loadError = ref('')
const saving = ref(false)
const saveError = ref('')
const logoBusy = ref(false)

const cfg = ref<BrandingConfig | null>(null)
const form = reactive({
  loginTitle: '',
  loginSubtitle: '',
  footerText: '',
  links: [] as SiteFooterLink[],
})
const fileInput = ref<HTMLInputElement | null>(null)

const maxLinks = computed(() => cfg.value?.max_links ?? 3)
const logoURL = computed(() => cfg.value?.logo_url ?? '')
const logoSet = computed(() => cfg.value?.logo_set === true)

function fill(c: BrandingConfig): void {
  cfg.value = c
  form.loginTitle = c.login_title
  form.loginSubtitle = c.login_subtitle
  form.footerText = c.footer_text
  form.links = c.footer_links.map((link) => ({ ...link }))
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = ''
  try {
    fill(await getBranding())
  } catch (cause) {
    loadError.value = cause instanceof ApiError ? cause.message : '无法加载品牌设置'
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function save(): Promise<void> {
  if (saving.value) return
  saving.value = true
  saveError.value = ''
  try {
    const links = form.links
      .map((link) => ({ label: link.label.trim(), url: link.url.trim() }))
      .filter((link) => link.label !== '' || link.url !== '')
    fill(
      await updateBranding({
        login_title: form.loginTitle,
        login_subtitle: form.loginSubtitle,
        footer_text: form.footerText,
        footer_links: links,
      }),
    )
    await site.refresh()
    toast.push('品牌设置已保存')
  } catch (cause) {
    saveError.value = cause instanceof ApiError ? cause.message : '保存失败，请稍后重试'
  } finally {
    saving.value = false
  }
}

function resetToDefaults(): void {
  const c = cfg.value
  if (!c) return
  form.loginTitle = c.default_login.title
  form.loginSubtitle = c.default_login.subtitle
  form.footerText = ''
  form.links = []
}

function pickLogo(): void {
  fileInput.value?.click()
}

async function onLogoPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (file.type !== 'image/png' && file.type !== 'image/jpeg') {
    saveError.value = 'Logo 仅支持 PNG/JPG'
    return
  }
  if (file.size > 512 * 1024) {
    saveError.value = 'Logo 不能超过 512 KB'
    return
  }
  logoBusy.value = true
  saveError.value = ''
  try {
    fill(await uploadBrandingLogo(file))
    await site.refresh()
    toast.push('Logo 已更新')
  } catch (cause) {
    saveError.value = cause instanceof ApiError ? cause.message : '上传失败，请稍后重试'
  } finally {
    logoBusy.value = false
  }
}

async function removeLogo(): Promise<void> {
  if (logoBusy.value) return
  logoBusy.value = true
  saveError.value = ''
  try {
    fill(await deleteBrandingLogo())
    await site.refresh()
    toast.push('已恢复默认 Logo')
  } catch (cause) {
    saveError.value = cause instanceof ApiError ? cause.message : '操作失败，请稍后重试'
  } finally {
    logoBusy.value = false
  }
}

function addLink(): void {
  if (form.links.length >= maxLinks.value) return
  form.links.push({ label: '', url: '' })
}

function removeLink(index: number): void {
  form.links.splice(index, 1)
}
</script>

<template>
  <section class="brand-card" aria-labelledby="branding-title">
    <header class="brand-head">
      <div>
        <h2 id="branding-title" class="brand-title">外观与品牌</h2>
        <p class="brand-hint">
          仅 root 管理员可修改。Logo 用于侧栏、登录页与 favicon；登录页文案与页脚会立即对访客生效。
        </p>
      </div>
    </header>

    <p v-if="loadError" class="brand-error" role="alert">{{ loadError }}</p>
    <p v-else-if="loading" class="brand-loading">加载中…</p>

    <template v-else-if="cfg">
      <div class="logo-row">
        <div class="logo-preview">
          <img v-if="logoSet" :src="logoURL" alt="站点 Logo" />
          <BrandMark v-else :size="44" />
        </div>
        <div class="logo-copy">
          <p class="logo-title">品牌 Logo</p>
          <p class="logo-hint">PNG/JPG，≤512 KB，建议 512×512、透明背景。未上传时使用默认图标。</p>
          <div class="logo-actions">
            <button class="btn btn-secondary btn-sm" type="button" :disabled="logoBusy" @click="pickLogo">
              {{ logoSet ? '更换 Logo' : '上传 Logo' }}
            </button>
            <button v-if="logoSet" class="btn btn-ghost btn-sm" type="button" :disabled="logoBusy" @click="removeLogo">
              恢复默认
            </button>
            <input ref="fileInput" class="sr-only" type="file" accept="image/png,image/jpeg" @change="onLogoPicked" />
          </div>
        </div>
      </div>

      <div class="brand-grid">
        <label class="field">
          <span class="field-label">登录页主标题</span>
          <textarea
            v-model="form.loginTitle"
            class="input brand-textarea"
            rows="2"
            maxlength="120"
            :placeholder="cfg.default_login.title"
          />
          <span class="field-hint">支持换行，最多 120 字；留空则使用默认文案。</span>
        </label>

        <label class="field">
          <span class="field-label">登录页副标题</span>
          <textarea
            v-model="form.loginSubtitle"
            class="input brand-textarea"
            rows="2"
            maxlength="160"
            :placeholder="cfg.default_login.subtitle"
          />
          <span class="field-hint">最多 160 字。</span>
        </label>
      </div>

      <div class="brand-grid">
        <label class="field">
          <span class="field-label">页脚文字</span>
          <input v-model="form.footerText" class="input" maxlength="200" placeholder="例如：© 2026 Carp · 自托管网盘" />
          <span class="field-hint">最多 200 字；留空则不显示页脚文字。</span>
        </label>
      </div>

      <div class="links-editor">
        <div class="links-head">
          <span class="field-label">页脚链接（最多 {{ maxLinks }} 条）</span>
          <button class="btn btn-ghost btn-sm" type="button" :disabled="form.links.length >= maxLinks" @click="addLink">
            + 添加链接
          </button>
        </div>
        <div v-if="form.links.length === 0" class="links-empty">还没有链接。</div>
        <div v-for="(link, index) in form.links" :key="index" class="link-row">
          <input v-model="link.label" class="input link-label" maxlength="40" placeholder="名称" aria-label="链接名称" />
          <input v-model="link.url" class="input link-url" maxlength="400" placeholder="https://… 或 /about" aria-label="链接地址" />
          <button class="btn btn-ghost btn-sm" type="button" @click="removeLink(index)">移除</button>
        </div>
        <p class="field-hint">链接只允许 http/https 或站内以 / 开头的路径。</p>
      </div>

      <div class="brand-preview">
        <span class="preview-label">页脚预览</span>
        <div class="preview-line">
          <span v-if="form.footerText.trim()">{{ form.footerText }}</span>
          <span v-for="(link, index) in form.links.filter((l) => l.label.trim() && l.url.trim())" :key="index" class="preview-link">
            {{ link.label }}
          </span>
          <span v-if="!form.footerText.trim() && form.links.filter((l) => l.label.trim() && l.url.trim()).length === 0" class="preview-empty">
            页脚当前为空
          </span>
        </div>
      </div>

      <p v-if="saveError" class="brand-error" role="alert">{{ saveError }}</p>

      <div class="brand-actions">
        <button class="btn btn-ghost btn-sm" type="button" :disabled="saving" @click="resetToDefaults">恢复默认文案</button>
        <button class="btn btn-primary" type="button" :disabled="saving" @click="save">
          {{ saving ? '保存中…' : '保存品牌设置' }}
        </button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.brand-card {
  padding: 20px 22px 18px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-xs);
}

.brand-title {
  font-size: var(--fs-lg);
  font-weight: 600;
}

.brand-hint {
  margin-top: 4px;
  font-size: var(--fs-sm);
  color: var(--color-muted);
}

.brand-loading {
  padding: 18px 0;
  font-size: var(--fs-sm);
  color: var(--color-muted);
}

.brand-error {
  margin-top: 12px;
  padding: 9px 11px;
  background: var(--color-danger-soft);
  border-radius: var(--radius-sm);
  font-size: var(--fs-sm);
  color: var(--color-danger);
}

.logo-row {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-top: 18px;
  padding: 14px;
  background: var(--color-surface-2);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
}

.logo-preview {
  display: grid;
  place-items: center;
  width: 64px;
  height: 64px;
  flex: none;
  overflow: hidden;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
}

.logo-preview img {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.logo-title {
  font-size: var(--fs-sm);
  font-weight: 600;
}

.logo-hint {
  margin-top: 2px;
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.logo-actions {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}

.brand-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 14px;
  margin-top: 16px;
}

.brand-textarea {
  resize: vertical;
  min-height: 62px;
}

.links-editor {
  margin-top: 16px;
}

.links-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.links-empty {
  margin-top: 8px;
  font-size: var(--fs-sm);
  color: var(--color-muted);
}

.link-row {
  display: grid;
  grid-template-columns: minmax(100px, 0.5fr) minmax(180px, 1.4fr) auto;
  gap: 8px;
  align-items: center;
  margin-top: 8px;
}

.brand-preview {
  margin-top: 16px;
  padding: 12px 14px;
  background: var(--color-surface-2);
  border: 1px dashed var(--color-border-strong);
  border-radius: var(--radius);
}

.preview-label {
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.preview-line {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  margin-top: 6px;
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.preview-link {
  color: var(--color-primary-ink);
}

.preview-empty {
  color: var(--color-muted);
}

.brand-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 18px;
  padding-top: 14px;
  border-top: 1px solid var(--color-border);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
