import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { downloadUrl, publicSharePreviewUrl, publicShareDownloadUrl, type NodeItem } from '../api/client'
import { findSidecar, mediaKind, mediaSiblings, type MediaKind } from '../lib/media'

/**
 * Global media player state.
 *
 * The overlay is mounted once by AppShell, so audio keeps playing while the
 * user navigates the rest of the site (the lesson from Carp: a page-local
 * overlay died on every route change). Everything is cleared on logout and on
 * account switch so one user can never hear another user's track.
 */
export const useMediaStore = defineStore('media', () => {
  const open = ref(false)
  /** The audio mini window (top-right card); full overlay takes over when open. */
  const miniOpen = ref(true)
  /** Video is minimized to a single top-bar icon; playback is paused while collapsed. */
  const videoMini = ref(false)
  const node = ref<NodeItem | null>(null)
  /** When set, players read content through the public share preview endpoint. */
  const shareToken = ref<string | null>(null)
  /** All files of the directory the current track came from (playlist + sidecars). */
  const dirFiles = ref<NodeItem[]>([])

  const kind = computed<MediaKind | null>(() => (node.value ? mediaKind(node.value.name) : null))
  const playlist = computed(() => {
    if (!node.value || !kind.value) return []
    const list = mediaSiblings(dirFiles.value, kind.value)
    if (!list.some((item) => item.id === node.value?.id)) list.unshift(node.value)
    return list
  })
  const cover = computed(() => (node.value && kind.value === 'audio' ? findSidecar(dirFiles.value, node.value, 'cover') : null))
  const lyrics = computed(() => (node.value && kind.value === 'audio' ? findSidecar(dirFiles.value, node.value, 'lyrics') : null))
  const subtitle = computed(() => (node.value && kind.value === 'video' ? findSidecar(dirFiles.value, node.value, 'subtitles') : null))

  /** Bind playback to a public share token (null restores personal URLs). */
  function setShareToken(token: string | null): void {
    shareToken.value = token
  }

  /** Media/preview URL: inline and free of the share download budget. */
  function urlFor(id: number): string {
    return shareToken.value ? publicSharePreviewUrl(shareToken.value, id) : downloadUrl(id)
  }

  /** Real download URL (consumes a share download when the budget allows). */
  function downloadURL(id: number): string {
    return shareToken.value ? publicShareDownloadUrl(shareToken.value, id) : downloadUrl(id)
  }

  function openWith(target: NodeItem, files: NodeItem[]): void {
    if (!mediaKind(target.name)) return
    node.value = target
    dirFiles.value = files
    // Audio opens in the top-right mini window; only video takes over the screen.
    open.value = kind.value === 'video'
    miniOpen.value = true
    videoMini.value = false
  }

  /** Hide the overlay; audio keeps playing (use stop() to end playback). */
  function hide(): void {
    open.value = false
  }

  /** Pause a video and collapse it to the top-bar icon (the position is kept). */
  function minimizeVideo(): void {
    if (!node.value || kind.value !== 'video') return
    open.value = false
    videoMini.value = true
  }

  /** Reopen a minimized video overlay; VideoPlayer resumes from the saved position. */
  function restoreVideo(): void {
    if (!node.value || kind.value !== 'video') return
    videoMini.value = false
    open.value = true
  }

  /** Collapse/expand the audio mini window (playback is never affected). */
  function toggleMini(): void {
    if (!node.value || kind.value !== 'audio') return
    open.value = false
    miniOpen.value = !miniOpen.value
  }

  /** Navigate inside the current queue without unloading the audio element. */
  function navigate(target: NodeItem | number, opts: { open?: boolean } = {}): void {
    const item = typeof target === 'number' ? playlist.value.find((entry) => entry.id === target) : target
    if (!item) return
    node.value = item
    if (opts.open !== undefined) {
      open.value = opts.open
    }
    miniOpen.value = true
    videoMini.value = false
  }

  /** Fully stop: clear state so the next user cannot inherit a track. */
  function stop(): void {
    open.value = false
    miniOpen.value = true
    videoMini.value = false
    node.value = null
    dirFiles.value = []
  }

  return {
    open,
    miniOpen,
    videoMini,
    node,
    shareToken,
    dirFiles,
    kind,
    playlist,
    cover,
    lyrics,
    subtitle,
    setShareToken,
    urlFor,
    downloadURL,
    openWith,
    hide,
    minimizeVideo,
    restoreVideo,
    toggleMini,
    navigate,
    stop,
  }
})
