using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using DotNetEnv;
using DocumentFormat.OpenXml.Packaging;
using Azure.Identity;
using System.Text.Json; // Add this for JSON serialization

public class Ingest
{

    public static async Task Main(string[] args)
    {
        Env.Load();

        // Load and validate environment variables
        var requiredVars = new string[] {
            "AZURE_OPENAI_ENDPOINT",
            "AZURE_AISEARCH_ENDPOINT",
            "AZURE_AISEARCH_INDEX_NAME"
        };
        foreach (var varName in requiredVars)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(varName)))
            {
                Console.WriteLine($"Missing required environment variable: {varName}");
                return;
            }
        }


        // Extract text from local Word documents
        var docsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "docs");
        if (!Directory.Exists(docsDirectory))
        {
            Console.WriteLine($"Docs directory does not exist: {docsDirectory}");
            return;
        }

        var extractedTexts = new List<string>();
        var documents = new List<SearchDocument>();
        var docUuid = Guid.NewGuid().ToString();

        // For each doc, save the filename for 'PageTitle'
        foreach (var filePath in Directory.GetFiles(docsDirectory, "*.docx"))
        {
            var docName = Path.GetFileName(filePath);
            using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(filePath, false))
            {
                var body = wordDoc.MainDocumentPart.Document.Body;
                extractedTexts.Add(body.InnerText);

                int chunkSequence = 0; // explicitly track chunk sequence as integer
                foreach (var chunk in ChunkTextByTokens(body.InnerText, 7000))
                {
                    if (string.IsNullOrWhiteSpace(chunk)) continue;
                    var embedding = await GenerateEmbedding(chunk);

                    documents.Add(new SearchDocument
                    {
                        { "ChunkId", Guid.NewGuid().ToString() },
                        { "ParentChunkId", docUuid },
                        { "ChunkSequence", chunkSequence },
                        { "DocumentTitle", docName },
                        //  { "PageVersion", Path.GetFileNameWithoutExtension(filePath) },
                        { "CitationTitle", "N/A" },
                        { "ChunkText", chunk },
                        { "ChunkVector", embedding != null ? embedding.ConvertAll(x => x.ToString()) : new List<string>() },
                        { "URL", docName },
                        { "PublicationDate", DateTimeOffset.UtcNow.ToString("o") },
                        { "ManualType", "docx" },
                        { "MetaData", "N/A" },
                        { "ContractType", "N/A" }
                    });

                    chunkSequence++; // increment chunk sequence
                }
            }
        }

        // Upload documents to Azure Cognitive Search
        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_AISEARCH_ENDPOINT");
        var indexName = Environment.GetEnvironmentVariable("AZURE_AISEARCH_INDEX_NAME");
        var credential = new DefaultAzureCredential();

        var searchClient = new SearchClient(new Uri(searchEndpoint), indexName, credential);
        await searchClient.UploadDocumentsAsync(documents);

        Console.WriteLine("Documents uploaded successfully.");
    }

    private static async Task DownloadBlob(BlobClient blobClient, string localFilePath)
    {
        using var downloadFileStream = File.OpenWrite(localFilePath);
        await blobClient.DownloadToAsync(downloadFileStream);
    }

    // Simple chunking by token count
    private static List<string> ChunkTextByTokens(string text, int maxTokens)
    {
        var words = text.Split(' ');
        var chunks = new List<string>();
        var currentChunk = new List<string>();
        int currentLength = 0;

        foreach (var w in words)
        {
            var length = w.Split(' ').Length;
            if (currentLength + length <= maxTokens)
            {
                currentChunk.Add(w);
                currentLength += length;
            }
            else
            {
                chunks.Add(string.Join(" ", currentChunk));
                currentChunk.Clear();
                currentChunk.Add(w);
                currentLength = length;
            }
        }
        if (currentChunk.Count > 0)
            chunks.Add(string.Join(" ", currentChunk));

        return chunks;
    }

    // Call the OpenAI embeddings endpoint
    private static async Task<List<float>> GenerateEmbedding(string inputText)
    {
        int maxRetries = 3;
        int delay = 1000; // initial delay in milliseconds

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var httpClient = new HttpClient();
                var endpoint = $"{Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")}/" +
                               "openai/deployments/" +
                               $"{Environment.GetEnvironmentVariable("MODEL_EMBEDDINGS_DEPLOYMENT_NAME")}/embeddings?api-version=2023-05-15";
                var credential = new DefaultAzureCredential();
                var token = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

                var content = new StringContent($"{{\"input\":\"{inputText.Replace("\"", "\\\"")}\"}}",
                                                Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(endpoint, content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(responseString);
                var embedJson = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
                var embeddingList = new List<float>();
                foreach (var val in embedJson.EnumerateArray())
                {
                    embeddingList.Add((float)val.GetDouble());
                }
                return embeddingList;
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429"))
            {
                if (attempt == maxRetries - 1)
                {
                    throw;
                }
                await Task.Delay(delay);
                delay *= 2; // exponential backoff
            }
        }

        return new List<float>();
    }
}

public class SearchDocument : Dictionary<string, object> { }