/** A unit as the gateway sends it: a dimension and its conversion to SI, not a bare symbol. */
export interface Unit {
  symbol: string;
  dimension: string;
  factorToSi: number;
  offsetToSi: number;
}

export interface TreeTag {
  id: string;
  name: string;
  valueKind: 'Numeric' | 'Boolean' | 'Text' | 'Discrete';
  unit: Unit | null;
  sourceAddress: string;
  isWritable: boolean;
}

export interface TreeDevice {
  id: string;
  name: string;
  driverKey: string;
  connectionSettings: Record<string, string>;
  /** Null for a pushing device, which has no scan interval (ADR-0016). */
  scanIntervalMs: number | null;
  folderId: string | null;
  tags: TreeTag[];
}

export interface TreeFolder {
  id: string;
  name: string;
  parentFolderId: string | null;
  folders: TreeFolder[];
  devices: TreeDevice[];
}

export interface SiteTree {
  siteId: string;
  name: string;
  timeZoneId: string;
  folders: TreeFolder[];
  devices: TreeDevice[];
}

export interface Site {
  id: string;
  name: string;
  timeZoneId: string;
}

export interface HistorySample {
  value: {
    kind: string;
    numeric?: number | null;
    boolean?: boolean | null;
    text?: string | null;
    code?: number | null;
    label?: string | null;
  };
  sourceTimestampUtc: string;
  ingestedAtUtc: string;
  quality: string;
}

/** A device type, instantiated many times (ADR-0010). */
export interface DeviceTemplate {
  id: string;
  name: string;
}

/** One tag on a template, with the parameters its address needs. */
export interface TemplateTag {
  id: string;
  name: string;
  valueKind: 'Numeric' | 'Boolean' | 'Text' | 'Discrete';
  unit: Unit | null;
  addressTemplate: string;
  isWritable: boolean;
  parameterNames: string[];
}

/** A connection setting as the UI edits it — a name and a value, nothing driver-specific. */
export interface SettingEntry {
  key: string;
  value: string;
}

/** Turns the editable pairs back into the map the API expects, dropping blank names. */
export function settingsToMap(entries: SettingEntry[]): Record<string, string> {
  const map: Record<string, string> = {};
  for (const entry of entries) {
    if (entry.key.trim().length > 0) {
      map[entry.key.trim()] = entry.value;
    }
  }
  return map;
}

export function mapToSettings(map: Record<string, string>): SettingEntry[] {
  return Object.entries(map).map(([key, value]) => ({ key, value }));
}

export type AlarmState = 'Active' | 'Acknowledged' | 'Cleared' | 'Shelved';

/** One standing alarm, as pushed by the gateway. */
export interface Alarm {
  /** This alarm, as distinct from earlier and later ones on the same definition. */
  occurrenceId: string;
  definitionId: string;
  tagId: string;
  /** The Site the alarm was raised in, fixed at the raise. */
  siteId: string;
  tagPath: string;
  limit: 'High' | 'Low';
  limitValue: number;
  valueAtRaise: number;
  unitSymbol: string | null;
  raisedAtUtc: string;
  state: AlarmState;
  acknowledgedAtUtc: string | null;
  /** Who acknowledged it, by their username at the time. */
  acknowledgedBy: string | null;
  clearedAtUtc: string | null;
  /** When a shelf ends; set only while shelved. */
  shelvedUntilUtc: string | null;
  /** First seen on the first evaluation after a Gateway restart, so likely began unwatched. */
  detectedAfterRestart: boolean;
}

/** A configured threshold on a tag. */
export interface AlarmDefinition {
  id: string;
  tagId: string;
  highLimit: number | null;
  lowLimit: number | null;
}

/**
 * A tag's history plus the names it was recorded under.
 *
 * The names resolve even when the tag or its device has been deleted, so a trend for
 * retired equipment reads as a name rather than an identifier.
 */
export interface TagHistory {
  tagId: string;
  tagName: string | null;
  deviceName: string | null;
  isDeleted: boolean;
  samples: HistorySample[];
}

/** A folder option in a picker, with its depth so the list can read as a tree. */
export interface FolderOption {
  id: string | null;
  label: string;
  depth: number;
}

/** Flattens a site's folders into a pickable list, parents before children. */
export function folderOptions(tree: SiteTree | null): FolderOption[] {
  const options: FolderOption[] = [{ id: null, label: 'Directly under the site', depth: 0 }];

  const walk = (folders: TreeFolder[], depth: number): void => {
    for (const folder of folders) {
      options.push({ id: folder.id, label: folder.name, depth });
      walk(folder.folders, depth + 1);
    }
  };

  walk(tree?.folders ?? [], 1);
  return options;
}

// ---- edges (ADR-0019) -------------------------------------------------------

/**
 * An edge: one agent on the plant floor that reads the devices assigned to it and pushes their
 * values on. It hangs off the deployment rather than a Site, and its name is the identity in
 * its own certificate (ADR-0019).
 */
export interface Edge {
  id: string;
  name: string;
  /** The device carrying its link, or null while it has none. */
  linkDeviceId: string | null;
  /** The devices it reads. Assigning one is an ordinary edit of the device (ADR-0019). */
  deviceIds: string[];
}

/** An edge as a picker option, where null is "not on an edge". */
export interface EdgeOption {
  id: string | null;
  label: string;
}

/** The edges as a picker, "not on an edge" first. */
export function edgeOptions(edges: Edge[]): EdgeOption[] {
  return [
    { id: null, label: 'Not on an edge' },
    ...edges.map((edge) => ({ id: edge.id, label: edge.name })),
  ];
}

/**
 * The edge a device is assigned to, or null.
 *
 * Read from the edges rather than from the tree, because the tree carries what a device is and
 * the assignment is not one of those things: it belongs to the edge, which is why assigning is
 * an ordinary edit of the device and nothing else (ADR-0019).
 */
export function edgeOfDevice(edges: Edge[], deviceId: string): Edge | null {
  return edges.find((edge) => edge.deviceIds.includes(deviceId)) ?? null;
}

/** Every device in a site tree, folders walked. */
export function treeDevices(
  tree: { folders: TreeFolder[]; devices: TreeDevice[] } | null,
): TreeDevice[] {
  const all: TreeDevice[] = [...(tree?.devices ?? [])];

  const walk = (folders: TreeFolder[]): void => {
    for (const folder of folders) {
      all.push(...folder.devices);
      walk(folder.folders);
    }
  };

  walk(tree?.folders ?? []);
  return all;
}

/**
 * What an edge reads, as far as the tree being browsed knows it.
 *
 * The client holds one Site's tree at a time and an edge is tenant-wide, so a device this tree
 * does not have is counted rather than named: a name would have to be invented for it.
 */
export function edgeReads(
  tree: { folders: TreeFolder[]; devices: TreeDevice[] } | null,
  edge: Edge,
): { names: string[]; elsewhere: number } {
  const devices = treeDevices(tree);
  const names: string[] = [];
  let elsewhere = 0;

  for (const id of edge.deviceIds) {
    const device = devices.find((candidate) => candidate.id === id);
    if (device === undefined) {
      elsewhere += 1;
    } else {
      names.push(device.name);
    }
  }

  return { names, elsewhere };
}

/**
 * The devices an edge's link may be: the pushing devices of the Site being browsed, and the one
 * it already has even when that device is not in this Site.
 *
 * A link has to be a pushing device, because a polled one would leave every device the edge
 * reads with nothing reading it (ADR-0016). The one it already has is kept in the list even
 * when this Site cannot offer it, because a picker that dropped it would release the link the
 * next time anything on the form was saved.
 */
export function linkOptions(
  devices: TreeDevice[],
  drivers: DriverShape[],
  current: string | null,
): EdgeOption[] {
  const pushing = devices.filter((device) => pushes(drivers, device.driverKey));
  const options: EdgeOption[] = [
    { id: null, label: 'No link' },
    ...pushing.map((device) => ({ id: device.id, label: device.name })),
  ];

  if (current !== null && !pushing.some((device) => device.id === current)) {
    options.push({ id: current, label: 'A device outside this Site' });
  }

  return options;
}

// ---- users and access (ADR-0011) --------------------------------------------

/** A role on one Site. Admin is not one of them: it is tenant-wide, a flag on the user. */
export type SiteRole = 'Viewer' | 'Operator';

export interface SiteRoleGrant {
  siteId: string;
  role: SiteRole;
}

/** A user and what they may do, as the gateway reports it. */
export interface Access {
  userId: string;
  username: string;
  isAdmin: boolean;
  sites: SiteRoleGrant[];
}

export interface LoginResponse {
  token: string;
  access: Access;
}

/**
 * One entry in the alarm journal (ADR-0013).
 *
 * `siteId` is null on the engine's own events — EvaluationStarted, EvaluationStopped and
 * JournalGap — which belong to no Site because an outage applies to the whole Gateway.
 * Every signed-in user sees those; alarm events are filtered to the reader's Sites by the
 * Gateway, never here.
 *
 * SamplesLost and SourceClockSkew are about a pushing source — an edge (ADR-0017). They name
 * a device and its Site, and no alarm.
 */
export interface AlarmEvent {
  type: string;
  recordedAtUtc: string;
  sourceTimeUtc: string | null;
  occurrenceId: string | null;
  definitionId: string | null;
  tagId: string | null;
  siteId: string | null;
  tagPath: string | null;
  limit: string | null;
  limitValue: number | null;
  value: number | null;
  unitSymbol: string | null;
  actorUsername: string | null;
  detectedAfterRestart: boolean;
  shelvedUntilUtc: string | null;
  reason: string | null;
  gapFromUtc: string | null;
  gapUntilUtc: string | null;
  unrecordedTransitions: number | null;
  deviceId: string | null;
  lostSamples: number | null;
  clockSkewSeconds: number | null;
}

/**
 * Whether an entry is the engine speaking: it names neither an alarm nor a Site. A source's
 * own event names no alarm either, but it does name the Site its device is on.
 */
export function isEngineEvent(entry: Pick<AlarmEvent, 'occurrenceId' | 'siteId'>): boolean {
  return entry.occurrenceId === null && entry.siteId === null;
}

/**
 * What a source's own journal entry says, in words: how many samples it dropped, or how far
 * its clock is from the Gateway's and in which direction. Null for every other entry.
 */
export function describeSourceEvent(
  entry: Pick<AlarmEvent, 'type' | 'lostSamples' | 'clockSkewSeconds'>,
): string | null {
  if (entry.type === 'SamplesLost' && entry.lostSamples !== null) {
    return `${entry.lostSamples} ${entry.lostSamples === 1 ? 'sample' : 'samples'} dropped at the source`;
  }

  if (entry.type === 'SourceClockSkew' && entry.clockSkewSeconds !== null) {
    const direction = entry.clockSkewSeconds > 0 ? 'ahead of' : 'behind';
    return `source clock ${formatDuration(Math.abs(entry.clockSkewSeconds))} ${direction} the Gateway's`;
  }

  return null;
}

/** A length of time as a reader would say it: seconds, then minutes, then hours. */
function formatDuration(seconds: number): string {
  if (seconds < 120) {
    return `${Math.round(seconds)} s`;
  }

  if (seconds < 7200) {
    return `${Math.round(seconds / 60)} min`;
  }

  return `${(seconds / 3600).toFixed(1)} h`;
}

/**
 * What a numeric form field actually holds.
 *
 * An `<input type="number">` bound with `ngModel` hands over a **number** — or `null`
 * once the box is empty or its contents are not a number — never the string the draft
 * was seeded with. Declaring such a field `string` is a lie the compiler cannot catch,
 * and it is what let `raw.trim()` reach a number and throw on the first save of an alarm
 * threshold.
 */
export type NumberField = string | number | null | undefined;

/** Either a value — `null` meaning the field was left blank — or a refusal. */
export type ParsedNumber = { readonly ok: true; readonly value: number | null } | { readonly ok: false };

/**
 * Reads a number out of a form field, whatever the binding handed over.
 *
 * Blank stays blank: it becomes `null`, never `0`. For a limit those mean opposite
 * things — "no limit on this side" against "the limit is zero" — and zero is an
 * ordinary threshold. Anything that is not a number is refused rather than passed on as
 * `NaN`, which `JSON.stringify` would quietly turn into `null` and so into "no limit".
 */
export function parseNumberField(raw: NumberField): ParsedNumber {
  if (raw === null || raw === undefined) {
    return { ok: true, value: null };
  }

  if (typeof raw === 'number') {
    return Number.isFinite(raw) ? { ok: true, value: raw } : { ok: false };
  }

  const text = raw.trim();
  if (text.length === 0) {
    return { ok: true, value: null };
  }

  // A decimal comma is what a Macedonian keyboard produces. A thousands separator
  // would leave a second one behind and fail the check below, which is the right
  // answer: refuse it rather than guess which one the operator meant.
  const value = Number(text.replace(',', '.'));
  return Number.isFinite(value) ? { ok: true, value } : { ok: false };
}

/** Two digits, so 9:5:3 never reads as 9:5:3. */
function pad(value: number): string {
  return value.toString().padStart(2, '0');
}

/**
 * A measurement as an operator should read it.
 *
 * A double straight from the wire reads as `4.8100000000000005 bar`, which is the
 * arithmetic showing through rather than a pressure anyone measured. Two decimals, the
 * same as the live value elsewhere in the client, so the two never appear to disagree.
 */
export function formatMeasurement(value: number | null | undefined, unitSymbol?: string | null): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—';
  }

  const text = value.toFixed(2);
  return unitSymbol ? `${text} ${unitSymbol}` : text;
}

/**
 * The window a gap covers, as a reader can place in time.
 *
 * Times alone are enough while both ends fall on the same day. Across midnight they are
 * not: "15:31:33 – 14:13:44" reads as an interval that ran backwards, when it is in fact
 * an outage of nearly a day. Both ends then carry their date.
 */
export function formatGapWindow(
  from: string | Date | null | undefined,
  to: string | Date | null | undefined,
): string | null {
  const start = toDate(from);
  const end = toDate(to);

  if (start === null || end === null) {
    return null;
  }

  const sameDay =
    start.getFullYear() === end.getFullYear() &&
    start.getMonth() === end.getMonth() &&
    start.getDate() === end.getDate();

  const time = (at: Date) => `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`;
  const dated = (at: Date) =>
    `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${time(at)}`;

  return sameDay ? `${time(start)} – ${time(end)}` : `${dated(start)} – ${dated(end)}`;
}

function toDate(value: string | Date | null | undefined): Date | null {
  if (value === null || value === undefined) {
    return null;
  }

  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/**
 * Why an occurrence was retired, in words rather than in the engine's own vocabulary.
 *
 * An unknown reason is shown as it stands. A reason this client has not been taught is
 * still worth more to a reader than nothing at all, and silence would hide it from
 * whoever has to notice the gap.
 */
export function describeReason(reason: string | null | undefined): string | null {
  if (!reason) {
    return null;
  }

  switch (reason) {
    case 'superseded-by-new-breach':
      return 'replaced by a new alarm';
    case 'superseded':
      return 'replaced by a later occurrence';
    case 'definition-removed':
      return 'threshold deleted';
    case 'retirement-completed-at-startup':
      return 'closed at startup';
    default:
      return reason;
  }
}

/** How many devices a tree holds, at any depth of folders. */
export function deviceCount(tree: { folders: TreeFolder[]; devices: TreeDevice[] }): number {
  return tree.devices.length + tree.folders.reduce((sum, folder) => sum + deviceCount(folder), 0);
}

/**
 * The Site to open on: the first, in the order given, that has a device to look at, or the
 * first of all when none has. Found walking the Phase 6 gate: opening on a Site with nothing
 * in it — the seeded second Site holds only an empty folder — reads as "nothing works".
 *
 * @param treeOf Loads one Site's tree. Sites after the chosen one are never loaded.
 */
export async function siteToOpen(
  sites: Site[],
  treeOf: (siteId: string) => Promise<{ folders: TreeFolder[]; devices: TreeDevice[] }>,
): Promise<string | null> {
  for (const site of sites) {
    if (deviceCount(await treeOf(site.id)) > 0) {
      return site.id;
    }
  }

  return sites[0]?.id ?? null;
}

/** A Site's name for display, from the Sites the reader may see. */
export function siteName(sites: Site[], siteId: string): string {
  return sites.find((site) => site.id === siteId)?.name ?? '—';
}

/**
 * A tag's path without its leading Site, for a table that names the Site in its own column.
 * Only a leading segment that is exactly that Site's name is removed; anything else is shown
 * whole rather than guessed at.
 */
export function pathWithinSite(tagPath: string, siteName: string): string {
  const prefix = `${siteName}/`;
  return tagPath.startsWith(prefix) ? tagPath.slice(prefix.length) : tagPath;
}

/**
 * The forms whose name the Gateway may refuse as already taken (ADR-0015). An edge's name is
 * unique in the deployment rather than in a Site, and it is refused the same way.
 */
export type NameField = 'folder' | 'device' | 'tag' | 'instance' | 'templateTag' | 'edge';

/**
 * The Gateway's reason when it refused a name as already taken — a 409 — or null for any other
 * failure, which belongs in the general error line rather than against the name field.
 */
export function nameConflictMessage(error: unknown): string | null {
  const refusal = error as { status?: unknown; message?: unknown } | null;
  return refusal !== null && typeof refusal === 'object' && refusal.status === 409 && typeof refusal.message === 'string'
    ? refusal.message
    : null;
}

/**
 * Runs one save at a time: a second press while the first is still in flight does nothing.
 *
 * A courtesy against a double click, and no more (ADR-0015). It cannot help a retry after a
 * lost reply, or two people saving the same name; the database's unique index is what
 * guarantees that, and a 409 is how it says so.
 */
export class SingleFlight {
  private running = false;

  constructor(private readonly onBusyChange: (busy: boolean) => void = () => undefined) {}

  get busy(): boolean {
    return this.running;
  }

  /** Runs the action unless one is already running; says whether it ran. */
  async run(action: () => Promise<void>): Promise<boolean> {
    if (this.running) {
      return false;
    }

    this.running = true;
    this.onBusyChange(true);
    try {
      await action();
      return true;
    } finally {
      this.running = false;
      this.onBusyChange(false);
    }
  }
}

/** A driver this build has, and whether it pushes its values rather than being polled (ADR-0016). */
export interface DriverShape {
  key: string;
  pushing: boolean;
}

/** Whether devices of this driver push; an unknown key is treated as polled, as the Gateway does. */
export function pushes(drivers: DriverShape[], driverKey: string): boolean {
  const key = driverKey.trim().toLowerCase();
  return drivers.some((driver) => driver.pushing && driver.key.toLowerCase() === key);
}

/**
 * The scan interval to send for a device: none for a pushing driver, which has no scan interval
 * and would be refused one (ADR-0016); a positive number of milliseconds for a polled one.
 */
export function scanIntervalToSend(
  drivers: DriverShape[],
  driverKey: string,
  raw: NumberField,
): { ok: true; value: number | null } | { ok: false; error: string } {
  if (pushes(drivers, driverKey)) {
    return { ok: true, value: null };
  }

  const scan = parseNumberField(raw);
  if (!scan.ok || scan.value === null || scan.value <= 0) {
    return { ok: false, error: 'Scan interval must be a positive number of milliseconds.' };
  }

  return { ok: true, value: scan.value };
}

/**
 * What to say about a tag that has never received anything (ADR-0016): "no data since" the moment
 * the Gateway began listening — an observed time, not a measured one — so a device that was never
 * set up reads differently from one that fell silent. Null for any other tag.
 */
export function noDataNote(
  snapshot: { noDataSinceUtc?: string | null },
  locale?: string,
): string | null {
  if (!snapshot.noDataSinceUtc) {
    return null;
  }

  const since = new Date(snapshot.noDataSinceUtc).toLocaleString(locale, {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });

  return `No data since ${since}`;
}
