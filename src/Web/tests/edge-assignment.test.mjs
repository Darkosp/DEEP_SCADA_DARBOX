// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// ADR-0019 in the client. An edge reads the devices assigned to it, and assigning one is an
// ordinary edit of the device, so the device form reads the assignment out of the edges. The
// edge form must not drop a link it cannot offer in the Site being browsed, or saving anything
// else on that form would silently release it.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  edgeOfDevice,
  edgeOptions,
  edgeReads,
  linkOptions,
  treeDevices,
} from '../.node-test/models.js';

const drivers = [
  { key: 'modbus-tcp', pushing: false },
  { key: 'mqtt', pushing: true },
];

const device = (id, name, driverKey) => ({
  id,
  name,
  driverKey,
  connectionSettings: {},
  scanIntervalMs: null,
  folderId: null,
  tags: [],
});

const tree = {
  siteId: 's1',
  name: 'Plant',
  timeZoneId: 'UTC',
  devices: [device('d1', 'bridge', 'mqtt'), device('d2', 'meter', 'modbus-tcp')],
  folders: [
    {
      id: 'f1',
      name: 'Line 1',
      parentFolderId: null,
      folders: [],
      devices: [device('d3', 'tank', 'modbus-tcp')],
    },
  ],
};

const edge = { id: 'e1', name: 'edge-a', linkDeviceId: 'd1', deviceIds: ['d2', 'd3', 'gone'] };

test('the edges are a picker, with "not on an edge" first', () => {
  assert.deepEqual(edgeOptions([{ id: 'e1', name: 'edge-a', linkDeviceId: null, deviceIds: [] }]), [
    { id: null, label: 'Not on an edge' },
    { id: 'e1', label: 'edge-a' },
  ]);
});

test('a device is found on the edge that reads it, wherever it sits in the tree', () => {
  assert.equal(edgeOfDevice([edge], 'd3')?.id, 'e1');
  assert.equal(edgeOfDevice([edge], 'd1'), null);
});

test('a tree is walked to the bottom for its devices', () => {
  assert.deepEqual(
    treeDevices(tree).map((one) => one.name),
    ['bridge', 'meter', 'tank'],
  );
  assert.deepEqual(treeDevices(null), []);
});

test('what an edge reads is named where the tree knows it and counted where it does not', () => {
  const reads = edgeReads(tree, edge);

  assert.deepEqual(reads.names, ['meter', 'tank']);
  assert.equal(reads.elsewhere, 1);
});

test('a link may be a pushing device of this Site, or none', () => {
  assert.deepEqual(linkOptions(treeDevices(tree), drivers, null), [
    { id: null, label: 'No link' },
    { id: 'd1', label: 'bridge' },
  ]);
});

test('the link an edge already has survives a Site that cannot offer it', () => {
  const options = linkOptions(treeDevices(tree), drivers, 'd9');

  assert.equal(options.length, 3);
  assert.deepEqual(options[2], { id: 'd9', label: 'A device outside this Site' });
});
