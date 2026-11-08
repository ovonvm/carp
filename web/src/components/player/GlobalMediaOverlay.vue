<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { NodeItem } from '../../api/client'
import { fileStem, formatClock, subtitleFormat } from '../../lib/media'
import { convertSubtitle } from '../../lib/subtitles'
import { useMediaStore } from '../../stores/media'
import { usePlayerStore } from '../../stores/player'
import AudioPlayer from './AudioPlayer.vue'
import VideoPlayer from './VideoPlayer.vue'
import VideoInfoBar from './VideoInfoBar.vue'
import PlayerPlaylist from './PlayerPlaylist.vue'

interface PlayerControls {
  toggle: () => void
  play: () => void
  pause: () => void
  seekTo: (seconds: number) => void
  seekBy: (delta: number) => void
  setVolume: (volume: number) => void
  toggleMute: () => void
  setSpeed: (speed: number) => void
  toggleFullscreen?: () => void
  getProgress?: () => { position: number; duration: number }
}

const store = useMediaStore()
const player = usePlayerStore()

const audioRef = ref<PlayerControls | null>(null)
const videoRef = ref<PlayerControls | null>(null)
const subtitleUrl = ref<string | null>(null)
const subtitleNote = ref<string | null>(null)
const subtitleLoading = ref(false)
const resumeAt = ref(0)
const playing = ref(false)
const mediaError = ref('')
const playlistOpen = ref(true)
const compact = () => window.matchMedia('(max-width: 760px)').matches
watch(
  () => store.open,
  (open) => {
    if (open) playlistOpen.value = !compact()
  },
)

/** Floating audio mini window (shown only for audio when the overlay is hidden). */
const miniPosition = ref(0)
const miniDuration = ref(0)
const volume = ref(1)
/** In-mini playlist: expands downward under the card, paged (8 per page). */
const miniListOpen = ref(false)
const miniPage = ref(0)
const MINI_PAGE_SIZE = 8
const miniPages = computed(() => Math.max(1, Math.ceil(store.playlist.length / MINI_PAGE_SIZE)))
const miniTracks = computed(() =>
  store.playlist.slice(miniPage.value * MINI_PAGE_SIZE, (miniPage.value + 1) * MINI_PAGE_SIZE),
)
const canSkip = computed(() => store.playlist.length > 1)
const miniVisible = computed(() => !store.open && kind.value === 'audio' && store.miniOpen && current.value !== null)

const kind = computed(() => store.kind)
const current = computed(() => store.node)
const subtitleLabel = computed(() => {
  const format = store.subtitle ? subtitleFormat(store.subtitle.name) : null
  return format ? format.toUpperCase() : null
})
const playModeLabel = computed(
  () =>
    ({ sequence: '顺序播放', list: '列表循环', one: '单曲循环', shuffle: '随机播放' })[player.mode],
)

function controls(): PlayerControls | null {
  return kind.value === 'audio' ? audioRef.value : videoRef.value
}

function close(): void {
  // Hide the overlay without ending audio (audio keeps playing in its mini
  // window). Video minimization uses minimize()/restoreVideo() instead.
  store.hide()
}

/** Backdrop click: collapses audio, never closes a video (only the X stops it). */
function onBackdropClick(): void {
  if (kind.value === 'video') return
  close()
}

/** Minimize: audio collapses to its mini window, video pauses to the top-bar icon. */
function minimize(): void {
  if (kind.value === 'video') {
    videoRef.value?.pause()
    store.minimizeVideo()
    return
  }
  close()
}

function onWindowKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !store.open) return
  event.preventDefault()
  minimize()
}

function reopen(): void {
  store.miniOpen = true
  store.open = true
  if (kind.value === 'video') {
    // Autoplay is allowed here: the click that reopened the overlay is a gesture.
    void nextTick(() => videoRef.value?.play())
  }
}

function syncMiniProgress(): void {
  const progress = audioRef.value?.getProgress?.()
  if (progress) {
    miniPosition.value = progress.position
    miniDuration.value = progress.duration
  }
}

function seekMini(event: Event): void {
  const value = Number((event.target as HTMLInputElement).value)
  audioRef.value?.seekTo(value)
  miniPosition.value = value
}

function setMiniVolume(event: Event): void {
  const value = Number((event.target as HTMLInputElement).value) / 100
  volume.value = value
  audioRef.value?.setVolume(value)
}

function isInsideMini(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null
  return Boolean(element?.closest?.('[data-audio-mini], [data-audio-toggle]'))
}

function onDocumentPointer(event: PointerEvent): void {
  if (miniVisible.value && !isInsideMini(event.target)) store.miniOpen = false
}

let miniTimer = 0
watch(
  miniVisible,
  (visible) => {
    window.clearInterval(miniTimer)
    if (visible) {
      syncMiniProgress()
      miniTimer = window.setInterval(syncMiniProgress, 800)
    }
  },
  { immediate: true },
)

onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointer, true)
  window.addEventListener('keydown', onWindowKeydown)
})

function download(): void {
  if (!current.value) return
  const anchor = document.createElement('a')
  anchor.href = store.downloadURL(current.value.id)
  anchor.download = current.value.name
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

function playNext(): void {
  const items = store.playlist
  if (items.length <= 1) return
  const index = items.findIndex((item) => item.id === current.value?.id)
  let target = items[(index + 1) % items.length]
  if (kind.value === 'audio' && player.mode === 'shuffle') {
    const others = items.filter((item) => item.id !== current.value?.id)
    target = others[Math.floor(Math.random() * others.length)] ?? target
  }
  if (target) store.navigate(target, { open: store.open })
}

function playPrev(): void {
  const items = store.playlist
  if (items.length <= 1) return
  const index = items.findIndex((item) => item.id === current.value?.id)
  let target = items[(index - 1 + items.length) % items.length]
  if (kind.value === 'audio' && player.mode === 'shuffle') {
    const others = items.filter((item) => item.id !== current.value?.id)
    target = others[Math.floor(Math.random() * others.length)] ?? target
  }
  if (target) store.navigate(target, { open: store.open })
}

/** Sequence mode stops at the end of the list; no overlay is ever forced open. */
function onAudioEnded(): void {
  const items = store.playlist
  if (items.length === 0) return
  const index = items.findIndex((item) => item.id === current.value?.id)
  if (player.mode === 'one') return
  if (player.mode === 'list') {
    const target = items[(index + 1) % items.length]
    if (target) store.navigate(target, { open: store.open })
    return
  }
  if (player.mode === 'shuffle') {
    if (items.length <= 1) return
    const others = items.filter((item) => item.id !== current.value?.id)
    const target = others[Math.floor(Math.random() * others.length)]
    if (target) store.navigate(target, { open: store.open })
    return
  }
  // sequence: advance while tracks remain, otherwise stay paused at the end.
  if (index >= 0 && index < items.length - 1) {
    const target = items[index + 1]
    if (target) store.navigate(target, { open: store.open })
  }
}

function toggleMiniList(): void {
  miniListOpen.value = !miniListOpen.value
  if (!miniListOpen.value) miniPage.value = 0
}

/** Cycle 顺序 → 列表 → 单曲 → 随机, same order as the full player. */
function cycleMiniMode(): void {
  const order = ['sequence', 'list', 'one', 'shuffle'] as const
  const next = order[(order.indexOf(player.mode) + 1) % order.length] ?? 'sequence'
  player.setMode(next)
}

function pickMiniTrack(item: NodeItem): void {
  store.navigate(item, { open: false })
  miniListOpen.value = true
}

watch(
  () => store.playlist.length,
  (length) => {
    const pages = Math.max(1, Math.ceil(length / MINI_PAGE_SIZE))
    if (miniPage.value >= pages) miniPage.value = 0
  },
)

function clearSubtitle(): void {
  if (subtitleUrl.value?.startsWith('blob:')) URL.revokeObjectURL(subtitleUrl.value)
  subtitleUrl.value = null
  subtitleNote.value = null
}

async function loadSubtitle(): Promise<void> {
  clearSubtitle()
  const sidecar = store.subtitle
  if (!sidecar) return
  const format = subtitleFormat(sidecar.name)
  if (!format) return
  if (sidecar.size > 1024 * 1024) {
    subtitleNote.value = '字幕文件超过 1 MB，已跳过'
    return
  }
  if (format === 'vtt') {
    subtitleUrl.value = store.urlFor(sidecar.id)
    return
  }
  subtitleLoading.value = true
  try {
    const response = await fetch(store.urlFor(sidecar.id))
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    const converted = convertSubtitle(sidecar.name, await response.text())
    subtitleUrl.value = URL.createObjectURL(new Blob([converted.vtt], { type: 'text/vtt' }))
    subtitleNote.value = converted.note
  } catch {
    subtitleNote.value = '字幕加载失败（可下载后使用本地播放器）'
  } finally {
    subtitleLoading.value = false
  }
}

function toggleSubtitles(): void {
  player.setShowSubtitles(!player.showSubtitles)
  if (store.subtitle && subtitleUrl.value === null) void loadSubtitle()
}

function resumeFromStart(): void {
  controls()?.seekTo(0)
  if (current.value) player.clearPosition(current.value.id)
  resumeAt.value = 0
}

function setupMediaSession(): void {
  if (!('mediaSession' in navigator) || !current.value) return
  try {
    navigator.mediaSession.metadata = new MediaMetadata({
      title: fileStem(current.value.name),
      artist: 'Carp',
      artwork: store.cover ? [{ src: store.urlFor(store.cover.id), sizes: '512x512' }] : [],
    })
    navigator.mediaSession.setActionHandler('play', () => controls()?.play())
    navigator.mediaSession.setActionHandler('pause', () => controls()?.pause())
    navigator.mediaSession.setActionHandler('previoustrack', playPrev)
    navigator.mediaSession.setActionHandler('nexttrack', playNext)
    navigator.mediaSession.setActionHandler('seekbackward', () => controls()?.seekBy(-10))
    navigator.mediaSession.setActionHandler('seekforward', () => controls()?.seekBy(10))
  } catch {
    // Best effort only.
  }
}

watch(
  () => current.value?.id,
  () => {
    clearSubtitle()
    mediaError.value = ''
    resumeAt.value = current.value ? (player.positionFor(current.value.id)?.position ?? 0) : 0
    setupMediaSession()
    if (kind.value === 'video') void loadSubtitle()
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onWindowKeydown)
  document.removeEventListener('pointerdown', onDocumentPointer, true)
  window.clearInterval(miniTimer)
  clearSubtitle()
})
</script>

<template>
  <div v-if="current" class="global-media">
    <Teleport to="body">
      <Transition name="preview">
        <div v-show="store.open" class="overlay" @click.self="onBackdropClick">
          <section class="shell" :class="[kind, { 'no-side': !playlistOpen }]" role="dialog" aria-modal="true" aria-label="媒体播放器">
            <header class="bar">
              <div class="bar-main">
                <p class="bar-name" :title="current.name">{{ current.name }}</p>
                <p class="bar-meta">
                  {{ kind === 'audio' ? '音频' : '视频' }}
                  <span v-if="kind === 'audio'">· {{ playModeLabel }}</span>
                </p>
              </div>
              <div class="bar-actions">
                <button
                  class="icon-button"
                  type="button"
                  :aria-pressed="playlistOpen"
                  aria-label="播放列表"
                  title="播放列表"
                  @click="playlistOpen = !playlistOpen"
                >
                  <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
                    <path d="M3 4h10M3 8h10M3 12h6" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
                  </svg>
                </button>
                <button class="icon-button" type="button" aria-label="下载" title="下载" @click="download">
                  <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
                    <path d="M8 2.6v7.2M5 7l3 3 3-3M3.4 12.4h9.2" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                </button>
                <button
                  class="icon-button"
                  type="button"
                  :aria-label="kind === 'video' ? '最小化视频' : '收起播放器'"
                  :title="kind === 'video' ? '最小化（暂停并保留进度）' : '收起（音频继续播放）'"
                  @click="minimize"
                >
                  <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
                    <path d="M4 8h8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
                  </svg>
                </button>
                <button class="icon-button close" type="button" aria-label="关闭并停止" title="关闭并停止" @click="store.stop()">
                  <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
                    <path d="m4 4 8 8M12 4l-8 8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
                  </svg>
                </button>
              </div>
            </header>

            <div class="body" :class="{ 'with-side': playlistOpen }">
              <div class="main">
                <div v-if="resumeAt > 10" class="resume-banner">
                  <span>上次播放到 {{ formatClock(resumeAt) }}，已为你继续。</span>
                  <button type="button" class="resume-link" @click="resumeFromStart">从头播放</button>
                </div>

                <!-- Audio stays mounted while the overlay is hidden so playback survives navigation. -->
                <AudioPlayer
                  v-if="kind === 'audio'"
                  ref="audioRef"
                  :node="current"
                  :cover="store.cover"
                  :lyrics="store.lyrics"
                  @next="playNext"
                  @prev="playPrev"
                  @ended="onAudioEnded"
                  @playing="playing = $event"
                />

                <template v-else>
                  <VideoPlayer
                    v-if="store.open"
                    ref="videoRef"
                    :node="current"
                    :subtitle-src="subtitleUrl"
                    :subtitle-label="subtitleLabel"
                    :subtitles-on="player.showSubtitles"
                    @toggle-subtitles="toggleSubtitles"
                    @ended="playNext"
                    @error="mediaError = $event"
                  />
                  <p v-if="subtitleLoading" class="note">正在加载字幕…</p>
                  <p v-else-if="subtitleNote" class="note">{{ subtitleNote }}</p>
                  <p v-if="mediaError" class="note error">{{ mediaError }}</p>
                  <VideoInfoBar :node="current" :parent-id="current.parent_id" @download="download" />
                </template>
              </div>

              <aside v-show="playlistOpen" class="side">
                <PlayerPlaylist
                  :items="store.playlist"
                  :current-id="current.id"
                  :kind="kind === 'video' ? 'video' : 'audio'"
                  @select="store.navigate($event)"
                />
              </aside>
            </div>
          </section>
        </div>
      </Transition>
    </Teleport>

    <!-- Audio mini window: floats top-right, closing it only collapses playback,
         it never stops the track (the top bar logo reopens it). -->
    <div v-if="miniVisible" class="audio-mini" data-audio-mini role="region" aria-label="音频播放器小窗">
      <div class="mini-head">
        <span class="mini-cover">
          <img v-if="store.cover" :src="store.urlFor(store.cover.id)" alt="" />
          <svg v-else width="18" height="18" viewBox="0 0 20 20" aria-hidden="true">
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
        </span>
        <span class="mini-info">
          <span class="mini-name" :title="current.name">{{ fileStem(current.name) }}</span>
          <span class="mini-meta">{{ playing ? '正在播放' : '已暂停' }} · {{ playModeLabel }}</span>
        </span>
        <button class="mini-icon" type="button" aria-label="展开播放器" title="展开" @click="reopen">
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M6 3.5 10.5 8 6 12.5" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <button class="mini-icon" type="button" aria-label="收起小窗" title="收起（继续播放）" @click="store.miniOpen = false">
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="m4 4 8 8M12 4l-8 8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
          </svg>
        </button>
      </div>

      <div class="mini-seek">
        <input
          class="mini-range"
          type="range"
          min="0"
          :max="Math.max(1, Math.round(miniDuration))"
          :value="Math.round(miniPosition)"
          aria-label="播放进度"
          @input="seekMini"
        />
        <span class="mini-time numeric">{{ formatClock(miniPosition) }} / {{ formatClock(miniDuration) }}</span>
      </div>

      <div class="mini-controls">
        <button class="mini-icon" type="button" :aria-label="playModeLabel" :title="`播放模式：${playModeLabel}`" @click="cycleMiniMode">
          <svg width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path v-if="player.mode === 'shuffle'" d="M3 4h2l6 8h2M13 12l-2-2M3 12h2l2-2.6M13 4l-2 2M9 4h4" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
            <path v-else-if="player.mode === 'one'" d="M4 12.5a4 4 0 1 1 8 0M8 5v3l2 1" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
            <path v-else-if="player.mode === 'list'" d="M3 5.5h10M3 8.5h10M3 11.5h6M12 10.5v3l2-1.5" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
            <path v-else d="M3 4.5h10M3 8h10M3 11.5h5M12 9.5v4l2-2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <button class="mini-icon" type="button" aria-label="上一首" :disabled="!canSkip" @click="playPrev">
          <svg width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M12 3.6v8.8L6 8zM4 3.6v8.8" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <button class="mini-play" type="button" :aria-label="playing ? '暂停' : '播放'" @click="audioRef?.toggle()">
          <svg v-if="playing" width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M5 3.4h2.1v9.2H5zM8.9 3.4H11v9.2H8.9z" fill="currentColor" />
          </svg>
          <svg v-else width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M5 3.4v9.2l7.4-4.6z" fill="currentColor" />
          </svg>
        </button>
        <button class="mini-icon" type="button" aria-label="下一首" :disabled="!canSkip" @click="playNext">
          <svg width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M4 3.6v8.8l6-4.4zM11.4 3.6v8.8" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <span class="mini-volume">
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M3 6.2h2.1L8.4 3.5v9L5.1 9.8H3z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" />
            <path d="M10.6 6.1a2.8 2.8 0 0 1 0 3.8" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
          </svg>
          <input
            class="mini-volume-range"
            type="range"
            min="0"
            max="100"
            :value="Math.round(volume * 100)"
            aria-label="音量"
            @input="setMiniVolume"
          />
        </span>
        <button
          class="mini-icon"
          type="button"
          aria-label="播放列表"
          title="展开 / 收起播放列表"
          :aria-expanded="miniListOpen"
          @click="toggleMiniList"
        >
          <svg width="15" height="15" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M3 4h10M3 8h10M3 12h6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />
          </svg>
        </button>
      </div>

      <div v-if="miniListOpen" class="mini-playlist" data-audio-mini-playlist>
        <button
          v-for="(track, index) in miniTracks"
          :key="track.id"
          type="button"
          class="mini-track"
          :class="{ current: track.id === current.id }"
          @click="pickMiniTrack(track)"
        >
          <span class="mini-track-index numeric">{{ miniPage * MINI_PAGE_SIZE + index + 1 }}</span>
          <span class="mini-track-name">{{ fileStem(track.name) }}</span>
          <svg v-if="track.id === current.id" width="12" height="12" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M5 3.4v9.2l7.4-4.6z" fill="currentColor" />
          </svg>
        </button>
        <div v-if="miniPages > 1" class="mini-pager">
          <button type="button" class="mini-page-btn" :disabled="miniPage === 0" @click="miniPage--">上一页</button>
          <span class="numeric">{{ miniPage + 1 }} / {{ miniPages }}</span>
          <button type="button" class="mini-page-btn" :disabled="miniPage >= miniPages - 1" @click="miniPage++">下一页</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.overlay {
  position: fixed;
  inset: 0;
  z-index: 90;
  display: grid;
  place-items: center;
  padding: 24px;
  background: rgba(12, 10, 9, 0.74);
  backdrop-filter: blur(3px);
}

.shell {
  display: flex;
  width: min(1120px, 100%);
  max-height: min(88vh, 100%);
  flex-direction: column;
  overflow: hidden;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  box-shadow: var(--shadow-pop);
}

.shell.audio {
  width: min(1000px, 100%);
}

/* 收起播放列表时收窄外壳，让主区宽度与展开时一致（避免比例被拉长）。 */
.shell.audio.no-side {
  width: min(748px, 100%);
}

.shell.no-side .body:not(.with-side) {
  grid-template-columns: minmax(0, 1fr);
}

.shell.video.no-side {
  width: min(844px, 100%);
}

.bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 10px 12px 10px 16px;
  border-bottom: 1px solid var(--color-border);
}

.bar-main {
  min-width: 0;
}

.bar-name {
  overflow: hidden;
  font-size: var(--fs-base);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bar-meta {
  margin-top: 1px;
  font-size: var(--fs-xs);
  color: var(--color-muted);
}

.bar-actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
}

.icon-button {
  display: grid;
  width: 32px;
  height: 32px;
  place-items: center;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--color-text-soft);
  cursor: pointer;
}

.icon-button:hover {
  background: var(--color-surface-soft);
  color: var(--color-text);
}

.icon-button.close:hover {
  background: rgba(249, 115, 22, 0.12);
  color: var(--color-primary-ink);
}

.body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 16px;
  min-height: 0;
  padding: 14px;
  overflow: auto;
}

.body.with-side {
  grid-template-columns: minmax(0, 1fr) 264px;
}

.shell.audio .body.with-side {
  grid-template-columns: minmax(0, 1fr) 240px;
}

.main {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 10px;
}

.side {
  min-width: 0;
}

.resume-banner {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 10px;
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
  font-size: 13px;
}

.resume-link {
  border: 0;
  background: transparent;
  color: var(--color-primary-ink);
  cursor: pointer;
  font-size: 13px;
  text-decoration: underline;
}

.note {
  margin: 0;
  color: var(--color-muted);
  font-size: 12px;
}

.note.error {
  color: var(--color-danger, #b42318);
}

/* Audio mini window: top-right floating card. Closing it only collapses. */
.audio-mini {
  position: fixed;
  top: 64px;
  right: 16px;
  z-index: 80;
  width: min(344px, calc(100vw - 32px));
  padding: 12px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 16px;
  box-shadow: var(--shadow-pop);
}

.mini-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.mini-cover {
  display: grid;
  width: 38px;
  height: 38px;
  flex-shrink: 0;
  place-items: center;
  overflow: hidden;
  border-radius: 10px;
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
}

.mini-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.mini-info {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  gap: 1px;
}

.mini-name {
  overflow: hidden;
  font-size: 13px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mini-meta {
  color: var(--color-muted);
  font-size: 11px;
}

.mini-icon {
  display: grid;
  width: 26px;
  height: 26px;
  flex-shrink: 0;
  place-items: center;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--color-text-soft);
  cursor: pointer;
}

.mini-icon:hover {
  background: var(--color-surface-soft);
  color: var(--color-text);
}

.mini-icon:disabled {
  opacity: 0.3;
  cursor: default;
}

.mini-icon:disabled:hover {
  background: transparent;
  color: var(--color-text-soft);
}

.mini-playlist {
  margin-top: 10px;
  border-top: 1px solid var(--color-border);
  padding-top: 6px;
  max-height: 264px;
  overflow-y: auto;
}

.mini-track {
  display: flex;
  width: 100%;
  align-items: center;
  gap: 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  padding: 7px 8px;
  color: var(--color-text-soft);
  font-size: 12.5px;
  text-align: left;
  cursor: pointer;
}

.mini-track:hover {
  background: var(--color-surface-soft);
  color: var(--color-text);
}

.mini-track.current {
  background: var(--color-primary-soft);
  color: var(--color-primary-ink);
  font-weight: 600;
}

.mini-track-index {
  width: 18px;
  flex-shrink: 0;
  color: var(--color-muted);
  font-size: 11px;
}

.mini-track-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mini-pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 4px 2px;
  color: var(--color-muted);
  font-size: 11.5px;
}

.mini-page-btn {
  border: 1px solid var(--color-border);
  border-radius: 6px;
  background: transparent;
  padding: 3px 8px;
  color: var(--color-text-soft);
  font-size: 11.5px;
  cursor: pointer;
}

.mini-page-btn:disabled {
  opacity: 0.35;
  cursor: default;
}

.mini-seek {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 10px 2px 4px;
}

.mini-range {
  flex: 1;
  accent-color: var(--color-primary);
}

.mini-time {
  flex-shrink: 0;
  color: var(--color-muted);
  font-size: 11px;
}

.mini-controls {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-top: 4px;
}

.mini-play {
  display: grid;
  width: 32px;
  height: 32px;
  flex-shrink: 0;
  place-items: center;
  border: 0;
  border-radius: 50%;
  background: var(--color-primary);
  color: #fff;
  cursor: pointer;
}

.mini-play:hover {
  background: var(--color-primary-ink);
}

.mini-volume {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 4px;
  margin: 0 6px;
  color: var(--color-text-soft);
}

.mini-volume-range {
  width: 100%;
  accent-color: var(--color-primary);
}

@media (max-width: 640px) {
  .audio-mini {
    top: 58px;
    right: 8px;
    width: calc(100vw - 16px);
  }
}


@media (max-width: 760px) {
  .overlay {
    padding: 0;
    place-items: stretch;
  }
  .shell.video {
    width: 100vw;
    height: 100dvh;
    max-height: none;
    border: 0;
    border-radius: 0;
  }
  .shell.video .bar {
    padding: 8px 8px 8px 12px;
  }
  .shell.video .bar-name { font-size: 14px; }
  .shell.video .body {
    flex: 1;
    min-height: 0;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr);
    gap: 0;
    padding: 0;
    overflow: hidden;
  }
  .shell.video .main {
    position: relative;
    justify-content: center;
    height: 100%;
    min-height: 0;
    gap: 0;
    background: #000;
  }
  .shell.video :deep(.video-shell) {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
  }
  .shell.video :deep(.video-stage) {
    flex: 1;
    min-height: 0;
    height: 100%;
    aspect-ratio: auto;
    border-radius: 0;
  }
  .shell.video :deep(.video-info) { display: none; }
  .shell.video .resume-banner {
    position: absolute;
    top: 8px;
    left: 8px;
    right: 8px;
    z-index: 3;
  }
  .shell.video .side {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    z-index: 4;
    max-height: 46dvh;
    overflow: auto;
    padding: 10px 10px calc(12px + env(safe-area-inset-bottom));
    background: var(--color-surface);
    border-top: 1px solid var(--color-border);
    border-radius: 16px 16px 0 0;
    box-shadow: 0 -12px 34px rgba(0, 0, 0, 0.4);
  }
  .shell.audio {
    width: 100vw;
    max-height: 100dvh;
    border-radius: 0;
    border: 0;
  }
  .shell.audio .bar { padding: 8px 8px 8px 12px; }
}

</style>
