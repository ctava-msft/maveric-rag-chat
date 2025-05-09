using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using DotNetEnv;
using Azure.Identity;
using Azure.Core.Diagnostics;      // +add
using Azure.Core;                  // +add

public class CreateIndex
{
    private static List<SearchField> LoadFieldsFromJson(string path)
    {
        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        var fieldsArray = doc.RootElement.GetProperty("fields");
        var fields = new List<SearchField>();
        foreach (var f in fieldsArray.EnumerateArray())
        {
            try
            {
                var name = f.GetProperty("name").GetString();
                var dataType = f.GetProperty("type").GetString();
                bool isKey = f.GetProperty("key").GetBoolean();
                bool isSearchable = f.GetProperty("searchable").GetBoolean();
                bool isFilterable = f.GetProperty("filterable").GetBoolean();
                bool isSortable = f.GetProperty("sortable").GetBoolean();
                bool isFacetable = f.GetProperty("facetable").GetBoolean();
                bool isCollection = dataType.StartsWith("Collection(");
                SearchField field;
                Console.WriteLine($"Processing vector field: {name} {dataType} {isCollection}");
                if (isSearchable && !isKey)
                {
                    field = new SearchableField(name, isCollection)
                    {
                        IsKey = isKey,
                        IsFilterable = isFilterable,
                        IsSortable = isSortable,
                        IsFacetable = isFacetable
                    };
                }
                else
                {
                    field = new SimpleField(name, ConvertToSearchFieldDataType(dataType))
                    {
                        IsKey = isKey,
                        IsFilterable = isFilterable,
                        IsSortable = isSortable,
                        IsFacetable = isFacetable
                    };
                }

                // // If the field is a vector field, assign dimensions and vector search configuration.
                if (dataType == "Collection(Edm.Single)")
                {
                    Console.WriteLine($"Fix me");

                }
                //     Console.WriteLine($"Processing vector field: {name} {dataType} {field.GetType().GetProperty("dimensions")}");
                //     var dimsProp = field.GetType().GetProperty("Dimensions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                //     if (dimsProp != null && f.TryGetProperty("dimensions", out JsonElement dimensionsElement))
                //     {
                //         dimsProp.SetValue(field, dimensionsElement.GetInt32());
                //     }
                //     var vsConfigProp = field.GetType().GetProperty("VectorSearchConfiguration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                //     if (vsConfigProp != null && f.TryGetProperty("vectorSearchConfiguration", out JsonElement vsConfigElement))
                //     {
                //         vsConfigProp.SetValue(field, vsConfigElement.GetString());
                //     }
                // }

                fields.Add(field);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing field: {f}. Exception: {ex.Message}");
                throw;
            }
        }
        return fields;
    }

    private static List<SearchField> MakeFields()
    {
        var fields = new List<SearchField>
        {
            new SimpleField("ChunkId", SearchFieldDataType.String) { IsKey = true },
            new SearchableField("ParentChunkId"),
            new SimpleField("ChunkSequence", SearchFieldDataType.Int32),
            new SearchableField("DocumentTitle"),
            new SearchableField("CitationTitle"),
            new SearchableField("ChunkText"),
            new SearchableField("URL"),
            new SearchableField("PublicationDate"),
            new SearchableField("ManualType"),
            new SearchableField("MetaData"),
            new SearchableField("ContractType"),
            new SearchField("ChunkVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = 3072,
                VectorSearchProfileName = "default"
            }
        };
        return fields;
    }

    // Update ConvertToSearchFieldDataType to handle additional types gracefully
    private static SearchFieldDataType ConvertToSearchFieldDataType(string dataType)
    {
        return dataType switch
        {
            "Edm.String" => SearchFieldDataType.String,
            "Edm.Int32" => SearchFieldDataType.Int32,
            "Edm.DateTimeOffset" => SearchFieldDataType.DateTimeOffset,
            "Edm.Single" => SearchFieldDataType.Single,
            "Collection(Edm.String)" => SearchFieldDataType.Collection(SearchFieldDataType.String),
            "Collection(Edm.Single)" => SearchFieldDataType.Collection(SearchFieldDataType.Single),
            _ => throw new NotSupportedException($"Data type '{dataType}' is not supported."),
        };
    }

    private static TokenCredential BuildCredential(bool devMode)
    {
        // Read an optional tenant override (useful when VS signs-in with a different default tenant)
        string tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

        if (devMode)
        {
            Console.WriteLine("Dev mode detected – using local credentials only.");
            return new ChainedTokenCredential(
                new EnvironmentCredential(),
                new VisualStudioCredential(new VisualStudioCredentialOptions { TenantId = tenantId }),
                new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }));
        }

        var opts = new DefaultAzureCredentialOptions
        {
            ExcludeManagedIdentityCredential = true,
            TenantId = tenantId,
            VisualStudioTenantId = tenantId,           // keep VS on the same tenant
            SharedTokenCacheTenantId = tenantId
        };
        return new DefaultAzureCredential(opts);
    }

    public static void Main(string[] args)
    {
        // Enable verbose console logging for all Azure SDK operations
        using AzureEventSourceListener listener = AzureEventSourceListener.CreateConsoleLogger(
            System.Diagnostics.Tracing.EventLevel.Verbose);

        Env.Load();

        var requiredVars = new List<string>
        {
            "AZURE_AISEARCH_ENDPOINT",
            "AZURE_AISEARCH_INDEX_NAME"
        };

        foreach (var v in requiredVars)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
            {
                throw new Exception($"Missing required environment variable: {v}");
            }
        }

        string endpoint = Environment.GetEnvironmentVariable("AZURE_AISEARCH_ENDPOINT");
        string indexName = Environment.GetEnvironmentVariable("AZURE_AISEARCH_INDEX_NAME");

        // Configure credential behaviour based on environment
        bool devMode = string.Equals(
            Environment.GetEnvironmentVariable("AZURE_DEV_MODE"), "true",
            StringComparison.OrdinalIgnoreCase);

        string apiKey   = Environment.GetEnvironmentVariable("AZURE_AISEARCH_API_KEY");

        TokenCredential credential = BuildCredential(devMode);

        // Explicitly test token acquisition so failures are surfaced early
        try
        {
            // Mgmt scope – proves we can get *a* token
            var testMgmt = credential.GetToken(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                System.Threading.CancellationToken.None);
            Console.WriteLine($"Mgmt token OK – expires: {testMgmt.ExpiresOn}");

            // Search scope – proves it is valid for Cognitive Search
            var testSearch = credential.GetToken(
                new TokenRequestContext(new[] { "https://search.azure.com/.default" }),
                System.Threading.CancellationToken.None);
            Console.WriteLine($"Search token OK – expires: {testSearch.ExpiresOn}");
        }
        catch (AuthenticationFailedException ex)
        {
            Console.WriteLine($"Token acquisition failed: {ex.Message}");
            throw;
        }

        SearchIndexClient indexClient = !string.IsNullOrEmpty(apiKey)
            ? new SearchIndexClient(new Uri(endpoint), new Azure.AzureKeyCredential(apiKey))
            : new SearchIndexClient(new Uri(endpoint), credential);

        // Load fields from JSON
        //var fields = LoadFieldsFromJson("fields.json");
        var fields = MakeFields();

        // Define VectorSearch configuration
        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(
            new HnswAlgorithmConfiguration("default-HNSW")
            {
                Parameters = new HnswParameters
                {
                    Metric = VectorSearchAlgorithmMetric.Cosine
                }
            });

        vectorSearch.Algorithms.Add(
            new ExhaustiveKnnAlgorithmConfiguration("default") 
            {
                Parameters = new ExhaustiveKnnParameters
                {
                    Metric = VectorSearchAlgorithmMetric.Cosine
                }
            });

        vectorSearch.Profiles.Add(new VectorSearchProfile("default", "default")); // Provided required parameters
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-HNSW", "default-HNSW")); // Provided required parameters

        var index = new SearchIndex(indexName)
        {
            Fields = fields,
            VectorSearch = vectorSearch
        };

        indexClient.CreateOrUpdateIndex(index);
        Console.WriteLine($"Index '{indexName}' created or updated successfully.");
    }
}