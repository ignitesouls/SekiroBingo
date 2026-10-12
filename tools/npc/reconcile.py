"""Validate or rebuild canonical NPC event gates from the staged mod baseline."""
import argparse
import itertools
import json
from pathlib import Path
import struct

from event_codec import check_flag, decompress, events, patch_flag, wrap
from npc_rules import simulate_book, validate_isshin, validate_monk, validate_shrine

DEFAULT_MOD = Path(__file__).resolve().parents[2] / 'SekiroBingo/Resources/me3/sekiro-bingo/mod'
# path, event, instruction, condition group, accepted historical inputs, final flag
GATES = [
    ('common_func.emevd.dcx', 20006121, 10, 11, {71110206, 9303}, 9303),
    ('common_func.emevd.dcx', 20006113, 14, 15, {71100505, 1281}, 1281),
    ('common_func.emevd.dcx', 20006113, 15, 15, {8400, 71110206}, 71110206),
    ('m11_01_00_00.emevd.dcx', 11115762, 1, 1, {71110059, 9303}, 9303),
    ('m20_00_00_00.emevd.dcx', 12005217, 2, 0, {8400, 9303}, 9303),
]


def validate(raw):
    common, castle, senpou = (raw[name] for name in (
        'common_func.emevd.dcx', 'm11_01_00_00.emevd.dcx', 'm20_00_00_00.emevd.dcx'))
    for filename, event, index, group, _, flag in GATES:
        check_flag(events(raw[filename])[event][index], group, flag)
    # Reconstruct the pre-shrine-fix gates for the existing-save regression matrix.
    before = patch_flag(common, 20006113, 14, 15, 1281, 71100505)
    before = patch_flag(before, 20006113, 15, 15, 71110206, 8400)
    shrine_cases = validate_shrine(before, common, castle)
    records = events(senpou)
    bell = records[12005210][2]
    if (bell['bank'], bell['code'], bell['args']) != (1000, 103, b'\x02\0\0\0'):
        raise ValueError('Early Monkeys bell is not independently available.')
    # Monk appearance must retain its stage gate, not the historical always-present patch.
    appearance = records[12005720][7]
    if (appearance['bank'], appearance['code'], appearance['args']) != (1003, 101, struct.pack('<BBBBi', 0, 1, 0, 0, 1480)):
        raise ValueError('Unexpected monk appearance gate.')
    for geni, blade, collected in itertools.product([0, 1], repeat=3):
        simulate_book(records[12005217], geni, blade, collected)
    return {'isshin_cases': validate_isshin(castle), 'monk_cases': validate_monk(common),
            'shrine_cases': shrine_cases, 'chapter_cases': 8}


def rebuild(raw):
    result = dict(raw)
    for filename, event, index, group, accepted, flag in GATES:
        record = events(result[filename])[event][index]
        previous = struct.unpack_from('<i', record['args'], 4)[0]
        if previous not in accepted:
            raise ValueError(f'Unknown gate for {filename} event {event}, instruction {index}: {previous}')
        check_flag(record, group, previous)
        if previous != flag:
            result[filename] = patch_flag(result[filename], event, index, group, previous, flag)
    validate(result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['validate', 'build', 'decode', 'pack'])
    parser.add_argument('--mod', type=Path, default=DEFAULT_MOD)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--input', type=Path, help='DCX input file for decode')
    parser.add_argument('--template', type=Path, help='Existing mod DCX header for pack')
    args = parser.parse_args()
    if args.command in ('decode', 'pack'):
        if not args.input or not args.output:
            parser.error('decode/pack requires --input and --output')
        if args.output.exists():
            parser.error('output already exists')
        if args.command == 'pack':
            if not args.template:
                parser.error('pack requires --template')
            data = wrap(args.input.read_bytes(), args.template.read_bytes())
        else:
            data = decompress(args.input.read_bytes())
        args.output.write_bytes(data)
        return
    templates = {name: (args.mod / 'event' / name).read_bytes() for name in sorted({gate[0] for gate in GATES})}
    raw = {name: decompress(data) for name, data in templates.items()}
    if args.command == 'build':
        if not args.output or args.output.exists():
            parser.error('build requires a new --output directory')
        raw = rebuild(raw)
        packed = {name: wrap(data, templates[name]) for name, data in raw.items()}
        args.output.mkdir(parents=True)
        for name, data in packed.items():
            (args.output / name).write_bytes(data)
    print(json.dumps(validate(raw), indent=2))


if __name__ == '__main__':
    main()
