import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { fetchSiteConfig, type SiteFooterLink } from '../api/client'

const DEFAULT_TITLE = '你的文件，\n放在自己的服务器。'
const DEFAULT_SUBTITLE = '自托管网盘 · 数据始终在你自己手里。支持离线下载、分享、组空间与媒体播放。'

/**
 * Public, anonymous site branding (M6.8).
 *
 * Loaded once per page load; failures are silent and the built-in defaults are
 * shown, so a broken settings row can never block login.
 */
export const useSiteStore = defineStore('site', () => {
  const loaded = ref(false)
  const siteName = ref('Carp')
  const logoURL = ref('')
  const loginTitle = ref(DEFAULT_TITLE)
  const loginSubtitle = ref(DEFAULT_SUBTITLE)
  const footerText = ref('')
  const footerLinks = ref<SiteFooterLink[]>([])

  const hasLogo = computed(() => logoURL.value !== '')
  const titleLines = computed(() => loginTitle.value.split('\n').filter((line) => line.trim() !== ''))
  const hasFooter = computed(() => footerText.value.trim() !== '' || footerLinks.value.length > 0)

  let pending: Promise<void> | null = null

  /** Fetch the public config; safe to call from every entry point. */
  function ensure(): Promise<void> {
    if (loaded.value) return Promise.resolve()
    if (pending) return pending
    pending = (async () => {
      try {
        const cfg = await fetchSiteConfig()
        siteName.value = cfg.site_name || 'Carp'
        logoURL.value = cfg.logo_url || ''
        loginTitle.value = cfg.login.title || DEFAULT_TITLE
        loginSubtitle.value = cfg.login.subtitle || DEFAULT_SUBTITLE
        footerText.value = cfg.footer?.text || ''
        footerLinks.value = Array.isArray(cfg.footer?.links) ? cfg.footer.links : []
        applyFavicon()
      } catch {
        // Defaults stay in place.
      } finally {
        loaded.value = true
        pending = null
      }
    })()
    return pending
  }

  /** Re-read after the root administrator saves the branding form. */
  async function refresh(): Promise<void> {
    loaded.value = false
    await ensure()
  }

  function applyFavicon(): void {
    const link = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    if (!link) return
    link.href = logoURL.value || '/favicon.svg'
  }

  return {
    loaded,
    siteName,
    logoURL,
    hasLogo,
    loginTitle,
    loginSubtitle,
    titleLines,
    footerText,
    footerLinks,
    hasFooter,
    ensure,
    refresh,
  }
})
