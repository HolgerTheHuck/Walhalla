// API client for Walhalla.VectorStore REST API

export interface CollectionInfo {
  name: string;
  dimension: number;
  metric: 'Euclidean' | 'Cosine' | 'DotProduct';
  count: number;
  hnswEnabled: boolean;
}

export interface VectorEntry {
  id: number;
  dimension: number;
  vector?: number[];
  metadata?: Record<string, unknown>;
}

export interface SearchResult {
  id: number;
  score: number;
  metadata?: Record<string, unknown>;
}

export type FullTextQueryMode = 'all' | 'any';

export interface CreateCollectionRequest {
  name: string;
  dimension: number;
  metric: 'Euclidean' | 'Cosine' | 'DotProduct';
  enableHnsw?: boolean;
}

export interface PutVectorRequest {
  id: number;
  vector: number[];
  metadata?: Record<string, unknown>;
}

export interface SearchRequest {
  vector: number[];
  topK: number;
  ef?: number;
  filter?: Record<string, unknown>;
}

export interface TextSearchRequest {
  field: string;
  query: string;
  topK: number;
  mode?: FullTextQueryMode;
  notQuery?: string;
}

export interface HybridSearchRequest {
  field: string;
  textQuery: string;
  vector: number[];
  topK: number;
  textCandidateCount?: number;
  mode?: FullTextQueryMode;
  notQuery?: string;
}

class ApiClient {
  private baseUrl: string;
  private apiKey: string;

  constructor(baseUrl = '/api', apiKey = 'walhalla-dev-key') {
    this.baseUrl = baseUrl;
    this.apiKey = apiKey;
  }

  private async fetchJson<T>(path: string, options?: RequestInit): Promise<T> {
    const response = await fetch(`${this.baseUrl}${path}`, {
      headers: {
        'Content-Type': 'application/json',
        'X-API-Key': this.apiKey,
      },
      ...options,
    });
    if (!response.ok) {
      const error = await response.text();
      throw new Error(`API error ${response.status}: ${error}`);
    }
    return response.json() as Promise<T>;
  }

  async getCollections(): Promise<CollectionInfo[]> {
    return this.fetchJson<CollectionInfo[]>('/collections');
  }

  async createCollection(req: CreateCollectionRequest): Promise<CollectionInfo> {
    return this.fetchJson<CollectionInfo>('/collections', {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async deleteCollection(name: string): Promise<void> {
    await fetch(`${this.baseUrl}/collections/${encodeURIComponent(name)}`, {
      method: 'DELETE',
      headers: { 'X-API-Key': this.apiKey },
    });
  }

  async getVectors(collection: string, limit = 100, offset = 0): Promise<VectorEntry[]> {
    return this.fetchJson<VectorEntry[]>(
      `/collections/${encodeURIComponent(collection)}/vectors?limit=${limit}&offset=${offset}`
    );
  }

  async putVector(collection: string, req: PutVectorRequest): Promise<void> {
    await this.fetchJson<void>(`/collections/${encodeURIComponent(collection)}/vectors`, {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async getVector(collection: string, id: number): Promise<VectorEntry> {
    return this.fetchJson<VectorEntry>(`/collections/${encodeURIComponent(collection)}/vectors/${id}`);
  }

  async deleteVector(collection: string, id: number): Promise<void> {
    await fetch(`${this.baseUrl}/collections/${encodeURIComponent(collection)}/vectors/${id}`, {
      method: 'DELETE',
      headers: { 'X-API-Key': this.apiKey },
    });
  }

  async putVectorsBulk(
    collection: string,
    requests: PutVectorRequest[],
    batchSize = 100
  ): Promise<{ imported: number; failed: number; errors: string[] }> {
    let imported = 0;
    let failed = 0;
    const errors: string[] = [];

    for (let i = 0; i < requests.length; i += batchSize) {
      const batch = requests.slice(i, i + batchSize);
      const results = await Promise.allSettled(
        batch.map(req => this.putVector(collection, req))
      );
      results.forEach((r, idx) => {
        if (r.status === 'fulfilled') {
          imported++;
        } else {
          failed++;
          errors.push(`Row ${i + idx + 1}: ${r.reason}`);
        }
      });
    }

    return { imported, failed, errors };
  }

  async searchExact(collection: string, req: SearchRequest): Promise<SearchResult[]> {
    return this.fetchJson<SearchResult[]>(`/collections/${encodeURIComponent(collection)}/search/exact`, {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async searchHnsw(collection: string, req: SearchRequest): Promise<SearchResult[]> {
    return this.fetchJson<SearchResult[]>(`/collections/${encodeURIComponent(collection)}/search/hnsw`, {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async searchText(collection: string, req: TextSearchRequest): Promise<SearchResult[]> {
    return this.fetchJson<SearchResult[]>(`/collections/${encodeURIComponent(collection)}/search/text`, {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async searchHybrid(collection: string, req: HybridSearchRequest): Promise<SearchResult[]> {
    return this.fetchJson<SearchResult[]>(`/collections/${encodeURIComponent(collection)}/search/hybrid`, {
      method: 'POST',
      body: JSON.stringify(req),
    });
  }

  async getStats(): Promise<{ collections: number; totalVectors: number; uptime: string }> {
    return this.fetchJson('/stats');
  }
}

export const api = new ApiClient();
