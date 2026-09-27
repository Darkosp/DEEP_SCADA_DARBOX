using Npgsql;
using ScadaDarbox.Persistence.TimescaleDb;
using Xunit;

namespace ScadaDarbox.Persistence.Tests;

/// <summary>
/// ADR-0015: an upgrade over a database that already holds duplicate names renames the
/// later rows instead of failing, audits every rename, and ends with the indexes in place.
/// </summary>
/// <remarks>
/// The database is taken back to exactly how it was before migration 0010 — which only adds
/// the renames and the indexes — by dropping the indexes and 0010's journal entry. Then the
/// duplicates go in, and the production migrator runs again.
/// </remarks>
public sealed class DuplicateNameMigrationTests
{
    private const string Script = "ScadaDarbox.Persistence.TimescaleDb.migrations.0010_names_unique_within_parent.sql";

    // Ids chosen so their order is known: within each group the lowest keeps its name.
    private static readonly Guid Tenant = Id(0x01);
    private static readonly Guid Site = Id(0x02);
    private static readonly Guid Hall = Id(0x10);
    private static readonly Guid HallAgain = Id(0x11);
    private static readonly Guid Room = Id(0x12);
    private static readonly Guid RoomAgain = Id(0x13);
    private static readonly Guid Booster = Id(0x20);
    private static readonly Guid BoosterLowerCase = Id(0x21);
    private static readonly Guid BoosterDeleted = Id(0x22);
    private static readonly Guid Pump = Id(0x30);
    private static readonly Guid PumpAgain = Id(0x31);
    private static readonly Guid PumpThird = Id(0x32);
    private static readonly Guid Only = Id(0x40);
    private static readonly Guid Pressure = Id(0x50);
    private static readonly Guid PressureAgain = Id(0x51);

    [RequiresDatabaseFact]
    public async Task Duplicates_already_in_the_database_are_renamed_audited_and_then_indexed()
    {
        var name = await TestDatabase.CreateEmptyAsync();
        try
        {
            var connectionString = TestDatabase.ConnectionStringFor(name);
            await MigrateAsync(connectionString);
            await using var db = NpgsqlDataSource.Create(connectionString);

            // Back to before 0010, then the duplicates.
            await ExecuteAsync(db, $"""
                DROP INDEX ux_folder_name_in_parent, ux_device_name_in_parent, ux_tag_name_in_device;
                DELETE FROM schemaversions WHERE scriptname = '{Script}';

                INSERT INTO tenant (id, name) VALUES ('{Tenant}', 'Tenant');
                INSERT INTO site (id, tenant_id, name, time_zone_id) VALUES ('{Site}', '{Tenant}', 'Skopje', 'Europe/Skopje');

                -- Two root folders (null parent), and two sub-folders in one of them.
                INSERT INTO folder (id, site_id, parent_folder_id, name) VALUES
                    ('{Hall}', '{Site}', NULL, 'Hall'),
                    ('{HallAgain}', '{Site}', NULL, 'Hall'),
                    ('{Room}', '{Site}', '{Hall}', 'Room'),
                    ('{RoomAgain}', '{Site}', '{Hall}', 'Room');

                -- Directly under the Site (null folder): a pair differing only in case, a
                -- deleted third that must keep its name, and a triple in a folder.
                INSERT INTO device (id, site_id, folder_id, name, driver_key) VALUES
                    ('{Booster}', '{Site}', NULL, 'Booster', 'modbus-tcp'),
                    ('{BoosterLowerCase}', '{Site}', NULL, 'booster', 'modbus-tcp'),
                    ('{BoosterDeleted}', '{Site}', NULL, 'Booster', 'modbus-tcp'),
                    ('{Pump}', '{Site}', '{Hall}', 'Pump', 'modbus-tcp'),
                    ('{PumpAgain}', '{Site}', '{Hall}', 'Pump', 'modbus-tcp'),
                    ('{PumpThird}', '{Site}', '{Hall}', 'Pump', 'modbus-tcp'),
                    ('{Only}', '{Site}', NULL, 'Only', 'modbus-tcp');
                UPDATE device SET deleted_at = now() WHERE id = '{BoosterDeleted}';

                INSERT INTO tag (id, device_id, name, value_kind, source_address) VALUES
                    ('{Pressure}', '{Booster}', 'Pressure', 0, 'holding:0'),
                    ('{PressureAgain}', '{Booster}', 'Pressure', 0, 'holding:1');
                """);

            // The upgrade. It must not fail on data someone already has.
            await MigrateAsync(connectionString);

            // The lowest id in each group keeps its name; every other one is renamed after its id.
            Assert.Equal("Hall", await NameAsync(db, "folder", Hall));
            Assert.Equal(Renamed("Hall", HallAgain), await NameAsync(db, "folder", HallAgain));
            Assert.Equal("Room", await NameAsync(db, "folder", Room));
            Assert.Equal(Renamed("Room", RoomAgain), await NameAsync(db, "folder", RoomAgain));
            Assert.Equal("Booster", await NameAsync(db, "device", Booster));
            Assert.Equal(Renamed("booster", BoosterLowerCase), await NameAsync(db, "device", BoosterLowerCase));
            Assert.Equal("Pump", await NameAsync(db, "device", Pump));
            Assert.Equal(Renamed("Pump", PumpAgain), await NameAsync(db, "device", PumpAgain));
            Assert.Equal(Renamed("Pump", PumpThird), await NameAsync(db, "device", PumpThird));
            Assert.Equal("Pressure", await NameAsync(db, "tag", Pressure));
            Assert.Equal(Renamed("Pressure", PressureAgain), await NameAsync(db, "tag", PressureAgain));

            // Untouched: a deleted row does not hold a name, so it is no duplicate; nor is a unique one.
            Assert.Equal("Booster", await NameAsync(db, "device", BoosterDeleted));
            Assert.Equal("Only", await NameAsync(db, "device", Only));

            // One audit entry per rename, naming both names, with no actor — nobody did it.
            var audit = await ListAsync(db, """
                SELECT action || '|' || entity_id || '|' || (detail->>'from') || '|' || (detail->>'to') || '|' || coalesce(actor_user_id::text, 'none')
                FROM audit_log WHERE action LIKE '%.rename_duplicate' ORDER BY entity_id
                """);
            Assert.Equal(
                new[]
                {
                    Audit("folder", HallAgain, "Hall"),
                    Audit("folder", RoomAgain, "Room"),
                    Audit("device", BoosterLowerCase, "booster"),
                    Audit("device", PumpAgain, "Pump"),
                    Audit("device", PumpThird, "Pump"),
                    Audit("tag", PressureAgain, "Pressure"),
                },
                audit);

            // And the indexes exist, unique, recorded as applied. The edge index is 0012's, not
            // this migration's, but it answers the same question — a name unique within its
            // parent (ADR-0015) — so it belongs in this list too.
            var indexes = await ListAsync(db, """
                SELECT c.relname FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                WHERE i.indisunique AND c.relname LIKE 'ux\_%\_name\_%' ORDER BY 1
                """);
            Assert.Equal(
                ["ux_device_name_in_parent", "ux_edge_name_in_tenant", "ux_folder_name_in_parent", "ux_tag_name_in_device"],
                indexes);
            Assert.Equal(["1"], await ListAsync(db, $"SELECT count(*)::text FROM schemaversions WHERE scriptname = '{Script}'"));
        }
        finally
        {
            await TestDatabase.DropEmptyAsync(name);
        }
    }

    private static Guid Id(int n) => new($"00000000-0000-4000-8000-0000000000{n:x2}");

    // The whole id: these test ids deliberately share their first eight characters, as the
    // demo seed's do, which is what caught a shortened suffix making two renames identical.
    private static string Renamed(string name, Guid id) => $"{name} (duplicate {id})";

    private static string Audit(string entity, Guid id, string name) =>
        $"{entity}.rename_duplicate|{id}|{name}|{Renamed(name, id)}|none";

    private static Task MigrateAsync(string connectionString) =>
        DatabaseMigrator.RunAsync(connectionString, TestDatabase.ApplicationPassword, DatabaseMigrator.DefaultLockTimeout, CancellationToken.None);

    private static async Task<string> NameAsync(NpgsqlDataSource db, string table, Guid id)
    {
        await using var command = db.CreateCommand($"SELECT name FROM {table} WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource db, string sql)
    {
        await using var command = db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ListAsync(NpgsqlDataSource db, string sql)
    {
        await using var command = db.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
