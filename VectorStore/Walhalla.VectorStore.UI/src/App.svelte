<script lang="ts">
  import { api, type CollectionInfo } from './lib/api'
  import Dashboard from './components/Dashboard.svelte'
  import CollectionsPanel from './components/CollectionsPanel.svelte'
  import SearchPanel from './components/SearchPanel.svelte'
  import VectorsPanel from './components/VectorsPanel.svelte'
  import Toast from './components/Toast.svelte'

  let currentView = $state<'dashboard' | 'collections' | 'search' | 'vectors'>('dashboard')
  let collections = $state<CollectionInfo[]>([])
  let loading = $state(false)
  let error = $state('')
  let sidebarOpen = $state(false)

  function closeSidebar() {
    sidebarOpen = false
  }

  async function loadCollections() {
    loading = true
    error = ''
    try {
      collections = await api.getCollections()
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load collections'
    } finally {
      loading = false
    }
  }

  $effect(() => {
    loadCollections()
    const interval = setInterval(loadCollections, 5000)
    return () => clearInterval(interval)
  })
</script>

<div class="layout">
  <button
    type="button"
    class="sidebar-overlay"
    class:open={sidebarOpen}
    onclick={closeSidebar}
    aria-label="Close navigation menu"
  ></button>
  <aside class="sidebar" class:open={sidebarOpen}>
    <div class="sidebar-header">
      <div class="logo">
        <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="12" cy="12" r="3"/>
          <path d="M12 2v4M12 18v4M4.93 4.93l2.83 2.83M16.24 16.24l2.83 2.83M2 12h4M18 12h4M4.93 19.07l2.83-2.83M16.24 7.76l2.83-2.83"/>
        </svg>
        <span>Walhalla</span>
      </div>
      <div class="version">VectorStore</div>
    </div>

    <nav class="nav">
      <button class="nav-item" class:active={currentView === 'dashboard'} onclick={() => { currentView = 'dashboard'; closeSidebar(); }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/>
          <rect x="14" y="14" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/>
        </svg>
        Dashboard
      </button>
      <button class="nav-item" class:active={currentView === 'collections'} onclick={() => { currentView = 'collections'; closeSidebar(); }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/>
        </svg>
        Collections
        {#if collections.length > 0}
          <span class="nav-badge">{collections.length}</span>
        {/if}
      </button>
      <button class="nav-item" class:active={currentView === 'search'} onclick={() => { currentView = 'search'; closeSidebar(); }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <circle cx="11" cy="11" r="8"/><path d="m21 21-4.35-4.35"/>
        </svg>
        Search
      </button>
      <button class="nav-item" class:active={currentView === 'vectors'} onclick={() => { currentView = 'vectors'; closeSidebar(); }}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
          <polyline points="14 2 14 8 20 8"/>
          <line x1="16" y1="13" x2="8" y2="13"/>
          <line x1="16" y1="17" x2="8" y2="17"/>
          <polyline points="10 9 9 9 8 9"/>
        </svg>
        Vectors
      </button>
    </nav>

    <div class="sidebar-footer">
      <div class="status" class:online={!error} class:offline={!!error}>
        <span class="status-dot"></span>
        {error ? 'Disconnected' : 'Connected'}
      </div>
    </div>
  </aside>

  <main class="main">
    <button class="mobile-menu-btn" onclick={() => sidebarOpen = !sidebarOpen} aria-label="Toggle menu">
      <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
        <line x1="3" y1="12" x2="21" y2="12"/>
        <line x1="3" y1="6" x2="21" y2="6"/>
        <line x1="3" y1="18" x2="21" y2="18"/>
      </svg>
    </button>
    {#if currentView === 'dashboard'}
      <Dashboard {collections} {loading} />
    {:else if currentView === 'collections'}
      <CollectionsPanel {collections} onRefresh={loadCollections} />
    {:else if currentView === 'search'}
      <SearchPanel {collections} />
    {:else if currentView === 'vectors'}
      <VectorsPanel {collections} onRefresh={loadCollections} />
    {/if}
  </main>
</div>

<Toast />

<style>
  .layout {
    display: flex;
    height: 100vh;
    width: 100vw;
  }

  .sidebar {
    width: 240px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    flex-shrink: 0;
  }

  .sidebar-header {
    padding: 1.25rem;
    border-bottom: 1px solid var(--border);
  }

  .logo {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    color: var(--accent);
    font-size: 1.25rem;
    font-weight: 700;
  }

  .version {
    font-size: 0.75rem;
    color: var(--text-secondary);
    margin-top: 0.25rem;
    margin-left: 2.5rem;
  }

  .nav {
    flex: 1;
    padding: 0.75rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .nav-item {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.625rem 0.875rem;
    border: none;
    border-radius: var(--radius);
    background: transparent;
    color: var(--text-secondary);
    font-size: 0.875rem;
    cursor: pointer;
    transition: all 0.15s;
    text-align: left;
    position: relative;
  }

  .nav-item:hover {
    background: var(--bg-tertiary);
    color: var(--text-primary);
  }

  .nav-item.active {
    background: var(--accent);
    color: white;
  }

  .nav-badge {
    margin-left: auto;
    background: var(--bg-tertiary);
    color: var(--text-secondary);
    padding: 0.125rem 0.5rem;
    border-radius: 9999px;
    font-size: 0.75rem;
    font-weight: 500;
  }

  .nav-item.active .nav-badge {
    background: rgba(255,255,255,0.2);
    color: white;
  }

  .sidebar-footer {
    padding: 1rem;
    border-top: 1px solid var(--border);
  }

  .status {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.75rem;
    color: var(--text-secondary);
  }

  .status-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--danger);
  }

  .status.online .status-dot {
    background: var(--success);
  }

  .main {
    flex: 1;
    overflow: auto;
    padding: 1.5rem;
  }

  .mobile-menu-btn {
    display: none;
    background: none;
    border: none;
    color: var(--text-primary);
    cursor: pointer;
    padding: 0.5rem;
    margin-bottom: 0.75rem;
  }

  @media (max-width: 768px) {
    .mobile-menu-btn {
      display: block;
    }

    .sidebar {
      position: fixed;
      left: 0;
      top: 0;
      bottom: 0;
      z-index: 100;
      transform: translateX(-100%);
      transition: transform 0.2s ease;
    }

    .sidebar.open {
      transform: translateX(0);
    }

    .main {
      padding: 1rem;
      width: 100vw;
    }
  }
</style>
