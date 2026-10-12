"""Sekiro event codec adapted from the staged current patch source."""
import struct
import ctypes
import os
import zlib


def decompress(data):
    if data[:4] != b'DCX\0':
        return data
    if len(data) < 76:
        raise ValueError('Truncated DCX header.')
    size, compressed_size = struct.unpack_from('>II', data, 28)
    offset = struct.unpack_from('>I', data, 20)[0]
    if offset < 76 or offset + compressed_size > len(data):
        raise ValueError('Invalid DCX payload bounds.')
    payload = data[offset:offset + compressed_size]
    if data[40:44] == b'DFLT':
        raw = zlib.decompress(payload)
        if len(raw) != size:
            raise ValueError('Incorrect decompressed size.')
        return raw
    if data[40:44] != b'KRAK':
        raise ValueError('Unsupported DCX compression.')
    blocks, cursor, remaining = [], 0, size
    while remaining and payload[cursor:cursor + 2] == b'\xcc\x06':
        cursor += 2
        length = min(0x40000, remaining)
        block = payload[cursor:cursor + length]
        if len(block) != length:
            raise ValueError('Truncated raw Kraken block.')
        blocks.append(block)
        cursor += length
        remaining -= length
    if not remaining and cursor == len(payload):
        return b''.join(blocks)
    # Original game assets require a developer-supplied Oodle library.
    path = os.environ.get('SEKIRO_OODLE_LIBRARY')
    if not path:
        raise ValueError('Compressed Kraken input requires SEKIRO_OODLE_LIBRARY. Shipped mod assets do not.')
    library = ctypes.CDLL(path)
    native = library.OodleLZ_Decompress
    native.argtypes = [ctypes.c_void_p, ctypes.c_ssize_t, ctypes.c_void_p, ctypes.c_ssize_t,
                       ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_ssize_t,
                       ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ssize_t, ctypes.c_int]
    native.restype = ctypes.c_ssize_t
    source = ctypes.create_string_buffer(payload, len(payload) + 64)
    target = ctypes.create_string_buffer(size + 64)
    if native(source, len(payload), target, size, 1, 0, 0, None, 0, None, None, None, 0, 3) != size:
        raise ValueError('Oodle decompression failed.')
    return target.raw[:size]

def wrap(raw, template):
    if not (template[:4] == b'DCX\x00' and template[40:44] == b'KRAK'):
        raise ValueError('Unexpected event layout or behavior.')
    offset = struct.unpack_from('>I', template, 20)[0]
    if not offset == 76:
        raise ValueError('Unexpected event layout or behavior.')
    payload = b''.join((b'\xcc\x06' + raw[i:i + 262144] for i in range(0, len(raw), 262144)))
    header = bytearray(template[:offset])
    struct.pack_into('>II', header, 28, len(raw), len(payload))
    packed = bytes(header) + payload
    packed += bytes(-len(packed) % 16)
    if not decompress(packed) == raw:
        raise ValueError('Unexpected event layout or behavior.')
    return packed

def events(data):
    if not data[:4] == b'EVD\x00':
        raise ValueError('Unexpected event layout or behavior.')
    header = struct.unpack_from('<16Q', data, 16)
    result = {}
    for i in range(header[0]):
        event_id, count, offset, param_count, param_offset, rest, zero = struct.unpack_from('<5Q2I', data, header[1] + i * 48)
        records = []
        for index in range(count):
            record_offset = header[3] + offset + index * 32
            bank, code, size, arg_offset, layer = struct.unpack_from('<IIQqq', data, record_offset)
            absolute = header[13] + arg_offset
            if not 0 <= absolute <= absolute + size <= len(data):
                raise ValueError('Unexpected event layout or behavior.')
            records.append({'bank': bank, 'code': code, 'args': data[absolute:absolute + size], 'offset': absolute, 'layer': layer})
        result[event_id] = records
    return result

def check_flag(record, group, flag):
    if not (record['bank'], record['code']) == (3, 0):
        raise ValueError('Unexpected event layout or behavior.')
    if not record['args'][:4] == struct.pack('<bBBx', group, 1, 0):
        raise ValueError('Unexpected event layout or behavior.')
    if not struct.unpack_from('<i', record['args'], 4)[0] == flag:
        raise ValueError('Unexpected event layout or behavior.')

def patch_flag(raw, event_id, index, group, old, new):
    record = events(raw)[event_id][index]
    check_flag(record, group, old)
    output = bytearray(raw)
    struct.pack_into('<i', output, record['offset'] + 4, new)
    check_flag(events(output)[event_id][index], group, new)
    allowed = set(range(record['offset'] + 4, record['offset'] + 8))
    changed = {i for i, (a, b) in enumerate(zip(raw, output)) if a != b}
    if not (changed and changed <= allowed and (len(output) == len(raw))):
        raise ValueError('Unexpected event layout or behavior.')
    return bytes(output)
