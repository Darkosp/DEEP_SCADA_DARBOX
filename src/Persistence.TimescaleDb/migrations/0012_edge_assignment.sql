-- The edge assignment (ADR-0019): an Edge the cloud owns, and the devices it acquires.
--
-- An edge hangs off the tenant rather than a site, the shape device_template already takes
-- (migration 0007) and for a sharper reason. An edge's identity is the name in its own client
-- certificate, and the broker's topic namespace ('scada/edge/{name}/...') is shared by every
-- edge in the deployment (ADR-0017). Two sites under one tenant must not be able to mint the
-- same edge name, or their certificates would authenticate as the same broker user and their
-- topics would collide. A name unique within the tenant is exactly that guarantee, and one
-- deployment holds one tenant (ADR-0004).
CREATE TABLE edge (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL REFERENCES tenant (id),
    name       text NOT NULL,
    deleted_at timestamptz
);

-- ADR-0013: since migration 0009 a new table is granted SELECT and INSERT only, and one that
-- needs more asks for it in the migration that creates it. An edge is renamed and soft-deleted,
-- both UPDATEs, so this is that ask; nothing ever hard-deletes one.
GRANT UPDATE ON edge TO scada_app;

-- A device is acquired by at most one edge (ADR-0019). Nullable, and null is the ordinary case:
-- a device with no assignment is polled by the Gateway itself, exactly as before this migration,
-- so the on-premises topology is unchanged.
--
-- Deliberately a plain key, not the same-site composite folder_id carries. An edge is
-- tenant-scoped, so there is no site column to duplicate alongside it; and with one tenant per
-- deployment a device and an edge can only ever belong to the same one.
ALTER TABLE device ADD COLUMN edge_id uuid REFERENCES edge (id);

CREATE VIEW edge_active AS
SELECT id, tenant_id, name
FROM edge
WHERE deleted_at IS NULL;

-- device_active names its columns, so adding one to the base table means recreating it — the
-- maintenance cost migration 0004 predicted, and migration 0007 paid once already. Nothing is
-- built on device_active, so this is the whole cascade.
DROP VIEW device_active;

CREATE VIEW device_active AS
SELECT id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms,
       template_id, edge_id
FROM device
WHERE deleted_at IS NULL;

-- ADR-0015: a name is unique within its parent, among live rows, ignoring case. An edge's parent
-- is its tenant. No rename step is needed as there was in 0010 — the table is new, so it cannot
-- already hold a duplicate.
CREATE UNIQUE INDEX ux_edge_name_in_tenant
    ON edge (tenant_id, lower(name))
    WHERE deleted_at IS NULL;

-- The read that finds the devices an edge acquires, and the reverse lookup a device edit makes.
CREATE INDEX ix_device_edge ON device (edge_id) WHERE deleted_at IS NULL;
