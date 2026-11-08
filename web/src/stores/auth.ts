import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { fetchMe, login as apiLogin, logout as apiLogout } from '../api/client'
import type { User } from '../api/client'
import { useMediaStore } from './media'
import { usePreferencesStore } from './preferences'
import { applyTheme } from '../lib/theme'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const hydrated = ref(false)
  /** Server policy: users may self-delete when enabled (root settings). */
  const deletionEnabled = ref(false)

  let sessionRequest: Promise<void> | null = null

  const isAuthenticated = computed(() => user.value !== null)
  const isAdmin = computed(() => user.value?.role === 'admin')
  /** Root administrator: the only one allowed to change the registration switch. */
  const isRoot = computed(() => user.value?.is_root === true)
  /** Delegated group creation/admin rights (P1). */
  const canCreateGroups = computed(() => user.value?.can_create_groups === true)
  /** Admin or delegate: may open the group console (`/admin/groups`). */
  const canManageGroups = computed(() => isAdmin.value || canCreateGroups.value)

  /** Restore the session once per page load; 401 simply means "signed out". */
  function ensureSession(): Promise<void> {
    if (hydrated.value) return Promise.resolve()
    if (sessionRequest) return sessionRequest

    sessionRequest = (async () => {
      try {
        const me = await fetchMe()
        user.value = me.user
        deletionEnabled.value = me.deletionEnabled
        await syncPreferences(me.preferences)
      } catch {
        user.value = null
      } finally {
        hydrated.value = true
        sessionRequest = null
      }
    })()

    return sessionRequest
  }

  async function login(username: string, password: string): Promise<void> {
    // Never let the previous account's audio survive a new login (stale tab or
    // expired session -> login as someone else).
    useMediaStore().stop()
    user.value = await apiLogin(username, password)
    hydrated.value = true
    await usePreferencesStore().load(true)
    await syncPreferences(usePreferencesStore().values)
  }

  /** Re-read the profile so `used_bytes`/`quota_bytes` follow uploads. */
  async function refreshUser(): Promise<void> {
    if (!user.value) return
    try {
      const me = await fetchMe()
      user.value = me.user
      deletionEnabled.value = me.deletionEnabled
    } catch {
      // A transient failure must not sign the user out on its own.
    }
  }

  async function logout(): Promise<void> {
    try {
      await apiLogout()
    } finally {
      useMediaStore().stop()
      usePreferencesStore().reset()
      user.value = null
      hydrated.value = true
    }
  }

  function clearUser(): void {
    user.value = null
  }

  /** Hydrate account preferences coming with /auth/me or login. */
  async function syncPreferences(prefs: Record<string, string>): Promise<void> {
    const store = usePreferencesStore()
    store.values = prefs && Object.keys(prefs).length > 0 ? prefs : {}
    if (Object.keys(store.values).length === 0) {
      // Migration: first login on a device with a local theme adopts it.
      try {
        const local = window.localStorage.getItem('carp.theme')
        if (local) await store.save({ theme: local })
      } catch {
        // Best effort only.
      }
    }
    syncThemeFromPreferences()
    syncLocalPreferences()
  }

  function syncThemeFromPreferences(): void {
    const theme = usePreferencesStore().get('theme')
    if (theme) applyTheme(theme as Parameters<typeof applyTheme>[0])
  }

  /** Mirror server preferences into the local cache used before hydration. */
  function syncLocalPreferences(): void {
    const store = usePreferencesStore()
    const view = store.get('view_mode')
    if (view) {
      try {
        window.localStorage.setItem('carp.viewMode.v2', view)
      } catch {}
    }
    const fx = store.get('fx')
    if (fx) {
      try {
        window.localStorage.setItem('carp.fx', fx === '1' ? '1' : '0')
      } catch {}
    }
  }

  return {
    user,
    hydrated,
    isAuthenticated,
    isAdmin,
    isRoot,
    deletionEnabled,
    canCreateGroups,
    canManageGroups,
    ensureSession,
    login,
    refreshUser,
    logout,
    clearUser,
  }
})
