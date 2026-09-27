# ADR-0019 — How an edge is configured: the cloud is the source of truth, delivered over the link it already has

**Status:** Accepted
**Complements:** ADR-0001 (the tag's stable id), ADR-0002 (the core/module
boundary), ADR-0003 (a value that has no honest reading is not invented),
ADR-0011 (the audit trail), ADR-0016 (a driver declares itself polled or
pushing), ADR-0017 (the edge-to-cloud link).
**Date:** 2026-09-27

## Context

Phase 7 built the link: an edge reads a plant, buffers what it read, and
ships it to the cloud Gateway over MQTT with a certificate per edge
(ADR-0017). What Phase 7 did not decide is where the edge's list of devices
and tags comes from. At the time this ADR was written it was a hand-written
`edge.json`, mounted into the edge container read-only, and it had to name the
**cloud** Gateway's tag ids — `src/EdgeAgent/EdgeOptions.cs` said so
explicitly, and called out that how the list reaches an edge was still open.
That is the question this ADR answers. The file is still written by hand
today, because the cloud derives no configuration yet: the only part of the
decision below that is built is the assignment — which edge reads which device
— and the Gateway's consequent refusal to poll it (2026-09-27).

That leaves two lists a human must keep in agreement:

- the cloud Gateway's MQTT device and its tags, created in the web client;
- the edge's `edge.json`, listing the plant's devices and, for each tag, its
  address and the cloud tag's id.

The only thing joining them is the tag id, typed by hand on both sides. The
costs of getting this wrong are quiet and real. A mistyped id attaches a
plant's readings to the wrong tag, or to none, and the mistake surfaces as a
wrong number on a screen rather than as an error. Adding a device in a plant
needs a person to edit a file on a machine at that plant and paste ids out of
a browser. Nothing knows which edge owns which device, so nothing can be
reconciled, audited or automated, and the two lists drifting apart is
invisible.

The forces that shape any answer:

- **The link is outbound only.** No inbound port is opened at the plant —
  ADR-0017's own constraint, and the reason the topology survives an OT
  security review. So a configuration must be either fetched by the edge or
  delivered over the connection the edge already holds.
- **An edge already has an identity.** Its certificate's name is its id, and
  the broker's ACL lets it publish under that name only (ADR-0017).
- **The cloud is where configuration already lives.** Devices, tags, folders
  and their addresses are configured in the Gateway and scoped by Site
  (ADR-0001, ADR-0011). The edge is the only place a second, independent copy
  exists.
- **The cloud's view of an edge is flatter than the plant.** The Gateway
  models an edge as a single MQTT device holding every tag the edge reads,
  while the edge reads one or more plant devices, each with its own driver,
  scan interval and address space. The plant's structure exists only at the
  edge today.

## Decision

**1. The cloud is the source of truth for what an edge reads.**

A device, its tags, and each tag's plant address and kind are configured in
the cloud Gateway. The edge holds no independently authored list; the file it
is started with is derived, not typed.

**2. An `Edge` exists as an entity, and a device is assigned to at most one
edge.**

The edge's identity is the name in its certificate (ADR-0017). The assignment
— which edge reads which device — is what makes "this edge's configuration" a
question with an answer. A device with no assignment is acquired by the
Gateway itself, exactly as today, so the on-premises topology is unchanged.

**3. A device assigned to an edge is acquired by that edge, not by the
Gateway.**

Its driver key, scan interval, settings, and its tags' addresses travel to the
edge, which reads them; the Gateway does not poll it and receives its values
over the link instead. The same driver module runs on both sides (ADR-0002),
so a device's driver names *how it is read where it is reachable* — the same
meaning in both topologies. This keeps a plant's real shape in the cloud's
browse tree instead of collapsing every edge into one flat MQTT device.

**4. The Gateway derives each edge's configuration and publishes it over the
link that already exists.**

A per-edge topic under the edge's own prefix, retained and versioned; the edge
subscribes to it over the same outbound TLS connection it already holds. No
inbound port is opened at the plant (ADR-0017 holds), and the broker's ACL
already confines an edge to its own prefix.

**5. The configuration is versioned, and the edge keeps the last one it
accepted.**

The edge applies the newest version it has seen and keeps the last accepted
one across a restart. While the link is down it keeps reading the last
configuration it had — store-and-forward applies to configuration as it does
to samples. A change made while an edge is away is delivered when it returns,
because the topic is retained.

**6. A new configuration is applied by restarting acquisition.**

The edge reads its configuration once at start-up (`IOptions<EdgeOptions>`,
and `AcquisitionService` iterates it). A newer version is applied by restarting
the acquisition service. Live reload is a later refinement and is not part of
this decision.

**7. The tag id is never typed by hand.**

The derived configuration carries each tag's own stable id (ADR-0001). The two
hand-typed lists become one.

## Consequences

- The operator configures a plant once, in the cloud. No file is edited on a
  machine in a plant, and no id is copied out of a browser.
- A plant's devices appear in the cloud's browse tree in their real shape —
  one node per device — instead of every edge flattened into a single MQTT
  device. The per-edge MQTT device an operator creates today becomes a
  property of the assignment rather than something anyone configures.
- The Gateway must distinguish the devices it polls from the ones it only
  receives. That distinction already exists as a property of the driver
  (ADR-0016); this ADR gives it a second source — the edge assignment —
  rather than inventing a new one.
- A tag's plant address becomes cloud configuration, so changing it is an
  audited configuration change (ADR-0011) rather than a file edit nobody sees.
- The link now carries configuration as well as samples. A configuration is
  small and changes rarely, so it rides a retained topic without competing
  with the sample stream.
- Removing a device or a tag from an edge's assignment stops the edge reading
  it. Samples already in the edge's buffer are still delivered — the buffer is
  not rewritten — and the tag receives nothing new, so its last value goes Bad
  by the staleness rule (ADR-0016) rather than freezing silently.
- The configuration format is a compatibility surface the project now owns,
  exactly as the sample payload is (ADR-0017), and needs a version field from
  the first message.
- **Writing to a tag whose device is assigned to an edge is not decided here,
  and is refused until it is.** The link is outbound only, so the Gateway has
  no route to that device: the write path opens its own connection to the
  device (`TagWriter`) and would wait out its ten-second deadline before
  reporting a device error that never happened. A write to such a tag is
  therefore refused with a named reason and audited, rather than attempted.
  Routing a write to the edge — over the link, with the result reported back —
  is a decision of its own and needs an ADR before any code.
- **A tag whose device is assigned to an edge is watched by the link that
  carries it, and no new setting is invented for that.** *Added while
  implementing (2026-09-27).* Once the Gateway stops polling such a device,
  nothing feeds its tags, so they must go Bad by the staleness rule (ADR-0016)
  rather than freeze at their last value (ADR-0003). But ADR-0016 gives the
  limit to a **driver**, and the Gateway has no driver for a device an edge
  reads: it never opens a connection to it. The limit therefore belongs to the
  transport that does carry the values — the edge's **link**, which in the
  Gateway is a pushing device whose `stalenessSeconds` is already the declared
  limit for everything it carries. So an edge names its link device, that
  device carries the tags of every device assigned to the edge, and its limit
  covers them. One limit per link rather than per plant device, because one
  link is what is either silent or not. `PushedSources:ClockSkewTolerance` is
  not reused for this: it is about a clock, not about silence.
  That makes the two halves one change. Excluding a device from polling before
  its link carries its tags would leave those tags with no source at all, and
  the MQTT driver **refuses** a sample naming a tag the device does not carry,
  so the link has to know the assignment before it can deliver anything. For
  the same reason an assignment is refused, by name, unless the edge has a
  link device; and a link device is not deleted, nor an edge's link cleared or
  moved, while devices are assigned. There is deliberately no state in which a
  device is read by an edge while the Gateway neither polls it nor watches a
  link for it.
- **Not decided here, and left to implementation:** live reload instead of a
  restart; the exact topic and payload shape; whether an edge may be sent a
  device it cannot reach; how a device moving from one edge to another is
  ordered so that no reading is attributed twice; and the link device being
  derived from the edge rather than named, so that nothing about an edge is
  typed by hand.

## Verified in review by

- A device and its tags are configured in the cloud and assigned to an edge;
  the edge reads them with **no hand-written file on the plant machine**, and
  its readings reach the history under the cloud's own tag ids.
- A device with no edge assignment is still acquired by the Gateway itself —
  the on-premises path is unchanged.
- With an edge offline, a configuration change is made in the cloud; on
  reconnect the edge applies it, and until then it kept reading the
  configuration it had.
- The edge keeps its last accepted configuration across a restart taken while
  the link is down.
- There is no path in which a human types a tag id into an edge's
  configuration.
- A write to a tag whose device is assigned to an edge is refused with a named
  reason and audited, and no connection is opened to a device the Gateway
  cannot reach (ADR-0003: a refusal must not report a failure that did not
  happen).
- With a device assigned to an edge that names a link device, the Gateway
  starts **no scan loop** for that device, and the link device is started with
  the device's tags among those it carries. Removing the assignment filter
  makes the scan loop start again; removing the link's wider tag list makes the
  device's samples be refused as tags the link does not have.
- An assignment is refused, by name, unless the edge has a link device; and a
  link device is not deleted, nor an edge's link cleared or moved, while
  devices are assigned to it. There is no state in which a device is read by an
  edge while the Gateway neither polls it nor watches a link for it.
- No Core type mentions MQTT, Mosquitto or a topic (ADR-0002, ADR-0016,
  ADR-0017).
