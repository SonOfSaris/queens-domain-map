// Reader for Unreal Engine 4 save games ("GVAS" files, tagged property serialization).
// parseGvas(ArrayBuffer | Uint8Array) -> { className, engine, props, warnings }
// Values: structs -> plain objects, arrays/sets -> arrays, maps -> arrays of [key, value], vectors -> {x, y, z}.

const NATIVE = {
  Vector: r => ({ x: r.f32(), y: r.f32(), z: r.f32() }),
  Rotator: r => ({ pitch: r.f32(), yaw: r.f32(), roll: r.f32() }),
  Quat: r => ({ x: r.f32(), y: r.f32(), z: r.f32(), w: r.f32() }),
  Vector2D: r => ({ x: r.f32(), y: r.f32() }),
  IntPoint: r => ({ x: r.i32(), y: r.i32() }),
  IntVector: r => ({ x: r.i32(), y: r.i32(), z: r.i32() }),
  LinearColor: r => ({ r: r.f32(), g: r.f32(), b: r.f32(), a: r.f32() }),
  Color: r => ({ b: r.u8(), g: r.u8(), r: r.u8(), a: r.u8() }),
  Guid: r => r.guid(),
  DateTime: r => r.i64(),
  Timespan: r => r.i64(),
  SoftObjectPath: r => r.str() + (r.str() && ''),
  SoftClassPath: r => r.str() + (r.str() && ''),
  GameplayTag: r => r.str(),
  GameplayTagContainer: r => { const n = r.i32(), out = []; for (let i = 0; i < n; i++) out.push(r.str()); return out; },
  Box: r => ({ min: NATIVE.Vector(r), max: NATIVE.Vector(r), valid: r.u8() }),
};

class Reader {
  constructor(bytes) {
    this.b = bytes;
    this.dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    this.p = 0;
    this.warnings = [];
  }
  need(n) { if (this.p + n > this.b.length) throw new Error(`unexpected end of file at ${this.p}`); }
  u8() { this.need(1); return this.b[this.p++]; }
  i8() { this.need(1); return this.dv.getInt8(this.p++); }
  i16() { this.need(2); const v = this.dv.getInt16(this.p, true); this.p += 2; return v; }
  u16() { this.need(2); const v = this.dv.getUint16(this.p, true); this.p += 2; return v; }
  i32() { this.need(4); const v = this.dv.getInt32(this.p, true); this.p += 4; return v; }
  u32() { this.need(4); const v = this.dv.getUint32(this.p, true); this.p += 4; return v; }
  i64() { this.need(8); const v = this.dv.getBigInt64(this.p, true); this.p += 8; return Number(v); }
  u64() { this.need(8); const v = this.dv.getBigUint64(this.p, true); this.p += 8; return Number(v); }
  f32() { this.need(4); const v = this.dv.getFloat32(this.p, true); this.p += 4; return v; }
  f64() { this.need(8); const v = this.dv.getFloat64(this.p, true); this.p += 8; return v; }
  guid() {
    this.need(16);
    let s = '';
    for (let i = 0; i < 4; i++) s += this.dv.getUint32(this.p + i * 4, true).toString(16).padStart(8, '0');
    this.p += 16;
    return s.toUpperCase();
  }
  str() {
    const n = this.i32();
    if (n === 0) return '';
    if (n > 0) {
      if (n > 1 << 20) throw new Error(`bad string length ${n} at ${this.p - 4}`);
      this.need(n);
      let s = '';
      for (let i = 0; i < n - 1; i++) s += String.fromCharCode(this.b[this.p + i]);
      this.p += n;
      return s;
    }
    const c = -n;
    if (c > 1 << 20) throw new Error(`bad string length ${n} at ${this.p - 4}`);
    this.need(c * 2);
    let s = '';
    for (let i = 0; i < c - 1; i++) s += String.fromCharCode(this.dv.getUint16(this.p + i * 2, true));
    this.p += c * 2;
    return s;
  }
  /** True when a plausible FString (printable, null-terminated, short) starts at offset p. */
  looksLikeStr(p = this.p, max = 256) {
    if (p + 4 > this.b.length) return false;
    const n = this.dv.getInt32(p, true);
    if (n < 2 || n > max || p + 4 + n > this.b.length || this.b[p + 4 + n - 1] !== 0) return false;
    for (let i = 0; i < n - 1; i++) { const ch = this.b[p + 4 + i]; if (ch < 32 || ch > 126) return false; }
    return true;
  }
  /** True when a tagged property (name string followed by a "...Property" type string) or a "None" terminator starts here. */
  looksLikeProps() {
    if (!this.looksLikeStr()) return false;
    const n = this.dv.getInt32(this.p, true);
    const name = String.fromCharCode(...this.b.subarray(this.p + 4, this.p + 4 + n - 1));
    if (name === 'None') return true;
    const q = this.p + 4 + n;
    if (!this.looksLikeStr(q, 64)) return false;
    const tn = this.dv.getInt32(q, true);
    return String.fromCharCode(...this.b.subarray(q + 4, q + 4 + tn - 1)).endsWith('Property');
  }
}

function readText(r) {
  r.u32(); // flags
  const hist = r.i8();
  if (hist === -1) return r.i32() ? r.str() : '';
  if (hist === 0) { r.str(); r.str(); return r.str(); }
  if (hist === 11) { const table = r.str(); return `${table}:${r.str()}`; }
  throw new Error(`text history type ${hist} is not supported`);
}

function readProps(r) {
  const out = {};
  for (;;) {
    const name = r.str();
    if (name === 'None' || name === '') break;
    const type = r.str();
    const size = r.i32();
    const index = r.i32();
    const key = index ? `${name}[${index}]` : name;
    out[key] = readTagged(r, type, size, key);
  }
  return out;
}

function readTagged(r, type, size, name) {
  const tag = {};
  switch (type) {
    case 'BoolProperty': tag.bool = r.u8(); break;
    case 'StructProperty': tag.struct = r.str(); r.p += 16; break;
    case 'ArrayProperty': case 'SetProperty': tag.inner = r.str(); break;
    case 'MapProperty': tag.key = r.str(); tag.value = r.str(); break;
    case 'EnumProperty': case 'ByteProperty': tag.enum = r.str(); break;
  }
  if (r.u8()) r.p += 16; // property guid
  const start = r.p, end = start + size;
  let v;
  try {
    v = readValue(r, type, tag, size);
  } catch (e) {
    r.warnings.push(`${name} (${type}): ${e.message}`);
    v = undefined;
  }
  if (r.p !== end) {
    if (v !== undefined) r.warnings.push(`${name} (${type}${tag.struct ? ' ' + tag.struct : ''}): read ${r.p - start} of ${size} bytes`);
    r.p = end;
  }
  return v;
}

function readValue(r, type, tag, size) {
  switch (type) {
    case 'BoolProperty': return !!tag.bool;
    case 'StructProperty': return readStruct(r, tag.struct, size);
    case 'ArrayProperty': return readArray(r, tag.inner, size);
    case 'SetProperty': { r.i32(); const n = r.i32(), out = []; for (let i = 0; i < n; i++) out.push(readBare(r, tag.inner)); return out; }
    case 'MapProperty': {
      r.i32();
      const n = r.i32(), out = [];
      for (let i = 0; i < n; i++) { const k = readBare(r, tag.key); out.push([k, readBare(r, tag.value)]); }
      return out;
    }
    case 'ByteProperty': return tag.enum === 'None' ? r.u8() : r.str();
    default: return readBare(r, type);
  }
}

/** A value without a property tag (array, set and map elements). */
function readBare(r, type) {
  switch (type) {
    case 'IntProperty': return r.i32();
    case 'UInt32Property': return r.u32();
    case 'Int64Property': return r.i64();
    case 'UInt64Property': return r.u64();
    case 'Int16Property': return r.i16();
    case 'UInt16Property': return r.u16();
    case 'Int8Property': return r.i8();
    case 'FloatProperty': return r.f32();
    case 'DoubleProperty': return r.f64();
    case 'BoolProperty': return !!r.u8();
    case 'ByteProperty': return r.looksLikeStr() ? r.str() : r.u8();
    case 'StrProperty': case 'NameProperty': case 'EnumProperty': case 'ObjectProperty': case 'InterfaceProperty': return r.str();
    case 'SoftObjectProperty': case 'SoftClassProperty': { const a = r.str(); r.str(); return a; }
    case 'TextProperty': return readText(r);
    case 'StructProperty': return r.looksLikeProps() ? readProps(r) : r.guid(); // untyped struct in a map/set: property list, else a Guid
    default: throw new Error(`unsupported property type ${type}`);
  }
}

function readStruct(r, name, size) {
  const native = NATIVE[name];
  if (native) return native(r);
  if (size > 0 && !r.looksLikeProps()) throw new Error(`struct ${name} is not a property list`);
  return readProps(r);
}

function readArray(r, inner, size) {
  const n = r.i32(), out = [];
  if (inner === 'StructProperty') {
    r.str(); r.str(); // element tag: name, type
    const bytes = r.i32(); r.i32();
    const struct = r.str();
    r.p += 16;
    if (r.u8()) r.p += 16;
    const each = n ? bytes / n : 0;
    for (let i = 0; i < n; i++) out.push(readStruct(r, struct, each));
    return out;
  }
  if (inner === 'ByteProperty') {
    if (size - 4 === n) { const a = Array.from(r.b.subarray(r.p, r.p + n)); r.p += n; return a; }
    for (let i = 0; i < n; i++) out.push(r.str());
    return out;
  }
  for (let i = 0; i < n; i++) out.push(readBare(r, inner));
  return out;
}

export function parseGvas(data) {
  const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
  const r = new Reader(bytes);
  if (bytes.length < 32 || String.fromCharCode(bytes[0], bytes[1], bytes[2], bytes[3]) !== 'GVAS') throw new Error('not an Unreal save game (no GVAS header)');
  r.p = 4;
  const saveVersion = r.i32(), packageVersion = r.i32();
  const engine = `${r.u16()}.${r.u16()}.${r.u16()}`;
  r.u32();
  r.str(); // branch
  r.i32(); // custom version format
  const customVersions = r.i32();
  r.p += customVersions * 20;
  const className = r.str();
  const props = readProps(r);
  return { className, engine, saveVersion, packageVersion, props, warnings: r.warnings };
}
