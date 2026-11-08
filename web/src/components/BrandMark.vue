<script setup lang="ts">
withDefaults(defineProps<{ size?: number }>(), { size: 24 })
</script>

<template>
  <svg
    class="brand-mark"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    aria-hidden="true"
    focusable="false"
  >
    <defs>
      <linearGradient id="carp-logo-g" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="#ff7e7e" />
        <stop offset="1" stop-color="#ffa259" />
      </linearGradient>
    </defs>
    <rect width="24" height="24" rx="6.5" fill="url(#carp-logo-g)" />
    <path
      d="M6.5 9.25c0-.69.56-1.25 1.25-1.25h3.1l1.5 1.6h4.4c.69 0 1.25.56 1.25 1.25v4.9c0 .69-.56 1.25-1.25 1.25H7.75c-.69 0-1.25-.56-1.25-1.25v-6.5Z"
      stroke="#fff"
      stroke-width="1.5"
      stroke-linejoin="round"
    />
    <path d="M9.5 13.4h5" stroke="#fff" stroke-width="1.5" stroke-linecap="round" />
  </svg>
</template>

<style scoped>
.brand-mark {
  flex: none;
}
</style>
