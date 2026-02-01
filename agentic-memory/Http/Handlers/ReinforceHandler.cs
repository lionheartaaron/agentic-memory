using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Handlers;

/// <summary>
/// Handle memory reinforcement requests
/// </summary>
public class ReinforceHandler : IHandler
{
    private readonly IMemoryRepository? _repository;

    public ReinforceHandler(IMemoryRepository? repository = null)
    {
        _repository = repository;
    }

    public async Task<Response> HandleAsync(Request request, CancellationToken cancellationToken)
    {
        if (request.Method != Models.HttpMethod.POST)
        {
            return Response.MethodNotAllowed("Use POST to reinforce a memory");
        }

        var idStr = request.GetParameter("id");
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
        {
            return Response.BadRequest("Invalid or missing memory ID");
        }

        // Use real repository if available
        if (_repository is not null)
        {
            var entity = await _repository.GetAsync(id, cancellationToken);
            if (entity is null)
            {
                return Response.NotFound($"Memory node with ID {id} not found");
            }

            // Reinforce the memory
            await _repository.ReinforceAsync(id, cancellationToken);

            return Response.Ok(new { message = "Memory reinforced successfully", id });
        }

        // Fallback - just return success
        return Response.Ok(new { message = "Memory reinforced successfully (mock)", id });
    }
}
