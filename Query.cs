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
// Add these new namespaces for Semantic Kernel
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

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
        private static string _semanticConfigName = "sema";
        // Add Semantic Kernel instance
        private static Kernel _semanticKernel;

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
            
            // Initialize Semantic Kernel
            _semanticKernel = InitializeSemanticKernel();

            await RunQueriesAsync();
        }
        
        private static Kernel InitializeSemanticKernel()
        {
            _logger.LogInformation("Initializing Semantic Kernel...");
            
            try
            {
                var builder = Kernel.CreateBuilder();
                
                // Configure Azure OpenAI
                builder.AddAzureOpenAIChatCompletion(
                    deploymentName: _chatModelName,
                    endpoint: Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
                    apiKey: Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY"));
                
                var kernel = builder.Build();
                _logger.LogInformation("Semantic Kernel initialized successfully");
                return kernel;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize Semantic Kernel: {ex.Message}");
                throw;
            }
        }

        private static async Task RunQueriesAsync()
        {
            string query = "primary care managers responsibilities?";
            
            // Define desired fields
            string[] desiredFields = new[] 
            { 
                "ChunkSequence", "chunk_id", "id", "ChunkText", "ChunkVector",
                "chunk", "content", "DocumentTitle", "title", "URL" 
            };
            
            // Get only the fields that actually exist in the index
            string[] selectFields = await SafeSelectFieldsAsync(desiredFields);

            // Generate a single random suffix for all query types
            var random = new Random();
            int randomSuffix = random.Next(1000, 9999);
            
            // Dictionary to store results from all query types for the final summary
            var allResults = new Dictionary<string, List<Dictionary<string, object>>>();

            // 1. Keyword search (original)
            _logger.LogInformation("Running keyword search...");
            var keywordResults = await RunKeywordSearchAsync(query, selectFields);
            await ProcessAndDisplayResultsAsync(keywordResults, query, "Keyword Search", randomSuffix, 1);
            allResults["Keyword Search"] = keywordResults;
            
            // 2. Vector search
            try
            {
                _logger.LogInformation("Running vector search...");
                float[] queryVector = await GetEmbeddingsAsync(query);
                var vectorResults = await RunVectorSearchAsync(queryVector, selectFields);
                await ProcessAndDisplayResultsAsync(vectorResults, query, "Vector Search", randomSuffix, 2);
                allResults["Vector Search"] = vectorResults;
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
                await ProcessAndDisplayResultsAsync(hybridResults, query, "Hybrid Search (Keyword + Vector)", randomSuffix, 3);
                allResults["Hybrid Search (Keyword + Vector)"] = hybridResults;
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
                await ProcessAndDisplayResultsAsync(semanticResults, query, "Hybrid Search + Semantic Ranker", randomSuffix, 4);
                allResults["Hybrid Search + Semantic Ranker"] = semanticResults;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hybrid search with semantic ranker failed: {ex.Message}");
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
                    "Hybrid Search + Semantic Ranker + Query Rewriting", randomSuffix, 5);
                allResults["Hybrid Search + Semantic Ranker + Query Rewriting"] = semanticRewrittenResults;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hybrid search with semantic ranker and query rewriting failed: {ex.Message}");
            }

            // Create a single summary file with results from all query types
            await SaveCombinedSummaryMarkdownAsync(allResults, query, randomSuffix);
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
                    _logger.LogInformation($"Rewriting query: '{query}'");
                    
                    // Use simple relative path for prompts
                    var pluginDirectoryPath = "./Prompts";
                    _logger.LogInformation($"Looking for prompts at: {pluginDirectoryPath}");
                    
                    try
                    {
                        // Import plugins from the prompts directory
                        KernelPlugin plugins = _semanticKernel.ImportPluginFromPromptDirectory(pluginDirectoryPath);
                        
                        // Get the function reference
                        var queryRewriterFunction = plugins["QueryRewriter"];
                        
                        // Execute the function
                        var result = await _semanticKernel.InvokeAsync(
                            queryRewriterFunction,
                            new() { { "query", query } });
                        
                        string rewrittenQuery = result.GetValue<string>().Trim();
                        
                        // If empty or null response, return the original query
                        if (string.IsNullOrWhiteSpace(rewrittenQuery))
                        {
                            _logger.LogWarning("Empty response from query rewriter - using original query");
                            return query;
                        }
                        
                        _logger.LogInformation($"Query rewritten: '{query}' → '{rewrittenQuery}'");
                        return rewrittenQuery;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        _logger.LogWarning($"Prompts directory not found at {pluginDirectoryPath}");
                        return query;  // Return original query if directory not found
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error rewriting query: {ex.Message}");
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
            
            return NormalizeSearchResults(response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList());
        }

        private static async Task<List<Dictionary<string, object>>> RunVectorSearchAsync(float[] queryVector, string[] selectFields)
        {
            var searchOptions = new SearchOptions
            {
                Size = 5,
                VectorSearch = new VectorSearchOptions
                {
                    Queries = { new VectorizedQuery(queryVector) { 
                        KNearestNeighborsCount = 5,
                        Fields = { _vectorFieldName } },
                     }
                }
            };
            foreach (var field in selectFields)
            {
                searchOptions.Select.Add(field);
            }
            
            var response = await _searchClient.SearchAsync<Dictionary<string, object>>(null, searchOptions);
            
            return NormalizeSearchResults(response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList());
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
            
            return NormalizeSearchResults(response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList());
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
            
            return NormalizeSearchResults(response.Value.GetResults()
                .Select(r => 
                {
                    var result = new Dictionary<string, object>(r.Document);
                    result["@search.score"] = r.Score;
                    return result;
                })
                .ToList());
        }

        /// <summary>
        /// Normalizes search scores to a 0-1 scale for consistent comparison across search methods
        /// </summary>
        private static List<Dictionary<string, object>> NormalizeSearchResults(List<Dictionary<string, object>> results)
        {
            if (results == null || results.Count == 0)
                return results;

            // Find min and max scores
            double minScore = double.MaxValue;
            double maxScore = double.MinValue;

            foreach (var result in results)
            {
                double score = Convert.ToDouble(result["@search.score"]);
                minScore = Math.Min(minScore, score);
                maxScore = Math.Max(maxScore, score);
            }

            // If min == max, we can't normalize in the standard way
            if (Math.Abs(maxScore - minScore) < 0.0001)
            {
                // Set all to 1.0 if there's just a single value
                foreach (var result in results)
                {
                    result["@search.score"] = 1.0;
                }
                return results;
            }

            // Apply min-max normalization
            foreach (var result in results)
            {
                double originalScore = Convert.ToDouble(result["@search.score"]);
                double normalizedScore = (originalScore - minScore) / (maxScore - minScore);
                
                // Store both scores for reference
                result["@search.original_score"] = originalScore;
                result["@search.score"] = normalizedScore;
            }

            _logger.LogInformation($"Normalized scores from range [{minScore:F4} - {maxScore:F4}] to [0 - 1]");
            return results;
        }

        private static async Task<object> ProcessAndDisplayResultsAsync(List<Dictionary<string, object>> resultsList, string query, string searchType, int randomSuffix, int queryNumber)
        {
            Console.WriteLine($"\n=== {searchType.ToUpper()} ===\n");
            
            // Print results in a readable format for console
            for (int i = 0; i < resultsList.Count; i++)
            {
                var row = resultsList[i];
                Console.WriteLine($"Result {i+1}:");
                Console.WriteLine($"  Score (Normalized): {row["@search.score"]:F4}");
                if (row.ContainsKey("@search.original_score"))
                {
                    Console.WriteLine($"  Original Score: {row["@search.original_score"]:F4}");
                }                
                Console.WriteLine("");
            }
            
            // Save results to markdown
            string outputFile = await SaveToMarkdownAsync(resultsList, query, searchType, randomSuffix, queryNumber);
            if (!string.IsNullOrEmpty(outputFile))
            {
                _logger.LogInformation($"{searchType} results saved to {outputFile}");
            }
            
            // Remove individual summary file creation
            // await SaveSummaryMarkdownAsync(resultsList, searchType, randomSuffix);
            
            return null;
        }

        /// <summary>
        /// Saves a combined summary of all search results to a single markdown file
        /// </summary>
        private static async Task<string> SaveCombinedSummaryMarkdownAsync(
            Dictionary<string, List<Dictionary<string, object>>> allResults, 
            string query, 
            int randomSuffix)
        {
            try
            {
                string filename = $"Output_Summary_{randomSuffix}.md";
                
                using (var writer = new StreamWriter(filename, false, Encoding.UTF8))
                {
                    await writer.WriteLineAsync("# Search Results Summary\n");
                    await writer.WriteLineAsync($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    await writer.WriteLineAsync($"## Query\n\n{query}\n");
                    
                    foreach (var searchType in allResults.Keys)
                    {
                        var results = allResults[searchType];
                        
                        await writer.WriteLineAsync($"## {searchType}\n");
                        
                        // Create a table header
                        await writer.WriteLineAsync("| Result # | Score (Normalized) | Original Score |");
                        await writer.WriteLineAsync("|----------|-------------------|---------------|");
                        
                        // Add a row for each result
                        for (int i = 0; i < results.Count; i++)
                        {
                            var result = results[i];
                            string originalScore = result.ContainsKey("@search.original_score") 
                                ? $"{result["@search.original_score"]:F4}" 
                                : "N/A";
                                
                            await writer.WriteLineAsync($"| Result #{i+1} | {result["@search.score"]:F4} | {originalScore} |");
                        }
                        
                        // Add a row with mean scores
                        if (results.Count > 0)
                        {
                            double meanNormalizedScore = results.Average(r => Convert.ToDouble(r["@search.score"]));
                            
                            // Calculate mean of original scores if available
                            string meanOriginalScore = "N/A";
                            if (results.All(r => r.ContainsKey("@search.original_score")))
                            {
                                double meanOriginal = results.Average(r => Convert.ToDouble(r["@search.original_score"]));
                                meanOriginalScore = $"{meanOriginal:F4}";
                            }
                            
                            await writer.WriteLineAsync("|----------|-------------------|---------------|");
                            await writer.WriteLineAsync($"| **MEAN** | **{meanNormalizedScore:F4}** | **{meanOriginalScore}** |");
                        }
                        
                        await writer.WriteLineAsync("\n");
                    }
                }
                
                _logger.LogInformation($"Combined summary for all search types saved to {filename}");
                return filename;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving combined summary to markdown: {ex.Message}");
                return null;
            }
        }

        // Keep the individual summary method for other uses
        private static async Task<string> SaveSummaryMarkdownAsync(List<Dictionary<string, object>> results, string searchType, int randomSuffix)
        {
            try
            {
                string safeSearchType = searchType.Replace(" ", "_").Replace("+", "plus")
                    .Replace("(", "").Replace(")", "");
                string filename = $"summary_{safeSearchType}_{randomSuffix}.md";
                
                using (var writer = new StreamWriter(filename, false, Encoding.UTF8))
                {
                    await writer.WriteLineAsync("# Search Results Summary\n");
                    await writer.WriteLineAsync($"## Query Type\n{searchType}\n");
                    await writer.WriteLineAsync("## Results Overview\n");
                    
                    // Create a table header
                    await writer.WriteLineAsync("| Result # | Score (Normalized) | Original Score |");
                    await writer.WriteLineAsync("|----------|-------------------|---------------|");
                    
                    // Add a row for each result
                    for (int i = 0; i < results.Count; i++)
                    {
                        var result = results[i];
                        string originalScore = result.ContainsKey("@search.original_score") 
                            ? $"{result["@search.original_score"]:F4}" 
                            : "N/A";
                            
                        await writer.WriteLineAsync($"| Result #{i+1} | {result["@search.score"]:F4} | {originalScore} |");
                    }
                }
                
                _logger.LogInformation($"Summary saved to {filename}");
                return filename;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving summary to markdown: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> SaveToMarkdownAsync(List<Dictionary<string, object>> results, string query, string searchType, int randomSuffix, int queryNumber)
        {
            try
            {
                string safeSearchType = searchType.Replace(" ", "_").Replace("+", "plus")
                    .Replace("(", "").Replace(")", "");
                string filename = $"Output_{queryNumber}_{safeSearchType}_{randomSuffix}.md";
                
                using (var writer = new StreamWriter(filename, false, Encoding.UTF8))
                {
                    await writer.WriteLineAsync("# Query Result\n");
                    await writer.WriteLineAsync($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                    await writer.WriteLineAsync($"## Query\n\n{query}\n");
                    await writer.WriteLineAsync($"## Search Type\n\n{searchType}\n");
                    await writer.WriteLineAsync("## Search Results\n");
                    
                    foreach (var result in results)
                    {
                        // Add a heading with the result number
                        await writer.WriteLineAsync($"### Result #{results.IndexOf(result) + 1}\n");
                        
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
                        
                        await writer.WriteLineAsync($"Score (Normalized): {result["@search.score"]:F4}");
                        if (result.ContainsKey("@search.original_score"))
                        {
                            await writer.WriteLineAsync($"Original Score: {result["@search.original_score"]:F4}");
                        }
                        await writer.WriteLineAsync("");
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
