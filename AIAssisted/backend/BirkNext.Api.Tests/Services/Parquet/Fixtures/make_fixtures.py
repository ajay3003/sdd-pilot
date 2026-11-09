"""Independent Parquet fixture writer + oracle (scratch acceptance only; standard library only, no BirkNext code).

Writes synthetic Parquet files from the format specification (Thrift compact footer, PLAIN + RLE_DICTIONARY encodings, data page v1 and v2,
RLE/bit-packed definition levels, SNAPPY (own encoder with copies) and GZIP codecs, statistics, several row groups) and an oracle JSON with the
expected schema, row counts, null counts, values and diffs computed from the generator's own data — never from BirkNext's reader.
Usage: python -I make_fixtures.py <out-dir>
"""
import json, os, random, struct, sys, zlib, hashlib, datetime as dt

# ── Thrift compact protocol (writer) ──────────────────────────────────────────────────────────────────────────────────
def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F; n >>= 7
        if n: out.append(b | 0x80)
        else: out.append(b); return bytes(out)
def zz(n): return (n << 1) ^ (n >> 63)
T_TRUE, T_FALSE, T_BYTE, T_I16, T_I32, T_I64, T_DOUBLE, T_BINARY, T_LIST, T_STRUCT = 1, 2, 3, 4, 5, 6, 7, 8, 9, 12

class Struct:
    """fields: list of (id, type, value). value for T_STRUCT is a Struct; for T_LIST a (elemtype, [values])."""
    def __init__(self, *fields): self.fields = [f for f in fields if f is not None and f[2] is not None]
    def encode(self):
        out = bytearray(); last = 0
        for fid, typ, val in sorted(self.fields, key=lambda f: f[0]):
            wire = (T_TRUE if val else T_FALSE) if typ == 'bool' else typ
            delta = fid - last
            out += bytes([(delta << 4) | wire]) if 0 < delta <= 15 else bytes([wire]) + varint(zz(fid))
            last = fid
            if typ != 'bool': out += value(typ, val)
        out.append(0); return bytes(out)
def value(typ, val):
    if typ in (T_I32, T_I64, T_I16): return varint(zz(val))
    if typ == T_BYTE: return bytes([val & 0xFF])
    if typ == T_DOUBLE: return struct.pack('<d', val)
    if typ == T_BINARY:
        b = val.encode() if isinstance(val, str) else val
        return varint(len(b)) + b
    if typ == T_STRUCT: return val.encode()
    if typ == T_LIST:
        et, items = val
        head = bytes([(len(items) << 4) | et]) if len(items) < 15 else bytes([0xF0 | et]) + varint(len(items))
        return head + b''.join(value(et, i) for i in items)
    raise ValueError(typ)

# ── Encodings ─────────────────────────────────────────────────────────────────────────────────────────────────────────
BOOLEAN, INT32, INT64, INT96, FLOAT, DOUBLE, BYTE_ARRAY, FLBA = range(8)
def plain(ptype, values):
    if ptype == INT32: return b''.join(struct.pack('<i', v) for v in values)
    if ptype == INT64: return b''.join(struct.pack('<q', v) for v in values)
    if ptype == FLOAT: return b''.join(struct.pack('<f', v) for v in values)
    if ptype == DOUBLE: return b''.join(struct.pack('<d', v) for v in values)
    if ptype == BYTE_ARRAY: return b''.join(struct.pack('<I', len(v)) + v for v in (x.encode() for x in values))
    if ptype == BOOLEAN:
        out = bytearray((len(values) + 7) // 8)
        for i, v in enumerate(values):
            if v: out[i // 8] |= 1 << (i % 8)
        return bytes(out)
    raise ValueError(ptype)

def hybrid(levels, bit_width):
    """RLE/bit-packed hybrid: runs of >= 8 equal values as RLE, the rest bit-packed in groups of 8 (both run kinds appear)."""
    out = bytearray(); i = 0; n = len(levels); byte_w = (bit_width + 7) // 8
    while i < n:
        j = i
        while j < n and levels[j] == levels[i]: j += 1
        if j - i >= 8:
            out += varint((j - i) << 1) + levels[i].to_bytes(byte_w, 'little'); i = j
        else:
            group = levels[i:i + 8]; k = i + 8
            while k < n:  # extend the bit-packed run until an RLE-worthy run starts
                r = k
                while r < n and levels[r] == levels[k]: r += 1
                if r - k >= 8: break
                group += levels[k:k + 8]; k += 8
            group = group + [0] * ((8 - len(group) % 8) % 8)
            out += varint(((len(group) // 8) << 1) | 1)
            bits = 0; nbits = 0
            for v in group:
                bits |= v << nbits; nbits += bit_width
                while nbits >= 8: out.append(bits & 0xFF); bits >>= 8; nbits -= 8
            if nbits: out.append(bits & 0xFF)
            i = min(k, n)
    return bytes(out)

def snappy(data):
    """Snappy block format with literals and 2-byte-offset copies (greedy 4-byte hash matcher)."""
    out = bytearray(varint(len(data))); i = 0; lit = 0; table = {}
    def literal(a, b):
        nonlocal out
        while a < b:
            n = min(b - a, 60 * 1000)
            if n <= 60: out.append((n - 1) << 2)
            elif n < 256: out += bytes([60 << 2, n - 1])
            else: out += bytes([61 << 2]) + (n - 1).to_bytes(2, 'little')
            out += data[a:a + n]; a += n
    while i + 4 <= len(data):
        key = data[i:i + 4]; cand = table.get(key); table[key] = i
        if cand is not None and i - cand < 65536:
            literal(lit, i); m = 4
            while i + m < len(data) and data[cand + m] == data[i + m] and m < 64: m += 1
            out.append(((m - 1) << 2) | 2); out += (i - cand).to_bytes(2, 'little')
            i += m; lit = i
        else: i += 1
    literal(lit, len(data)); return bytes(out)

CODECS = {'UNCOMPRESSED': 0, 'SNAPPY': 1, 'GZIP': 2, 'ZSTD': 6}
def compress(codec, data):
    if codec == 'SNAPPY': return snappy(data)
    if codec == 'GZIP':
        c = zlib.compressobj(6, zlib.DEFLATED, 31); return c.compress(data) + c.flush()
    return data  # UNCOMPRESSED; ZSTD fixtures are declared only (the reader must refuse before decoding)

# ── Writer ────────────────────────────────────────────────────────────────────────────────────────────────────────────
class Col:
    def __init__(self, name, ptype, optional=True, converted=None, logical=None, scale=None, precision=None, dictionary=False, stats=True):
        self.name, self.ptype, self.optional, self.converted, self.logical = name, ptype, optional, converted, logical
        self.scale, self.precision, self.dictionary, self.stats = scale, precision, dictionary, stats

def stat_bytes(ptype, v):
    if ptype == INT32: return struct.pack('<i', v)
    if ptype == INT64: return struct.pack('<q', v)
    if ptype == DOUBLE: return struct.pack('<d', v)
    if ptype == FLOAT: return struct.pack('<f', v)
    if ptype == BYTE_ARRAY: return v.encode()
    if ptype == BOOLEAN: return bytes([1 if v else 0])

def write(path, cols, rows, row_group_sizes, codec='SNAPPY', page_v2=False, nested_group=None, kv=None, corrupt=None):
    buf = bytearray(b'PAR1'); groups = []; start = 0
    for size in row_group_sizes:
        chunk_rows = rows[start:start + size]; start += size; chunks = []; rg_bytes = 0
        for ci, col in enumerate(cols):
            vals = [r[col.name] for r in chunk_rows]
            present = [v for v in vals if v is not None]
            defs = [0 if v is None else 1 for v in vals]
            col_start = len(buf); dict_off = None; encodings = [0, 3]
            if col.dictionary:
                uniq = sorted(set(present)); index = {v: k for k, v in enumerate(uniq)}
                dpage = plain(col.ptype, uniq); dcomp = compress(codec, dpage)
                dh = Struct((1, T_I32, 2), (2, T_I32, len(dpage)), (3, T_I32, len(dcomp)), (7, T_STRUCT, Struct((1, T_I32, len(uniq)), (2, T_I32, 0))))
                dict_off = len(buf); buf += dh.encode() + dcomp
                bw = max(1, (len(uniq) - 1).bit_length())
                values_bytes = bytes([bw]) + hybrid([index[v] for v in present], bw); encodings = [0, 3, 8]
            else:
                values_bytes = plain(col.ptype, present)
            data_off = len(buf)
            if page_v2:
                levels = hybrid(defs, 1) if col.optional else b''
                comp = compress(codec, values_bytes)
                h = Struct((1, T_I32, 3), (2, T_I32, len(levels) + len(values_bytes)), (3, T_I32, len(levels) + len(comp)),
                           (8, T_STRUCT, Struct((1, T_I32, len(vals)), (2, T_I32, defs.count(0)), (3, T_I32, len(vals)), (4, T_I32, 8 if col.dictionary else 0),
                                                (5, T_I32, len(levels)), (6, T_I32, 0), (7, 'bool', True))))
                buf += h.encode() + levels + comp
                unc = len(levels) + len(values_bytes)
            else:
                body = ((struct.pack('<I', len(hybrid(defs, 1))) + hybrid(defs, 1)) if col.optional else b'') + values_bytes
                comp = compress(codec, body)
                h = Struct((1, T_I32, 0), (2, T_I32, len(body)), (3, T_I32, len(comp)),
                           (5, T_STRUCT, Struct((1, T_I32, len(vals)), (2, T_I32, 8 if col.dictionary else 0), (3, T_I32, 3), (4, T_I32, 3))))
                buf += h.encode() + comp; unc = len(body)
            size_written = len(buf) - col_start; rg_bytes += size_written
            st = None
            if col.stats and present and col.ptype != BOOLEAN:
                st = Struct((3, T_I64, defs.count(0)), (5, T_BINARY, stat_bytes(col.ptype, max(present))), (6, T_BINARY, stat_bytes(col.ptype, min(present))))
            elif col.stats:
                st = Struct((3, T_I64, defs.count(0)))
            meta = Struct((1, T_I32, col.ptype), (2, T_LIST, (T_I32, encodings)), (3, T_LIST, (T_BINARY, [col.name])), (4, T_I32, CODECS[codec]),
                          (5, T_I64, len(vals)), (6, T_I64, unc), (7, T_I64, size_written), (9, T_I64, data_off),
                          (11, T_I64, dict_off) if dict_off is not None else None, (12, T_STRUCT, st) if st is not None else None)
            chunks.append(Struct((2, T_I64, col_start), (3, T_STRUCT, meta)))
        groups.append(Struct((1, T_LIST, (T_STRUCT, chunks)), (2, T_I64, rg_bytes), (3, T_I64, len(chunk_rows))))
    schema = [Struct((4, T_BINARY, 'schema'), (5, T_I32, len(cols) + (1 if nested_group else 0)))]
    for col in cols:
        schema.append(Struct((1, T_I32, col.ptype), (3, T_I32, 1 if col.optional else 0), (4, T_BINARY, col.name), (6, T_I32, col.converted) if col.converted is not None else None,
                             (7, T_I32, col.scale) if col.scale is not None else None, (8, T_I32, col.precision) if col.precision is not None else None,
                             (10, T_STRUCT, col.logical) if col.logical is not None else None))
    if nested_group:  # schema-only nested group (address{street, city}); no column chunks: the reader must show it, not read it
        name, children = nested_group
        schema.append(Struct((3, T_I32, 1), (4, T_BINARY, name), (5, T_I32, len(children))))
        for child in children: schema.append(Struct((1, T_I32, BYTE_ARRAY), (3, T_I32, 1), (4, T_BINARY, child), (6, T_I32, 0)))
    meta = Struct((1, T_I32, 2), (2, T_LIST, (T_STRUCT, schema)), (3, T_I64, len(rows)), (4, T_LIST, (T_STRUCT, groups)),
                  (5, T_LIST, (T_STRUCT, [Struct((1, T_BINARY, k), (2, T_BINARY, v)) for k, v in (kv or {}).items()])) if kv else None,
                  (6, T_BINARY, 'birknext-fixture-writer 1 (independent oracle)'))
    footer = meta.encode()
    buf += footer + struct.pack('<I', len(footer)) + b'PAR1'
    if corrupt == 'magic': buf[0:4] = b'PARX'
    if corrupt == 'truncated': buf = buf[: len(buf) // 2]
    if corrupt == 'footer-length': buf[-8:-4] = struct.pack('<I', len(buf) * 4)
    open(path, 'wb').write(buf)
    return hashlib.sha256(buf).hexdigest()

STRING = Struct((1, T_STRUCT, Struct()))
DATE = Struct((6, T_STRUCT, Struct()))
TS_MICROS_UTC = Struct((8, T_STRUCT, Struct((1, 'bool', True), (2, T_STRUCT, Struct((2, T_STRUCT, Struct()))))))
DEC_10_2 = Struct((5, T_STRUCT, Struct((1, T_I32, 2), (2, T_I32, 10))))

def children(seed, n):
    rnd = random.Random(seed); epoch = dt.date(1970, 1, 1)
    rows = []
    for i in range(1, n + 1):
        rows.append({
            'id': i,
            'name': None if i % 17 == 0 else f'Synthetic child {i}',
            'age': None if i % 23 == 0 else rnd.randint(0, 18),
            'status': None if i % 29 == 0 else rnd.choice(['ACTIVE', 'INACTIVE', 'MOVED']),
            'birth_date': (dt.date(2008, 1, 1) + dt.timedelta(days=rnd.randint(0, 6000)) - epoch).days,
            'registered_at': 1_700_000_000_000_000 + i * 60_000_000,
            'amount': rnd.randint(0, 1_000_000),  # unscaled, scale 2
            'score': None if i % 31 == 0 else round(rnd.uniform(0, 100), 3),
            'flag': i % 3 == 0,
        })
    return rows

def main(out):
    os.makedirs(out, exist_ok=True)
    cols = [Col('id', INT64, optional=False), Col('name', BYTE_ARRAY, converted=0, logical=STRING), Col('age', INT32),
            Col('status', BYTE_ARRAY, converted=0, logical=STRING, dictionary=True), Col('birth_date', INT32, converted=6, logical=DATE),
            Col('registered_at', INT64, logical=TS_MICROS_UTC), Col('amount', INT64, converted=5, logical=DEC_10_2, scale=2, precision=10),
            Col('score', DOUBLE), Col('flag', BOOLEAN, optional=False)]
    v1 = children(42, 1000)
    hashes = {'children_v1.parquet': write(os.path.join(out, 'children_v1.parquet'), cols, v1, [400, 400, 200], 'SNAPPY', kv={'producer': 'synthetic', 'dataset': 'children'})}

    # v2: schema change (flag removed, school added) and record changes keyed by id: 10 removed, 15 added, 20 changed.
    v2 = [dict(r) for r in v1 if not (500 <= r['id'] < 510)]
    changed = []
    for r in v2:
        if r['id'] % 50 == 1 and len(changed) < 20:
            r['age'] = (r['age'] or 0) + 1 if r['age'] is not None else 5; r['status'] = 'MOVED' if r['status'] != 'MOVED' else 'ACTIVE'; changed.append(r['id'])
    for i in range(1001, 1016):
        v2.append({**children(i, 1)[0], 'id': i, 'name': f'Synthetic child {i}'})
    for r in v2: r.pop('flag'); r['school'] = None if r['id'] % 7 == 0 else f'School {r["id"] % 12}'
    cols2 = [c for c in cols if c.name != 'flag'] + [Col('school', BYTE_ARRAY, converted=0, logical=STRING)]
    hashes['children_v2.parquet'] = write(os.path.join(out, 'children_v2.parquet'), cols2, v2, [600, 405], 'GZIP', page_v2=True)

    small = v1[:30]
    hashes['nested_schema.parquet'] = write(os.path.join(out, 'nested_schema.parquet'), cols[:2], small, [30], 'UNCOMPRESSED', nested_group=('address', ['street', 'city']))
    hashes['zstd_declared.parquet'] = write(os.path.join(out, 'zstd_declared.parquet'), cols[:2], small, [30], 'ZSTD')
    hashes['bad_magic.parquet'] = write(os.path.join(out, 'bad_magic.parquet'), cols[:2], small, [30], 'UNCOMPRESSED', corrupt='magic')
    hashes['truncated.parquet'] = write(os.path.join(out, 'truncated.parquet'), cols[:2], small, [30], 'UNCOMPRESSED', corrupt='truncated')
    hashes['bad_footer_length.parquet'] = write(os.path.join(out, 'bad_footer_length.parquet'), cols[:2], small, [30], 'UNCOMPRESSED', corrupt='footer-length')
    open(os.path.join(out, 'empty.parquet'), 'wb').close(); hashes['empty.parquet'] = hashlib.sha256(b'').hexdigest()

    def nulls(rows, name): return sum(1 for r in rows if r[name] is None)
    ids1 = {r['id']: r for r in v1}; ids2 = {r['id']: r for r in v2}
    common = sorted(set(ids1) & set(ids2))
    shared_cols = [c.name for c in cols2 if c.name in ids1[1]]
    changed_ids = [i for i in common if any(ids1[i][c] != ids2[i][c] for c in shared_cols)]
    oracle = {
        'writer': 'birknext-fixture-writer 1 (independent oracle; standard-library Python)',
        'hashes': hashes,
        'children_v1': {
            'rows': len(v1), 'rowGroups': [400, 400, 200], 'codec': 'SNAPPY', 'columns': [c.name for c in cols],
            'nullCounts': {c.name: nulls(v1, c.name) for c in cols},
            'statusValues': sorted({r['status'] for r in v1 if r['status']}),
            'statusCounts': {s: sum(1 for r in v1 if r['status'] == s) for s in ['ACTIVE', 'INACTIVE', 'MOVED']},
            'ageMin': min(r['age'] for r in v1 if r['age'] is not None), 'ageMax': max(r['age'] for r in v1 if r['age'] is not None),
            'ageOver15': sum(1 for r in v1 if r['age'] is not None and r['age'] > 15),
            'row1': v1[0], 'row401': v1[400], 'row1000': v1[999],
            'amountRow1': f"{v1[0]['amount'] // 100}.{v1[0]['amount'] % 100:02d}",
            'birthDateRow1': (dt.date(1970, 1, 1) + dt.timedelta(days=v1[0]['birth_date'])).isoformat(),
            'registeredAtRow1': dt.datetime.fromtimestamp(v1[0]['registered_at'] / 1_000_000, dt.timezone.utc).isoformat().replace('+00:00', 'Z'),
            'distinctIds': len({r['id'] for r in v1}),
        },
        'children_v2': {'rows': len(v2), 'rowGroups': [600, 405], 'codec': 'GZIP', 'pageVersion': 2, 'columns': [c.name for c in cols2]},
        'diff_v1_v2_by_id': {'schemaAdded': ['school'], 'schemaRemoved': ['flag'], 'added': len(set(ids2) - set(ids1)), 'removed': len(set(ids1) - set(ids2)),
                             'changed': len(changed_ids), 'unchanged': len(common) - len(changed_ids), 'changedIdsFirst5': changed_ids[:5]},
        'nested_schema': {'groups': ['address'], 'readableColumns': ['id', 'name']},
        'refusals': {'zstd_declared.parquet': 'ZSTD', 'bad_magic.parquet': 'magic', 'truncated.parquet': 'footer', 'bad_footer_length.parquet': 'footer', 'empty.parquet': 'empty'},
    }
    json.dump(oracle, open(os.path.join(out, 'oracle.json'), 'w'), indent=2, default=str)
    print(json.dumps({k: v for k, v in oracle.items() if k in ('children_v1', 'diff_v1_v2_by_id')}, default=str)[:1500])

if __name__ == '__main__':
    main(sys.argv[1])
