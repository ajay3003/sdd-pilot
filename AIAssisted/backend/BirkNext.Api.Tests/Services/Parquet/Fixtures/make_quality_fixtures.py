"""Independent Parquet Quality Review fixtures + oracle (standard library only; no BirkNext code).

Uses the independent writer in make_fixtures.py (Thrift compact footer, PLAIN/RLE, Snappy) to write synthetic coverage datasets for the synthetic
contract quality/coverage-contract.md, and computes every expected outcome from the generator's own rows: schema compatibility, null / enum /
format violations, sum-identity and roll-up groups, composite-key duplicates, identifiers, canaries and baseline differences.
Fixed seeds; no real personal data (the 11-digit value and the canary are synthetic markers).
Usage: python -I make_quality_fixtures.py <out-dir>
"""
import importlib.util, json, os, random, sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('mf', os.path.join(HERE, 'make_fixtures.py'))
mf = importlib.util.module_from_spec(spec); spec.loader.exec_module(mf)
Col, write, BYTE_ARRAY, INT32, INT64, STRING = mf.Col, mf.write, mf.BYTE_ARRAY, mf.INT32, mf.INT64, mf.STRING

CATS = ['FOSTERHJEM', 'PRIVAT_INSTITUSJON']
TOTAL = 'TOTAL'
MEASURES = ['EXPECTED', 'MISSING', 'RECEIVED', 'REJECTED_ONLY']
TOTAL_ONLY = ['RECEIVED_NOT_EXPECTED', 'REJECTED_UNATTRIBUTED']
ALLOWED_MEASURES = MEASURES + TOTAL_ONLY
CANARY = 'CANARY-PROTECTED-7Q4'
SYNTHETIC_ID = '01017099999'  # 11 digits, synthetic marker (not a valid national id)


def coverage(seed, period='2026-10'):
    rnd = random.Random(seed); rows = []; totals = {m: 0 for m in MEASURES}
    for cat in CATS:
        rec, rej, mis = rnd.randint(0, 9), rnd.randint(0, 3), rnd.randint(0, 4)
        vals = {'EXPECTED': rec + rej + mis, 'MISSING': mis, 'RECEIVED': rec, 'REJECTED_ONLY': rej}
        for m in MEASURES:
            rows.append({'period': period, 'category': cat, 'measure': m, 'reason_code': None, 'count': vals[m]}); totals[m] += vals[m]
    for m in MEASURES: rows.append({'period': period, 'category': TOTAL, 'measure': m, 'reason_code': None, 'count': totals[m]})
    rows.append({'period': period, 'category': TOTAL, 'measure': 'RECEIVED_NOT_EXPECTED', 'reason_code': None, 'count': rnd.randint(0, 2)})
    for code in ['MISSING_REFERENCE', 'INVALID_MESSAGE']:
        rows.append({'period': period, 'category': TOTAL, 'measure': 'REJECTED_UNATTRIBUTED', 'reason_code': code, 'count': rnd.randint(1, 3)})
    return sort(rows)


def sort(rows):
    return sorted(rows, key=lambda r: (r['category'], r['measure'], '' if r['reason_code'] is None else '\u0001' + r['reason_code']))


def cols(count_type=INT32, extra=None, drop=None, category_optional=False, measure_type=BYTE_ARRAY):
    c = [Col('period', BYTE_ARRAY, optional=False, converted=0, logical=STRING),
         Col('category', BYTE_ARRAY, optional=category_optional, converted=0, logical=STRING),
         Col('measure', measure_type, optional=False, converted=0 if measure_type == BYTE_ARRAY else None, logical=STRING if measure_type == BYTE_ARRAY else None),
         Col('reason_code', BYTE_ARRAY, optional=True, converted=0, logical=STRING),
         Col('count', count_type, optional=False, converted=0 if count_type == BYTE_ARRAY else None, logical=STRING if count_type == BYTE_ARRAY else None)]
    if drop: c = [x for x in c if x.name != drop]
    if extra: c.append(extra)
    return c


# ── Oracle (computed from the rows, never from BirkNext) ────────────────────────────────────────────────────────────
def oracle(rows, columns):
    names = [c.name for c in columns]
    o = {'rows': len(rows), 'columns': names}
    o['nullCategory'] = sum(1 for r in rows if r.get('category') is None) if 'category' in names else None
    o['invalidMeasure'] = sum(1 for r in rows if r.get('measure') is not None and r['measure'] not in ALLOWED_MEASURES) if 'measure' in names else None
    o['invalidCategory'] = sum(1 for r in rows if r.get('category') is not None and r['category'] not in CATS + [TOTAL]) if 'category' in names else None
    o['badPeriod'] = sum(1 for r in rows if not (len(r['period']) == 7 and r['period'][4] == '-' and r['period'][:4].isdigit() and r['period'][5:].isdigit() and 1 <= int(r['period'][5:]) <= 12)) if 'period' in names else None
    o['reasonOutsideRejected'] = sum(1 for r in rows if r.get('reason_code') is not None and r.get('measure') != 'REJECTED_UNATTRIBUTED') if 'reason_code' in names else None
    o['totalOnlyOutsideTotal'] = sum(1 for r in rows if r.get('measure') in TOTAL_ONLY and r.get('category') != TOTAL) if 'measure' in names else None
    if all(n in names for n in ['category', 'measure', 'count']) and all(isinstance(r['count'], int) for r in rows):
        bad = 0; groups = 0
        for cat in sorted({r['category'] for r in rows if r['category'] is not None}):
            vals = {}
            for r in rows:
                if r['category'] == cat: vals[r['measure']] = vals.get(r['measure'], 0) + r['count']
            if any(m in vals for m in ['RECEIVED', 'REJECTED_ONLY', 'MISSING', 'EXPECTED']):
                groups += 1
                if vals.get('RECEIVED', 0) + vals.get('REJECTED_ONLY', 0) + vals.get('MISSING', 0) != vals.get('EXPECTED', 0): bad += 1
        o['sumGroups'] = groups; o['sumBadGroups'] = bad
        roll = {}
        for r in rows:
            if r['measure'] in TOTAL_ONLY: continue
            k = (r['period'], r['measure'], r.get('reason_code'))
            part, tot = roll.get(k, (0, 0))
            roll[k] = (part + r['count'], tot) if r['category'] != TOTAL else (part, tot + r['count'])
        o['rollGroups'] = len(roll); o['rollBadGroups'] = sum(1 for p, t in roll.values() if p != t)
        o['sumByCategoryMeasure'] = {f"{r['category']}|{r['measure']}": 0 for r in rows}
        for r in rows: o['sumByCategoryMeasure'][f"{r['category']}|{r['measure']}"] += r['count']
    keys = [(r.get('category'), r.get('measure'), r.get('reason_code')) for r in rows]
    o['duplicateKeys'] = len(keys) - len(set(keys))
    o['sorted'] = rows == sort(rows) if 'category' in names and all(r.get('category') is not None for r in rows) else None
    o['identifierRows'] = sum(1 for r in rows if any(isinstance(v, str) and SYNTHETIC_ID in v for v in r.values()))
    o['canaryRows'] = sum(1 for r in rows if any(isinstance(v, str) and CANARY in v for v in r.values()))
    return o


def main(out):
    os.makedirs(out, exist_ok=True)
    result = {'contract': 'coverage-contract.md', 'canary': CANARY, 'fixtures': {}}

    def emit(name, columns, rows, codec='SNAPPY', groups=None, expect=None):
        sha = write(os.path.join(out, name), columns, rows, groups or [len(rows)], codec)
        entry = oracle(rows, columns); entry['sha256'] = sha; entry['codec'] = codec; entry['rowGroups'] = len(groups or [len(rows)])
        entry.update(expect or {})
        result['fixtures'][name] = entry

    valid = coverage(7)
    emit('q_valid.parquet', cols(), valid, expect={'schema': 'Compatible'})
    emit('q_missing_column.parquet', cols(drop='count'), [{k: v for k, v in r.items() if k != 'count'} for r in valid], expect={'schema': 'Incompatible', 'missing': ['count']})
    emit('q_wrong_type.parquet', cols(count_type=INT64), valid, expect={'schema': 'Incompatible', 'typeMismatch': ['count']})
    nulled = [dict(r) for r in valid]; nulled[0]['category'] = None
    emit('q_null_required.parquet', cols(category_optional=True), nulled, expect={'schema': 'CompatibleWithWarnings'})
    enum = [dict(r) for r in valid]; enum[1]['measure'] = 'UNKNOWN_MEASURE'
    emit('q_invalid_enum.parquet', cols(), enum, expect={'schema': 'Compatible'})
    dup = valid + [dict(valid[0])]
    emit('q_duplicate_key.parquet', cols(), sort(dup), expect={'schema': 'Compatible'})
    period = [dict(r) for r in valid]; period[2]['period'] = '2026-13'
    emit('q_wrong_period.parquet', cols(), period, expect={'schema': 'Compatible'})
    agg = [dict(r) for r in valid]
    for r in agg:
        if r['category'] == 'FOSTERHJEM' and r['measure'] == 'EXPECTED': r['count'] += 1
    emit('q_bad_aggregate.parquet', cols(), agg, expect={'schema': 'Compatible'})
    extra = [dict(r, note=None if i % 2 else 'synthetic') for i, r in enumerate(valid)]
    emit('q_added_optional.parquet', cols(extra=Col('note', BYTE_ARRAY, optional=True, converted=0, logical=STRING)), extra, expect={'schema': 'CompatibleWithWarnings', 'unexpected': ['note']})
    emit('q_breaking.parquet', cols(measure_type=INT32), [dict(r, measure=ALLOWED_MEASURES.index(r['measure'])) for r in valid], expect={'schema': 'Incompatible', 'typeMismatch': ['measure']})
    other = [{'id': i, 'name': f'Synthetic item {i}'} for i in range(1, 11)]
    emit('q_other_dataset.parquet', [Col('id', INT64, optional=False), Col('name', BYTE_ARRAY, converted=0, logical=STRING)], other, expect={'schema': 'Incompatible'})
    can = [dict(r) for r in valid]; can[-1]['reason_code'] = CANARY; can[-2]['reason_code'] = 'REF ' + SYNTHETIC_ID
    emit('q_canary.parquet', cols(), can, expect={'schema': 'Compatible'})
    changed = coverage(8)
    emit('q_changed_baseline.parquet', cols(), changed, expect={'schema': 'Compatible'})
    # The pre-translation layout (Norwegian names, Norwegian values, uncompressed) like the real sample: only periode→period is documented.
    legacy_names = {'period': 'periode', 'category': 'kategori', 'measure': 'maal', 'reason_code': 'aarsakskode', 'count': 'antall'}
    legacy_values = {'TOTAL': 'TOTALT', 'EXPECTED': 'FORVENTET', 'RECEIVED': 'MOTTATT', 'REJECTED_ONLY': 'KUN_AVVIST', 'MISSING': 'MANGLER',
                     'RECEIVED_NOT_EXPECTED': 'MOTTATT_IKKE_FORVENTET', 'REJECTED_UNATTRIBUTED': 'AVVIST_IKKE_TILORDNET'}
    legacy_rows = [{legacy_names[k]: (legacy_values.get(v, v) if isinstance(v, str) and k in ('category', 'measure') else v) for k, v in r.items()} for r in valid]
    legacy_cols = [Col(legacy_names[c.name], c.ptype, optional=c.optional, converted=c.converted, logical=c.logical) for c in cols()]
    emit('q_legacy.parquet', legacy_cols, legacy_rows, codec='UNCOMPRESSED', expect={'schema': 'Incompatible', 'documentedMappings': ['periode->period'],
                                                                              'mappedOracle': oracle(valid, cols())})
    json.dump(result, open(os.path.join(out, 'quality-oracle.json'), 'w', encoding='utf-8'), indent=1, sort_keys=True)


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'quality'))
