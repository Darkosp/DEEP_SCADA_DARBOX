using Dapper;
using Npgsql;
using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Templates;
using static ScadaDarbox.Persistence.TimescaleDb.ConfigurationRows;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Device template storage and instance materialisation (ADR-0008, ADR-0010).</summary>
public sealed class DeviceTemplateRepository : IDeviceTemplateRepository
{
    private const string TemplateTagColumns = """
        SELECT id, template_id, name, value_kind, unit_symbol, unit_dimension,
               unit_factor_to_si, unit_offset_to_si, address_template, is_writable
        FROM device_template_tag_active
        """;

    private readonly NpgsqlDataSource _dataSource;

    static DeviceTemplateRepository() => DapperConfiguration.Ensure();

    public DeviceTemplateRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<DeviceTemplate>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeviceTemplateRow>(
            new CommandDefinition(
                "SELECT id, tenant_id, name FROM device_template_active ORDER BY name",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<DeviceTemplateTag>> GetTagsAsync(
        Guid templateId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeviceTemplateTagRow>(
            new CommandDefinition(
                $"{TemplateTagColumns} WHERE template_id = @templateId ORDER BY name",
                new { templateId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(DeviceTemplate template, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO device_template (id, tenant_id, name) VALUES (@Id, @TenantId, @Name)",
            template,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task InstantiateAsync(Device device, CancellationToken cancellationToken)
    {
        if (device.TemplateId is not { } templateId)
        {
            throw new InvalidOperationException("This device has no template to instantiate from.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var templateTags = (await connection.QueryAsync<DeviceTemplateTagRow>(
            new CommandDefinition(
                $"{TemplateTagColumns} WHERE template_id = @templateId",
                new { templateId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false))
            .Select(r => r.ToDomain())
            .ToList();

        // Addresses are resolved before anything is written, so a template naming a
        // parameter this device lacks fails without having created a half-built device.
        var tags = templateTags
            .Select(templateTag => Materialise(templateTag, device))
            .ToList();

        // The device and its tags go in together; a clash on either name refuses the whole
        // instance, and the rollback leaves nothing half-made.
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO device (id, site_id, folder_id, name, driver_key, connection_settings,
                                    scan_interval_ms, template_id)
                VALUES (@Id, @SiteId, @FolderId, @Name, @DriverKey, @ConnectionSettings::jsonb,
                        @ScanIntervalMs, @TemplateId)
                """,
                new
                {
                    device.Id,
                    device.SiteId,
                    device.FolderId,
                    device.Name,
                    device.DriverKey,
                    ConnectionSettings = ConnectionSettingsJson.Serialize(device.ConnectionSettings),
                    ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds,
                    device.TemplateId,
                },
                transaction,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            foreach (var (name, value) in device.TemplateParameters)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO device_template_parameter (device_id, name, value) VALUES (@deviceId, @name, @value)",
                    new { deviceId = device.Id, name, value },
                    transaction,
                    cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

            foreach (var tag in tags)
            {
                await InsertTagAsync(connection, transaction, tag, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw exception.ConstraintName == UniqueNames.DeviceIndex
                ? await UniqueNames.DeviceTakenAsync(_dataSource, device.Name, device.SiteId, device.FolderId, cancellationToken).ConfigureAwait(false)
                : UniqueNames.TemplateTagsCollide();
        }
    }

    public async Task<int> AddTagAsync(DeviceTemplateTag templateTag, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var instances = await InstancesOfAsync(connection, null, templateTag.TemplateId, cancellationToken)
            .ConfigureAwait(false);

        // Resolve every instance's address up front. If one instance is missing a
        // parameter the whole edit is refused rather than applied to the others, which
        // would leave the template's instances disagreeing about their own shape.
        var materialised = instances
            .Select(instance => Materialise(templateTag, instance))
            .ToList();

        // The new tag lands on every instance at once (ADR-0010). If any of them already has a
        // tag by that name, none of them gets it.
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO device_template_tag (id, template_id, name, value_kind, unit_symbol,
                                                 unit_dimension, unit_factor_to_si, unit_offset_to_si,
                                                 address_template, is_writable)
                VALUES (@Id, @TemplateId, @Name, @ValueKind, @UnitSymbol, @UnitDimension,
                        @UnitFactorToSi, @UnitOffsetToSi, @AddressTemplate, @IsWritable)
                """,
                TemplateTagParameters(templateTag),
                transaction,
                cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            foreach (var tag in materialised)
            {
                await InsertTagAsync(connection, transaction, tag, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (UniqueNames.IsNameClash(exception))
        {
            throw await UniqueNames.TemplateTagTakenAsync(_dataSource, templateTag.Name, templateTag.TemplateId, cancellationToken).ConfigureAwait(false);
        }
        return materialised.Count;
    }

    public async Task<int> RemoveTagAsync(Guid templateTagId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var removed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE device_template_tag SET deleted_at = now() WHERE id = @templateTagId AND deleted_at IS NULL",
            new { templateTagId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (removed == 0)
        {
            throw new ConfigurationConflictException($"Template tag {templateTagId} no longer exists.");
        }

        // Soft delete, through the same mechanism as any other tag (ADR-0009): the
        // materialised tags disappear from browsing while their history stays queryable.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE tag SET deleted_at = now() WHERE template_tag_id = @templateTagId AND deleted_at IS NULL",
            new { templateTagId },
            transaction,
            cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <summary>Builds the real tag one instance gets from one template tag.</summary>
    private static Tag Materialise(DeviceTemplateTag templateTag, Device instance) => new()
    {
        // Its own identity, not the template's: history is per device (ADR-0001).
        Id = Guid.NewGuid(),
        DeviceId = instance.Id,
        Name = templateTag.Name,
        ValueKind = templateTag.ValueKind,
        Unit = templateTag.Unit,
        SourceAddress = AddressTemplate.Resolve(templateTag.AddressTemplate, instance.TemplateParameters),
        IsWritable = templateTag.IsWritable,
        TemplateTagId = templateTag.Id,
    };

    /// <summary>Live devices built from one template, each with its own parameters.</summary>
    private static async Task<IReadOnlyList<Device>> InstancesOfAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        var devices = (await connection.QueryAsync<DeviceRow>(
            new CommandDefinition(
                """
                SELECT id, site_id, folder_id, name, driver_key,
                       connection_settings::text AS connection_settings, scan_interval_ms,
                       template_id, edge_id
                FROM device_active
                WHERE template_id = @templateId
                """,
                new { templateId },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false))
            .Select(r => r.ToDomain())
            .ToList();

        if (devices.Count == 0)
        {
            return devices;
        }

        var parameters = await connection.QueryAsync<(Guid DeviceId, string Name, string Value)>(
            new CommandDefinition(
                """
                SELECT device_id, name, value
                FROM device_template_parameter
                WHERE device_id = ANY(@ids)
                """,
                new { ids = devices.Select(d => d.Id).ToArray() },
                transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var byDevice = parameters
            .GroupBy(p => p.DeviceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(p => p.Name, p => p.Value));

        return devices
            .Select(device => new Device
            {
                Id = device.Id,
                SiteId = device.SiteId,
                FolderId = device.FolderId,
                Name = device.Name,
                DriverKey = device.DriverKey,
                ConnectionSettings = device.ConnectionSettings,
                ScanInterval = device.ScanInterval,
                TemplateId = templateId,
                EdgeId = device.EdgeId,
                TemplateParameters = byDevice.GetValueOrDefault(device.Id, new Dictionary<string, string>()),
            })
            .ToList();
    }

    private static Task InsertTagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Tag tag,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO tag (id, device_id, name, value_kind, unit_symbol, unit_dimension,
                             unit_factor_to_si, unit_offset_to_si, source_address, is_writable,
                             template_tag_id)
            VALUES (@Id, @DeviceId, @Name, @ValueKind, @UnitSymbol, @UnitDimension,
                    @UnitFactorToSi, @UnitOffsetToSi, @SourceAddress, @IsWritable, @TemplateTagId)
            """,
            new
            {
                tag.Id,
                tag.DeviceId,
                tag.Name,
                ValueKind = (short)tag.ValueKind,
                UnitSymbol = tag.Unit?.Symbol,
                UnitDimension = tag.Unit is null ? (short?)null : (short)tag.Unit.Dimension,
                UnitFactorToSi = tag.Unit?.FactorToSi,
                UnitOffsetToSi = tag.Unit?.OffsetToSi,
                tag.SourceAddress,
                tag.IsWritable,
                tag.TemplateTagId,
            },
            transaction,
            cancellationToken: cancellationToken));

    private static object TemplateTagParameters(DeviceTemplateTag templateTag) => new
    {
        templateTag.Id,
        templateTag.TemplateId,
        templateTag.Name,
        ValueKind = (short)templateTag.ValueKind,
        UnitSymbol = templateTag.Unit?.Symbol,
        UnitDimension = templateTag.Unit is null ? (short?)null : (short)templateTag.Unit.Dimension,
        UnitFactorToSi = templateTag.Unit?.FactorToSi,
        UnitOffsetToSi = templateTag.Unit?.OffsetToSi,
        templateTag.AddressTemplate,
        templateTag.IsWritable,
    };
}
