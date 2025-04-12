using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;
using OpenAI;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using DotNetEnv;

namespace MavericRagChat
{
    class Program
    {
        private static ILogger _logger;
        private static SearchClient _searchClient;
        private static AzureOpenAIClient _openAIClient;
        private static string _embeddingModelName;
        private static string _chatModelName;
        private static string _vectorFieldName = "ChunkVector";
        private static string _semanticConfigName = "default-semantic-config";

        static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("MavericRagChat", LogLevel.Information)
                    .AddConsole();
            });
            _logger = loggerFactory.CreateLogger<Program>();
            
            _logger.LogInformation("Starting query script execution.");

            Env.Load();

            var requiredVars = new string[] {
                "AZURE_AISEARCH_ENDPOINT",
                "AZURE_AISEARCH_INDEX_NAME",
                "AZURE_AISEARCH_KEY",
                "AZURE_OPENAI_ENDPOINT",
                "AZURE_OPENAI_KEY",
                "MODEL_EMBEDDINGS_DEPLOYMENT_NAME",
                "MODEL_CHAT_DEPLOYMENT_NAME"
            };
            foreach (var varName in requiredVars)
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(varName)))
                {
                    Console.WriteLine($"Missing required environment variable: {varName}");
                    return;
                }
            }

            string endpoint = Environment.GetEnvironmentVariable("AZURE_AISEARCH_ENDPOINT");
            string indexName = Environment.GetEnvironmentVariable("AZURE_AISEARCH_INDEX_NAME");
            
            AzureKeyCredential searchCredential = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_AISEARCH_KEY")) 
                ? new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_AISEARCH_KEY")) 
                : null;
            
            TokenCredential defaultCredential = new DefaultAzureCredential();
            
            _searchClient = searchCredential != null
                ? new SearchClient(new Uri(endpoint), indexName, searchCredential)
                : new SearchClient(new Uri(endpoint), indexName, defaultCredential);

            _openAIClient = new AzureOpenAIClient(
                new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")),
                new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY")));

            _embeddingModelName = Environment.GetEnvironmentVariable("MODEL_EMBEDDINGS_DEPLOYMENT_NAME");
            _chatModelName = Environment.GetEnvironmentVariable("MODEL_CHAT_DEPLOYMENT_NAME");

            await RunQueriesAsync();
        }

        private static async Task RunQueriesAsync()
        {
            string query = "What do primary care managers do?";
            
            // Define desired fields
            string[] desiredFields = new[] 
            { 
                "ChunkSequence", "chunk_id", "id", "ChunkText", "ChunkVector",
                "chunk", "content", "DocumentTitle", "title", "URL" 
            };
            
            // Get only the fields that actually exist in the index
            string[] selectFields = await SafeSelectFieldsAsync(desiredFields);

            // 1. Keyword search (original)
            _logger.LogInformation("Running keyword search...");
            var keywordResults = await RunKeywordSearchAsync(query, selectFields);
            await ProcessAndDisplayResultsAsync(keywordResults, query, "Keyword Search");
            
            // 2. Vector search
            try
            {
                _logger.LogInformation("Running vector search...");
                float[] queryVector = await GetEmbeddingsAsync(query);
                var vectorResults = await RunVectorSearchAsync(queryVector, selectFields);
                await ProcessAndDisplayResultsAsync(vectorResults, query, "Vector Search");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Vector search failed: {ex.Message}");
            }
            
            // 3. Hybrid search (keyword + vector)
            try
            {
                _logger.LogInformation("Running hybrid search...");
                float[] queryVector = await GetEmbeddingsAsync(query);
                var hybridResults = await RunHybridSearchAsync(query, queryVector, selectFields);
                await ProcessAndDisplayResultsAsync(hybridResults, query, "Hybrid Search (Keyword + Vector)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hybrid search failed: {ex.Message}");
            }
            
            // 4. Hybrid search + semantic ranker
            try
            {
                _logger.LogInformation("Running hybrid search with semantic ranker...");
                var semanticResults = await RunSemanticSearchAsync(query, selectFields);
                await ProcessAndDisplayResultsAsync(semanticResults, query, "Hybrid Search + Semantic Ranker");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hybrid search with semantic ranker failed: {ex.Message}");
                
                if (ex.Message.ToLower().Contains("semantic configurations"))
                {
                    _logger.LogWarning("Semantic search failed because no semantic configurations are defined in the index. " +
                                      "See https://learn.microsoft.com/en-us/azure/search/semantic-how-to-enable to enable semantic search.");
                    _logger.LogInformation("Falling back to standard search for Hybrid Search + Semantic Ranker...");
                    
                    var fallbackResults = await RunKeywordSearchAsync(query, selectFields);
                    await ProcessAndDisplayResultsAsync(fallbackResults, query, "Hybrid Search + Semantic Ranker (Fallback to Standard)");
                }
            }
            
            // 5. Hybrid search + semantic ranker + query rewriting
            try
            {
                _logger.LogInformation("Running hybrid search with semantic ranker and query rewriting...");
                string rewrittenQuery = await RewriteQueryAsync(query);
                _logger.LogInformation($"Rewritten query: {rewrittenQuery}");
                
                var semanticRewrittenResults = await RunSemanticSearchAsync(rewrittenQuery, selectFields);
                await ProcessAndDisplayResultsAsync(semanticRewrittenResults, 
                    $"Original: '{query}'\nRewritten: '{rewrittenQuery}'", 
                    "Hybrid Search + Semantic Ranker + Query Rewriting");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hybrid search with semantic ranker and query rewriting failed: {ex.Message}");
                
                if (ex.Message.ToLower().Contains("semantic configurations"))
                {
                    _logger.LogWarning("Semantic search failed because no semantic configurations are defined in the index. " +
                                      "See https://learn.microsoft.com/en-us/azure/search/semantic-how-to-enable to enable semantic search.");
                    _logger.LogInformation("Falling back to standard search with rewritten query...");
                    
                    string rewrittenQuery = await RewriteQueryAsync(query);
                    var fallbackResults = await RunKeywordSearchAsync(rewrittenQuery, selectFields);
                    await ProcessAndDisplayResultsAsync(fallbackResults, 
                        $"Original: '{query}'\nRewritten: '{rewrittenQuery}'", 
                        "Hybrid Search + Semantic Ranker + Query Rewriting (Fallback to Standard)");
                }
            }

        }

        private static AsyncRetryPolicy CreateRetryPolicy()
        {
            return Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    6,
                    retryAttempt => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryAttempt), 20) + 
                                                         TimeSpan.FromMilliseconds(new Random().Next(0, 1000)).TotalSeconds));
        }


        private static async Task<float[]> GetEmbeddingsAsync(string text)
        {
            var retryPolicy = CreateRetryPolicy();
            
            return await retryPolicy.ExecuteAsync(async () =>
            {
                try
                {
                    EmbeddingClient client = _openAIClient.GetEmbeddingClient(Environment.GetEnvironmentVariable("MODEL_EMBEDDINGS_DEPLOYMENT_NAME"));
                    OpenAIEmbedding embedding = await client.GenerateEmbeddingAsync(text);
                    ReadOnlyMemory<float> vector = embedding.ToFloats();
                    return vector.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error generating embeddings: {ex.Message}");
                    throw;
                }
            });
        } 

        private static async Task<float[]> GetEmbeddingsAsyncOpenAI(string text)
        {
            var retryPolicy = CreateRetryPolicy();
            
            return await retryPolicy.ExecuteAsync(async () =>
            {
                try
                {
                    EmbeddingClient client = new(Environment.GetEnvironmentVariable("MODEL_EMBEDDINGS_DEPLOYMENT_NAME"), 
                    Environment.GetEnvironmentVariable("OPENAI_KEY"));

                    OpenAIEmbedding embedding = await client.GenerateEmbeddingAsync(text);
                    ReadOnlyMemory<float> vector = embedding.ToFloats();
                    return vector.ToArray();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error generating embeddings: {ex.Message}");
                    throw;
                }
            });
        } 

        private static async Task<string> RewriteQueryAsync(string query)
        {
            var retryPolicy = CreateRetryPolicy();
            
            return await retryPolicy.ExecuteAsync(async () =>
            {
                try
                {
                    var chatCompletionsOptions = new ChatCompletionsOptions
                    {
                        Temperature = 0.0f,
                        MaxTokens = 100,
                        Messages = 
                        {
                            new ChatMessage(ChatRole.System, "You are a search assistant. Rewrite the following query to make it more effective for search, but maintain the original intent and scope."),
                            new ChatMessage(ChatRole.User, query)
                        }
                    };
                    
                    ChatClient client = _openAIClient.GetChatClient(Environment.GetEnvironmentVariable("MODEL_CHAT_DEPLOYMENT_NAME"));
                    var response = await client.GetChatCompletionsAsync(_chatModelName, chatCompletionsOptions);
                    
                    return response.Value.Choices[0].Message.Content.Trim();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error rewriting query: {ex.Message}");
                    return query;  // Fallback to original query
                }
            });
        }

        private static async Task<List<string>> GetAvailableFieldsAsync()
        {
            try
            {
                var searchOptions = new SearchOptions
                {
                    Size = 1
                };
                
                var testResults = await _searchClient.SearchAsync<Dictionary<string, object>>("*", searchOptions);
                var availableFields = new List<string>();
                
                if (testResults.Value.GetResults().Any())
                {
                    var testDoc = testResults.Value.GetResults().First().Document;
                    availableFields = testDoc.Keys.Where(k => !k.StartsWith('@')).ToList();
                    _logger.LogInformation($"Available fields in index: {String.Join(", ", availableFields)}");
                }
                else
                {
                    _logger.LogWarning("No documents found in index to determine available fields");
                    availableFields = new List<string> { "id", "content", "title" };
                }
                
                return availableFields;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error determining available fields: {ex.Message}");
                return new List<string> { "id", "content", "title" };
            }
        }

        private static async Task<string[]> SafeSelectFieldsAsync(string[] desiredFields)
        {
            var available = await GetAvailableFieldsAsync();
            
            // Always include score
            if (!available.Contains("@search.score"))
            {
                available.Add("@search.score");
            }
            
            // Filter to only fields that exist
            var validFields = desiredFields.Where(field => available.Contains(field)).ToArray();
            
            if (validFields.Length == 0)
            {
                _logger.LogWarning($"None of the requested fields {string.Join(", ", desiredFields)} exist in the index. Using all available fields.");
                return available.ToArray();
            }
            
            _logger.LogInformation($"Using fields: {string.Join(", ", validFields)}");
            return validFields;
        }

        private static async Task<List<Dictionary<string, object>>> RunKeywordSearchAsync(string query, string[] selectFields)
        {
            var searchOptions = new SearchOptions
            {
                Size = 5
            };
            
            foreach (var field in selectFields)
            {
                searchOptions.Select.Add(field);
            }
            
            var response = await _searchClient.SearchAsync<Dictionary<string, object>>(query, searchOptions);
            
            return response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList();
        }

        private static async Task<List<Dictionary<string, object>>> RunVectorSearchAsync(float[] queryVector, string[] selectFields)
        {
            var searchOptions = new SearchOptions
            {
                Size = 5,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { new VectorizedQuery(queryVector) { KNearestNeighborsCount = 5, Fields = { _vectorFieldName } } }
                }
            };
            
            foreach (var field in selectFields)
            {
                searchOptions.Select.Add(field);
            }
            
            var response = await _searchClient.SearchAsync<Dictionary<string, object>>(null, searchOptions);
            
            return response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList();
        }

        private static async Task<List<Dictionary<string, object>>> RunHybridSearchAsync(string query, float[] queryVector, string[] selectFields)
        {
            var searchOptions = new SearchOptions
            {
                Size = 5,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { new VectorizedQuery(queryVector) { KNearestNeighborsCount = 5, Fields = { _vectorFieldName } } }
                }
            };
            
            foreach (var field in selectFields)
            {
                searchOptions.Select.Add(field);
            }
            
            var response = await _searchClient.SearchAsync<Dictionary<string, object>>(query, searchOptions);
            
            return response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList();
        }

        private static async Task<List<Dictionary<string, object>>> RunSemanticSearchAsync(string query, string[] selectFields)
        {
            var searchOptions = new SearchOptions
            {
                Size = 5,
                QueryType = SearchQueryType.Semantic
            };
            
            foreach (var field in selectFields)
            {
                searchOptions.Select.Add(field);
            }
            
            searchOptions.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = _semanticConfigName
            };
            
            var response = await _searchClient.SearchAsync<Dictionary<string, object>>(query, searchOptions);
            
            return response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList();
        }

        private static async Task<object> ProcessAndDisplayResultsAsync(List<Dictionary<string, object>> resultsList, string query, string searchType)
        {
            Console.WriteLine($"\n=== {searchType.ToUpper()} ===\n");
            
            // Print results in a readable format for console
            for (int i = 0; i < resultsList.Count; i++)
            {
                var row = resultsList[i];
                Console.WriteLine($"Result {i+1}:");
                Console.WriteLine($"  Score: {row["@search.score"]}");
                
                // if (row.ContainsKey("ChunkSequence"))
                //     Console.WriteLine($"  Chunk Sequence: {row["ChunkSequence"]}");
                
                // if (row.ContainsKey("DocumentTitle"))
                //     Console.WriteLine($"  Document Title: {row["DocumentTitle"]}");
                
                // if (row.ContainsKey("ChunkText"))
                //     Console.WriteLine($"  Text: {row["ChunkText"]}");
                
                // if (row.ContainsKey("URL"))
                //     Console.WriteLine($"  URL: {row["URL"]}");
                
                Console.WriteLine("");
            }
            
            // Save results to markdown
            string outputFile = await SaveToMarkdownAsync(resultsList, query, searchType);
            if (!string.IsNullOrEmpty(outputFile))
            {
                _logger.LogInformation($"{searchType} results saved to {outputFile}");
            }
            
            return null; // In C# we're not returning a styled dataframe
        }

        private static async Task<string> SaveToMarkdownAsync(List<Dictionary<string, object>> results, string query, string searchType)
        {
            try
            {
                var random = new Random();
                int randomSuffix = random.Next(1000, 9999);
                string filename = $"output_{randomSuffix}.md";
                
                using (var writer = new StreamWriter(filename, false, Encoding.UTF8))
                {
                    await writer.WriteLineAsync("# Query Result\n");
                    await writer.WriteLineAsync($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    await writer.WriteLineAsync($"## Query\n\n{query}\n");
                    await writer.WriteLineAsync($"## Search Type\n\n{searchType}\n");
                    
                    await writer.WriteLineAsync("## Search Results\n");
                    
                    foreach (var result in results)
                    {
                        // Look for ID field
                        foreach (var idField in new[] { "ChunkSequence", "chunk_id", "id", "Id" })
                        {
                            if (result.ContainsKey(idField))
                            {
                                await writer.WriteLineAsync($"{idField}: {result[idField]}");
                                break;
                            }
                        }
                        
                        // Look for content field
                        foreach (var contentField in new[] { "ChunkText", "chunk", "content", "Content" })
                        {
                            if (result.ContainsKey(contentField))
                            {
                                string content = result[contentField].ToString();
                                if (content.Length > 300)
                                    await writer.WriteLineAsync($"Content: {content.Substring(0, 300)}...");
                                else
                                    await writer.WriteLineAsync($"Content: {content}");
                                break;
                            }
                        }
                        
                        // Add title if present
                        foreach (var titleField in new[] { "DocumentTitle", "title", "Title" })
                        {
                            if (result.ContainsKey(titleField))
                            {
                                await writer.WriteLineAsync($"Title: {result[titleField]}");
                                break;
                            }
                        }
                        
                        // Add URL if present
                        if (result.ContainsKey("URL"))
                        {
                            await writer.WriteLineAsync($"URL: {result["URL"]}");
                        }
                        
                        await writer.WriteLineAsync($"Score: {result["@search.score"]}\n");
                    }
                }
                
                _logger.LogInformation($"Results saved to {filename}");
                return filename;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving to markdown: {ex.Message}");
                return null;
            }
        }
    }
}
