-- The device that carries an edge's link (ADR-0019).
--
-- An edge's values reach the Gateway over a pushing device: an MQTT subscription to the topics
-- the edge publishes under (ADR-0016, ADR-0017). Once a device is assigned to an edge the Gateway
-- stops polling it, so that link is the only thing feeding its tags — the link carries the
-- assigned devices' tags, and the staleness limit the link declares is what turns its silence
-- into Bad rather than into a last value held for ever. Nothing else can answer "which device is
-- this edge's link", so the schema holds it.
--
-- Nullable, because an edge may be created before its link. That an edge with no link may have no
-- devices assigned to it is deliberately not a schema constraint: it spans two tables and an
-- assignment statement, the shape edge deletion already takes (migration 0012), and a composite
-- key could not express it without duplicating the edge's link onto every device.
ALTER TABLE edge ADD COLUMN link_device_id uuid REFERENCES device (id);

-- The reverse lookup: the devices one device carries, and the guard that refuses to delete a
-- device an edge still names as its link.
CREATE INDEX ix_edge_link_device ON edge (link_device_id) WHERE deleted_at IS NULL;

-- edge_active names its columns, so adding one means recreating it — the maintenance cost
-- migration 0004 predicted, and 0007 and 0012 have each paid. Nothing is built on edge_active
-- but the repository.
DROP VIEW edge_active;

CREATE VIEW edge_active AS
SELECT id, tenant_id, name, link_device_id
FROM edge
WHERE deleted_at IS NULL;
