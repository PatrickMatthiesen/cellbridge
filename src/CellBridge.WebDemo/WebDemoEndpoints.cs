using CellBridge.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;

namespace CellBridge.WebDemo;

public static class WebDemoEndpoints
{
    public static IServiceCollection AddWebDemo(this IServiceCollection services)
    {
        services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/"));
        services.AddScoped<DocumentWorkspace>();
        return services;
    }

    public static IEndpointRouteBuilder MapWebDemo(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.LocalRedirect("/library")).AllowAnonymous();
        app.MapRazorPages();
        app.MapGet("/api/documents", async (HttpContext context, DocumentWorkspace workspace) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var offset = int.TryParse(context.Request.Query["offset"], out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
            var limit = int.TryParse(context.Request.Query["limit"], out var parsedLimit) ? Math.Clamp(parsedLimit, 1, 1000) : 1000;
            var actor = CellBridgeActor.FromPrincipal(context.User);
            if (actor is null) return Results.Unauthorized();
            var page = await workspace.ReadAsync(actor, offset, limit, context.RequestAborted);
            if (page.NextOffset is { } nextOffset)
                context.Response.Headers["X-CellBridge-Next-Offset"] = nextOffset.ToString();
            return Results.Ok(page.Documents);
        }).RequireAuthorization();
        app.MapPost("/api/documents", async (CreateDocumentRequest request, HttpContext context, DocumentWorkspace workspace) =>
        {
            var actor = CellBridgeActor.FromPrincipal(context.User);
            if (actor is null) return Results.Unauthorized();
            if (!actor.CanCreate) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var result = await workspace.CreateAsync(request.Name, request.Type, actor, context.RequestAborted);
            return result.StatusCode == StatusCodes.Status201Created
                ? Results.Created(result.Location!, result.Document)
                : Results.Json(new { error = result.Error, budget = result.Budget }, statusCode: result.StatusCode);
        }).RequireAuthorization();
        return app;
    }
}
