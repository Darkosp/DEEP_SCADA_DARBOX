using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// The tenant's edges, the device carrying each one's link, and the devices each one reads
/// (ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// An edge hangs off the tenant rather than a Site: its name is the identity in its certificate
/// and the segment the broker carries its topics under, so it has to be unique across the whole
/// deployment (ADR-0015, ADR-0017). Nothing scopes it to a Site, and by the rule an unscoped tag
/// already follows that makes the whole resource Admin-only (ADR-0011) — there is no Site to hold
/// an answer against, so there is no not-found that would tell a lesser caller less than a refusal
/// already does.
/// </para>
/// <para>
/// Which devices an edge reads is not set here: that is an ordinary edit of the device, and
/// <c>EdgeId</c> on its save request is the only way it changes (ADR-0019 §2). It is shown here
/// because that assignment is the edge's membership, and nowhere else reports it.
/// </para>
/// </remarks>
internal static class EdgeEndpoints
{
    internal static void MapEdgeApi(this WebApplication app)
    {
        var edges = app.MapGroup("/api/edges").RequireAuthorization(Policies.Admin);

        edges.MapGet("", (TagCatalogSource catalogSource) =>
        {
            var catalog = catalogSource.Current;

            return Results.Ok(catalog.Edges
                .OrderBy(edge => edge.Name, StringComparer.OrdinalIgnoreCase)
                .Select(edge => new EdgeDto(
                    edge.Id,
                    edge.Name,
                    edge.LinkDeviceId,
                    catalog.Devices
                        .Where(device => device.EdgeId == edge.Id)
                        .Select(device => device.Id)
                        .ToArray())));
        });

        edges.MapPost("", async (
            SaveEdgeRequest request,
            TagCatalogSource catalogSource,
            DriverShapes shapes,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var catalog = catalogSource.Current;

            if (ProblemWithLink(request.LinkDeviceId, catalog, shapes) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var edge = new Edge
            {
                Id = Guid.NewGuid(),
                // The tenant is the ownership scope (ADR-0004), not a field of the form.
                TenantId = catalog.Tenant.Id,
                Name = request.Name,
                LinkDeviceId = request.LinkDeviceId,
            };

            return await ConfigurationEndpoints.SaveAsync(
                () => repository.AddAsync(edge, cancellationToken),
                reloader,
                cancellationToken,
                () => Results.Created($"/api/edges/{edge.Id}", edge.Id));
        }).AdminWrite("edge.create", "edge");

        edges.MapPut("/{edgeId:guid}", async (
            Guid edgeId,
            SaveEdgeRequest request,
            TagCatalogSource catalogSource,
            DriverShapes shapes,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (ProblemWithLink(request.LinkDeviceId, catalogSource.Current, shapes) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var edge = new Edge
            {
                Id = edgeId,
                TenantId = catalogSource.Current.Tenant.Id,
                Name = request.Name,
                LinkDeviceId = request.LinkDeviceId,
            };

            return await ConfigurationEndpoints.SaveAsync(
                () => repository.UpdateAsync(edge, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent);
        }).AdminWrite("edge.update", "edge", "edgeId");

        edges.MapDelete("/{edgeId:guid}", async (
            Guid edgeId,
            IEdgeRepository repository,
            ConfigurationReloader reloader,
            CancellationToken cancellationToken) => await ConfigurationEndpoints.SaveAsync(
                () => repository.DeleteAsync(edgeId, cancellationToken),
                reloader,
                cancellationToken,
                Results.NoContent)).AdminWrite("edge.delete", "edge", "edgeId");
    }

    /// <summary>
    /// Why this device cannot carry an edge's link, or null when it can (ADR-0016, ADR-0019).
    /// </summary>
    /// <remarks>
    /// A link is the Gateway's end of the connection the edge already holds, so it has to be a
    /// device that pushes; naming a polled one would leave every device assigned to that edge with
    /// nothing reading it. The device also has to be configured at all — the catalogue holds live
    /// devices only, so an id that has been deleted is refused here rather than passing the
    /// foreign key and leaving the edge naming a device that is gone (ADR-0009).
    /// </remarks>
    private static string? ProblemWithLink(Guid? linkDeviceId, TagCatalog catalog, DriverShapes shapes)
    {
        if (linkDeviceId is not { } deviceId)
        {
            return null;
        }

        if (catalog.FindDevice(deviceId) is not { } device)
        {
            return "There is no such device to carry this edge's link.";
        }

        return shapes.Pushes(device.DriverKey)
            ? null
            : $"'{device.DriverKey}' is polled, not pushing, so a device using it cannot carry an edge's link.";
    }
}
