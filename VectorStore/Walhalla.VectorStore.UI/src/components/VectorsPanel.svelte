<script lang="ts">
  import { api, type CollectionInfo, type VectorEntry, type PutVectorRequest } from '../lib/api'
  import { selectedCollection } from '../lib/stores'
  import { showToast } from './Toast.svelte'

  interface Props {
    collections: CollectionInfo[]
    onRefresh: () => void
  }

  let { collections, onRefresh }: Props = $props()

  let vectors = $state<VectorEntry[]>([])
  let loading = $state(false)
  let limit = $state(50)
  let offset = $state(0)

  let showAddModal = $state(false)
  let showDeleteConfirm = $state<number | null>(null)
  let adding = $state(false)
  let deleting = $state(false)
  let bulkUploading = $state(false)
  let dragOver = $state(false)

  let newId = $state('')
  let vectorInput = $state('')
  let metadataInput = $state('')

  let selectedInfo = $derived(collections.find(c => c.name === $selectedCollection))
  let totalCount = $derived(selectedInfo?.count ?? 0)
  let hasMore = $derived(offset + limit < totalCount)
  let startIndex = $derived(offset + 1)
  let endIndex = $derived(Math.min(offset + limit, totalCount))

  $effect(() => {
    if ($selectedCollection) {
      offset = 0
      loadVectors()
    }
  })

  async function loadVectors() {
    if (!$selectedCollection) return
    loading = true
    try {
      vectors = await api.getVectors($selectedCollection, limit, offset)
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to load vectors', 'error')
    } finally {
      loading = false
    }
  }

  function parseVector(input: string): number[] | null {
    try {
      const parsed = JSON.parse(input)
      if (Array.isArray(parsed) && parsed.every(v => typeof v === 'number')) {
        return parsed
      }
    } catch {
      // fall through to comma-separated
    }
    const comma = input.split(',').map(s => parseFloat(s.trim())).filter(n => !isNaN(n))
    if (comma.length > 0) return comma
    return null
  }

  function parseMetadata(input: string): Record<string, unknown> | null {
    if (!input.trim()) return null
    try {
      return JSON.parse(input) as Record<string, unknown>
    } catch {
      showToast('Invalid metadata JSON', 'error')
      return null
    }
  }

  async function addVector() {
    if (!$selectedCollection) {
      showToast('Please select a collection', 'error')
      return
    }

    const id = parseInt(newId, 10)
    if (isNaN(id)) {
      showToast('Invalid ID', 'error')
      return
    }

    const vector = parseVector(vectorInput)
    if (!vector || vector.length === 0) {
      showToast('Please enter a valid vector', 'error')
      return
    }

    const collection = collections.find(c => c.name === $selectedCollection)
    if (collection && vector.length !== collection.dimension) {
      showToast(`Dimension mismatch: expected ${collection.dimension}, got ${vector.length}`, 'error')
      return
    }

    const metadata = parseMetadata(metadataInput)
    if (metadataInput.trim() && metadata === null) return

    adding = true
    try {
      await api.putVector($selectedCollection, { id, vector, metadata: metadata ?? undefined })
      showToast(`Vector ${id} added`, 'success')
      showAddModal = false
      newId = ''
      vectorInput = ''
      metadataInput = ''
      await loadVectors()
      onRefresh()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to add vector', 'error')
    } finally {
      adding = false
    }
  }

  async function deleteVector(id: number) {
    if (!$selectedCollection) return
    deleting = true
    try {
      await api.deleteVector($selectedCollection, id)
      showToast(`Vector ${id} deleted`, 'success')
      showDeleteConfirm = null
      await loadVectors()
      onRefresh()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Failed to delete vector', 'error')
    } finally {
      deleting = false
    }
  }

  async function handleBulkUpload(file: File) {
    if (!$selectedCollection) {
      showToast('Please select a collection first', 'error')
      return
    }

    const collection = collections.find(c => c.name === $selectedCollection)
    if (!collection) return

    bulkUploading = true
    try {
      const text = await file.text()
      const data = JSON.parse(text)

      if (!Array.isArray(data)) {
        showToast('Bulk upload expects a JSON array', 'error')
        return
      }

      const requests: PutVectorRequest[] = []
      const errors: string[] = []

      for (let i = 0; i < data.length; i++) {
        const item = data[i]
        if (typeof item.id !== 'number' || !Array.isArray(item.vector)) {
          errors.push(`Row ${i + 1}: missing id or vector`)
          continue
        }
        if (item.vector.length !== collection.dimension) {
          errors.push(`Row ${i + 1}: dimension mismatch`)
          continue
        }
        requests.push({
          id: item.id,
          vector: item.vector,
          metadata: item.metadata
        })
      }

      if (errors.length > 0 && errors.length === data.length) {
        showToast(`All ${errors.length} rows invalid`, 'error')
        return
      }

      const result = await api.putVectorsBulk($selectedCollection, requests)
      showToast(`Imported ${result.imported} of ${requests.length} vectors`, result.failed > 0 ? 'error' : 'success')
      if (result.failed > 0 && result.errors.length > 0) {
        console.error('Bulk upload errors:', result.errors.slice(0, 5))
      }

      await loadVectors()
      onRefresh()
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Bulk upload failed', 'error')
    } finally {
      bulkUploading = false
    }
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault()
    dragOver = false
    const file = e.dataTransfer?.files[0]
    if (file) {
      handleBulkUpload(file)
    }
  }

  function handleDragOver(e: DragEvent) {
    e.preventDefault()
    dragOver = true
  }

  function handleDragLeave() {
    dragOver = false
  }

  function prevPage() {
    offset = Math.max(0, offset - limit)
    loadVectors()
  }

  function nextPage() {
    if (hasMore) {
      offset += limit
      loadVectors()
    }
  }
</script>

<div class="vectors-panel">
  <header class="page-header">
    <div>
      <h1>Vectors</h1>
      <p class="subtitle">Manage vectors in your collections</p>
    </div>
    <button class="btn btn-primary" onclick={() => showAddModal = true} disabled={!$selectedCollection}>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M12 5v14M5 12h14"/>
      </svg>
      Add Vector
    </button>
  </header>

  {#if !$selectedCollection}
    <div class="card empty-state">
      <div class="empty-state-icon">📦</div>
      <p>No collection selected</p>
      <p class="empty-hint">Select a collection to view its vectors</p>
      <div class="form-group collection-picker">
        <select value={$selectedCollection} onchange={(e) => selectedCollection.set((e.target as HTMLSelectElement).value)}>
          <option value="">Select a collection...</option>
          {#each collections as c (c.name)}
            <option value={c.name}>{c.name} ({c.dimension}d, {c.metric})</option>
          {/each}
        </select>
      </div>
    </div>
  {:else}
    <div class="toolbar">
      <div class="toolbar-info">
        <span class="toolbar-label">{$selectedCollection}</span>
        <span class="toolbar-count">
          {#if totalCount > 0}
            Showing {startIndex}–{endIndex} of {totalCount.toLocaleString()}
          {:else}
            {totalCount.toLocaleString()} vectors
          {/if}
        </span>
      </div>
      <div class="toolbar-actions">
        <select bind:value={limit} onchange={() => { offset = 0; loadVectors(); }} class="rows-select">
          <option value={10}>10 rows</option>
          <option value={25}>25 rows</option>
          <option value={50}>50 rows</option>
          <option value={100}>100 rows</option>
        </select>
        <button class="btn btn-secondary" onclick={prevPage} disabled={offset === 0 || loading}>
          Prev
        </button>
        <button class="btn btn-secondary" onclick={nextPage} disabled={!hasMore || loading}>
          Next
        </button>
      </div>
    </div>

    {#if vectors.length === 0}
      <div class="card empty-state">
        <div class="empty-state-icon">📭</div>
        <p>No vectors in this collection</p>
        <p class="empty-hint">Add vectors or upload a JSON file</p>
      </div>
    {:else}
      <div class="table-wrapper">
        <table class="table">
          <thead>
            <tr>
              <th>ID</th>
              <th>Dimension</th>
              <th>Vector Preview</th>
              <th>Metadata</th>
              <th style="width: 60px;"></th>
            </tr>
          </thead>
          <tbody>
            {#each vectors as v (v.id)}
              <tr>
                <td>{v.id}</td>
                <td>{v.dimension}</td>
                <td>
                  {#if v.vector}
                    <code class="vector-preview">
                      [{v.vector.slice(0, 3).join(', ')}{v.vector.length > 3 ? ', ...' : ''}]
                    </code>
                  {:else}
                    <span class="no-meta">—</span>
                  {/if}
                </td>
                <td>
                  {#if v.metadata}
                    <pre class="metadata">{JSON.stringify(v.metadata)}</pre>
                  {:else}
                    <span class="no-meta">—</span>
                  {/if}
                </td>
                <td>
                  <button
                    class="btn-icon"
                    title="Delete"
                    onclick={() => showDeleteConfirm = v.id}
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                      <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                    </svg>
                  </button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}

    {#if loading}
      <div class="loading-bar">Loading...</div>
    {/if}

    <div
      class="dropzone"
      class:active={dragOver}
      class:uploading={bulkUploading}
      ondrop={handleDrop}
      ondragover={handleDragOver}
      ondragleave={handleDragLeave}
      role="button"
      tabindex="0"
    >
      {#if bulkUploading}
        <span class="dropzone-text">Uploading...⏳</span>
      {:else}
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
          <polyline points="17 8 12 3 7 8"/>
          <line x1="12" y1="3" x2="12" y2="15"/>
        </svg>
        <span class="dropzone-text">Drop JSON file here for bulk upload</span>
        <span class="dropzone-hint">Expected format: &#91;&#123;"id": 1, "vector": [0.1, ...], "metadata": &#123;&#125;&#125;&#93;</span>
      {/if}
    </div>
  {/if}
</div>

{#if showAddModal}
  <div class="modal-overlay">
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close add vector dialog"
      onclick={() => showAddModal = false}
    ></button>
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="add-vector-title">
      <div class="modal-header">
        <h2 class="modal-title" id="add-vector-title">Add Vector</h2>
        <button class="modal-close" onclick={() => showAddModal = false} aria-label="Close dialog">&times;</button>
      </div>

      <div class="form-group">
        <label for="add-vector-collection">Collection</label>
        <select id="add-vector-collection" value={$selectedCollection} onchange={(e) => selectedCollection.set((e.target as HTMLSelectElement).value)}>
          {#each collections as c (c.name)}
            <option value={c.name}>{c.name} ({c.dimension}d)</option>
          {/each}
        </select>
      </div>

      <div class="form-group">
        <label for="add-vector-id">ID</label>
        <input id="add-vector-id" type="number" bind:value={newId} placeholder="1" />
      </div>

      <div class="form-group">
        <label for="add-vector-values">Vector</label>
        <div class="vector-input-wrapper">
          <textarea
            id="add-vector-values"
            bind:value={vectorInput}
            placeholder="[0.1, 0.2, 0.3, ...] or comma-separated values"
            rows="3"
          ></textarea>
        </div>
      </div>

      <div class="form-group">
        <label for="add-vector-metadata">Metadata (JSON)</label>
        <textarea
          id="add-vector-metadata"
          bind:value={metadataInput}
          placeholder={'{"body":"agent memory"}'}
          rows="2"
        ></textarea>
      </div>

      <div class="modal-actions">
        <button class="btn btn-secondary" onclick={() => showAddModal = false}>Cancel</button>
        <button class="btn btn-primary" onclick={addVector} disabled={adding}>
          {adding ? 'Adding...' : 'Add Vector'}
        </button>
      </div>
    </div>
  </div>
{/if}

{#if showDeleteConfirm !== null}
  <div class="modal-overlay">
    <button
      type="button"
      class="modal-backdrop"
      aria-label="Close delete vector dialog"
      onclick={() => showDeleteConfirm = null}
    ></button>
    <div class="modal" role="dialog" aria-modal="true" aria-labelledby="delete-vector-title">
      <div class="modal-header">
        <h2 class="modal-title" id="delete-vector-title">Delete Vector</h2>
      </div>
      <p>Are you sure you want to delete vector <strong>#{showDeleteConfirm}</strong>?</p>
      <p class="empty-hint">This action cannot be undone.</p>
      <div class="modal-actions">
        <button class="btn btn-secondary" onclick={() => showDeleteConfirm = null}>Cancel</button>
        <button class="btn btn-danger" onclick={() => { if (showDeleteConfirm !== null) deleteVector(showDeleteConfirm) }} disabled={deleting}>
          {deleting ? 'Deleting...' : 'Delete'}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .vectors-panel {
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

  .toolbar {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
    gap: 1rem;
    flex-wrap: wrap;
  }

  .toolbar-info {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .toolbar-label {
    font-weight: 600;
    color: var(--text-primary);
  }

  .toolbar-count {
    font-size: 0.875rem;
    color: var(--text-secondary);
  }

  .toolbar-actions {
    display: flex;
    gap: 0.5rem;
    align-items: center;
  }

  .rows-select {
    width: auto;
    padding-right: 1.5rem;
  }

  .table-wrapper {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: auto;
    margin-bottom: 1rem;
  }

  .vector-preview {
    font-family: var(--mono, monospace);
    font-size: 0.8rem;
    background: var(--bg-primary);
    padding: 0.25rem 0.5rem;
    border-radius: var(--radius);
    color: var(--text-primary);
  }

  .metadata {
    font-size: 0.75rem;
    color: var(--text-secondary);
    background: var(--bg-primary);
    padding: 0.5rem;
    border-radius: var(--radius);
    max-height: 100px;
    overflow: auto;
    margin: 0;
    font-family: var(--mono, monospace);
  }

  .no-meta {
    color: var(--text-secondary);
  }

  .dropzone {
    border: 2px dashed var(--border);
    border-radius: var(--radius);
    padding: 2rem;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.5rem;
    color: var(--text-secondary);
    cursor: pointer;
    transition: all 0.15s;
    background: var(--bg-secondary);
  }

  .dropzone:hover,
  .dropzone.active {
    border-color: var(--accent);
    color: var(--accent);
    background: rgba(59, 130, 246, 0.05);
  }

  .dropzone.uploading {
    border-color: var(--warning);
    color: var(--warning);
  }

  .dropzone-text {
    font-size: 0.875rem;
    font-weight: 500;
  }

  .dropzone-hint {
    font-size: 0.75rem;
    color: var(--text-secondary);
    font-family: var(--mono, monospace);
  }

  .loading-bar {
    text-align: center;
    padding: 1rem;
    color: var(--text-secondary);
    font-size: 0.875rem;
  }

  .collection-picker {
    max-width: 400px;
    margin: 1rem auto 0;
  }

  .collection-picker select {
    width: 100%;
  }

  .empty-hint {
    font-size: 0.875rem;
    margin: 0.5rem 0 1rem;
  }

  .modal-actions {
    display: flex;
    justify-content: flex-end;
    gap: 0.75rem;
    margin-top: 1.5rem;
  }

  @media (max-width: 768px) {
    .toolbar {
      flex-direction: column;
      align-items: flex-start;
    }

    .toolbar-actions {
      width: 100%;
      justify-content: flex-start;
    }
  }
</style>
