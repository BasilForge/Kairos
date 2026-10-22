"""Prints the tracks of an MP4: handler type, codec fourcc, size, duration, frames and frame rate, then a summary line
(the picture, its frame rate and codec, and how many sound tracks). Reads plain and fragmented files (a recording is
written in fragments). No dependencies.

Usage: python -I tools/mp4info.py <file.mp4>
"""
import struct
import sys

CONTAINERS = {b"moov", b"trak", b"mdia", b"minf", b"stbl", b"moof", b"traf", b"mvex"}


def boxes(data, start, end):
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        header = 8
        if size == 1:
            size = struct.unpack(">Q", data[pos + 8:pos + 16])[0]
            header = 16
        elif size == 0:
            size = end - pos
        if size < header:
            return
        yield kind, pos + header, pos + size
        pos += size


def find(data, start, end, path):
    for kind, s, e in boxes(data, start, end):
        if kind == path[0]:
            if len(path) == 1:
                return s, e
            return find(data, s, e, path[1:])
    return None


def duration(data, box):
    v = data[box[0]]
    if v == 1:
        scale, dur = struct.unpack(">IQ", data[box[0] + 20:box[0] + 32])
    else:
        scale, dur = struct.unpack(">II", data[box[0] + 12:box[0] + 20])
    return scale, dur


def fragment_samples(data):
    """Samples and their total duration (in the track's units) per track id, from the moof boxes of a fragmented file."""
    counts, units = {}, {}
    for kind, s, e in boxes(data, 0, len(data)):
        if kind != b"moof":
            continue
        for tkind, ts, te in boxes(data, s, e):
            if tkind != b"traf":
                continue
            tfhd = find(data, ts, te, [b"tfhd"])
            if not tfhd:
                continue
            flags = struct.unpack(">I", data[tfhd[0]:tfhd[0] + 4])[0] & 0xFFFFFF
            track = struct.unpack(">I", data[tfhd[0] + 4:tfhd[0] + 8])[0]
            p = tfhd[0] + 8
            if flags & 0x1: p += 8
            if flags & 0x2: p += 4
            default_duration = struct.unpack(">I", data[p:p + 4])[0] if flags & 0x8 else 0
            for rkind, rs, re in boxes(data, ts, te):
                if rkind != b"trun":
                    continue
                rflags = struct.unpack(">I", data[rs:rs + 4])[0] & 0xFFFFFF
                n = struct.unpack(">I", data[rs + 4:rs + 8])[0]
                q = rs + 8
                if rflags & 0x1: q += 4
                if rflags & 0x4: q += 4
                per = sum(4 for bit in (0x100, 0x200, 0x400, 0x800) if rflags & bit)
                total = 0
                for i in range(n):
                    total += struct.unpack(">I", data[q + i * per:q + i * per + 4])[0] if rflags & 0x100 else default_duration
                counts[track] = counts.get(track, 0) + n
                units[track] = units.get(track, 0) + total
    return counts, units


def main(path):
    data = open(path, "rb").read()
    moov = find(data, 0, len(data), [b"moov"])
    if not moov:
        print("no moov box")
        return 1
    mvhd = find(data, *moov, [b"mvhd"])
    if mvhd:
        scale, dur = duration(data, mvhd)
        print(f"movie duration: {dur / scale if scale else 0:.2f} s")
    fragments = sum(1 for k, _, _ in boxes(data, 0, len(data)) if k == b"moof")
    if fragments:
        print(f"fragments: {fragments}")
    frag_counts, frag_units = fragment_samples(data) if fragments else ({}, {})
    picture, audio = None, 0
    for kind, s, e in boxes(data, *moov):
        if kind != b"trak":
            continue
        tkhd = find(data, s, e, [b"tkhd"])
        track_id = struct.unpack(">I", data[tkhd[0] + (20 if data[tkhd[0]] == 1 else 12):][:4])[0] if tkhd else 0
        mdhd = find(data, s, e, [b"mdia", b"mdhd"])
        hdlr = find(data, s, e, [b"mdia", b"hdlr"])
        stsd = find(data, s, e, [b"mdia", b"minf", b"stbl", b"stsd"])
        stts = find(data, s, e, [b"mdia", b"minf", b"stbl", b"stts"])
        handler = data[hdlr[0] + 8:hdlr[0] + 12].decode() if hdlr else "?"
        codec = data[stsd[0] + 12:stsd[0] + 16].decode(errors="replace") if stsd else "?"
        scale, dur = duration(data, mdhd) if mdhd else (0, 0)
        samples, units = 0, 0
        if stts:
            n = struct.unpack(">I", data[stts[0] + 4:stts[0] + 8])[0]
            for i in range(n):
                count, delta = struct.unpack(">II", data[stts[0] + 8 + i * 8:stts[0] + 16 + i * 8])
                samples += count
                units += count * delta
        if samples == 0 and track_id in frag_counts:
            samples, units = frag_counts[track_id], frag_units[track_id]
            dur = units
        seconds = dur / scale if scale else 0
        # The frame rate from the samples' own durations (the track's length can include a B-frame delay).
        timed = units / scale if scale and units else seconds
        extra = ""
        if handler == "vide" and stsd:
            w, h = struct.unpack(">HH", data[stsd[0] + 8 + 32:stsd[0] + 8 + 36])
            fps = samples / timed if timed else 0
            extra = f" {w}x{h} {samples} frames {fps:.2f} fps"
            picture = picture or (w, h, fps, codec)
        if handler == "soun":
            audio += 1
        print(f"track: {handler} {codec}{extra} {seconds:.2f} s")
    if picture:
        w, h, fps, codec = picture
        print(f"summary: {w}x{h} {fps:.2f} fps {codec}, {audio} sound track(s)")
    else:
        print(f"summary: no picture, {audio} sound track(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
