import type { Memory } from './types'

export function timeAgo(dateStr: string): string {
  const seconds = Math.floor((Date.now() - new Date(dateStr).getTime()) / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  if (seconds < 604800) return `${Math.floor(seconds / 86400)}d ago`
  return new Date(dateStr).toLocaleDateString()
}

export function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1_048_576) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1_048_576).toFixed(1)} MB`
}

export function getCurrentStrength(memory: Memory): number {
  if (memory.isPinned) return memory.baseStrength
  const daysSinceAccess =
    (Date.now() - new Date(memory.lastAccessedAt).getTime()) / 86_400_000
  const effectiveDecayRate = memory.decayRate * (1 - memory.importance * 0.5)
  const strength = memory.baseStrength * Math.exp(-effectiveDecayRate * daysSinceAccess)
  return Math.max(0, Math.min(1, strength))
}

export function strengthColor(strength: number): string {
  if (strength >= 0.7) return 'text-green-400'
  if (strength >= 0.4) return 'text-yellow-400'
  return 'text-red-400'
}

export function strengthBg(strength: number): string {
  if (strength >= 0.7) return 'bg-green-400'
  if (strength >= 0.4) return 'bg-yellow-400'
  return 'bg-red-400'
}
