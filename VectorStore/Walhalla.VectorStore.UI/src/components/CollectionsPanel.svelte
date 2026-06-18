<script lang="ts">
  import { api, type CollectionInfo, type CreateCollectionRequest } from '../lib/api'
  import { showToast } from './Toast.svelte'

  interface Props {
    collections: CollectionInfo[]
    onRefresh: () => void
  }

  let { collections, onRefresh }: Props = $props()

  let showCreateModal = $state(false)
  let showDeleteConfirm = $state('')
  let selectedCollection = $state('')
  let newCollection = $state<CreateCollectionRequest>({
    name: '',
    dimension: 128,
    metric: 'Cosine',
    enableHnsw: true,
  })
  let creating = $state(false)
  let deleting = $state(false)

  async function createCollection() {
    if (!newCollection.name.trim()) {
      showToast('Collection name is required', 'error')
      return
    }
    if (newCollection.dimension < 1) {
      showToast('Dimension must be at least 1', 'error')
      return
    }

    creating = true
    try {
      await api.createCollection(newCollection)
      showToast(`Collection "${newCollection.name}" created`, 'success')
      showCreateModal = false
      newCollection = { name: '', dimension: 128, metric: 'Cosine', enableHnsw: true }
      onRefresh()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to create collection', 'error')
    } finally {
      creating = false
    }
  }

  async function deleteCollection(name: string) {
    deleting = true
    try {
      await api.deleteCollection(name)
      showToast(`Collection "${name}" deleted`, 'success')
      showDeleteConfirm = ''
      onRefresh()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to delete collection', 'error')
    } finally {
      deleting = false
    }
  }
</script>

<div class="collections-panel">
  <header class="page-header">
    <div>
      <h1>Collections</h1>
      <p class="subtitle">Manage your vector collections</p>
    </div>
    <button class="btn btn-primary" onclick={() => showCreateModal = true}>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M12 5v14M5 12h14"/>
      </svg>
      New Collection
    </button>
  </header>

  {#if collections.length === 0}
    <div class="card empty-state">
      <div class="empty-state-icon">📦</div>
      <p>No collections yet</p>
      <p class="empty-hint">Create your first collection to start storing vectors</p>
      <button class="btn btn-primary" onclick={() => showCreateModal = true}>Create Collection</button>
    </div>
  {:else}
    <div class="collections-table-wrapper">
      <table class="table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Dimension</th>
            <th>Metric</th>
            <th>Vectors</th>
            <th>Index</th>
            <th style="width: 120px">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each collections as collection (collection.name)}
            <tr class:selected={selectedCollection === collection.name}
              onclick={() => selectedCollection = collection.name}
            >
              <td>
                <div class="collection-name">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/>
                  </svg>
                  {collection.name}
                </div>
              </td>
              <td>{collection.dimension}</td>
              <td>
                <span class="badge badge-info">{collection.metric}</span>
              </td>
              <td>{collection.count.toLocaleString()}</td>
              <td>
                {#if collection.hnswEnabled}
                  <span class="badge badge-success">HNSW</span>
                {:else}
                  <span class="badge badge-warning">None</span>
                {/if}
              </td>
              <td>
                <div class="actions">
                  <button
                    class="btn-icon"
                    title="Delete"
                    onclick={(e) => { e.stopPropagation(); showDeleteConfirm = collection.name }}
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                      <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                    </svg>
                  </button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

{#if showCreateModal}
  <div class="modal-overlay">
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close create collection dialog"
      onclick={() => showCreateModal = false}
    ></button>
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="create-collection-title">
      <div class="modal-header">
        <h2 class="modal-title" id="create-collection-title">Create Collection</h2>
        <button class="modal-close" onclick={() => showCreateModal = false} aria-label="Close dialog">&times;</button>
      </div>

      <div class="form-group">
        <label for="create-collection-name">Name</label>
        <input id="create-collection-name" bind:value={newCollection.name} placeholder="my-collection" />
      </div>

      <div class="form-group">
        <label for="create-collection-dimension">Dimension</label>
        <input id="create-collection-dimension" type="number" bind:value={newCollection.dimension} min="1" />
      </div>

      <div class="form-group">
        <label for="create-collection-metric">Metric</label>
        <select id="create-collection-metric" bind:value={newCollection.metric}>
          <option value="Cosine">Cosine</option>
          <option value="Euclidean">Euclidean</option>
          <option value="DotProduct">Dot Product</option>
        </select>
      </div>

      <div class="form-group">
        <label class="checkbox-label">
          <input type="checkbox" bind:checked={newCollection.enableHnsw} />
          Enable HNSW Index
        </label>
      </div>

      <div class="modal-actions">
        <button class="btn btn-secondary" onclick={() => showCreateModal = false}>Cancel</button>
        <button class="btn btn-primary" onclick={createCollection} disabled={creating}>
          {creating ? 'Creating...' : 'Create'}
        </button>
      </div>
    </div>
  </div>
{/if}

{#if showDeleteConfirm}
  <div class="modal-overlay">
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close delete collection dialog"
      onclick={() => showDeleteConfirm = ''}
    ></button>
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="delete-collection-title">
      <div class="modal-header">
        <h2 class="modal-title" id="delete-collection-title">Delete Collection</h2>
      </div>
      <p>Are you sure you want to delete "{showDeleteConfirm}"? This action cannot be undone.</p>
      <div class="modal-actions">
        <button class="btn btn-secondary" onclick={() => showDeleteConfirm = ''}>Cancel</button>
        <button class="btn btn-danger" onclick={() => deleteCollection(showDeleteConfirm)} disabled={deleting}>
          {deleting ? 'Deleting...' : 'Delete'}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .collections-panel {
    max-width: 1200px;
  }

  .page-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1.5rem;
  }

  .page-header h1 {
    font-size: 1.75rem;
    font-weight: 700;
    margin-bottom: 0.25rem;
  }

  .subtitle {
    color: var(--text-secondary);
    font-size: 0.875rem;
  }

  .collections-table-wrapper {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .collection-name {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-weight: 500;
  }

  .actions {
    display: flex;
    gap: 0.5rem;
  }

  .btn-icon {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    padding: 0.25rem;
    border-radius: var(--radius);
    transition: all 0.15s;
  }

  .btn-icon:hover {
    color: var(--danger);
    background: rgba(239, 68, 68, 0.1);
  }

  tr {
    cursor: pointer;
    transition: background 0.15s;
  }

  tr:hover {
    background: rgba(255, 255, 255, 0.02);
  }

  tr.selected {
    background: rgba(59, 130, 246, 0.1);
  }

  .checkbox-label {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    cursor: pointer;
    color: var(--text-primary);
  }

  .checkbox-label input {
    width: auto;
  }

  .modal-actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    margin-top: 1.5rem;
  }

  .empty-hint {
    font-size: 0.875rem;
    margin: 0.5rem 0 1rem;
  }
</style>
