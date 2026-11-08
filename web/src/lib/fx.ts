/**
 * "Carp 趣味动效" (easter-egg mode) — a tiny canvas particle system.
 *
 * Off by default: the switch lives in 设置 → 外观 and the choice is stored in
 * localStorage. Everything is pure Canvas 2D (no animation library), every
 * effect lasts ≤1.4s with ≤220 particles, and `prefers-reduced-motion` always
 * wins. Effects are decorative: nothing here blocks navigation or data.
 */

export type FxKind = 'burst' | 'drift' | 'confetti' | 'float' | 'ring' | 'scan' | 'dust'

export interface FxPreset {
  kind: FxKind
  colors: string[]
}

export interface FxPlayDetail {
  name: string
  /** Viewport coordinates; when omitted the layer picks a sensible origin. */
  x?: number
  y?: number
}

const ORANGE = ['#e06a1f', '#f59e0b', '#fbbf24', '#fde68a']
const ROSE = ['#e11d48', '#fb7185', '#fda4af', '#fecdd3']
const BLUE = ['#2563eb', '#60a5fa', '#93c5fd', '#dbeafe']
const TEAL = ['#0d9488', '#2dd4bf', '#99f6e4', '#ccfbf1']
const SLATE = ['#475569', '#94a3b8', '#cbd5e1', '#e2e8f0']
const INDIGO = ['#4f46e5', '#818cf8', '#c7d2fe', '#e0e7ff']
const MINT = ['#059669', '#34d399', '#a7f3d0', '#d1fae5']
const PURPLE = ['#7c3aed', '#a78bfa', '#ddd6fe', '#ede9fe']
const AMBER = ['#b45309', '#f59e0b', '#fcd34d', '#fef3c7']
const CORAL = ['#f97316', '#fb923c', '#fdba74', '#fed7aa']
const STONE = ['#57534e', '#a8a29e', '#d6d3d1', '#e7e5e4']

/** One preset per destination so each part of the app feels different. */
const PRESETS: Record<string, FxPreset> = {
  files: { kind: 'burst', colors: ORANGE },
  shares: { kind: 'drift', colors: ROSE },
  downloads: { kind: 'drift', colors: BLUE },
  transfers: { kind: 'drift', colors: TEAL },
  trash: { kind: 'confetti', colors: SLATE },
  'group-files': { kind: 'ring', colors: INDIGO },
  'admin-users': { kind: 'float', colors: MINT },
  'admin-groups': { kind: 'ring', colors: PURPLE },
  'admin-audit': { kind: 'scan', colors: AMBER },
  'admin-settings': { kind: 'float', colors: CORAL },
  'admin-storage': { kind: 'confetti', colors: STONE },
  upload: { kind: 'confetti', colors: ORANGE },
  delete: { kind: 'dust', colors: SLATE },
  restore: { kind: 'float', colors: MINT },
  share: { kind: 'drift', colors: ROSE },
  login: { kind: 'ring', colors: ORANGE },
}

export function presetFor(name: string): FxPreset | null {
  return PRESETS[name] ?? null
}

/** Sidebar destinations, keyed by route name. */
export function routeFxPreset(routeName: string | null | undefined): string | null {
  if (!routeName) return null
  const map: Record<string, string> = {
    files: 'files',
    shares: 'shares',
    downloads: 'downloads',
    transfers: 'transfers',
    trash: 'trash',
    'group-files': 'group-files',
    'admin-users': 'admin-users',
    'admin-groups': 'admin-groups',
    'admin-audit': 'admin-audit',
    'admin-settings': 'admin-settings',
    'admin-storage': 'admin-storage',
  }
  return map[routeName] ?? null
}

const STORAGE_KEY = 'carp.fx'

export function fxEnabled(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === '1'
  } catch {
    return false
  }
}

export function setFxEnabled(enabled: boolean): void {
  try {
    localStorage.setItem(STORAGE_KEY, enabled ? '1' : '0')
  } catch {
    // Private mode: effects simply stay off for this tab.
  }
  window.dispatchEvent(new CustomEvent('carp:fx-enabled', { detail: { enabled } }))
}

export function playFx(name: string, origin?: { x: number; y: number }): void {
  window.dispatchEvent(new CustomEvent<FxPlayDetail>('carp:fx-play', { detail: { name, ...origin } }))
}
