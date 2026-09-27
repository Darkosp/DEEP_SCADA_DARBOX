using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Persistence.TimescaleDb;

/// <summary>Flat row shapes matching the configuration tables, one per table.</summary>
/// <remarks>
/// Dapper maps columns onto these by name. The projection from a row to a domain
/// object stays hand-written (ADR-0008 is explicit that Dapper does not remove it):
/// a <see cref="UnitOfMeasure"/> is assembled from four columns and must be absent
/// rather than half-built when the dimension or factor is missing.
/// </remarks>
internal static class ConfigurationRows
{
    internal sealed record SiteRow(Guid Id, Guid TenantId, string Name, string TimeZoneId)
    {
        internal Site ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            TimeZoneId = TimeZoneId,
        };
    }

    internal sealed record FolderRow(Guid Id, Guid SiteId, Guid? ParentFolderId, string Name)
    {
        internal Folder ToDomain() => new()
        {
            Id = Id,
            SiteId = SiteId,
            ParentFolderId = ParentFolderId,
            Name = Name,
        };
    }

    internal sealed record DeviceRow(
        Guid Id,
        Guid SiteId,
        Guid? FolderId,
        string Name,
        string DriverKey,
        string ConnectionSettings,
        int ScanIntervalMs,
        Guid? TemplateId,
        Guid? EdgeId)
    {
        internal Device ToDomain() => new()
        {
            Id = Id,
            SiteId = SiteId,
            FolderId = FolderId,
            Name = Name,
            DriverKey = DriverKey,
            ConnectionSettings = ConnectionSettingsJson.Deserialize(ConnectionSettings),
            ScanInterval = TimeSpan.FromMilliseconds(ScanIntervalMs),
            TemplateId = TemplateId,
            EdgeId = EdgeId,
        };
    }

    internal sealed record EdgeRow(Guid Id, Guid TenantId, string Name, Guid? LinkDeviceId)
    {
        internal Edge ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
            LinkDeviceId = LinkDeviceId,
        };
    }

    internal sealed record DeviceTemplateRow(Guid Id, Guid TenantId, string Name)
    {
        internal DeviceTemplate ToDomain() => new()
        {
            Id = Id,
            TenantId = TenantId,
            Name = Name,
        };
    }

    internal sealed record DeviceTemplateTagRow(
        Guid Id,
        Guid TemplateId,
        string Name,
        short ValueKind,
        string? UnitSymbol,
        short? UnitDimension,
        double? UnitFactorToSi,
        double? UnitOffsetToSi,
        string AddressTemplate,
        bool IsWritable)
    {
        internal DeviceTemplateTag ToDomain() => new()
        {
            Id = Id,
            TemplateId = TemplateId,
            Name = Name,
            ValueKind = (TagValueKind)ValueKind,
            Unit = UnitDimension is null || UnitFactorToSi is null
                ? null
                : new UnitOfMeasure(
                    UnitSymbol ?? string.Empty,
                    (Dimension)UnitDimension.Value,
                    UnitFactorToSi.Value,
                    UnitOffsetToSi ?? 0.0),
            AddressTemplate = AddressTemplate,
            IsWritable = IsWritable,
        };
    }

    internal sealed record AlarmDefinitionRow(Guid Id, Guid TagId, double? HighLimit, double? LowLimit)
    {
        internal AlarmDefinition ToDomain() => new()
        {
            Id = Id,
            TagId = TagId,
            HighLimit = HighLimit,
            LowLimit = LowLimit,
        };
    }

    internal sealed record TagRow(
        Guid Id,
        Guid DeviceId,
        string Name,
        short ValueKind,
        string? UnitSymbol,
        short? UnitDimension,
        double? UnitFactorToSi,
        double? UnitOffsetToSi,
        string SourceAddress,
        bool IsWritable,
        Guid? TemplateTagId)
    {
        internal Tag ToDomain() => new()
        {
            Id = Id,
            DeviceId = DeviceId,
            Name = Name,
            ValueKind = (TagValueKind)ValueKind,
            // A bare symbol is not a unit (ADR-0005): without a dimension and a factor
            // there is nothing to convert with, so the unit is absent rather than partial.
            Unit = UnitDimension is null || UnitFactorToSi is null
                ? null
                : new UnitOfMeasure(
                    UnitSymbol ?? string.Empty,
                    (Dimension)UnitDimension.Value,
                    UnitFactorToSi.Value,
                    UnitOffsetToSi ?? 0.0),
            SourceAddress = SourceAddress,
            IsWritable = IsWritable,
            TemplateTagId = TemplateTagId,
        };
    }
}
