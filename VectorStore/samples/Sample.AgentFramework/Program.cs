// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ClientModel;
using System.Text;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using Walhalla.VectorStore;
using Walhalla.VectorStore.Embeddings;
using Walhalla.VectorStore.Embeddings.Onnx;

// ═══════════════════════════════════════════════════════════════
// Sample.AgentFramework – Chat-Agent mit Walhalla Memory
// ═══════════════════════════════════════════════════════════════
// Chat laeuft ueber das Microsoft Agent Framework + LM Studio.
// Das Embedding passiert LOKAL via ONNX (nomic-embed-text-v1.5),
// ohne separaten Embedding-Endpoint. Die Naht "Text rein ->
// Vektor gespeichert/gesucht" kapselt TextVectorCollection:
//   memory.UpsertTextAsync(...) / memory.SearchTextAsync(...)
// Der Embedding-Generator ist eine einzige Zeile und damit gegen
// jeden anderen IEmbeddingGenerator (Remote, OpenAI, Ollama)
// austauschbar.
// ═══════════════════════════════════════════════════════════════

// ── Konfiguration ─────────────────────────────────────────────
var endpoint   = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT")    ?? "http://localhost:1234/v1";
var apiKey     = Environment.GetEnvironmentVariable("OPENAI_API_KEY")      ?? "not-needed";
var chatModel  = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL")   ?? "qwen2.5-7b-instruct";
var memoryTopK = int.TryParse(Environment.GetEnvironmentVariable("MEMORY_TOP_K"), out var k) ? k : 5;

Console.WriteLine("=== Walhalla Chat mit Agent Framework + LM Studio ===");
Console.WriteLine($"Endpoint:    {endpoint}");
Console.WriteLine($"Chat Model:  {chatModel}");
Console.WriteLine($"Memory Top-K:{memoryTopK}");

// ── Lokaler Embedding-Generator (ONNX) ────────────────────────
// Laedt das Modell beim ersten Start nach %LOCALAPPDATA%\Walhalla\models.
var lastPct = -1;
var progress = new Progress<double>(p =>
{
    var pct = (int)p;
    if (pct != lastPct) { lastPct = pct; Console.Write($"\rEmbedding-Modell laden: {pct,3}%"); }
});
using var embeddingGenerator = await OnnxEmbeddingGenerator.CreateAsync(
    new OnnxEmbeddingOptions { Variant = OnnxModelVariant.Quantized },
    progress);
Console.WriteLine("\nGib 'exit' ein zum Beenden.\n");

// ── Chat Agent (LM Studio) ────────────────────────────────────
var openAIOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
var openAIClient  = new OpenAIClient(new ApiKeyCredential(apiKey), openAIOptions);
var agent = openAIClient.GetChatClient(chatModel)
    .AsAIAgent(
        instructions: "Du bist ein hilfreicher Assistent mit Langzeitgedaechtnis." +
                      "Du erinnerst dich an vergangene Unterhaltungen und nutzt diese als Kontext.",
        name: "WalhallaAgent");

// ── Walhalla Memory Store ─────────────────────────────────────
// Dimension wird aus den Modell-Metadaten abgeleitet (kein Hardcoding).
using var memoryStore = new EmbeddedVectorStore("chat_memory");
var memory = memoryStore.GetOrCreateTextCollection("chat_history", embeddingGenerator);

ulong messageId = 0;

// ── Chat Loop ────────────────────────────────────────────────
while (true)
{
    Console.Write("Du: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Trim().ToLowerInvariant() == "exit")
        break;

    try
    {
        // 1. Relevante vergangene Nachrichten abrufen (vor dem Speichern!).
        //    Query wird intern als Suchanfrage embedded; Metadaten kommen mit.
        var relevant = await RetrieveRelevantAsync(memory, input, memoryTopK);

        // 2. User-Nachricht speichern (Embedding passiert im Upsert)
        await StoreMessageAsync(memory, ++messageId, "user", input);

        // 3. Prompt mit System + Kontext + aktueller Nachricht bauen
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildSystemPrompt(relevant)),
            new UserChatMessage(input),
        };

        // 4. Antwort generieren
        Console.Write("Agent: ");
        var completion = await agent.RunAsync(messages);
        var response = completion.Content.ToString() ?? string.Empty;
        Console.WriteLine(response);

        // 5. Assistant-Antwort speichern
        await StoreMessageAsync(memory, ++messageId, "assistant", response);

        // 6. Checkpoint auf Disk
        await memoryStore.CheckpointAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[Fehler] {ex.Message}");
    }
}

Console.WriteLine("\nAuf Wiedersehen!");

// ═══════════════════════════════════════════════════════════════
// Hilfsmethoden – kein manuelles Embedding mehr noetig
// ═══════════════════════════════════════════════════════════════

static async Task<List<MemoryEntry>> RetrieveRelevantAsync(TextVectorCollection memory, string query, int topK)
{
    var results = new List<MemoryEntry>();
    foreach (var r in await memory.SearchTextAsync(query, topK: topK, ef: 64))
    {
        var payload = r.Metadata?.Payload;
        if (payload is null) continue;

        results.Add(new MemoryEntry
        {
            Role    = payload.GetValueOrDefault("role")?.ToString() ?? "?",
            Content = payload.GetValueOrDefault("text")?.ToString() ?? "?",
            Score   = r.Score,
        });
    }

    return results;
}

static Task StoreMessageAsync(TextVectorCollection memory, ulong id, string role, string content)
    => memory.UpsertTextAsync(id, content, new Dictionary<string, object>
    {
        ["role"]      = role,
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });

static string BuildSystemPrompt(List<MemoryEntry> relevantMessages)
{
    var sb = new StringBuilder();
    sb.AppendLine("Du bist ein hilfreicher Assistent mit Langzeitgedaechtnis.");

    if (relevantMessages.Count > 0)
    {
        sb.AppendLine("\nRelevante Informationen aus vergangenen Unterhaltungen:");
        foreach (var msg in relevantMessages.OrderBy(m => m.Score))
            sb.AppendLine($"- [{msg.Role}] {msg.Content}");
        sb.AppendLine();
    }

    sb.AppendLine("Beantworte die aktuelle Frage unter Beruecksichtigung des obigen Kontexts.");
    return sb.ToString();
}

// ── DTOs ──────────────────────────────────────────────────────
class MemoryEntry
{
    public required string Role { get; set; }
    public required string Content { get; set; }
    public float Score { get; set; }
}
