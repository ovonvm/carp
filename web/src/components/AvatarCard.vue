<script setup lang="ts">
import { computed, ref } from 'vue'
import { ApiError, deleteMyAvatar, uploadMyAvatar } from '../api/client'
import { useAuthStore } from '../stores/auth'
import { useToast } from '../composables/useToast'

const auth = useAuthStore()
const toast = useToast()

const busy = ref(false)
const error = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

const avatarURL = computed(() => auth.user?.avatar_url ?? '')
const initial = computed(() => (auth.user?.username ?? '?').slice(0, 1).toUpperCase())

function pick(): void {
  fileInput.value?.click()
}

async function onPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) {
    error.value = '头像仅支持 PNG/JPG/WebP'
    return
  }
  if (file.size > 2 * 1024 * 1024) {
    error.value = '头像不能超过 2 MB'
    return
  }
  busy.value = true
  error.value = ''
  try {
    const result = await uploadMyAvatar(file)
    if (auth.user) auth.user.avatar_url = result.avatar_url
    await auth.refreshUser()
    toast.push('头像已更新')
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '上传失败，请稍后重试'
  } finally {
    busy.value = false
  }
}

async function remove(): Promise<void> {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await deleteMyAvatar()
    await auth.refreshUser()
    toast.push('头像已删除')
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '操作失败，请稍后重试'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="avatar-card" aria-labelledby="avatar-title">
    <header>
      <h2 id="avatar-title" class="avatar-title">个人资料</h2>
      <p class="avatar-hint">头像会按原图等比缩放显示，不会裁剪；全站出现你的名字的地方都会使用它。</p>
    </header>

    <div class="avatar-row">
      <span class="avatar-preview">
        <img v-if="avatarURL" :src="avatarURL" alt="" />
        <span v-else aria-hidden="true">{{ initial }}</span>
      </span>
      <div class="avatar-actions">
        <button class="btn btn-secondary btn-sm" type="button" :disabled="busy" @click="pick">
          {{ avatarURL ? '更换头像' : '上传头像' }}
        </button>
        <button v-if="avatarURL" class="btn btn-ghost btn-sm" type="button" :disabled="busy" @click="remove">
          删除头像
        </button>
        <input ref="fileInput" class="sr-only" type="file" accept="image/png,image/jpeg,image/webp" @change="onPicked" />
      </div>
    </div>

    <p class="avatar-hint">PNG/JPG/WebP，≤2 MB。</p>
    <p v-if="error" class="avatar-error" role="alert">{{ error }}</p>
  </section>
</template>

<style scoped>
.avatar-card {
  padding: 20px 22px 18px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-xs);
}

.avatar-title {
  font-size: var(--fs-lg);
  font-weight: 600;
}

.avatar-hint {
  margin-top: 4px;
  font-size: var(--fs-sm);
  color: var(--color-muted);
}

.avatar-row {
  display: flex;
  align-items: center;
  gap: 16px;
  margin: 16px 0 8px;
}

.avatar-preview {
  display: grid;
  place-items: center;
  width: 64px;
  height: 64px;
  overflow: hidden;
  background: var(--color-primary-soft);
  border-radius: 50%;
  font-size: 24px;
  font-weight: 600;
  color: var(--color-primary-ink);
}

.avatar-preview img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.avatar-actions {
  display: flex;
  gap: 8px;
}

.avatar-error {
  margin-top: 8px;
  padding: 8px 10px;
  background: var(--color-danger-soft);
  border-radius: var(--radius-sm);
  font-size: var(--fs-sm);
  color: var(--color-danger);
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
