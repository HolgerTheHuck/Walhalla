<script lang="ts" module>
  export interface ToastMessage {
    id: number
    message: string
    type: 'success' | 'error' | 'info'
  }

  let toasts = $state<ToastMessage[]>([])
  let nextId = 0

  export function showToast(message: string, type: 'success' | 'error' | 'info' = 'info') {
    const id = nextId++
    toasts = [...toasts, { id, message, type }]
    setTimeout(() => {
      toasts = toasts.filter(t => t.id !== id)
    }, 4000)
  }
</script>

<script lang="ts">
  // Re-export for component usage
</script>

<div class="toast-container">
  {#each toasts as toast (toast.id)}
    <div class="toast {toast.type}">
      <span>{toast.message}</span>
      <button class="toast-close" onclick={() => toasts = toasts.filter(t => t.id !== toast.id)}>&times;</button>
    </div>
  {/each}
</div>

<style>
  .toast-container {
    position: fixed;
    top: 1rem;
    right: 1rem;
    z-index: 200;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .toast {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    padding: 0.75rem 1rem;
    border-radius: var(--radius);
    color: white;
    font-size: 0.875rem;
    min-width: 280px;
    max-width: 400px;
    animation: slideIn 0.2s ease;
    box-shadow: var(--shadow);
  }

  .toast.success { background: var(--success); }
  .toast.error { background: var(--danger); }
  .toast.info { background: var(--accent); }

  .toast-close {
    background: none;
    border: none;
    color: white;
    font-size: 1.25rem;
    cursor: pointer;
    opacity: 0.7;
    line-height: 1;
  }

  .toast-close:hover {
    opacity: 1;
  }

  @keyframes slideIn {
    from { transform: translateX(100%); opacity: 0; }
    to { transform: translateX(0); opacity: 1; }
  }
</style>
