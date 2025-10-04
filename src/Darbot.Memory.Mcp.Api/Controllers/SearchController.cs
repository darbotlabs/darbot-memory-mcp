using Darbot.Memory.Mcp.Core.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Darbot.Memory.Mcp.Api.Controllers;

/// <summary>
/// Enhanced search API controller with AI-native capabilities
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly IEnhancedSearchService _searchService;
    private readonly IConversationContextManager _contextManager;
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        IEnhancedSearchService searchService,
        IConversationContextManager contextManager,
        ILogger<SearchController> logger)
    {
        _searchService = searchService;
        _contextManager = contextManager;
        _logger = logger;
    }

    /// <summary>
    /// Enhanced search with AI-native features and relevance scoring
    /// </summary>
    /// <param name="request">Enhanced search request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Enhanced search response with scored results</returns>
    [HttpPost("enhanced")]
    public async Task<ActionResult<EnhancedSearchResponse>> EnhancedSearchAsync(
        [FromBody] EnhancedSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();
            var requestWithUserId = request with { UserId = userId };

            _logger.LogInformation("Enhanced search request from user {UserId}: {Query}", userId, request.Query);

            var response = await _searchService.SearchAsync(requestWithUserId, cancellationToken);

            _logger.LogInformation("Enhanced search completed: {ResultCount} results in {SearchTime}ms",
                response.Results.Count, response.SearchTime.TotalMilliseconds);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enhanced search failed for query: {Query}", request.Query);
            return StatusCode(500, new { error = "Search failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Get search suggestions based on query and user patterns
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Search suggestions</returns>
    [HttpGet("suggestions")]
    public async Task<ActionResult<SearchSuggestionsResponse>> GetSuggestionsAsync(
        [FromQuery] string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = "Query parameter is required" });
            }

            _logger.LogDebug("Getting suggestions for query: {Query}", query);

            var response = await _searchService.GetSuggestionsAsync(query, cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get suggestions for query: {Query}", query);
            return StatusCode(500, new { error = "Failed to get suggestions", message = ex.Message });
        }
    }

    /// <summary>
    /// Get personalized search suggestions based on user patterns
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Personalized suggestions</returns>
    [HttpGet("personalized-suggestions")]
    public async Task<ActionResult<IReadOnlyList<PersonalizedSuggestion>>> GetPersonalizedSuggestionsAsync(
        [FromQuery] string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();

            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = "Query parameter is required" });
            }

            _logger.LogDebug("Getting personalized suggestions for user {UserId}, query: {Query}", userId, query);

            var suggestions = await _contextManager.GetPersonalizedSuggestionsAsync(userId, query, cancellationToken);

            return Ok(suggestions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get personalized suggestions for query: {Query}", query);
            return StatusCode(500, new { error = "Failed to get personalized suggestions", message = ex.Message });
        }
    }

    /// <summary>
    /// Get related conversations based on context similarity
    /// </summary>
    /// <param name="conversationId">Target conversation ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Related conversations</returns>
    [HttpGet("related/{conversationId}")]
    public async Task<ActionResult<RelatedConversationsResponse>> GetRelatedConversationsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return BadRequest(new { error = "Conversation ID is required" });
            }

            _logger.LogDebug("Getting related conversations for: {ConversationId}", conversationId);

            var response = await _searchService.GetRelatedConversationsAsync(conversationId, cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get related conversations for: {ConversationId}", conversationId);
            return StatusCode(500, new { error = "Failed to get related conversations", message = ex.Message });
        }
    }

    /// <summary>
    /// Record search interaction for learning and improvement
    /// </summary>
    /// <param name="interaction">Search interaction details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpPost("interaction")]
    public async Task<ActionResult> RecordInteractionAsync(
        [FromBody] SearchInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();
            var interactionWithUserId = interaction with { UserId = userId };

            _logger.LogDebug("Recording search interaction for user {UserId}: {Type}", userId, interaction.Type);

            await _searchService.RecordSearchInteractionAsync(interactionWithUserId, cancellationToken);

            return Ok(new { message = "Interaction recorded successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record search interaction");
            return StatusCode(500, new { error = "Failed to record interaction", message = ex.Message });
        }
    }

    /// <summary>
    /// Get user conversation analytics and patterns
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User analytics</returns>
    [HttpGet("analytics")]
    public async Task<ActionResult<ConversationAnalytics>> GetUserAnalyticsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();

            _logger.LogDebug("Getting analytics for user {UserId}", userId);

            var analytics = await _contextManager.AnalyzeUserPatternsAsync(userId, cancellationToken);

            return Ok(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user analytics");
            return StatusCode(500, new { error = "Failed to get analytics", message = ex.Message });
        }
    }

    /// <summary>
    /// Record conversation interaction for pattern learning
    /// </summary>
    /// <param name="interaction">Conversation interaction details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpPost("conversation-interaction")]
    public async Task<ActionResult> RecordConversationInteractionAsync(
        [FromBody] ConversationInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();
            var interactionWithUserId = interaction with { UserId = userId };

            _logger.LogDebug("Recording conversation interaction for user {UserId}: {ConversationId}",
                userId, interaction.ConversationId);

            await _contextManager.RecordConversationInteractionAsync(userId, interactionWithUserId, cancellationToken);

            return Ok(new { message = "Conversation interaction recorded successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record conversation interaction");
            return StatusCode(500, new { error = "Failed to record conversation interaction", message = ex.Message });
        }
    }

    /// <summary>
    /// Update search pattern for user learning
    /// </summary>
    /// <param name="pattern">Search pattern details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    [HttpPost("pattern")]
    public async Task<ActionResult> UpdateSearchPatternAsync(
        [FromBody] SearchPattern pattern,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();

            _logger.LogDebug("Updating search pattern for user {UserId}: {Query}", userId, pattern.Query);

            await _contextManager.UpdateSearchPatternAsync(userId, pattern, cancellationToken);

            return Ok(new { message = "Search pattern updated successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update search pattern");
            return StatusCode(500, new { error = "Failed to update search pattern", message = ex.Message });
        }
    }

    /// <summary>
    /// Get user conversation context
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User context</returns>
    [HttpGet("context")]
    public ActionResult<ConversationContext> GetUserContext(CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = GetUserId();

            _logger.LogDebug("Getting context for user {UserId}", userId);

            var context = _contextManager.GetOrCreateContext(userId);

            return Ok(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user context");
            return StatusCode(500, new { error = "Failed to get context", message = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint for search functionality
    /// </summary>
    /// <returns>Health status</returns>
    [HttpGet("health")]
    [AllowAnonymous]
    public ActionResult<object> HealthCheck()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "v1.0.0",
            features = new[]
            {
                "enhanced_search",
                "relevance_scoring",
                "personalized_suggestions",
                "context_management",
                "pattern_learning"
            }
        });
    }

    private string GetUserId()
    {
        // Extract user ID from claims or use a default
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return userIdClaim?.Value ?? "anonymous";
    }
}