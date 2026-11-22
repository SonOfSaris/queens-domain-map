// Reads a Queen's Domain save slot (World<N>.sav) and works out which map markers it has already dealt with.
// The exporter stores each actor's save ids (marker.g), data asset (marker.a) and item / enemy asset paths (items[].p, enemies[].p).
import { parseGvas } from './gvas.js';

const pkg = s => String(s || '').split('.')[0].toLowerCase(); // "/Game/A/B.B" -> "/game/a/b"
const field = (o, name) => { for (const k in o) if (k === name || k.startsWith(name + '_')) return o[k]; };
const toMap = (a, kf = k => k) => new Map((a || []).map(([k, v]) => [kf(k), v]));
const mapPos = t => [t.x / 100, t.z / 100, t.y / 100]; // Unreal cm (x, y, z-up) -> map metres (x, height, y)

/** The game stores local wall-clock time as .NET ticks. */
function ticksToDate(ticks) {
  if (!ticks) return null;
  const utc = new Date((ticks - 621355968000000000) / 10000);
  if (isNaN(utc)) return null;
  return new Date(utc.getUTCFullYear(), utc.getUTCMonth(), utc.getUTCDate(), utc.getUTCHours(), utc.getUTCMinutes(), utc.getUTCSeconds());
}

export function readSave(buf) {
  const g = parseGvas(buf);
  const p = g.props;
  if (!p.SavableActorStates && !p.InventoryContents) {
    throw new Error(/SystemSave|Challenge/i.test(g.className) ? 'that file holds settings or challenges, not a save slot — pick a World<N>.sav file' : 'this is not a Queen\'s Domain save slot');
  }
  const t = p.SaveLocation?.Translation, q = p.SaveLocation?.Rotation;
  const sv = {
    level: p.SavedPlayerLevel ?? 0,
    xp: p.XPAmount ?? 0,
    playSeconds: p.PlayTime_Seconds ?? 0,
    savedAt: ticksToDate(p.LastSaveTime),
    pos: t ? mapPos(t) : null,
    // facing as a view yaw of the map camera (Unreal yaw turns from +X towards +Y)
    yaw: q ? Math.PI / 2 - Math.atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.y * q.y + q.z * q.z)) : 0,
    savePoint: pkg(p.SavePointID),
    states: toMap(p.SavableActorStates),       // actor save id -> state (1 = taken / opened, kill count for spawners)
    actorItems: toMap(p.SavableActorItems),    // key slots: actor save id -> item placed in it
    named: toMap(p.NamedSaveData),             // quest flags and counters
    inventory: toMap(p.InventoryContents, pkg), // item -> count (0 = had it, since used, sold or evolved)
    visited: new Set((p.SavePointsVisited || []).map(pkg)),
    teleporters: new Set((p.UnlockedTeleporters || []).map(pkg)),
    journal: new Set((p.NPCsInJournal || []).map(pkg)),
    kills: toMap(p.KilledEnemyCounts, pkg),
    drops: [],                                 // loot lying on the ground
    warnings: g.warnings,
  };
  for (const [id, d] of p.ItemDrops || []) {
    const tr = field(d, 'Position')?.Translation, inv = field(d, 'Inventory');
    if (tr && inv?.length) sv.drops.push({ id, pos: mapPos(tr), items: inv.map(([k, n]) => [pkg(k), n]) });
  }
  return sv;
}

/**
 * Which markers the save has already dealt with.
 * Returns { found: Set<marker id>, note: Map<marker id, text>, owned: Map<item index, count>, kills: Map<enemy index, count> }.
 */
export function matchSave(sv, md) {
  const found = new Set(), note = new Map(), owned = new Map(), kills = new Map();
  md.items.forEach((it, i) => { const n = sv.inventory.get((it.p || '').toLowerCase()); if (n != null) owned.set(i, n); });
  md.enemies.forEach((e, i) => { const n = sv.kills.get((e.p || '').toLowerCase()); if (n) kills.set(i, n); });

  const placements = new Map(), spawners = new Map();
  for (const m of md.markers) {
    if (m.t === 'item') for (const [i] of m.it) placements.set(i, (placements.get(i) || 0) + 1);
    if (m.e != null) spawners.set(m.e, (spawners.get(m.e) || 0) + 1);
  }
  // an item with a single placement and no other source: owning it means that pickup is done (covers pickups an NPC hands over)
  const onlyHere = i => placements.get(i) === 1 && !md.items[i].src && owned.has(i);

  for (const m of md.markers) {
    const st = (m.g || []).map(g => sv.states.get(g) ?? -1);
    const max = st.length ? Math.max(...st) : -1;
    const a = (m.a || '').toLowerCase();
    let f = false;
    switch (m.t) {
      case 'item': f = max >= 1 || m.it.every(([i]) => onlyHere(i)); break;
      case 'chest': case 'secret': case 'keyslot': case 'switch': f = max >= 1; break;
      case 'door': // first id is the door itself, the others its locks
        f = st.length > 0 && st[0] >= 1;
        if (!f && max >= 1) note.set(m.id, `${st.filter(v => v >= 1).length} of ${Math.max(1, st.length - 1)} keys placed`);
        break;
      case 'boss': f = max >= 1 || (m.e != null && spawners.get(m.e) === 1 && kills.get(m.e) >= 1); break;
      case 'enemy':
        if (m.mk) f = max >= m.mk;
        if (max >= 1) note.set(m.id, f ? 'Defeated — does not come back' : `Defeated here ${max}×`);
        break;
      case 'npc': f = sv.journal.has(a) || max >= 1; break;
      case 'save': f = sv.visited.has(a); break;
      case 'teleport': f = sv.teleporters.has(a); break;
      case 'lore': f = !!m.a && sv.named.has('ReadNote_' + m.a); break;
    }
    if (f) found.add(m.id);
  }
  return { found, note, owned, kills };
}
