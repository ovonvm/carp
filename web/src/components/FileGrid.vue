<script setup lang="ts">
import RowActions from './RowActions.vue'
import type { RowActionKind } from './RowActions.vue'
import FileTypeIcon from './FileTypeIcon.vue'
import { formatBytes, formatRelative } from '../lib/format'
import type { NodeItem } from '../api/client'

defineProps<{
  items: NodeItem[]
  selectedIds: ReadonlySet<number>
  /** Restrict the card action menu; omitted means every default action. */
  actions?: RowActionKind[]
  /** Folder id currently highlighted as a drag-and-drop target. */
  dropTargetId?: number | null
}>()

const emit = defineEmits<{
  /** Corner checkbox: accumulates/toggles the selection. */
  toggle: [id: number, event: MouseEvent]
  /** Card click: Explorer-style selection (Ctrl/Shift handled by the view). */
  select: [node: NodeItem, event: MouseEvent]
  action: [kind: RowActionKind, node: NodeItem]
  open: [node: NodeItem]
  /** Double click opens folders and the file preview overlay. */
  preview: [node: NodeItem]
  dragstart: [node: NodeItem, event: DragEvent]
  dragend: []
  dragover: [node: NodeItem, event: DragEvent]
  dragleave: [node: NodeItem]
  drop: [node: NodeItem, event: DragEvent]
}>()

function sizeLabel(node: NodeItem): string {
  return node.kind === 'dir' ? '—' : formatBytes(node.size)
}

/** A card click only selects (Explorer-style); opening happens on double click. */
function onCardClick(node: NodeItem, event: MouseEvent): void {
  const target = event.target instanceof Element ? event.target : null
  if (target?.closest('button, a, input, [role="menu"], [role="menuitem"]')) return
  emit('select', node, event)
}

/** Double click opens folders and previews files. */
function onCardDblClick(node: NodeItem, event: MouseEvent): void {
  const target = event.target instanceof Element ? event.target : null
  if (target?.closest('button, a, input, [role="menu"], [role="menuitem"]')) return
  if (node.kind === 'dir') {
    emit('open', node)
    return
  }
  event.preventDefault()
  emit('preview', node)
}
</script>

<template>
  <ul class="file-grid" aria-label="文件网格">
    <li
      v-for="node in items"
      :key="node.id"
      class="card"
      :class="{
        'is-selected': selectedIds.has(node.id),
        'is-dir': node.kind === 'dir',
        'is-drop-target': dropTargetId === node.id,
      }"
      :data-node-id="node.id"
      draggable="true"
      @click="onCardClick(node, $event)"
      @dblclick="onCardDblClick(node, $event)"
      @dragstart="emit('dragstart', node, $event)"
      @dragend="emit('dragend')"
      @dragover="emit('dragover', node, $event)"
      @dragleave="emit('dragleave', node)"
      @drop="emit('drop', node, $event)"
    >
      <!-- The hit target is 44x44 for touch, while the box stays 18x18. -->
      <button
        type="button"
        class="card-check-hit"
        role="checkbox"
        :aria-checked="selectedIds.has(node.id) ? 'true' : 'false'"
        :aria-label="`选择 ${node.name}`"
        @click.prevent.stop="emit('toggle', node.id, $event)"
      >
        <span class="card-check-box">
          <svg viewBox="0 0 12 12" aria-hidden="true">
            <path
              d="M2.2 6.3 4.8 8.8 9.8 3.4"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
        </span>
      </button>

      <span class="card-menu">
        <RowActions :node="node" :actions="actions" @action="(kind, target) => emit('action', kind, target)" />
      </span>

      <FileTypeIcon class="card-icon" :name="node.name" :dir="node.kind === 'dir'" />

      <span class="card-name" :title="node.name">{{ node.name }}</span>
      <span class="card-meta numeric">
        <template v-if="node.kind === 'dir'">文件夹</template>
        <template v-else>{{ sizeLabel(node) }}</template>
        · {{ formatRelative(node.updated_at) }}
      </span>
    </li>
  </ul>
</template>

<style scoped>
.file-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(var(--card-min, 172px), 1fr));
  gap: var(--card-gap, 16px);
  margin: 0;
  padding: 0;
  list-style: none;
}

.card {
  position: relative;
  user-select: none;
  -webkit-user-select: none;
  display: flex;
  min-height: clamp(168px, 11.5vw, 184px);
  flex-direction: column;
  gap: 4px;
  padding: clamp(16px, 1.35vw, 21px);
  background: var(--color-surface);
  border: 1px solid var(--color-border-soft);
  border-radius: 14px;
  box-shadow: var(--shadow-card);
  transition:
    background-color var(--dur) var(--ease),
    border-color var(--dur) var(--ease),
    box-shadow var(--dur) var(--ease),
    transform var(--dur) var(--ease);
}

.card:hover {
  background: #fff;
  border-color: var(--color-border);
  box-shadow: var(--shadow-card-hover);
}

.card.is-drop-target {
  border-color: var(--color-primary);
  outline: 2px dashed var(--color-primary);
  outline-offset: -2px;
}

.card.is-selected {
  background: var(--color-primary-soft);
  border: 1.5px solid var(--color-primary);
  box-shadow: var(--shadow-card), 0 0 0 2px color-mix(in srgb, var(--color-primary) 22%, transparent);
}

.card.is-dir {
  cursor: pointer;
}

/* 44x44 touch target; the visible box stays 22x22 in the card corner. */
.card-check-hit {
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 2;
  display: grid;
  width: 44px;
  height: 44px;
  margin: 0;
  padding: 0;
  place-items: center;
  border: 0;
  border-radius: 50%;
  background: none;
  cursor: pointer;
  opacity: 0;
  user-select: none;
  transition: opacity var(--dur) var(--ease);
}

.card-check-box {
  display: inline-flex;
  width: 22px;
  height: 22px;
  align-items: center;
  justify-content: center;
  border: 1.5px solid var(--color-border-strong);
  border-radius: 6px;
  background: #fff;
  color: transparent;
  transition:
    background-color var(--dur) var(--ease),
    border-color var(--dur) var(--ease),
    color var(--dur) var(--ease);
}

.card-check-hit:hover .card-check-box {
  border-color: var(--color-primary);
}

.card-check-box svg {
  width: 12px;
  height: 12px;
  opacity: 0;
  transform: scale(0.6);
  transition:
    opacity var(--dur) var(--ease),
    transform var(--dur) var(--ease);
}

.card:hover .card-check-hit,
.card-check-hit:focus-visible,
.card-check-hit[aria-checked='true'] {
  opacity: 1;
}

.card-check-hit:focus-visible {
  outline: 2px solid var(--color-primary);
  outline-offset: -2px;
}

.card-check-hit[aria-checked='true'] .card-check-box {
  border-color: var(--color-primary);
  background: var(--color-primary);
  color: #fff;
}

.card-check-hit[aria-checked='true'] .card-check-box svg {
  opacity: 1;
  transform: scale(1);
}

.card-menu {
  position: absolute;
  top: 10px;
  right: 10px;
  z-index: 2;
  opacity: 0;
  transition: opacity var(--dur) var(--ease);
}

.card:hover .card-menu,
.card-menu:focus-within {
  opacity: 1;
}

.card-menu :deep(.row-menu-button) {
  width: 26px;
  height: 26px;
}

.card-icon {
  margin-top: clamp(26px, 1.9vw, 30px);
  margin-bottom: 8px;
}

.card-name {
  display: -webkit-box;
  overflow: hidden;
  font-size: 13.5px;
  font-weight: 600;
  line-height: 1.45;
  overflow-wrap: anywhere;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}

.card-meta {
  margin-top: auto;
  font-size: 12px;
  color: var(--color-muted);
}

/* Folder names are the primary way into a directory, so they are real buttons. */
.card-name-button {
  width: 100%;
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.card-name-button:hover {
  color: var(--color-primary-ink);
}

@media (max-width: 899px) {
  .file-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 599px) {
  .file-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 12px;
  }
}

@media (max-width: 700px) {
  .card {
    min-height: 140px;
    padding: 12px;
  }

  /* Touch layouts have no hover, so keep the controls visible. */
  .card-check-hit,
  .card-menu {
    opacity: 1;
  }
}
</style>
