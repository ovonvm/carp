// Build-time version injected by Vite from APP_VERSION (see scripts/build*.sh).
// Falls back to `dev` for local dev servers where no git tag is known.
export const APP_VERSION: string =
  typeof __APP_VERSION__ === 'string' && __APP_VERSION__ ? __APP_VERSION__ : 'dev'

export const APP_VERSION_LABEL: string = APP_VERSION.startsWith('v')
  ? APP_VERSION
  : `v${APP_VERSION}`
