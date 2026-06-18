<script lang="ts">
  import { api, type CollectionInfo, type FullTextQueryMode, type SearchResult } from '../lib/api'
  import { showToast } from './Toast.svelte'
  import { selectedCollection } from '../lib/stores'

  interface Props {
    collections: CollectionInfo[]
  }

  let { collections }: Props = $props()

  let queryVector = $state('')
  let textField = $state('body')
  let textQuery = $state('')
  let notQuery = $state('')
  let topK = $state(10)
  let ef = $state(100)
  let textCandidateCount = $state(50)
  let textMode = $state<FullTextQueryMode>('all')
  let searchType = $state<'exact' | 'hnsw' | 'text' | 'hybrid'>('hnsw')
  let searching = $state(false)
  let results = $state<SearchResult[]>([])
  let searchTime = $state(0)
  let showFilter = $state(false)
  let filterJson = $state('')

  let requiresVector = $derived(searchType === 'exact' || searchType === 'hnsw' || searchType === 'hybrid')
  let supportsTextQuery = $derived(searchType === 'text' || searchType === 'hybrid')
  let supportsFilter = $derived(searchType === 'exact' || searchType === 'hnsw')
  let searchModeHint = $derived(
    searchType === 'text'
      ? 'Phrase-aware text search over a metadata field'
      : searchType === 'hybrid'
        ? 'Text narrows the candidate set and vector distance reranks the final hits'
        : 'Vector similarity search over the selected collection'
  )
  let scoreLabel = $derived(searchType === 'text' ? 'Relevance' : 'Distance')
  let actionLabel = $derived(
    searchType === 'text'
      ? 'Search Text'
      : searchType === 'hybrid'
        ? 'Run Hybrid Search'
        : 'Search'
  )

  function parseVector(input: string): number[] | null {
    try {
      const parsed = JSON.parse(input)
      if (Array.isArray(parsed) && parsed.every(v => typeof v === 'number')) {
        return parsed
      }
      // Try comma-separated
      const comma = input.split(',').map(s => parseFloat(s.trim())).filter(n => !isNaN(n))
      if (comma.length > 0) return comma
      return null
    } catch {
      const comma = input.split(',').map(s => parseFloat(s.trim())).filter(n => !isNaN(n))
      if (comma.length > 0) return comma
      return null
    }
  }

  async function performSearch() {
    if (!$selectedCollection) {
      showToast('Please select a collection', 'error')
      return
    }

    const collection = collections.find(c => c.name === $selectedCollection)
    let vector: number[] | undefined

    if (requiresVector) {
      const parsedVector = parseVector(queryVector)
      if (!parsedVector || parsedVector.length === 0) {
        showToast('Please enter a valid query vector', 'error')
        return
      }

      vector = parsedVector

      if (collection && vector.length !== collection.dimension) {
        showToast(`Vector dimension mismatch: expected ${collection.dimension}, got ${vector.length}`, 'error')
        return
      }
    }

    const normalizedField = textField.trim()
    const normalizedTextQuery = textQuery.trim()
    const normalizedNotQuery = notQuery.trim() || undefined
    if (supportsTextQuery && (!normalizedField || !normalizedTextQuery)) {
      showToast('Please enter a payload field and a text query', 'error')
      return
    }

    let filter: Record<string, unknown> | undefined = undefined
    if (supportsFilter && showFilter && filterJson.trim()) {
      try {
        filter = JSON.parse(filterJson.trim())
      } catch {
        showToast('Invalid filter JSON', 'error')
        return
      }
    }

    searching = true
    results = []
    const start = performance.now()

    try {
      if (searchType === 'exact') {
        results = await api.searchExact($selectedCollection, { vector: vector!, topK, filter })
      } else if (searchType === 'hnsw') {
        results = await api.searchHnsw($selectedCollection, { vector: vector!, topK, ef, filter })
      } else if (searchType === 'text') {
        results = await api.searchText($selectedCollection, {
          field: normalizedField,
          query: normalizedTextQuery,
          topK,
          mode: textMode,
          notQuery: normalizedNotQuery,
        })
      } else {
        results = await api.searchHybrid($selectedCollection, {
          field: normalizedField,
          textQuery: normalizedTextQuery,
          vector: vector!,
          topK,
          textCandidateCount: Math.max(textCandidateCount, topK),
          mode: textMode,
          notQuery: normalizedNotQuery,
        })
      }

      searchTime = performance.now() - start
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Search failed', 'error')
    } finally {
      searching = false
    }
  }

  function generateRandomVector(dim: number): string {
    const vec = Array.from({ length: dim }, () => (Math.random() * 2 - 1).toFixed(6))
    return `[${vec.join(', ')}]`
  }

  function fillRandom() {
    const collection = collections.find(c => c.name === $selectedCollection)
    if (collection) {
      queryVector = generateRandomVector(collection.dimension)
    }
  }

  function scoreTone(score: number): '' | 'good' | 'medium' {
    if (searchType === 'text') {
      if (score >= 1.5) return 'good'
      if (score >= 0.5) return 'medium'
      return ''
    }

    if (score < 0.3) return 'good'
    if (score < 0.7) return 'medium'
    return ''
  }
</script>

<div class="search-panel">
  <header class="page-header">
    <div>
      <h1>Search</h1>
      <p class="subtitle">{searchModeHint}</p>
    </div>
  </header>

  <div class="search-form card">
    <div class="form-row">
      <div class="form-group">
        <label for="search-collection">Collection</label>
        <select id="search-collection" value={$selectedCollection} onchange={(e) => selectedCollection.set((e.target as HTMLSelectElement).value)}>
          <option value="">Select a collection...</option>
          {#each collections as c (c.name)}
            <option value={c.name}>{c.name} ({c.dimension}d, {c.metric})</option>
          {/each}
        </select>
      </div>

      <div class="form-group">
        <div class="group-label">Search Type</div>
        <div class="toggle-group search-type-toggle">
          <button
            class="toggle-btn"
            class:active={searchType === 'hnsw'}
            onclick={() => searchType = 'hnsw'}
          >
            HNSW
          </button>
          <button
            class="toggle-btn"
            class:active={searchType === 'exact'}
            onclick={() => searchType = 'exact'}
          >
            Exact
          </button>
          <button
            class="toggle-btn"
            class:active={searchType === 'text'}
            onclick={() => searchType = 'text'}
          >
            Text
          </button>
          <button
            class="toggle-btn"
            class:active={searchType === 'hybrid'}
            onclick={() => searchType = 'hybrid'}
          >
            Hybrid
          </button>
        </div>
      </div>
    </div>

    {#if supportsTextQuery}
      <div class="form-row">
        <div class="form-group form-group-medium">
          <label for="search-text-field">Payload Field</label>
          <input id="search-text-field" bind:value={textField} placeholder="body" />
          <p class="field-help">Metadata string field to search in.</p>
        </div>

        <div class="form-group form-group-mode">
          <div class="group-label">Text Mode</div>
          <div class="toggle-group mode-toggle">
            <button
              class="toggle-btn"
              class:active={textMode === 'all'}
              onclick={() => textMode = 'all'}
            >
              All
            </button>
            <button
              class="toggle-btn"
              class:active={textMode === 'any'}
              onclick={() => textMode = 'any'}
            >
              Any
            </button>
          </div>
          <p class="field-help">Quotes enable phrase search in both query and not query.</p>
        </div>
      </div>

      <div class="form-row">
        <div class="form-group">
          <label for="search-text-query">{searchType === 'hybrid' ? 'Text Gate' : 'Text Query'}</label>
          <textarea
            id="search-text-query"
            bind:value={textQuery}
            placeholder='shared "agent memory"'
            rows="2"
          ></textarea>
        </div>
      </div>

      <div class="form-row">
        <div class="form-group">
          <label for="search-not-query">Exclude Terms</label>
          <input id="search-not-query" bind:value={notQuery} placeholder='private draft "old version"' />
          <p class="field-help">Optional negative query. Matching hits are removed before ranking.</p>
        </div>

        {#if searchType === 'hybrid'}
          <div class="form-group form-group-small">
            <label for="search-text-candidates">Text Candidates</label>
            <input id="search-text-candidates" type="number" bind:value={textCandidateCount} min="1" max="500" />
            <p class="field-help">Hybrid reranks this many text hits with vector distance.</p>
          </div>
        {/if}
      </div>
    {/if}

    {#if requiresVector}
      <div class="form-row">
        <div class="form-group">
          <label for="search-vector-query">{searchType === 'hybrid' ? 'Vector Query' : 'Query Vector'}</label>
          <div class="vector-input-wrapper">
            <textarea
              id="search-vector-query"
              bind:value={queryVector}
              placeholder="[0.1, 0.2, 0.3, ...] or comma-separated values"
              rows="3"
            ></textarea>
            <button class="btn btn-secondary btn-small" onclick={fillRandom} title="Generate random vector">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                <path d="M16 3h5v5M4 20L21 3M21 16v5h-5M15 15l-6 6M4 4l5 5"/>
              </svg>
              Random
            </button>
          </div>
        </div>
      </div>
    {/if}

    <div class="form-row">
      <div class="form-group form-group-small">
        <label for="search-topk">Top K</label>
        <input id="search-topk" type="number" bind:value={topK} min="1" max="100" />
      </div>

      {#if searchType === 'hnsw'}
        <div class="form-group form-group-small">
          <label for="search-ef">EF</label>
          <input id="search-ef" type="number" bind:value={ef} min="1" max="1000" />
        </div>
      {/if}

      <div class="form-group form-group-actions">
        <button class="btn btn-primary" onclick={performSearch} disabled={searching || !$selectedCollection}>
          {#if searching}
            <svg class="spinner" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M21 12a9 9 0 1 1-6.219-8.56" />
            </svg>
            Searching...
          {:else}
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <circle cx="11" cy="11" r="8"/><path d="m21 21-4.35-4.35"/>
            </svg>
            {actionLabel}
          {/if}
        </button>
      </div>
    </div>

    {#if supportsFilter}
      <div class="form-row">
        <div class="form-group">
          <button class="btn btn-secondary btn-small" onclick={() => showFilter = !showFilter}>
            {showFilter ? 'Hide Filter' : 'Advanced Filter'}
          </button>
        </div>
      </div>
    {:else}
      <div class="form-row">
        <div class="form-group">
          <div class="search-note">
            Text and hybrid search currently target the full-text endpoints directly. Metadata filters remain available on Exact and HNSW search.
          </div>
        </div>
      </div>
    {/if}

    {#if supportsFilter && showFilter}
      <div class="form-row">
        <div class="form-group">
          <label for="search-filter-json">Metadata Filter (JSON)</label>
          <textarea
            id="search-filter-json"
            bind:value={filterJson}
            placeholder={'{"must":[{"key":"category","match":{"value":"pdf"}}]}'}
            rows="4"
          ></textarea>
        </div>
      </div>
    {/if}
  </div>

  {#if results.length > 0}
    <div class="results-section">
      <div class="results-header">
        <h2>Results</h2>
        <span class="results-meta">{results.length} results in {searchTime.toFixed(1)}ms</span>
      </div>

      <div class="results-table-wrapper">
        <table class="table">
          <thead>
            <tr>
              <th>Rank</th>
              <th>ID</th>
              <th>{scoreLabel}</th>
              <th>Metadata</th>
            </tr>
          </thead>
          <tbody>
            {#each results as result, i (result.id)}
              <tr>
                <td><span class="rank">#{i + 1}</span></td>
                <td>{result.id}</td>
                <td>
                  <span class="score" class:good={scoreTone(result.score) === 'good'} class:medium={scoreTone(result.score) === 'medium'}>
                    {result.score.toFixed(6)}
                  </span>
                </td>
                <td>
                  {#if result.metadata}
                    <pre class="metadata">{JSON.stringify(result.metadata, null, 2)}</pre>
                  {:else}
                    <span class="no-meta">—</span>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    </div>
  {:else if !searching && searchTime > 0}
    <div class="card empty-state">
      <div class="empty-state-icon">🔍</div>
      <p>No results found</p>
    </div>
  {/if}
</div>

<style>
  .search-panel {
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

  .search-form {
    margin-bottom: 1.5rem;
  }

  .form-row {
    display: flex;
    gap: 1rem;
    margin-bottom: 1rem;
  }

  .form-row:last-child {
    margin-bottom: 0;
  }

  .form-group {
    flex: 1;
  }

  .form-group-small {
    flex: 0 0 120px;
  }

  .form-group-medium {
    flex: 0 0 220px;
  }

  .form-group-mode {
    flex: 0 0 220px;
  }

  .form-group-actions {
    flex: 0 0 auto;
    display: flex;
    align-items: flex-end;
  }

  .vector-input-wrapper {
    display: flex;
    gap: 0.5rem;
    align-items: flex-start;
  }

  .vector-input-wrapper textarea {
    flex: 1;
    font-family: var(--mono, monospace);
    font-size: 0.8rem;
  }

  .btn-small {
    padding: 0.5rem;
    font-size: 0.75rem;
    white-space: nowrap;
  }

  .toggle-group {
    display: flex;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .group-label {
    display: block;
    margin-bottom: 0.25rem;
    font-size: 0.875rem;
    color: var(--text-secondary);
  }

  .search-type-toggle {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }

  .mode-toggle {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .toggle-btn {
    flex: 1;
    padding: 0.5rem 1rem;
    border: none;
    background: var(--bg-primary);
    color: var(--text-secondary);
    font-size: 0.875rem;
    cursor: pointer;
    transition: all 0.15s;
  }

  .toggle-btn:hover {
    color: var(--text-primary);
  }

  .toggle-btn.active {
    background: var(--accent);
    color: white;
  }

  .field-help {
    margin-top: 0.4rem;
    color: var(--text-secondary);
    font-size: 0.75rem;
  }

  .search-note {
    padding: 0.75rem 0.9rem;
    border: 1px dashed rgba(59, 130, 246, 0.4);
    border-radius: var(--radius);
    background: rgba(59, 130, 246, 0.08);
    color: var(--text-secondary);
    font-size: 0.8rem;
  }

  .results-section {
    margin-top: 1.5rem;
  }

  .results-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1rem;
  }

  .results-header h2 {
    font-size: 1.25rem;
    font-weight: 600;
  }

  .results-meta {
    font-size: 0.875rem;
    color: var(--text-secondary);
  }

  .results-table-wrapper {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .rank {
    font-weight: 600;
    color: var(--accent);
  }

  .score {
    font-family: var(--mono, monospace);
    font-size: 0.875rem;
  }

  .score.good {
    color: var(--success);
  }

  .score.medium {
    color: var(--warning);
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
  }

  .no-meta {
    color: var(--text-secondary);
  }

  .spinner {
    animation: spin 1s linear infinite;
  }

  @keyframes spin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
  }
</style>
