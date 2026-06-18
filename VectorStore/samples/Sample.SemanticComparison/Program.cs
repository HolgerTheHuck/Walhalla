// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Embeddings;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Indexes;

// ═══════════════════════════════════════════════════════════════════════════════
// Sample.SemanticComparison – Walhalla vs Qdrant mit LM Studio Embeddings
// ═══════════════════════════════════════════════════════════════════════════════
// Nutzt text-embedding-nomic-embed-text-v1.5 (768 Dim) auf LM Studio,
// generiert ~1000+ Dokumente, speichert identische Vektoren in Walhalla
// und Qdrant und vergleicht semantische Suchergebnisse ueber Exact- und
// HNSW-Suche inkl. Recall, Übereinstimmung und Rank-Differenz.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Konfiguration ─────────────────────────────────────────────────────────────
var endpoint       = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT")        ?? "http://localhost:1234/v1";
var apiKey         = Environment.GetEnvironmentVariable("OPENAI_API_KEY")          ?? "not-needed";
var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL")  ?? "text-embedding-nomic-embed-text-v1.5";
var embeddingDim   = int.TryParse(Environment.GetEnvironmentVariable("EMBEDDING_DIMENSION"), out var d) ? d : 768;

var qdrantHost     = Environment.GetEnvironmentVariable("QDRANT_HOST")              ?? "localhost";
var qdrantPort     = int.TryParse(Environment.GetEnvironmentVariable("QDRANT_PORT"), out var qp) ? qp : 6334;

const int TopK = 10;
const int DocumentCount = 10000;

var hnswConfigs = new[]
{
    (M: 16,  EfConstruction: 200, EfSearch: 64,  Label: "M16_EF200_EF64"),
    (M: 16,  EfConstruction: 200, EfSearch: 128, Label: "M16_EF200_EF128"),
    (M: 16,  EfConstruction: 200, EfSearch: 256, Label: "M16_EF200_EF256"),
    (M: 32,  EfConstruction: 200, EfSearch: 64,  Label: "M32_EF200_EF64"),
    (M: 32,  EfConstruction: 400, EfSearch: 128, Label: "M32_EF400_EF128"),
    (M: 64,  EfConstruction: 400, EfSearch: 128, Label: "M64_EF400_EF128"),
    (M: 64,  EfConstruction: 400, EfSearch: 256, Label: "M64_EF400_EF256"),
};

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Semantischer Vergleich: Walhalla.VectorStore vs Qdrant");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"LM Studio Endpoint:  {endpoint}");
Console.WriteLine($"Embedding Model:     {embeddingModel}");
Console.WriteLine($"Embedding Dimension: {embeddingDim}");
Console.WriteLine($"Qdrant:              {qdrantHost}:{qdrantPort}");
Console.WriteLine($"Dokumente:           {DocumentCount:N0}");
Console.WriteLine($"Top-K:               {TopK}");
Console.WriteLine();

// ── Embedding Client ──────────────────────────────────────────────────────────
var openAIOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
var openAIClient  = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), openAIOptions);
var embeddingClient = openAIClient.GetEmbeddingClient(embeddingModel);

// ── Dokumentengenerator ──────────────────────────────────────────────────────
static List<(string Category, string Text)> GenerateDocuments(int count)
{
    var rnd = new Random(42);
    var categories = new[]
    {
        ("Technik", new[]
        {
            "KI-Modell",
            "GPU",
            "CPU",
            "Quantencomputer",
            "Cloud-Dienst",
            "Smartphone",
            "Roboter",
            "Blockchain",
            "neuronales Netzwerk",
            "5G-Netzwerk"
        }),
        ("Natur", new[]
        {
            "Regenwald",
            "Korallenriff",
            "Vulkan",
            "Gletscher",
            "Tiefsee",
            "Savanne",
            "Arktis",
            "Wüste",
            "Waldbrand",
            "Hurrikan"
        }),
        ("Geschichte", new[]
        {
            "Revolution",
            "Mondlandung",
            "Krieg",
            "Entdeckung",
            "Dynastie",
            "Kaiserreich",
            "Kolonialzeit",
            "Wiedervereinigung",
            "Weltwirtschaftskrise",
            "Goldrausch"
        }),
        ("Sport", new[]
        {
            "Fußball",
            "Basketball",
            "Tennis",
            "Leichtathletik",
            "Schwimmen",
            "Radrennen",
            "Boxen",
            "Golf",
            "Eishockey",
            "Formel 1"
        }),
        ("Essen", new[]
        {
            "Pizza",
            "Sushi",
            "Pasta",
            "Steak",
            "Salat",
            "Suppe",
            "Dessert",
            "Brot",
            "Käse",
            "Schokolade"
        }),
        ("Wissenschaft", new[]
        {
            "Genetik",
            "Astronomie",
            "Chemie",
            "Physik",
            "Biologie",
            "Mathematik",
            "Medizin",
            "Psychologie",
            "Archäologie",
            "Ozeanografie"
        }),
        ("Kunst", new[]
        {
            "Malerei",
            "Skulptur",
            "Musik",
            "Literatur",
            "Film",
            "Fotografie",
            "Ballett",
            "Theater",
            "Street Art",
            "Oper"
        }),
        ("Wirtschaft", new[]
        {
            "Aktienmarkt",
            "Inflation",
            "Startup",
            "Handel",
            "Investition",
            "Kryptowährung",
            "Immobilien",
            "Automobilindustrie",
            "Energiekrise",
            "Arbeitsmarkt"
        })
    };

    var verbs = new[] { "revolutioniert", "beeinflusst", "verändert", "dominiert", "wächst", "schwindet", "erobert", "bedroht", "fördert", "verbindet" };
    var adj = new[] { "schnell", "effizient", "nachhaltig", "global", "lokal", "digital", "traditionell", "innovativ", "kritisch", "optimistisch" };
    var contexts = new[] { "in Europa", "weltweit", "in der Forschung", "für Verbraucher", "im Alltag", "langfristig", "kurzfristig", "im Vergleich", "historisch", "zukünftig" };

    var docs = new List<(string, string)>();
    for (int i = 0; i < count; i++)
    {
        var cat = categories[rnd.Next(categories.Length)];
        var subject = cat.Item2[rnd.Next(cat.Item2.Length)];
        var verb = verbs[rnd.Next(verbs.Length)];
        var adjective = adj[rnd.Next(adj.Length)];
        var context = contexts[rnd.Next(contexts.Length)];

        // Generiere einen einigermassen sinnvollen Satz
        var templates = new[]
        {
            $"Das Thema {subject} {verb} {context} die Entwicklung in {adjective}er Weise.",
            $"{subject} wird {adjective} {context} als Katalysator für neue Ideen verstanden.",
            $"Die Diskussion um {subject} zeigt {context} {adjective}e Potenziale und Risiken.",
            $"Experten analysieren, wie {subject} {context} {verb} und {adjective}e Ergebnisse liefert.",
            $"{context} gewinnt {subject} durch {adjective}e Ansätze an Bedeutung.",
            $"{subject} und seine {adjective}e Anwendung {verb} {context} den Markt.",
            $"Studien belegen: {subject} wirkt {context} {adjective} auf die Gesellschaft.",
            $"{subject} bleibt {context} ein {adjective}es Schlagwort mit vielen Deutungen.",
            $"Die Zukunft von {subject} erscheint {context} {adjective} und voller Chancen.",
            $"{adjective}e Perspektiven auf {subject} {verb} {context} die Debatte."
        };

        var text = templates[rnd.Next(templates.Length)];
        docs.Add((cat.Item1, text));
    }
    return docs;
}

var documents = GenerateDocuments(DocumentCount);

var queries = new[]
{
    "Welche technologischen Neuheiten gibt es in der KI?",
    "Wie geht es dem Regenwald und dem Klima?",
    "Wer hat historische Entdeckungen gemacht?",
    "Erzähle mir etwas über Sportrekorde.",
    "Was gibt es Interessantes über Essen und Kultur?",
    "Wie entwickelt sich die Wirtschaft global?",
    "Welche wissenschaftlichen Durchbrüche gibt es?",
    "Was ist aktuell in der Kunstszene los?"
};

// ── Embeddings erzeugen ─────────────────────────────────────────────────────
Console.WriteLine("[1] Erzeuge Embeddings über LM Studio...");
var docEmbeddings = new List<float[]>();
for (int i = 0; i < documents.Count; i++)
{
    var emb = await GetEmbeddingAsync(embeddingClient, documents[i].Text);
    docEmbeddings.Add(emb);
    if ((i + 1) % 50 == 0 || i == documents.Count - 1)
        Console.Write($"\r    Dokumente: {i + 1}/{documents.Count}");
}
Console.WriteLine();

var queryEmbeddings = new List<float[]>();
for (int i = 0; i < queries.Length; i++)
{
    var emb = await GetEmbeddingAsync(embeddingClient, queries[i]);
    queryEmbeddings.Add(emb);
    Console.Write($"\r    Queries: {i + 1}/{queries.Length}");
}
Console.WriteLine();

// ── Walhalla initialisieren ─────────────────────────────────────────────────
var walhallaPath = Path.Combine(Path.GetTempPath(), $"walhalla_semantic_{Guid.NewGuid():N}");
Directory.CreateDirectory(walhallaPath);
using var walhallaStore = new EmbeddedVectorStore(walhallaPath);
var walhallaCollection = walhallaStore.GetOrCreateCollection(
    name:        "semantic_comparison",
    dimension:   embeddingDim,
    metric:      DistanceMetric.Cosine,
    enableHnsw:  true,
    hnswOptions: new HnswOptions { M = 16, EfConstruction = 200 });

// ── Qdrant initialisieren ───────────────────────────────────────────────────
var qdrantClient = new QdrantClient(qdrantHost, qdrantPort, https: false);
var qdrantCollectionName = $"semantic_comparison_{Guid.NewGuid():N}";
try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
await qdrantClient.CreateCollectionAsync(
    qdrantCollectionName,
    new VectorParams { Size = (ulong)embeddingDim, Distance = Distance.Cosine },
    hnswConfig: new HnswConfigDiff { M = 16, EfConstruct = 200 });

// ── Daten einfügen ──────────────────────────────────────────────────────────
Console.WriteLine("[2] Füge Dokumente in Walhalla und Qdrant ein...");
var qdrantPoints = new List<PointStruct>();
for (int i = 0; i < documents.Count; i++)
{
    var id = (ulong)(i + 1);
    var (category, text) = documents[i];

    // Walhalla
    await walhallaCollection.UpsertAsync(id, new Walhalla.VectorStore.Vector(docEmbeddings[i]), new Dictionary<string, object>
    {
        ["category"] = category,
        ["text"] = text
    });

    // Qdrant
    qdrantPoints.Add(new PointStruct
    {
        Id = id,
        Vectors = docEmbeddings[i],
        Payload =
        {
            ["category"] = category,
            ["text"] = text
        }
    });

    if ((i + 1) % 100 == 0 || i == documents.Count - 1)
        Console.Write($"\r    Eingefügt: {i + 1}/{documents.Count}");
}

await walhallaStore.CheckpointAsync();
await qdrantClient.UpsertAsync(qdrantCollectionName, qdrantPoints);
Console.WriteLine($"\r    {documents.Count:N0} Dokumente eingefügt.");

// ── Suchvergleich ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Suchvergleich");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

var allWRecall = new List<double>();
var allQRecall = new List<double>();
var allWExactQExact = new List<double>();
var allWHnswQHnsw = new List<double>();
var allWExactQHnsw = new List<double>();

for (int qi = 0; qi < queries.Length; qi++)
{
    var queryText = queries[qi];
    var queryVector = new Walhalla.VectorStore.Vector(queryEmbeddings[qi]);

    Console.WriteLine();
    Console.WriteLine($"── Query {qi + 1}: \"{queryText}\" ──");

    // Walhalla Suchen
    var wExact = await walhallaCollection.SearchExactAsync(queryVector, TopK).ToListAsync();
    var wHnsw  = await walhallaCollection.SearchHnswAsync(queryVector, TopK, ef: 64).ToListAsync();

    // Qdrant Suchen
    var qExact = await qdrantClient.SearchAsync(
        qdrantCollectionName, queryEmbeddings[qi], limit: (ulong)TopK,
        searchParams: new SearchParams { Exact = true });
    var qHnsw = await qdrantClient.SearchAsync(
        qdrantCollectionName, queryEmbeddings[qi], limit: (ulong)TopK,
        searchParams: new SearchParams { HnswEf = 64 });

    // Ergebnisse formatieren
    var wExactIds = new List<ulong>(wExact.Select(r => r.Id));
    var wHnswIds  = new List<ulong>(wHnsw.Select(r => r.Id));
    var qExactIds = new List<ulong>(qExact.Select(r => r.Id.Num));
    var qHnswIds  = new List<ulong>(qHnsw.Select(r => r.Id.Num));

    var wExactSet = new HashSet<ulong>(wExactIds);
    var wHnswSet  = new HashSet<ulong>(wHnswIds);
    var qExactSet = new HashSet<ulong>(qExactIds);
    var qHnswSet  = new HashSet<ulong>(qHnswIds);

    // Recall: HNSW vs Exact (innerhalb desselben Systems)
    var wRecall = (double)wExactSet.Intersect(wHnswSet).Count() / wExactSet.Count;
    var qRecall = (double)qExactSet.Intersect(qHnswSet).Count() / qExactSet.Count;
    allWRecall.Add(wRecall);
    allQRecall.Add(qRecall);

    // Übereinstimmung zwischen Systemen
    var wExactVsQExact = (double)wExactSet.Intersect(qExactSet).Count() / TopK;
    var wHnswVsQHnsw   = (double)wHnswSet.Intersect(qHnswSet).Count() / TopK;
    var wExactVsQHnsw  = (double)wExactSet.Intersect(qHnswSet).Count() / TopK;
    allWExactQExact.Add(wExactVsQExact);
    allWHnswQHnsw.Add(wHnswVsQHnsw);
    allWExactQHnsw.Add(wExactVsQHnsw);

    // Durchschnittliche Rank-Differenz (nur für gemeinsame IDs)
    var wExactRank = wExactIds.Select((id, idx) => (id, rank: idx)).ToDictionary(x => x.id, x => x.rank);
    var wHnswRank  = wHnswIds.Select((id, idx) => (id, rank: idx)).ToDictionary(x => x.id, x => x.rank);
    var qExactRank = qExactIds.Select((id, idx) => (id, rank: idx)).ToDictionary(x => x.id, x => x.rank);
    var qHnswRank  = qHnswIds.Select((id, idx) => (id, rank: idx)).ToDictionary(x => x.id, x => x.rank);

    double AvgRankDiff(Dictionary<ulong, int> a, Dictionary<ulong, int> b)
    {
        var common = a.Keys.Intersect(b.Keys);
        if (!common.Any()) return double.NaN;
        return common.Average(id => Math.Abs(a[id] - b[id]));
    }

    var wExactVsWHnswRank = AvgRankDiff(wExactRank, wHnswRank);
    var qExactVsQHnswRank = AvgRankDiff(qExactRank, qHnswRank);
    var wExactVsQExactRank = AvgRankDiff(wExactRank, qExactRank);

    // Score-Statistiken
    var wExactScores = wExact.Select(r => r.Score).ToList();
    var wHnswScores  = wHnsw.Select(r => r.Score).ToList();
    var qExactScores = qExact.Select(r => r.Score).ToList();
    var qHnswScores  = qHnsw.Select(r => r.Score).ToList();

    Console.WriteLine();
    Console.WriteLine("  Metriken:");
    Console.WriteLine($"    Walhalla HNSW Recall@{TopK}:              {wRecall:P1}");
    Console.WriteLine($"    Qdrant   HNSW Recall@{TopK}:              {qRecall:P1}");
    Console.WriteLine($"    Übereinstimmung W-Exact / Q-Exact:       {wExactVsQExact:P1}");
    Console.WriteLine($"    Übereinstimmung W-HNSW  / Q-HNSW:        {wHnswVsQHnsw:P1}");
    Console.WriteLine($"    Übereinstimmung W-Exact / Q-HNSW:        {wExactVsQHnsw:P1}");
    Console.WriteLine($"    Avg Rank-Diff W-Exact ↔ W-HNSW:          {wExactVsWHnswRank:F2}");
    Console.WriteLine($"    Avg Rank-Diff Q-Exact ↔ Q-HNSW:          {qExactVsQHnswRank:F2}");
    Console.WriteLine($"    Avg Rank-Diff W-Exact ↔ Q-Exact:         {wExactVsQExactRank:F2}");

    Console.WriteLine();
    Console.WriteLine("  Score-Durchschnitte:");
    Console.WriteLine($"    Walhalla Exact:  {wExactScores.Average():F6} (min {wExactScores.Min():F6}, max {wExactScores.Max():F6})");
    Console.WriteLine($"    Walhalla HNSW:   {wHnswScores.Average():F6} (min {wHnswScores.Min():F6}, max {wHnswScores.Max():F6})");
    Console.WriteLine($"    Qdrant Exact:    {qExactScores.Average():F6} (min {qExactScores.Min():F6}, max {qExactScores.Max():F6})");
    Console.WriteLine($"    Qdrant HNSW:     {qHnswScores.Average():F6} (min {qHnswScores.Min():F6}, max {qHnswScores.Max():F6})");

    // Ergebnis-IDs anzeigen
    Console.WriteLine();
    Console.WriteLine("  Ergebnis-IDs:");
    Console.WriteLine($"    Walhalla Exact:  {string.Join(", ", wExactIds)}");
    Console.WriteLine($"    Walhalla HNSW:   {string.Join(", ", wHnswIds)}");
    Console.WriteLine($"    Qdrant Exact:    {string.Join(", ", qExactIds)}");
    Console.WriteLine($"    Qdrant HNSW:     {string.Join(", ", qHnswIds)}");
}

// ── Gesamtzusammenfassung ────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Gesamtzusammenfassung über alle Queries");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine($"  Walhalla HNSW Recall@{TopK} (Durchschnitt):    {allWRecall.Average():P1} (min {allWRecall.Min():P1}, max {allWRecall.Max():P1})");
Console.WriteLine($"  Qdrant   HNSW Recall@{TopK} (Durchschnitt):    {allQRecall.Average():P1} (min {allQRecall.Min():P1}, max {allQRecall.Max():P1})");
Console.WriteLine();
Console.WriteLine($"  Übereinstimmung W-Exact / Q-Exact (Avg):     {allWExactQExact.Average():P1}");
Console.WriteLine($"  Übereinstimmung W-HNSW  / Q-HNSW  (Avg):     {allWHnswQHnsw.Average():P1}");
Console.WriteLine($"  Übereinstimmung W-Exact / Q-HNSW  (Avg):     {allWExactQHnsw.Average():P1}");
Console.WriteLine();
Console.WriteLine("Hinweise zur Interpretation:");
Console.WriteLine("  • Recall nahe 1,0 bedeutet, dass HNSW fast alle exakten Top-K");
Console.WriteLine("    Treffer findet. Ein Wert über 0,8 gilt als gut.");
Console.WriteLine("  • Übereinstimmung zeigt, wie ähnlich die Ranking-Listen sind.");
Console.WriteLine("    Da beide Systeme Cosine-Distanz nutzen, sollten Exact-Suchen");
Console.WriteLine("    nahezu identische Ergebnisse liefern (Übereinstimmung ~1,0).");
Console.WriteLine("  • Score-Durchschnitte können zwischen den Systemen variieren,");
Console.WriteLine("    da die interne Distanzberechnung leicht unterschiedlich sein kann.");
Console.WriteLine("  • Rank-Differenz = durchschnittliche Positionsverschiebung");
Console.WriteLine("    gemeinsamer IDs zwischen zwei Ranking-Listen.");
Console.WriteLine();

// ── Cleanup ───────────────────────────────────────────────────────────────────
try { await qdrantClient.DeleteCollectionAsync(qdrantCollectionName); } catch { }
walhallaStore.Dispose();
Directory.Delete(walhallaPath, recursive: true);

Console.WriteLine("Cleanup abgeschlossen.");

// ═══════════════════════════════════════════════════════════════════════════════
// Hilfsmethoden
// ═══════════════════════════════════════════════════════════════════════════════

static async Task<float[]> GetEmbeddingAsync(EmbeddingClient client, string text)
{
    var result = await client.GenerateEmbeddingAsync(text);
    return result.Value.ToFloats().ToArray();
}
