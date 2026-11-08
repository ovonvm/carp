<script setup lang="ts">
import { APP_VERSION_LABEL } from '../lib/version'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import BrandMark from './BrandMark.vue'
import GlobalMediaOverlay from './player/GlobalMediaOverlay.vue'
import UploadDock from './UploadDock.vue'
import { useAuthStore } from '../stores/auth'
import { useDownloadsStore } from '../stores/downloads'
import { useFilesStore } from '../stores/files'
import { useGroupsStore } from '../stores/groups'
import { useMediaStore } from '../stores/media'
import { useTrashStore } from '../stores/trash'
import { useTransfersStore } from '../stores/transfers'
import { useUploadsStore } from '../stores/uploads'
import { useSiteStore } from '../stores/site'
import { playFx, routeFxPreset } from '../lib/fx'
import { useToast } from '../composables/useToast'
import { formatGB } from '../lib/format'
import { THEMES, applyTheme, currentTheme, type ThemeId } from '../lib/theme'

const auth = useAuthStore()
const files = useFilesStore()
const groups = useGroupsStore()
const media = useMediaStore()
const trash = useTrashStore()
const uploads = useUploadsStore()
const downloads = useDownloadsStore()
const transfers = useTransfersStore()
const router = useRouter()
const route = useRoute()
const toast = useToast()
const site = useSiteStore()

const menuOpen = ref(false)
const menuRef = ref<HTMLElement | null>(null)
const themeId = ref<ThemeId>(currentTheme())

function selectTheme(id: ThemeId): void {
  applyTheme(id)
  themeId.value = id
}

// Filename search lives in the top bar: inline on desktop, in a full-width
// drop-down on narrow screens. The box mirrors `?q=` on /search (typing never
// fires a request on its own; Enter or the page filters commit the query).
const searchOpen = ref(false)
const searchTerm = ref('')
const searchInput = ref<HTMLInputElement | null>(null)
const mobileSearchInput = ref<HTMLInputElement | null>(null)

const activeQuery = computed(() => {
  const value = route.query.q
  return typeof value === 'string' ? value : ''
})

watch(
  () => [route.name, activeQuery.value] as const,
  ([name, value]) => {
    if (name !== 'search') return
    searchTerm.value = value
  },
  { immediate: true },
)

watch(
  () => route.name,
  (name) => {
    searchOpen.value = false
    const preset = routeFxPreset(typeof name === 'string' ? name : null)
    if (preset) playFx(preset, { x: 170, y: 170 })
  },
)

function openSearch(): void {
  searchOpen.value = true
  void nextTick(() => mobileSearchInput.value?.focus())
}

function closeSearch(): void {
  searchOpen.value = false
}

function onSearchEscape(): void {
  searchOpen.value = false
  searchInput.value?.blur()
  mobileSearchInput.value?.blur()
}

async function submitSearch(): Promise<void> {
  const q = searchTerm.value.trim()
  if (!q) {
    ;(searchOpen.value ? mobileSearchInput : searchInput).value?.focus()
    return
  }

  const query: Record<string, string> = { q }
  // Refining a query keeps the current filters instead of resetting them.
  if (route.name === 'search') {
    const type = route.query.type
    if (type === 'file' || type === 'dir') query.type = type
    if (route.query.recent === '1') query.recent = '1'
  }

  searchOpen.value = false
  await router.push({ name: 'search', query })
}

const username = computed(() => auth.user?.username ?? '')
const initial = computed(() => username.value.slice(0, 1).toUpperCase())
const roleLabel = computed(() => (auth.user?.role === 'admin' ? '管理员' : '成员'))

const hasQuota = computed(() => (auth.user?.quota_bytes ?? 0) > 0)
const usedLabel = computed(() => formatGB(auth.user?.used_bytes ?? 0))
const quotaLabel = computed(() => formatGB(auth.user?.quota_bytes ?? 0))
const storagePercent = computed(() => {
  if (!auth.user || !hasQuota.value || auth.user.quota_bytes <= 0) return 0
  return Math.min(100, Math.max(0, Math.round((auth.user.used_bytes / auth.user.quota_bytes) * 100)))
})

function closeMenu(): void {
  menuOpen.value = false
}

function onDocumentPointerDown(event: PointerEvent): void {
  const target = event.target as Node | null
  if (!menuOpen.value || !target) return
  if (menuRef.value && !menuRef.value.contains(target)) closeMenu()
}

function onWindowKeydown(event: KeyboardEvent): void {
  // Ctrl/Cmd+A selects the current page's items, not the page text.
  if ((event.ctrlKey || event.metaKey) && (event.key === 'a' || event.key === 'A')) {
    const target = event.target instanceof HTMLElement ? event.target : null
    if (target?.closest('input, textarea, select, [contenteditable="true"]')) return
    event.preventDefault()
    window.dispatchEvent(new CustomEvent('carp:select-all'))
    return
  }
  if (event.key !== 'Escape') return
  if (searchOpen.value) closeSearch()
  if (menuOpen.value) closeMenu()
}

async function handleLogout(): Promise<void> {
  closeMenu()
  try {
    await auth.logout()
  } catch {
    // The local session is cleared even when the network call fails.
  }
  uploads.reset()
  downloads.reset()
  trash.reset()
  transfers.reset()
  files.reset()
  groups.reset()
  toast.push('已退出登录')
  await router.replace({ name: 'login' })
}

// Sidebar counters are cheap to refresh, so keep them current as users move
// between pages instead of showing a stale number.
watch(
  () => route.path,
  () => {
    void trash.refreshTotal()
  },
)

onMounted(() => {
  void site.ensure()
  // The first page load has no route transition, so greet the destination once
  // after the effects layer is mounted (the watch needs a listening canvas).
  window.setTimeout(() => {
    const preset = routeFxPreset(typeof route.name === 'string' ? route.name : null)
    if (preset) playFx(preset, { x: 170, y: 170 })
  }, 220)
  downloads.restore()
  void trash.refreshTotal()
  void groups.load()
})

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown)
  window.addEventListener('keydown', onWindowKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  window.removeEventListener('keydown', onWindowKeydown)
})
</script>

<template>
  <div class="shell">
    <header class="topbar">
      <form class="search" role="search" @submit.prevent="submitSearch">
        <svg class="search-icon" width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
          <circle cx="7" cy="7" r="4.4" fill="none" stroke="currentColor" stroke-width="1.4" />
          <path d="M10.4 10.4 13.6 13.6" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
        </svg>
        <input
          ref="searchInput"
          v-model="searchTerm"
          class="search-input"
          type="search"
          name="q"
          placeholder="搜索文件名"
          aria-label="搜索文件"
          autocomplete="off"
          spellcheck="false"
          @keydown.escape="onSearchEscape"
        />
      </form>

      <div class="topbar-actions">
        <button
          class="topbar-button search-toggle"
          type="button"
          :aria-expanded="searchOpen"
          aria-label="搜索文件"
          title="搜索文件"
          @click="openSearch"
        >
          <svg width="17" height="17" viewBox="0 0 20 20" aria-hidden="true">
            <circle cx="8.8" cy="8.8" r="5.4" fill="none" stroke="currentColor" stroke-width="1.5" />
            <path d="M12.8 12.8 16.6 16.6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />
          </svg>
        </button>
        <button
          v-if="media.node && media.kind === 'video' && media.videoMini"
          class="topbar-button video-toggle"
          type="button"
          data-video-toggle
          aria-label="视频播放器"
          title="视频播放器（点击展开）"
          @click="media.restoreVideo()"
        >
          <svg width="17" height="17" viewBox="0 0 20 20" aria-hidden="true">
            <rect x="2.6" y="4.6" width="11.2" height="10.8" rx="2.2" fill="none" stroke="currentColor" stroke-width="1.5" />
            <path d="m13.8 8.4 3.2-1.9v7l-3.2-1.9" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" />
          </svg>
        </button>
        <button
          v-if="media.node && media.kind === 'audio'"
          class="topbar-button audio-toggle"
          type="button"
          data-audio-toggle
          :aria-expanded="media.miniOpen"
          aria-label="音频播放器"
          title="音频播放器"
          @click="media.toggleMini()"
        >
          <svg width="17" height="17" viewBox="0 0 20 20" aria-hidden="true">
            <path
              d="M8 14.1a1.9 1.9 0 1 1-3.8 0 1.9 1.9 0 0 1 3.8 0Zm0 0V6.2l6.8-1.7v7.6"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
            <circle cx="13" cy="12.1" r="1.9" fill="none" stroke="currentColor" stroke-width="1.5" />
          </svg>
        </button>

        <div ref="menuRef" class="user">
        <button
          class="user-button"
          type="button"
          :aria-expanded="menuOpen"
          aria-haspopup="menu"
          :aria-label="`账号菜单，当前用户 ${username}`"
          @click="menuOpen = !menuOpen"
        >
          <img
            v-if="auth.user?.avatar_url"
            class="avatar avatar-img"
            :src="auth.user?.avatar_url"
            alt=""
            aria-hidden="true"
          />
          <span v-else class="avatar" aria-hidden="true">{{ initial }}</span>
          <span class="user-name">{{ username }}</span>
          <svg
            class="chevron"
            :class="{ 'is-open': menuOpen }"
            width="12"
            height="12"
            viewBox="0 0 12 12"
            aria-hidden="true"
          >
            <path
              d="M3 4.5 6 7.5 9 4.5"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
        </button>

        <div v-if="menuOpen" class="user-menu" role="menu" aria-label="账号菜单">
          <div class="menu-meta">
            <span class="menu-name">{{ username }}</span>
            <span class="menu-role">{{ roleLabel }}</span>
          </div>
          <div class="menu-label">切换主题</div>
          <div class="theme-list" role="group" aria-label="切换主题">
            <button
              v-for="option in THEMES"
              :key="option.id"
              class="theme-option"
              :class="{ 'is-current': themeId === option.id }"
              type="button"
              role="menuitemradio"
              :aria-checked="themeId === option.id"
              @click="selectTheme(option.id)"
            >
              <span class="theme-swatch" aria-hidden="true">
                <i v-for="color in option.colors" :key="color" :style="{ background: color }" />
              </span>
              <span class="theme-name">{{ option.name }}</span>
              <span class="theme-check" aria-hidden="true">✓</span>
            </button>
          </div>
          <div class="menu-divider" aria-hidden="true"></div>
          <RouterLink class="menu-item" role="menuitem" to="/settings" @click="closeMenu">设置</RouterLink>
          <button class="menu-item" type="button" role="menuitem" @click="handleLogout">退出登录</button>
        </div>
      </div>
      </div>

      <form v-if="searchOpen" class="search-mobile" role="search" @submit.prevent="submitSearch">
        <input
          ref="mobileSearchInput"
          v-model="searchTerm"
          class="search-input"
          type="search"
          name="q"
          placeholder="搜索文件名"
          aria-label="搜索文件"
          autocomplete="off"
          spellcheck="false"
          @keydown.escape="closeSearch"
        />
        <button type="button" class="btn btn-secondary search-cancel" @click="closeSearch">取消</button>
      </form>
    </header>

    <aside class="sidebar">
      <RouterLink class="brand" to="/files" :aria-label="`${site.siteName} 首页`">
        <img v-if="site.hasLogo" class="brand-logo" :src="site.logoURL" :alt="site.siteName" />
        <BrandMark v-else :size="24" />
        <span class="wordmark">{{ site.siteName }}</span>
      </RouterLink>

      <nav class="nav" aria-label="主导航">
        <RouterLink class="nav-item" to="/files" active-class="is-active">
          <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
            <path
              d="M2.75 6.25A1.75 1.75 0 0 1 4.5 4.5h3.2l1.55 1.75h6.25a1.75 1.75 0 0 1 1.75 1.75v6.25a1.75 1.75 0 0 1-1.75 1.75H4.5a1.75 1.75 0 0 1-1.75-1.75V6.25Z"
              fill="none"
              stroke="currentColor"
              stroke-width="1.4"
              stroke-linejoin="round"
            />
          </svg>
          <span>我的文件</span>
        </RouterLink>

        <template v-if="groups.hasGroups">
          <p class="nav-group-label">组空间</p>
          <RouterLink
            v-for="group in groups.groups"
            :key="group.id"
            class="nav-item nav-item-group"
            :to="`/groups/${group.id}`"
            active-class="is-active"
            :title="group.role === 'manager' ? '我是该组管理员' : '我是该组组员'"
          >
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <circle cx="7.4" cy="7.6" r="2.5" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M2.9 15.3c0-2.2 2-3.7 4.5-3.7s4.5 1.5 4.5 3.7"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <path
                d="M12.4 5.6a2.4 2.4 0 0 1 0 4.6M14.2 12.2c1.7.4 2.9 1.6 2.9 3.1"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span class="nav-group-name">{{ group.name }}</span>
            <span v-if="group.role === 'manager'" class="nav-role-tag">管理员</span>
          </RouterLink>
        </template>

        <RouterLink class="nav-item" to="/shares" active-class="is-active">
          <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
            <path
              d="M8.3 11.7a3.2 3.2 0 0 0 4.5 0l2.4-2.4a3.2 3.2 0 0 0-4.5-4.5l-1.2 1.2"
              fill="none"
              stroke="currentColor"
              stroke-width="1.4"
              stroke-linecap="round"
            />
            <path
              d="M11.7 8.3a3.2 3.2 0 0 0-4.5 0l-2.4 2.4a3.2 3.2 0 0 0 4.5 4.5l1.2-1.2"
              fill="none"
              stroke="currentColor"
              stroke-width="1.4"
              stroke-linecap="round"
            />
          </svg>
          <span>分享</span>
        </RouterLink>

        <RouterLink class="nav-item" to="/trash" active-class="is-active">
          <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
            <path
              d="M3.4 5.2h13.2M7.6 5.2V3.9a1.1 1.1 0 0 1 1.1-1.1h2.6a1.1 1.1 0 0 1 1.1 1.1v1.3M5.4 5.2l.7 9.6a1.3 1.3 0 0 0 1.3 1.2h5.2a1.3 1.3 0 0 0 1.3-1.2l.7-9.6"
              fill="none"
              stroke="currentColor"
              stroke-width="1.4"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
          <span>回收站</span>
          <span v-if="trash.total > 0" class="nav-badge numeric">{{ trash.total > 99 ? '99+' : trash.total }}</span>
        </RouterLink>

        <template v-if="auth.isAdmin">
          <p class="nav-group-label">管理</p>
          <RouterLink class="nav-item" to="/admin/users" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <circle cx="8" cy="7" r="2.7" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M3.3 15.4c0-2.4 2.1-4 4.7-4s4.7 1.6 4.7 4"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <path
                d="M14.2 6.2v3.6M12.4 8h3.6"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span>用户管理</span>
          </RouterLink>
          <RouterLink class="nav-item" to="/admin/groups" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <circle cx="7.4" cy="7.6" r="2.5" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M2.9 15.3c0-2.2 2-3.7 4.5-3.7s4.5 1.5 4.5 3.7"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <path
                d="M12.4 5.6a2.4 2.4 0 0 1 0 4.6M14.2 12.2c1.7.4 2.9 1.6 2.9 3.1"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span>组管理</span>
          </RouterLink>
          <RouterLink class="nav-item" to="/admin/audit" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <rect x="3.6" y="3" width="12.8" height="14" rx="2" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M7 7.2h6M7 10h6M7 12.8h3.6"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span>审计日志</span>
          </RouterLink>
          <RouterLink class="nav-item" to="/admin/settings" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <path
                d="M4 6.2h12M4 10h12M4 13.8h12"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <circle cx="7.4" cy="6.2" r="1.5" fill="var(--color-bg)" stroke="currentColor" stroke-width="1.4" />
              <circle cx="12.6" cy="10" r="1.5" fill="var(--color-bg)" stroke="currentColor" stroke-width="1.4" />
              <circle cx="8.6" cy="13.8" r="1.5" fill="var(--color-bg)" stroke="currentColor" stroke-width="1.4" />
            </svg>
            <span>系统设置</span>
          </RouterLink>
          <RouterLink class="nav-item" to="/admin/storage" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <ellipse cx="10" cy="5.4" rx="5.6" ry="2.3" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M4.4 5.4v9.2c0 1.27 2.51 2.3 5.6 2.3s5.6-1.03 5.6-2.3V5.4"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <path
                d="M4.4 10c0 1.27 2.51 2.3 5.6 2.3s5.6-1.03 5.6-2.3"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span>存储</span>
          </RouterLink>
        </template>

        <template v-if="auth.canManageGroups && !auth.isAdmin">
          <p class="nav-group-label">管理</p>
          <RouterLink class="nav-item" to="/admin/groups" active-class="is-active">
            <svg width="16" height="16" viewBox="0 0 20 20" aria-hidden="true">
              <circle cx="7.4" cy="7.6" r="2.5" fill="none" stroke="currentColor" stroke-width="1.4" />
              <path
                d="M2.9 15.3c0-2.2 2-3.7 4.5-3.7s4.5 1.5 4.5 3.7"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
              <path
                d="M12.4 5.6a2.4 2.4 0 0 1 0 4.6M14.2 12.2c1.7.4 2.9 1.6 2.9 3.1"
                fill="none"
                stroke="currentColor"
                stroke-width="1.4"
                stroke-linecap="round"
              />
            </svg>
            <span>组管理</span>
          </RouterLink>
        </template>
      </nav>

      <section v-if="auth.user" class="storage" aria-label="存储空间">
        <div class="storage-head">
          <span>存储</span>
          <span v-if="hasQuota" class="numeric">{{ storagePercent }}%</span>
        </div>
        <div class="storage-track" aria-hidden="true">
          <div
            class="storage-fill"
            :style="{ width: storagePercent > 0 ? `max(3px, ${storagePercent}%)` : '0px' }"
          ></div>
        </div>
        <p class="storage-text">
          <template v-if="hasQuota">{{ usedLabel }} / {{ quotaLabel }}</template>
          <template v-else>{{ usedLabel }} · 未设上限</template>
        </p>
      </section>
    </aside>

    <main class="content">
      <div class="content-body">
        <RouterView />
      </div>
      <footer class="site-foot">
        <div class="site-foot-text">
          <span>{{ site.footerText || '© 2026 Carp' }}</span>
          <nav v-if="site.footerLinks.length > 0" class="site-foot-links">
            <a
              v-for="link in site.footerLinks"
              :key="link.url"
              :href="link.url"
              :target="link.url.startsWith('http') ? '_blank' : undefined"
              rel="noopener noreferrer"
            >{{ link.label }}</a>
          </nav>
        </div>
        <span class="site-foot-version">{{ APP_VERSION_LABEL }}</span>
      </footer>
    </main>

    <UploadDock />
  </div>

  <!-- One media overlay for the whole site: audio survives route changes. -->
  <GlobalMediaOverlay />
</template>

<style scoped>
.shell {
  display: grid;
  grid-template-columns: var(--shell-sidebar) minmax(0, 1fr);
  grid-template-rows: var(--shell-topbar) minmax(0, 1fr);
  min-height: 100vh;
}

.topbar {
  position: sticky;
  top: 0;
  z-index: 20;
  grid-row: 1;
  grid-column: 2;
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 0 var(--gutter-right) 0 var(--topbar-inset-left);
  background: var(--color-bg);
}

.brand-logo {
  width: 40px;
  height: 40px;
  border-radius: 11px;
  object-fit: contain;
  box-shadow: 0 3px 8px rgba(154, 74, 16, 0.2);
}

.brand {
  display: flex;
  align-items: center;
  gap: 11px;
  padding-left: 8px;
  color: var(--color-text);
  text-decoration: none;
}

.brand:hover {
  text-decoration: none;
}

.wordmark {
  font-size: 20px;
  font-weight: 800;
  letter-spacing: -0.03em;
}

.topbar-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

/* Desktop search: a quiet inset field that only lights up on focus. */
.search {
  display: flex;
  flex: 0 1 clamp(320px, 33vw, 560px);
  align-items: center;
  gap: 7px;
  height: 38px;
  padding: 0 10px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  transition:
    border-color var(--dur) var(--ease),
    background-color var(--dur) var(--ease);
}

.search:focus-within {
  background: var(--color-surface);
  border-color: var(--color-primary);
}

.search-icon {
  flex: none;
  color: var(--color-muted);
}

.search-input {
  flex: 1;
  min-width: 0;
  height: 100%;
  padding: 0;
  background: none;
  border: 0;
  color: var(--color-text);
  font: inherit;
  font-size: var(--fs-sm);
}

.search-input:focus {
  outline: none;
}

.search-input::placeholder {
  color: var(--color-muted);
}

.search-input::-webkit-search-cancel-button {
  appearance: none;
}

/* Scoped over .topbar-button so the desktop input stays the only affordance. */
.topbar-actions .search-toggle {
  display: none;
}

/* Phone drop-down: full-width row directly under the top bar. */
.search-mobile {
  position: absolute;
  top: 100%;
  right: 0;
  left: 0;
  z-index: 30;
  display: flex;
  gap: 8px;
  padding: 10px 12px;
  background: var(--color-surface);
  border-bottom: 1px solid var(--color-border);
  box-shadow: var(--shadow-pop);
}

.search-mobile .search-input {
  flex: 1;
  height: 34px;
  min-width: 0;
  padding: 0 10px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
}

.search-mobile .search-input:focus {
  border-color: var(--color-primary);
}

.search-cancel {
  flex: none;
}

.topbar-button {
  position: relative;
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: var(--radius-sm);
  color: var(--color-muted);
  transition:
    background-color var(--dur) var(--ease),
    color var(--dur) var(--ease);
}

.topbar-button:hover,
.topbar-button[aria-expanded='true'] {
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
}

.topbar-badge,
.nav-badge {
  display: grid;
  place-items: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: var(--radius-pill);
  background: var(--color-primary);
  font-size: 10px;
  font-weight: 600;
  line-height: 1;
  color: var(--color-on-primary);
}

.topbar-badge {
  position: absolute;
  top: 3px;
  right: 3px;
}

.nav-badge {
  margin-left: auto;
}

.user {
  position: relative;
}

.user-button {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 34px;
  padding: 0 8px;
  border-radius: var(--radius-sm);
  color: var(--color-text);
  transition: background-color var(--dur) var(--ease);
}

.user-button:hover {
  background: var(--color-primary-soft);
}

.avatar-img {
  object-fit: cover;
}

.avatar {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
  font-size: 11px;
  font-weight: 600;
  line-height: 1;
}

.user-name {
  max-width: 140px;
  overflow: hidden;
  font-size: var(--fs-sm);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chevron {
  color: var(--color-muted);
  transition: transform var(--dur) var(--ease);
}

.chevron.is-open {
  transform: rotate(180deg);
}

.user-menu {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 30;
  width: 232px;
  padding: 6px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  box-shadow: var(--shadow-pop);
}

.menu-meta {
  display: flex;
  flex-direction: column;
  padding: 6px 10px 9px;
  border-bottom: 1px solid var(--color-border);
  margin-bottom: 4px;
}

.menu-name {
  font-size: var(--fs-sm);
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.menu-role {
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.menu-item {
  display: block;
  width: 100%;
  padding: 7px 10px;
  border-radius: var(--radius-sm);
  font-size: var(--fs-sm);
  text-align: left;
  text-decoration: none;
  color: var(--color-text);
}

.menu-item:hover {
  background: var(--color-primary-soft);
}

.menu-label {
  margin: 10px 10px 6px;
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.theme-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.theme-option {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  padding: 6px 10px;
  border-radius: var(--radius-sm);
  font-size: var(--fs-sm);
  color: var(--color-text);
}

.theme-option:hover,
.theme-option.is-current {
  background: var(--color-primary-soft);
}

.theme-option.is-current {
  font-weight: 600;
}

.theme-swatch {
  display: flex;
  flex: none;
  flex-direction: column;
  gap: 2px;
  width: 14px;
}

.theme-swatch i {
  display: block;
  height: 5px;
  border-radius: 2px;
}

.theme-swatch i:first-child {
  border-radius: 3px 3px 2px 2px;
}

.theme-swatch i:last-child {
  border-radius: 2px 2px 3px 3px;
}

.theme-name {
  flex: 1;
  text-align: left;
}

.theme-check {
  color: var(--color-primary);
  font-weight: 700;
  opacity: 0;
}

.theme-option.is-current .theme-check {
  opacity: 1;
}

.menu-divider {
  height: 1px;
  margin: 6px;
  background: var(--color-border);
}

.sidebar {
  position: sticky;
  top: 0;
  grid-row: 1 / -1;
  grid-column: 1;
  align-self: start;
  display: flex;
  flex-direction: column;
  gap: 0;
  height: 100vh;
  padding: 0 16px 16px;
  background: var(--sidebar-bg);
}

.sidebar > .brand {
  display: flex;
  align-items: center;
  height: 88px;
  margin-bottom: 6px;
  color: var(--sidebar-ink);
}

.sidebar .nav {
  margin-top: 30px;
}

.nav {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.nav-item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 12px;
  height: 38px;
  padding: 0 12px;
  border-radius: 10px;
  color: var(--sidebar-muted);
  font-size: 14px;
  font-weight: 500;
  text-decoration: none;
  transition:
    background-color var(--dur) var(--ease),
    color var(--dur) var(--ease);
}

.nav-item svg {
  width: 18px;
  height: 18px;
  flex: none;
}

.nav-item:hover {
  background: var(--sidebar-hover);
  color: var(--sidebar-ink);
  text-decoration: none;
}

.nav-item.is-active {
  background: var(--nav-active-bg);
  color: var(--nav-active-ink);
  font-weight: 600;
  box-shadow: var(--shadow-xs);
}

.nav-item.is-active::before {
  content: '';
  position: absolute;
  left: 0;
  top: 11px;
  bottom: 11px;
  width: 2.5px;
  border-radius: 0 2px 2px 0;
  background: var(--color-primary);
}

.nav-group-label {
  margin: 18px 0 6px;
  padding: 0 12px;
  color: var(--sidebar-muted);
  font-size: 12px;
  font-weight: 500;
}

/* Group space entries (P1): name truncates, manager gets a compact tag. */
.nav-item-group {
  gap: 8px;
}

.nav-group-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.nav-role-tag {
  margin-left: auto;
  flex: none;
  padding: 1px 5px;
  border-radius: var(--radius-badge);
  background: var(--nav-active-bg);
  color: var(--sidebar-muted);
  font-size: 10px;
  font-weight: 600;
  line-height: 1.5;
}

.nav-item.is-active .nav-role-tag {
  background: var(--sidebar-card-bg);
}

.storage {
  display: flex;
  flex-direction: column;
  gap: 7px;
  margin-top: auto;
  padding: 14px;
  border: 1px solid var(--sidebar-border);
  border-radius: 12px;
  background: var(--sidebar-card-bg);
}

.storage-head {
  display: flex;
  justify-content: space-between;
  font-size: var(--fs-xs);
  color: var(--sidebar-card-muted);
}

.storage-track {
  height: 6px;
  overflow: hidden;
  background: var(--sidebar-border);
  border-radius: 999px;
  border-radius: var(--radius-pill);
}

.storage-fill {
  height: 100%;
  background: var(--color-primary);
  border-radius: inherit;
  transition: width 240ms var(--ease);
}

.storage-text {
  font-size: var(--fs-xs);
  color: var(--sidebar-card-muted);
  font-variant-numeric: tabular-nums;
}

.content {
  grid-row: 2;
  grid-column: 2;
  display: flex;
  flex-direction: column;
  min-width: 0;
  padding: 22px var(--gutter-right) 0 var(--content-pad-x);
}

.content-body {
  flex: 1;
  min-width: 0;
}

.site-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-top: auto;
  padding: 18px 2px 20px;
  font-size: 12px;
  color: var(--color-muted);
}

.site-foot-text {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 14px;
}

.site-foot-links {
  display: flex;
  gap: 14px;
}

.site-foot-links a {
  color: var(--color-muted);
}

.site-foot-links a:hover {
  color: var(--color-primary-ink);
}

@media (max-width: 860px) {
  .shell {
    grid-template-columns: minmax(0, 1fr);
  }

  .sidebar {
    display: none;
  }

  .content {
    grid-row: 2;
    grid-column: 1;
    padding: 20px 16px 48px;
  }

  .topbar {
    padding: 0 12px;
  }

  .search {
    display: none;
  }

  .topbar-actions .search-toggle {
    display: grid;
  }
}
/* ---- warm premium pass ---- */
.sidebar {
  background: var(--sidebar-bg);
  border-right: 1px solid var(--sidebar-border);
}
.nav-item {
  position: relative;
  height: 38px;
  border-radius: var(--radius-sm);
}
.nav-item:hover {
  background: var(--sidebar-hover);
  box-shadow: var(--shadow-xs);
}
.nav-item.is-active {
  background: var(--nav-active-bg);
  color: var(--nav-active-ink);
  box-shadow: var(--shadow-xs);
}
.nav-item.is-active::before {
  content: '';
  position: absolute;
  left: 0;
  top: 10px;
  bottom: 10px;
  width: 2px;
  border-radius: 2px;
  background: var(--color-primary);
}
.nav-group-label {
  margin: 20px 0 6px;
  color: var(--sidebar-muted);
  font-size: 11px;
  letter-spacing: 0.1em;
}
.search {
  max-width: clamp(320px, 33vw, 560px);
  height: 38px;
  padding: 0 14px;
  background: var(--color-surface);
  border-radius: var(--radius-pill);
}
.search:focus-within {
  border-color: var(--color-primary);
  box-shadow: 0 0 0 4px var(--color-focus-soft);
}
.topbar {
  background: color-mix(in srgb, var(--color-bg) 88%, transparent);
  border-bottom: 1px solid var(--color-border-soft);
}
</style>