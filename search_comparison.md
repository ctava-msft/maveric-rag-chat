# Search Method Comparison: Keyword vs. Vector Search

## Score Scale Differences

| Search Method | Score Range | Example Scores | Meaning |
|---------------|-------------|----------------|---------|
| Keyword Search | Normalized 0 to 1 | 0.70 - 0.85 | Based on exact term matches and frequency |
| Vector Search | Normalized 0 to 1 | 0.61 - 0.69 | Based on semantic similarity in vector space |
| Hybrid Search (Keyword + Vector) | Normalized 0 to 1 | 0.65 - 0.78 | Combines keyword matching with semantic similarity |
| Hybrid Search + Semantic Ranker | Normalized 0 to 1 | 0.75 - 0.88 | Relevance prioritized by AI understanding |
| Hybrid Search + Semantic Ranker + Query Rewriting | Normalized 0 to 1 | 0.80 - 0.92 | Maximized relevance through query optimization |

## Key Differences

1. **Keyword Search**
   - Focuses on finding exact word matches
   - Scores based on frequency and importance of matched terms
   - Normalized to 0-1 scale for comparison with other methods
   - Best for precise, terminology-specific queries

2. **Vector Search**
   - Focuses on semantic meaning and context
   - Scores represent similarity between query and document in vector space
   - Native 0-1 range representing cosine similarity
   - Best for conceptual, meaning-based queries

3. **Hybrid Search (Keyword + Vector)**
   - Combines both keyword matching and semantic similarity
   - Balances exact term matches with contextual understanding
   - Normalized to 0-1 scale for comparison
   - Best for queries that need both precision and semantic understanding

4. **Hybrid Search + Semantic Ranker**
   - Enhances hybrid search with AI-powered relevance ranking
   - Re-ranks results based on deeper semantic understanding
   - Normalized to 0-1 scale for comparison
   - Best for complex queries where understanding intent is crucial

5. **Hybrid Search + Semantic Ranker + Query Rewriting**
   - Automatically reformulates user queries for optimal search performance
   - Expands abbreviated terms, adds synonyms, and clarifies ambiguities
   - Normalized to 0-1 scale for comparison
   - Best for handling natural language queries and user questions

## When to Use Each Method

- **Keyword Search**: When looking for specific terms, exact matches, or when terminology is standardized
- **Vector Search**: When looking for conceptual matches, similar ideas expressed in different words, or when exploring related concepts
- **Hybrid Search**: When you need balanced results that capture both exact matches and semantically related content
- **Semantic Ranking**: When result quality and relevance are critical, especially for complex topics
- **Query Rewriting**: When handling natural language queries from users who may not use exact technical terminology

## Evaluating Performance

The true measure of search performance is relevance to the user's intent, not the numerical score. With normalized scores, you can more directly compare results across different search methods. Generally, higher scores (closer to 1) indicate higher relevance regardless of the method used.

Note that score normalization is performed to allow for comparison across methods. The raw scores from each method may use different scales and algorithms before normalization.

Advanced search methods like hybrid search with semantic ranking and query rewriting typically provide the best user experience for natural language questions, though they require more computational resources.
