<script setup lang="ts">
import { computed } from 'vue'

/** File-type tile used by the grid cards: one tinted square plus a line icon. */
const props = defineProps<{ name: string; dir?: boolean }>()

type Kind = 'dir' | 'image' | 'video' | 'audio' | 'pdf' | 'doc' | 'sheet' | 'archive' | 'code' | 'file'

const IMAGE = ['jpg', 'jpeg', 'png', 'gif', 'webp', 'avif', 'bmp', 'svg']
const VIDEO = ['mp4', 'mkv', 'webm', 'mov', 'avi', 'm4v', 'm2ts', 'ts', 'wmv', 'flv']
const AUDIO = ['mp3', 'flac', 'wav', 'aac', 'm4a', 'ogg', 'opus', 'wma']
const SHEET = ['xls', 'xlsx', 'csv', 'ods']
const DOC = ['doc', 'docx', 'txt', 'md', 'rtf', 'odt', 'ppt', 'pptx']
const ARCHIVE = ['zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz', 'iso']
const CODE = ['js', 'ts', 'vue', 'go', 'py', 'json', 'yml', 'yaml', 'sh', 'css', 'html', 'sql']

const kind = computed<Kind>(() => {
  if (props.dir) return 'dir'
  const ext = props.name.includes('.') ? (props.name.split('.').pop() ?? '').toLowerCase() : ''
  if (IMAGE.includes(ext)) return 'image'
  if (VIDEO.includes(ext)) return 'video'
  if (AUDIO.includes(ext)) return 'audio'
  if (ext === 'pdf') return 'pdf'
  if (SHEET.includes(ext)) return 'sheet'
  if (DOC.includes(ext)) return 'doc'
  if (ARCHIVE.includes(ext)) return 'archive'
  if (CODE.includes(ext)) return 'code'
  return 'file'
})
</script>

<template>
  <span class="type-tile" :class="`is-${kind}`" aria-hidden="true">
    <svg v-if="kind === 'dir'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="M3.5 7.2A2.2 2.2 0 0 1 5.7 5h3.6l2.1 2.4h7a2.2 2.2 0 0 1 2.1 2.2v6.6a2.2 2.2 0 0 1-2.1 2.2H5.7a2.2 2.2 0 0 1-2.2-2.2V7.2Z" />
    </svg>
    <svg v-else-if="kind === 'image'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <rect x="3.5" y="4.5" width="17" height="15" rx="2.4" />
      <circle cx="8.4" cy="9.2" r="1.4" />
      <path d="m4.6 17.4 4.4-4.2 3 2.7 3-3.2 3.8 4.1" />
    </svg>
    <svg v-else-if="kind === 'video'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <rect x="3.5" y="5" width="17" height="14" rx="2.6" />
      <path d="m10 9.2 4.6 2.8L10 14.8V9.2Z" fill="currentColor" stroke="none" />
    </svg>
    <svg v-else-if="kind === 'audio'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="M9 17.2V7.4l8-1.6v9.8" />
      <circle cx="7.4" cy="17.4" r="1.9" />
      <circle cx="15.4" cy="15.6" r="1.9" />
    </svg>
    <svg v-else-if="kind === 'pdf' || kind === 'doc'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="M6.2 3.5h7.2l4.4 4.4v12.6H6.2V3.5Z" />
      <path d="M13.2 3.7v4.4h4.4M9 12h6M9 15.2h6" />
    </svg>
    <svg v-else-if="kind === 'sheet'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <rect x="3.8" y="4.2" width="16.4" height="15.6" rx="2.2" />
      <path d="M3.8 9.4h16.4M9.6 9.4v10.4M14.8 9.4v10.4" />
    </svg>
    <svg v-else-if="kind === 'archive'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <rect x="4" y="4" width="16" height="16" rx="2.4" />
      <path d="M10 4v3M14 4v3M12 7v3M10 13h4v3h-4z" />
    </svg>
    <svg v-else-if="kind === 'code'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="m8.2 8-4 4 4 4M15.8 8l4 4-4 4" />
    </svg>
    <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
      <path d="M6.4 3.6h7.4l4 4v12.8H6.4z" />
      <path d="M13.6 3.8v4h4M9.4 13h5.2M9.4 16.2h5.2" />
    </svg>
  </span>
</template>

<style scoped>
.type-tile {
  display: inline-flex;
  width: 44px;
  height: 44px;
  flex: none;
  align-items: center;
  justify-content: center;
  border-radius: 12px;
  background: var(--color-surface-2);
  color: var(--color-muted);
}

.type-tile svg {
  width: 23px;
  height: 23px;
}

.is-dir { background: #ffd9a6; color: #b46a20; }
.is-image { background: #ffe9e9; color: #d9694e; }
.is-video { background: #ffefd9; color: #d9822b; }
.is-audio { background: #ffedb9; color: #a9781c; }
.is-pdf { background: #ffe2d2; color: #c95b43; }
.is-doc { background: #fff3d0; color: #a5821f; }
.is-sheet { background: #fff0d8; color: #9a7a22; }
.is-archive { background: #fff0e0; color: #b07b45; }
.is-code { background: #fff3e0; color: #a06a2f; }
.is-file { background: #f3eedf; color: #8c7864; }
</style>
