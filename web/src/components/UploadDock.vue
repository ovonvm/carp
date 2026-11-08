<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { formatBytes } from '../lib/format'
import { playFx } from '../lib/fx'
import { useUploadsStore } from '../stores/uploads'
import type { UploadTask, UploadStatus } from '../stores/uploads'

const uploads = useUploadsStore()

/** Collapsed (capsule) by default; the panel is global so page switches keep transfers alive. */
const expanded = ref(false)
const speedById = ref<Record<number, number>>({})
const totalSpeed = ref(0)

const resumeInput = ref<HTMLInputElement | null>(null)
const resumeTaskId = ref<number | null>(null)

const STATUS_LABELS: Record<UploadStatus, string> = {
  pending: '待续传',
  queued: '排队中',
  hashing: '准备中…',
  uploading: '上传中',
  completing: '合并中…',
  done: '已完成',
  deduped: '已秒传',
  canceled: '已取消',
  error: '上传失败',
}

watch(
  () => uploads.activeCount,
  (now, before) => {
    if (now === 0 && before > 0) {
      playFx('upload', { x: window.innerWidth - 140, y: window.innerHeight - 120 })
    }
  },
)

const overallPercent = computed(() => {
  const live = uploads.tasks.filter((task) => task.status !== 'done' && task.status !== 'deduped' && task.status !== 'canceled')
  if (live.length === 0) return 100
  const total = live.reduce((sum, task) => sum + task.size, 0)
  const received = live.reduce((sum, task) => sum + task.receivedBytes, 0)
  return total > 0 ? Math.min(99, Math.round((received / total) * 100)) : 0
})

const headline = computed(() => {
  if (uploads.activeCount > 0) return `${uploads.activeCount} 个上传中`
  if (uploads.pendingCount > 0) return `${uploads.pendingCount} 个待续传`
  return '上传完成'
})

function statusText(task: UploadTask): string {
  if (task.status === 'uploading') return `${task.percent}%`
  if (task.status === 'error') return task.error ?? STATUS_LABELS.error
  return STATUS_LABELS[task.status]
}

function taskSpeed(task: UploadTask): string {
  if (task.status !== 'uploading') return ''
  return `${formatBytes(speedById.value[task.id] ?? 0)}/s`
}

function overallSpeedText(): string {
  return totalSpeed.value > 0 ? `${formatBytes(totalSpeed.value)}/s` : ''
}

function canCancel(task: UploadTask): boolean {
  return ['pending', 'queued', 'hashing', 'uploading', 'completing'].includes(task.status)
}

function canRetry(task: UploadTask): boolean {
  return task.status === 'error' || task.status === 'canceled'
}

function requestResume(task: UploadTask): void {
  resumeTaskId.value = task.id
  resumeInput.value?.click()
}

async function onResumePicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0] ?? null
  const id = resumeTaskId.value
  input.value = ''
  resumeTaskId.value = null
  if (!file || id === null) return
  await uploads.resumeWithFile(id, file)
}

let timer = 0
let previous = new Map<number, { bytes: number; at: number }>()

function sample(): void {
  const now = performance.now()
  const next = new Map<number, { bytes: number; at: number }>()
  const speeds: Record<number, number> = {}
  let total = 0

  for (const task of uploads.tasks) {
    if (task.status !== 'uploading') {
      speeds[task.id] = 0
      continue
    }
    const before = previous.get(task.id)
    let bps = 0
    if (before && now > before.at) {
      bps = ((task.receivedBytes - before.bytes) * 1000) / (now - before.at)
    }
    if (!Number.isFinite(bps) || bps < 0) bps = 0
    speeds[task.id] = bps
    total += bps
    next.set(task.id, { bytes: task.receivedBytes, at: now })
  }

  previous = next
  speedById.value = speeds
  totalSpeed.value = total
}

onMounted(() => {
  timer = window.setInterval(sample, 500)
})

onBeforeUnmount(() => {
  window.clearInterval(timer)
})
</script>

<template>
  <section v-if="uploads.hasTasks" class="dock" :class="{ 'is-expanded': expanded }" aria-label="上传队列">
    <input ref="resumeInput" class="dock-hidden-input" type="file" @change="onResumePicked" />

    <button
      v-if="!expanded"
      class="capsule"
      type="button"
      aria-label="展开上传队列"
      @click="expanded = true"
    >
      <span class="capsule-ring" :style="{ '--p': overallPercent + '%' }" aria-hidden="true">
        <span class="capsule-ring-text numeric">{{ overallPercent }}</span>
      </span>
      <span class="capsule-main">
        <span class="capsule-title">{{ headline }}</span>
        <span class="capsule-meta numeric">{{ overallSpeedText() || `${uploads.tasks.length} 个文件` }}</span>
      </span>
      <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
        <path d="M4 10.5 8 6.5l4 4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>

    <div v-else class="panel">
      <header class="panel-head">
        <span class="panel-title">上传队列</span>
        <span class="panel-speed numeric">{{ overallSpeedText() }}</span>
        <button v-if="uploads.hasFinished" class="panel-link" type="button" @click="uploads.clearFinished()">清除已结束</button>
        <button class="panel-icon" type="button" aria-label="收起上传队列" title="收起（不中断上传）" @click="expanded = false">
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M4 8h8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
          </svg>
        </button>
      </header>

      <ul class="panel-list">
        <li v-for="task in uploads.tasks" :key="task.id" class="task" :class="`is-${task.status}`">
          <div class="task-top">
            <span class="task-name" :title="task.label">{{ task.label }}</span>
            <span class="task-speed numeric">{{ taskSpeed(task) }}</span>
          </div>
          <div class="task-bar"><span :style="{ width: `${task.percent}%` }" /></div>
          <div class="task-bottom">
            <span class="task-status">{{ statusText(task) }} · {{ formatBytes(task.size) }}</span>
            <span class="task-actions">
              <button v-if="task.status === 'pending'" class="task-btn" type="button" @click="requestResume(task)">选择文件续传</button>
              <button v-if="canRetry(task)" class="task-btn" type="button" @click="uploads.retry(task.id)">重试</button>
              <button v-if="canCancel(task)" class="task-btn danger" type="button" @click="uploads.cancel(task.id)">取消</button>
            </span>
          </div>
        </li>
      </ul>

      <p class="panel-note">切页、关闭此面板都不会中断上传；刷新后可从「我的文件」重新选择文件续传。</p>
    </div>
  </section>
</template>

<style scoped>
.dock {
  position: fixed;
  right: 18px;
  bottom: 18px;
  z-index: 75;
  font-size: 13px;
}

.dock-hidden-input {
  display: none;
}

.capsule {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 12px 9px 10px;
  border: 1px solid var(--color-border);
  border-radius: 999px;
  background: var(--color-surface);
  box-shadow: var(--shadow-pop);
  color: var(--color-text);
  cursor: pointer;
}

.capsule-ring {
  display: grid;
  width: 30px;
  height: 30px;
  place-items: center;
  border-radius: 50%;
  background:
    radial-gradient(closest-side, var(--color-surface) 72%, transparent 73% 100%),
    conic-gradient(var(--color-primary) var(--p, 0%), var(--color-surface-soft) 0);
}

.capsule-ring-text {
  font-size: 10px;
  color: var(--color-text-soft);
}

.capsule-main {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  line-height: 1.25;
}

.capsule-title {
  font-weight: 600;
}

.capsule-meta {
  color: var(--color-muted);
  font-size: 12px;
}

.panel {
  display: flex;
  width: min(380px, calc(100vw - 36px));
  max-height: min(52vh, 460px);
  flex-direction: column;
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  background: var(--color-surface);
  box-shadow: var(--shadow-pop);
  overflow: hidden;
}

.panel-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--color-border);
}

.panel-title {
  font-weight: 600;
}

.panel-speed {
  margin-left: auto;
  color: var(--color-primary-ink);
  font-size: 12px;
}

.panel-link {
  border: 0;
  background: transparent;
  color: var(--color-muted);
  font-size: 12px;
  cursor: pointer;
}

.panel-link:hover {
  color: var(--color-primary-ink);
}

.panel-icon {
  display: grid;
  width: 24px;
  height: 24px;
  place-items: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--color-text-soft);
  cursor: pointer;
}

.panel-icon:hover {
  background: var(--color-surface-soft);
}

.panel-list {
  margin: 0;
  padding: 4px 0;
  overflow: auto;
  list-style: none;
}

.task {
  padding: 8px 12px;
}

.task + .task {
  border-top: 1px dashed var(--color-border);
}

.task-top {
  display: flex;
  align-items: center;
  gap: 8px;
}

.task-name {
  overflow: hidden;
  flex: 1;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.task-speed {
  color: var(--color-primary-ink);
  font-size: 12px;
}

.task-bar {
  height: 4px;
  margin: 6px 0 4px;
  border-radius: 999px;
  background: var(--color-surface-soft);
  overflow: hidden;
}

.task-bar span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: var(--color-primary);
  transition: width 0.25s ease;
}

.task.is-done .task-bar span,
.task.is-deduped .task-bar span {
  background: var(--color-success);
}

.task.is-error .task-bar span {
  background: var(--color-danger, #b42318);
}

.task-bottom {
  display: flex;
  align-items: center;
  gap: 8px;
}

.task-status {
  overflow: hidden;
  flex: 1;
  color: var(--color-muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.task-actions {
  display: flex;
  gap: 6px;
}

.task-btn {
  border: 0;
  background: transparent;
  color: var(--color-primary-ink);
  font-size: 12px;
  cursor: pointer;
}

.task-btn:hover {
  text-decoration: underline;
}

.task-btn.danger {
  color: var(--color-danger, #b42318);
}

.panel-note {
  margin: 0;
  padding: 8px 12px;
  border-top: 1px solid var(--color-border);
  color: var(--color-muted);
  font-size: 11px;
  line-height: 1.4;
}

@media (max-width: 640px) {
  .dock {
    right: 10px;
    bottom: 10px;
  }

  .panel {
    max-height: 46vh;
  }
}
</style>
