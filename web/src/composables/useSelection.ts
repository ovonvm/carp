import { computed, ref, type Ref } from 'vue'

/** Anything that can be selected by its integer id. */
export interface Selectable {
  id: number
}

export interface SelectionOptions {
  /** Items in display order; the selection is always normalised to this order. */
  items: Ref<Selectable[]> | (() => Selectable[])
  /** Selected ids, replaced as a whole. */
  selectedIds: Ref<number[]>
}

/**
 * Explorer-style selection shared by every file surface.
 *
 * - A plain body click replaces the selection with the clicked item.
 * - Ctrl/Cmd+click toggles one item; Shift+click replaces with the range from
 *   the anchor (Ctrl+Shift unions the range); double click opens/preview.
 * - A checkbox click always toggles, so touch users need no modifier.
 * - The anchor only moves on plain/ctrl/checkbox clicks, exactly like Explorer.
 */
export function useSelection(options: SelectionOptions) {
  const items = () =>
    typeof options.items === 'function' ? options.items() : options.items.value
  const selectedIds = options.selectedIds
  const anchorIndex = ref<number | null>(null)

  const selectedSet = computed(() => new Set(selectedIds.value))

  function indexOf(id: number): number {
    return items().findIndex((node) => node.id === id)
  }

  /** Keep only ids that still exist, in list order. */
  function setSelection(ids: Iterable<number>): void {
    const wanted = new Set(ids)
    selectedIds.value = items()
      .filter((node) => wanted.has(node.id))
      .map((node) => node.id)
  }

  function clear(): void {
    selectedIds.value = []
    anchorIndex.value = null
  }

  function selectAll(): void {
    selectedIds.value = items().map((node) => node.id)
    anchorIndex.value = items().length > 0 ? 0 : null
  }

  /** Inclusive index range between two rows. */
  function range(from: number, to: number): number[] {
    const start = Math.min(from, to)
    const end = Math.max(from, to)
    return items()
      .slice(start, end + 1)
      .map((node) => node.id)
  }

  /** Body click: plain replaces, Ctrl/Cmd toggles, Shift extends from the anchor. */
  function click(node: Selectable, event: MouseEvent): void {
    const index = indexOf(node.id)
    if (index < 0) return
    const additive = event.ctrlKey || event.metaKey

    if (event.shiftKey && anchorIndex.value !== null) {
      const ids = range(anchorIndex.value, index)
      setSelection(additive ? [...selectedIds.value, ...ids] : ids)
      return
    }

    if (additive) {
      const next = new Set(selectedIds.value)
      if (next.has(node.id)) next.delete(node.id)
      else next.add(node.id)
      setSelection(next)
    } else {
      setSelection([node.id])
    }
    anchorIndex.value = index
  }

  /** Checkbox click: always toggles; Shift still extends from the anchor. */
  function toggle(id: number, event: MouseEvent): void {
    const index = indexOf(id)
    if (index < 0) return
    if (event.shiftKey && anchorIndex.value !== null) {
      setSelection(range(anchorIndex.value, index))
      return
    }
    const next = new Set(selectedIds.value)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setSelection(next)
    anchorIndex.value = index
  }

  /** Marquee result: normalise and forget the anchor (no click point). */
  function apply(ids: readonly number[]): void {
    setSelection(ids)
    anchorIndex.value = null
  }

  return { anchorIndex, selectedSet, clear, selectAll, click, toggle, apply, setSelection, indexOf }
}
