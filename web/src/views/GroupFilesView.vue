<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import EmptyState from '../components/EmptyState.vue'
import FileGrid from '../components/FileGrid.vue'
import FileList from '../components/FileList.vue'
import FolderDialog from '../components/FolderDialog.vue'
import MoveDialog from '../components/MoveDialog.vue'
import PreviewOverlay from '../components/PreviewOverlay.vue'
import RenameDialog from '../components/RenameDialog.vue'
import ConfirmDialog from '../components/ConfirmDialog.vue'
import type { RowActionKind } from '../components/RowActions.vue'
import { copyNode, createNode, deleteNode, listNodes, updateNode } from '../api/client'
import type { GroupPermission, NodeItem } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { usePreferencesStore } from '../stores/preferences'
import { useGroupsStore } from '../stores/groups'
import { useUploadsStore } from '../stores/uploads'
import { useToast } from '../composables/useToast'
import { useSelection } from '../composables/useSelection'
import { useNodeDrag, type DragMoveFailure } from '../composables/useNodeDrag'
import { errorCodeOf } from '../lib/errors'
import { canGroupAction, groupErrorMessage } from '../lib/groups'

const PAGE_SIZE = 200
const VIEW_STORAGE_KEY = 'carp.viewMode.v2'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const groups = useGroupsStore()
const uploads = useUploadsStore()
const toast = useToast()

const groupId = computed(() => {
  const raw = route.params.id
  const value = Number(Array.isArray(raw) ? raw[0] : raw)
  return Number.isInteger(value) && value > 0 ? value : 0
})

const group = computed(() => groups.byId(groupId.value))
const rootId = computed(() => group.value?.root_id ?? 0)
const role = computed(() => group.value?.role ?? 'member')
const perms = computed<GroupPermission[]>(() => group.value?.member_perms ?? ['read'])
const canCreate = computed(() => canGroupAction(role.value, perms.value, 'create'))
const canUpload = computed(() => canGroupAction(role.value, perms.value, 'upload'))
const canWrite = computed(() => canGroupAction(role.value, perms.value, 'write'))
const canDelete = computed(() => canGroupAction(role.value, perms.value, 'delete'))

const items = ref<NodeItem[]>([])
const total = ref(0)
const loading = ref(false)
const error = ref<string | null>(null)
const loaded = ref(false)
const notFound = ref(false)

const crumbs = ref<{ id: number; name: string }[]>([])
const parentId = computed(() => crumbs.value[crumbs.value.length - 1]?.id ?? rootId.value)
const isRoot = computed(() => crumbs.value.length <= 1)

const selectedIds = ref<number[]>([])
const selection = useSelection({ items: () => items.value, selectedIds })
const selectedSet = selection.selectedSet
const selectedItems = computed(() => items.value.filter((node) => selectedSet.value.has(node.id)))

/** Every name directly under `parent`, used to abort a drop on a name clash. */
async function listFolderNames(parent: number): Promise<string[]> {
  const names: string[] = []
  for (let offset = 0; ; offset += 200) {
    const page = await listNodes({ parent, limit: 200, offset })
    names.push(...page.items.map((node) => node.name))
    if (page.items.length === 0 || offset + page.items.length >= page.total) break
  }
  return names
}

async function afterDragMove(moved: number, failures: DragMoveFailure[]): Promise<void> {
  if (moved > 0) await load()
  if (failures.length === 0) {
    if (moved > 0) toast.push(`已移动 ${moved} 项`)
    return
  }
  const conflict = failures.some((item) => (item.cause as { code?: string })?.code === 'name_conflict')
  toast.push(
    conflict
      ? '目标目录已有同名文件，本次移动已取消'
      : groupErrorMessage(failures[0]!.cause, '移动失败，请稍后重试'),
    'error',
  )
}

const nodeDrag = useNodeDrag({
  items: () => items.value,
  selectedIds,
  canMove: () => canWrite.value,
  selectOnly: (node) => selection.setSelection([node.id]),
  move: (node, parent) => updateNode(node.id, { parent, revision: node.revision }).then(() => undefined),
  listNames: listFolderNames,
  onConflict: (name) => toast.push(`「${name}」已存在，本次移动已取消`, 'error'),
  onMoved: afterDragMove,
})

const dropTargetId = computed(() => nodeDrag.overId.value)
const crumbOverId = computed(() => nodeDrag.overCrumbId.value)

function onNodeDragStart(node: NodeItem, event: DragEvent): void {
  nodeDrag.onDragStart(node, event)
}

function onNodeDragEnd(): void {
  nodeDrag.onDragEnd()
}

function onNodeDragOver(node: NodeItem, event: DragEvent): void {
  nodeDrag.onDragOver(node, event)
}

function onNodeDragLeave(node: NodeItem): void {
  nodeDrag.onDragLeave(node)
}

async function onNodeDrop(node: NodeItem, event: DragEvent): Promise<void> {
  await nodeDrag.onDrop(node, event)
}

function onCrumbDragOver(parent: number, event: DragEvent): void {
  nodeDrag.onCrumbDragOver(parent, event)
}

function onCrumbDragLeave(parent: number): void {
  nodeDrag.onCrumbDragLeave(parent)
}

async function onCrumbDrop(parent: number, event: DragEvent): Promise<void> {
  await nodeDrag.onCrumbDrop(parent, event)
}

const viewMode = ref<'list' | 'grid'>('grid')
const dialogOpen = ref(false)
const renameOpen = ref(false)
const renameTarget = ref<NodeItem | null>(null)
const moveOpen = ref(false)
const moveTargets = ref<NodeItem[]>([])
const moveBusy = ref(false)
const moveError = ref<string | null>(null)
const deleteOpen = ref(false)
const deleteTargets = ref<NodeItem[]>([])
const deleteBusy = ref(false)
const deleteError = ref<string | null>(null)
const previewOpen = ref(false)
const previewId = ref<number | null>(null)
const dragDepth = ref(0)
const fileInput = ref<HTMLInputElement | null>(null)

const previewFiles = computed(() => items.value.filter((node) => node.kind === 'file'))
const previewNode = computed(() => previewFiles.value.find((node) => node.id === previewId.value) ?? null)
const hasSelection = computed(() => selectedIds.value.length > 0)
const allSelected = computed(() => items.value.length > 0 && selectedIds.value.length === items.value.length)

const rowActions = computed<RowActionKind[]>(() => {
  const actions: RowActionKind[] = ['preview']
  if (canWrite.value) actions.push('rename', 'move')
  if (canCreate.value) actions.push('copy')
  if (canDelete.value) actions.push('trash')
  return actions
})

const isDragging = computed(() => dragDepth.value > 0)

function readViewMode(): 'list' | 'grid' {
  try {
    return localStorage.getItem(VIEW_STORAGE_KEY) === 'list' ? 'list' : 'grid'
  } catch {
    return 'grid'
  }
}

function setViewMode(mode: 'list' | 'grid'): void {
  viewMode.value = mode
  try {
    localStorage.setItem(VIEW_STORAGE_KEY, mode)
  } catch {
    // storage may be disabled; the mode still works for this session.
  }
  usePreferencesStore().save({ view_mode: mode }).catch(() => {})
}

function clearSelection(): void {
  selection.clear()
}

function toggleNode(id: number, event: MouseEvent): void {
  selection.toggle(id, event)
}

function selectNode(node: NodeItem, event: MouseEvent): void {
  selection.click(node, event)
}

async function load(): Promise<void> {
  const id = parentId.value
  if (!id) return
  loading.value = true
  error.value = null
  notFound.value = false

  try {
    const page = await listNodes({ parent: id, limit: PAGE_SIZE, offset: 0 })
    items.value = page.items
    total.value = page.total
    loaded.value = true
    selectedIds.value = selectedIds.value.filter((value) => page.items.some((node) => node.id === value))
  } catch (cause) {
    items.value = []
    if (errorCodeOf(cause) === 'not_found') {
      notFound.value = true
      error.value = '组不存在，或你没有访问权限'
    } else {
      error.value = groupErrorMessage(cause, '组空间加载失败，请稍后重试')
    }
  } finally {
    loading.value = false
  }
}

function openFolder(node: NodeItem): void {
  if (node.kind !== 'dir') return
  crumbs.value = [...crumbs.value, { id: node.id, name: node.name }]
  clearSelection()
  void load()
}

function goCrumb(index: number): void {
  if (index < 0 || index >= crumbs.value.length - 1) return
  crumbs.value = crumbs.value.slice(0, index + 1)
  clearSelection()
  void load()
}

function goBack(): void {
  goCrumb(crumbs.value.length - 2)
}

async function createFolder(name: string): Promise<void> {
  try {
    await createNode({ parent: parentId.value, name, kind: 'dir' })
    toast.push(`已新建文件夹「${name}」`)
    await load()
  } catch (cause) {
    toast.push(groupErrorMessage(cause, '新建文件夹失败，请稍后重试'), 'error')
    throw cause
  }
}

function openRename(node: NodeItem): void {
  renameTarget.value = node
  renameOpen.value = true
}

async function renameNode(node: NodeItem, name: string): Promise<NodeItem> {
  const updated = await updateNode(node.id, { name, revision: node.revision })
  toast.push('改名成功')
  await load()
  return updated
}

function openMove(nodes: NodeItem[]): void {
  moveTargets.value = nodes
  moveError.value = null
  moveOpen.value = true
}

async function onMoveTo(target: number): Promise<void> {
  const targets = moveTargets.value
  if (targets.length === 0) return
  moveBusy.value = true
  moveError.value = null

  try {
    for (const node of targets) {
      await updateNode(node.id, { parent: target, revision: node.revision })
    }
    moveOpen.value = false
    clearSelection()
    toast.push(targets.length === 1 ? '已移动' : `已移动 ${targets.length} 项`)
    await load()
  } catch (cause) {
    moveError.value = groupErrorMessage(cause, '移动失败，请稍后重试')
  } finally {
    moveBusy.value = false
  }
}

function openDelete(nodes: NodeItem[]): void {
  if (nodes.length === 0) return
  deleteTargets.value = nodes
  deleteError.value = null
  deleteOpen.value = true
}

const deleteTitle = computed(() => '永久删除')
const deleteMessage = computed(() => {
  const targets = deleteTargets.value
  if (targets.length === 1) return `「${targets[0]?.name ?? ''}」将被永久删除。`
  return `所选的 ${targets.length} 项将被永久删除。`
})

async function confirmDelete(): Promise<void> {
  deleteBusy.value = true
  deleteError.value = null

  try {
    for (const node of deleteTargets.value) {
      // Group spaces have no recycle bin: a plain DELETE is already permanent.
      await deleteNode(node.id)
    }
    deleteOpen.value = false
    toast.push(deleteTargets.value.length === 1 ? '已永久删除' : `已永久删除 ${deleteTargets.value.length} 项`)
    clearSelection()
    await load()
  } catch (cause) {
    deleteError.value = groupErrorMessage(cause, '删除失败，请稍后重试')
  } finally {
    deleteBusy.value = false
  }
}

async function onRowAction(kind: RowActionKind, node: NodeItem): Promise<void> {
  switch (kind) {
    case 'preview':
      openPreview(node)
      return
    case 'rename':
      openRename(node)
      return
    case 'move':
      openMove([node])
      return
    case 'copy':
      await copyNodeInGroup(node)
      return
    case 'trash':
      openDelete([node])
      return
    default:
      return
  }
}

async function copyNodeInGroup(node: NodeItem): Promise<void> {
  try {
    const created = await copyNode(node.id)
    toast.push(`已复制为「${created.name}」`)
    await load()
  } catch (cause) {
    toast.push(groupErrorMessage(cause, '复制失败，请稍后重试'), 'error')
  }
}

function openPreview(node: NodeItem): void {
  if (node.kind !== 'file') return
  previewId.value = node.id
  previewOpen.value = true
}

function onPreviewNavigate(id: number): void {
  previewId.value = id
}

function openFilePicker(): void {
  if (!canUpload.value) return
  fileInput.value?.click()
}

function onFilesPicked(event: Event): void {
  const input = event.target as HTMLInputElement
  if (input.files && input.files.length > 0) uploads.addFiles(input.files, parentId.value)
  input.value = ''
}

function carriesFiles(event: DragEvent): boolean {
  const types = event.dataTransfer?.types
  return types ? Array.from(types).includes('Files') : false
}

function onDragEnter(event: DragEvent): void {
  if (!canUpload.value || !carriesFiles(event)) return
  dragDepth.value += 1
}

function onDragOver(event: DragEvent): void {
  if (!canUpload.value || !carriesFiles(event)) return
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
}

function onDragLeave(): void {
  dragDepth.value = Math.max(0, dragDepth.value - 1)
}

function onDrop(event: DragEvent): void {
  if (!canUpload.value) return
  event.preventDefault()
  dragDepth.value = 0
  if (event.dataTransfer?.files && event.dataTransfer.files.length > 0) {
    uploads.addFiles(event.dataTransfer.files, parentId.value)
  }
}

function onWindowDragOver(event: DragEvent): void {
  if (!canUpload.value) return
  if (carriesFiles(event)) event.preventDefault()
}

function onWindowDrop(event: DragEvent): void {
  if (!canUpload.value) return
  event.preventDefault()
  dragDepth.value = 0
}

function onSelectAllEvent(): void {
  if (items.value.length === 0) return
  selection.setSelection(items.value.map((item) => item.id))
}

onMounted(async () => {
  window.addEventListener('carp:select-all', onSelectAllEvent)
  viewMode.value = readViewMode()
  window.addEventListener('dragover', onWindowDragOver)
  window.addEventListener('drop', onWindowDrop)
  // 组可能刚被创建，或上一次列表请求与其它页面并发后结果被丢弃：
  // 进入页面时强制刷新一次，仍未找到再重试一次，最后才判定“组不存在”。
  await groups.load()
  let current = groups.byId(groupId.value)
  if (!current) {
    await groups.refresh()
    current = groups.byId(groupId.value)
  }
  if (!current) {
    notFound.value = true
    error.value = '组不存在，或你没有访问权限'
    return
  }
  crumbs.value = [{ id: current.root_id, name: current.name }]
  await load()
})

onBeforeUnmount(() => {
  window.removeEventListener('carp:select-all', onSelectAllEvent)
  window.removeEventListener('dragover', onWindowDragOver)
  window.removeEventListener('drop', onWindowDrop)
})

watch(
  () => auth.user,
  (user) => {
    if (!user) void router.replace({ name: 'login' })
  },
)

watch(
  () => groups.groups.length,
  () => {
    const current = groups.byId(groupId.value)
    if (current && notFound.value) {
      // 列表晚到时恢复页面，不再把“尚未加载”当成“组不存在”。
      notFound.value = false
      error.value = null
      crumbs.value = [{ id: current.root_id, name: current.name }]
      void load()
      return
    }
    if (!loaded.value && !notFound.value) void load()
  },
)

// The router reuses this component across /groups/:id, so switching groups must reset state.
watch(groupId, async (next, previous) => {
  if (next === previous) return
  selectedIds.value = []
  previewId.value = null
  previewOpen.value = false
  const current = groups.byId(next)
  if (!current) {
    items.value = []
    total.value = 0
    notFound.value = true
    error.value = '组不存在，或你没有访问权限'
    crumbs.value = []
    return
  }
  notFound.value = false
  error.value = null
  crumbs.value = [{ id: current.root_id, name: current.name }]
  await load()
})
onMounted(async () => {
  const prefs = usePreferencesStore()
  await prefs.load()
  const accountMode = prefs.get('view_mode')
  if (accountMode === 'list' || accountMode === 'grid') {
    viewMode.value = accountMode
    try {
      localStorage.setItem(VIEW_STORAGE_KEY, accountMode)
    } catch {}
  }
})
</script>

<template>
  <div
    class="page"
    @dragenter.prevent="onDragEnter"
    @dragover="onDragOver"
    @dragleave="onDragLeave"
    @drop="onDrop"
  >
    <header class="page-head">
      <div class="page-heading">
        <button
          v-if="!isRoot"
          class="back-link"
          type="button"
          :class="{ 'is-drop-target': crumbOverId === (crumbs[crumbs.length - 2]?.id ?? 0) }"
          @click="goBack"
          @dragover="onCrumbDragOver(crumbs[crumbs.length - 2]?.id ?? 0, $event)"
          @dragleave="onCrumbDragLeave(crumbs[crumbs.length - 2]?.id ?? 0)"
          @drop="onCrumbDrop(crumbs[crumbs.length - 2]?.id ?? 0, $event)"
        >
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M9.8 3.4 5 8l4.8 4.6" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
          返回上一级
        </button>
        <nav class="breadcrumb" aria-label="组空间路径">
          <template v-for="(crumb, index) in crumbs" :key="crumb.id">
            <span v-if="index > 0" class="crumb-sep" aria-hidden="true">/</span>
            <button
              v-if="index < crumbs.length - 1"
              class="crumb-link"
              type="button"
              :class="{ 'is-drop-target': crumbOverId === crumb.id }"
              @click="goCrumb(index)"
              @dragover="onCrumbDragOver(crumb.id, $event)"
              @dragleave="onCrumbDragLeave(crumb.id)"
              @drop="onCrumbDrop(crumb.id, $event)"
            >
              {{ crumb.name }}
            </button>
            <h1 v-else class="page-title crumb-current" aria-current="page">{{ crumb.name }}</h1>
          </template>
        </nav>
        <p v-if="!loading && !error && total > 0" class="page-sub">共 {{ total }} 项</p>
      </div>

      <div class="page-actions">
        <button
          class="btn btn-secondary"
          type="button"
          :disabled="!canCreate"
          :title="canCreate ? '' : '当前角色没有新建权限'"
          @click="dialogOpen = true"
        >
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M8 3.4v9.2M3.4 8h9.2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
          </svg>
          新建文件夹
        </button>
        <button
          class="btn btn-secondary"
          type="button"
          :disabled="!canUpload"
          :title="canUpload ? '' : '当前角色没有上传权限'"
          @click="openFilePicker"
        >
          上传文件
        </button>
        <RouterLink class="btn btn-secondary" :to="{ name: 'downloads' }">
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <path d="M7 2.6v6.8M4.1 6.6 7 9.5l2.9-2.9" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
            <path d="M2.4 11.6h9.2" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
          </svg>
          下载
        </RouterLink>
        <RouterLink class="btn btn-secondary" :to="{ name: 'transfers' }">
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <path d="M4.4 10.6a2.4 2.4 0 0 1-.3-4.8 3.4 3.4 0 0 1 6.4-.5 2.5 2.5 0 0 1 .6 4.9" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
            <path d="M7 6.2v5M5.2 9.4 7 11.2l1.8-1.8" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
          离线下载
        </RouterLink>
        <button
          class="btn btn-secondary view-toggle"
          type="button"
          :aria-label="viewMode === 'grid' ? '切换到列表视图' : '切换到网格视图'"
          :aria-pressed="viewMode === 'grid'"
          @click="setViewMode(viewMode === 'grid' ? 'list' : 'grid')"
        >
          <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
            <path
              v-if="viewMode === 'grid'"
              d="M2 3.5h10M2 7h10M2 10.5h10"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
            />
            <path
              v-else
              d="M2.2 2.2h3.4v3.4H2.2zM8.4 2.2h3.4v3.4H8.4zM2.2 8.4h3.4v3.4H2.2zM8.4 8.4h3.4v3.4H8.4z"
              fill="currentColor"
            />
          </svg>
        </button>
      </div>
    </header>

    <p v-if="hasSelection" class="selection-bar">
      <span class="selection-info">已选 {{ selectedIds.length }} 项</span>
      <button class="link-button" type="button" @click="selectedIds = allSelected ? [] : items.map((node) => node.id)">
        {{ allSelected ? '取消全选' : '全选' }}
      </button>
      <button class="link-button" type="button" @click="clearSelection">取消选择</button>
      <span class="selection-actions">
        <button
          v-if="canWrite"
          class="btn btn-secondary btn-small"
          type="button"
          @click="openMove(selectedItems)"
        >
          移动
        </button>
        <button
          v-if="canDelete"
          class="btn btn-danger btn-small"
          type="button"
          @click="openDelete(selectedItems)"
        >
          永久删除
        </button>
      </span>
    </p>

    <div v-if="notFound && !loading" class="state panel-block" role="alert">
      <h2 class="state-title">组不存在</h2>
      <p class="state-desc">{{ error }}</p>
      <RouterLink class="btn btn-primary" :to="{ name: 'files' }">返回我的文件</RouterLink>
    </div>

    <template v-else>
      <FileList
        v-if="viewMode === 'list'"
        :items="items"
        :loading="loading"
        :error="error"
        :selected-ids="selectedSet"
        :actions="rowActions"
        empty-title="这个组空间还没有文件"
        :empty-description="canUpload ? '上传文件，或新建一个文件夹开始整理。' : '组内还没有内容。'"
        :drop-target-id="dropTargetId"
        @retry="load"
        @create="dialogOpen = true"
        @action="onRowAction"
        @toggle="toggleNode"
        @select="selectNode"
        @open="openFolder"
        @preview="openPreview"
        @dragstart="onNodeDragStart"
        @dragend="onNodeDragEnd"
        @dragover="onNodeDragOver"
        @dragleave="onNodeDragLeave"
        @drop="onNodeDrop"
      />

      <section v-else class="panel" aria-label="组空间文件网格">
        <div v-if="loading" class="grid-skeleton" aria-hidden="true">
          <div v-for="index in 6" :key="index" class="skeleton-card" />
        </div>
        <div v-else-if="error && items.length === 0" class="state" role="alert">
          <h2 class="state-title">加载失败</h2>
          <p class="state-desc">{{ error }}</p>
          <button class="btn btn-secondary" type="button" @click="load">重试</button>
        </div>
        <EmptyState
          v-else-if="items.length === 0"
          :title="'这个组空间还没有文件'"
          :description="canUpload ? '上传文件，或新建一个文件夹开始整理。' : '组内还没有内容。'"
          :action-label="canCreate ? '新建文件夹' : undefined"
          @action="dialogOpen = true"
        />
        <FileGrid
          v-else
          :items="items"
          :selected-ids="selectedSet"
          :actions="rowActions"
          :drop-target-id="dropTargetId"
          @action="onRowAction"
          @toggle="toggleNode"
          @select="selectNode"
          @open="openFolder"
          @preview="openPreview"
          @dragstart="onNodeDragStart"
          @dragend="onNodeDragEnd"
          @dragover="onNodeDragOver"
          @dragleave="onNodeDragLeave"
          @drop="onNodeDrop"
        />
      </section>
    </template>

    <input ref="fileInput" class="visually-hidden" type="file" multiple @change="onFilesPicked" />

    <div v-if="isDragging" class="drop-overlay" aria-hidden="true">
      <div class="drop-card">松开鼠标上传到「{{ crumbs[crumbs.length - 1]?.name }}」</div>
    </div>

    <FolderDialog :open="dialogOpen" :create="createFolder" @close="dialogOpen = false" @created="dialogOpen = false" />
    <RenameDialog
      :open="renameOpen"
      :node="renameTarget"
      :rename="renameNode"
      @close="renameOpen = false"
      @renamed="renameOpen = false"
    />
    <MoveDialog
      :open="moveOpen"
      :nodes="moveTargets"
      :root-id="rootId"
      :root-name="group?.name ?? '组空间'"
      :busy="moveBusy"
      :error="moveError"
      @close="moveOpen = false"
      @move-to="onMoveTo"
    />
    <ConfirmDialog
      :open="deleteOpen"
      :title="deleteTitle"
      :message="deleteMessage"
      detail="组空间没有回收站，删除后不可恢复，将释放存储空间。"
      confirm-label="永久删除"
      danger
      :busy="deleteBusy"
      :error="deleteError"
      @close="deleteOpen = false"
      @confirm="confirmDelete"
    />
    <PreviewOverlay
      :open="previewOpen"
      :files="previewFiles"
      :file-id="previewId"
      @close="previewOpen = false"
      @navigate="onPreviewNavigate"
    />

    <p v-if="previewNode && previewOpen" class="visually-hidden">正在预览 {{ previewNode.name }}</p>

    <p v-if="loaded && total > items.length" class="load-more-tip">
      已显示 {{ items.length }} 项，共 {{ total }} 项。
    </p>

    <p v-if="auth.isAdmin" class="admin-tip">
      系统管理员通过「组管理」维护组与成员，文件内容需成为组成员后才能访问。
      <RouterLink class="link-button" :to="{ name: 'admin-groups' }">前往组管理</RouterLink>
    </p>
  </div>
</template>

<style scoped>
.page {
  position: relative;
  min-height: max(320px, calc(100vh - 264px));
}

.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  margin-bottom: 22px;
}

.page-title {
  margin: 0;
  font-size: 28px;
  font-weight: 800;
  letter-spacing: -0.03em;
}

.link-button {
  padding: 0;
  color: var(--color-primary-ink);
  font-size: var(--fs-sm);
  font-weight: 500;
}

.link-button:hover {
  text-decoration: underline;
  text-underline-offset: 2px;
}

.page-heading {
  display: flex;
  min-width: 0;
  flex-direction: column;
}

.page-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.view-toggle {
  width: 40px;
  padding: 0;
}

.back-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-bottom: 6px;
  color: var(--color-muted);
  font-size: var(--fs-sm);
}

.back-link:hover {
  color: var(--color-text);
}

.breadcrumb {
  display: flex;
  max-width: 100%;
  align-items: baseline;
  gap: 5px;
  overflow-x: auto;
  white-space: nowrap;
  scrollbar-width: none;
}

.breadcrumb::-webkit-scrollbar {
  display: none;
}

.back-link.is-drop-target,
.crumb-link.is-drop-target {
  background: var(--color-primary-soft);
  border-color: var(--color-primary);
  outline: 2px dashed var(--color-primary);
  outline-offset: 1px;
}

.crumb-sep {
  color: var(--color-muted);
}

.crumb-link {
  color: var(--color-muted);
  font-size: var(--fs-lg);
  font-weight: 500;
}

.crumb-link:hover {
  color: var(--color-primary-ink);
}

.crumb-current {
  flex: none;
}

.page-sub {
  margin-top: 2px;
  color: var(--color-muted);
  font-size: var(--fs-xs);
}

.selection-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
  padding: 9px 14px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  background: var(--color-surface);
  font-size: var(--fs-sm);
}

.selection-info {
  font-weight: 600;
}

.selection-actions {
  display: flex;
  margin-left: auto;
  gap: 8px;
}

.btn-small {
  height: 30px;
  padding: 0 10px;
  font-size: var(--fs-xs);
}

.btn-danger {
  background: var(--color-danger);
  color: var(--color-on-primary);
}

.btn-danger:hover {
  background: var(--color-brand-red);
}

.panel {
  overflow: hidden;
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  background: var(--color-surface);
}

.panel-block {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 48px 24px;
}

.state {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 48px 24px;
  text-align: center;
}

.state-title {
  margin: 0;
  font-size: var(--fs-lg);
  font-weight: 600;
}

.state-desc {
  margin: 0;
  color: var(--color-muted);
  font-size: var(--fs-sm);
}

.grid-skeleton {
  display: grid;
  gap: 12px;
  padding: 14px;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
}

.skeleton-card {
  height: 150px;
  border-radius: var(--radius);
  background: var(--color-border);
  opacity: 0.35;
}

.segmented {
  display: inline-flex;
  padding: 2px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-bg);
}

.segmented-btn {
  height: 28px;
  padding: 0 10px;
  border-radius: 5px;
  color: var(--color-muted);
  font-size: var(--fs-xs);
}

.segmented-btn.is-active {
  background: var(--color-surface);
  color: var(--color-text);
  font-weight: 600;
}

.load-more-tip,
.admin-tip {
  margin-top: 12px;
  color: var(--color-muted);
  font-size: var(--fs-xs);
}

.admin-tip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.drop-overlay {
  position: fixed;
  z-index: 60;
  display: grid;
  background: rgba(249, 115, 22, 0.1);
  inset: 0;
  place-items: center;
  pointer-events: none;
}

.drop-card {
  padding: 18px 26px;
  border: 1px dashed var(--color-primary);
  border-radius: var(--radius);
  background: var(--color-surface);
  color: var(--color-primary-ink);
  font-weight: 600;
}

@media (max-width: 520px) {
  .page-actions {
    width: 100%;
  }

  .selection-actions {
    width: 100%;
    margin-left: 0;
  }
}
</style>
