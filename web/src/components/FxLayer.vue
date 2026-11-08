<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { fxEnabled, presetFor, type FxPlayDetail } from '../lib/fx'

interface Particle {
  x: number
  y: number
  vx: number
  vy: number
  gravity: number
  size: number
  color: string
  life: number
  ttl: number
  shape: 'circle' | 'rect'
  spin: number
  rot: number
}

const canvas = ref<HTMLCanvasElement | null>(null)
let ctx: CanvasRenderingContext2D | null = null
let particles: Particle[] = []
let raf: number | null = null
let enabled = false
let last = 0
const MAX_PARTICLES = 220

const reducedMotion = (): boolean =>
  typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

function resize(): void {
  const el = canvas.value
  if (!el) return
  const dpr = Math.min(window.devicePixelRatio || 1, 2)
  el.width = Math.round(window.innerWidth * dpr)
  el.height = Math.round(window.innerHeight * dpr)
  el.style.width = `${window.innerWidth}px`
  el.style.height = `${window.innerHeight}px`
  if (!ctx) return
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
}

function spawn(detail: FxPlayDetail): void {
  const preset = presetFor(detail.name)
  if (!preset || reducedMotion()) return

  const x = detail.x ?? Math.min(200, window.innerWidth * 0.2)
  const y = detail.y ?? Math.min(220, window.innerHeight * 0.3)
  const colors = preset.colors

  const push = (p: Partial<Particle>): void => {
    if (particles.length >= MAX_PARTICLES) return
    particles.push({
      x,
      y,
      vx: 0,
      vy: 0,
      gravity: 0,
      size: 4,
      color: colors[0],
      life: 0,
      ttl: 900,
      shape: 'circle',
      spin: 0,
      rot: 0,
      ...p,
    })
  }

  const color = (i: number): string => colors[i % colors.length]

  switch (preset.kind) {
    case 'burst': {
      const count = 36
      for (let i = 0; i < count; i += 1) {
        const angle = (Math.PI * 2 * i) / count + Math.random() * 0.2
        const speed = 90 + Math.random() * 150
        push({
          vx: Math.cos(angle) * speed,
          vy: Math.sin(angle) * speed,
          gravity: 120,
          size: 2.5 + Math.random() * 3,
          color: color(i),
          ttl: 800 + Math.random() * 400,
        })
      }
      break
    }
    case 'drift': {
      for (let i = 0; i < 30; i += 1) {
        push({
          x: x + (Math.random() - 0.5) * 80,
          y: y + (Math.random() - 0.5) * 40,
          vx: 120 + Math.random() * 180,
          vy: -20 + Math.random() * 40,
          size: 1.5 + Math.random() * 2.5,
          color: color(i),
          ttl: 700 + Math.random() * 400,
        })
      }
      break
    }
    case 'confetti': {
      for (let i = 0; i < 42; i += 1) {
        push({
          x: x + (Math.random() - 0.5) * 160,
          y: y + (Math.random() - 0.5) * 40,
          vx: (Math.random() - 0.5) * 120,
          vy: -160 - Math.random() * 120,
          gravity: 320,
          size: 3 + Math.random() * 4,
          color: color(i),
          shape: 'rect',
          spin: (Math.random() - 0.5) * 10,
          ttl: 1000 + Math.random() * 400,
        })
      }
      break
    }
    case 'float': {
      for (let i = 0; i < 26; i += 1) {
        push({
          x: x + (Math.random() - 0.5) * 120,
          y: y + Math.random() * 60,
          vx: (Math.random() - 0.5) * 30,
          vy: -60 - Math.random() * 70,
          size: 2 + Math.random() * 3,
          color: color(i),
          ttl: 1000 + Math.random() * 400,
        })
      }
      break
    }
    case 'ring': {
      const count = 40
      for (let i = 0; i < count; i += 1) {
        const angle = (Math.PI * 2 * i) / count
        push({
          x: x + Math.cos(angle) * 24,
          y: y + Math.sin(angle) * 24,
          vx: Math.cos(angle) * 130,
          vy: Math.sin(angle) * 130,
          size: 2 + Math.random() * 2,
          color: color(i),
          ttl: 700 + Math.random() * 300,
        })
      }
      break
    }
    case 'scan': {
      for (let i = 0; i < 34; i += 1) {
        push({
          x: x - 120 + (i / 34) * 240,
          y,
          vx: 0,
          vy: 0,
          size: 2,
          color: color(i),
          ttl: 900 + Math.random() * 300,
        })
      }
      break
    }
    case 'dust': {
      for (let i = 0; i < 30; i += 1) {
        push({
          x: x + (Math.random() - 0.5) * 100,
          y: y + (Math.random() - 0.5) * 60,
          vx: (Math.random() - 0.5) * 60,
          vy: 20 + Math.random() * 60,
          gravity: 40,
          size: 2 + Math.random() * 4,
          color: color(i),
          ttl: 800 + Math.random() * 400,
        })
      }
      break
    }
  }

  start()
}

function start(): void {
  if (raf !== null) return
  last = performance.now()
  raf = requestAnimationFrame(tick)
}

function tick(now: number): void {
  raf = null
  const el = canvas.value
  if (!el || !ctx) return
  const dt = Math.min((now - last) / 1000, 0.05)
  last = now

  ctx.clearRect(0, 0, window.innerWidth, window.innerHeight)

  const keep: Particle[] = []
  for (const p of particles) {
    p.life += dt * 1000
    if (p.life >= p.ttl) continue
    p.vy += p.gravity * dt
    p.x += p.vx * dt
    p.y += p.vy * dt
    p.rot += p.spin * dt

    const progress = p.life / p.ttl
    const alpha = 1 - progress * progress

    ctx.globalAlpha = Math.max(0, alpha)
    ctx.fillStyle = p.color
    if (p.shape === 'rect') {
      ctx.save()
      ctx.translate(p.x, p.y)
      ctx.rotate(p.rot)
      ctx.fillRect(-p.size, -p.size / 2, p.size * 2, p.size)
      ctx.restore()
    } else {
      ctx.beginPath()
      ctx.arc(p.x, p.y, p.size, 0, Math.PI * 2)
      ctx.fill()
    }
    keep.push(p)
  }
  ctx.globalAlpha = 1
  particles = keep

  if (particles.length > 0) {
    raf = requestAnimationFrame(tick)
  } else {
    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight)
  }
}

function onPlay(event: Event): void {
  if (!enabled) return
  spawn((event as CustomEvent<FxPlayDetail>).detail)
}

function onToggle(event: Event): void {
  enabled = (event as CustomEvent<{ enabled: boolean }>).detail.enabled
}

onMounted(() => {
  enabled = fxEnabled()
  const el = canvas.value
  if (!el) return
  ctx = el.getContext('2d')
  resize()
  window.addEventListener('resize', resize)
  window.addEventListener('carp:fx-play', onPlay)
  window.addEventListener('carp:fx-enabled', onToggle)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  window.removeEventListener('carp:fx-play', onPlay)
  window.removeEventListener('carp:fx-enabled', onToggle)
  if (raf !== null) cancelAnimationFrame(raf)
})
</script>

<template>
  <canvas ref="canvas" class="fx-layer" aria-hidden="true"></canvas>
</template>

<style scoped>
.fx-layer {
  position: fixed;
  inset: 0;
  z-index: 80;
  pointer-events: none;
}
</style>
