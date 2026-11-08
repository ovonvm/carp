export type ThemeId = 'sunset' | 'forest' | 'coast' | 'blossom' | 'dark'

export interface ThemeDef {
  id: ThemeId
  name: string
  /** 4 swatch colours shown in the account menu. */
  colors: [string, string, string, string]
}

export const THEMES: ThemeDef[] = [
  { id: 'sunset', name: '日落', colors: ['#ffedb9', '#ffcb56', '#ffa259', '#ff7e7e'] },
  { id: 'forest', name: '森林', colors: ['#e8f5e9', '#a5d6a7', '#66bb6a', '#1b5e20'] },
  { id: 'coast', name: '海岸', colors: ['#f2f2ed', '#7cd5c7', '#464b71', '#118ab2'] },
  { id: 'blossom', name: '极光', colors: ['#fff4f4', '#ffd6e0', '#bdb2ff', '#3a86ff'] },
  { id: 'dark', name: '暗色', colors: ['#171310', '#3d3225', '#f79a55', '#ffcb56'] },
]

const STORAGE_KEY = 'carp.theme'
export const DEFAULT_THEME: ThemeId = 'sunset'

function isThemeId(value: unknown): value is ThemeId {
  return THEMES.some((theme) => theme.id === value)
}

export function currentTheme(): ThemeId {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (isThemeId(stored)) return stored
  } catch {
    // private mode / storage disabled — fall through to default
  }
  return DEFAULT_THEME
}

export function applyTheme(id: ThemeId): void {
  document.documentElement.dataset.theme = id
  try {
    localStorage.setItem(STORAGE_KEY, id)
  } catch {
    // ignore storage failures: the in-memory choice still applies for this page
  }
  syncThemeColor()
}

export function themeName(id: ThemeId): string {
  return THEMES.find((theme) => theme.id === id)?.name ?? DEFAULT_THEME
}

export function initTheme(): void {
  document.documentElement.dataset.theme = currentTheme()
  syncThemeColor()
}

/** Keep the browser UI colour in sync with the active theme. */
export function syncThemeColor(): void {
  const bg = getComputedStyle(document.documentElement).getPropertyValue('--color-bg').trim()
  if (!bg) return
  document.querySelectorAll<HTMLMetaElement>('meta[name="theme-color"]').forEach((meta) => {
    meta.content = bg
  })
}
