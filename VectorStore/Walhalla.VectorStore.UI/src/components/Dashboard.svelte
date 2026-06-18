<script lang="ts">
  import type { CollectionInfo } from '../lib/api'

  interface Props {
    collections: CollectionInfo[]
    loading: boolean
  }

  let { collections, loading }: Props = $props()

  let totalVectors = $derived(collections.reduce((sum, c) => sum + c.count, 0))
  let hnswCollections = $derived(collections.filter(c => c.hnswEnabled).length)
</script>

<div class="dashboard">
  <header class="page-header">
    <h1>Dashboard</h1>
    <p class="subtitle">Overview of your vector store</p>
  </header>

  <div class="stats-grid">
    <div class="stat-card">
      <div class="stat-icon" style="color: var(--accent)">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/>
        </svg>
      </div>
      <div class="stat-value">{collections.length}</div>
      <div class="stat-label">Collections</div>
    </div>

    <div class="stat-card">
      <div class="stat-icon" style="color: var(--success)">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M12 2v20M2 12h20"/>
        </svg>
      </div>
      <div class="stat-value">{totalVectors.toLocaleString()}</div>
      <div class="stat-label">Total Vectors</div>
    </div>

    <div class="stat-card">
      <div class="stat-icon" style="color: var(--warning)">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/>
        </svg>
      </div>
      <div class="stat-value">{hnswCollections}</div>
      <div class="stat-label">HNSW Indexed</div>
    </div>

    <div class="stat-card">
      <div class="stat-icon" style="color: var(--accent)">
        <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>
        </svg>
      </div>
      <div class="stat-value">Active</div>
      <div class="stat-label">Status</div>
    </div>
  </div>

  <div class="section">
    <div class="section-header">
      <h2>Collections</h2>
      {#if loading}
        <span class="loading">Refreshing...</span>
      {/if}
    </div>

    {#if collections.length === 0}
      <div class="card empty-state">
        <div class="empty-state-icon">📦</div>
        <p>No collections yet</p>
        <p class="empty-hint">Create your first collection to start storing vectors</p>
      </div>
    {:else}
      <div class="collections-grid">
        {#each collections as collection}
          <div class="collection-card">
            <div class="collection-header">
              <h3>{collection.name}</h3>
              {#if collection.hnswEnabled}
                <span class="badge badge-success">HNSW</span>
              {:else}
                <span class="badge badge-warning">No Index</span>
              {/if}
            </div>
            <div class="collection-stats">
              <div class="collection-stat">
                <span class="collection-stat-value">{collection.count.toLocaleString()}</span>
                <span class="collection-stat-label">vectors</span>
              </div>
              <div class="collection-stat">
                <span class="collection-stat-value">{collection.dimension}</span>
                <span class="collection-stat-label">dim</span>
              </div>
              <div class="collection-stat">
                <span class="collection-stat-value">{collection.metric}</span>
                <span class="collection-stat-label">metric</span>
              </div>
            </div>
          </div>
        {/each}
      </div>
    {/if}
  </div>
</div>

<style>
  .dashboard {
    max-width: 1200px;
  }

  .page-header {
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

  .stats-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 1rem;
    margin-bottom: 2rem;
  }

  .stat-card {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .stat-icon {
    margin-bottom: 0.25rem;
  }

  .stat-value {
    font-size: 1.75rem;
    font-weight: 700;
    color: var(--text-primary);
  }

  .stat-label {
    font-size: 0.875rem;
    color: var(--text-secondary);
  }

  .section {
    margin-top: 2rem;
  }

  .section-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
  }

  .section-header h2 {
    font-size: 1.25rem;
    font-weight: 600;
  }

  .loading {
    font-size: 0.875rem;
    color: var(--text-secondary);
  }

  .collections-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 1rem;
  }

  .collection-card {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1.25rem;
    transition: border-color 0.15s;
  }

  .collection-card:hover {
    border-color: var(--accent);
  }

  .collection-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
  }

  .collection-header h3 {
    font-size: 1rem;
    font-weight: 600;
  }

  .collection-stats {
    display: flex;
    gap: 1.5rem;
  }

  .collection-stat {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .collection-stat-value {
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--text-primary);
  }

  .collection-stat-label {
    font-size: 0.75rem;
    color: var(--text-secondary);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }

  .empty-hint {
    font-size: 0.875rem;
    margin-top: 0.5rem;
  }
</style>
