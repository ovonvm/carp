/// <reference types="vite/client" />

declare const __APP_VERSION__: string

interface SiteBoot {
  site_name?: string
  logo_url?: string
  title?: string
  favicon?: string
  footer_text?: string
  footer_links?: { label: string; url: string }[]
}

interface Window {
  __SITE_BOOT__?: SiteBoot | null
}
