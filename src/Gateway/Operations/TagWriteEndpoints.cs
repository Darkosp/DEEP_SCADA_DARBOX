using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Operations;

/// <summary>
/// Writing a value to a tag — the write path Phase 5 had to build before "cannot write to a
/// tag" meant anything (ADR-0011).
/// </summary>
internal static class TagWriteEndpoints
{
    internal static void MapTagWriteApi(this WebApplication app) =>
        app.MapPost("/api/tags/{tagId:guid}/value", async (
            Guid tagId,
            WriteTagValueRequest request,
            Caller caller,
            TagCatalogSource catalogSource,
            TagWriter writer,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var catalog = catalogSource.Current;
            var tag = catalog.FindTag(tagId);
            var device = tag is null ? null : catalog.FindDevice(tag.DeviceId);

            // Not found, rather than forbidden, for a Site the caller cannot see: a refusal
            // would confirm the tag exists.
            if (tag is null || device is null || !caller.Access.CanView(device.SiteId))
            {
                return Results.NotFound();
            }

            if (!caller.Access.CanOperate(device.SiteId))
            {
                return ApiErrors.Forbidden("Writing a tag needs the Operator role on its Site.");
            }

            if (!tag.IsWritable)
            {
                return Results.BadRequest(new { error = "This tag is not writable." });
            }

            // A device an edge acquires is not reachable from here (ADR-0019 §3). The link is
            // outbound only, so the write below would open a connection that cannot be made and
            // then report a device error that never happened — an untrue refusal, the class
            // ADR-0003 exists to prevent. Refused by name instead, and recorded: an operator
            // deserves to know which edge holds the device. Routing the write to that edge is a
            // decision of its own and is not taken here.
            if (catalog.EdgeOfDevice(device.Id) is { } edge)
            {
                await audit.AppendAsync(
                    new AuditEntry(
                        caller.UserId,
                        "tag.write_refused",
                        "tag",
                        tag.Id,
                        Audit.Detail(
                            ("siteId", device.SiteId),
                            ("deviceId", device.Id),
                            ("edgeId", edge.Id))),
                    CancellationToken.None);

                return Results.Conflict(new
                {
                    error = $"This tag is read by edge '{edge.Name}', which the Gateway cannot reach.",
                });
            }

            if (!TryReadValue(request.Value, tag.ValueKind, out var value))
            {
                return Results.BadRequest(new { error = $"This tag takes a {tag.ValueKind.ToString().ToLowerInvariant()} value." });
            }

            var detail = Audit.Detail(
                ("siteId", device.SiteId),
                ("deviceId", device.Id),
                ("value", TagValueDto.From(value)));

            try
            {
                await writer.WriteAsync(device, tag, value, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Recorded too: the attempt may have reached the equipment even though it
                // did not report success.
                detail["error"] = exception.Message;
                await audit.AppendAsync(new AuditEntry(caller.UserId, "tag.write_failed", "tag", tag.Id, detail), CancellationToken.None);

                return Results.Json(
                    new { error = $"The device did not accept the write: {exception.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            await audit.AppendAsync(new AuditEntry(caller.UserId, "tag.write", "tag", tag.Id, detail), CancellationToken.None);
            return Results.NoContent();
        });

    private static bool TryReadValue(JsonElement json, TagValueKind kind, [NotNullWhen(true)] out TagValue? value)
    {
        value = kind switch
        {
            TagValueKind.Numeric when json.ValueKind == JsonValueKind.Number
                                      && json.TryGetDouble(out var number)
                                      && double.IsFinite(number) => new TagValue.Numeric(number),
            TagValueKind.Boolean when json.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                new TagValue.Boolean(json.GetBoolean()),
            TagValueKind.Text when json.ValueKind == JsonValueKind.String => new TagValue.Text(json.GetString()!),
            TagValueKind.Discrete when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var code) =>
                new TagValue.Discrete(code),
            _ => null,
        };

        return value is not null;
    }
}

/// <summary>
/// Hands a write to the driver that owns the tag's device.
/// </summary>
/// <remarks>
/// The write opens its own short-lived connection through the device's driver factory
/// rather than borrowing the scan loop's. The client libraries behind the drivers are not
/// safe for concurrent use, so sharing would mean coordinating every write with a poll in
/// progress; a connection per write costs a little latency on an action an operator takes
/// by hand, which is the cheaper side of that trade.
/// </remarks>
public sealed class TagWriter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factoriesByKey;

    public TagWriter(IEnumerable<IDeviceDriverFactory> driverFactories) =>
        _factoriesByKey = driverFactories.ToDictionary(factory => factory.DriverKey, StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(Device device, Tag tag, TagValue value, CancellationToken cancellationToken)
    {
        if (!_factoriesByKey.TryGetValue(device.DriverKey, out var factory))
        {
            throw new InvalidOperationException($"Driver '{device.DriverKey}' is not part of this build.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await using var driver = factory.Create(device);
            await driver.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await driver.WriteAsync(new DriverTag(tag.Id, tag.SourceAddress, tag.ValueKind), value, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The device did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }
}
