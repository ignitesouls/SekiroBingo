"""Canonical NPC validators and event simulators consolidated from staged source."""
import itertools
import struct
from event_codec import events, check_flag

def simulate_monk(records, flags):
    flags = dict(flags)
    groups = {}
    cursor = 1
    while cursor < len(records):
        record = records[cursor]
        bank, code, args = (record['bank'], record['code'], record['args'])
        if (bank, code) == (2003, 63):
            start, end = struct.unpack_from('<ii', args)
            for flag in range(start, end + 1):
                flags[flag] = args[8]
        elif (bank, code) == (2003, 62):
            flags[struct.unpack_from('<i', args)[0]] = args[4]
        elif (bank, code) == (3, 0):
            group = struct.unpack_from('<b', args)[0]
            actual = flags.get(struct.unpack_from('<i', args, 4)[0], 0) == args[1]
            groups[group] = groups.get(group, True) and actual
        elif (bank, code) == (1000, 1):
            group = struct.unpack_from('<b', args, 2)[0]
            if groups[group] == bool(args[1]):
                cursor += args[0]
        else:
            raise AssertionError((cursor, bank, code))
        cursor += 1
    return flags

def validate_monk(raw):
    records = events(raw)[20006121]
    if not len(records) == 15:
        raise ValueError('Unexpected event layout or behavior.')
    keys = [9303, 71110206, 9305, 8410, 71110055, 71110081, 50006320, 72000050]
    cases = 0
    for stage, dead in itertools.product([1480, 1481, 1482], [0, 1]):
        for values in itertools.product([0, 1], repeat=len(keys)):
            flags = dict(zip(keys, values))
            flags.update({stage: 1, 1499: dead, 8400: 1, 72000051: 1})
            result = simulate_monk(records, flags)
            wanted = 1482 if flags[9303] else 1481 if flags[71110055] else 1480
            if not result[wanted] == 1:
                raise ValueError('Unexpected event layout or behavior.')
            if not all((result.get(flag, 0) == 0 for flag in [1480, 1481, 1482] if flag != wanted)):
                raise ValueError('Unexpected event layout or behavior.')
            if not result[1499] == flags[9303]:
                raise ValueError('Unexpected event layout or behavior.')
            if not all((result[key] == value for key, value in flags.items() if not 1480 <= key <= 1499)):
                raise ValueError('Unexpected event layout or behavior.')
            cases += 1
    return cases

def validate_isshin(raw):
    records = events(raw)[11115762]
    for index, group, flag in [(0, 1, 1280), (1, 1, 9303), (2, -1, 71100501), (3, -1, 71100500)]:
        check_flag(records[index], group, flag)
    if not records[4]['bank'] == records[5]['bank'] == 0:
        raise ValueError('Unexpected event layout or behavior.')
    if not records[4]['code'] == records[5]['code'] == 0:
        raise ValueError('Unexpected event layout or behavior.')
    if not records[4]['args'] == b'\x01\x01\xff\x00':
        raise ValueError('Unexpected event layout or behavior.')
    if not records[5]['args'] == b'\x00\x01\x01\x00':
        raise ValueError('Unexpected event layout or behavior.')
    keys = [71100500, 71100501, 9303, 71110059, 8410, 71110081, 8301]
    cases = 0
    for stage in [1280, 1281, 1282, 1283]:
        for values in itertools.product([0, 1], repeat=len(keys)):
            flags = dict(zip(keys, values))
            flags[stage] = 1
            groups = {}
            for record in records[:6]:
                args = record['args']
                group = struct.unpack_from('<b', args)[0]
                if record['bank'] == 3:
                    flag = struct.unpack_from('<i', args, 4)[0]
                    actual = flags.get(flag, 0) == args[1]
                else:
                    source = struct.unpack_from('<b', args, 2)[0]
                    actual = groups[source] == bool(args[1])
                groups[group] = groups.get(group, False) or actual if group < 0 else groups.get(group, True) and actual
            expected = stage == 1280 and bool(flags[9303]) and bool(flags[71100500] or flags[71100501])
            if not groups[0] == expected:
                raise ValueError('Unexpected event layout or behavior.')
            cases += 1
    return cases

def simulate_shared(records, flags, own_world=True):
    """Execute the actual shared Isshin/Tengu event, including its native guards."""
    flags = dict(flags)
    groups = {}
    pc = 0

    def add(group, value):
        default = group >= 0
        old = groups.get(group, default)
        groups[group] = old and value if default else old or value
    while pc < len(records):
        r = records[pc]
        key, a = ((r['bank'], r['code']), r['args'])
        if key == (1003, 13):
            if not a == b'\x00\x01\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            if not own_world:
                break
        elif key == (1003, 3):
            if not a[1:4] == b'\x02\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            lo, hi = struct.unpack_from('<ii', a, 4)
            if any((flags.get(f, 0) for f in range(lo, hi + 1))):
                pc += a[0]
        elif key == (1003, 2):
            if not a[:4] == b'\x00\x01\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            if flags.get(struct.unpack_from('<i', a, 4)[0], 0):
                break
        elif key == (3, 0):
            group = struct.unpack_from('<b', a)[0]
            if not a[2:4] == b'\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            flag = struct.unpack_from('<i', a, 4)[0]
            add(group, flags.get(flag, 0) == a[1])
        elif key == (0, 0):
            group, desired, source = struct.unpack_from('<bBb', a)
            add(group, groups[source] == bool(desired))
        elif key == (1000, 1):
            source = struct.unpack_from('<b', a, 2)[0]
            if groups[source] == bool(a[1]):
                pc += a[0]
        elif key == (2003, 63):
            lo, hi = struct.unpack_from('<ii', a)
            for flag in range(lo, hi + 1):
                flags[flag] = a[8]
        elif key == (2003, 62):
            flags[struct.unpack_from('<i', a)[0]] = a[4]
        else:
            raise AssertionError((pc, key, a.hex()))
        pc += 1
    return flags

def unlock_tower(records, flags):
    """Execute the actual tower gate through its wait and stage writes."""
    flags = dict(flags)
    groups = {}
    for r in records[:8]:
        key, a = ((r['bank'], r['code']), r['args'])
        if key == (3, 0):
            group = struct.unpack_from('<b', a)[0]
            value = flags.get(struct.unpack_from('<i', a, 4)[0], 0) == a[1]
            old = groups.get(group, group >= 0)
            groups[group] = old and value if group >= 0 else old or value
        elif key == (0, 0):
            group, desired, source = struct.unpack_from('<bBb', a)
            value = groups[source] == bool(desired)
            if group == 0:
                if not value:
                    break
            else:
                groups[group] = groups.get(group, True) and value
        elif key == (2003, 63):
            lo, hi = struct.unpack_from('<ii', a)
            for flag in range(lo, hi + 1):
                flags[flag] = a[8]
        elif key == (2003, 62):
            flags[struct.unpack_from('<i', a)[0]] = a[4]
        else:
            raise AssertionError(key)
    return flags

def validate_shrine(before, after, castle):
    old = events(before)[20006113]
    new = events(after)[20006113]
    tower = events(castle)[11115762]
    shrine = events(castle)[11114700]
    if not (shrine[2]['bank'] == 1003 and shrine[2]['code'] == 101):
        raise ValueError('Unexpected event layout or behavior.')
    if not shrine[2]['args'] == struct.pack('<BBBBi', 0, 1, 0, 0, 1661):
        raise ValueError('Unexpected event layout or behavior.')
    if not (shrine[6]['bank'], shrine[6]['code']) == (2004, 5):
        raise ValueError('Unexpected event layout or behavior.')
    if not shrine[6]['args'] == struct.pack('<ii', 0, 1):
        raise ValueError('Unexpected event layout or behavior.')
    cases = 0
    for route, geni, topic, stage, native_quest, early, closed, dragon, dead, host in itertools.product([0, 1, 2, 3], [0, 1], [0, 1], [1660, 1661, 1674], [0, 1], range(4), range(4), [0, 1], [0, 1], [False, True]):
        flags = {1280: 1, stage: 1, 1299: dead, 9303: geni, 71100500: route & 1, 71100501: route >> 1 & 1, 71100505: native_quest, 71110206: topic, 8400: early & 1, 8410: early >> 1 & 1, 8301: closed & 1, 71100533: closed >> 1 & 1, 8302: dragon, 9305: 1, 71110059: 0, 71110081: 1, 50006320: 1, 50006240: 1, 72000050: 1, 72000051: 1}
        flags = unlock_tower(tower, flags)
        unlocked = bool(geni and route)
        if not bool(flags.get(1281, 0)) == unlocked:
            raise ValueError('Unexpected event layout or behavior.')
        result = simulate_shared(new, flags, host)
        previous = simulate_shared(old, flags, host)
        wanted = stage
        if host and (not dead):
            if stage == 1660 and unlocked and (not dragon) and topic:
                wanted = 1661
            if wanted in (1660, 1661) and closed:
                wanted = 1674
        if not result.get(wanted, 0) == 1:
            raise ValueError('Unexpected event layout or behavior.')
        if not all((result.get(f, 0) == 0 for f in range(1660, 1680) if f != wanted)):
            raise ValueError('Unexpected event layout or behavior.')
        if not {f: v for f, v in result.items() if not 1660 <= f <= 1679} == {f: v for f, v in previous.items() if not 1660 <= f <= 1679}:
            raise ValueError('Unrelated quest/story/reward state changed.')
        if not simulate_shared(new, result, host) == result:
            raise ValueError('Unexpected event layout or behavior.')
        cases += 1
    for response in [71100500, 71100501]:
        save = {1660: 1, 1281: 1, response: 1, 9303: 1, 8400: 1, 8410: 1, 71110206: 1, 71100505: 0}
        if not simulate_shared(old, save).get(1661, 0) == 0:
            raise ValueError('Unexpected event layout or behavior.')
        if not simulate_shared(new, save)[1661] == 1:
            raise ValueError('Unexpected event layout or behavior.')
        save[71110206] = 0
        if not simulate_shared(new, save).get(1661, 0) == 0:
            raise ValueError('Unexpected event layout or behavior.')
    return cases

def simulate_book(records, geni, blade, collected):
    """Run the emitted item event until its wait or completion.

    Native treasure pickup suppression uses the unchanged acquisition flag.
    This simulator models that separately from object and treasure enablement.
    """
    flags = {9303: geni, 8400: blade, 50006320: collected}
    object_on = treasure_on = None
    for record in records:
        bank, code, data = (record['bank'], record['code'], record['args'])
        if (bank, code) == (2005, 3):
            if not struct.unpack_from('<i', data)[0] == 2001202:
                raise ValueError('Unexpected event layout or behavior.')
            object_on = bool(data[4])
        elif (bank, code) == (2005, 4):
            if not struct.unpack_from('<i', data)[0] == 2001202:
                raise ValueError('Unexpected event layout or behavior.')
            treasure_on = bool(data[4])
        elif (bank, code) == (3, 0):
            if not data[:4] == b'\x00\x01\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            if flags.get(struct.unpack_from('<i', data, 4)[0], 0) != data[1]:
                break
        elif (bank, code) == (1000, 4):
            if not data == b'\x00\x00\x00\x00':
                raise ValueError('Unexpected event layout or behavior.')
            break
        else:
            raise AssertionError((bank, code))
    if not (object_on == bool(geni) and treasure_on == bool(geni)):
        raise ValueError('Unexpected event layout or behavior.')
    if not (object_on and treasure_on and (not flags[50006320])) == (bool(geni) and (not collected)):
        raise ValueError('Unexpected event layout or behavior.')
    if not flags == {9303: geni, 8400: blade, 50006320: collected}:
        raise ValueError('Unexpected event layout or behavior.')
