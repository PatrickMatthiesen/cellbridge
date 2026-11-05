using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CellBridge.AspNetCore;

/// <summary>Maps an authenticated, anti-forgery-protected HTTP document delete endpoint.</summary>
public static class DocumentDeletionEndpoint
{
    private const int MaximumRequestBytes = 1024;

    /// <summary>
    /// Maps DELETE /api/documents/{resourceId} at the application root. Configure ASP.NET Core
    /// antiforgery with a request header name so JSON requests can submit the paired request token.
    /// </summary>
    public static IEndpointRouteBuilder MapCellBridgeDocumentDeletion(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapDelete("/api/documents/{resourceId:guid}", DeleteAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> DeleteAsync(Guid resourceId, HttpContext context,
        CellBridgeDocumentService service, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        var actor = CellBridgeActor.FromPrincipal(context.User);
        if (actor is null) return Results.Unauthorized();

        var originalToken = context.RequestAborted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        deadline.CancelAfter(context.RequestServices.GetRequiredService<CellBridgeOptions>().RequestTimeout);
        ProtocolRequestAdmission.AdmissionResult admitted;
        try
        {
            admitted = await context.RequestServices.GetRequiredService<ProtocolRequestAdmission>()
                .AcquireAsync(actor.Identity.Subject, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        using var lease = admitted.Lease;
        if (lease is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(admitted.RejectionStatus);
        }

        context.RequestAborted = deadline.Token;
        try
        {
            if (context.Request.ContentLength is > MaximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            byte[] body;
            try { body = await ProtocolRequestBody.ReadAsync(context.Request.Body, MaximumRequestBytes, deadline.Token); }
            catch (ProtocolRequestBody.RequestBodyLimitException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }

            DeleteDocumentRequest? request;
            try { request = JsonSerializer.Deserialize<DeleteDocumentRequest>(body, JsonSerializerOptions.Web); }
            catch (JsonException) { return Results.BadRequest(); }
            if (request?.ExpectedLifecycleGeneration is not > 0 || request.ExpectedStateVersion is not >= 0)
                return Results.BadRequest();

            try
            {
                var result = await service.DeleteDocumentAsync(resourceId, request.ExpectedLifecycleGeneration.Value,
                    request.ExpectedStateVersion.Value, actor, deadline.Token);
                return result switch
                {
                    DocumentDeletionStatus.Deleted => Results.NoContent(),
                    DocumentDeletionStatus.NotFound => Results.NotFound(),
                    _ => Results.Conflict(),
                };
            }
            catch (UnauthorizedAccessException)
            {
                // Hide document existence from actors who cannot delete it.
                return Results.NotFound();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        finally
        {
            context.RequestAborted = originalToken;
        }
    }

    private sealed record DeleteDocumentRequest(long? ExpectedLifecycleGeneration, long? ExpectedStateVersion);
}
