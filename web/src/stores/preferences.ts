import { ref } from 'vue'
import { defineStore } from 'pinia'
import { fetchPreferences, putPreferences, type PreferenceMap } from '../api/client'

/**
 * Per-account UI preferences. The store keeps the server map as the source of
 * truth while the DOM keeps applying changes immediately (optimistic), so a
 * failed PUT never leaves the UI stuck on the old value.
 */
export const usePreferencesStore = defineStore('preferences', () => {
  const values = ref<PreferenceMap>({})
  let pending: Promise<void> | null = null

  function get(key: string): string | undefined {
    return values.value[key]
  }

  /** Load once per session; anonymous visitors get an empty map. */
  function load(force = false): Promise<void> {
    if (pending) return pending
    if (!force && Object.keys(values.value).length > 0) return Promise.resolve()
    pending = (async () => {
      try {
        values.value = await fetchPreferences()
      } catch {
        // 401 / offline: anonymous pages simply have no account preferences.
        values.value = {}
      } finally {
        pending = null
      }
    })()
    return pending
  }

  function reset(): void {
    values.value = {}
    pending = null
  }

  /** Persist a partial patch; resolves with the full server map. */
  async function save(patch: PreferenceMap): Promise<void> {
    try {
      values.value = await putPreferences(patch)
    } catch (err) {
      // Keep the optimistic value for this tab, but surface the failure so the
      // caller can tell the user it did not reach the account.
      throw err
    }
  }

  return { values, get, load, save, reset }
})
