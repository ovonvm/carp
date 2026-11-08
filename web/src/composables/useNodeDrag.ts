import { ref, type Ref } from 'vue'
import type { NodeItem } from '../api/client'

/**
 * Private MIME type for dragging nodes inside the app. OS file drops carry
 * "Files" instead, so the upload handler and the move handler never mix.
 */
export const NODE_DRAG_MIME = 'application/x-carp-nodes'

export interface DragMoveFailure {
  node: NodeItem
  cause: unknown
}

export interface NodeDragOptions {
  /** Items currently listed in the directory. */
  items: () => NodeItem[]
  selectedIds: Ref<number[]>
  /** Move permission for this surface (read-only group spaces stay inert). */
  canMove: () => boolean
  /** Plain-select one node (used when dragging an unselected row). */
  selectOnly: (node: NodeItem) => void
  /** Moves one node under `targetParent` (0 = root). */
  move: (node: NodeItem, targetParent: number) => Promise<unknown>
  /** Lists the names already present in the target folder (paged). */
  listNames?: (parent: number) => Promise<string[]>
  /** Called instead of moving when a dragged name already exists there. */
  onConflict?: (name: string) => void
  /** Called once after a drop with the outcome; views refresh and toast here. */
  onMoved: (moved: number, failures: DragMoveFailure[]) => void | Promise<void>
}

function carriesNodes(event: DragEvent): boolean {
  const types = event.dataTransfer?.types
  return !!types && Array.from(types).includes(NODE_DRAG_MIME)
}

/**
 * Explorer-style internal drag and drop: drag rows/cards onto a folder row,
 * a folder card or a breadcrumb segment to move them. Multi-selection drags as
 * a batch; moves run sequentially so conflicts are reported per item.
 */
export function useNodeDrag(options: NodeDragOptions) {
  const dragging = ref<NodeItem[]>([])
  /** Id of the folder currently highlighted as a drop target. */
  const overId = ref<number | null>(null)
  /** Id of the breadcrumb segment currently highlighted. */
  const overCrumbId = ref<number | null>(null)

  function selectionFor(node: NodeItem): NodeItem[] {
    const selected = new Set(options.selectedIds.value)
    if (selected.has(node.id)) {
      return options.items().filter((item) => selected.has(item.id))
    }
    options.selectOnly(node)
    return [node]
  }

  function canDropOn(target: NodeItem): boolean {
    if (!options.canMove() || dragging.value.length === 0) return false
    if (target.kind !== 'dir') return false
    if (dragging.value.some((item) => item.id === target.id)) return false
    // Dropping where the items already live is a no-op.
    if (dragging.value.every((item) => item.parent_id === target.id)) return false
    return true
  }

  function onDragStart(node: NodeItem, event: DragEvent): void {
    if (!options.canMove()) {
      event.preventDefault()
      return
    }
    dragging.value = selectionFor(node)
    overId.value = null
    const transfer = event.dataTransfer
    if (!transfer) return
    transfer.effectAllowed = 'move'
    transfer.setData(NODE_DRAG_MIME, JSON.stringify(dragging.value.map((item) => item.id)))
    transfer.setData('text/plain', dragging.value.map((item) => item.name).join('\n'))
  }

  function onDragEnd(): void {
    dragging.value = []
    overId.value = null
    overCrumbId.value = null
  }

  /** Row/card hover: only internal node drags are accepted. */
  function onDragOver(target: NodeItem | null, event: DragEvent): void {
    if (!carriesNodes(event)) return
    if (!target || !canDropOn(target)) {
      if (overId.value !== null) overId.value = null
      return
    }
    event.preventDefault()
    // Keep the page-level upload handler from seeing this drag.
    event.stopPropagation()
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
    overId.value = target.id
  }

  function onDragLeave(target: NodeItem): void {
    if (overId.value === target.id) overId.value = null
  }

  async function moveTo(parent: number): Promise<void> {
    const targets = dragging.value.filter((node) => node.parent_id !== parent)
    dragging.value = []
    overId.value = null
    overCrumbId.value = null
    if (targets.length === 0) return

    // A name clash cancels the whole drop; the user asked for "move or nothing",
    // so nothing is moved and the view explains why.
    if (options.listNames) {
      const names = new Set((await options.listNames(parent)).map((name) => name.toLowerCase()))
      const clash = targets.find((node) => names.has(node.name.toLowerCase()))
      if (clash) {
        options.onConflict?.(clash.name)
        return
      }
    }

    let moved = 0
    let conflict: string | null = null
    const failures: DragMoveFailure[] = []
    for (const node of targets) {
      try {
        await options.move(node, parent)
        moved += 1
      } catch (cause) {
        failures.push({ node, cause })
        // A race that slipped past the preflight still cancels the rest.
        if ((cause as { code?: string })?.code === 'name_conflict') {
          conflict = node.name
          break
        }
      }
    }
    if (conflict) options.onConflict?.(conflict)
    await options.onMoved(moved, failures)
  }

  async function onDrop(target: NodeItem, event: DragEvent): Promise<void> {
    if (!carriesNodes(event) || !canDropOn(target)) return
    event.preventDefault()
    event.stopPropagation()
    await moveTo(target.id)
  }

  /** Breadcrumb segments accept a drop to move items one or more levels up. */
  function onCrumbDragOver(parent: number, event: DragEvent): void {
    if (!carriesNodes(event) || !options.canMove() || dragging.value.length === 0) return
    if (dragging.value.every((item) => item.parent_id === parent)) return
    event.preventDefault()
    event.stopPropagation()
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
    overCrumbId.value = parent
  }

  function onCrumbDragLeave(parent: number): void {
    if (overCrumbId.value === parent) overCrumbId.value = null
  }

  async function onCrumbDrop(parent: number, event: DragEvent): Promise<void> {
    if (!carriesNodes(event)) return
    event.preventDefault()
    event.stopPropagation()
    await moveTo(parent)
  }

  return {
    dragging,
    overId,
    overCrumbId,
    onDragStart,
    onDragEnd,
    onDragOver,
    onDragLeave,
    onDrop,
    onCrumbDragOver,
    onCrumbDragLeave,
    onCrumbDrop,
  }
}
